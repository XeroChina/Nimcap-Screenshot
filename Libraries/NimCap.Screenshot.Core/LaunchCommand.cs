using L = NimCap.Screenshot.Localization.UiText;
using NimCap.Screenshot.Models;

namespace NimCap.Screenshot.Services;

public sealed record LaunchCommand(CaptureMode? Mode, int DelaySeconds, bool CopyToClipboard, IReadOnlyList<string>? ImagePaths = null)
{
    public static bool TryParse(IReadOnlyList<string> args, out LaunchCommand command, out string error)
    {
        CaptureMode? mode = null;
        var delay = 0;
        var copy = false;
        var images = new List<string>();
        error = string.Empty;

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "-region":
                case "--region":
                    if (!SetMode(CaptureMode.Region)) return Fail(out command, out error, L.Get("OnlyOneCaptureModeCanBeSpecified"));
                    break;
                case "-full":
                case "--full":
                    if (!SetMode(CaptureMode.FullScreen)) return Fail(out command, out error, L.Get("OnlyOneCaptureModeCanBeSpecified"));
                    break;
                case "-window":
                case "--window":
                    if (!SetMode(CaptureMode.ActiveWindow)) return Fail(out command, out error, L.Get("OnlyOneCaptureModeCanBeSpecified"));
                    break;
                case "-delay":
                case "--delay":
                    if (++i >= args.Count || !int.TryParse(args[i], out delay) || delay is < 1 or > 3600)
                        return Fail(out command, out error, L.Get("DelayRequiresANumberOfSecondsFrom"));
                    break;
                case "-clip":
                case "--clip":
                    copy = true;
                    break;
                default:
                    // A bare existing file path opens that image in the image tools.
                    if (File.Exists(args[i])) { images.Add(Path.GetFullPath(args[i])); break; }
                    return Fail(out command, out error, L.Format("UnknownCommandLineArgument0", args[i]));
            }
        }

        if (delay > 0 && mode is null) mode = CaptureMode.Region;
        command = new LaunchCommand(mode, delay, copy, images.Count > 0 ? images : null);
        return true;

        bool SetMode(CaptureMode value)
        {
            if (mode is not null && mode != value) return false;
            mode = value;
            return true;
        }

    }

    private static bool Fail(out LaunchCommand command, out string error, string message)
    {
        error = message;
        command = new LaunchCommand(null, 0, false);
        return false;
    }
}
