using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// Immersive FSRS Spaced Repetition Arena (FSRS 科学记忆训练场).
/// Supports Flashcard Flip, Context Cloze Deletion, Spelling Dictation, and Listening Dictation (听音拼写)
/// with real-time FSRS interval previews and KokoroSharp neural speech.
/// </summary>
public sealed class FsrsReviewArenaView : UserControl
{
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speechService;
    private readonly AppSettingsStore _settingsStore;
    private readonly Action<string> _setStatus;

    private const double StageWidth = 760;
    private readonly ComboBox _modeCombo;
    private readonly ComboBox _deckCombo;
    private readonly TextBlock _progressText;
    private readonly TextBlock _progressMeta;
    private readonly ProgressBar _progressBar;
    private readonly StackPanel _cardStage;
    internal Control Toolbar { get; }

    private IReadOnlyList<VocabularyEntry> _queue = [];
    private int _currentIndex;
    private bool _answerRevealed;
    private bool _refreshingDecks;

    public FsrsReviewArenaView(
        IVocabularyService vocabularyService,
        ISpeechSynthesisService speechService,
        AppSettingsStore settingsStore,
        Action<string> setStatus)
    {
        _vocabularyService = vocabularyService;
        _speechService = speechService;
        _settingsStore = settingsStore;
        _setStatus = setStatus;

        _modeCombo = new ComboBox
        {
            Width = 132,
            ItemsSource = new[] { "闪卡回忆", "语境填空", "看义拼写", "听音默写" },
            SelectedIndex = 0
        };
        _modeCombo.SelectionChanged += (_, _) => RenderCard();

        _deckCombo = new ComboBox { Width = 180 };
        _deckCombo.SelectionChanged += (_, _) => { if (!_refreshingDecks) StartSession(); };

        _progressText = TextStyles.Apply(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, TextRole.SectionTitle);
        _progressMeta = TextStyles.Apply(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, TextRole.Meta);
        _progressBar = new ProgressBar { Height = 3, MinHeight = 3, Minimum = 0, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, AppTheme.Space.S, 0, 0) };

        Toolbar = new WrapPanel
        {
            ItemSpacing = AppTheme.Space.XL, LineSpacing = AppTheme.Space.S,
            Children =
            {
                StudioUi.CreateToolbarField("模式", _modeCombo),
                StudioUi.CreateToolbarField("词本", _deckCombo)
            }
        };
        var refresh = StudioUi.CreateSecondaryButton("refresh", "刷新队列", StartSession);
        Grid.SetColumn(refresh, 1);
        Grid.SetRowSpan(refresh, 2);
        Grid.SetRow(_progressBar, 1);
        var topBar = new Grid
        {
            MaxWidth = StageWidth,
            Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.M),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = AppTheme.Space.L,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.M, VerticalAlignment = VerticalAlignment.Center,
                    Children = { _progressText, _progressMeta }
                },
                _progressBar, refresh
            }
        };

        // Top-anchored, same reading width as the dictionary: no vertically centered card floating in a void.
        _cardStage = new StackPanel
        {
            MaxWidth = StageWidth,
            Spacing = AppTheme.Space.L,
            Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.XXL),
            VerticalAlignment = VerticalAlignment.Top
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                topBar,
                new ScrollViewer { [Grid.RowProperty] = 1, Content = _cardStage }
            }
        };
    }

    public void RefreshDecksAndStart(string? notebookId = null)
    {
        var notebooks = _vocabularyService.GetNotebooks();
        var allOption = new VocabularyNotebook { Id = "all", Name = "全部词本" };
        var list = new[] { allOption }.Concat(notebooks).ToArray();
        var prevId = notebookId ?? (_deckCombo.SelectedItem as VocabularyNotebook)?.Id ?? "all";
        _refreshingDecks = true;
        try
        {
            _deckCombo.ItemsSource = list;
            _deckCombo.SelectedItem = list.FirstOrDefault(n => n.Id == prevId) ?? allOption;
            _deckCombo.ItemTemplate = new FuncDataTemplate<VocabularyNotebook>((n, _) => new TextBlock { Text = n?.Name });
        }
        finally { _refreshingDecks = false; }
        StartSession();
    }

    public void StartSession()
    {
        var deckId = (_deckCombo.SelectedItem as VocabularyNotebook)?.Id ?? "all";
        var due = _vocabularyService.GetDueQueue(deckId, maxCount: 50);
        if (due.Count == 0)
        {
            due = _vocabularyService.QueryEntries(new VocabularyQueryFilter
            {
                NotebookId = deckId,
                SortOrder = VocabularySortOrder.NextReviewDue
            });
        }
        _queue = due;
        _currentIndex = 0;
        _answerRevealed = false;
        RenderCard();
    }

    private void RenderCard()
    {
        _cardStage.Children.Clear();
        if (_queue.Count == 0 || _currentIndex >= _queue.Count)
        {
            _progressText.Text = "本轮复习已完成";
            _progressMeta.Text = _queue.Count == 0 ? "当前词本还没有词条" : $"共 {_queue.Count} 张";
            _progressBar.Maximum = Math.Max(1, _queue.Count);
            _progressBar.Value = _progressBar.Maximum;
            _cardStage.Children.Add(StudioUi.CreateHeroCard(new StackPanel
            {
                Spacing = AppTheme.Space.M,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(AppTheme.Space.XXL, AppTheme.Space.XXL),
                Children =
                {
                    new MdiIcon("check-all", 44, AppTheme.Emerald) { HorizontalAlignment = HorizontalAlignment.Center },
                    Centered(TextStyles.Create("待复习的卡片都完成了", TextRole.PageTitle)),
                    Centered(TextStyles.Create("FSRS 已按你的反馈安排好下次复习时间。", TextRole.Secondary)),
                    Centered(StudioUi.CreatePrimaryButton("refresh", "再练一轮", StartSession))
                }
            }));
            return;
        }

        var entry = _queue[_currentIndex];
        _progressText.Text = $"第 {_currentIndex + 1} / {_queue.Count} 张";
        _progressMeta.Text = $"{UiLabels.Stage(entry.Stage)} · 稳定度 {entry.StabilityDays:F1} 天";
        _progressBar.Maximum = _queue.Count;
        _progressBar.Value = _currentIndex;

        var modeIndex = Math.Clamp(_modeCombo.SelectedIndex, 0, 3);
        var cardStack = new StackPanel { Spacing = AppTheme.Space.L, Margin = new Thickness(AppTheme.Space.XS) };
        var cloze = modeIndex == 1 ? SentenceCloze.ForReview(entry) : null;
        if (modeIndex == 1 && cloze is null)
        {
            cardStack.Children.Add(TextStyles.Create("这张卡缺少可挖空的原句，本题用闪卡回忆。", TextRole.Meta));
            modeIndex = 0;
        }
        var mode = modeIndex >= 2 ? ReviewQuizMode.SpellingDictation : (ReviewQuizMode)modeIndex;

        if (modeIndex == 0) // Flashcard
        {
            var headword = TextStyles.Create(entry.Headword, TextRole.Display);
            headword.TextAlignment = TextAlignment.Center;
            cardStack.Children.Add(headword);
            var audioBar = PhonicsAudioBar.CreateWordPhonicsStrip(
                _speechService,
                _settingsStore,
                entry.Headword,
                entry.UsPhonetic,
                entry.UkPhonetic,
                entry.ContextSentence);
            audioBar.HorizontalAlignment = HorizontalAlignment.Center;
            cardStack.Children.Add(audioBar);
            if (!string.IsNullOrWhiteSpace(entry.ContextSentence))
            {
                cardStack.Children.Add(StudioUi.CreateInfoTile("收录时的语境", entry.ContextSentence, "format-quote-close"));
            }
        }
        else if (cloze is not null)
        {
            cardStack.Children.Add(TextStyles.Create("回忆原句中的表达", TextRole.FieldLabel));
            cardStack.Children.Add(TextStyles.Create(_answerRevealed ? cloze.Source : cloze.Masked, TextRole.PageTitle));
            if (_answerRevealed)
                cardStack.Children.Add(StudioUi.CreateInfoTile("原句用词", $"{cloze.Answer}\n{cloze.Translation}".TrimEnd()));
        }
        else // Spelling Quiz or Listening Dictation (盲听发音默写)
        {
            var isListeningMode = modeIndex == 3;
            var usPlayback = new SpeechPlayback();
            if (isListeningMode)
            {
                _ = _speechService.SpeakAsync(
                    entry.Headword,
                    SpeechAccent.AmericanEnglish,
                    entry.UsPhonetic,
                    _settingsStore.Current.SpeechSpeed,
                    _settingsStore.Current.PreferredUsVoiceId, playback: usPlayback);
            }
            cardStack.Children.Add(TextStyles.Create(isListeningMode
                ? "听发音，输入你听到的词条，按 Enter 核对"
                : "看释义，拼写对应的词条，按 Enter 核对", TextRole.FieldLabel));
            cardStack.Children.Add(PhonicsAudioBar.CreateWordPhonicsStrip(
                _speechService,
                _settingsStore,
                entry.Headword,
                entry.UsPhonetic,
                entry.UkPhonetic,
                entry.ContextSentence, usPlayback: usPlayback));
            if (!isListeningMode || _answerRevealed)
                cardStack.Children.Add(TextStyles.Create(entry.Translation, TextRole.Translation));
            var spellInput = new TextBox
            {
                PlaceholderText = "在此输入拼写并按 Enter 键检阅…",
                FontSize = 18,
                FontFamily = AppTheme.MonoFont,
                Background = AppTheme.InputBg,
                Foreground = AppTheme.TextPrimary,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10)
            };
            spellInput.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    var typed = (spellInput.Text ?? "").Trim();
                    var ok = string.Equals(typed, entry.Headword, StringComparison.OrdinalIgnoreCase);
                    _setStatus(ok ? $"✓ 拼写完全正确：{entry.Headword}" : $"✗ 拼写偏差，正确拼写为：{entry.Headword}");
                    _answerRevealed = true;
                    RenderCard();
                }
            };
            cardStack.Children.Add(spellInput);
        }

        if (!_answerRevealed)
        {
            var revealBtn = StudioUi.CreatePrimaryButton("eye-outline", "显示释义与助记", () =>
            {
                _answerRevealed = true;
                RenderCard();
            });
            revealBtn.HorizontalAlignment = HorizontalAlignment.Center;
            cardStack.Children.Add(revealBtn);
        }
        else
        {
            cardStack.Children.Add(new Border { Height = 1, Background = AppTheme.BorderSubtle });
            var answer = new WrapPanel { ItemSpacing = AppTheme.Space.M, LineSpacing = AppTheme.Space.XXS };
            var answerHead = TextStyles.Create(entry.Headword, TextRole.PageTitle);
            answerHead.VerticalAlignment = VerticalAlignment.Center;
            var answerMeaning = TextStyles.Create(entry.Translation, TextRole.Translation);
            answerMeaning.VerticalAlignment = VerticalAlignment.Center;
            answer.Children.Add(answerHead);
            answer.Children.Add(answerMeaning);
            cardStack.Children.Add(answer);
            if (DictionaryInsightBlocks.WordFormation(entry.Morphemes, entry.EtymologyAndRoot, entry.Headword) is { } formation)
                cardStack.Children.Add(StudioUi.CreateInfoTile("构词与词源", formation, "code-braces", AppTheme.Insight));
            if (DictionaryInsightBlocks.Note(entry.MemoryMnemonic, "lightbulb-on-outline", RichBlocks.CalloutTone.Attention) is { } mnemonic)
                cardStack.Children.Add(mnemonic);

            var intervals = SpacedRepetitionScheduler.PreviewIntervals(entry, DateTimeOffset.UtcNow);
            var gradeGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
                ColumnSpacing = AppTheme.Space.S,
                Margin = new Thickness(0, AppTheme.Space.XS, 0, 0)
            };

            void Submit(ReviewRating r)
            {
                var updated = _vocabularyService.SubmitReview(entry.Id, r, mode);
                _setStatus($"已记录「{updated.Headword}」反馈 ({UiLabels.Rating(r)}) · 下次复习间隔已更新");
                _currentIndex++;
                _answerRevealed = false;
                RenderCard();
            }

            var grades = new[] { ReviewRating.Again, ReviewRating.Hard, ReviewRating.Good, ReviewRating.Easy }
                .Select(r => (r, r switch
                {
                    ReviewRating.Again => intervals.AgainInterval,
                    ReviewRating.Hard => intervals.HardInterval,
                    ReviewRating.Good => intervals.GoodInterval,
                    _ => intervals.EasyInterval
                }, HueMap.Rating(r))).ToArray();
            for (var i = 0; i < grades.Length; i++)
            {
                var (rating, interval, hue) = grades[i];
                var button = CreateGradeButton(UiLabels.Rating(rating), SpacedRepetitionScheduler.FormatInterval(interval), hue, () => Submit(rating));
                Grid.SetColumn(button, i);
                gradeGrid.Children.Add(button);
            }
            cardStack.Children.Add(gradeGrid);
        }

        _cardStage.Children.Add(StudioUi.CreateHeroCard(cardStack));
    }

    private static T Centered<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        if (control is TextBlock text) text.TextAlignment = TextAlignment.Center;
        return control;
    }

    private static Button CreateGradeButton(string title, string interval, Hue hue, Action onClick)
    {
        var label = TextStyles.Create(title, TextRole.SectionTitle, wrap: false);
        label.Foreground = hue.Ink;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var next = TextStyles.Create($"下次 {interval}", TextRole.Meta, wrap: false);
        next.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new Border
        {
            Width = 22, Height = 3, CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = hue.Solid,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 4)
        };
        var btn = new Button
        {
            Classes = { "ghost" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = AppTheme.CardBg,
            BorderBrush = AppTheme.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.S, AppTheme.Space.M - 2),
            Content = new StackPanel { Spacing = 2, Children = { bar, label, next } }
        };
        Avalonia.Automation.AutomationProperties.SetName(btn, $"{title}，下次 {interval}");
        btn.Click += (_, _) => onClick();
        return btn;
    }
}
