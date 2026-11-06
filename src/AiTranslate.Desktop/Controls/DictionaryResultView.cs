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
using Avalonia.Threading;

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
    private readonly FontAwesomeIcon _statusIcon = new("wand-magic-sparkles", 13, AppTheme.Insight) { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Text = "正在翻译…", FontSize = AppTheme.Type.Caption, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
    private readonly SelectableTextBlock _heading = new() { FontWeight = FontWeight.Bold, Foreground = AppTheme.TextPrimary, TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _titleLine;
    private readonly DictionaryTranslations _translations = new(smoothPreview: true);
    private readonly Border _audio = new() { IsVisible = false };
    internal SpeechPlayback SourcePlayback { get; } = new();
    internal DictionaryDisclosures Disclosures { get; }
    internal BlockReviewActions BlockReviews { get; } = new();
    private readonly DictionaryDetails _details;
    private readonly ISpeechSynthesisService _speech;
    private readonly AppSettingsStore _settings;
    private readonly Action<string, string, bool> _lookup;
    private readonly TextBlock _readingHintText = TextStyles.Apply(new TextBlock
    {
        VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap
    }, TextRole.Meta);
    private readonly Control _readingHint;
    private readonly ContentControl _actions = new() { IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
    private readonly ContentControl _secondaryActions = new()
    {
        VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly Border _footer;
    private readonly TextBlock _reviewFeedback = BlockReviewActions.CreateError();
    private readonly SelectableTextBlock _errorText = new() { FontSize = AppTheme.Type.Small, Foreground = AppTheme.Danger, TextWrapping = TextWrapping.Wrap };
    private readonly Border _error;
    private readonly Control _skeleton = new LoadingSkeleton { Margin = new Thickness(0, AppTheme.Space.XXL, 0, 0) };
    private CancellationTokenSource? _pulse;
    private (bool, string?, string?, string)? _audioKey;
    private IReadOnlyList<string> _shownDomains = [];
    private (VocabularyItemType, string, string)? _formatKey;
    private (string, StatusKind, string?)? _statusKey;
    private DictionaryEntryResult? _pendingPreview;
    private bool _previewQueued, _streamClosed;

    public DictionaryResultView(string query, string targetLanguage, ISpeechSynthesisService speech, AppSettingsStore settings,
        Action<string, string, bool> lookup, Action? goBack = null, string? contextSentence = null,
        DictionaryDisclosures? disclosures = null)
    {
        _query = query;
        Classes.Add("dictionary-reading");
        _speech = speech;
        _settings = settings;
        _lookup = lookup;
        Disclosures = disclosures ?? new();
        _details = new DictionaryDetails(speech, settings, lookup, Disclosures, BlockReviews);
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

        _heading.VerticalAlignment = VerticalAlignment.Center;
        _audio.VerticalAlignment = VerticalAlignment.Center;
        _audio.Margin = new Thickness(0, AppTheme.Space.XXS, 0, 0);

        var hintIcon = new FontAwesomeIcon("circle-info", 13, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_readingHintText, 1);
        _readingHint = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.XS, IsVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { hintIcon, _readingHintText }
        };
        Grid.SetColumn(_secondaryActions, 1);
        var toolbar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = AppTheme.Space.M, MinHeight = 32,
            Children = { _readingHint, _secondaryActions }
        };
        toolbar.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < 550;
            Grid.SetColumnSpan(_readingHint, compact ? 2 : 1);
            Grid.SetColumn(_secondaryActions, compact ? 0 : 1);
            Grid.SetColumnSpan(_secondaryActions, compact ? 2 : 1);
            Grid.SetRow(_secondaryActions, compact ? 1 : 0);
            _secondaryActions.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            toolbar.RowSpacing = compact && _secondaryActions.Content is not null ? AppTheme.Space.XS : 0;
        };
        _footer = new Border
        {
            IsVisible = false, Child = new StackPanel
            {
                Spacing = AppTheme.Space.XS, Children = { toolbar, _reviewFeedback }
            }
        };

        _error = new Border
        {
            IsVisible = false, Background = AppTheme.DangerSoft, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2), Child = _errorText
        };
        // Headword and phonetics share a line; primary actions sit top-right instead of taking a row of their own.
        _titleLine = new WrapPanel
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
        Children.Add(Spaced(_titleLine, AppTheme.Space.S));
        Children.Add(Spaced(_translations, AppTheme.Space.M));
        Children.Add(Spaced(_error, AppTheme.Space.M));
        if (!string.IsNullOrWhiteSpace(contextSentence) && contextSentence != query)
            Children.Add(Spaced(CreateContextQuote(contextSentence, query), AppTheme.Space.M));
        Children.Add(Spaced(_footer, AppTheme.Space.L));
        Children.Add(_details);
        Children.Add(_skeleton);
        var hint = DictionaryQuery.SuggestType(query);
        SetFormat(hint, "auto", targetLanguage);
        if (hint.IsPassage()) _details.ShowSource(query);
        SetStatus("正在翻译…", StatusKind.Working);
        AttachedToVisualTree += (_, _) =>
        {
            if (_statusKey?.Item2 == StatusKind.Working) StartPulse();
        };
        DetachedFromVisualTree += (_, _) => StopPulse();
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
            Spacing = AppTheme.Space.XXS,
            Children =
            {
                TextStyles.Create("查词原句", TextRole.Label),
                text
            }
        };
        return RichBlocks.Quote(body);
    }

    private void SetFormat(VocabularyItemType type, string sourceLanguage, string targetLanguage)
    {
        var key = (type, sourceLanguage, targetLanguage);
        if (_formatKey == key) return;
        _formatKey = key;
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
            _languages.Children.Add(new FontAwesomeIcon("arrow-right", 12, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center });
            _languages.Children.Add(MetaText(UiLabels.Language(targetLanguage)));
        }
        _heading.Text = _query;
        _titleLine.IsVisible = !passage;
        TextStyles.Apply(_heading, TextStyles.HeadwordRole(_query));
    }

    private static TextBlock MetaText(string text) => new()
    {
        Text = text, FontSize = AppTheme.Type.Caption, Foreground = AppTheme.TextSecondary, VerticalAlignment = VerticalAlignment.Center
    };

    private void SetStatus(string text, StatusKind kind, string? icon = null)
    {
        var key = (text, kind, icon);
        if (_statusKey == key) return;
        _statusKey = key;
        ToolTip.SetTip(_status, null);
        _status.Text = text;
        _status.Foreground = kind == StatusKind.Error ? AppTheme.Danger : AppTheme.TextMuted;
        _statusIcon.IsVisible = icon is not null;
        _statusDot.IsVisible = icon is null;
        if (icon is not null)
        {
            _statusIcon.IconName = icon;
            _statusIcon.Foreground = icon == "wand-magic-sparkles" ? AppTheme.Insight : AppTheme.TextMuted;
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
        if (_streamClosed) return;
        if (_pendingPreview is { } pending) _pendingPreview = pending with { PrimaryTranslation = text };
        _translations.ShowPreview(text, _formatKey?.Item1 ?? DictionaryQuery.SuggestType(_query));
        if (_status.Text == "正在翻译…") SetStatus("正在生成…", StatusKind.Working);
    }

    public void ShowEntry(DictionaryEntryResult entry, bool complete)
    {
        if (complete)
        {
            _streamClosed = true;
            _pendingPreview = null;
            RenderEntry(entry, complete: true);
            return;
        }
        if (_streamClosed) return;
        // Network bursts can post several snapshots before one UI frame. Render only the latest one.
        _pendingPreview = entry;
        if (_previewQueued) return;
        _previewQueued = true;
        Dispatcher.UIThread.Post(FlushEntryPreview, DispatcherPriority.Background);
    }

    private void FlushEntryPreview()
    {
        _previewQueued = false;
        if (_pendingPreview is not { } entry) return;
        _pendingPreview = null;
        RenderEntry(entry, complete: false);
    }

    internal static string? QualityStatus(DictionaryEntryResult entry) => entry.Processing switch
    {
        { Review: DictionaryStageState.Running } => "正在核对译文和解析…",
        { Extension: DictionaryStageState.Running } => "正在补充学习解析…",
        { Review: DictionaryStageState.Failed } => "译文可用 · 审校未完成",
        { Review: DictionaryStageState.Unresolved } => "有待确认的译义",
        { Extension: DictionaryStageState.Failed } => "译文可用 · 学习解析未完成",
        { Intent: DictionaryLookupIntent.Learning, Extension: DictionaryStageState.Invalidated } => "已修正 · 部分解析待补",
        { Review: DictionaryStageState.Invalidated } => "内容已更新 · 待核对",
        { Review: DictionaryStageState.Completed } => "译文与现有解析已核对",
        _ => null
    };

    internal static string? CompletionStatus(DictionaryEntryResult entry) =>
        entry.Processing is { Review: DictionaryStageState.Failed, Message.Length: > 0 } state
            ? state.Message : QualityStatus(entry);

    private void RenderEntry(DictionaryEntryResult entry, bool complete)
    {
        SetFormat(entry.ItemType, entry.DetectedSourceLanguage, entry.TargetLanguage);
        if (!_shownDomains.SequenceEqual(entry.Domains))
        {
            _shownDomains = entry.Domains;
            _domains.Children.Clear();
            foreach (var domain in entry.Domains) _domains.Children.Add(StudioUi.CreateChip(domain, "tag"));
            _domains.IsVisible = entry.Domains.Count > 0;
            ToolTip.SetTip(_domains, "所属专业领域");
        }
        _translations.ShowEntry(entry, complete ? (q, context) => _lookup(q, context, true) : null, complete ? BlockReviews : null);
        if (complete && entry.NotebookSyncPending) SetStatus("已缓存 · 生词本待同步",         StatusKind.Done, "database");
        else if (QualityStatus(entry) is { } status)
        {
            SetStatus(status, complete ? StatusKind.Done : StatusKind.Working, complete ? "wand-magic-sparkles" : null);
            ToolTip.SetTip(_status, string.IsNullOrWhiteSpace(entry.Processing?.Message) ? null : entry.Processing.Message);
        }
        else if (complete)
            switch (entry.Origin)
            {
                case DictionaryResultOrigin.Cache: SetStatus("本地缓存", StatusKind.Done, "database"); break;
                case DictionaryResultOrigin.Supplemented:                 SetStatus("已用 AI 补充", StatusKind.Done, "wand-magic-sparkles"); break;
                                case DictionaryResultOrigin.Reviewed: SetStatus("智能复核已更新", StatusKind.Done, "wand-magic-sparkles"); break;
                case DictionaryResultOrigin.BuiltIn: SetStatus("内置词条", StatusKind.Done, "book-open"); break;
                default: SetStatus("AI 翻译", StatusKind.Done, "wand-magic-sparkles"); break;
            }
        else SetStatus(entry.ItemType.IsPassage() ? "正在整理译文…" : "正在补全词条…", StatusKind.Working);
        _details.ShowEntry(entry, complete);
        _reviewFeedback.IsVisible = complete && entry.Processing?.Review == DictionaryStageState.Failed;
        _reviewFeedback.Text = _reviewFeedback.IsVisible ? CompletionStatus(entry) : "";
        if (complete)
        {
            DictionaryReadingText.EnableSelectionLookup(this, (q, context) => _lookup(q, context, false));
            _readingHintText.Text = _translations.HasLinkedTerms || entry.Collocations.Count > 0 || entry.GlossaryReferences.Count > 0
                ? "单击下划线词语，或选中正文后右键查词" : "选中正文后右键查词";
            if (entry.GrammarHighlights.Count > 0) _readingHintText.Text += " · 紫底标出关键语法";
            _readingHint.IsVisible = true;
            _footer.IsVisible = true;
        }
        var showIpa = PhonicsAudioBar.HasEnglishAudio(entry);
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
        if (complete) Children.Remove(_skeleton);
    }

    public void ShowActions(Control actions, Control? secondary = null)
    {
        _actions.Content = actions;
        _actions.IsVisible = true;
        _secondaryActions.Content = secondary;
    }

    public void EnableSupplementActions(bool enabled)
    {
        _secondaryActions.IsEnabled = enabled;
        if (!enabled) _reviewFeedback.IsVisible = false;
    }

    public void MarkStopped()
    {
        EndStream();
        SetStatus("已停止生成", StatusKind.Done);
        Children.Remove(_skeleton);
    }
    public void ShowError(string message)
    {
        EndStream();
        SetStatus("未完成", StatusKind.Error);
        Children.Remove(_skeleton);
        _errorText.Text = message;
        _error.IsVisible = true;
    }

    private void EndStream()
    {
        _streamClosed = true;
        FlushEntryPreview();
        _translations.FinishPreview();
    }
}
