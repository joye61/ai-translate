using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
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
/// Lightweight floating popup window for Selection Translate (划词翻译) and Clipboard Translate (剪贴板快译).
/// Features instant translation, KokoroSharp US/UK/Phonics speech buttons, senses, one-click Add to Vocabulary Notebook, and "Expand in Main Dictionary".
/// </summary>
public sealed class SelectionPopupWindow : Window
{
    private readonly IAiDictionaryService _dictionaryService;
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speechService;
    private readonly AppSettingsStore _settings;
    private readonly Action<DictionaryEntryResult, CapturedTextEvent, string?, string?> _onExpandToMain;

    private readonly TextBlock _headwordText;
    private readonly ScrollViewer _bodyHost;
    private readonly TextBlock _sourceTagText;
    private readonly NotebookBookmarkButton _addNotebookBtn;

    private sealed class Lookup
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public List<VocabularyEntry> Collections { get; } = [];
    }

    private Lookup? _lookup;
    private DictionaryEntryResult? _bookmarkEntry;
    private DictionaryEntryResult? _currentResult;
    private CapturedTextEvent? _currentCapture;

    public SelectionPopupWindow(
        IAiDictionaryService dictionaryService,
        IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService,
        AppSettingsStore settings,
        Action<DictionaryEntryResult, CapturedTextEvent, string?, string?> onExpandToMain,
        IDesktopPlatformService? platform = null)
    {
        _dictionaryService = dictionaryService;
        _vocabularyService = vocabularyService;
        _speechService = speechService;
        _settings = settings;
        _onExpandToMain = onExpandToMain;

        Title = "AI 灵动划词翻译";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 460;
        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Background = Brushes.Transparent;

        _sourceTagText = TextStyles.Apply(new TextBlock
        {
            Text = "划词速译",
            VerticalAlignment = VerticalAlignment.Center
        }, TextRole.Label);

        _addNotebookBtn = new NotebookBookmarkButton(collect =>
        {
            if (_bookmarkEntry is not { } entry || _currentCapture is not { } capture) return;
            try
            {
                if (collect)
                {
                    var saved = _vocabularyService.AddOrUpdateFromDictionary(entry, _settings.Current.DefaultNotebookId,
                        source: capture.Source, sourceTitle: capture.SourceWindowTitle ?? "");
                    _lookup?.Collections.Add(saved);
                }
                else _vocabularyService.RemoveFromNotebook(entry, _settings.Current.DefaultNotebookId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _sourceTagText.Text = "词本保存失败";
                ToolTip.SetTip(_sourceTagText, ex.Message);
            }
            RefreshBookmark();
        });

        var expandBtn = CreateIconButton("book-open-variant", "在主界面深度打开", () => Expand(null, null));

        var copyBtn = CreateIconButton("content-copy", "复制译文", async () =>
        {
            if (_currentResult is not null && Clipboard is not null)
            {
                try { await Clipboard.SetTextAsync(_currentResult.PrimaryTranslation); }
                catch (System.Runtime.InteropServices.COMException) { _sourceTagText.Text = "剪贴板暂不可用，请再试一次"; }
            }
        });

        var closeBtn = CreateIconButton("close", "关闭弹窗 (Esc)", Hide);
        var refreshBtn = CreateIconButton("refresh", "重新用 AI 翻译", () =>
        {
            if (_currentCapture is { } capture && _currentResult is { } result)
                _ = ShowAndTranslateAsync(capture, forceRefresh: true, result.TargetLanguage);
        });

        var inset = AppTheme.ScrollContentInset;
        var headerBar = new Grid
        {
            Margin = inset,
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 6,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new MdiIcon("translate", 16, AppTheme.AccentBright),
                        _sourceTagText
                    }
                }
            }
        };
        Grid.SetColumn(_addNotebookBtn, 2);
        Grid.SetColumn(refreshBtn, 1);
        Grid.SetColumn(copyBtn, 3);
        Grid.SetColumn(expandBtn, 4);
        Grid.SetColumn(closeBtn, 5);
        headerBar.Children.Add(_addNotebookBtn);
        headerBar.Children.Add(refreshBtn);
        headerBar.Children.Add(copyBtn);
        headerBar.Children.Add(expandBtn);
        headerBar.Children.Add(closeBtn);

        _headwordText = TextStyles.Apply(new TextBlock
        {
            Margin = inset,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3
        }, TextRole.PageTitle);

        _bodyHost = new ScrollViewer
        {
            Padding = inset,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        var rootCard = new Border
        {
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderStrong,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(0, 14),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"),
                RowSpacing = 10,
                Children =
                {
                    headerBar,
                    new Border { [Grid.RowProperty] = 1, Height = 1, Margin = inset, Background = AppTheme.BorderSubtle },
                    _headwordText,
                    _bodyHost
                }
            }
        };
        Grid.SetRow(_headwordText, 2);
        Grid.SetRow(_bodyHost, 3);

        // Allow dragging popup by header
        headerBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
        };

        Deactivated += (_, _) => Hide();
        if (platform is not null)
        {
            PropertyChanged += (_, e) =>
            {
                if (e.Property != IsVisibleProperty) return;
                if (IsVisible) platform.PointerPressed += DismissOutside;
                else platform.PointerPressed -= DismissOutside;
            };
        }

        Content = rootCard;
    }

    private void DismissOutside(PointD point) => Dispatcher.UIThread.Post(() =>
    {
        if (IsVisible && !new Rect(ClientSize).Contains(this.PointToClient(new PixelPoint((int)point.X, (int)point.Y))))
            Hide();
    });

    public async Task ShowAndTranslateAsync(CapturedTextEvent captured, bool forceRefresh = false, string? targetLanguage = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(() => ShowAndTranslateAsync(captured, forceRefresh, targetLanguage));
            return;
        }

        var query = DictionaryQuery.CleanInput(captured.Text);
        if (query.Length == 0) return;
        captured = captured with { Text = query };
        // A collected lookup still owes its notebook entry the final details after the popup moves on.
        if (_lookup is { Collections.Count: 0 } old) old.Cancellation.Cancel();
        var request = new Lookup();
        using var cancellation = request.Cancellation;
        _lookup = request;
        var token = cancellation.Token;
        var started = DateTime.UtcNow.Ticks;
        var previous = forceRefresh && _currentCapture == captured ? _currentResult : null;
        _currentResult = null;
        _bookmarkEntry = null;
        _currentCapture = captured;

        _sourceTagText.Text = captured.Source switch
        {
            TranslationTriggerSource.ClipboardMonitor => "剪贴板监听快译",
            TranslationTriggerSource.SelectionPopup => "划词即时翻译",
            _ => "AI 速查"
        };
        ToolTip.SetTip(_sourceTagText, null);
        _headwordText.Text = captured.Text;
        var body = new SelectionPopupBody(_speechService, _settings, Expand);
        _bodyHost.Content = body;
        _bodyHost.ScrollToHome();

        RefreshBookmark();

        PositionNearCursor(captured.ScreenPoint);
        if (!IsVisible) Show();

        try
        {
            var result = await _dictionaryService.LookupOrTranslateAsync(
                captured.Text,
                targetLanguage ?? DictionaryTranslationDirection.ResolveTargetId(captured.Text, DictionaryTranslationDirection.GetMode(_settings.Current)),
                contextSentence: null,
                cancellationToken: token,
                onEntryPreview: partial => Dispatcher.UIThread.Post(() =>
                {
                    if (_lookup == request) body.ShowEntry(partial, complete: false);
                }),
                forceRefresh: forceRefresh,
                onUsableEntry: entry => Dispatcher.UIThread.Post(() =>
                {
                    if (_lookup != request) return;
                    _bookmarkEntry = entry;
                    body.ShowEntry(entry, complete: false);
                    RefreshBookmark();
                }));

            foreach (var collected in request.Collections)
                _vocabularyService.CompleteCollection(collected, result, result.CacheStamp?.Revision ?? started);
            if (_lookup != request) return;

            ShowResult(result, body);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_lookup != request) return;
            if ((previous ?? _bookmarkEntry) is not { } usable) body.ShowError($"翻译出错：{ex.Message}");
            else
            {
                ShowResult(usable, body);
                _sourceTagText.Text = previous is null ? "详情未完成" : "刷新失败";
                ToolTip.SetTip(_sourceTagText, ex.Message);
            }
        }
        finally
        {
            if (_lookup == request) _lookup = null;
        }
    }

    private void Expand(string? translation, string? context)
    {
        if (_currentResult is null || _currentCapture is null) return;
        Hide();
        _onExpandToMain(_currentResult, _currentCapture, translation, context);
    }

    private void ShowResult(DictionaryEntryResult result, SelectionPopupBody body)
    {
        _currentResult = result;
        _bookmarkEntry = result;
        RefreshBookmark();
        if (result.Origin == DictionaryResultOrigin.Cache) _sourceTagText.Text = "本地缓存";
        body.ShowEntry(result, complete: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lookup?.Cancellation.Cancel();
        _lookup = null;
        base.OnClosed(e);
    }

    internal void RefreshBookmark()
    {
        _addNotebookBtn.IsEnabled = _bookmarkEntry is not null;
        _addNotebookBtn.SetSaved(_bookmarkEntry is not null &&
            _vocabularyService.IsCollected(_bookmarkEntry, _settings.Current.DefaultNotebookId));
    }

    private void PositionNearCursor(PointD cursor)
    {
        var x = (int)cursor.X + 14;
        var y = (int)cursor.Y + 18;

        // Clamp against the bounds of the monitor the cursor is actually on, not a hardcoded (0,0)-based
        // minimum: secondary monitors placed above/left of the primary have negative origin coordinates,
        // so Math.Max(16, ...) against a global floor would yank the popup onto the wrong monitor.
        var bounds = Screens?.ScreenFromPoint(new PixelPoint((int)cursor.X, (int)cursor.Y))?.WorkingArea;
        if (bounds is { } area)
        {
            x = Math.Clamp(x, area.X + 8, Math.Max(area.X + 8, area.Right - 8));
            y = Math.Clamp(y, area.Y + 8, Math.Max(area.Y + 8, area.Bottom - 8));
        }

        Position = new PixelPoint(x, y);
    }

    private static Button CreateIconButton(string icon, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Classes = { "ghost" },
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(AppTheme.Radius.S),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new MdiIcon(icon, 16, AppTheme.TextSecondary)
        };
        ToolTip.SetTip(btn, tooltip);
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
