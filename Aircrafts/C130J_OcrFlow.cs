using DCS.OCR.Library.Models;
using DCS.OCR.Library.Services;
using Newtonsoft.Json;
using NLog;
using OpenCvSharp;
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
    
    private Profile? _currentProfile;
    private bool _isRunning;
    private Task? _flowTask;
    private CancellationTokenSource? _cts;

    public event EventHandler<OcrResult>? FrameProcessed;

    public C130J_OcrFlow()
    {
        _captureService = new ScreenCaptureService();
        _rapidOcrService = new RapidOcrService();
        _customOcrService = new CustomOcrService();
        _correctionService = new CorrectionService();
        _stabilityFilter = new StabilityFilter();

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
        _cts = new CancellationTokenSource();
        _flowTask = Task.Run(() => FlowLoop(_cts.Token));
        Logger.Info("C-130J OCR Flow started");
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
        try
        {
            _flowTask?.Wait(1000);
        }
        catch
        {
            // Ignored
        }
    }

    private async Task FlowLoop(CancellationToken token)
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

                    FrameProcessed?.Invoke(this, stable);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in C-130J OCR Flow Loop");
            }

            await Task.Delay(200, token); // ~5 FPS
        }
    }

    public void Dispose()
    {
        Stop();
        _customOcrService.Dispose();
        _rapidOcrService.Dispose();
        _cts?.Dispose();
    }
}
