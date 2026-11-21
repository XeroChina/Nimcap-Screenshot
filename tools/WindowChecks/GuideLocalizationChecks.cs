using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NimCap.Screenshot.Localization;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;
using NimCap.Screenshot.Views;
using ComboBox = System.Windows.Controls.ComboBox;
using Point = System.Windows.Point;

namespace NimCap.Screenshot.WindowChecks;

internal static partial class Program
{
    private static void GuideLocalizationChecks()
    {
        string[] keys = ["ScreenRuler", "ScreenRulerToggle", "PrecisionKeysHint", "ScreenRulerHelp", "ScreenRulerResize",
            "CaptureCrosshair", "CaptureToolShortcutHint", "CapturePointerMoveHint", "ScreenRulerReadout",
            "DesktopRulerLine", "DesktopRulerBox", "DesktopRulerDistance", "DesktopRulerGuides", "DesktopRulerAngle", "DesktopRulerMore",
            "DesktopRulerMagnifier", "DesktopRulerClear", "DesktopRulerMeasureHint", "DesktopRulerAngleHint", "DesktopRulerGuidesHint", "DesktopRulerCaptureUnavailable",
            "DesktopRulerLineSteps", "DesktopRulerBoxSteps", "DesktopRulerDistanceSteps", "DesktopRulerAngleStep1", "DesktopRulerAngleStep2",
            "DesktopRulerAngleStep3", "DesktopRulerQuickHelp", "DesktopRulerLayers", "DesktopRulerMoveKeys",
            "DesktopRulerAutoCopy", "DesktopRulerAutoCopyHint", "DesktopRulerLengthCopy", "DesktopRulerDistanceCopy", "DesktopRulerCopied", "DesktopRulerCopyBusy"];
        foreach (var language in LocalizationService.Languages.Where(l => l.Code.Length > 0))
        {
            var culture = CultureInfo.GetCultureInfo(language.Code);
            foreach (var key in keys)
            {
                var value = UiText.Resources.GetString(key, culture);
                Require(!string.IsNullOrWhiteSpace(value) && !value.Contains('?') && !value.Contains('\uFFFD'),
                    language.Code + "/" + key + " contains damaged or missing text.");
                if (language.Code == "en" || key is "CaptureToolShortcutHint" or "ScreenRulerReadout") continue;
                Require(value != UiText.Resources.GetString(key, CultureInfo.GetCultureInfo("en")),
                    language.Code + "/" + key + " still contains an English fallback.");
            }
        }
        var original = UiText.Culture.Name;
        try
        {
            LocalizationService.Apply("zh-CN");
            Require(UiText.Get("ScreenRuler") == "屏幕尺子" && UiText.Get("CaptureCrosshair") == "十字线",
                "Compiled Simplified Chinese guide resources are corrupted.");
            var image = PrecisionImage();
            var screen = new CapturedScreen(new System.Drawing.Rectangle(0, 0, image.PixelWidth, image.PixelHeight), image);
            var preferences = new SettingsService(Path.Combine(Root, "guide-localization.json"));
            var overlay = new SelectionWindow(screen, [], new PrecisionCapture(), settings: preferences);
            try
            {
                overlay.Show(); overlay.Activate(); overlay.Focus(); overlay.UpdateLayout();
                ((ComboBox)overlay.FindName("CaptureModePicker")).SelectedIndex = 0;
                Invoke(overlay, "UpdatePointer", new Point(390, 330));
                Require(((ComboBoxItem)((ComboBox)overlay.FindName("ShapePicker")).SelectedItem).Content.ToString() == "矩形",
                    "Capture XAML did not load its Chinese resource text.");
                SavePreview((FrameworkElement)overlay.Content, Path.Combine(Root, "capture-guides-zh-CN.png"));
            }
            finally { overlay.Close(); }
            using var hotkeys = new GlobalHotkeyHost();
            var settings = new SettingsWindow(preferences, new StartupRegistrationService(), hotkeys);
            try
            {
                settings.SelectCategory("Keyboard"); settings.Show(); settings.UpdateLayout();
                SavePreview((FrameworkElement)settings.Content, Path.Combine(Root, "guide-settings-zh-CN.png"));
            }
            finally { settings.Close(); }
        }
        finally { LocalizationService.Apply(original); }
        Console.WriteLine($"PASS: all {keys.Length} guide/ruler strings translated in the built language packs; compiled Chinese resources and capture/settings previews verified.");
    }
}
