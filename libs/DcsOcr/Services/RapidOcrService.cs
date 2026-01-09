using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using DCS.OCR.Library.Models;
using System.IO;

namespace DCS.OCR.Library.Services
{
    public class RapidOcrService : IDisposable
    {
        private InferenceSession? _recSession;
        private List<string>? _alphabet;
        private string? _inputName;
        private readonly RowSegmentationService _rowSegmentationService = new RowSegmentationService();
        public Mat? LastPreprocessedFrame { get; set; }
        public Mat? LastNormalizedFrame { get; set; }
        public List<OpenCvSharp.Rect>? LastInvertedRegions { get; set; }
        public bool IsLoaded => _recSession != null && _alphabet != null;

        public void LoadModel(string recModelPath)
        {
            _recSession = new InferenceSession(recModelPath);
            _inputName = _recSession.InputMetadata.Keys.FirstOrDefault();
            
            // Extract alphabet from metadata
            if (_recSession.ModelMetadata.CustomMetadataMap.TryGetValue("character", out string? charListStr))
            {
                var chars = charListStr.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
                
                // RapidOCR logic:
                // 0: blank
                // 1..N: characters
                // N+1: space
                _alphabet = new List<string> { "blank" };
                _alphabet.AddRange(chars);
                _alphabet.Add(" ");
            }
            else
            {
                throw new Exception("Model metadata does not contain 'character' key");
            }
        }

        public OcrCell[][] ProcessFrame(Mat frame, Profile profile)
        {
            if (_recSession == null || _alphabet == null)
                throw new InvalidOperationException("Model not loaded");

            LastPreprocessedFrame?.Dispose();
            LastPreprocessedFrame = PreprocessImage(frame, 
                profile.Invert ?? false,
                profile.UseGlobalThreshold ?? true,
                profile.BinaryThreshold ?? 84,
                profile.GreenOnly ?? true,
                profile.Sharpness ?? 0,
                profile.Contrast ?? 1.0f,
                profile.Dilation ?? 0);

            int rows = profile.Rows;
            int cols = profile.Cols;
            float cellH = (float)frame.Height / rows;

            var grid = new OcrCell[rows][];

            List<RowSegmentationService.RowBand>? rowBands = null;
            LastNormalizedFrame?.Dispose();
            LastNormalizedFrame = null;
            LastInvertedRegions = null;
            
            // Always detect and normalize inverted regions
            // This ensures the OCR model sees normal text (white on black) even for highlighted/inverted regions
            if (LastPreprocessedFrame != null)
            {
                LastNormalizedFrame = RowSegmentationService.NormalizeInvertedRegions(LastPreprocessedFrame, out var invertedRegions);
                LastInvertedRegions = invertedRegions;
            }
            
            // Use the normalized frame for OCR so inverted text appears normal
            var frameForOcr = LastNormalizedFrame ?? LastPreprocessedFrame;
            
            if (profile.UseDynamicRowSegmentation == true && frameForOcr != null)
            {
                // Use anchor-based segmentation if RowCenters are provided
                if (profile.RowCenters != null && profile.RowCenters.Count >= rows)
                {
                    int yOffset = profile.DynamicRowYOffset ?? 10;
                    int minHeight = profile.DynamicRowHeightMin ?? 32;
                    int maxHeight = profile.DynamicRowHeightMax ?? 60;
                    int gap = profile.DynamicRowGap ?? 2;
                    rowBands = _rowSegmentationService.SegmentRowsAnchored(frameForOcr, profile.RowCenters, yOffset, minHeight, maxHeight, gap);
                }
                else
                {
                    rowBands = _rowSegmentationService.SegmentRows(frameForOcr, rows);
                }
            }

            // Calculate static row bands if dynamic segmentation is not used
            int[][]? staticRowBands = null;
            if (rowBands == null)
            {
                // Check for explicit RowBands in profile
                if (profile.RowBands != null && profile.RowBands.Count >= rows)
                {
                    staticRowBands = new int[rows][];
                    for (int r = 0; r < rows; r++)
                    {
                        var band = profile.RowBands[r];
                        staticRowBands[r] = band.Length >= 2 ? new[] { band[0], band[1] } : new[] { 0, (int)cellH };
                    }
                }
            }

            for (int r = 0; r < rows; r++)
            {
                grid[r] = new OcrCell[cols];

                float rowXStart = 0;
                float rowColW = (float)frame.Width / cols;

                int y1;
                int y2;
                if (rowBands != null)
                {
                    y1 = rowBands[r].Y1;
                    y2 = rowBands[r].Y2;
                }
                else if (staticRowBands != null)
                {
                    y1 = staticRowBands[r][0];
                    y2 = staticRowBands[r][1];
                }
                else
                {
                    // Fallback: uniform row heights
                    y1 = (int)(r * cellH);
                    y2 = (int)((r + 1) * cellH);
                }

                y1 = Math.Clamp(y1, 0, Math.Max(0, frame.Height - 1));
                y2 = Math.Clamp(y2, y1 + 1, frame.Height);

                int rowH = Math.Min(y2 - y1, frame.Height - y1);
                
                if (rowH <= 0)
                {
                    // Initialize empty cells for invalid rows
                    for (int c = 0; c < cols; c++)
                    {
                        grid[r][c] = new OcrCell 
                        { 
                            Char = ' ', 
                            Confidence = 0,
                            X = (int)(rowXStart + c * rowColW),
                            Y = y1,
                            W = (int)rowColW,
                            H = rowH
                        };
                    }
                    continue;
                }

                int y_clamp = Math.Clamp(y1, 0, frame.Height - rowH);
                // Use normalized frame for OCR so inverted text appears normal
                using var rowROI = new Mat(frameForOcr, new Rect(0, y_clamp, frame.Width, rowH));

                // Detect character boundaries using vertical projection for adaptive cell widths
                var charBoundaries = DetectCharacterBoundaries(rowROI);
                
                // Create adaptive cell widths based on detected characters
                // Each detected character gets its own cell, remaining cells are distributed in gaps
                var adaptiveCells = CreateAdaptiveCells(charBoundaries, cols, frame.Width, y1, rowH);
                
                // Initialize grid with adaptive cells
                for (int c = 0; c < cols; c++)
                {
                    grid[r][c] = adaptiveCells[c];
                }

                var align = GetDynamicRowAlignment(rowROI, rowColW, cols, rowXStart, y_clamp);
                if (!align.HasText) continue;

                float alignmentPadding = profile.AlignmentPadding ?? 0;
                
                // Use normalized frame for character recognition - inverted regions are already un-inverted
                using var rowNormalizedROI = new Mat(frameForOcr, new Rect(0, y_clamp, frame.Width, rowH));
                
                foreach (var region in align.Regions)
                {
                    int xStart = Math.Clamp(region.Start, 0, frame.Width - 1);
                    int xEnd = Math.Clamp(region.End, 0, frame.Width);
                    int regionW = xEnd - xStart;
                    if (regionW <= 0) continue;

                    // Extract word from normalized frame (inverted text already corrected)
                    using var wordROI = new Mat(rowNormalizedROI, new Rect(xStart, 0, regionW, rowH));

                    List<OcrCell> cells = RecognizeLine(wordROI, profile.Charset, profile.MinCharDistance ?? 0);
                    if (cells.Count == 0) continue;

                    // Translate character coordinates to absolute frame coordinates
                    // and mark cells as inverted if they fall within an inverted region
                    foreach (var cell in cells)
                    {
                        if (cell.X.HasValue) cell.X += xStart;
                        if (cell.Y.HasValue) cell.Y += y_clamp;
                        
                        // Check if this cell is within an inverted region
                        if (LastInvertedRegions != null && cell.X.HasValue && cell.Y.HasValue)
                        {
                            int cellCenterX = cell.X.Value + (cell.W ?? 0) / 2;
                            int cellCenterY = cell.Y.Value + (cell.H ?? 0) / 2;
                            foreach (var invRegion in LastInvertedRegions)
                            {
                                if (cellCenterX >= invRegion.X && cellCenterX < invRegion.X + invRegion.Width &&
                                    cellCenterY >= invRegion.Y && cellCenterY < invRegion.Y + invRegion.Height)
                                {
                                    cell.IsInverted = true;
                                    break;
                                }
                            }
                        }
                    }

                    // Map detected characters to the adaptive grid
                    // Find the best matching cell for each detected character based on position overlap
                    foreach (var cell in cells)
                    {
                        if (!cell.X.HasValue || !cell.W.HasValue) continue;
                        
                        int cellCenterX = cell.X.Value + cell.W.Value / 2;
                        
                        // Find the grid cell that best contains this character
                        int bestCol = -1;
                        int bestOverlap = 0;
                        
                        for (int c = 0; c < cols; c++)
                        {
                            var gridCell = grid[r][c];
                            if (!gridCell.X.HasValue || !gridCell.W.HasValue) continue;
                            
                            // Check if character center falls within this grid cell
                            int gridLeft = gridCell.X.Value;
                            int gridRight = gridLeft + gridCell.W.Value;
                            
                            if (cellCenterX >= gridLeft && cellCenterX < gridRight)
                            {
                                // Character center is in this cell - use it
                                bestCol = c;
                                break;
                            }
                            
                            // Calculate overlap for fallback
                            int charLeft = cell.X.Value;
                            int charRight = charLeft + cell.W.Value;
                            int overlapLeft = Math.Max(gridLeft, charLeft);
                            int overlapRight = Math.Min(gridRight, charRight);
                            int overlap = Math.Max(0, overlapRight - overlapLeft);
                            
                            if (overlap > bestOverlap)
                            {
                                bestOverlap = overlap;
                                bestCol = c;
                            }
                        }
                        
                        if (bestCol >= 0 && bestCol < cols)
                        {
                            // Keep the adaptive cell boundaries but update the character and confidence
                            var existingCell = grid[r][bestCol];
                            cell.X = existingCell.X;
                            cell.Y = y1;
                            cell.W = existingCell.W;
                            cell.H = rowH;
                            grid[r][bestCol] = cell;
                        }
                    }
                }
            }

            return grid;
        }

        public Mat PreprocessImage(Mat img, bool invert = false, bool global = true, int thresh = 82, bool greenOnly = true, float sharpness = 0, float contrast = 1.0f, int dilation = 0)
        {
            Mat input = new Mat();
            if (greenOnly && img.Channels() == 3)
            {
                Cv2.ExtractChannel(img, input, 1);
            }
            else
            {
                if (img.Channels() > 1)
                    Cv2.CvtColor(img, input, ColorConversionCodes.BGR2GRAY);
                else
                    img.CopyTo(input);
            }

            // Apply Contrast adjustment
            if (Math.Abs(contrast - 1.0f) > 0.01f)
            {
                input.ConvertTo(input, -1, contrast, 0);
            }

            // Apply Sharpness adjustment
            if (sharpness > 0)
            {
                using var blurred = new Mat();
                Cv2.GaussianBlur(input, blurred, new OpenCvSharp.Size(0, 0), 3);
                Cv2.AddWeighted(input, 1.0 + sharpness, blurred, -sharpness, 0, input);
            }

            Cv2.GaussianBlur(input, input, new OpenCvSharp.Size(3, 3), 0);

            Mat binary = new Mat();
            var thresholdType = invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            if (global)
            {
                Cv2.Threshold(input, binary, thresh, 255, thresholdType);
            }
            else
            {
                Cv2.AdaptiveThreshold(input, binary, 255, AdaptiveThresholdTypes.GaussianC, thresholdType, 15, 2);
            }

            // Apply Dilation if requested (thicken text)
            if (dilation > 0)
            {
                var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1 + dilation * 2, 1 + dilation * 2));
                Cv2.Dilate(binary, binary, kernel);
            }
            
            input.Dispose();
            return binary;
        }

        public struct AlignmentResult
        {
            public float BestXStart;
            public float BestYStart;
            public bool HasText;
            public bool IsHighlighted;
            public List<TextRegion> Regions;
        }

        public struct TextRegion
        {
            public int Start;      // Pixel X start of text region
            public int End;        // Pixel X end of text region
            public int ColStart;   // First column index in this segment
            public int ColEnd;     // Last column index in this segment (inclusive)
            public float XOffset;  // Per-segment X offset for alignment
        }

        /// <summary>
        /// Creates adaptive cells based on detected character boundaries.
        /// Ensures exactly 'cols' cells are created, with detected characters getting their actual widths
        /// and empty spaces getting proportionally distributed widths.
        /// </summary>
        public OcrCell[] CreateAdaptiveCells(List<(int X, int W)> charBoundaries, int cols, int rowWidth, int y, int h)
        {
            var cells = new OcrCell[cols];
            
            if (charBoundaries.Count == 0)
            {
                // No characters detected - create uniform cells
                float cellW = (float)rowWidth / cols;
                for (int c = 0; c < cols; c++)
                {
                    cells[c] = new OcrCell
                    {
                        Char = ' ',
                        Confidence = 0,
                        X = (int)(c * cellW),
                        Y = y,
                        W = (int)cellW,
                        H = h
                    };
                }
                return cells;
            }
            
            // If we have more characters than columns, merge some characters
            // If we have fewer characters than columns, distribute empty cells in gaps
            
            int numChars = charBoundaries.Count;
            
            if (numChars >= cols)
            {
                // More characters than columns - assign one character per cell
                // Use the first 'cols' characters (or distribute evenly)
                float step = (float)numChars / cols;
                for (int c = 0; c < cols; c++)
                {
                    int charIdx = Math.Min((int)(c * step), numChars - 1);
                    var boundary = charBoundaries[charIdx];
                    cells[c] = new OcrCell
                    {
                        Char = ' ',
                        Confidence = 0,
                        X = boundary.X,
                        Y = y,
                        W = boundary.W,
                        H = h
                    };
                }
            }
            else
            {
                // Fewer characters than columns - need to distribute empty cells
                // Strategy: Place character cells at their detected positions,
                // then fill gaps with empty cells
                
                // First, identify gaps between characters and at the edges
                var segments = new List<(int Start, int End, bool IsChar, int CharIdx)>();
                
                // Gap before first character
                if (charBoundaries[0].X > 0)
                {
                    segments.Add((0, charBoundaries[0].X, false, -1));
                }
                
                // Characters and gaps between them
                for (int i = 0; i < numChars; i++)
                {
                    var boundary = charBoundaries[i];
                    segments.Add((boundary.X, boundary.X + boundary.W, true, i));
                    
                    // Gap after this character (if not the last one)
                    if (i < numChars - 1)
                    {
                        int gapStart = boundary.X + boundary.W;
                        int gapEnd = charBoundaries[i + 1].X;
                        if (gapEnd > gapStart)
                        {
                            segments.Add((gapStart, gapEnd, false, -1));
                        }
                    }
                }
                
                // Gap after last character
                var lastChar = charBoundaries[numChars - 1];
                if (lastChar.X + lastChar.W < rowWidth)
                {
                    segments.Add((lastChar.X + lastChar.W, rowWidth, false, -1));
                }
                
                // Calculate how many empty cells we need
                int emptyCellsNeeded = cols - numChars;
                
                // Calculate total gap width
                int totalGapWidth = 0;
                foreach (var seg in segments)
                {
                    if (!seg.IsChar)
                    {
                        totalGapWidth += seg.End - seg.Start;
                    }
                }
                
                // Distribute empty cells proportionally across gaps
                int cellIdx = 0;
                int emptyCellsAssigned = 0;
                
                foreach (var seg in segments)
                {
                    if (cellIdx >= cols) break;
                    
                    if (seg.IsChar)
                    {
                        // This is a character - assign one cell
                        var boundary = charBoundaries[seg.CharIdx];
                        cells[cellIdx] = new OcrCell
                        {
                            Char = ' ',
                            Confidence = 0,
                            X = boundary.X,
                            Y = y,
                            W = boundary.W,
                            H = h
                        };
                        cellIdx++;
                    }
                    else
                    {
                        // This is a gap - distribute empty cells proportionally
                        int gapWidth = seg.End - seg.Start;
                        int cellsForThisGap;
                        
                        if (totalGapWidth > 0)
                        {
                            // Proportional distribution
                            cellsForThisGap = (int)Math.Round((float)gapWidth / totalGapWidth * emptyCellsNeeded);
                            // Ensure we don't exceed remaining empty cells
                            cellsForThisGap = Math.Min(cellsForThisGap, emptyCellsNeeded - emptyCellsAssigned);
                        }
                        else
                        {
                            cellsForThisGap = 0;
                        }
                        
                        if (cellsForThisGap > 0)
                        {
                            float cellW = (float)gapWidth / cellsForThisGap;
                            for (int i = 0; i < cellsForThisGap && cellIdx < cols; i++)
                            {
                                cells[cellIdx] = new OcrCell
                                {
                                    Char = ' ',
                                    Confidence = 0,
                                    X = seg.Start + (int)(i * cellW),
                                    Y = y,
                                    W = (int)cellW,
                                    H = h
                                };
                                cellIdx++;
                                emptyCellsAssigned++;
                            }
                        }
                    }
                }
                
                // Fill any remaining cells (shouldn't happen, but safety net)
                float defaultW = (float)rowWidth / cols;
                while (cellIdx < cols)
                {
                    cells[cellIdx] = new OcrCell
                    {
                        Char = ' ',
                        Confidence = 0,
                        X = (int)(cellIdx * defaultW),
                        Y = y,
                        W = (int)defaultW,
                        H = h
                    };
                    cellIdx++;
                }
            }
            
            return cells;
        }

        /// <summary>
        /// Detects character boundaries within a row using vertical projection.
        /// Returns a list of (X, Width) tuples representing detected character cells.
        /// Each boundary is expanded by 2 pixels on each side to prevent character edges from being clipped.
        /// Filters out floating pixels that are under 2x2 pixels or have height/width of just 1 pixel.
        /// </summary>
        public List<(int X, int W)> DetectCharacterBoundaries(Mat rowImg, int minCharWidth = 2, int minCharHeight = 1)
        {
            var boundaries = new List<(int X, int W)>();
            int width = rowImg.Width;
            int height = rowImg.Height;
            
            if (width <= 0 || height <= 0) return boundaries;
            
            // Calculate vertical projection (sum of white pixels in each column)
            float[] proj = new float[width];
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (rowImg.At<byte>(y, x) > 127)
                    {
                        proj[x]++;
                    }
                }
            }
            
            // Find character boundaries using projection
            float threshold = height * 0.05f; // 5% of row height
            bool inChar = false;
            int charStart = 0;
            const int padding = 5; // Expand each side by 5 pixels to prevent clipping
            
            for (int x = 0; x < width; x++)
            {
                if (proj[x] > threshold)
                {
                    if (!inChar)
                    {
                        inChar = true;
                        charStart = x;
                    }
                }
                else
                {
                    if (inChar)
                    {
                        inChar = false;
                        int charWidth = x - charStart;
                        if (charWidth >= minCharWidth)
                        {
                            // Calculate actual height of the character region (number of rows with pixels)
                            int charHeight = CalculateRegionHeight(rowImg, charStart, x, height);
                            
                            // Filter out floating pixels: must be at least minCharHeight tall and minCharWidth wide
                            // Also filter out regions under 2x2 pixels total
                            if (charHeight >= minCharHeight && charWidth * charHeight >= 4)
                            {
                                // Expand boundary by padding on each side, clamped to image bounds
                                int expandedX = Math.Max(0, charStart - padding);
                                int expandedEnd = Math.Min(width, x + padding);
                                int expandedWidth = expandedEnd - expandedX;
                                boundaries.Add((expandedX, expandedWidth));
                            }
                        }
                    }
                }
            }
            
            // Handle character at end of row
            if (inChar)
            {
                int charWidth = width - charStart;
                if (charWidth >= minCharWidth)
                {
                    // Calculate actual height of the character region
                    int charHeight = CalculateRegionHeight(rowImg, charStart, width, height);
                    
                    // Filter out floating pixels
                    if (charHeight >= minCharHeight && charWidth * charHeight >= 4)
                    {
                        // Expand boundary by padding on each side, clamped to image bounds
                        int expandedX = Math.Max(0, charStart - padding);
                        int expandedEnd = width; // Already at end, no need to expand right
                        int expandedWidth = expandedEnd - expandedX;
                        boundaries.Add((expandedX, expandedWidth));
                    }
                }
            }
            
            return boundaries;
        }
        
        /// <summary>
        /// Calculates the actual height of a character region by counting rows that contain white pixels.
        /// </summary>
        private int CalculateRegionHeight(Mat rowImg, int xStart, int xEnd, int imgHeight)
        {
            int minY = imgHeight;
            int maxY = -1;
            
            for (int y = 0; y < imgHeight; y++)
            {
                for (int x = xStart; x < xEnd; x++)
                {
                    if (rowImg.At<byte>(y, x) > 127)
                    {
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        break; // Found a pixel in this row, move to next row
                    }
                }
            }
            
            if (maxY < 0) return 0; // No pixels found
            return maxY - minY + 1;
        }

        
        public AlignmentResult GetDynamicRowAlignment(Mat rowImg, float cellWidth, int cols, float initialXStart = 0, float initialYStart = 0)
        {
            int width = rowImg.Width;
            int height = rowImg.Height;
            
            // The input rowImg should already be normalized (inverted regions un-inverted)
            // so we just detect white pixels (text) directly without any inversion handling
            float[] proj = new float[width];
            float[] vProj = new float[height];

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    if (rowImg.At<byte>(y, x) > 127)
                    {
                        proj[x]++;
                        vProj[y]++;
                    }
                }
            }

            // Vertical Alignment refinement
            float bestYStart = initialYStart;
            int firstY = -1, lastY = -1;
            float vThreshold = width * 0.01f; // 1% of row width
            for (int y = 0; y < height; y++)
            {
                if (vProj[y] > vThreshold)
                {
                    if (firstY == -1) firstY = y;
                    lastY = y;
                }
            }

            if (firstY != -1)
            {
                float textHeight = lastY - firstY;
                bestYStart = initialYStart + (firstY + lastY) / 2f - (height / 2f);
            }

            // Detect text regions using horizontal projection
            var rawRegions = new List<(int Start, int End)>();
            bool inText = false;
            int start = 0;
            float threshold = height * 0.1f;

            for (int x = 0; x < width; x++)
            {
                if (proj[x] > threshold)
                {
                    if (!inText)
                    {
                        inText = true;
                        start = x;
                    }
                }
                else
                {
                    if (inText)
                    {
                        inText = false;
                        // Minimum width for a region to be considered text
                        if (x - start > 2)
                        {
                            rawRegions.Add((start, x));
                        }
                    }
                }
            }
            if (inText) rawRegions.Add((start, width));

            if (rawRegions.Count == 0) return new AlignmentResult { HasText = false, IsHighlighted = false, BestXStart = initialXStart, BestYStart = initialYStart, Regions = new List<TextRegion>() };

            // Determine which columns belong to each region based on cell center positions
            // A column belongs to a region if its center falls within or near the region bounds
            var regions = new List<TextRegion>();
            float searchRange = cellWidth * 1.5f;
            
            foreach (var rawRegion in rawRegions)
            {
                // Find columns that overlap with this text region
                int colStart = -1;
                int colEnd = -1;
                
                for (int c = 0; c < cols; c++)
                {
                    float cellLeft = initialXStart + c * cellWidth;
                    float cellRight = cellLeft + cellWidth;
                    float cellMid = (cellLeft + cellRight) / 2f;
                    
                    // Check if cell center is within or near the text region
                    // Use a margin of half cell width to catch edge cases
                    float margin = cellWidth * 0.5f;
                    if (cellMid >= rawRegion.Start - margin && cellMid <= rawRegion.End + margin)
                    {
                        if (colStart == -1) colStart = c;
                        colEnd = c;
                    }
                }
                
                // Skip regions that don't map to any columns
                if (colStart == -1) continue;
                
                // Calculate best X offset for this segment only
                float bestSegmentOffset = 0;
                float maxScore = float.MinValue;
                
                for (float dx = -searchRange; dx <= searchRange; dx += 1.0f)
                {
                    float score = 0;
                    
                    // Only score columns that belong to this segment
                    for (int c = colStart; c <= colEnd; c++)
                    {
                        float cellLeft = initialXStart + c * cellWidth + dx;
                        float cellRight = cellLeft + cellWidth;
                        float cellMid = (cellLeft + cellRight) / 2f;

                        // Middle 50% of the cell should have text
                        int midRegionStart = (int)(cellMid - cellWidth / 4f);
                        int midRegionEnd = (int)(cellMid + cellWidth / 4f);
                        
                        for (int x = midRegionStart; x <= midRegionEnd; x++)
                        {
                            if (x >= 0 && x < width) 
                            {
                                // Bonus for having pixels where a character should be
                                score += proj[x];
                            }
                            else 
                            {
                                score -= (height * 0.1f);
                            }
                        }
                        
                        // Penalty for having pixels in the "gutters" (between characters)
                        int gutterStart = (int)(cellRight - cellWidth / 8f);
                        int gutterEnd = (int)(cellRight + cellWidth / 8f);
                        for (int x = gutterStart; x <= gutterEnd; x++)
                        {
                            if (x >= 0 && x < width)
                            {
                                score -= proj[x] * 1.5f; // Stronger penalty for pixels in gutters
                            }
                        }
                    }

                    if (score > maxScore)
                    {
                        maxScore = score;
                        bestSegmentOffset = dx;
                    }
                }
                
                regions.Add(new TextRegion 
                { 
                    Start = rawRegion.Start, 
                    End = rawRegion.End,
                    ColStart = colStart,
                    ColEnd = colEnd,
                    XOffset = bestSegmentOffset
                });
            }
            
            // For columns not covered by any region, they keep the default offset (0)
            // Calculate a global best offset as fallback (for backward compatibility)
            float bestXStart = initialXStart;
            if (regions.Count > 0)
            {
                // Use the offset of the first region as the global offset
                bestXStart = initialXStart + regions[0].XOffset;
            }

            return new AlignmentResult 
            { 
                BestXStart = bestXStart, 
                BestYStart = bestYStart,
                HasText = true, 
                IsHighlighted = false, // No longer used - inversion handled by NormalizeInvertedRegions
                Regions = regions
            };
        }

        /// <summary>
        /// Extract a character for dataset generation with tight cropping.
        /// No padding is added - the character is cropped exactly to its bounding box.
        /// </summary>
        public Mat ExtractCharacterTight(Mat binary, Rect rect, int targetSize = 32)
        {
            // Clamp rect to image bounds
            int x1 = Math.Max(0, rect.X);
            int y1 = Math.Max(0, rect.Y);
            int x2 = Math.Min(binary.Width, rect.X + rect.Width);
            int y2 = Math.Min(binary.Height, rect.Y + rect.Height);

            int roiW = x2 - x1;
            int roiH = y2 - y1;
            if (roiW <= 0 || roiH <= 0)
            {
                return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);
            }

            using var roi = new Mat(binary, new Rect(x1, y1, roiW, roiH));
            using var working = roi.Clone();

            // Find foreground bounding box - tight crop to actual character pixels
            Cv2.FindContours(working, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            if (contours.Length == 0)
            {
                return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);
            }

            int minCx = int.MaxValue, minCy = int.MaxValue, maxCx = int.MinValue, maxCy = int.MinValue;
            bool foundAny = false;
            foreach (var contour in contours)
            {
                var br = Cv2.BoundingRect(contour);
                
                // TODO Filter out stray pixels:
                // - Lines with height of 2 pixel (e.g., clipped top/bottom of neighboring character)
                // - Lines with width of 2 pixel (e.g., clipped side of neighboring character)
                // - Fragments smaller than 4x4 pixels (corner artifacts)
                if (br.Height <= 2 || br.Width <= 2 || (br.Width <= 4 && br.Height <= 4))
                    continue;
                
                minCx = Math.Min(minCx, br.Left);
                minCy = Math.Min(minCy, br.Top);
                maxCx = Math.Max(maxCx, br.Right);
                maxCy = Math.Max(maxCy, br.Bottom);
                foundAny = true;
            }

            if (!foundAny) return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);

            int charW = maxCx - minCx;
            int charH = maxCy - minCy;
            if (charW <= 0 || charH <= 0) return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);

            using var charOnly = new Mat(working, new Rect(minCx, minCy, charW, charH));

            // Center in a square canvas with minimal padding (just 5% to avoid edge artifacts)
            int canvasSize = (int)(Math.Max(charW, charH) * 1.1);
            if (canvasSize <= 0) canvasSize = 1;
            
            using var canvas = new Mat(canvasSize, canvasSize, MatType.CV_8UC1, Scalar.Black);
            int offsetX = (canvasSize - charW) / 2;
            int offsetY = (canvasSize - charH) / 2;
            
            using (var sub = new Mat(canvas, new Rect(offsetX, offsetY, charW, charH)))
            {
                charOnly.CopyTo(sub);
            }

            // Resize to target size
            var final = new Mat();
            Cv2.Resize(canvas, final, new OpenCvSharp.Size(targetSize, targetSize));
            
            return final;
        }

        public Mat ExtractCharacter(Mat binary, Rect rect, bool isHighlighted, out bool wasInverted, int targetSize = 32)
        {
            // 1. Get a slightly larger ROI to avoid cut-offs if our grid is slightly off.
            int padX = (int)(rect.Width * 0.15);
            int padY = (int)(rect.Height * 0.15);

            int x1 = Math.Max(0, rect.X - padX);
            int y1 = Math.Max(0, rect.Y - padY);
            int x2 = Math.Min(binary.Width, rect.X + rect.Width + padX);
            int y2 = Math.Min(binary.Height, rect.Y + rect.Height + padY);

            int roiW = x2 - x1;
            int roiH = y2 - y1;
            if (roiW <= 0 || roiH <= 0)
            {
                wasInverted = false;
                return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);
            }

            using var roi = new Mat(binary, new Rect(x1, y1, roiW, roiH));
            using var working = roi.Clone();

            // Detect if this is an inverted character (white background with black text)
            // by checking the pixel ratio even if isHighlighted flag is not set
            int totalPixels = working.Width * working.Height;
            int whitePixels = Cv2.CountNonZero(working);
            double whiteRatio = (double)whitePixels / totalPixels;
            bool isActuallyInverted = whiteRatio > 0.5 || isHighlighted;
            wasInverted = isActuallyInverted;

            if (isActuallyInverted)
            {
                Cv2.BitwiseNot(working, working);
            }

            // 2. Find foreground bounding box
            Cv2.FindContours(working, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            if (contours.Length == 0)
            {
                return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);
            }

            int minCx = int.MaxValue, minCy = int.MaxValue, maxCx = int.MinValue, maxCy = int.MinValue;
            bool foundAny = false;
            foreach (var contour in contours)
            {
                var br = Cv2.BoundingRect(contour);
                
                // Filter out stray pixels:
                // - Lines with height of 2 pixel (e.g., clipped top/bottom of neighboring character)
                // - Lines with width of 2 pixel (e.g., clipped side of neighboring character)
                // - Fragments 4x4 pixels or smaller (corner artifacts)
                if (br.Height <= 2 || br.Width <= 2 || (br.Width <= 4 && br.Height <= 4))
                    continue;
                
                minCx = Math.Min(minCx, br.Left);
                minCy = Math.Min(minCy, br.Top);
                maxCx = Math.Max(maxCx, br.Right);
                maxCy = Math.Max(maxCy, br.Bottom);
                foundAny = true;
            }

            if (!foundAny) return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);

            int charW = maxCx - minCx;
            int charH = maxCy - minCy;
            if (charW <= 0 || charH <= 0) return new Mat(targetSize, targetSize, MatType.CV_8UC1, Scalar.Black);

            using var charOnly = new Mat(working, new Rect(minCx, minCy, charW, charH));

            // 3. Center in a square canvas with some padding
            int canvasSize = (int)(Math.Max(charW, charH) * 1.2);
            if (canvasSize <= 0) canvasSize = 1;
            
            using var canvas = new Mat(canvasSize, canvasSize, MatType.CV_8UC1, Scalar.Black);
            int offsetX = (canvasSize - charW) / 2;
            int offsetY = (canvasSize - charH) / 2;
            
            using (var sub = new Mat(canvas, new Rect(offsetX, offsetY, charW, charH)))
            {
                charOnly.CopyTo(sub);
            }

            // 4. Resize to target size
            var final = new Mat();
            Cv2.Resize(canvas, final, new OpenCvSharp.Size(targetSize, targetSize));
            
            return final;
        }

        public List<OcrCell> RecognizeLine(Mat lineImg, string? charset = null, int minCharDistance = 0)
        {
            if (_recSession == null || _alphabet == null) return new List<OcrCell>();

            int targetH = 48;
            double ratio = (double)lineImg.Width / lineImg.Height;
            int targetW = (int)(targetH * ratio);
            
            targetW = Math.Max(targetW, 8); // Minimum width
            targetW = Math.Min(targetW, 2048); 

            // Resize to target height while maintaining aspect ratio. 
            using var resized = new Mat();
            Cv2.Resize(lineImg, resized, new OpenCvSharp.Size(targetW, targetH));

            using var bgr = new Mat();
            if (resized.Channels() == 1)
                Cv2.CvtColor(resized, bgr, ColorConversionCodes.GRAY2BGR);
            else
                resized.CopyTo(bgr);

            var tensor = new DenseTensor<float>(new[] { 1, 3, targetH, targetW });
            for (int row = 0; row < targetH; row++)
            {
                for (int col = 0; col < targetW; col++)
                {
                    var pixel = bgr.At<Vec3b>(row, col);
                    tensor[0, 0, row, col] = (pixel.Item0 / 255f - 0.5f) / 0.5f; 
                    tensor[0, 1, row, col] = (pixel.Item1 / 255f - 0.5f) / 0.5f; 
                    tensor[0, 2, row, col] = (pixel.Item2 / 255f - 0.5f) / 0.5f; 
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName ?? "input", tensor)
            };

            using var results = _recSession.Run(inputs);
            var output = results.First().AsTensor<float>();

            int T = output.Dimensions[1];
            int C = output.Dimensions[2];
            
            float stepW = (float)lineImg.Width / T;

            var decoded = new List<OcrCell>();
            int lastIdx = -1;
            int charStartT = -1;
            
            for (int t = 0; t < T; t++)
            {
                int bestIdx = 0;
                float maxLogit = float.MinValue;
                for (int c = 0; c < C; c++)
                {
                    float val = output[0, t, c];
                    if (val > maxLogit)
                    {
                        maxLogit = val;
                        bestIdx = c;
                    }
                }

                // Calculate Softmax-like confidence for the best index
                float confidence = maxLogit;
                if (maxLogit > 2.0f || maxLogit < 0.0f)
                {
                    // Apply robust softmax to get probability
                    double sum = 0;
                    for (int c = 0; c < C; c++)
                    {
                        sum += Math.Exp(output[0, t, c] - maxLogit);
                    }
                    confidence = (float)(1.0 / sum);
                }

                if (bestIdx != 0 && bestIdx != lastIdx)
                {
                    if (bestIdx < _alphabet.Count)
                    {
                        string charStr = _alphabet[bestIdx];
                        char actualChar = charStr[0];

                        if (charset != null && !charset.Contains(charStr) && charStr != " ")
                        {
                            char? mapped = MapToSimilar(actualChar, charset);
                            if (mapped.HasValue) 
                            {
                                actualChar = mapped.Value;
                            }
                            else
                            {
                                // Skip if not in charset and no mapping found
                                lastIdx = bestIdx;
                                continue; 
                            }
                        }

                        var newCell = new OcrCell { 
                            Char = actualChar, 
                            Confidence = confidence,
                            X = (int)(t * stepW),
                            Y = 0,
                            W = (int)stepW,
                            H = lineImg.Height
                        };

                        // Check for duplicates too close to each other
                        if (minCharDistance > 0 && decoded.Count > 0)
                        {
                            var last = decoded[^1];
                            int dist = newCell.X.Value - (last.X ?? 0);
                            if (newCell.Char == last.Char && dist < minCharDistance)
                            {
                                if (newCell.Confidence > last.Confidence)
                                {
                                    decoded[^1] = newCell;
                                    charStartT = t;
                                }
                                lastIdx = bestIdx;
                                continue;
                            }
                        }

                        decoded.Add(newCell);
                        charStartT = t;
                    }
                }
                else if (bestIdx != 0 && bestIdx == lastIdx && decoded.Count > 0)
                {
                    // Update confidence of last char if this frame is more confident
                    if (confidence > decoded[^1].Confidence)
                        decoded[^1].Confidence = confidence;

                    // Update width based on current extent
                    decoded[^1].W = (int)((t - charStartT + 1) * stepW);
                }
                
                lastIdx = bestIdx;
            }

            return decoded;
        }

        private char? MapToSimilar(char c, string charset)
        {
            var similarityMap = new Dictionary<char, string>
            {
                { '/', ":" },
                { ':', "/" },
                { '0', "O" },
                { 'O', "0" },
                { 'I', "1" },
                { '1', "I" },
                { 'S', "5" },
                { '5', "S" },
                { 'B', "8" },
                { '8', "B" },
                { '*', "°" },
                { 'o', "°" }
            };

            if (similarityMap.TryGetValue(c, out string? targets))
            {
                foreach (char target in targets)
                {
                    if (charset.Contains(target)) return target;
                }
            }

            return null;
        }

        public void Dispose()
        {
            _recSession?.Dispose();
            LastPreprocessedFrame?.Dispose();
        }
    }
}
