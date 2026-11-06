using System.Diagnostics;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Views;

/// <summary>
/// Flash-card review of one notebook. A card shows the word first; the learner says whether they know it,
/// then sees the meaning. Cards that were not recognised come back a few cards later.
/// </summary>
internal sealed class VocabularyReviewView : UserControl
{
    private const int SessionSize = 20;

    private readonly IVocabularyService _service;
    private readonly ISpeechSynthesisService _speech;
    private readonly AppSettingsStore _settings;
    private readonly Action<VocabularyEntry> _openEntry;
    private readonly Action _exit;
    private readonly Action<string> _setStatus;
    private readonly TextBlock _heading = TextStyles.Create("", TextRole.SectionTitle, wrap: false);
    private readonly TextBlock _progressText = TextStyles.Create("", TextRole.FieldLabel, wrap: false);
    private readonly ProgressBar _progress = new()
    {
        Height = 6, MinHeight = 6, Minimum = 0, CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Margin = new Thickness(0, AppTheme.Space.M, 0, 0),
        Background = AppTheme.BorderSubtle, Foreground = AppTheme.Accent
    };
    private readonly StackPanel _stage = new() { Spacing = AppTheme.Space.M, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, AppTheme.Space.S, 0, 0) };
    private readonly Stopwatch _thinking = new();
    private string _notebookId = "default";
    private string _notebookName = "";
    private bool _extraPractice;
    private VocabularyReviewSession _session = new([]);
    private (VocabularyEntry Entry, ReviewRating Rating, bool Again)? _revealed;

    public VocabularyReviewView(IVocabularyService service, ISpeechSynthesisService speech, AppSettingsStore settings,
        Action<VocabularyEntry> openEntry, Action exit, Action<string> setStatus)
    {
        _service = service;
        _speech = speech;
        _settings = settings;
        _openEntry = openEntry;
        _exit = exit;
        _setStatus = setStatus;
        Focusable = true;

        var back = StudioUi.CreateIconButton("arrow-left", "退出复习", _exit);
        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.M, VerticalAlignment = VerticalAlignment.Center,
            Children = { back, _heading }
        };
        _heading.VerticalAlignment = VerticalAlignment.Center;
        _progressText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_progressText, 1);
        Grid.SetRow(_progress, 1);
        Grid.SetColumnSpan(_progress, 2);
        var topBar = new Grid
        {
            Margin = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.L),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            Children = { title, _progressText, _progress }
        };
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                topBar,
                new ScrollViewer
                {
                    [Grid.RowProperty] = 1, Padding = new Thickness(AppTheme.Space.XXL, 0, AppTheme.Space.XXL, AppTheme.Space.XXL),
                    Content = _stage
                }
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    internal void Start(string notebookId, string notebookName)
    {
        _notebookId = notebookId;
        _notebookName = notebookName;
        var due = _service.GetDueQueue(notebookId, SessionSize);
        _extraPractice = due.Count == 0;
        _session = new VocabularyReviewSession(_extraPractice
            ? _service.QueryEntries(new() { NotebookId = notebookId, SortOrder = VocabularySortOrder.ReviewDue }).Take(SessionSize)
            : due);
        _revealed = null;
        Render();
    }

    private void Render()
    {
        _stage.Children.Clear();
        _heading.Text = $"复习 · {_notebookName}";
        _progress.Maximum = Math.Max(1, _session.Total);
        _progress.Value = _session.Finished;
        _progressText.Text = $"{_session.Finished} / {_session.Total}";
        if (_session.Total == 0) _stage.Children.Add(Card(Message("book-bookmark", AppTheme.Hues.Coral, "这个生词本还没有词条",
            "在查词结果里点击「加入生词表」，再回来复习。", ActionButton("返回生词本", "arrow-left", _exit, primary: true))));
        else if (_revealed is { } shown) _stage.Children.Add(Card(Back(shown.Entry, shown.Rating, shown.Again)));
        else if (_session.Current is { } card)
        {
            _stage.Children.Add(Card(Front(card)));
            if (_extraPractice && !_session.IsRetry && _session.Finished == 0)
                _stage.Children.Add(Hint("今天没有到期的词，先提前巩固一轮。"));
        }
        else _stage.Children.Add(Card(Summary()));
        _thinking.Restart();
        Focus();
    }

    private static Border Card(Control content) =>
        StudioUi.CreateHeroCard(content, new Thickness(AppTheme.Space.XXXL, AppTheme.Space.XXL));

    private static Control Hint(string text)
    {
        var hint = TextStyles.Create(text, TextRole.Meta);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        return hint;
    }

    private Control Front(VocabularyEntry entry)
    {
        var passage = entry.ItemType.IsPassage();
        var body = new StackPanel { Spacing = AppTheme.Space.L };
        if (passage)
            body.Children.Add(TextStyles.Selectable(entry.Headword, TextRole.ExampleSource));
        else
        {
            body.Children.Add(Centered(TextStyles.Selectable(entry.Headword, TextStyles.HeadwordRole(entry.Headword))));
            if (Audio(entry) is { } audio) body.Children.Add(Centered(audio));
            if (!string.IsNullOrWhiteSpace(entry.ContextSentence) && entry.ContextSentence != entry.Headword)
                body.Children.Add(RichBlocks.Quote(TextStyles.Selectable(entry.ContextSentence, TextRole.Secondary)));
        }
        if (passage && Audio(entry) is { } read) body.Children.Add(read);

        var answers = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = AppTheme.Space.S };
        var choices = new[] { (ReviewRating.Good, "1"), (ReviewRating.Hard, "2"), (ReviewRating.Again, "3") };
        for (var i = 0; i < choices.Length; i++)
        {
            var (rating, key) = choices[i];
            var button = AnswerButton(rating, key);
            Grid.SetColumn(button, i);
            answers.Children.Add(button);
        }
        var prompt = TextStyles.Create("先回想含义，再选择熟悉程度", TextRole.Meta, wrap: false);
        prompt.HorizontalAlignment = HorizontalAlignment.Center;
        body.Children.Add(new Border
        {
            BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, AppTheme.Space.S, 0, 0), Padding = new Thickness(0, AppTheme.Space.M, 0, 0),
            Child = new StackPanel { Spacing = AppTheme.Space.M, Children = { prompt, answers } }
        });
        return body;
    }

    private Control Back(VocabularyEntry entry, ReviewRating rating, bool again)
    {
        var passage = entry.ItemType.IsPassage();
        var body = new StackPanel { Spacing = AppTheme.Space.M };
        var verdict = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS };
        verdict.Children.Add(StudioUi.CreateChip(UiLabels.Rating(rating), HueMap.Rating(rating)));
        if (again) verdict.Children.Add(TextStyles.Create("稍后会再出现", TextRole.Meta));
        body.Children.Add(verdict);
        if (passage) body.Children.Add(TextStyles.Selectable(entry.Headword, TextRole.ExampleSource));
        else
        {
            body.Children.Add(TextStyles.Selectable(entry.Headword, TextRole.PageTitle));
            if (Audio(entry) is { } audio) body.Children.Add(audio);
        }
        body.Children.Add(TextStyles.Selectable(entry.Translation, passage ? TextRole.PassageTranslation : TextRole.Translation));

        var senses = entry.Senses.Where(s => !string.IsNullOrWhiteSpace(s.DefinitionZh) && s.DefinitionZh != entry.Translation).Take(3).ToArray();
        if (!passage && senses.Length > 0)
        {
            var list = new StackPanel { Spacing = AppTheme.Space.XS };
            foreach (var sense in senses)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S };
                var definition = TextStyles.Selectable(sense.DefinitionZh, TextRole.Body);
                Grid.SetColumn(definition, 1);
                row.Children.Add(string.IsNullOrWhiteSpace(sense.PartOfSpeech)
                    ? new Border { Width = 0 }
                    : StudioUi.CreateTag(sense.PartOfSpeech, hue: HueMap.PartOfSpeech(sense.PartOfSpeech)));
                row.Children.Add(definition);
                list.Children.Add(row);
            }
            body.Children.Add(list);
        }
        if (!passage && entry.Examples.FirstOrDefault() is { } example)
        {
            var pair = new StackPanel { Spacing = AppTheme.Space.XXS, Children = { TextStyles.Selectable(example.SourceSentence, TextRole.ExampleSource) } };
            if (!string.IsNullOrWhiteSpace(example.TranslatedSentence))
                pair.Children.Add(TextStyles.Selectable(example.TranslatedSentence, TextRole.ExampleTarget));
            body.Children.Add(RichBlocks.Quote(pair));
        }
        // The sentence the word was saved from, with its meaning, unless it is the example already shown.
        if (!passage && !string.IsNullOrWhiteSpace(entry.ContextSentence) && entry.ContextSentence != entry.Headword &&
            entry.ContextSentence != entry.Examples.FirstOrDefault()?.SourceSentence)
        {
            var origin = new StackPanel
            {
                Spacing = AppTheme.Space.XXS,
                Children = { TextStyles.Create("收录时的语境", TextRole.Label), TextStyles.Selectable(entry.ContextSentence, TextRole.ExampleSource) }
            };
            if (!string.IsNullOrWhiteSpace(entry.ContextTranslation))
                origin.Children.Add(TextStyles.Selectable(entry.ContextTranslation, TextRole.ExampleTarget));
            body.Children.Add(RichBlocks.Quote(origin));
        }

        var last = _session.IsComplete;
        var next = ActionButton(last ? "完成" : "下一个", last ? "check" : "arrow-right", () => { _revealed = null; Render(); }, primary: true);
        next.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(next, 1);
        var more = StudioUi.CreateCompactButton("book-open", "查看完整词条", () => _openEntry(entry));
        var footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, AppTheme.Space.S, 0, 0),
            Children = { more, next }
        };
        more.HorizontalAlignment = HorizontalAlignment.Left;
        more.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(new Border
        {
            BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, AppTheme.Space.S, 0, 0), Padding = new Thickness(0, AppTheme.Space.L, 0, 0), Child = footer
        });
        return body;
    }

    private Control Summary()
    {
        var stats = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = AppTheme.Space.S };
        var counts = new[] { (ReviewRating.Good, _session.Known), (ReviewRating.Hard, _session.Fuzzy), (ReviewRating.Again, _session.Forgotten) };
        for (var i = 0; i < counts.Length; i++)
        {
            var (rating, count) = counts[i];
            var hue = HueMap.Rating(rating);
            var number = TextStyles.Create(count.ToString(), TextRole.Display, wrap: false);
            number.Foreground = hue.Ink;
            var tile = new Border
            {
                Background = hue.Soft, CornerRadius = new CornerRadius(AppTheme.Radius.M), Padding = new Thickness(AppTheme.Space.S, AppTheme.Space.M),
                Child = new StackPanel
                {
                    Spacing = AppTheme.Space.XXS,
                    Children = { Centered(number), Centered(TextStyles.Create(UiLabels.Rating(rating), TextRole.FieldLabel, wrap: false)) }
                }
            };
            Grid.SetColumn(tile, i);
            stats.Children.Add(tile);
        }
        var more = _service.GetDueQueue(_notebookId, 1).Count > 0;
        stats.Margin = new Thickness(0, AppTheme.Space.S);
        stats.MinWidth = 380;
        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S,
            Margin = new Thickness(0, AppTheme.Space.S, 0, 0)
        };
        buttons.Children.Add(ActionButton(more ? "继续复习" : "再练一轮", "arrows-rotate", () => Start(_notebookId, _notebookName), primary: more));
        buttons.Children.Add(ActionButton("返回生词本", "arrow-left", _exit, primary: !more));
        return Message("check-double", AppTheme.Hues.Sage, "本轮复习完成",
            more ? "还有词条等待复习，休息一下再继续也可以。" : "复习进度已保存，下次到期时会再提醒你。", stats, buttons);
    }

    private static Control Message(string icon, Hue hue, string title, string detail, params Control[] tail)
    {
        var body = StudioUi.CreateEmptyState(icon, hue, title, detail, 420);
        body.Spacing = AppTheme.Space.M;
        body.Margin = new Thickness(0, AppTheme.Space.M);
        foreach (var control in tail) body.Children.Add(control);
        return body;
    }

    private Control? Audio(VocabularyEntry entry)
    {
        var result = entry.ToDictionaryResult();
        if (PhonicsAudioBar.HasEnglishAudio(result))
            return PhonicsAudioBar.CreateAccentStrip(_speech, _settings, entry.Headword, entry.UsPhonetic, entry.UkPhonetic);
        return PhonicsAudioBar.SourceAccent(entry.SourceLanguage) is { } voice
            ? PhonicsAudioBar.CreateSentenceReadButton(_speech, _settings, entry.Headword, "朗读", voice)
            : null;
    }

    private Button AnswerButton(ReviewRating rating, string key)
    {
        var hue = HueMap.Rating(rating);
        var label = TextStyles.Create(UiLabels.Rating(rating), TextRole.SectionTitle, wrap: false);
        label.Foreground = hue.Ink;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var keycap = StudioUi.CreateKeycap(key);
        keycap.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Button
        {
            Classes = { "answer", rating switch { ReviewRating.Good => "good", ReviewRating.Hard => "hard", _ => "again" } },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderStrong, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.M), Padding = new Thickness(AppTheme.Space.S, AppTheme.Space.M),
            Content = new StackPanel
            {
                Spacing = AppTheme.Space.S,
                Children =
                {
                    new Border
                    {
                        Width = 22, Height = 3, CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = hue.Solid,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    label, keycap
                }
            }
        };
        AutomationProperties.SetName(button, UiLabels.Rating(rating));
        button.Click += (_, _) => Answer(rating);
        return button;
    }

    private static Button ActionButton(string text, string icon, Action click, bool primary = false)
    {
        var button = primary ? StudioUi.CreatePrimaryButton(icon, text, click) : StudioUi.CreateSecondaryButton(icon, text, click);
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void Answer(ReviewRating rating)
    {
        if (_revealed is not null || _session.Current is not { } entry) return;
        var again = rating != ReviewRating.Good;
        var elapsed = (int)Math.Clamp(_thinking.ElapsedMilliseconds, 300, 60_000);
        try
        {
            if (!_session.IsRetry) _service.SubmitReview(entry.Id, rating, elapsed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _setStatus($"复习记录保存失败。{ex.Message}");
            return;
        }
        _session.Answer(rating);
        _revealed = (entry, rating, again);
        Render();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsVisible || e.Handled) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _exit();
            return;
        }
        if (_revealed is not null)
        {
            if (e.Key is not (Key.Space or Key.Enter or Key.Right)) return;
            e.Handled = true;
            _revealed = null;
            Render();
            return;
        }
        ReviewRating? rating = e.Key switch
        {
            Key.D1 or Key.NumPad1 => ReviewRating.Good,
            Key.D2 or Key.NumPad2 => ReviewRating.Hard,
            Key.D3 or Key.NumPad3 => ReviewRating.Again,
            _ => null
        };
        if (rating is null || _session.Current is null) return;
        e.Handled = true;
        Answer(rating.Value);
    }

    private static T Centered<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Center;
        if (control is TextBlock text) text.TextAlignment = TextAlignment.Center;
        return control;
    }
}
