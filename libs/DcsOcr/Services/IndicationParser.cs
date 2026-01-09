using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DCS.OCR.Library.Models;

namespace DCS.OCR.Library.Services
{
    public class IndicationParser
    {
        public char[][] Parse(string text, Profile profile)
        {
            var grid = new char[profile.Rows][];
            for (int i = 0; i < profile.Rows; i++)
            {
                grid[i] = new char[profile.Cols];
                for (int j = 0; j < profile.Cols; j++) grid[i][j] = '\0';
            }

            var data = new Dictionary<int, string>();
            var nameData = new Dictionary<string, string>();

            // Try generic parsing [id] = "value"
            var matches = Regex.Matches(text, @"\[(\d+)\]\s*=\s*""([^""]*)""");
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups[1].Value, out int id))
                    data[id] = match.Groups[2].Value;
            }

            // Fallback: dash-separated format
            if (data.Count == 0)
            {
                var blocks = Regex.Split(text, @"^-{10,}$", RegexOptions.Multiline);
                foreach (var block in blocks)
                {
                    var lines = block.Trim().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length >= 1)
                    {
                        string key = lines[0].Trim();
                        string value = lines.Length >= 2 ? lines[1].Trim() : "";

                        // Try to extract ID if key is like "[82]"
                        var idMatch = Regex.Match(key, @"\[(\d+)\]");
                        if (idMatch.Success && int.TryParse(idMatch.Groups[1].Value, out int id))
                        {
                            data[id] = value;
                        }
                        else
                        {
                            nameData[key] = value;
                        }
                    }
                }
            }

            if (profile.FieldMap != null)
            {
                foreach (var mapping in profile.FieldMap)
                {
                    string? val = null;
                    if (data.TryGetValue(mapping.Id, out var v))
                    {
                        val = v;
                    }
                    else if (!string.IsNullOrEmpty(mapping.Name))
                    {
                        // Try matching by name (fuzzy)
                        foreach (var kvp in nameData)
                        {
                            if (kvp.Key.IndexOf(mapping.Name, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                mapping.Name.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                val = kvp.Value;
                                break;
                            }
                        }
                    }

                    if (val != null)
                    {
                        for (int i = 0; i < val.Length; i++)
                        {
                            if (mapping.Row < profile.Rows && mapping.Col + i < profile.Cols)
                            {
                                grid[mapping.Row][mapping.Col + i] = val[i];
                            }
                        }
                    }
                }
            }

            return grid;
        }

        public List<string> ExtractValues(string text)
        {
            var values = new HashSet<string>();

            // Generic format
            var matches = Regex.Matches(text, @"\[(\d+)\]\s*=\s*""([^""]*)""");
            foreach (Match match in matches)
            {
                var val = match.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(val) && !IsGuid(val)) values.Add(val);
            }

            // Dash format
            var blocks = Regex.Split(text, @"^-{10,}$", RegexOptions.Multiline);
            foreach (var block in blocks)
            {
                var lines = block.Trim().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length >= 2)
                {
                    string value = lines[1].Trim();
                    if (!string.IsNullOrWhiteSpace(value) && !IsGuid(value))
                    {
                        values.Add(value);
                    }
                }
                else if (lines.Length == 1 && !lines[0].Contains("{") && !lines[0].Contains("["))
                {
                    // Sometimes the key IS the value if it's just a static label exported
                    // But be careful not to include GUIDs
                    string value = lines[0].Trim();
                    if (!string.IsNullOrWhiteSpace(value) && !IsGuid(value))
                    {
                        values.Add(value);
                    }
                }
            }
            return values.ToList();
        }

        private bool IsGuid(string text)
        {
            return Regex.IsMatch(text, @"^\{?[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}?$");
        }
    }
}
