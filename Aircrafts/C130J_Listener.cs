using DCS_BIOS.EventArgs;
using DCS.OCR.Library.Models;
using NLog;
using WwDevicesDotNet;

namespace WWCduDcsBiosBridge.Aircrafts;

internal class C130J_Listener : AircraftListener
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly C130J_OcrFlow _ocrFlow;

    public C130J_Listener(ICdu? mcdu, UserOptions options, IFrontpanel? frontpanel = null)
        : base(mcdu, SupportedAircrafts.C130J, options, frontpanel)
    {
        _ocrFlow = new C130J_OcrFlow();
        _ocrFlow.FrameProcessed += (s, result) => UpdateDisplay(result);
    }

    protected override string GetAircraftName()
    {
        return SupportedAircrafts.C130J_Name;
    }

    protected override string GetFontFile()
    {
        return "resources/c130j-font-21x31.json";
        // Placeholder font
    }

    protected override void InitializeDcsBiosControls()
    {
    }

    public override void Start()
    {
        base.Start();
        _ocrFlow.Start();
    }

    public override void Stop()
    {
        _ocrFlow.Stop();
        base.Stop();
    }

    private void UpdateDisplay(OcrResult result)
    {
        var output = GetCompositor(DEFAULT_PAGE);
        for (var i = 0; i < result.Lines.Length && i < 14; i++)
        {
            var line = result.Lines[i];
            var format = result.LinesFormat[i];
            var size = result.LinesSize[i];
            
            // Apply clipping if necessary
            if (line.Length > 2) 
            {
                line = line.Substring(1, line.Length - 2);
                if (format.Length >= line.Length + 2)
                    format = format.Substring(1, line.Length);
            }

            var lineObj = output.Line(i).Green();
            
            // Apply inversion character by character
            for (int c = 0; c < line.Length; c++)
            {
                bool isInverted = c < format.Length && format[c] == 'I';
                if (isInverted) lineObj.InvertColors();
                if (size.Contains('S')) lineObj.Small();
                
                lineObj.Write(line[c]);

                if (size.Contains('S')) lineObj.Large();
                if (isInverted) lineObj.InvertColors();
            }
        }
    }

    public override void DcsBiosDataReceived(object sender, DCSBIOSDataEventArgs e)
    {
    }

    public override void DCSBIOSStringReceived(object sender, DCSBIOSStringDataEventArgs e)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ocrFlow.Dispose();
        }

        base.Dispose(disposing);
    }
}