using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace DCS.OCR.Library.Services
{
    public class RowSegmentationService
    {
        public sealed class RowBand
        {
            public int Y1 { get; init; }
            public int Y2 { get; init; }
            public float Score { get; init; }

            public int Height => Math.Max(0, Y2 - Y1);
        }

        /// <summary>
        /// Anchor-based row segmentation: uses provided row centers and adjusts within offset/height constraints.
        /// The row center is constrained to be within [anchor - yOffset, anchor + yOffset].
        /// The row height is constrained to be within [minHeight, maxHeight].
        /// Rows are guaranteed to be non-overlapping with at least 'gap' pixels between them.
        /// </summary>
        public List<RowBand> SegmentRowsAnchored(Mat binary, List<int> rowCenters, int yOffset = 10, int minHeight = 32, int maxHeight = 60, int gap = 2)
        {
            if (binary.Empty() || rowCenters == null || rowCenters.Count == 0)
                return FallbackRows(binary, rowCenters?.Count ?? 0);

            int imgHeight = binary.Height;
            int halfMin = minHeight / 2;
            int halfMax = maxHeight / 2;

            // Compute horizontal projection for ink-based growth
            using var projMat = new Mat();
            Cv2.Reduce(binary, projMat, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32F);
            var proj = new float[imgHeight];
            for (int y = 0; y < imgHeight; y++)
            {
                proj[y] = projMat.At<float>(y, 0) / 255f;
            }
            float maxProj = proj.Max();
            var smoothProj = Smooth1D(proj, 2);
            float inkThresh = Math.Max(1, maxProj * 0.02f);

            var bands = new List<RowBand>();

            for (int i = 0; i < rowCenters.Count; i++)
            {
                int anchor = rowCenters[i];
                
                // The row center is the anchor, clamped to valid image bounds
                int rowCenter = Math.Clamp(anchor, halfMin, imgHeight - halfMin);
                
                // Start with minHeight band around the center
                int y1 = rowCenter - halfMin;
                int y2 = rowCenter + halfMin;
                
                // Grow the band based on ink detection, up to maxHeight
                int maxGrowUp = halfMax - halfMin;
                int maxGrowDown = halfMax - halfMin;
                
                // Grow upward while there's ink
                int grewUp = 0;
                while (grewUp < maxGrowUp && y1 > 0 && smoothProj[Math.Max(0, y1 - 1)] >= inkThresh)
                {
                    y1--;
                    grewUp++;
                }
                
                // Grow downward while there's ink
                int grewDown = 0;
                while (grewDown < maxGrowDown && y2 < imgHeight && smoothProj[Math.Min(imgHeight - 1, y2)] >= inkThresh)
                {
                    y2++;
                    grewDown++;
                }
                
                // Clamp to image bounds
                y1 = Math.Max(0, y1);
                y2 = Math.Min(imgHeight, y2);
                
                // Ensure minimum height is met after clamping
                int height = y2 - y1;
                if (height < minHeight)
                {
                    if (y1 == 0)
                        y2 = Math.Min(imgHeight, minHeight);
                    else if (y2 == imgHeight)
                        y1 = Math.Max(0, imgHeight - minHeight);
                }

                bands.Add(new RowBand { Y1 = y1, Y2 = y2, Score = 1.0f });
            }

            // Ensure rows don't overlap and have minimum gap between them
            for (int i = 0; i < bands.Count - 1; i++)
            {
                var current = bands[i];
                var next = bands[i + 1];
                
                // Check if rows overlap or don't have enough gap
                int requiredNextY1 = current.Y2 + gap;
                if (next.Y1 < requiredNextY1)
                {
                    // Need to adjust - shrink current row's bottom to make room
                    // Calculate the midpoint between the two anchors
                    int currentAnchor = rowCenters[i];
                    int nextAnchor = rowCenters[i + 1];
                    int midpoint = (currentAnchor + nextAnchor) / 2;
                    
                    // Current row ends at midpoint - gap/2, next row starts at midpoint + gap/2
                    int newCurrentY2 = midpoint - (gap + 1) / 2;
                    int newNextY1 = midpoint + gap / 2;
                    
                    // Ensure minimum heights are still met
                    if (newCurrentY2 - current.Y1 < minHeight)
                    {
                        newCurrentY2 = current.Y1 + minHeight;
                        newNextY1 = newCurrentY2 + gap;
                    }
                    
                    if (next.Y2 - newNextY1 < minHeight)
                    {
                        newNextY1 = next.Y2 - minHeight;
                        newCurrentY2 = newNextY1 - gap;
                    }
                    
                    // Final clamp to image bounds
                    newCurrentY2 = Math.Clamp(newCurrentY2, current.Y1 + 1, imgHeight);
                    newNextY1 = Math.Clamp(newNextY1, 0, next.Y2 - 1);
                    
                    bands[i] = new RowBand { Y1 = current.Y1, Y2 = newCurrentY2, Score = current.Score };
                    bands[i + 1] = new RowBand { Y1 = newNextY1, Y2 = next.Y2, Score = next.Score };
                }
            }

            return bands;
        }

        public List<RowBand> SegmentRows(Mat binary, int expectedRows, float alpha = 0.15f, int smoothRadius = 2, int minBandHeight = 10, int mergeGap = 2, int maxBandHeight = 60)
        {
            if (binary.Empty()) return FallbackRows(binary, expectedRows);
            if (binary.Type() != MatType.CV_8UC1) throw new ArgumentException("binary must be CV_8UC1");
            if (expectedRows <= 0) return new List<RowBand>();

            // Normalize inverted regions before segmentation
            // This prevents inverted text (white bg) from merging multiple rows
            using var normalized = NormalizeInvertedRegions(binary);

            int h = normalized.Height;
            using var projMat = new Mat();
            Cv2.Reduce(normalized, projMat, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32F);

            var proj = new float[h];
            for (int y = 0; y < h; y++)
            {
                proj[y] = projMat.At<float>(y, 0) / 255f;
            }

            float max = proj.Max();
            if (max <= 1) return FallbackRows(binary, expectedRows);

            var smooth = Smooth1D(proj, smoothRadius);
            float thresh = Math.Max(1, max * alpha);

            var bands = new List<RowBand>();
            bool inBand = false;
            int start = 0;
            float bandSum = 0;

            for (int y = 0; y < h; y++)
            {
                bool active = smooth[y] >= thresh;
                if (active)
                {
                    if (!inBand)
                    {
                        inBand = true;
                        start = y;
                        bandSum = 0;
                    }
                    bandSum += smooth[y];
                }
                else
                {
                    if (inBand)
                    {
                        int end = y;
                        inBand = false;
                        if (end - start >= minBandHeight)
                        {
                            bands.Add(new RowBand { Y1 = start, Y2 = end, Score = bandSum });
                        }
                    }
                }
            }

            if (inBand)
            {
                int end = h;
                if (end - start >= minBandHeight)
                {
                    bands.Add(new RowBand { Y1 = start, Y2 = end, Score = bandSum });
                }
            }

            if (bands.Count == 0) return FallbackRows(binary, expectedRows);

            bands = GrowBandsToInk(bands, smooth, max, h);

            bands = bands.OrderBy(b => b.Y1).ToList();
            bands = MergeCloseBands(bands, mergeGap);
            bands = ExpandAndResolveOverlaps(bands, smooth, h);

            // Enforce min/max height constraints on all bands
            // bands = EnforceHeightConstraints(bands, minBandHeight, maxBandHeight, h);

            if (bands.Count == expectedRows) return ClampBands(bands, h);

            if (bands.Count > expectedRows)
            {
                var top = bands.OrderByDescending(b => b.Score).Take(expectedRows).OrderBy(b => b.Y1).ToList();
                return ClampBands(top, h);
            }

            // If we found fewer bands than expected, prefer splitting the tallest bands rather than
            // uniformly resampling (which tends to make all rows the same height).
            bands = SplitBandsToCount(bands, expectedRows, smooth, h, maxBandHeight);
            if (bands.Count == expectedRows) return ClampBands(bands, h);

            return ResampleBandsToCount(bands, expectedRows, h);
        }

        private static List<RowBand> SplitBandsToCount(List<RowBand> bands, int expectedRows, float[] smoothProj, int height, int maxBandHeight = 60)
        {
            if (bands.Count == 0) return bands;
            if (expectedRows <= 0) return new List<RowBand>();

            var result = bands.OrderBy(b => b.Y1).ToList();

            int guard = 0;
            while (result.Count < expectedRows && guard++ < 10000)
            {
                int idx = 0;
                int bestH = -1;
                for (int i = 0; i < result.Count; i++)
                {
                    int h0 = result[i].Height;
                    // Prioritize splitting bands that exceed maxBandHeight
                    if (h0 > maxBandHeight || h0 > bestH)
                    {
                        bestH = h0;
                        idx = i;
                    }
                }

                var b = result[idx];
                if (b.Height <= 2) break;

                int split = FindValleySplit(smoothProj, b.Y1, b.Y2);
                if (split <= b.Y1 + 1 || split >= b.Y2 - 1)
                {
                    split = (b.Y1 + b.Y2) / 2;
                }

                if (split <= b.Y1 + 1 || split >= b.Y2 - 1) break;

                float frac = (float)(split - b.Y1) / Math.Max(1, b.Height);
                float s1 = b.Score * frac;
                float s2 = b.Score - s1;

                var left = new RowBand { Y1 = b.Y1, Y2 = split, Score = s1 };
                var right = new RowBand { Y1 = split, Y2 = b.Y2, Score = s2 };

                result.RemoveAt(idx);
                result.Insert(idx, right);
                result.Insert(idx, left);
            }

            result = ClampBands(result.OrderBy(b => b.Y1).ToList(), height);

            // Enforce strict non-overlap after splitting.
            for (int i = 0; i < result.Count - 1; i++)
            {
                var a = result[i];
                var b = result[i + 1];
                if (a.Y2 > b.Y1)
                {
                    int y = a.Y2;
                    result[i + 1] = new RowBand { Y1 = Math.Min(y, b.Y2 - 1), Y2 = b.Y2, Score = b.Score };
                }
            }

            return ClampBands(result, height);
        }

        private static List<RowBand> EnforceHeightConstraints(List<RowBand> bands, int minHeight, int maxHeight, int imageHeight)
        {
            var result = new List<RowBand>(bands.Count);
            foreach (var b in bands)
            {
                int y1 = b.Y1;
                int y2 = b.Y2;
                int h = y2 - y1;

                // If band is too short, expand it symmetrically
                if (h < minHeight)
                {
                    int expand = (minHeight - h + 1) / 2;
                    y1 = Math.Max(0, y1 - expand);
                    y2 = Math.Min(imageHeight, y2 + expand);
                    // If still too short after symmetric expansion, expand in available direction
                    h = y2 - y1;
                    if (h < minHeight)
                    {
                        if (y1 > 0) y1 = Math.Max(0, y2 - minHeight);
                        else y2 = Math.Min(imageHeight, y1 + minHeight);
                    }
                }

                // If band is too tall, shrink it from the center
                h = y2 - y1;
                if (h > maxHeight)
                {
                    int center = (y1 + y2) / 2;
                    y1 = center - maxHeight / 2;
                    y2 = y1 + maxHeight;
                    // Clamp to image bounds
                    if (y1 < 0) { y1 = 0; y2 = maxHeight; }
                    if (y2 > imageHeight) { y2 = imageHeight; y1 = imageHeight - maxHeight; }
                }

                result.Add(new RowBand { Y1 = Math.Max(0, y1), Y2 = Math.Min(imageHeight, y2), Score = b.Score });
            }
            return result;
        }

        private static List<RowBand> GrowBandsToInk(List<RowBand> bands, float[] smoothProj, float maxProj, int height)
        {
            if (bands.Count == 0) return bands;

            float edgeThresh = Math.Max(1, maxProj * 0.02f);
            int maxGrow = 24;

            var grown = new List<RowBand>(bands.Count);
            foreach (var b in bands)
            {
                int y1 = b.Y1;
                int y2 = b.Y2;

                int steps = 0;
                while (y1 > 0 && steps < maxGrow && smoothProj[Math.Max(0, y1 - 1)] >= edgeThresh)
                {
                    y1--;
                    steps++;
                }

                steps = 0;
                while (y2 < height && steps < maxGrow && smoothProj[Math.Min(smoothProj.Length - 1, y2)] >= edgeThresh)
                {
                    y2++;
                    steps++;
                }

                if (y2 <= y1) y2 = Math.Min(height, y1 + 1);
                grown.Add(new RowBand { Y1 = y1, Y2 = y2, Score = b.Score });
            }

            return grown;
        }

        private static List<RowBand> ExpandAndResolveOverlaps(List<RowBand> bands, float[] smoothProj, int height)
        {
            if (bands.Count == 0) return bands;

            var expanded = bands.OrderBy(b => b.Y1).ToList();

            // Resolve overlaps by splitting the overlap at the midpoint.
            for (int i = 0; i < expanded.Count - 1; i++)
            {
                var a = expanded[i];
                var b = expanded[i + 1];

                if (a.Y2 > b.Y1)
                {
                    int split = FindValleySplit(smoothProj, b.Y1, a.Y2);
                    int aY2 = Math.Max(a.Y1 + 1, split);
                    int bY1 = Math.Min(b.Y2 - 1, split);

                    // If midpoint split collapsed one band, fall back to tight non-overlap.
                    if (aY2 <= a.Y1) aY2 = a.Y1 + 1;
                    if (bY1 >= b.Y2) bY1 = b.Y2 - 1;

                    expanded[i] = new RowBand { Y1 = a.Y1, Y2 = aY2, Score = a.Score };
                    expanded[i + 1] = new RowBand { Y1 = bY1, Y2 = b.Y2, Score = b.Score };
                }
            }

            return ClampBands(expanded, height);
        }

        private static int FindValleySplit(float[] smoothProj, int startY, int endY)
        {
            if (smoothProj.Length == 0) return (startY + endY) / 2;

            int a = Math.Clamp(startY, 0, smoothProj.Length - 1);
            int b = Math.Clamp(endY, 0, smoothProj.Length - 1);
            if (b <= a) return a;

            int margin = 2;
            int sa = Math.Min(b, a + margin);
            int sb = Math.Max(sa, b - margin);
            if (sb <= sa) return (a + b) / 2;

            float best = float.MaxValue;
            float worst = float.MinValue;
            int bestY = (sa + sb) / 2;

            for (int y = sa; y <= sb; y++)
            {
                float v = smoothProj[y];
                if (v > worst) worst = v;
                if (v < best)
                {
                    best = v;
                    bestY = y;
                }
            }

            // If the overlap region is uniformly high (common with inverted/highlight background),
            // there is no meaningful valley: split at midpoint instead of picking an arbitrary edge.
            if (worst <= 0) return (a + b) / 2;
            if (best / worst > 0.85f) return (a + b) / 2;

            return bestY;
        }

        private static float[] Smooth1D(float[] src, int radius)
        {
            if (radius <= 0) return (float[])src.Clone();
            int n = src.Length;
            var dst = new float[n];
            int win = radius * 2 + 1;

            for (int i = 0; i < n; i++)
            {
                float sum = 0;
                int count = 0;
                int a = Math.Max(0, i - radius);
                int b = Math.Min(n - 1, i + radius);
                for (int j = a; j <= b; j++)
                {
                    sum += src[j];
                    count++;
                }
                dst[i] = sum / Math.Max(1, count);
            }

            return dst;
        }

        private static List<RowBand> MergeCloseBands(List<RowBand> bands, int mergeGap)
        {
            if (bands.Count <= 1) return bands;

            var merged = new List<RowBand>();
            RowBand cur = bands[0];

            for (int i = 1; i < bands.Count; i++)
            {
                var b = bands[i];
                if (b.Y1 - cur.Y2 <= mergeGap)
                {
                    cur = new RowBand
                    {
                        Y1 = cur.Y1,
                        Y2 = Math.Max(cur.Y2, b.Y2),
                        Score = cur.Score + b.Score
                    };
                }
                else
                {
                    merged.Add(cur);
                    cur = b;
                }
            }

            merged.Add(cur);
            return merged;
        }

        private static List<RowBand> ResampleBandsToCount(List<RowBand> bands, int expectedRows, int height)
        {
            if (bands.Count == 0) return FallbackRows(height, expectedRows);
            if (expectedRows <= 0) return new List<RowBand>();

            var result = new List<RowBand>();

            float top = bands.First().Y1;
            float bottom = bands.Last().Y2;
            float span = Math.Max(1, bottom - top);

            for (int i = 0; i < expectedRows; i++)
            {
                float t0 = (float)i / expectedRows;
                float t1 = (float)(i + 1) / expectedRows;

                int y1 = (int)Math.Round(top + t0 * span);
                int y2 = (int)Math.Round(top + t1 * span);
                if (y2 <= y1) y2 = y1 + 1;

                result.Add(new RowBand { Y1 = y1, Y2 = y2, Score = 0 });
            }

            return ClampBands(result, height);
        }

        private static List<RowBand> ClampBands(List<RowBand> bands, int height)
        {
            var result = new List<RowBand>(bands.Count);
            foreach (var b in bands)
            {
                int y1 = Math.Clamp(b.Y1, 0, Math.Max(0, height - 1));
                int y2 = Math.Clamp(b.Y2, y1 + 1, height);
                result.Add(new RowBand { Y1 = y1, Y2 = y2, Score = b.Score });
            }
            return result;
        }

        private static List<RowBand> FallbackRows(Mat binary, int expectedRows)
        {
            return FallbackRows(binary.Height, expectedRows);
        }

        private static List<RowBand> FallbackRows(int height, int expectedRows)
        {
            if (expectedRows <= 0) return new List<RowBand>();
            int cellH = Math.Max(1, height / expectedRows);

            var bands = new List<RowBand>(expectedRows);
            for (int r = 0; r < expectedRows; r++)
            {
                int y1 = r * cellH;
                int y2 = (r == expectedRows - 1) ? height : (r + 1) * cellH;
                if (y2 <= y1) y2 = Math.Min(height, y1 + 1);
                bands.Add(new RowBand { Y1 = y1, Y2 = y2, Score = 0 });
            }

            return bands;
        }

        /// <summary>
        /// Normalizes inverted regions (white background with black text) to normal polarity.
        /// This prevents inverted text from causing row segmentation to merge multiple rows.
        /// </summary>
        private static Mat NormalizeInvertedRegions(Mat binary)
        {
            return NormalizeInvertedRegions(binary, out _);
        }

        /// <summary>
        /// Normalizes inverted regions and returns the detected inverted region bounds.
        /// Detects white boxes (inverted text regions) using contour detection and inverts only those areas.
        /// </summary>
        public static Mat NormalizeInvertedRegions(Mat binary, out List<Rect> invertedRegions)
        {
            invertedRegions = new List<Rect>();
            var result = binary.Clone();
            int width = binary.Width;
            int height = binary.Height;

            // Find contours of white regions (potential inverted text boxes)
            Cv2.FindContours(binary, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            // Collect candidate boxes that meet minimum size requirements
            var candidateBoxes = new List<Rect>();
            foreach (var contour in contours)
            {
                var boundingRect = Cv2.BoundingRect(contour);
                
                // Filter: minimum size 20x35 for inverted segments, not the entire image
                if (boundingRect.Width < 20 || boundingRect.Height < 35) continue;
                if (boundingRect.Width > width * 0.9 && boundingRect.Height > height * 0.9) continue;
                
                // Check if this region has high white pixel density (indicating a white box)
                using var roi = new Mat(binary, boundingRect);
                int totalPixels = boundingRect.Width * boundingRect.Height;
                int whitePixels = Cv2.CountNonZero(roi);
                double whiteRatio = (double)whitePixels / totalPixels;

                // If more than 40% white pixels, this is likely an inverted text box
                if (whiteRatio > 0.40)
                {
                    candidateBoxes.Add(boundingRect);
                }
            }

            // Merge overlapping or adjacent boxes into single segments
            var mergedBoxes = MergeOverlappingRects(candidateBoxes, mergeGap: 5);

            // Invert each merged region and add to output
            foreach (var box in mergedBoxes)
            {
                // Clamp to image bounds
                int x1 = Math.Max(0, box.X);
                int y1 = Math.Max(0, box.Y);
                int x2 = Math.Min(width, box.X + box.Width);
                int y2 = Math.Min(height, box.Y + box.Height);
                var clampedBox = new Rect(x1, y1, x2 - x1, y2 - y1);
                
                if (clampedBox.Width > 0 && clampedBox.Height > 0)
                {
                    // Invert the region directly
                    for (int y = clampedBox.Y; y < clampedBox.Y + clampedBox.Height; y++)
                    {
                        for (int x = clampedBox.X; x < clampedBox.X + clampedBox.Width; x++)
                        {
                            byte val = result.At<byte>(y, x);
                            result.Set(y, x, (byte)(255 - val));
                        }
                    }
                    
                    // Clear the edge pixels (set to black) to prevent them from affecting text detection
                    // This removes the white border that was the edge of the inverted box
                    int edgeWidth = 3;
                    for (int y = clampedBox.Y; y < clampedBox.Y + clampedBox.Height; y++)
                    {
                        // Left edge
                        for (int x = clampedBox.X; x < Math.Min(clampedBox.X + edgeWidth, clampedBox.X + clampedBox.Width); x++)
                            result.Set(y, x, (byte)0);
                        // Right edge
                        for (int x = Math.Max(clampedBox.X, clampedBox.X + clampedBox.Width - edgeWidth); x < clampedBox.X + clampedBox.Width; x++)
                            result.Set(y, x, (byte)0);
                    }
                    for (int x = clampedBox.X; x < clampedBox.X + clampedBox.Width; x++)
                    {
                        // Top edge
                        for (int y = clampedBox.Y; y < Math.Min(clampedBox.Y + edgeWidth, clampedBox.Y + clampedBox.Height); y++)
                            result.Set(y, x, (byte)0);
                        // Bottom edge
                        for (int y = Math.Max(clampedBox.Y, clampedBox.Y + clampedBox.Height - edgeWidth); y < clampedBox.Y + clampedBox.Height; y++)
                            result.Set(y, x, (byte)0);
                    }
                    
                    invertedRegions.Add(clampedBox);
                }
            }

            return result;
        }

        /// <summary>
        /// Merges overlapping or nearby rectangles into single bounding boxes.
        /// </summary>
        private static List<Rect> MergeOverlappingRects(List<Rect> rects, int mergeGap = 5)
        {
            if (rects.Count == 0) return new List<Rect>();

            var merged = new List<Rect>();
            var used = new bool[rects.Count];

            for (int i = 0; i < rects.Count; i++)
            {
                if (used[i]) continue;

                var current = rects[i];
                bool didMerge;

                do
                {
                    didMerge = false;
                    for (int j = 0; j < rects.Count; j++)
                    {
                        if (i == j || used[j]) continue;

                        // Check if rectangles overlap or are within mergeGap pixels
                        var expanded = new Rect(
                            current.X - mergeGap,
                            current.Y - mergeGap,
                            current.Width + mergeGap * 2,
                            current.Height + mergeGap * 2);

                        if (RectsIntersect(expanded, rects[j]))
                        {
                            // Merge: create union bounding box
                            int x1 = Math.Min(current.X, rects[j].X);
                            int y1 = Math.Min(current.Y, rects[j].Y);
                            int x2 = Math.Max(current.X + current.Width, rects[j].X + rects[j].Width);
                            int y2 = Math.Max(current.Y + current.Height, rects[j].Y + rects[j].Height);
                            current = new Rect(x1, y1, x2 - x1, y2 - y1);
                            used[j] = true;
                            didMerge = true;
                        }
                    }
                } while (didMerge);

                merged.Add(current);
                used[i] = true;
            }

            return merged;
        }

        private static bool RectsIntersect(Rect a, Rect b)
        {
            return a.X < b.X + b.Width && a.X + a.Width > b.X &&
                   a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
        }
    }
}
