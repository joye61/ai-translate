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
    private readonly Action<DictionaryEntryResult, CapturedTextEvent> _onExpandToMain;

    private readonly TextBlock _headwordText;
    private readonly Border _phonicsHost;
    private readonly TextBlock _sourceTagText;
    private readonly TextBlock _primaryTranslationText;
    private readonly StackPanel _sensesPanel;
    private readonly TextBlock _mnemonicText;
    private readonly Border _mnemonicCard;
    private readonly Button _addNotebookBtn;
    private readonly MdiIcon _addNotebookIcon;
    private readonly TextBlock _addNotebookLabel;

    private CancellationTokenSource? _cts;
    private DictionaryEntryResult? _currentResult;
    private CapturedTextEvent? _currentCapture;

    public SelectionPopupWindow(
        IAiDictionaryService dictionaryService,
        IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService,
        AppSettingsStore settings,
        Action<DictionaryEntryResult, CapturedTextEvent> onExpandToMain)
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
        ShowInTaskbar = false;
        Background = Brushes.Transparent;

        _sourceTagText = new TextBlock
        {
            Text = "划词速译",
            FontFamily = AppTheme.BodyFont,
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppTheme.AccentBright,
            VerticalAlignment = VerticalAlignment.Center
        };

        _addNotebookIcon = new MdiIcon("notebook-plus", 15, Brushes.White);
        _addNotebookLabel = new TextBlock
        {
            Text = "加入生词本",
            FontFamily = AppTheme.BodyFont,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        _addNotebookBtn = new Button
        {
            Background = AppTheme.Accent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4),
            BorderThickness = new Thickness(0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { _addNotebookIcon, _addNotebookLabel }
            }
        };
        _addNotebookBtn.Click += (_, _) => AddCurrentToVocabulary();

        var expandBtn = CreateIconButton("book-open-variant", "在主界面深度打开", () =>
        {
            if (_currentResult is not null && _currentCapture is not null)
            {
                Hide();
                _onExpandToMain(_currentResult, _currentCapture);
            }
        });

        var copyBtn = CreateIconButton("content-copy", "复制译文", async () =>
        {
            if (_currentResult is not null && Clipboard is not null)
            {
                await Clipboard.SetTextAsync(_currentResult.PrimaryTranslation);
            }
        });

        var closeBtn = CreateIconButton("close", "关闭弹窗 (Esc)", Hide);

        var headerBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
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
        Grid.SetColumn(copyBtn, 3);
        Grid.SetColumn(expandBtn, 4);
        Grid.SetColumn(closeBtn, 5);
        headerBar.Children.Add(_addNotebookBtn);
        headerBar.Children.Add(copyBtn);
        headerBar.Children.Add(expandBtn);
        headerBar.Children.Add(closeBtn);

        _headwordText = new TextBlock
        {
            FontFamily = AppTheme.BodyFont,
            FontSize = 18,
            FontWeight = FontWeight.Bold,
            Foreground = AppTheme.TextPrimary,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3
        };

        _phonicsHost = new Border { IsVisible = false };

        _primaryTranslationText = new TextBlock
        {
            FontFamily = AppTheme.BodyFont,
            FontSize = 14.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = AppTheme.Emerald,
            TextWrapping = TextWrapping.Wrap
        };

        _sensesPanel = new StackPanel { Spacing = 4 };

        _mnemonicText = new TextBlock
        {
            FontFamily = AppTheme.BodyFont,
            FontSize = 12,
            Foreground = AppTheme.TextSecondary,
            TextWrapping = TextWrapping.Wrap
        };
        _mnemonicCard = new Border
        {
            Background = AppTheme.InputBg,
            BorderBrush = AppTheme.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 5,
                        Children =
                        {
                            new MdiIcon("lightbulb-on-outline", 14, AppTheme.Amber),
                            new TextBlock
                            {
                                Text = "AI 巧记与词源拆解",
                                FontFamily = AppTheme.BodyFont,
                                FontSize = 11.5,
                                FontWeight = FontWeight.SemiBold,
                                Foreground = AppTheme.Amber
                            }
                        }
                    },
                    _mnemonicText
                }
            }
        };

        var rootCard = new Border
        {
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderStrong,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    headerBar,
                    new Border { Height = 1, Background = AppTheme.BorderSubtle },
                    _headwordText,
                    _phonicsHost,
                    _primaryTranslationText,
                    _sensesPanel,
                    _mnemonicCard
                }
            }
        };

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

        Content = rootCard;
    }

    public async Task ShowAndTranslateAsync(CapturedTextEvent captured)
    {
        _currentCapture = captured;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var token = _cts.Token;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _sourceTagText.Text = captured.Source switch
            {
                TranslationTriggerSource.ClipboardMonitor => "剪贴板监听快译",
                TranslationTriggerSource.SelectionPopup => "划词即时翻译",
                _ => "AI 速查"
            };
            _headwordText.Text = captured.Text;
            _phonicsHost.IsVisible = false;
            _primaryTranslationText.Text = "正在通过 AI 引擎解析词义与语境…";
            _sensesPanel.Children.Clear();
            _mnemonicCard.IsVisible = false;

            var existing = _vocabularyService.FindByHeadword(captured.Text);
            UpdateNotebookButtonState(existing is not null);

            PositionNearCursor(captured.ScreenPoint);
            if (!IsVisible) Show();
            Activate();
        });

        try
        {
            var result = await _dictionaryService.LookupOrTranslateAsync(
                captured.Text,
                _settings.Current.DefaultTargetLanguage,
                contextSentence: null,
                cancellationToken: token);

            if (token.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _currentResult = result;
                _primaryTranslationText.Text = result.PrimaryTranslation;

                _phonicsHost.Child = PhonicsAudioBar.CreateWordPhonicsStrip(
                    _speechService,
                    _settings,
                    result.SourceText,
                    result.UsPhonetic,
                    result.UkPhonetic,
                    compact: true);
                _phonicsHost.IsVisible = true;

                _sensesPanel.Children.Clear();
                foreach (var sense in result.Senses.Take(3))
                {
                    _sensesPanel.Children.Add(new TextBlock
                    {
                        Text = $"{sense.PartOfSpeech}  {sense.DefinitionZh}",
                        FontFamily = AppTheme.BodyFont,
                        FontSize = 13,
                        Foreground = AppTheme.TextPrimary,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                var memoryHint = !string.IsNullOrWhiteSpace(result.MemoryMnemonic)
                    ? result.MemoryMnemonic
                    : result.EtymologyAndRoot;
                if (!string.IsNullOrWhiteSpace(memoryHint))
                {
                    _mnemonicText.Text = memoryHint;
                    _mnemonicCard.IsVisible = true;
                }

                if (_settings.Current.AutoAddLookedUpWordsToNotebook && result.ItemType == VocabularyItemType.Word)
                {
                    AddCurrentToVocabulary();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _primaryTranslationText.Text = $"翻译出错：{ex.Message}";
            });
        }
    }

    private void AddCurrentToVocabulary()
    {
        if (_currentResult is null) return;
        _vocabularyService.AddOrUpdateFromDictionary(
            _currentResult,
            notebookId: _settings.Current.DefaultNotebookId,
            contextSentence: _currentResult.Examples.FirstOrDefault()?.SourceSentence,
            source: _currentCapture?.Source ?? TranslationTriggerSource.SelectionPopup,
            sourceTitle: _currentCapture?.SourceWindowTitle ?? "");
        UpdateNotebookButtonState(true);
    }

    private void UpdateNotebookButtonState(bool saved)
    {
        _addNotebookIcon.IconName = saved ? "check" : "notebook-plus";
        _addNotebookLabel.Text = saved ? "已入生词本" : "加入生词本";
        _addNotebookBtn.Background = saved ? AppTheme.Emerald : AppTheme.Accent;
    }

    private void PositionNearCursor(PointD cursor)
    {
        var x = (int)cursor.X + 14;
        var y = (int)cursor.Y + 18;
        Position = new PixelPoint(Math.Max(16, x), Math.Max(16, y));
    }

    private static Button CreateIconButton(string icon, string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
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
