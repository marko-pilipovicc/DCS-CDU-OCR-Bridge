using DCS.OCR.Library.Models;
using DCS.OCR.Library.Services;
using Newtonsoft.Json;
using NLog;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;

namespace WWCduDcsBiosBridge.Aircrafts;

internal class C130J_OcrFlow : IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ScreenCaptureService _captureService;
    private readonly CorrectionService _correctionService;
    private readonly CustomOcrService _customOcrService;
    private readonly RapidOcrService _rapidOcrService;
    private readonly StabilityFilter _stabilityFilter;
    private readonly DcsIndicationListener _indicationListener;
    private readonly IndicationParser _indicationParser;
    
    private Profile? _currentProfile;
    private bool _isRunning;
    private string? _lastIndication;
    private CancellationTokenSource? _refinementCts;

    public event EventHandler<OcrResult>? FrameProcessed;

    public C130J_OcrFlow()
    {
        _captureService = new ScreenCaptureService();
        _rapidOcrService = new RapidOcrService();
        _customOcrService = new CustomOcrService();
        _correctionService = new CorrectionService();
        _stabilityFilter = new StabilityFilter(1); // Immediate update for triggered flow
        _indicationListener = new DcsIndicationListener(4242);
        _indicationParser = new IndicationParser();

        _indicationListener.IndicationReceived += (msg) => ProcessIndication(msg);

        LoadProfileAndModels();
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
                Logger.Info($"C-130J Profile loaded from {profilePath}");
            }
            else
            {
                Logger.Error($"C-130J Profile NOT found at {profilePath}");
            }

            var customModelPath = Path.Combine(baseDir, "Config", "OCR", "models", "custom", "model.onnx");
            var alphabetPath = Path.Combine(baseDir, "Config", "OCR", "models", "custom", "alphabet.txt");

            if (File.Exists(customModelPath))
            {
                _customOcrService.LoadModel(customModelPath, alphabetPath);
                Logger.Info("C-130J Custom OCR Model loaded");
            }
            else
            {
                Logger.Warn("C-130J Custom OCR Model NOT found");
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to load C-130J OCR profile/models");
        }
    }

    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _indicationListener.Start();
        Logger.Info("C-130J OCR Flow started (Triggered by Indications)");
    }

    public void Stop()
    {
        _isRunning = false;
        _indicationListener.Stop();
        Logger.Info("C-130J OCR Flow stopped");
    }

    private void ProcessIndication(string text)
    {
        if (!_isRunning || _currentProfile == null || !_customOcrService.IsLoaded) return;

        // Skip if indication is exactly the same as last time to reduce processing
        if (text == _lastIndication) return;
        _lastIndication = text;

        // Cancel any pending refinement as we have a new fresh indication
        _refinementCts?.Cancel();
        _refinementCts = new CancellationTokenSource();

        ExecuteOcr(text);

        // Schedule a refinement OCR 150ms later to catch late DCS UI rendering
        var token = _refinementCts.Token;
        Task.Delay(150, token).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !token.IsCancellationRequested)
            {
                ExecuteOcr(text);
            }
        }, token);
    }

    private void ExecuteOcr(string text)
    {
        if (!_isRunning || _currentProfile == null) return;

        try
        {
            var swTotal = Stopwatch.StartNew();
            var sw = Stopwatch.StartNew();

            using var frame = _captureService.CaptureRegion(
                _currentProfile.ViewportCrop.X,
                _currentProfile.ViewportCrop.Y,
                _currentProfile.ViewportCrop.W,
                _currentProfile.ViewportCrop.H
            );
            var captureMs = sw.ElapsedMilliseconds;
            sw.Restart();

            if (frame != null && !frame.Empty())
            {
                var grid = _customOcrService.ProcessFrame(frame, _currentProfile, _rapidOcrService);
                var ocrMs = sw.ElapsedMilliseconds;
                sw.Restart();

                // Use IndicationParser to get reference grid and DCS values for correction
                var refGrid = _indicationParser.Parse(text, _currentProfile);
                var dcsValues = _indicationParser.ExtractValues(text);

                var result = _correctionService.Correct(grid, refGrid, dcsValues, _currentProfile.ContextRules);
                var correctionMs = sw.ElapsedMilliseconds;
                sw.Restart();

                var stable = _stabilityFilter.Filter(result);
                var stabilityMs = sw.ElapsedMilliseconds;

                stable.CaptureTimeMs = captureMs;
                stable.OcrTimeMs = ocrMs;
                stable.CorrectionTimeMs = correctionMs;
                stable.StabilityTimeMs = stabilityMs;
                stable.TotalProcessingTimeMs = swTotal.ElapsedMilliseconds;

                FrameProcessed?.Invoke(this, stable);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error in C-130J OCR Execution");
        }
    }

    public void Dispose()
    {
        Stop();
        _indicationListener.Dispose();
        _customOcrService.Dispose();
        _rapidOcrService.Dispose();
        _refinementCts?.Dispose();
    }
}
