using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DCS.OCR.Library.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace DCS.OCR.Library.Services
{
    public class CustomOcrService : IDisposable
    {
        private InferenceSession? _session;
        private List<char>? _alphabet;
        private string? _inputName;
        private int _inputSize = 32;
        private readonly RowSegmentationService _rowSegmentationService = new RowSegmentationService();

        public bool IsLoaded => _session != null && _alphabet != null;

        public void LoadModel(string modelPath, string alphabetPath)
        {
            _session = new InferenceSession(modelPath);
            _inputName = _session.InputMetadata.Keys.First();
            
            if (File.Exists(alphabetPath))
            {
                string text = File.ReadAllText(alphabetPath);
                _alphabet = text.Trim().ToList();
            }
            else
            {
                // Fallback alphabet if not provided
                _alphabet = " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,:;!?-+/()[]{}<>|°".ToList();
            }
        }

        public OcrCell[][] ProcessFrame(Mat frame, Profile profile, RapidOcrService preprocessor)
        {
            if (_session == null || _alphabet == null)
                throw new InvalidOperationException("Model not loaded");

            // Use the excellent preprocessing logic from the other service
            // Store in preprocessor.LastPreprocessedFrame so overlays work
            preprocessor.LastPreprocessedFrame?.Dispose();
            preprocessor.LastPreprocessedFrame = preprocessor.PreprocessImage(frame,
                profile.Invert ?? false,
                profile.UseGlobalThreshold ?? true,
                profile.BinaryThreshold ?? 84,
                profile.GreenOnly ?? true,
                profile.Sharpness ?? 0,
                profile.Contrast ?? 1.0f,
                profile.Dilation ?? 0);
            
            var binary = preprocessor.LastPreprocessedFrame;

            int rows = profile.Rows;
            int cols = profile.Cols;
            float cellH = (float)frame.Height / rows;

            var grid = new OcrCell[rows][];

            List<RowSegmentationService.RowBand>? rowBands = null;
            
            // Always detect inverted regions for display (ShowInv checkbox)
            preprocessor.LastNormalizedFrame?.Dispose();
            preprocessor.LastNormalizedFrame = null;
            preprocessor.LastInvertedRegions = null;
            
            if (binary != null)
            {
                preprocessor.LastNormalizedFrame = RowSegmentationService.NormalizeInvertedRegions(binary, out var invertedRegions);
                preprocessor.LastInvertedRegions = invertedRegions;
            }
            
            // Use the normalized frame for OCR so inverted text appears normal (white on black)
            var frameForOcr = preprocessor.LastNormalizedFrame ?? binary;
            
            // Dynamic row segmentation should use the normalized frame (after inversion is corrected)
            // so that inverted regions appear as normal text for proper row detection
            if (profile.UseDynamicRowSegmentation == true && frameForOcr != null)
            {
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

            int prevDynamicY2 = 0;

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

                if (rowBands != null)
                {
                    y1 = Math.Max(y1, prevDynamicY2);
                    y2 = Math.Max(y2, y1 + 1);
                    prevDynamicY2 = y2;
                }

                int h = y2 - y1;

                if (y1 < 0 || y1 + h > frame.Height || h <= 0) 
                {
                    for (int c = 0; c < cols; c++)
                    {
                        int x = (int)(rowXStart + c * rowColW);
                        int w = (int)rowColW;
                        grid[r][c] = new OcrCell 
                        { 
                            Char = ' ', 
                            Confidence = 0,
                            X = x,
                            Y = y1,
                            W = w,
                            H = h
                        };
                    }
                    continue;
                }

                bool isHighlighted = false;
                float alignmentPadding = profile.AlignmentPadding ?? 0;
                
                // Detect character boundaries using vertical projection for adaptive cell widths
                // Use normalized frame so inverted regions appear as normal text
                using var rowROI = new Mat(frameForOcr, new Rect(0, Math.Clamp(y1, 0, frame.Height - 1), frame.Width, Math.Min(h, frame.Height - y1)));
                var charBoundaries = preprocessor.DetectCharacterBoundaries(rowROI);
                
                // Create adaptive cell widths based on detected characters
                var adaptiveCells = preprocessor.CreateAdaptiveCells(charBoundaries, cols, frame.Width, y1, h);
                
                // Initialize grid with adaptive cells
                for (int c = 0; c < cols; c++)
                {
                    grid[r][c] = adaptiveCells[c];
                }

                // Get alignment info for highlighted detection
                if (profile.AutoAlign == true)
                {
                    var alignment = preprocessor.GetDynamicRowAlignment(rowROI, rowColW, cols, rowXStart, y1);
                    if (alignment.HasText)
                    {
                        isHighlighted = alignment.IsHighlighted;
                    }
                }

                // Now process each cell with the adaptive boundaries
                for (int c = 0; c < cols; c++)
                {
                    var cellInfo = grid[r][c];
                    int x = cellInfo.X ?? (int)(rowXStart + c * rowColW);
                    int w = cellInfo.W ?? (int)rowColW;

                    if (x < 0 || x + w > frame.Width || w <= 0)
                    {
                        grid[r][c] = new OcrCell 
                        { 
                            Char = ' ', 
                            Confidence = 0,
                            X = x,
                            Y = y1,
                            W = w,
                            H = h
                        };
                        continue;
                    }

                    // Use robust character extraction with normalized frame (inverted regions already un-inverted)
                    using var charCrop = preprocessor.ExtractCharacter(frameForOcr, new Rect(x, y1, w, h), isHighlighted, out bool wasInverted);
                    
                    // Check if cell is "empty" (low pixel count) to save inference time
                    // Increased threshold to 5% to avoid false positives like '.', ':', '-'
                    if (Cv2.CountNonZero(charCrop) < (32 * 32 * 0.05)) // Less than 5% pixels are white
                    {
                        grid[r][c] = new OcrCell 
                        { 
                            Char = ' ', 
                            Confidence = 1.0f,
                            X = x,
                            Y = y1,
                            W = w,
                            H = h,
                            IsInverted = wasInverted
                        };
                        continue;
                    }

                    var result = PredictCharacter(charCrop);
                    grid[r][c] = new OcrCell 
                    { 
                        Char = result.Char, 
                        Confidence = result.Confidence,
                        X = x,
                        Y = y1,
                        W = w,
                        H = h,
                        IsInverted = wasInverted
                    };
                }
            }

            return grid;
        }

        private (char Char, float Confidence) PredictCharacter(Mat charCrop)
        {
            using var resized = new Mat();
            Cv2.Resize(charCrop, resized, new OpenCvSharp.Size(_inputSize, _inputSize));
            
            var tensor = new DenseTensor<float>(new[] { 1, 1, _inputSize, _inputSize });
            for (int row = 0; row < _inputSize; row++)
            {
                for (int col = 0; col < _inputSize; col++)
                {
                    // Map [0, 255] to [-1, 1] to match training normalization
                    tensor[0, 0, row, col] = (resized.At<byte>(row, col) / 255f - 0.5f) / 0.5f;
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName ?? "input", tensor)
            };

            using var results = _session!.Run(inputs);
            var output = results.First().AsTensor<float>();
            
            // Softmax & Find Best
            float maxVal = float.MinValue;
            int bestIdx = 0;
            
            float[] logits = output.ToArray();
            for (int i = 0; i < logits.Length; i++)
            {
                if (logits[i] > maxVal)
                {
                    maxVal = logits[i];
                    bestIdx = i;
                }
            }

            // Calculate confidence
            double sum = 0;
            foreach (var logit in logits) sum += Math.Exp(logit - maxVal);
            float confidence = (float)(1.0 / sum);

            char c = (bestIdx < _alphabet!.Count) ? _alphabet[bestIdx] : '?';
            return (c, confidence);
        }

        public void Dispose()
        {
            _session?.Dispose();
        }
    }
}
