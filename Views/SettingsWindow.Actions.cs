using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using L = NimCap.Screenshot.Localization.UiText;

namespace NimCap.Screenshot.Views;

public partial class SettingsWindow
{
    private readonly Dictionary<GlobalAction, TextBox> _actionFields = new();
    private readonly Dictionary<GlobalAction, CheckBox> _trayChecks = new();
    private TextBox RegionHotkey => _actionFields[GlobalAction.Region];
    private TextBox FullScreenHotkey => _actionFields[GlobalAction.FullScreen];
    private TextBox ActiveWindowHotkey => _actionFields[GlobalAction.ActiveWindow];
    private TextBox ClipboardPinHotkey => _actionFields[GlobalAction.ClipboardPin];
    private TextBox TogglePinsHotkey => _actionFields[GlobalAction.TogglePins];

    private void InitializeActionSettings(AppSettings settings)
    {
        foreach (var definition in AppActionCatalog.All)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(82) });
            var label = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            if (definition.Delay > 0) LocalizationService.Bind(label, TextBlock.TextProperty, "TrayDelayAction", definition.Delay);
            else LocalizationService.Bind(label, TextBlock.TextProperty, definition.LabelKey);
            var field = new TextBox { Text = AppActionCatalog.Hotkey(settings, definition.Action), Style = (Style)FindResource("HotkeyField"),
                Width = 160, HorizontalAlignment = HorizontalAlignment.Stretch, ToolTip = definition.Label };
            field.PreviewKeyDown += Hotkey_PreviewKeyDown;
            System.Windows.Automation.AutomationProperties.SetName(field, definition.Label);
            var check = new CheckBox { IsChecked = AppActionCatalog.Visible(settings, definition), HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, ToolTip = L.Get("ShowInTray"),
                Visibility = definition.ShowInTray ? Visibility.Visible : Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetName(check, definition.Label + " " + L.Get("ShowInTray"));
            Grid.SetColumn(field, 1); Grid.SetColumn(check, 2);
            row.Children.Add(label); row.Children.Add(field); row.Children.Add(check); ShortcutRows.Children.Add(row);
            _actionFields.Add(definition.Action, field); _trayChecks.Add(definition.Action, check);
        }
    }

    private void StoreActionSettings(AppSettings candidate)
    {
        candidate.ActionHotkeys = new(candidate.ActionHotkeys);
        candidate.TrayActions = new(candidate.TrayActions);
        foreach (var definition in AppActionCatalog.All)
        {
            AppActionCatalog.SetHotkey(candidate, definition.Action, NormalizeHotkey(_actionFields[definition.Action].Text, definition.Label));
            if (definition.ShowInTray)
                candidate.TrayActions[definition.Action.ToString()] = _trayChecks[definition.Action].IsChecked == true;
        }
    }

    internal static void RunActionSettingsSelfTest(string output, AppUpdateSession updates)
    {
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var settings = new SettingsService(Path.Combine(output, "action-settings.json"), output);
        settings.Update(new AppSettings { RegionHotkey = "Ctrl+D1", ClipboardPinHotkey = "Ctrl+D2", ScreenRulerHotkey = "Ctrl+D3" });
        var snapshot = System.Text.Json.JsonSerializer.Serialize(settings.Current);
        foreach (var culture in new[] { "zh-CN", "en" })
        {
            LocalizationService.Apply(culture);
            using var hotkey = new GlobalHotkeyHost(_ => false, (_, _, _) => true, _ => { });
            var window = new SettingsWindow(settings, new StartupRegistrationService(), hotkey, updates);
            try
            {
                Require(window._actionFields.Count == AppActionCatalog.All.Count && window.RegionHotkey.Text == "Ctrl+D1", "Legacy shortcuts were not retained.");
                Require(window.FindName("AppReleaseNotesPanel") is null && window.AppUpdateStatus.Text.Contains("99.0.0")
                    && window.AppUpdateDownloadButton.Visibility == Visibility.Visible, "Cached release UI is missing or still displays inline notes.");
                window.SelectCategory("Keyboard");
                var root = (FrameworkElement)window.Content;
                foreach (var size in new[] { new System.Windows.Size(960, 720), new System.Windows.Size(640, 520) })
                {
                    window.CompactLayout = size.Width < 840;
                    root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
                    foreach (var field in window._actionFields.Values)
                        Require(field.ActualWidth >= 130, "Shortcut field clipped at " + size);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(root); File.WriteAllBytes(Path.Combine(output, $"shortcut-settings-{culture}-{size.Width}.png"), OnnxWorker.Encode(bitmap));
                }
                var index = 0;
                foreach (var definition in AppActionCatalog.All)
                {
                    window._actionFields[definition.Action].Text = "Ctrl+Alt+F" + ++index;
                    window._trayChecks[definition.Action].IsChecked = false;
                }
                var candidate = window.BuildCandidate();
                Require(GlobalHotkeyHost.CreateRegistrations(candidate).Count == AppActionCatalog.All.Count, "Some tray actions cannot have shortcuts.");
                Require(AppActionCatalog.All.All(x => !AppActionCatalog.Visible(candidate, x)), "Visibility checkboxes were not stored.");
                Require(snapshot == System.Text.Json.JsonSerializer.Serialize(settings.Current), "Editing staged shortcuts changed persisted settings.");
                using var tray = new TrayService(); tray.UpdateHotkeyLabels(candidate);
                Require(tray.Menu.Items.OfType<MenuItem>().Count(x => x.Visibility == Visibility.Visible) == 3, "Open image, Settings and Exit must be the only fixed items.");
                foreach (var definition in AppActionCatalog.All) candidate.TrayActions[definition.Action.ToString()] = definition.DefaultVisible;
                tray.UpdateHotkeyLabels(candidate);
                tray.Menu.Measure(new System.Windows.Size(460, 900)); tray.Menu.Arrange(new Rect(tray.Menu.DesiredSize)); tray.Menu.UpdateLayout();
                var menuBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(tray.Menu.ActualWidth), (int)Math.Ceiling(tray.Menu.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                menuBitmap.Render(tray.Menu); File.WriteAllBytes(Path.Combine(output, "tray-" + culture + ".png"), OnnxWorker.Encode(menuBitmap));
                window._actionFields[GlobalAction.History].Text = window.RegionHotkey.Text;
                try { window.BuildCandidate(); throw new Exception("Duplicate shortcuts accepted."); }
                catch (InvalidOperationException) { }
                window._actionFields[GlobalAction.History].Text = "Ctrl+Alt+F15";
                settings.Update(candidate); settings.Load();
                Require(GlobalHotkeyHost.CreateRegistrations(settings.Current).Count == AppActionCatalog.All.Count, "New shortcut settings did not round-trip.");
                foreach (var field in window._actionFields.Values) field.Clear();
                Require(GlobalHotkeyHost.CreateRegistrations(window.BuildCandidate()).Count == 0, "Clearing shortcuts did not disable them.");
                // Reset only the isolated test store before checking the next language.
                settings.Update(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(snapshot)!);
            }
            finally { window.Close(); }
        }
    }
}
