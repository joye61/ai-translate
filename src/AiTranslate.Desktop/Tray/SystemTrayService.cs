using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AiTranslate.Desktop.Tray;

/// <summary>
/// System tray icon & NativeMenu service for AI Translate.
/// Supports minimize-to-tray, one-click restore, screenshot translate, selection translate,
/// clipboard translate, FSRS vocabulary review, and real-time monitoring toggles.
/// All icons are rendered from Font Awesome Free vector paths.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly AppSettingsStore _settingsStore;
    private readonly IVocabularyService _vocabularyService;
    private readonly NativeMenuItem _screenshotItem;
    private readonly NativeMenuItem _selectionItem;
    private readonly NativeMenuItem _clipboardItem;
    private readonly NativeMenuItem _dueReviewItem;
    private readonly Bitmap _reviewIcon = RenderIconBitmap("layer-group", AppTheme.TextSecondary);
    private readonly Bitmap _notebookIcon = RenderIconBitmap("book", AppTheme.TextSecondary);
    private readonly NativeMenuItem _clipboardMonitorItem;
    private readonly NativeMenuItem _selectionMonitorItem;
    private bool _disposed;

    public SystemTrayService(
        AppSettingsStore settingsStore,
        IVocabularyService vocabularyService,
        Action onShowMainWindow,
        Action onOpenVocabularyReview,
        Action onStartScreenshotTranslate,
        Action onTriggerSelectionTranslate,
        Action onTranslateClipboardNow,
        Action onOpenSettings,
        Action onExitApplication)
    {
        _settingsStore = settingsStore;
        _vocabularyService = vocabularyService;

        var showMainItem = new NativeMenuItem("打开 AI Translate")
        {
            Icon = RenderIconBitmap("language", AppTheme.AccentBright)
        };
        showMainItem.Click += (_, _) => onShowMainWindow();

        _screenshotItem =                 CreateActionItem("crop-simple", onStartScreenshotTranslate);
                _selectionItem = CreateActionItem("i-cursor", onTriggerSelectionTranslate);
                _clipboardItem = CreateActionItem("paste", onTranslateClipboardNow);

        _dueReviewItem = new NativeMenuItem();
        _dueReviewItem.Click += (_, _) => onOpenVocabularyReview();

        _clipboardMonitorItem = new NativeMenuItem("自动翻译剪贴板")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settingsStore.Current.EnableClipboardMonitor
        };
        _clipboardMonitorItem.Click += (_, _) =>
        {
            var next = !_settingsStore.Current.EnableClipboardMonitor;
            _settingsStore.Update(_settingsStore.Current with { EnableClipboardMonitor = next });
        };

        _selectionMonitorItem = new NativeMenuItem("鼠标划词即译")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settingsStore.Current.EnableMouseSelectionTranslate
        };
        _selectionMonitorItem.Click += (_, _) =>
        {
            var next = !_settingsStore.Current.EnableMouseSelectionTranslate;
            _settingsStore.Update(_settingsStore.Current with { EnableMouseSelectionTranslate = next });
        };

        var settingsItem = new NativeMenuItem("设置…")
        {
            Icon = RenderIconBitmap("gear", AppTheme.TextSecondary)
        };
        settingsItem.Click += (_, _) => onOpenSettings();

        var exitItem = new NativeMenuItem("退出")
        {
            Icon = RenderIconBitmap("power-off", AppTheme.TextSecondary)
        };
        exitItem.Click += (_, _) => onExitApplication();

        var menu = new NativeMenu
        {
            Items =
            {
                showMainItem,
                _dueReviewItem,
                new NativeMenuItemSeparator(),
                _screenshotItem,
                _selectionItem,
                _clipboardItem,
                new NativeMenuItemSeparator(),
                _clipboardMonitorItem,
                _selectionMonitorItem,
                new NativeMenuItemSeparator(),
                settingsItem,
                exitItem
            }
        };

        _trayIcon = new TrayIcon
        {
            Icon = AppIcons.CreateTrayIcon(),
            ToolTipText = "AI Translate · AI 词典 / 划词 / 截图原位翻译 / 超级单词本",
            Menu = menu,
            IsVisible = true
        };
        if (OperatingSystem.IsMacOS()) MacOSProperties.SetIsTemplateIcon(_trayIcon, true);
        _trayIcon.Clicked += (_, _) => onShowMainWindow();

        // A TrayIcon only becomes a real OS tray icon once it is registered on the Application
        // via the TrayIcon.Icons attached property. Without this call the icon object exists
        // but nothing is ever shown in the system tray.
        if (Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
            icons.Add(_trayIcon);
            TrayIcon.SetIcons(app, icons);
        }

        _settingsStore.Changed += OnSettingsChanged;
        _vocabularyService.Changed += OnVocabularyChanged;
        RefreshTrayStates();
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        RefreshTrayStates();
    }

    private void OnVocabularyChanged(object? sender, EventArgs e)
    {
        RefreshTrayStates();
    }

    public void RefreshTrayStates()
    {
        // A dictionary lookup can notify the notebook from its cache worker.
        if (!_trayIcon.Dispatcher.CheckAccess())
        {
            _trayIcon.Dispatcher.Post(RefreshTrayStates);
            return;
        }
        if (_disposed) return;
        var s = _settingsStore.Current;
        _clipboardMonitorItem.IsChecked = s.EnableClipboardMonitor;
        _selectionMonitorItem.IsChecked = s.EnableMouseSelectionTranslate;

        SetHotkey(_screenshotItem, "截图翻译", s.ScreenshotTranslateHotkey);
        SetHotkey(_selectionItem, "划词翻译", s.SelectionTranslateHotkey);
        SetHotkey(_clipboardItem, "剪贴板翻译", s.ClipboardTranslateHotkey);

        var summaries = _vocabularyService.GetNotebookSummaries();
        var due = summaries.Sum(n => n.DueCount);
        var hasDue = due > 0;
        _dueReviewItem.Header = hasDue ? $"复习今日到期 · {due} 词" : $"打开生词本 · {summaries.Sum(n => n.EntryCount)} 词";
        _dueReviewItem.Icon = hasDue ? _reviewIcon : _notebookIcon;
        _trayIcon.ToolTipText = hasDue
            ? $"AI Translate · 今日待复习 {due} 词"
            : "AI Translate";
    }

    private static NativeMenuItem CreateActionItem(string iconName, Action onClick)
    {
        var item = new NativeMenuItem { Icon = RenderIconBitmap(iconName, AppTheme.TextSecondary) };
        item.Click += (_, _) => onClick();
        return item;
    }

    // The shortcut goes into Gesture so the menu right-aligns it in a muted column; fall back to the
    // header text when the stored key has no Avalonia Key equivalent.
    private static void SetHotkey(NativeMenuItem item, string label, HotkeyGesture hotkey)
    {
        var gesture = ToKeyGesture(hotkey);
        item.Gesture = gesture;
        item.Header = gesture is null ? $"{label}    {hotkey}" : label;
    }

    private static KeyGesture? ToKeyGesture(HotkeyGesture hotkey)
    {
        var name = hotkey.Key.Trim();
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse<Key>(name, true, out var key) || key == Key.None) return null;

        var modifiers = KeyModifiers.None;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers |= KeyModifiers.Control;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers |= KeyModifiers.Alt;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers |= KeyModifiers.Shift;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Meta)) modifiers |= KeyModifiers.Meta;
        return new KeyGesture(key, modifiers);
    }

    public static WindowIcon CreateAppWindowIcon() => AppIcons.CreateWindowIcon();

    // Rendered large at 96 DPI: the Fluent menu icon slot is a down-scaling Viewbox (16 DIP) and native
    // menus resize icons to the menu font, so this stays crisp on high-DPI displays. A DPI-scaled
    // RenderTargetBitmap would be cropped by Image, which treats its DIP size as the pixel source rect.
    private static Bitmap RenderIconBitmap(string iconName, IBrush brush)
    {
        const int size = 64;
        var icon = new FontAwesomeIcon(iconName, size, brush) { Width = size, Height = size };
        icon.Measure(new Size(size, size));
        icon.Arrange(new Rect(0, 0, size, size));
        var rtb = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        rtb.Render(icon);
        return rtb;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settingsStore.Changed -= OnSettingsChanged;
        _vocabularyService.Changed -= OnVocabularyChanged;
        if (Application.Current is { } app && TrayIcon.GetIcons(app) is { } icons)
            icons.Remove(_trayIcon);
        _trayIcon.IsVisible = false;
        _trayIcon.Dispose();
    }
}
