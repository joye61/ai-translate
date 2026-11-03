using AiTranslate.Core;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace AiTranslate.Desktop.Controls;

/// <summary>One lookup owns its source, streamed translation and ordered dictionary sections.</summary>
internal sealed class DictionaryResultView : StackPanel
{
    private enum StatusKind { Working, Done, Error }

    private readonly string _query;
    private readonly TextBlock _kind = new() { FontSize = AppTheme.Type.Caption, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.TextSecondary };
    private readonly Border _kindPill;
    private readonly StackPanel _languages = new() { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XXS, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _domains = new() { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly Ellipse _statusDot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly MdiIcon _statusIcon = new("sparkles", 13, AppTheme.Insight) { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Text = "正在翻译…", FontSize = AppTheme.Type.Caption, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectableTextBlock _heading = new() { FontWeight = FontWeight.Bold, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _translation = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _translationHost = new() { IsVisible = false };
    private readonly Border _audio = new() { IsVisible = false };
    internal SpeechPlayback SourcePlayback { get; } = new();
    internal DictionaryDisclosures Disclosures { get; }
    private readonly DictionaryDetails _details;
    private readonly ISpeechSynthesisService _speech;
    private readonly AppSettingsStore _settings;
    private readonly Action<string, string, bool> _lookup;
    private readonly TextBlock _readingHintText = new()
    {
        FontSize = AppTheme.Type.Caption, Foreground = AppTheme.TextMuted, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Control _readingHint;
    private readonly ContentControl _actions = new() { IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly ContentControl _secondaryActions = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _footer;
    private readonly SelectableTextBlock _errorText = new() { FontSize = AppTheme.Type.Small, Foreground = AppTheme.Danger, TextWrapping = TextWrapping.Wrap };
    private readonly Border _error;
    private readonly Control _skeleton = CreateSkeleton();
    private CancellationTokenSource? _pulse;
    private (bool, string?, string?, string)? _audioKey;
    private IReadOnlyList<string> _shownDomains = [];

    public DictionaryResultView(string query, string targetLanguage, ISpeechSynthesisService speech, AppSettingsStore settings,
        Action<string, string, bool> lookup, Action? goBack = null, string? contextSentence = null,
        DictionaryDisclosures? disclosures = null)
    {
        _query = query;
        _speech = speech;
        _settings = settings;
        _lookup = lookup;
        Disclosures = disclosures ?? new();
        _details = new DictionaryDetails(speech, settings, lookup, cloze => ExamplePracticeCard.Open(this, cloze), Disclosures);
        _kindPill = new Border
        {
            Background = AppTheme.CardElevatedBg, CornerRadius = new CornerRadius(AppTheme.Radius.Pill),
            Padding = new Thickness(AppTheme.Space.S, 3), VerticalAlignment = VerticalAlignment.Center, Child = _kind
        };

        var meta = new WrapPanel
        {
            ItemSpacing = AppTheme.Space.M, LineSpacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _kindPill,
                _languages,
                _domains,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center,
                    Children = { _statusDot, _statusIcon, _status }
                }
            }
        };
        if (goBack is not null)
        {
            var back = StudioUi.CreateCompactButton("arrow-left", "返回上一级", goBack);
            back.Margin = new Thickness(-AppTheme.Space.S, 0, 0, 0);
            ToolTip.SetTip(back, "回到上一个查询结果");
            meta.Children.Insert(0, back);
        }

        _translationHost.Child = _translation;
        _heading.VerticalAlignment = VerticalAlignment.Center;
        _audio.VerticalAlignment = VerticalAlignment.Center;
        _audio.Margin = new Thickness(0, AppTheme.Space.XXS, 0, 0);

        var hintIcon = new MdiIcon("information-outline", 13, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_readingHintText, 1);
        _readingHint = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.XS, IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { hintIcon, _readingHintText }
        };
        Grid.SetColumn(_secondaryActions, 1);
        _footer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.L,
            Children = { _readingHint, _secondaryActions }
        };

        _error = new Border
        {
            IsVisible = false, Background = AppTheme.DangerSoft, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2), Child = _errorText
        };
        // Headword and phonetics share a line; primary actions sit top-right instead of taking a row of their own.
        var titleLine = new WrapPanel
        {
            ItemSpacing = AppTheme.Space.L, LineSpacing = AppTheme.Space.XS, Children = { _heading, _audio }
        };
        Grid.SetColumn(_actions, 1);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.L,
            Children = { meta, _actions }
        };
        Children.Add(header);
        Children.Add(Spaced(titleLine, AppTheme.Space.S));
        Children.Add(Spaced(_translationHost, AppTheme.Space.S + 2));
        Children.Add(Spaced(_error, AppTheme.Space.M));
        if (!string.IsNullOrWhiteSpace(contextSentence) && contextSentence != query)
            Children.Add(Spaced(CreateContextQuote(contextSentence, query), AppTheme.Space.M));
        Children.Add(Spaced(_footer, AppTheme.Space.S));
        Children.Add(_details);
        Children.Add(_skeleton);
        var hint = DictionaryQuery.SuggestType(query);
        SetFormat(hint, "auto", targetLanguage);
        if (hint.IsPassage()) _details.ShowSource(query);
        SetStatus("正在翻译…", StatusKind.Working);
        DetachedFromVisualTree += (_, _) => StopPulse();
    }

    /// <summary>Placeholder lines shown while sections are still streaming in.</summary>
    private static Control CreateSkeleton()
    {
        static Border Bar(double width, double height = 12) => new()
        {
            Width = width, Height = height, HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Background = AppTheme.CardElevatedBg
        };
        var content = new StackPanel
        {
            Spacing = AppTheme.Space.M, Margin = new Thickness(0, AppTheme.Space.XXL, 0, 0),
            Children =
            {
                Bar(96, 14), Bar(320), Bar(260), Bar(200)
            }
        };
        CancellationTokenSource? shimmer = null;
        content.AttachedToVisualTree += (_, _) =>
        {
            shimmer = new CancellationTokenSource();
            _ = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(900), IterationCount = IterationCount.Infinite,
                PlaybackDirection = PlaybackDirection.Alternate, Easing = new Avalonia.Animation.Easings.SineEaseInOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.45d) } }
                }
            }.RunAsync(content, shimmer.Token);
        };
        content.DetachedFromVisualTree += (_, _) =>
        {
            shimmer?.Cancel();
            shimmer?.Dispose();
        };
        return content;
    }

    private static Control Spaced(Control control, double top)
    {
        control.Margin = new Thickness(0, top, 0, 0);
        return control;
    }

    private static Control CreateContextQuote(string sentence, string query)
    {
        var text = TextStyles.Selectable(null, TextRole.Secondary);
        var at = DictionaryTextMatch.IndexOf(sentence, query);
        if (at < 0) text.Text = sentence;
        else
        {
            // Color-only emphasis: a weight change mid-paragraph makes later CJK runs render bold.
            text.Inlines!.Add(new Run(sentence[..at]));
            text.Inlines.Add(new Run(sentence.Substring(at, query.Length)) { Foreground = AppTheme.AccentBright });
            text.Inlines.Add(new Run(sentence[(at + query.Length)..]));
        }
        var body = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                TextStyles.Create("查词原句", TextRole.Label),
                text
            }
        };
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S,
            Children = { new MdiIcon("format-quote-open", 16, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Top }, body }
        };
        Grid.SetColumn(body, 1);
        return grid;
    }

    private void SetFormat(VocabularyItemType type, string sourceLanguage, string targetLanguage)
    {
        var passage = type.IsPassage();
        _kind.Text = UiLabels.ItemType(type);
        var kindHue = HueMap.ItemType(type);
        _kind.Foreground = kindHue.Ink;
        _kindPill.Background = kindHue.Soft;
        _languages.Children.Clear();
        if (sourceLanguage is "" or "auto") _languages.Children.Add(MetaText($"译为{UiLabels.Language(targetLanguage)}"));
        else
        {
            _languages.Children.Add(MetaText(UiLabels.Language(sourceLanguage)));
            _languages.Children.Add(new MdiIcon("arrow-right", 12, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center });
            _languages.Children.Add(MetaText(UiLabels.Language(targetLanguage)));
        }
        _heading.Text = passage ? "译文" : _query;
        TextStyles.Apply(_heading, passage ? TextRole.PageTitle : TextRole.Display);
        TextStyles.Apply(_translation, passage ? TextRole.PassageTranslation : TextRole.Translation);
    }

    private static TextBlock MetaText(string text) => new()
    {
        Text = text, FontSize = AppTheme.Type.Caption, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center
    };

    private void SetStatus(string text, StatusKind kind, string? icon = null)
    {
        _status.Text = text;
        _status.Foreground = kind == StatusKind.Error ? AppTheme.Danger : AppTheme.TextMuted;
        _statusIcon.IsVisible = icon is not null;
        _statusDot.IsVisible = icon is null;
        if (icon is not null)
        {
            _statusIcon.IconName = icon;
            _statusIcon.Foreground = icon == "sparkles" ? AppTheme.Insight : AppTheme.TextMuted;
        }
        _statusDot.Fill = kind switch
        {
            StatusKind.Working => AppTheme.Accent,
            StatusKind.Error => AppTheme.Danger,
            _ => AppTheme.TextMuted
        };
        if (kind == StatusKind.Working) StartPulse();
        else StopPulse();
    }

    private void StartPulse()
    {
        if (_pulse is not null) return;
        _pulse = new CancellationTokenSource();
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1100), IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate, Easing = new Avalonia.Animation.Easings.SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.25d) } }
            }
        };
        _ = animation.RunAsync(_statusDot, _pulse.Token);
    }

    private void StopPulse()
    {
        _pulse?.Cancel();
        _pulse?.Dispose();
        _pulse = null;
        _statusDot.Opacity = 1;
    }

    public void ShowTranslation(string text)
    {
        _translation.Text = text;
        _translationHost.IsVisible = true;
        if (_status.Text == "正在翻译…") SetStatus("正在生成…", StatusKind.Working);
    }

    public void ShowEntry(DictionaryEntryResult entry, bool complete)
    {
        SetFormat(entry.ItemType, entry.DetectedSourceLanguage, entry.TargetLanguage);
        if (!_shownDomains.SequenceEqual(entry.Domains))
        {
            _shownDomains = entry.Domains;
            _domains.Children.Clear();
            foreach (var domain in entry.Domains) _domains.Children.Add(StudioUi.CreateChip(domain, "tag-outline"));
            _domains.IsVisible = entry.Domains.Count > 0;
            ToolTip.SetTip(_domains, "所属专业领域");
        }
        if (!string.IsNullOrEmpty(entry.PrimaryTranslation)) ShowTranslation(entry.PrimaryTranslation);
        if (complete)
            switch (entry.Origin)
            {
                case DictionaryResultOrigin.Cache: SetStatus("本地缓存", StatusKind.Done, "database-outline"); break;
                case DictionaryResultOrigin.Supplemented: SetStatus("已用 AI 补充", StatusKind.Done, "sparkles"); break;
                case DictionaryResultOrigin.BuiltIn: SetStatus("内置词条", StatusKind.Done, "book-open-variant"); break;
                default: SetStatus("AI 翻译", StatusKind.Done, "sparkles"); break;
            }
        else SetStatus(entry.ItemType.IsPassage() ? "正在整理译文…" : "正在补全词条…", StatusKind.Working);
        _details.ShowEntry(entry, complete);
        if (complete)
        {
            var translatedTerms = entry.Collocations.Select(c => c.Meaning).Concat(entry.Senses.Select(s => s.DefinitionZh));
            var reading = new DictionaryReadingText(entry.PrimaryTranslation, (q, context) => _lookup(q, context, true), translatedTerms)
            {
                FontSize = _translation.FontSize, LineHeight = _translation.LineHeight, FontWeight = _translation.FontWeight,
                Foreground = _translation.Foreground
            };
            _translationHost.Child = reading;
            _readingHintText.Text = reading.HasLinkedTerms || entry.Collocations.Count > 0 || entry.GlossaryReferences.Count > 0
                ? "点击下划线表达可继续查词 · 选中文字右键可查询" : "选中译文文字后右键即可查词";
            _readingHint.IsVisible = true;
        }
        var hasIpa = !string.IsNullOrWhiteSpace(entry.UsPhonetic) || !string.IsNullOrWhiteSpace(entry.UkPhonetic);
        var showIpa = !entry.ItemType.IsPassage() && (PhonicsAudioBar.SourceAccent(entry.DetectedSourceLanguage)
            == SpeechAccent.AmericanEnglish || (entry.DetectedSourceLanguage == "auto" && hasIpa));
        var key = (showIpa, entry.UsPhonetic, entry.UkPhonetic, entry.Pronunciation);
        if (key != _audioKey)
        {
            _audioKey = key;
            _audio.IsVisible = showIpa || (!entry.ItemType.IsPassage() && !string.IsNullOrWhiteSpace(entry.Pronunciation));
            if (showIpa) _audio.Child = PhonicsAudioBar.CreateAccentStrip(_speech, _settings, entry.SourceText,
                entry.UsPhonetic, entry.UkPhonetic, SourcePlayback);
            else if (_audio.IsVisible) _audio.Child = new SelectableTextBlock
            {
                Text = entry.Pronunciation, FontSize = AppTheme.Type.Body, Foreground = AppTheme.TextSecondary, TextWrapping = TextWrapping.Wrap
            };
        }
        if (complete && showIpa && entry.ItemType == VocabularyItemType.Word)
        {
            var practice = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
            PhonicsAudioBar.AddPracticeButtons(practice, _speech, _settings, entry.SourceText, entry.UsPhonetic,
                entry.UkPhonetic, entry.Examples.FirstOrDefault()?.SourceSentence);
            Children.Add(new DictionarySection("发音练习", practice));
        }
        if (complete) Children.Remove(_skeleton);
    }

    public void ShowActions(Control actions, Control? secondary = null)
    {
        _actions.Content = actions;
        _actions.IsVisible = true;
        _secondaryActions.Content = secondary;
    }

    public void MarkStopped()
    {
        SetStatus("已停止生成", StatusKind.Done);
        Children.Remove(_skeleton);
    }
    public void ShowError(string message)
    {
        SetStatus("未完成", StatusKind.Error);
        Children.Remove(_skeleton);
        _errorText.Text = message;
        _error.IsVisible = true;
    }
}
