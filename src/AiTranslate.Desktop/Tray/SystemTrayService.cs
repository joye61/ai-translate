using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AiTranslate.Desktop.Tray;

/// <summary>
/// System tray icon & NativeMenu service for AI Translate.
/// Supports minimize-to-tray, one-click restore, screenshot translate, selection translate,
/// clipboard translate, FSRS vocabulary review, and real-time monitoring toggles.
/// All icons are rendered from official Pictogrammers MDI vector paths.
/// </summary>
public sealed class SystemTrayService : IDisposable
{
    private readonly TrayIcon _trayIcon;
    private readonly AppSettingsStore _settingsStore;
    private readonly IVocabularyService _vocabularyService;
    private readonly NativeMenuItem _dueReviewItem;
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

        var showMainItem = new NativeMenuItem("打开 AI Translate 主工作台")
        {
            Icon = RenderMdiIconBitmap("translate", AppTheme.AccentBright)
        };
        showMainItem.Click += (_, _) => onShowMainWindow();

        var screenshotItem = new NativeMenuItem($"截图原位翻译 ({_settingsStore.Current.ScreenshotTranslateHotkey})")
        {
            Icon = RenderMdiIconBitmap("crop-free", AppTheme.Emerald)
        };
        screenshotItem.Click += (_, _) => onStartScreenshotTranslate();

        var selectionItem = new NativeMenuItem($"灵动划词速译 ({_settingsStore.Current.SelectionTranslateHotkey})")
        {
            Icon = RenderMdiIconBitmap("cursor-text", AppTheme.Purple)
        };
        selectionItem.Click += (_, _) => onTriggerSelectionTranslate();

        var clipboardItem = new NativeMenuItem($"翻译剪贴板内容 ({_settingsStore.Current.ClipboardTranslateHotkey})")
        {
            Icon = RenderMdiIconBitmap("clipboard-text-outline", AppTheme.AccentBright)
        };
        clipboardItem.Click += (_, _) => onTranslateClipboardNow();

        _dueReviewItem = new NativeMenuItem("FSRS 科学单词复习")
        {
            Icon = RenderMdiIconBitmap("brain", AppTheme.Amber)
        };
        _dueReviewItem.Click += (_, _) => onOpenVocabularyReview();

        _clipboardMonitorItem = new NativeMenuItem("后台监听剪贴板自动翻译")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settingsStore.Current.EnableClipboardMonitor
        };
        _clipboardMonitorItem.Click += (_, _) =>
        {
            var next = !_settingsStore.Current.EnableClipboardMonitor;
            _settingsStore.Update(_settingsStore.Current with { EnableClipboardMonitor = next });
        };

        _selectionMonitorItem = new NativeMenuItem("全局鼠标划词即译")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settingsStore.Current.EnableMouseSelectionTranslate
        };
        _selectionMonitorItem.Click += (_, _) =>
        {
            var next = !_settingsStore.Current.EnableMouseSelectionTranslate;
            _settingsStore.Update(_settingsStore.Current with { EnableMouseSelectionTranslate = next });
        };

        var settingsItem = new NativeMenuItem("AI 引擎与全局热键设置…")
        {
            Icon = RenderMdiIconBitmap("cog-outline", AppTheme.TextSecondary)
        };
        settingsItem.Click += (_, _) => onOpenSettings();

        var exitItem = new NativeMenuItem("完全退出 AI Translate")
        {
            Icon = RenderMdiIconBitmap("close", AppTheme.Danger)
        };
        exitItem.Click += (_, _) => onExitApplication();

        var menu = new NativeMenu
        {
            Items =
            {
                showMainItem,
                new NativeMenuItemSeparator(),
                screenshotItem,
                selectionItem,
                clipboardItem,
                new NativeMenuItemSeparator(),
                _dueReviewItem,
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
            Icon = CreateAppWindowIcon(),
            ToolTipText = "AI Translate · AI 词典 / 划词 / 截图原位翻译 / 超级单词本",
            Menu = menu,
            IsVisible = true
        };
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
        if (_disposed) return;
        var s = _settingsStore.Current;
        _clipboardMonitorItem.IsChecked = s.EnableClipboardMonitor;
        _selectionMonitorItem.IsChecked = s.EnableMouseSelectionTranslate;

        var stats = _vocabularyService.GetDashboardStats();
        _dueReviewItem.Header = stats.DueTodayCount > 0
            ? $"开始今日 FSRS 复习 ({stats.DueTodayCount} 词待复习)"
            : $"打开超级生词本 (共 {stats.TotalEntries} 词)";
        _trayIcon.ToolTipText = $"AI Translate · 今日待复习 {stats.DueTodayCount} 词\nAlt+Q 划词 | Alt+S 截图翻译 | 单击打开主界面";
    }

    public static WindowIcon CreateAppWindowIcon()
    {
        var badge = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = AppTheme.BrandGradient,
            Child = new MdiIcon("translate", 20, Brushes.White)
        };
        badge.Measure(new Size(32, 32));
        badge.Arrange(new Rect(0, 0, 32, 32));
        var rtb = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        rtb.Render(badge);
        return new WindowIcon(rtb);
    }

    private static Bitmap RenderMdiIconBitmap(string iconName, IBrush brush)
    {
        var icon = new MdiIcon(iconName, 18, brush)
        {
            Width = 24,
            Height = 24
        };
        icon.Measure(new Size(24, 24));
        icon.Arrange(new Rect(0, 0, 24, 24));
        var rtb = new RenderTargetBitmap(new PixelSize(24, 24), new Vector(96, 96));
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
