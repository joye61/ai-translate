using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Desktop.Tray;
using AiTranslate.Desktop.Views;
using AiTranslate.Platform.Windows;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop;

/// <summary>
/// Lightweight shell window orchestrating Navigation, System Tray lifecycle, Global Hotkeys, and the 4 modular Views.
/// Closing the window hides it to the System Tray unless an explicit Exit is requested from the tray menu.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly IDesktopPlatformService _platform;
    private readonly AppSettingsStore _settingsStore;
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speechService;
    private readonly ScreenshotTranslationCoordinator _screenshotCoordinator;
    private readonly SelectionPopupWindow _selectionPopup;
    private readonly SystemTrayService _trayService;

    private readonly DictionaryStudioView _dictionaryView;
    private readonly VocabularyVaultView _vocabularyView;
    private readonly FsrsReviewArenaView _reviewView;
    private readonly EngineSettingsView _settingsView;

    private readonly Button _navDictBtn;
    private readonly Button _navVocabBtn;
    private readonly Button _navReviewBtn;
    private readonly Button _navSettingsBtn;
    private readonly TextBlock _dueBadgeText;
    private readonly Border _dueBadgeBorder;
    private readonly ToggleSwitch _clipboardMonitorToggle;
    private readonly ToggleSwitch _selectionMonitorToggle;
    private readonly TextBlock _statusFooterText;

    private bool _allowExit;
    private bool _syncingToggles;

    public MainWindow(
        IDesktopPlatformService platform,
        AppSettingsStore settingsStore,
        IAiDictionaryService dictionaryService,
        IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService)
    {
        _platform = platform;
        _settingsStore = settingsStore;
        _vocabularyService = vocabularyService;
        _speechService = speechService;

        Title = "AI Translate · 认知词典 · 划词 · XCapture 截图翻译 · 超级单词本";
        Icon = SystemTrayService.CreateAppWindowIcon();
        Width = 1320;
        Height = 840;
        MinWidth = 1020;
        MinHeight = 680;
        Background = AppTheme.WindowBg;
        FontFamily = AppTheme.BodyFont;

        _statusFooterText = new TextBlock
        {
            Text = "常驻系统托盘运行中 · 快捷键：Alt+Q 划词速译 | Alt+S 截图原位翻译 | Alt+W 剪贴板翻译 | Alt+D 呼出工作台",
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center
        };

        _screenshotCoordinator = new ScreenshotTranslationCoordinator(
            _platform,
            _settingsStore,
            onOpenInDictionary: (text, sourceTitle) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ShowAndActivate();
                    SwitchTab(0);
                    _ = _dictionaryView!.LookupAsync(text, null, TranslationTriggerSource.ScreenshotOcr, sourceTitle ?? "截图翻译");
                });
            },
            onQuickSaveToNotebook: (source, translated) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var entry = new DictionaryEntryResult
                    {
                        SourceText = source,
                        PrimaryTranslation = translated,
                        TargetLanguage = _settingsStore.Current.DefaultTargetLanguage,
                        ItemType = source.Contains(' ') ? VocabularyItemType.Sentence : VocabularyItemType.Word,
                        Senses = [new("截图译文", translated)]
                    };
                    _vocabularyService.AddOrUpdateFromDictionary(
                        entry,
                        _settingsStore.Current.DefaultNotebookId,
                        contextSentence: source,
                        source: TranslationTriggerSource.ScreenshotOcr,
                        sourceTitle: "XCapture 截图原位翻译");
                    SetStatus($"已将截图识别内容收录至生词本！");
                });
            });

        _selectionPopup = new SelectionPopupWindow(
            dictionaryService,
            _vocabularyService,
            _speechService,
            _settingsStore,
            onExpandToMain: (result, captured) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ShowAndActivate();
                    SwitchTab(0);
                    _dictionaryView!.DisplayResult(result, captured.Source, captured.SourceWindowTitle);
                });
            });

        // Instantiate modular Views
        _dictionaryView = new DictionaryStudioView(
            dictionaryService,
            _vocabularyService,
            _speechService,
            _settingsStore,
            SetStatus,
            () => _screenshotCoordinator.StartCaptureAndTranslateAsync(),
            TranslateClipboardNowAsync);

        _vocabularyView = new VocabularyVaultView(
            _vocabularyService,
            _speechService,
            _settingsStore,
            onLookupInStudio: (word, ctx, src, title) =>
            {
                SwitchTab(0);
                _ = _dictionaryView.LookupAsync(word, ctx, src, title);
            },
            onSwitchToReview: () => SwitchTab(2),
            SetStatus);

        _reviewView = new FsrsReviewArenaView(_vocabularyService, _speechService, _settingsStore, SetStatus);
        _settingsView = new EngineSettingsView(_settingsStore, _speechService, SetStatus);
        _speechService.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => SetStatus(s));

        // Initialize System Tray Icon & Menu
        _trayService = new SystemTrayService(
            _settingsStore,
            _vocabularyService,
            onShowMainWindow: () => Dispatcher.UIThread.Post(ShowAndActivate),
            onOpenVocabularyReview: () => Dispatcher.UIThread.Post(() => { ShowAndActivate(); SwitchTab(2); }),
            onStartScreenshotTranslate: () => _ = _screenshotCoordinator.StartCaptureAndTranslateAsync(),
            onTriggerSelectionTranslate: () => _ = TriggerSelectionHotkeyAsync(),
            onTranslateClipboardNow: () => _ = TranslateClipboardNowAsync(),
            onOpenSettings: () => Dispatcher.UIThread.Post(() => { ShowAndActivate(); SwitchTab(3); }),
            onExitApplication: ExitApplication);

        _navDictBtn = CreateNavButton("translate", "AI 认知词典工作台", 0);
        _navVocabBtn = CreateNavButton("book-open-page-variant", "超级生词本与记忆库", 1);
        _navReviewBtn = CreateNavButton("brain", "FSRS 科学复习训练", 2);
        _navSettingsBtn = CreateNavButton("cog-outline", "AI 引擎与热键配置", 3);

        _dueBadgeText = new TextBlock { Text = "0", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        _dueBadgeBorder = new Border
        {
            Background = AppTheme.Amber,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Child = _dueBadgeText
        };

        _clipboardMonitorToggle = new ToggleSwitch
        {
            OffContent = "监听剪贴板自动快译",
            OnContent = "监听剪贴板自动快译",
            IsChecked = _settingsStore.Current.EnableClipboardMonitor,
            Foreground = AppTheme.TextSecondary,
            FontSize = 12
        };
        _clipboardMonitorToggle.IsCheckedChanged += (_, _) =>
        {
            if (_syncingToggles) return;
            var enabled = _clipboardMonitorToggle.IsChecked == true;
            _settingsStore.Update(_settingsStore.Current with { EnableClipboardMonitor = enabled });
        };

        _selectionMonitorToggle = new ToggleSwitch
        {
            OffContent = "全局鼠标划词即译",
            OnContent = "全局鼠标划词即译",
            IsChecked = _settingsStore.Current.EnableMouseSelectionTranslate,
            Foreground = AppTheme.TextSecondary,
            FontSize = 12
        };
        _selectionMonitorToggle.IsCheckedChanged += (_, _) =>
        {
            if (_syncingToggles) return;
            var enabled = _selectionMonitorToggle.IsChecked == true;
            _settingsStore.Update(_settingsStore.Current with { EnableMouseSelectionTranslate = enabled });
        };

        var sidebar = BuildSidebar();
        var contentHost = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new Panel
                {
                    Children =
                    {
                        _dictionaryView,
                        _vocabularyView,
                        _reviewView,
                        _settingsView
                    }
                },
                new Border
                {
                    [Grid.RowProperty] = 1,
                    Background = AppTheme.SidebarBg,
                    BorderBrush = AppTheme.BorderSubtle,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(20, 8),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            _statusFooterText,
                            StudioUi.CreateBadge("托盘常驻已启用", AppTheme.EmeraldSoft, AppTheme.Emerald)
                        }
                    }
                }
            }
        };
        Grid.SetColumn(((Grid)((Border)contentHost.Children[1]).Child!).Children[1], 1);

        var rootGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("256,*"),
            Children = { sidebar, contentHost }
        };
        Grid.SetColumn(contentHost, 1);
        Content = rootGrid;

        // Wire events
        _platform.IsClipboardMonitoringEnabled = _settingsStore.Current.EnableClipboardMonitor;
        _platform.IsMouseSelectionMonitoringEnabled = _settingsStore.Current.EnableMouseSelectionTranslate;
        _platform.ClipboardTextChanged += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Text)) _ = _selectionPopup.ShowAndTranslateAsync(e); };
        _platform.MouseSelectionCaptured += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Text)) _ = _selectionPopup.ShowAndTranslateAsync(e); };

        _settingsStore.Changed += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            _platform.IsClipboardMonitoringEnabled = s.EnableClipboardMonitor;
            _platform.IsMouseSelectionMonitoringEnabled = s.EnableMouseSelectionTranslate;
            _syncingToggles = true;
            try
            {
                _clipboardMonitorToggle.IsChecked = s.EnableClipboardMonitor;
                _selectionMonitorToggle.IsChecked = s.EnableMouseSelectionTranslate;
            }
            finally { _syncingToggles = false; }
        });

        _vocabularyService.Changed += (_, _) => Dispatcher.UIThread.Post(OnVocabularyChanged);

        RegisterGlobalHotkeys();
        OnVocabularyChanged();
        SwitchTab(0);
        _ = _dictionaryView.LookupAsync("serendipity", null, TranslationTriggerSource.ManualInput, "精选词条");
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowExit && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
            SetStatus("已最小化到系统托盘（单击托盘图标或按 Alt+D 可随时呼出）");
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _selectionPopup.Close();
        _screenshotCoordinator.Dispose();
        _trayService.Dispose();
        base.OnClosed(e);
    }

    public void ExitApplication()
    {
        _allowExit = true;
        Close();
    }

    private Border BuildSidebar()
    {
        var brandBox = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Margin = new Thickness(18, 20, 18, 16),
            Children =
            {
                new Border
                {
                    Width = 38,
                    Height = 38,
                    CornerRadius = new CornerRadius(10),
                    Background = AppTheme.BrandGradient,
                    Child = new MdiIcon("translate", 21, Brushes.White)
                },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "AI Translate", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = AppTheme.TextPrimary },
                        new TextBlock { Text = "Cognitive Lexicon Studio", FontSize = 11, Foreground = AppTheme.TextMuted }
                    }
                }
            }
        };

        var navStack = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(12, 0),
            Children =
            {
                _navDictBtn,
                _navVocabBtn,
                new Grid { Children = { _navReviewBtn, _dueBadgeBorder } },
                _navSettingsBtn
            }
        };

        var captureHeader = new TextBlock
        {
            Text = "全场景即时捕获",
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppTheme.TextMuted,
            Margin = new Thickness(18, 20, 18, 8)
        };

        var captureStack = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(12, 0),
            Children =
            {
                CreateQuickCaptureBtn("crop-free", "XCapture 截图翻译 (Alt+S)", AppTheme.AccentSoft, AppTheme.AccentBright, () => _ = _screenshotCoordinator.StartCaptureAndTranslateAsync()),
                CreateQuickCaptureBtn("cursor-text", "灵动划词速译 (Alt+Q)", AppTheme.PurpleSoft, AppTheme.Purple, () => _ = TriggerSelectionHotkeyAsync()),
                CreateQuickCaptureBtn("clipboard-text-outline", "读取剪贴板精翻 (Alt+W)", AppTheme.EmeraldSoft, AppTheme.Emerald, () => _ = TranslateClipboardNowAsync())
            }
        };

        var monitorCard = new Border
        {
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(12, 12, 12, 14),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "后台自动化感知", FontSize = 11.5, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextSecondary },
                    _clipboardMonitorToggle,
                    _selectionMonitorToggle
                }
            }
        };

        var dock = new DockPanel
        {
            LastChildFill = false,
            Children = { brandBox, navStack, captureHeader, captureStack, monitorCard }
        };
        DockPanel.SetDock(brandBox, Dock.Top);
        DockPanel.SetDock(navStack, Dock.Top);
        DockPanel.SetDock(captureHeader, Dock.Top);
        DockPanel.SetDock(captureStack, Dock.Top);
        DockPanel.SetDock(monitorCard, Dock.Bottom);

        return new Border
        {
            Background = AppTheme.SidebarBg,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = dock
        };
    }

    private void OnVocabularyChanged()
    {
        var stats = _vocabularyService.GetDashboardStats();
        _dueBadgeText.Text = stats.DueTodayCount.ToString();
        _dueBadgeBorder.IsVisible = stats.DueTodayCount > 0;
        _dictionaryView.RefreshNotebooks();
        _vocabularyView.RefreshAll();
    }

    public void SwitchTab(int index)
    {
        _dictionaryView.IsVisible = index == 0;
        _vocabularyView.IsVisible = index == 1;
        _reviewView.IsVisible = index == 2;
        _settingsView.IsVisible = index == 3;

        UpdateNavHighlight(_navDictBtn, index == 0);
        UpdateNavHighlight(_navVocabBtn, index == 1);
        UpdateNavHighlight(_navReviewBtn, index == 2);
        UpdateNavHighlight(_navSettingsBtn, index == 3);

        if (index == 1) _vocabularyView.RefreshAll();
        if (index == 2) _reviewView.RefreshDecksAndStart();
    }

    private void RegisterGlobalHotkeys()
    {
        var s = _settingsStore.Current;
        _platform.TryRegisterHotkey(GlobalHotkeyId.ShowMainWindow, s.ShowWindowHotkey, () => Dispatcher.UIThread.Post(ShowAndActivate), out _);
        _platform.TryRegisterHotkey(GlobalHotkeyId.SelectionTranslate, s.SelectionTranslateHotkey, () => _ = TriggerSelectionHotkeyAsync(), out _);
        _platform.TryRegisterHotkey(GlobalHotkeyId.ScreenshotTranslate, s.ScreenshotTranslateHotkey, () => _ = _screenshotCoordinator.StartCaptureAndTranslateAsync(), out _);
        _platform.TryRegisterHotkey(GlobalHotkeyId.ClipboardTranslate, s.ClipboardTranslateHotkey, () => _ = TranslateClipboardNowAsync(), out _);
    }

    private async Task TriggerSelectionHotkeyAsync()
    {
        var captured = await _platform.TryCaptureSelectedTextAsync();
        if (captured is not null && !string.IsNullOrWhiteSpace(captured.Text))
        {
            await _selectionPopup.ShowAndTranslateAsync(captured);
            return;
        }
        var cursor = _platform.GetCursorPosition();
        await _selectionPopup.ShowAndTranslateAsync(new CapturedTextEvent("serendipity", TranslationTriggerSource.SelectionPopup, cursor, "划词翻译快捷键"));
    }

    private async Task TranslateClipboardNowAsync()
    {
        string? text = null;
        if (Clipboard is not null)
            text = await Clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text) && OperatingSystem.IsWindows())
            text = WindowsDesktopPlatformService.TryReadClipboardUnicodeText();
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("当前剪贴板中没有可翻译的文本内容。");
            return;
        }
        ShowAndActivate();
        SwitchTab(0);
        await _dictionaryView.LookupAsync(text.Trim(), null, TranslationTriggerSource.ClipboardMonitor, "剪贴板读取");
    }

    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SetStatus(string message) => _statusFooterText.Text = message;

    private Button CreateNavButton(string icon, string title, int tabIndex)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 11),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    new MdiIcon(icon, 19, AppTheme.TextSecondary),
                    new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeight.Medium, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        btn.Click += (_, _) => SwitchTab(tabIndex);
        return btn;
    }

    private static void UpdateNavHighlight(Button btn, bool active)
    {
        btn.Background = active ? AppTheme.AccentSoft : Brushes.Transparent;
        if (btn.Content is StackPanel sp && sp.Children[0] is MdiIcon icon && sp.Children[1] is TextBlock tb)
        {
            icon.Foreground = active ? AppTheme.AccentBright : AppTheme.TextSecondary;
            tb.Foreground = active ? AppTheme.AccentBright : AppTheme.TextPrimary;
            tb.FontWeight = active ? FontWeight.Bold : FontWeight.Medium;
        }
    }

    private static Button CreateQuickCaptureBtn(string icon, string label, IBrush bg, IBrush fg, Action onClick)
    {
        var btn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = bg,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new MdiIcon(icon, 16, fg),
                    new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
