using L = NimCap.Screenshot.Localization.UiText;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NimCap.Screenshot.Models;
using SkiaSharp;
using System.Windows.Media.Imaging;

namespace NimCap.Screenshot.Services;

/// <summary>Bounded, serial CPU/DirectML inference. Models are always local.</summary>
public sealed partial class OnnxImageRuntime : IDisposable
{
    public static OnnxImageRuntime Shared { get; } = new();
    private readonly SemaphoreSlim _gate = new(1);
    private readonly Dictionary<string, InferenceSession> _sessions = [];
    private readonly SkiaImageService _images = new();
    public bool GpuDisabled { get; private set; }
    public string? GpuMemoryStatus { get; private set; }
    private bool _disposed;

    private void ClearSessions()
    {
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
    }

    public async Task<T> Execute<T>(AppSettings settings, Func<ModelAccess, CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        return await Execute(settings, settings.OnnxDevice, settings.OnnxDeviceId, operation, cancellationToken).ConfigureAwait(false);
    }
    public static void RequireFile(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException(L.Format("Missing0OpenSettingsOCRAndRecognition", label));
        if (new FileInfo(path).Length > 2_000_000_000) throw new ArgumentException(L.Format("0IsTooLarge", label));
    }
    public sealed class ModelAccess
    {
        private readonly OnnxImageRuntime _owner; private readonly int _threads;
        private readonly string _device; private readonly int _deviceId;
        private readonly HashSet<string> _opened = [];
        internal ModelAccess(OnnxImageRuntime owner, int threads, string device, int deviceId)
        { _owner = owner; _threads = threads; _device = device; _deviceId = deviceId; }
        public string DeviceLabel => _device == "DirectML" ? L.Format("DirectMLGPU0UnsupportedOperatorsMayRun", _deviceId) : "CPU";
        public InferenceSession Open(string path, string label, bool imageInput = true)
        {
            RequireFile(path, label);
            var key = Path.GetFullPath(path) + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + _threads + "|" + _device + "|" + _deviceId;
            _opened.Add(key);
            if (_owner._sessions.TryGetValue(key, out var cached)) return cached;
            if (_owner._sessions.Count >= 4)
            {
                var oldest = _owner._sessions.FirstOrDefault(x => !_opened.Contains(x.Key));
                if (oldest.Key is null) throw new InvalidOperationException(L.Get("AtMostFourModelSessionsPerInference"));
                oldest.Value.Dispose(); _owner._sessions.Remove(oldest.Key);
            }
            using var options = new SessionOptions { IntraOpNumThreads = _threads, InterOpNumThreads = 1, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            if (_device == "DirectML")
            {
                var memory = GpuMemoryBudget.Query(_deviceId);
                _owner.GpuMemoryStatus = memory?.ToString();
                if (memory is { } usage && usage.Budget > 0 && usage.Usage >= usage.Budget * .9)
                    throw new GpuBudgetException();
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                try { options.AppendExecutionProvider_DML(_deviceId); }
                catch (Exception ex) { throw new GpuInitializationException(ex); }
            }
            else if (_device != "CPU") throw new ArgumentException(L.Get("UnknownONNXDevice"));
            var session = new InferenceSession(path, options);
            if (imageInput && (session.InputMetadata.Count != 1 || session.InputMetadata.First().Value.ElementType != typeof(float)))
            { session.Dispose(); throw new NotSupportedException(L.Get("ThisModelRequiresOneFloat32NCHWImage")); }
            _owner._sessions.Add(key, session);
            if (_device == "DirectML")
            {
                var memory = GpuMemoryBudget.Query(_deviceId);
                _owner.GpuMemoryStatus = memory?.ToString();
                if (memory is { } usage && usage.Budget > 0 && usage.Usage >= usage.Budget * .9)
                    throw new GpuBudgetException();
            }
            return session;
        }
        public static (int Height, int Width) Size(InferenceSession session, int height, int width)
        {
            var shape = session.InputMetadata.First().Value.Dimensions;
            if (shape.Length != 4 || shape[0] > 1 || shape[1] is not (3 or -1)) throw new NotSupportedException(L.Get("ModelInputMustBeA13"));
            height = shape[2] > 0 ? shape[2] : height; width = shape[3] > 0 ? shape[3] : width;
            if (height is < 16 or > 2048 || width is < 16 or > 2048) throw new NotSupportedException(L.Get("ModelInputDimensionsMustBeWithin16"));
            return (height, width);
        }
        public static (float[] Data, int[] Shape) Run(InferenceSession session, DenseTensor<float> tensor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var options = new RunOptions();
            using var registration = token.Register(() => options.Terminate = true);
            using var result = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.First().Key, tensor)], session.OutputMetadata.Keys.ToArray(), options);
            token.ThrowIfCancellationRequested();
            var value = result.First().AsTensor<float>();
            return (value.ToArray(), value.Dimensions.ToArray());
        }
        public static Dictionary<string, (float[] Data, int[] Shape)> RunAll(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var options = new RunOptions();
            using var registration = token.Register(() => options.Terminate = true);
            using var results = session.Run(inputs, session.OutputMetadata.Keys.ToArray(), options);
            token.ThrowIfCancellationRequested();
            return results.ToDictionary(x => x.Name, x => (x.AsTensor<float>().ToArray(), x.AsTensor<float>().Dimensions.ToArray()));
        }
    }

    public static DenseTensor<float> Tensor(SKBitmap bitmap, int height, int width, string normalization, bool padWidth = false)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 3, height, width });
        var drawWidth = padWidth ? Math.Min(width, Math.Max(1, (int)Math.Ceiling(bitmap.Width * (double)height / bitmap.Height))) : width;
        using var resized = bitmap.Resize(new SKImageInfo(drawWidth, height, SKColorType.Bgra8888, SKAlphaType.Premul), new SKSamplingOptions(SKFilterMode.Linear))
            ?? throw new InvalidOperationException(L.Get("ImagePreprocessingFailed"));
        var mean = normalization == "ImageNet" ? new[] { .485f, .456f, .406f } : new[] { .5f, .5f, .5f };
        var std = normalization == "ImageNet" ? new[] { .229f, .224f, .225f } : normalization == "MinusOne" ? new[] { .5f, .5f, .5f } : new[] { 1f, 1f, 1f };
        for (var y = 0; y < height; y++) for (var x = 0; x < drawWidth; x++)
        {
            var pixel = resized.GetPixel(x, y);
            // Composite transparent source pixels on white before OCR/model normalization.
            var alpha = pixel.Alpha / 255f;
            tensor[0, 0, y, x] = ((pixel.Red / 255f * alpha + 1 - alpha) - mean[0]) / std[0];
            tensor[0, 1, y, x] = ((pixel.Green / 255f * alpha + 1 - alpha) - mean[1]) / std[1];
            tensor[0, 2, y, x] = ((pixel.Blue / 255f * alpha + 1 - alpha) - mean[2]) / std[2];
        }
        return tensor;
    }

    public async Task<BitmapSource> RemoveBackground(BitmapSource image, AppSettings settings, CancellationToken token = default)
    {
        if (!OnnxWorker.IsWorker)
        {
            var response = await OnnxWorker.Execute("cutout", image, settings, token).ConfigureAwait(false);
            return OnnxWorker.Decode(response.Image ?? throw new InvalidDataException("Missing cutout result."));
        }
        var device = settings.CutoutOnnxDevice is "CPU" or "DirectML" ? settings.CutoutOnnxDevice : settings.OnnxDevice;
        var deviceId = settings.CutoutOnnxDeviceId >= 0 ? settings.CutoutOnnxDeviceId : settings.OnnxDeviceId;
        return await Execute(settings, device, deviceId, (models, cancellation) =>
    {
        ValidateImage(image);
        if (settings.CutoutProfile is not ("U2Net" or "ISNet" or "BiRefNet" or "BiRefNetMatting" or "BiRefNetAlpha"))
            throw new NotSupportedException(L.Get("UnknownBackgroundRemovalModelContractReconfigureIt"));
        var session = models.Open(settings.CutoutModel, L.Get("BackgroundRemovalONNXModel"));
        var size = ModelAccess.Size(session, Math.Clamp(settings.CutoutInputSize, 32, 2048), Math.Clamp(settings.CutoutInputSize, 32, 2048));
        using var source = _images.ToSkBitmap(image);
        var output = ModelAccess.Run(session, Tensor(source, size.Height, size.Width, settings.CutoutProfile == "ISNet" ? "Half" : "ImageNet"), cancellation);
        var shape = output.Shape;
        if (shape.Length < 2 || shape.Length > 4 || shape.Take(shape.Length - 2).Any(x => x != 1)) throw new NotSupportedException(L.Get("BackgroundRemovalOutputMustBeASingle"));
        var height = shape[^2]; var width = shape[^1];
        if (height < 1 || width < 1 || (long)height * width > 16_000_000 || output.Data.Length != height * width) throw new InvalidDataException(L.Get("InvalidBackgroundRemovalMaskDimensions"));
        var minimum = output.Data.Min(); var maximum = output.Data.Max();
        if (!float.IsFinite(minimum) || !float.IsFinite(maximum)) throw new InvalidDataException(L.Get("BackgroundRemovalModelOutputContainsInvalidValues"));
        static float Sigmoid(float value) => 1f / (1f + MathF.Exp(-value));
        var probabilityMinimum = Sigmoid(minimum); var probabilityMaximum = Sigmoid(maximum);
        using var mask = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var value = output.Data[y * width + x];
            if (settings.CutoutProfile is "BiRefNet" or "BiRefNetMatting")
            {
                value = Sigmoid(value);
                // rembg general/portrait exports use min-max after sigmoid; matting preserves soft alpha.
                if (settings.CutoutProfile == "BiRefNet" && probabilityMaximum - probabilityMinimum > .00001f)
                    value = (value - probabilityMinimum) / (probabilityMaximum - probabilityMinimum);
            }
            else if (settings.CutoutProfile == "U2Net" && maximum - minimum > .00001) value = (value - minimum) / (maximum - minimum);
            // BiRefNetAlpha exports already include sigmoid: clamp only, never stretch soft alpha.
            var level = (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255); mask.SetPixel(x, y, new SKColor(level, level, level));
        }
        using var fullMask = mask.Resize(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear));
        using var result = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        for (var y = 0; y < source.Height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (var x = 0; x < source.Width; x++)
            { var pixel = source.GetPixel(x, y); var alpha = (byte)((pixel.Alpha * fullMask.GetPixel(x, y).Red + 127) / 255); result.SetPixel(x, y, pixel.WithAlpha(alpha)); }
        }
        return _images.ToBitmapSource(result);
        }, token).ConfigureAwait(false);
    }

    private async Task<T> Execute<T>(AppSettings settings, string device, int deviceId,
        Func<ModelAccess, CancellationToken, T> operation, CancellationToken cancellationToken, int? timeoutSeconds = null)
    {
        if (!OnnxWorker.IsWorker) throw new InvalidOperationException("ONNX sessions must run in an isolated worker.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? Math.Clamp(settings.RecognitionTimeoutSeconds, 1, 120)));
        await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var effectiveDevice = GpuDisabled ? "CPU" : device;
            try
            {
                return await Task.Run(() => operation(new ModelAccess(this, Math.Clamp(settings.RecognitionThreads, 1, 8), effectiveDevice, deviceId), timeout.Token), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (effectiveDevice == "DirectML" && !timeout.IsCancellationRequested &&
                ex is OnnxRuntimeException or GpuInitializationException or GpuBudgetException)
            {
                GpuDisabled = true;
                ClearSessions(); // Never reuse sessions after a provider failure.
                return await Task.Run(() => operation(new ModelAccess(this, Math.Clamp(settings.RecognitionThreads, 1, 8), "CPU", deviceId), timeout.Token), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OnnxRuntimeException) when (timeout.IsCancellationRequested)
        { ClearSessions(); throw new OperationCanceledException(L.Get("LocalInferenceCanceledOrTimedOut"), timeout.Token); }
        catch { ClearSessions(); throw; }
        finally { _gate.Release(); }
    }

    private sealed class GpuInitializationException(Exception inner) : Exception("GPU initialization failed.", inner);
    private sealed class GpuBudgetException : Exception;

    public const long MaximumPixels = 16_000_000;

    public static void ValidateImage(BitmapSource image)
    {
        if (image.PixelWidth < 1 || image.PixelHeight < 1 || (long)image.PixelWidth * image.PixelHeight > MaximumPixels)
            throw new ArgumentException(L.Get("RecognitionBackgroundRemovalInputIsLimitedTo"));
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (!_disposed) { _disposed = true; ClearSessions(); } }
        finally { _gate.Release(); }
    }
}
