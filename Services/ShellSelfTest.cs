using NimCap.Screenshot.Models;
using NimCap.Screenshot.Views;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NimCap.Screenshot.Services;

internal static class ShellSelfTest
{
    internal static string Run(string output)
    {
        var log = new StringBuilder();
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        Require(TrayService.RunPinMenuSelfTest(), "Tray commands or visibility regression.");
        Require(TrayService.RunLocalizationSelfTest(), "Tray localization regression.");
        Require(GlobalHotkeyHost.RunPinRoutingSelfTest(), "Global action routing regression.");
        log.AppendLine("PASS: one clipboard pin entry, every tray action routes to a shortcut action, hidden menu items retain shortcuts, Settings/Exit remain fixed, empty submenus hide, English/Chinese labels.");
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 130));
            dc.DrawText(new FormattedText("NimCap Screenshot Screen Text 12345", System.Globalization.CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface("Arial"), 30, Brushes.Black, 1), new Point(12, 35));
        }
        var sample = new RenderTargetBitmap(640, 130, 96, 96, PixelFormats.Pbgra32); sample.Render(visual); sample.Freeze();
        var defaults = new AppSettings();
        foreach (var value in new[] { "#abc", "#3B82F6", "#803B82F6", "rgb(12, 34, 56)", "rgba(12,34,56,0.5)", "0xFF0033" })
        {
            var pin = PinContentService.FromClipboard(sample, value, defaults);
            Require(pin.Color is not null && pin.Text is null && pin.Image.IsFrozen, "Clipboard color was treated as an image/text: " + value);
        }
        Require(PinContentService.FromClipboard(sample, "regular text", defaults).Image == sample, "Clipboard image priority failed.");
        Require(PinContentService.FromClipboard(null, "regular text", defaults).Text == "regular text", "Clipboard text pin failed.");
        foreach (var invalid in new[] { "rgb(256,0,0)", "rgba(1,2,3,1.2)", "rgb(1,2,3,0.5)", "rgba(1,2,3)", "123456" })
            Require(!PinContentService.TryParseColor(invalid, out _), "Invalid color accepted: " + invalid);
        log.AppendLine("PASS: clipboard HEX/RGB/RGBA/0x colors create color pins directly, mixed image/color content chooses the color, image/text fallbacks and invalid colors.");
        var snapshot = JsonSerializer.Serialize(defaults);
        var fallback = ScreenWordService.ResolveSettings(defaults, "en-US");
        Require(fallback.OcrBackend == "Windows" && fallback.WindowsOcrLanguage == "en-US" && JsonSerializer.Serialize(defaults) == snapshot, "Screen-word fallback changed saved OCR preferences.");
        try { ScreenWordService.ResolveSettings(defaults, null); throw new Exception("Missing OCR did not explain the problem."); }
        catch (InvalidOperationException) { }
        foreach (var scale in new[] { 1d, 1.5, 2d, 3d })
        {
            var work = new System.Drawing.Rectangle(-1920, -100, 1920, 1080);
            var roi = ScreenWordService.Region(new(-1000, 900), work);
            var popup = ScreenWordService.PopupRegion(roi, work, scale);
            Require(work.Contains(roi) && popup is { } bounds && work.Contains(bounds) && !bounds.IntersectsWith(roi), "Screen-word popup clips or covers its capture region.");
        }
        var frame = new DispatcherFrame(); Exception? failure = null;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                using var handler = new ReleaseHandler(); using var client = new HttpClient(handler);
                var updates = new AppUpdateSession();
                await Task.WhenAll(updates.CheckOnStartup(default, client), updates.CheckOnStartup(default, client));
                Require(handler.Requests == 1 && updates.Checked && updates.Release?.Tag == "v99.0.0", "Startup update checked more than once or missed release.");
                SettingsWindow.RunActionSettingsSelfTest(output, updates);
                Require(handler.Requests == 1, "Opening settings made an automatic update request.");
                await updates.CheckOnStartup(default, client); Require(handler.Requests == 1, "Startup check repeated during use.");
                await updates.Check(default, client); Require(handler.Requests == 2, "Explicit check no longer works.");
                using var errorClient = new HttpClient(new ReleaseHandler(HttpStatusCode.Forbidden));
                var failed = new AppUpdateSession(); await failed.CheckOnStartup(default, errorClient);
                Require(failed.Failed && failed.Checked && failed.Release is null, "Update failure reported as latest.");
                using var emptyClient = new HttpClient(new ReleaseHandler(HttpStatusCode.NotFound));
                var empty = new AppUpdateSession(); await empty.CheckOnStartup(default, emptyClient);
                Require(empty.Checked && !empty.Failed && empty.Release is null, "No-release response failed.");
                log.AppendLine("PASS: once-per-startup update request, concurrent deduplication, cached settings result, explicit recheck, network error/no-release distinction, no release-notes panel.");
                var language = ScreenWordService.WindowsLanguage("en-US");
                if (language is null) log.AppendLine("SKIP: real Windows OCR recognition (no installed Windows OCR language).");
                else
                {
                    var config = new AppSettings { WindowsOcrLanguage = language };
                    using var service = new ScreenWordService(() => config, () => false);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var result = await service.RecognizeImage(sample, timeout.Token);
                    Require(result.Text.Contains("12345") && result.Text.Contains("NimCap", StringComparison.OrdinalIgnoreCase), "Real screen-word OCR failed: " + result.Text);
                    Require(config.OcrBackend == "PaddleOCR", "Screen-word fallback persisted a backend change.");
                    File.WriteAllText(Path.Combine(output, "screen-word-result.txt"), result.Text);
                    log.AppendLine("PASS: real screen-word OCR with missing Paddle model uses installed Windows OCR without changing preferences: " + result.Text.Replace('\n', ' '));
                }
                log.AppendLine("PASS: screen-word missing-backend message, unchanged settings, popup placement on negative monitors at 100/150/200/300% DPI.");
            }
            catch (Exception ex) { failure = ex; }
            finally { frame.Continue = false; }
        }));
        Dispatcher.PushFrame(frame);
        if (failure is not null) throw new InvalidOperationException("Shell workflow regression.", failure);
        File.WriteAllBytes(Path.Combine(output, "screen-word-sample.png"), OnnxWorker.Encode(sample));
        return log.ToString();
    }

    private sealed class ReleaseHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                tag_name = "v99.0.0", draft = false, prerelease = false, body = "One-line notes should not create a panel.",
                html_url = "https://github.com/XeroChina/nimcap-screenshot/releases/tag/v99.0.0",
                assets = new[] { new { name = "nimcap-screenshot-v99.0.0-win-x64.zip", browser_download_url = "https://github.com/XeroChina/nimcap-screenshot/releases/download/v99.0.0/nimcap-screenshot-v99.0.0-win-x64.zip" } }
            })) });
        }
    }
}
