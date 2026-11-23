using L = NimCap.Screenshot.Localization.UiText;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using NimCap.Screenshot.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using WinForms = System.Windows.Forms;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace NimCap.Screenshot.Services;

public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _notifyIcon;
    private readonly Queue<(string Title, string Message, NotificationIcon Icon)> _pendingNotifications = new();
    private readonly Dictionary<GlobalAction, MenuItem> _actions = new();
    private readonly Dictionary<string, MenuItem> _groups = new();
    private readonly MenuItem _settingsItem, _exitItem, _openImageItem;
    public event Action<GlobalAction>? ActionRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? OpenImageRequested;

    public TrayService()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var definition in AppActionCatalog.All.Where(x => x.ShowInTray))
        {
            ItemsControl parent = menu;
            if (definition.Group.Length > 0)
            {
                if (!_groups.TryGetValue(definition.Group, out var group))
                {
                    group = LocalizedMenu(definition.Group); _groups.Add(definition.Group, group); menu.Items.Add(group);
                }
                parent = group;
            }
            var item = LocalizedMenu(definition.LabelKey, definition.Delay > 0 ? new object?[] { definition.Delay } : []);
            item.IsCheckable = definition.Action == GlobalAction.ScreenWords;
            item.Click += (_, _) => ActionRequested?.Invoke(definition.Action);
            parent.Items.Add(item); _actions.Add(definition.Action, item);
        }
        _actions[GlobalAction.CancelDelay].IsEnabled = false;
        _openImageItem = LocalizedMenu("OpenImage");
        _openImageItem.Click += (_, _) => OpenImageRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_openImageItem);
        menu.Items.Add(new Separator());
        _settingsItem = LocalizedMenu("Settings"); _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_settingsItem);
        _exitItem = LocalizedMenu("Exit"); _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_exitItem);
        _notifyIcon = new TaskbarIcon { IconSource = CreateTrayIconSource(), ToolTipText = L.Get("NimCapAppTitle"), ContextMenu = menu };
        _notifyIcon.TrayMouseDoubleClick += (_, _) => ActionRequested?.Invoke(GlobalAction.Region);
        UpdateHotkeyLabels(new AppSettings());
    }

    public void UpdateHotkeyLabels(AppSettings settings)
    {
        foreach (var definition in AppActionCatalog.All.Where(x => x.ShowInTray))
        {
            var item = _actions[definition.Action];
            item.InputGestureText = AppActionCatalog.Hotkey(settings, definition.Action);
            item.Visibility = AppActionCatalog.Visible(settings, definition) ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var group in _groups.Values)
            group.Visibility = group.Items.OfType<MenuItem>().Any(x => x.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        _settingsItem.Visibility = _exitItem.Visibility = Visibility.Visible;
        LocalizationService.Bind(_notifyIcon, TaskbarIcon.ToolTipTextProperty, "NimCap0RegionCapture", settings.RegionHotkey);
    }

    public void SetScreenWordsChecked(bool value) => _actions[GlobalAction.ScreenWords].IsChecked = value;
    public void SetDelayedScreenshotPending(bool pending) => _actions[GlobalAction.CancelDelay].IsEnabled = pending;

    public void Show()
    {
        if (_notifyIcon.IsCreated) _notifyIcon.TrayIcon.Show();
        else
        {
            try { _notifyIcon.ForceCreate(false); }
            catch (InvalidOperationException ex)
            {
                try { Directory.CreateDirectory(AppPaths.DiagnosticsRoot); File.WriteAllText(Path.Combine(AppPaths.DiagnosticsRoot, "tray-startup-error.txt"), ex.ToString()); }
                catch { }
            }
        }
        if (!_notifyIcon.IsCreated) return;
        while (_pendingNotifications.TryPeek(out var notification))
        {
            if (!TryShowMessage(notification.Title, notification.Message, notification.Icon)) break;
            _pendingNotifications.Dequeue();
        }
    }

    public void ShowMessage(string title, string message, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info)
    {
        var notificationIcon = icon switch { WinForms.ToolTipIcon.Warning => NotificationIcon.Warning,
            WinForms.ToolTipIcon.Error => NotificationIcon.Error, WinForms.ToolTipIcon.None => NotificationIcon.None, _ => NotificationIcon.Info };
        if (!TryShowMessage(title, message, notificationIcon)) _pendingNotifications.Enqueue((title, message, notificationIcon));
    }

    private static MenuItem LocalizedMenu(string key, params object?[] arguments)
    {
        var item = new MenuItem(); LocalizationService.Bind(item, MenuItem.HeaderProperty, key, arguments); return item;
    }

    internal static bool RunPinMenuSelfTest()
    {
        using var tray = new TrayService();
        var actions = new List<GlobalAction>(); tray.ActionRequested += actions.Add;
        RaiseClick(tray._actions[GlobalAction.ClipboardPin]);
        if (!actions.SequenceEqual(new[] { GlobalAction.ClipboardPin }) || tray._actions[GlobalAction.ClipboardPin].HasItems) return false;
        var settings = new AppSettings(); tray.UpdateHotkeyLabels(settings);
        if (tray._actions[GlobalAction.ClipboardPin].InputGestureText != settings.ClipboardPinHotkey) return false;
        foreach (var definition in AppActionCatalog.All) settings.TrayActions[definition.Action.ToString()] = false;
        settings.TrayActions["Settings"] = settings.TrayActions["Exit"] = false;
        tray.UpdateHotkeyLabels(settings);
        if (tray._actions.Values.Any(x => x.Visibility != Visibility.Collapsed) || tray._groups.Values.Any(x => x.Visibility != Visibility.Collapsed)
            || tray._settingsItem.Visibility != Visibility.Visible || tray._exitItem.Visibility != Visibility.Visible
            || tray._openImageItem.Visibility != Visibility.Visible) return false;
        foreach (var definition in AppActionCatalog.All) settings.TrayActions[definition.Action.ToString()] = true;
        tray.UpdateHotkeyLabels(settings); actions.Clear();
        foreach (var item in tray._actions.Values) RaiseClick(item);
        return actions.SequenceEqual(AppActionCatalog.All.Where(x => x.ShowInTray).Select(x => x.Action));
    }

    internal static bool RunLocalizationSelfTest()
    {
        using var tray = new TrayService();
        foreach (var language in new[] { "en", "zh-CN" })
        {
            LocalizationService.Apply(language);
            Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (AppActionCatalog.All.Where(x => x.ShowInTray).Any(x => !Equals(tray._actions[x.Action].Header, x.Label))
                || !Equals(tray._settingsItem.Header, L.Get("Settings")) || !Equals(tray._exitItem.Header, L.Get("Exit"))
                || !Equals(tray._openImageItem.Header, L.Get("OpenImage"))) return false;
        }
        return true;
    }

    internal System.Windows.Controls.ContextMenu Menu => _notifyIcon.ContextMenu;
    private static void RaiseClick(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));

    private bool TryShowMessage(string title, string message, NotificationIcon icon)
    {
        if (!_notifyIcon.IsCreated) return false;
        try { _notifyIcon.ShowNotification(title, message, icon); return true; }
        catch (InvalidOperationException) { return false; }
    }

    public void Dispose()
    {
        System.Windows.Data.BindingOperations.ClearAllBindings(_notifyIcon); _notifyIcon.Dispose();
    }

    private static BitmapSource CreateTrayIconSource()
    {
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri("pack://application:,,,/NimCap.Screenshot;component/logo.ico", UriKind.Absolute); source.EndInit(); source.Freeze(); return source;
    }
}

public enum PinCommand { Clipboard, CloseAll }
