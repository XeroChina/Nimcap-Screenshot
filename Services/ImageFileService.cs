using L = NimCap.Screenshot.Localization.UiText;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NimCap.Screenshot.Services;

/// <summary>Loads ordinary image files for the image tools without going through the clipboard.</summary>
internal static class ImageFileService
{
    internal static string Filter => L.Get("ImageFilesFilter");

    internal static BitmapSource Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException(L.Format("UnrecognizedImageFormatOrFileIs", path));
        BitmapSource frame = decoder.Frames[0];
        // Keep cutout, watermark and OCR inside the same pixel budget as a capture.
        var scale = ScaleFactor(frame.PixelWidth, frame.PixelHeight);
        if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
        if (!frame.IsFrozen) frame.Freeze();
        return frame;
    }

    internal static double ScaleFactor(long width, long height) =>
        (double)width * height > OnnxImageRuntime.MaximumPixels
            ? Math.Sqrt(OnnxImageRuntime.MaximumPixels / ((double)width * height))
            : 1;
}
