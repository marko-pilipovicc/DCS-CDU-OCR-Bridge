using System.IO;
using DCS_BIOS.EventArgs;
using DCS.OCR.Library.Models;
using DCS.OCR.Library.Services;
using Newtonsoft.Json;
using NLog;
using WwDevicesDotNet;

namespace WWCduDcsBiosBridge.Aircrafts;

internal class C130J_Listener : AircraftListener
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    
    private readonly ScreenCaptureService _captureService;
    private readonly CorrectionService _correctionService;
    private readonly CustomOcrService _customOcrService;
    private readonly RapidOcrService _rapidOcrService;
    private readonly StabilityFilter _stabilityFilter;
    private CancellationTokenSource? _cts;

    private Profile? _currentProfile;
    private bool _isOcrRunning;
    private Task? _ocrTask;

    public C130J_Listener(ICdu? mcdu, UserOptions options)
        : base(mcdu, SupportedAircrafts.C130J, options)
    {
        _captureService = new ScreenCaptureService();
        _rapidOcrService = new RapidOcrService();
        _customOcrService = new CustomOcrService();
        _correctionService = new CorrectionService();
        _stabilityFilter = new StabilityFilter();

        LoadProfileAndModels();
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

    private void LoadProfileAndModels()
    {
        try
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var profilePath = Path.Combine(baseDir, "Config", "OCR", "profiles", "C-130J", "PILOT_CNI.json");

            if (File.Exists(profilePath))
            {
                _currentProfile = JsonConvert.DeserializeObject<Profile>(File.ReadAllText(profilePath));
                App.Logger.Info($"C-130J Profile loaded from {profilePath}");
            }
            else
            {
                App.Logger.Error($"C-130J Profile NOT found at {profilePath}");
            }

            var customModelPath = Path.Combine(baseDir, "Config", "OCR", "models", "custom", "model.onnx");
            var alphabetPath = Path.Combine(baseDir, "Config", "OCR", "models", "custom", "alphabet.txt");

            if (File.Exists(customModelPath))
            {
                _customOcrService.LoadModel(customModelPath, alphabetPath);
                App.Logger.Info("C-130J Custom OCR Model loaded");
            }
            else
            {
                App.Logger.Warn("C-130J Custom OCR Model NOT found");
            }
        }
        catch (Exception ex)
        {
            App.Logger.Error(ex, "Failed to load C-130J OCR profile/models");
        }
    }

    protected override void InitializeDcsBiosControls()
    {
        // No specific DCS-BIOS controls for C-130J in this implementation
    }

    public override void Start()
    {
        base.Start();
        StartOcrLoop();
    }

    public override void Stop()
    {
        StopOcrLoop();
        base.Stop();
    }

    private void StartOcrLoop()
    {
        if (_isOcrRunning) return;

        _isOcrRunning = true;
        _cts = new CancellationTokenSource();
        _ocrTask = Task.Run(() => OcrLoop(_cts.Token));
        App.Logger.Info("C-130J OCR Loop started");
    }

    private void StopOcrLoop()
    {
        _isOcrRunning = false;
        _cts?.Cancel();
        try
        {
            _ocrTask?.Wait(1000);
        }
        catch
        {
        }
    }

    private async Task OcrLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_currentProfile == null || !_customOcrService.IsLoaded)
            {
                await Task.Delay(1000, token);
                continue;
            }

            try
            {
                using var frame = _captureService.CaptureRegion(
                    _currentProfile.ViewportCrop.X,
                    _currentProfile.ViewportCrop.Y,
                    _currentProfile.ViewportCrop.W,
                    _currentProfile.ViewportCrop.H
                );

                if (frame != null && !frame.Empty())
                {
                    var grid = _customOcrService.ProcessFrame(frame, _currentProfile, _rapidOcrService);
                    var corrected = _correctionService.Correct(grid, null, null, _currentProfile.ContextRules);
                    var stable = _stabilityFilter.Filter(corrected);

                    UpdateDisplay(stable);
                }
            }
            catch (Exception ex)
            {
                App.Logger.Error(ex, "Error in C-130J OCR Loop");
            }

            await Task.Delay(200, token); // Run at ~5 FPS
        }
    }

    private void UpdateDisplay(OcrResult result)
    {
        var output = GetCompositor(DEFAULT_PAGE);
        for (var i = 0; i < result.Lines.Length && i < 14; i++)
        {
            var line = result.Lines[i];
            var format = result.LinesFormat[i];
            var size = result.LinesSize[i];
            
            // Apply clipping if necessary (same as before)
            if (line.Length > 2) 
            {
                line = line.Substring(1, line.Length - 2);
                if (format.Length >= line.Length + 2)
                    format = format.Substring(1, line.Length);
            }

            var lineObj = output.Line(i).Green();
            
            // Logger.Info($"Line {i}: {line}");
            // Logger.Info($"Format {i}: {format}");
            // Logger.Info($"Size {i}: {size}");
            
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
            StopOcrLoop();
            _customOcrService.Dispose();
            _rapidOcrService.Dispose();
            // _captureService.Dispose(); // ScreenCaptureService is not IDisposable
            _cts?.Dispose();
        }

        base.Dispose(disposing);
    }
}