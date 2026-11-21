using NimCap.Screenshot.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NimCap.Screenshot.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsFile;
    private readonly string _executableDirectory;

    public SettingsService() : this(AppPaths.SettingsFile) { }
    public SettingsService(string settingsFile, string? executableDirectory = null)
    {
        _settingsFile = Path.GetFullPath(settingsFile);
        _executableDirectory = Path.GetFullPath(executableDirectory ?? AppPaths.ContentRoot);
    }

    public string ResolveModelPath(string? path) => ModelPathService.Resolve(path, _executableDirectory);
    public string StoreModelPath(string? path) => ModelPathService.ForStorage(path, _executableDirectory);

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        if (!File.Exists(_settingsFile)) return;
        try
        {
            Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsFile), JsonOptions) ?? new();
            Normalize();
        }
        catch
        {
            Current = new AppSettings();
        }
    }

    public void Save()
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFile)!);
        var temporary = _settingsFile + ".tmp";
        var snapshot = JsonSerializer.SerializeToNode(Current, JsonOptions)!.AsObject();
        ModelPathService.Transform(snapshot, StoreModelPath);
        File.WriteAllText(temporary, snapshot.ToJsonString(JsonOptions));
        File.Move(temporary, _settingsFile, true);
    }

    public void Update(AppSettings candidate)
    {
        var previous = Current;
        Current = candidate;
        try { Save(); }
        catch { Current = previous; throw; }
    }

    private void Normalize()
    {
        Current.ActionHotkeys ??= new();
        Current.TrayActions ??= new();
        Current.WatermarkModels ??= new();
        if (!WatermarkModels.All.Any(x => x.Id == Current.WatermarkDefaultModelId)) Current.WatermarkDefaultModelId = "lama";
        if (Current.WatermarkDevice is not ("CPU" or "DirectML")) Current.WatermarkDevice = "CPU";
        Current.WatermarkDetectionThreshold = double.IsFinite(Current.WatermarkDetectionThreshold) ? Math.Clamp(Current.WatermarkDetectionThreshold, .05, .95) : .5;
        Current.Recording ??= new();
        Current.Recording.SpeakerDeviceId ??= "";
        Current.Recording.MicrophoneDeviceId ??= "";
        Current.Recording.CameraDeviceId ??= "";
        if (string.IsNullOrWhiteSpace(Current.RecordingSaveDirectory))
            Current.RecordingSaveDirectory = AppSettings.DefaultRecordingSaveDirectory;
        try { Current.Recording.Validate(); } catch (ArgumentException) { Current.Recording = new(); }
        Current.ImageQuality = Math.Clamp(Current.ImageQuality, 1, 100);
        if (Current.ScrollingCaptureBackend is not ("Auto" or "WGC" or "DXGI" or "GDI")) Current.ScrollingCaptureBackend = "Auto";
        if (Current.OcrBackend is not ("PaddleOCR" or "Windows" or "Tesseract")) Current.OcrBackend = "PaddleOCR";
        if (Current.OcrModelPackage is not ("tiny" or "small" or "medium")) Current.OcrModelPackage = "small";
        Current.TesseractDataDirectory ??= "";
        if (string.IsNullOrWhiteSpace(Current.TesseractLanguages)) Current.TesseractLanguages = "chi_sim+eng";
        if (Current.TranslationProvider is not ("MyMemory" or "Google" or "LibreTranslate" or "Local"))
        {
            Current.TranslationProvider = "LibreTranslate";
            Current.AllowOnlineTranslation = false;
        }
        if (Current.TranslationApiProvider is not ("MyMemory" or "Google" or "LibreTranslate"))
            Current.TranslationApiProvider = Current.TranslationProvider is "MyMemory" or "Google" or "LibreTranslate" ? Current.TranslationProvider : "MyMemory";
        Current.OcrMaximumEdge = Math.Clamp(Current.OcrMaximumEdge, 320, 2048);
        Current.TableStructureModel ??= "";
        Current.FormulaModelDirectory ??= "";
        if (Current.OnnxDevice is not ("CPU" or "DirectML")) Current.OnnxDevice = "CPU";
        Current.OnnxDeviceId = Math.Clamp(Current.OnnxDeviceId, 0, 15);
        if (!ModelDeviceSupport.OcrSupports(Current.OcrModelPackage, Current.OnnxDevice)) Current.OcrModelPackage = "small";
        if (Current.CutoutOnnxDevice is not ("CPU" or "DirectML")) Current.CutoutOnnxDevice = Current.OnnxDevice;
        if (Current.CutoutOnnxDeviceId < 0) Current.CutoutOnnxDeviceId = Current.OnnxDeviceId;
        Current.CutoutOnnxDeviceId = Math.Clamp(Current.CutoutOnnxDeviceId, 0, 15);
        if (Current.LocalTranslationDevice is not ("CPU" or "Vulkan")) Current.LocalTranslationDevice = "CPU";
        Current.LocalTranslationModel ??= "";
        Current.LocalTranslationRuntime ??= "";
        Current.RecognitionThreads = Math.Clamp(Current.RecognitionThreads, 1, 8);
        Current.RecognitionTimeoutSeconds = Math.Clamp(Current.RecognitionTimeoutSeconds, 1, 120);
        Current.CutoutInputSize = Math.Clamp(Current.CutoutInputSize, 32, 2048);
        CutoutModels.Migrate(Current);
        var cutoutModels = CutoutModels.ForDevice(Current.CutoutOnnxDevice);
        if (!cutoutModels.Any(model => model.Id == Current.CutoutDefaultModelId)) Current.CutoutDefaultModelId = cutoutModels[0].Id;
        ModelPathService.Resolve(Current, _executableDirectory);
        if (Current.CutoutProfile is not ("U2Net" or "ISNet" or "BiRefNet" or "BiRefNetMatting" or "BiRefNetAlpha")) Current.CutoutProfile = "U2Net";
        if (string.IsNullOrWhiteSpace(Current.FileNameTemplate)) Current.FileNameTemplate = OutputFileService.DefaultTemplate;
        var extension = Current.OutputFormat ?? "";
        Current.OutputFormat = OutputFormat.IsSupported(extension) ? OutputFormat.NormalizeExtension(extension) : ".png";
        Current.HistoryLimit = Math.Clamp(Current.HistoryLimit, 0, 1000);
        Current.DefaultPinOpacity = Math.Clamp(Current.DefaultPinOpacity, 0.2, 1.0);
        Current.PinSnapDistance = double.IsFinite(Current.PinSnapDistance) ? Math.Clamp(Current.PinSnapDistance, 0, 48) : 12;
        Current.TextPinFontSize = double.IsFinite(Current.TextPinFontSize) ? Math.Clamp(Current.TextPinFontSize, 10, 64) : 20;
        Current.TextPinWidth = Math.Clamp(Current.TextPinWidth, 200, 1200);
        if (string.IsNullOrWhiteSpace(Current.TextPinFontFamily)) Current.TextPinFontFamily = "Microsoft YaHei UI";
        if (string.IsNullOrWhiteSpace(Current.SaveDirectory))
            Current.SaveDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
    }
}
