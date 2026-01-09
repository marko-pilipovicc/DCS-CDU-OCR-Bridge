using System;
using System.Linq;

namespace DCS.OCR.Library.Services
{
    public class StabilityFilter
    {
        private string[]? _lastStableLines;
        private string[]? _pendingLines;
        private int _consecutiveCount = 0;
        private readonly int _requiredFrames;

        public StabilityFilter(int requiredFrames = 2)
        {
            _requiredFrames = requiredFrames;
        }

        public string[] Filter(string[] newLines)
        {
            if (_lastStableLines == null)
            {
                _lastStableLines = newLines;
                _pendingLines = newLines;
                _consecutiveCount = 1;
                return newLines;
            }

            // Heuristic: If the change is massive (e.g. > 50% of lines changed),
            // it's likely a page turn. Bypass stability to reduce perceived lag.
            int changedLines = 0;
            int maxLines = Math.Max(newLines.Length, _lastStableLines.Length);
            for (int i = 0; i < Math.Min(newLines.Length, _lastStableLines.Length); i++)
            {
                if (newLines[i] != _lastStableLines[i]) changedLines++;
            }
            changedLines += Math.Abs(newLines.Length - _lastStableLines.Length);

            bool isMajorChange = maxLines > 0 && (double)changedLines / maxLines > 0.5;

            if (newLines.SequenceEqual(_pendingLines ?? Array.Empty<string>()))
            {
                _consecutiveCount++;
            }
            else
            {
                _pendingLines = newLines;
                _consecutiveCount = 1;
            }

            if (_consecutiveCount >= _requiredFrames || isMajorChange)
            {
                _lastStableLines = _pendingLines;
            }

            return _lastStableLines!;
        }
    }
}
