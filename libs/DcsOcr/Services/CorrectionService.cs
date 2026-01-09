using System;
using System.Collections.Generic;
using System.Linq;
using DCS.OCR.Library.Models;

namespace DCS.OCR.Library.Services
{
    public class CorrectionService
    {
        public string[] Correct(OcrCell[][] ocrGrid, char[][]? refGrid, List<string>? dcsValues = null, List<ContextRule>? contextRules = null, float threshold = 0.8f)
        {
            // 1. Apply Context-Aware Rules (e.g. 1NS -> INS if 1 is low confidence)
            if (contextRules != null && contextRules.Count > 0)
            {
                ApplyContextRules(ocrGrid, contextRules);
            }

            var resultLines = new string[ocrGrid.Length];
            
            // 2. Initial grid with mapping-based correction (if available)
            for (int r = 0; r < ocrGrid.Length; r++)
            {
                if (refGrid != null && r < refGrid.Length)
                {
                    resultLines[r] = CorrectLineWithMapping(ocrGrid[r], refGrid[r], threshold);
                }
                else
                {
                    // Only use characters with > 50% confidence as per user request
                    resultLines[r] = new string(ocrGrid[r].Select(c => c.Confidence > 0.5f ? c.Char : ' ').ToArray());
                }
            }

            // 3. Apply Fuzzy Correction using raw DCS values
            if (dcsValues != null && dcsValues.Count > 0)
            {
                // Order by length descending to match longer, more unique strings first
                foreach (var val in dcsValues.OrderByDescending(v => v.Length))
                {
                    if (val.Length < 3) continue; // Skip very short strings to avoid false positives

                    float bestScore = -1;
                    int bestRow = -1;
                    int bestCol = -1;

                    for (int r = 0; r < ocrGrid.Length; r++)
                    {
                        var (col, score) = FindBestMatch(ocrGrid[r], val);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestRow = r;
                            bestCol = col;
                        }
                    }

                    // If we found a strong match (at least 60% of trusted characters match)
                    if (bestScore > 0.6f)
                    {
                        char[] rowChars = resultLines[bestRow].ToCharArray();
                        for (int i = 0; i < val.Length; i++)
                        {
                            // If it's a strong match, we trust the DCS value (list_indications) 
                            // for the whole string segment as per user request.
                            rowChars[bestCol + i] = val[i];
                        }
                        resultLines[bestRow] = new string(rowChars);
                    }
                }
            }

            return resultLines;
        }

        public char[][] GetFuzzyMappedGrid(OcrCell[][] ocrGrid, List<string> dcsValues, float minScore = 0.6f)
        {
            var grid = new char[ocrGrid.Length][];
            for (int i = 0; i < ocrGrid.Length; i++)
            {
                grid[i] = new char[ocrGrid[i].Length];
                for (int j = 0; j < ocrGrid[i].Length; j++) grid[i][j] = '\0';
            }

            if (dcsValues == null) return grid;

            // Order by length descending to match longer, more unique strings first
            foreach (var val in dcsValues.OrderByDescending(v => v.Length))
            {
                if (val.Length < 2) continue;

                float bestScore = -1;
                int bestRow = -1;
                int bestCol = -1;

                for (int r = 0; r < ocrGrid.Length; r++)
                {
                    var (col, score) = FindBestMatch(ocrGrid[r], val);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRow = r;
                        bestCol = col;
                    }
                }

                if (bestScore >= minScore)
                {
                    for (int i = 0; i < val.Length; i++)
                    {
                        grid[bestRow][bestCol + i] = val[i];
                    }
                }
            }

            return grid;
        }

        private void ApplyContextRules(OcrCell[][] grid, List<ContextRule> rules)
        {
            foreach (var row in grid)
            {
                // Find words in the row
                int start = -1;
                for (int c = 0; c < row.Length; c++)
                {
                    bool isChar = row[c].Char != ' ' && row[c].Char != '\0';
                    if (isChar && start == -1)
                    {
                        start = c;
                    }
                    else if (!isChar && start != -1)
                    {
                        ProcessWord(row, start, c, rules);
                        start = -1;
                    }
                }
                if (start != -1)
                {
                    ProcessWord(row, start, row.Length, rules);
                }
            }
        }

        private void ProcessWord(OcrCell[] row, int start, int end, List<ContextRule> rules)
        {
            int length = end - start;
            char[] wordChars = new char[length];
            for (int i = 0; i < length; i++) 
            {
                // Only use characters with > 50% confidence for context matching
                wordChars[i] = row[start + i].Confidence > 0.5f ? row[start + i].Char : ' ';
            }
            string word = new string(wordChars);

            foreach (var rule in rules)
            {
                if (rule.Patterns == null || rule.Target == null) continue;

                foreach (var pattern in rule.Patterns)
                {
                    if (word == pattern && pattern.Length == rule.Target.Length)
                    {
                        // Check if we should swap based on confidence
                        bool changed = false;
                        for (int i = 0; i < length; i++)
                        {
                            if (row[start + i].Char != rule.Target[i])
                            {
                                if (row[start + i].Confidence < rule.Threshold)
                                {
                                    row[start + i].Char = rule.Target[i];
                                    changed = true;
                                }
                            }
                        }
                        if (changed) return; // Only apply one rule per word
                    }
                }
            }
        }

        private string CorrectLineWithMapping(OcrCell[] ocrLine, char[] refLine, float threshold)
        {
            char[] corrected = new char[ocrLine.Length];
            for (int i = 0; i < ocrLine.Length; i++)
            {
                if (i < refLine.Length && refLine[i] != '\0' && ocrLine[i].Confidence < threshold)
                {
                    corrected[i] = refLine[i];
                }
                else
                {
                    // Use OCR char only if more than 50% certain
                    corrected[i] = ocrLine[i].Confidence > 0.5f ? ocrLine[i].Char : ' ';
                }
            }
            return new string(corrected);
        }

        private (int col, float score) FindBestMatch(OcrCell[] ocrLine, string val)
        {
            int bestCol = -1;
            float maxScore = -1;

            // Slide string across the row
            for (int c = 0; c <= ocrLine.Length - val.Length; c++)
            {
                int matches = 0;
                for (int i = 0; i < val.Length; i++)
                {
                    // Only compare if OCR is more than 50% certain as per user request
                    if (ocrLine[c + i].Confidence > 0.5f)
                    {
                        if (ocrLine[c + i].Char == val[i] || AreSimilar(ocrLine[c + i].Char, val[i]))
                        {
                            matches++;
                        }
                    }
                }

                float score = (float)matches / val.Length;
                if (score > maxScore)
                {
                    maxScore = score;
                    bestCol = c;
                }
            }

            return (bestCol, maxScore);
        }

        private bool AreSimilar(char a, char b)
        {
            if (a == b) return true;
            
            var pairs = new[]
            {
                (':', '/'), ('0', 'O'), ('1', 'I'), ('5', 'S'), ('8', 'B'), ('*', '°'), ('o', '°')
            };

            foreach (var (p1, p2) in pairs)
            {
                if ((a == p1 && b == p2) || (a == p2 && b == p1)) return true;
            }

            return false;
        }

        public static int LevenshteinDistance(string s, string t)
        {
            int n = s.Length;
            int m = t.Length;
            int[,] d = new int[n + 1, m + 1];

            if (n == 0) return m;
            if (m == 0) return n;

            for (int i = 0; i <= n; d[i, 0] = i++) ;
            for (int j = 0; j <= m; d[0, j] = j++) ;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(
                        Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                        d[i - 1, j - 1] + cost);
                }
            }
            return d[n, m];
        }
    }
}
