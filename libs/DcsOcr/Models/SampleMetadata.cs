using System;

namespace DCS.OCR.Library.Models
{
    public class SampleMetadata
    {
        public DateTime Timestamp { get; set; }
        public string Aircraft { get; set; }
        public string ProfileId { get; set; }
        public string DisplayId { get; set; }
        public double SSIM { get; set; }
    }
}
