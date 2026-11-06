using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// Read saved entries and rate their familiarity to schedule the next review.
/// </summary>
public sealed class FsrsReviewArenaView : UserControl
{
    private readonly IVocabularyService _vocabularyService;
    private readonly ISpeechSynthesisService _speechService;
    private readonly AppSettingsStore _settingsStore;
    private readonly Action<string> _setStatus;

    private const double StageWidth = 760;
    private readonly ComboBox _deckCombo;
    private readonly TextBlock _progressText;
    private readonly TextBlock _progressMeta;
    private readonly ProgressBar _progressBar;
    private readonly StackPanel _cardStage;
    internal Control Toolbar { get; }

    private IReadOnlyList<VocabularyEntry> _queue = [];
    private int _currentIndex;
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

        _deckCombo = new ComboBox { Width = 180 };
        _deckCombo.SelectionChanged += (_, _) => { if (!_refreshingDecks) StartSession(); };

        _progressText = TextStyles.Apply(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, TextRole.SectionTitle);
        _progressMeta = TextStyles.Apply(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, TextRole.Meta);
        _progressBar = new ProgressBar { Height = 3, MinHeight = 3, Minimum = 0, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, AppTheme.Space.S, 0, 0) };

        Toolbar = StudioUi.CreateToolbarField("词本", _deckCombo);
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
                    Centered(StudioUi.CreatePrimaryButton("refresh", "再复习一轮", StartSession))
                }
            }));
            return;
        }

        var entry = _queue[_currentIndex];
        _progressText.Text = $"第 {_currentIndex + 1} / {_queue.Count} 张";
        _progressMeta.Text = $"{UiLabels.Stage(entry.Stage)} · 稳定度 {entry.StabilityDays:F1} 天";
        _progressBar.Maximum = _queue.Count;
        _progressBar.Value = _currentIndex;

        var cardStack = new StackPanel { Spacing = AppTheme.Space.L, Margin = new Thickness(AppTheme.Space.XS) };
        cardStack.Children.Add(TextStyles.Selectable(entry.Headword, TextStyles.HeadwordRole(entry.Headword)));
        cardStack.Children.Add(TextStyles.Selectable(entry.Translation, entry.ItemType.IsPassage() ? TextRole.PassageTranslation : TextRole.Translation));
        var accent = PhonicsAudioBar.SourceAccent(entry.SourceLanguage);
        if (accent == SpeechAccent.AmericanEnglish && !entry.ItemType.IsPassage())
            cardStack.Children.Add(PhonicsAudioBar.CreateWordPhonicsStrip(_speechService, _settingsStore, entry.Headword,
                entry.UsPhonetic, entry.UkPhonetic, entry.ContextSentence));
        else if (accent is { } voice)
            cardStack.Children.Add(PhonicsAudioBar.CreateSentenceReadButton(_speechService, _settingsStore, entry.Headword, "朗读原文", voice));

        if (!string.IsNullOrWhiteSpace(entry.ContextSentence))
        {
            var context = new StackPanel { Spacing = AppTheme.Space.XXS };
            context.Children.Add(TextStyles.Create("收录时的语境", TextRole.Label));
            context.Children.Add(TextStyles.Selectable(entry.ContextSentence, TextRole.ExampleSource));
            if (!string.IsNullOrWhiteSpace(entry.ContextTranslation))
                context.Children.Add(TextStyles.Selectable(entry.ContextTranslation, TextRole.ExampleTarget));
            cardStack.Children.Add(RichBlocks.Quote(context));
        }
        if (DictionaryInsightBlocks.Combine(DictionaryInsightBlocks.WordFormation(entry.Morphemes, entry.EtymologyAndRoot, entry.Headword),
            DictionaryInsightBlocks.Explanations(entry.EtymologyNotes)) is { } formation)
            cardStack.Children.Add(StudioUi.CreateInfoTile("构词与词源", formation, "code-braces", AppTheme.Insight));
        if (DictionaryInsightBlocks.Combine(DictionaryInsightBlocks.Note(entry.MemoryMnemonic, "lightbulb-on-outline", RichBlocks.CalloutTone.Attention),
            DictionaryInsightBlocks.Mnemonics(entry.Mnemonics)) is { } mnemonic)
            cardStack.Children.Add(mnemonic);

        var intervals = SpacedRepetitionScheduler.PreviewIntervals(entry, DateTimeOffset.UtcNow);
        var gradeGrid = new AdaptiveColumns(4, 96)
        {
            ColumnSpacing = AppTheme.Space.S, RowSpacing = AppTheme.Space.S
        };

        void Submit(ReviewRating r)
        {
            VocabularyEntry updated;
            try { updated = _vocabularyService.SubmitReview(entry.Id, r); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _setStatus($"复习记录保存失败。{ex.Message}");
                return;
            }
            _setStatus($"已记录「{updated.Headword}」反馈 ({UiLabels.Rating(r)}) · 下次复习间隔已更新");
            _currentIndex++;
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
            gradeGrid.Children.Add(button);
        }
        cardStack.Children.Add(new Border { BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, AppTheme.Space.M, 0, 0), Child = gradeGrid });

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
