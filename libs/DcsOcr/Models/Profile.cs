using System.Collections.Generic;

namespace DCS.OCR.Library.Models
{
    public class ViewportCrop
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
    }

    public class Profile
    {
        public string ProfileId { get; set; }
        public ViewportCrop ViewportCrop { get; set; }
        public int Rows { get; set; }
        public int Cols { get; set; }
        public string Charset { get; set; }
        public List<CellBox>? Cells { get; set; }
        public List<FieldMapping>? FieldMap { get; set; }
        public List<int[]>? RowBands { get; set; } // Explicit [y1, y2] pairs per row
        public List<int>? RowCenters { get; set; } // Y coordinate of each row's center (for anchor-based dynamic segmentation)
        public int? DynamicRowYOffset { get; set; } // Max pixels to adjust row center up/down (default: 10)
        public int? DynamicRowHeightMin { get; set; } // Minimum row height in pixels (default: 32)
        public int? DynamicRowHeightMax { get; set; } // Maximum row height in pixels (default: 60)
        public int? DynamicRowGap { get; set; } // Minimum gap between rows in pixels (default: 2)
        public bool? UseDynamicRowSegmentation { get; set; }
        public bool? AutoAlign { get; set; } // Enable dynamic grid centering
        public bool? Invert { get; set; } // Invert binarization
        public bool? UseGlobalThreshold { get; set; } // Use global threshold instead of adaptive
        public int? BinaryThreshold { get; set; } // Global threshold value (0-255)
        public bool? GreenOnly { get; set; } // Only look at green channel
        public int? AlignmentPadding { get; set; } // Extra pixels to add to detected text bounds
        public float? Sharpness { get; set; } // Sharpness adjustment (0.0 to 2.0+)
        public float? Contrast { get; set; } // Contrast adjustment (0.5 to 3.0+)
        public int? Dilation { get; set; } // Dilation (0 to 3) to thicken text
        public int? MinCharDistance { get; set; } // Minimum distance between identical characters
        public List<ContextRule>? ContextRules { get; set; } // Confidence-based word overrides
    }

    public class ContextRule
    {
        public string Target { get; set; } // e.g. "INS"
        public List<string> Patterns { get; set; } // e.g. ["1NS", "lNS"]
        public float Threshold { get; set; } = 0.5f;
    }

    public class FieldMapping
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Row { get; set; }
        public int Col { get; set; }
    }

    public class CellBox
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public int Row { get; set; }
        public int Col { get; set; }
    }

    public class OcrCell
    {
        public char Char { get; set; }
        public float Confidence { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public int? W { get; set; }
        public int? H { get; set; }
        public bool IsInverted { get; set; } // True if character had white background (inverted)
    }
}
