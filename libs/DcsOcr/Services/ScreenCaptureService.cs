using System;
using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace DCS.OCR.Library.Services
{
    public class ScreenCaptureService
    {
        public Mat CaptureRegion(int x, int y, int width, int height)
        {
            using (var bitmap = new Bitmap(width, height))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    // Direct screen copy without rotation or transformation.
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                }
                return bitmap.ToMat();
            }
        }
    }
}
