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
    private readonly ContentControl _pageTools = new();

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

        Title = "AI Translate · 翻译工作台";
        Icon = AppIcons.CreateWindowIcon();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowActivated = true;
        FitToWorkingArea(new Size(1040, 840));
        Background = AppTheme.WindowBg;
        FontFamily = AppTheme.BodyFont;

        _statusFooterText = new TextBlock
        {
            Text = "就绪",
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = AppTheme.Type.Caption,
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
                var entry = new DictionaryEntryResult
                {
                    SourceText = source,
                    PrimaryTranslation = translated,
                    TargetLanguage = _settingsStore.Current.DefaultTargetLanguage,
                    ItemType = DictionaryQuery.SuggestType(source),
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

        _selectionPopup = new SelectionPopupWindow(
            dictionaryService,
            _vocabularyService,
            _speechService,
            _settingsStore,
            onExpandToMain: (result, captured, translation, context) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ShowAndActivate();
                    SwitchTab(0);
                    if (translation is null)
                        _ = _dictionaryView!.OpenDetailsAsync(result, captured.Source, captured.SourceWindowTitle);
                    else
                    {
                        _dictionaryView!.DisplayResult(result, captured.Source, captured.SourceWindowTitle);
                        _dictionaryView.LookupRelated(translation, context ?? "", fromTranslation: true);
                    }
                });
            }, _platform);

        // Instantiate modular Views
        _dictionaryView = new DictionaryStudioView(
            dictionaryService,
            _vocabularyService,
            _speechService,
            _settingsStore,
            SetStatus);

        _vocabularyView = new VocabularyVaultView(
            _vocabularyService,
            _speechService,
            _settingsStore,
            onLookupInStudio: (word, ctx, src, title, target) =>
            {
                SwitchTab(0);
                _ = _dictionaryView.LookupAsync(word, ctx, src, title, target);
            },
            onCollect: notebookId =>
            {
                _dictionaryView.RefreshNotebooks(notebookId);
                SwitchTab(0);
            },
            onSwitchToReview: notebookId => SwitchTab(2, notebookId),
            SetStatus, dictionaryService);

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

        _navDictBtn = CreateNavButton("translate", "查词与翻译", 0);
        _navVocabBtn = CreateNavButton("book-open-page-variant", "生词本", 1);
        _navReviewBtn = CreateNavButton("brain", "生词复习", 2);
        _navSettingsBtn = CreateNavButton("cog-outline", "引擎与快捷键", 3);

        _dueBadgeText = new TextBlock { Text = "0", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        _dueBadgeBorder = new Border
        {
            Background = AppTheme.Accent,
            IsHitTestVisible = false,
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

        var windowButtons = WindowTitleBar.CreateButtons(this);
        var mac = OperatingSystem.IsMacOS();
        var sidebar = BuildSidebar(mac ? windowButtons : null);
        var contentHost = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                WindowTitleBar.CreateWorkspaceToolbar(this, _pageTools, mac ? null : windowButtons),
                new Panel
                {
                    [Grid.RowProperty] = 1,
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
                    [Grid.RowProperty] = 2,
                    Padding = new Thickness(AppTheme.Space.XXL, AppTheme.Space.XS, AppTheme.Space.XXL, AppTheme.Space.M),
                    Child = new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        ColumnSpacing = AppTheme.Space.L,
                        Children =
                        {
                            _statusFooterText,
                            new TextBlock
                            {
                                [Grid.ColumnProperty] = 1,
                                Text = $"托盘常驻   ·   {_settingsStore.Current.ShowWindowHotkey} 呼出",
                                FontSize = AppTheme.Type.Caption,
                                Foreground = AppTheme.TextSecondary
                            }
                        }
                    }
                }
            }
        };

        var rootGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("232,*"),
            Children = { sidebar, contentHost }
        };
        Grid.SetColumn(contentHost, 1);
        WindowTitleBar.SetContent(this, rootGrid);

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
        Opened += (_, _) => _dictionaryView.FocusInput();
    }

    private void FitToWorkingArea(Size requested, bool recenter = false)
    {
        // Match CenterScreen's monitor selection, including a fallback after a display is disconnected.
        var screen = Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        var available = Size.Infinity;
        if (screen?.WorkingArea is { Width: > 0, Height: > 0 } workArea)
        {
            var workSize = workArea.Size.ToSize(screen.Scaling);
            // Leave room for the resize frame and a visible desktop margin on small screens.
            available = new Size(Math.Max(1, workSize.Width - 2 * AppTheme.Space.L),
                Math.Max(1, workSize.Height - 2 * AppTheme.Space.L));
        }
        MinWidth = Math.Min(1020, available.Width);
        MinHeight = Math.Min(680, available.Height);
        Width = Math.Clamp(requested.Width, MinWidth, available.Width);
        Height = Math.Clamp(requested.Height, MinHeight, available.Height);
        if (recenter && screen?.WorkingArea is { Width: > 0, Height: > 0 } bounds)
        {
            // Avalonia applies WindowStartupLocation only on first show. Include the resize frame on later opens.
            var frame = (FrameSize ?? ClientSize) - ClientSize;
            var pixels = PixelSize.FromSize(new Size(Width, Height) + frame, screen.Scaling);
            Position = bounds.CenterRect(new PixelRect(pixels)).Position;
        }
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
        _dictionaryView.StopLookup();
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

    private Border BuildSidebar(Control? leadingButtons)
    {
        var wordmark = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Space.S,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(leadingButtons is null ? AppTheme.Space.XL : AppTheme.Space.S, 0, AppTheme.Space.M, 0),
            Children =
            {
                AppIcons.CreateLogo(32),
                new TextBlock
                {
                    Text = "AI Translate",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = AppTheme.Type.Title,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = AppTheme.TextPrimary
                }
            }
        };
        var brandBox = new DockPanel { Height = 64 };
        if (leadingButtons is not null)
        {
            DockPanel.SetDock(leadingButtons, Dock.Left);
            brandBox.Children.Add(leadingButtons);
        }
        brandBox.Children.Add(WindowTitleBar.CreateDragArea(this, wordmark));

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
            Text = "快捷捕获",
            FontSize = AppTheme.Type.Caption,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppTheme.TextMuted,
            Margin = new Thickness(18, 20, 18, 8)
        };

        var captureStack = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(12, 0),
            Children =
            {
                CreateQuickCaptureBtn("crop-free", "截图翻译", AppTheme.Hues.Sky, _settingsStore.Current.ScreenshotTranslateHotkey.ToString(), () => _ = _screenshotCoordinator.StartCaptureAndTranslateAsync()),
                CreateQuickCaptureBtn("cursor-text", "划词翻译", AppTheme.Hues.Jade, _settingsStore.Current.SelectionTranslateHotkey.ToString(), () => _ = TriggerSelectionHotkeyAsync()),
                CreateQuickCaptureBtn("clipboard-text-outline", "剪贴板翻译", AppTheme.Hues.Violet, _settingsStore.Current.ClipboardTranslateHotkey.ToString(), () => _ = TranslateClipboardNowAsync())
            }
        };

        var monitorCard = new Border
        {
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.L),
            Padding = new Thickness(14, 12),
            Margin = new Thickness(12, 12, 12, 14),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "自动捕获", FontSize = AppTheme.Type.Caption, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextSecondary },
                    _clipboardMonitorToggle,
                    _selectionMonitorToggle
                }
            }
        };

        var dock = new DockPanel
        {
            Children = { brandBox, navStack, captureHeader, captureStack, monitorCard, WindowTitleBar.CreateDragArea(this) }
        };
        DockPanel.SetDock(brandBox, Dock.Top);
        DockPanel.SetDock(navStack, Dock.Top);
        DockPanel.SetDock(captureHeader, Dock.Top);
        DockPanel.SetDock(captureStack, Dock.Top);
        DockPanel.SetDock(monitorCard, Dock.Bottom);

        return new Border
        {
            Background = AppTheme.SidebarBg,
            Child = dock
        };
    }

    private void OnVocabularyChanged()
    {
        var stats = _vocabularyService.GetDashboardStats();
        _dueBadgeText.Text = stats.DueTodayCount.ToString();
        _dueBadgeBorder.IsVisible = stats.DueTodayCount > 0;
        _dictionaryView.RefreshNotebooks();
        _selectionPopup.RefreshBookmark();
        _vocabularyView.RefreshAll();
    }

    public void SwitchTab(int index, string? reviewNotebookId = null)
    {
        _pageTools.Content = index switch
        {
            0 => new TextBlock
            {
                Text = "查词与翻译", FontSize = AppTheme.Type.Title,
                FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextPrimary
            },
            1 => _vocabularyView.Toolbar,
            2 => _reviewView.Toolbar,
            3 => _settingsView.Toolbar,
            _ => null
        };
        _dictionaryView.IsVisible = index == 0;
        _vocabularyView.IsVisible = index == 1;
        _reviewView.IsVisible = index == 2;
        _settingsView.IsVisible = index == 3;

        UpdateNavHighlight(_navDictBtn, index == 0);
        UpdateNavHighlight(_navVocabBtn, index == 1);
        UpdateNavHighlight(_navReviewBtn, index == 2);
        UpdateNavHighlight(_navSettingsBtn, index == 3);

        if (index == 0 && IsVisible) _dictionaryView.FocusInput();
        if (index == 1) _vocabularyView.RefreshAll();
        if (index == 2) _reviewView.RefreshDecksAndStart(reviewNotebookId);
    }

    private void RegisterGlobalHotkeys()
    {
        var s = _settingsStore.Current;
        _platform.TryRegisterHotkey(GlobalHotkeyId.ShowMainWindow, s.ShowWindowHotkey, () => Dispatcher.UIThread.Post(ShowAndActivate), out _);
        _platform.TryRegisterHotkey(GlobalHotkeyId.SelectionTranslate, s.SelectionTranslateHotkey,
            () => Dispatcher.UIThread.Post(() => _ = TriggerSelectionHotkeyAsync()), out _);
        _platform.TryRegisterHotkey(GlobalHotkeyId.ScreenshotTranslate, s.ScreenshotTranslateHotkey, () => _ = _screenshotCoordinator.StartCaptureAndTranslateAsync(), out _);
        _platform.TryRegisterHotkey(GlobalHotkeyId.ClipboardTranslate, s.ClipboardTranslateHotkey, () => _ = TranslateClipboardNowAsync(), out _);
    }

    private async Task TriggerSelectionHotkeyAsync()
    {
        var captured = await _platform.TryCaptureSelectedTextAsync();
        if (captured is not null && DictionaryQuery.HasContent(captured.Text))
        {
            await _selectionPopup.ShowAndTranslateAsync(captured);
            return;
        }
        // No selection was captured — tell the user instead of silently translating a hardcoded
        // demo word ("serendipity"), which previously masked capture failures as if they'd
        // succeeded on an unrelated word.
        SetStatus("未检测到选中的文本，请先在任意应用中选中文字后再按划词快捷键。");
    }

    private async Task TranslateClipboardNowAsync()
    {
        string? text = null;
        if (Clipboard is not null)
            text = await Clipboard.TryGetTextAsync();
        if (string.IsNullOrWhiteSpace(text) && OperatingSystem.IsWindows())
            text = WindowsDesktopPlatformService.TryReadClipboardUnicodeText();
        if (!DictionaryQuery.HasContent(text))
        {
            SetStatus("当前剪贴板中没有可翻译的文本内容。");
            return;
        }
        ShowAndActivate();
        SwitchTab(0);
        await _dictionaryView.LookupAsync(text, null, TranslationTriggerSource.ClipboardMonitor, "剪贴板读取");
    }

    public void ShowAndActivate()
    {
        var reopening = !IsVisible || WindowState == WindowState.Minimized;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (reopening && WindowState == WindowState.Normal)
            FitToWorkingArea(new Size(Width, Height), recenter: true);
        if (!IsVisible) Show();
        Activate();
    }

    private void SetStatus(string message) => _statusFooterText.Text = message;

    private Button CreateNavButton(string icon, string title, int tabIndex)
    {
        var btn = new Button
        {
            Classes = { "nav" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 11),
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
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
        btn.Classes.Set("active", active);
        if (btn.Content is StackPanel sp && sp.Children[0] is MdiIcon icon && sp.Children[1] is TextBlock tb)
        {
            icon.Foreground = active ? AppTheme.AccentBright : AppTheme.TextSecondary;
            tb.Foreground = active ? AppTheme.AccentBright : AppTheme.TextPrimary;
            tb.FontWeight = active ? FontWeight.SemiBold : FontWeight.Medium;
        }
    }

    private static Button CreateQuickCaptureBtn(string icon, string label, Hue hue, string shortcut, Action onClick)
    {
        var keycap = StudioUi.CreateKeycap(shortcut);
        Grid.SetColumn(keycap, 2);
        var btn = new Button
        {
            Classes = { "nav" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Padding = new Thickness(10, 6),
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 10,
                Children =
                {
                    new Border
                    {
                        Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = hue.Soft,
                        Child = new MdiIcon(icon, 14, hue.Ink)
                    },
                    new TextBlock { [Grid.ColumnProperty] = 1, Text = label, FontSize = 12.5, FontWeight = FontWeight.Medium, Foreground = AppTheme.TextPrimary, VerticalAlignment = VerticalAlignment.Center },
                    keycap
                }
            }
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
