using System.Text.Json;
using AiTranslate.Core;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// One grammar article: a header (category, origin, title, one-line summary) followed by the blocks of
/// <see cref="GrammarBlockCatalog"/> in order. Blocks appear as the model streams them and are rebuilt only when their data changes.
/// </summary>
internal sealed class GrammarTopicView : StackPanel
{
    private enum StatusKind { Working, Done, Error }

    private sealed class SectionState
    {
        public ContentControl Host { get; } = new() { IsVisible = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        public ContentControl Body { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        public DictionarySection? Section { get; set; }
        public string? Signature { get; set; }
    }

    private readonly GrammarBlocks _blocks;
    private readonly Dictionary<GrammarBlock, SectionState> _sections = [];
    private readonly WrapPanel _meta = new() { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _chips = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _statusDot = new() { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = TextStyles.Apply(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, TextRole.Meta);
    private readonly SelectableTextBlock _title;
    private readonly SelectableTextBlock _sourceTerm = TextStyles.Selectable(null, TextRole.Gloss);
    private readonly TextBlock _aliases = TextStyles.Create(null, TextRole.Meta);
    private readonly ContentControl _hero = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Border _error;
    private readonly SelectableTextBlock _errorText = new() { FontSize = AppTheme.Type.Small, Foreground = AppTheme.Danger, TextWrapping = TextWrapping.Wrap };
    private readonly Border _footer;
    private readonly Control _skeleton = new LoadingSkeleton { Margin = new Thickness(0, AppTheme.Space.XXL, 0, 0) };
    private CancellationTokenSource? _pulse;
    private string? _headKey, _heroKey;
    private (string, StatusKind)? _statusKey;
    private GrammarTopic? _shown;
    private bool _complete;

    public ContentControl Actions { get; } = new() { VerticalAlignment = VerticalAlignment.Top };
    public event Action? CompleteRequested;

    public GrammarTopicView(string requestedTitle, GrammarBlocks blocks, Action? goBack)
    {
        _blocks = blocks;
        Classes.Add("dictionary-reading");
        _title = TextStyles.Selectable(requestedTitle, TextRole.Display);
        _title.FontWeight = FontWeight.Bold;
        _title.VerticalAlignment = VerticalAlignment.Center;
        _chips.VerticalAlignment = VerticalAlignment.Center;
        if (goBack is not null)
        {
            var back = StudioUi.CreateCompactButton("arrow-left", "返回上一级", goBack);
            back.Margin = new Thickness(-AppTheme.Space.S, 0, 0, 0);
            ToolTip.SetTip(back, "回到上一个结果");
            _meta.Children.Add(back);
        }
        _meta.Children.Add(_chips);
        _meta.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center,
            Children = { _statusDot, _status }
        });
        _sourceTerm.IsVisible = false;
        _aliases.IsVisible = false;
        var left = new StackPanel
        {
            Spacing = AppTheme.Space.S,
            Children = { _meta, new WrapPanel { ItemSpacing = AppTheme.Space.L, Children = { _title } }, _sourceTerm, _aliases }
        };
        Grid.SetColumn(Actions, 1);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.L, Children = { left, Actions } };

        _error = new Border
        {
            IsVisible = false, Background = AppTheme.DangerSoft, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Margin = new Thickness(0, AppTheme.Space.M, 0, 0),
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2), Child = _errorText
        };
        _hero.Margin = new Thickness(0, AppTheme.Space.L, 0, 0);
        _footer = BuildFooter();

        Children.Add(header);
        Children.Add(_hero);
        Children.Add(_error);
        // Reserve every block's position in catalog order up front, so a late block never lands above an earlier one.
        foreach (var spec in GrammarBlockCatalog.All.Where(s => s.Block != GrammarBlock.Summary)) Ensure(spec);
        Children.Add(_footer);
        Children.Add(_skeleton);
        SetStatus("正在准备…", StatusKind.Working);
        AttachedToVisualTree += (_, _) =>
        {
            if (_statusKey?.Item2 == StatusKind.Working) StartPulse();
        };
        DetachedFromVisualTree += (_, _) => StopPulse();
    }

    private Border BuildFooter()
    {
        var button = StudioUi.CreateCompactButton("wand-magic-sparkles", "补全进阶内容", () => CompleteRequested?.Invoke());
        button.Background = AppTheme.CardBg;
        button.BorderBrush = AppTheme.BorderStrong;
        button.BorderThickness = new Thickness(1);
        button.CornerRadius = new CornerRadius(AppTheme.Radius.Pill);
        button.Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.XS);
        button.MinHeight = 32;
        var text = TextStyles.Create("易混对比、常见错误和小练习还没有生成。", TextRole.Secondary);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(button, 1);
        return new Border
        {
            IsVisible = false, Margin = new Thickness(0, AppTheme.Space.XL, 0, 0), Background = AppTheme.InputBg,
            CornerRadius = new CornerRadius(AppTheme.Radius.M), Padding = new Thickness(AppTheme.Space.L, AppTheme.Space.M),
            Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.M, Children = { text, button } }
        };
    }

    private SectionState Ensure(GrammarBlockSpec spec)
    {
        if (_sections.TryGetValue(spec.Block, out var section)) return section;
        section = new();
        section.Section = new DictionarySection(spec.Title, section.Body, spec.Insight);
        section.Host.Content = section.Section;
        _sections.Add(spec.Block, section);
        Children.Add(section.Host);
        return section;
    }

    public void SetStage(string text) => SetStatus(text, StatusKind.Working);

    public void ShowError(string message)
    {
        _errorText.Text = message;
        _error.IsVisible = true;
        _skeleton.IsVisible = false;
        SetStatus("未完成", StatusKind.Error);
    }

    public void Show(GrammarTopic topic, bool complete)
    {
        _shown = topic;
        _complete = complete;
        _blocks.Interactive = complete;
        _blocks.Accent = complete ? PhonicsAudioBar.SourceAccent(topic.Language) : null;
        _error.IsVisible = false;
        RenderHeader(topic);
        foreach (var spec in GrammarBlockCatalog.All.Where(s => s.Block != GrammarBlock.Summary)) RenderBlock(spec, topic);
        _footer.IsVisible = complete && !topic.Extended;
        _skeleton.IsVisible = !complete;
        if (complete)
            SetStatus(topic.Origin == GrammarOrigin.Library ? "来自本地语法库，未消耗 AI" : "AI 新生成，已存入本地语法库", StatusKind.Done);
    }

    private void RenderHeader(GrammarTopic topic)
    {
        var headKey = $"{topic.Title}|{topic.Category}|{topic.Level}|{topic.Language}|{topic.SourceTerm}|{string.Join('/', topic.Aliases)}|{topic.Origin}|{_complete}";
        if (_headKey != headKey)
        {
            _headKey = headKey;
            _title.Text = topic.Title;
            if (topic.Title.Length > 14) _title.FontSize = AppTheme.Type.Headline + 2;
            else _title.FontSize = AppTheme.Type.Display;
            var chips = new WrapPanel { ItemSpacing = AppTheme.Space.XS, LineSpacing = AppTheme.Space.XS };
            chips.Children.Add(StudioUi.CreateChip(string.IsNullOrWhiteSpace(topic.Category) ? "语法" : topic.Category, HueMap.GrammarCategory(topic.Category)));
            if (topic.Language != "en") chips.Children.Add(StudioUi.CreateChip(UiLabels.Language(topic.Language), "language", AppTheme.TextSecondary, AppTheme.CardElevatedBg));
            if (!string.IsNullOrWhiteSpace(topic.Level)) chips.Children.Add(StudioUi.CreateChip(topic.Level, "graduation-cap", AppTheme.TextSecondary, AppTheme.CardElevatedBg));
            _chips.Content = chips;
            _sourceTerm.Text = topic.SourceTerm;
            _sourceTerm.IsVisible = !string.IsNullOrWhiteSpace(topic.SourceTerm);
            var aliases = topic.Aliases.Take(4).ToArray();
            _aliases.Text = aliases.Length == 0 ? null : "也叫  " + string.Join("  ·  ", aliases);
            _aliases.IsVisible = aliases.Length > 0;
        }
        var heroKey = topic.Summary;
        if (_heroKey != heroKey)
        {
            _heroKey = heroKey;
            if (string.IsNullOrWhiteSpace(topic.Summary)) _hero.Content = null;
            else
            {
                var label = TextStyles.Create(GrammarBlockCatalog.For(GrammarBlock.Summary).Title, TextRole.Label);
                label.Foreground = AppTheme.Insight;
                _hero.Content = RichBlocks.Callout("lightbulb",
                    new StackPanel { Spacing = 2, Children = { label, RichText.Create(topic.Summary, TextRole.Definition) } }, RichBlocks.CalloutTone.Insight);
            }
            _hero.IsVisible = _hero.Content is not null;
        }
    }

    private void RenderBlock(GrammarBlockSpec spec, GrammarTopic topic)
    {
        switch (spec.Block)
        {
            case GrammarBlock.Concept:
                Custom(spec, topic.Concept, () => _blocks.Concept(topic.Concept));
                break;
            case GrammarBlock.Formulas:
                Custom(spec, topic.Formulas, () => _blocks.Formulas(topic.Formulas), Count(topic.Formulas.Count, "种"));
                break;
            case GrammarBlock.Usages:
                Custom(spec, topic.Usages, () => _blocks.Usages(topic.Usages), Count(topic.Usages.Count, "条"));
                break;
            case GrammarBlock.Signals:
                Custom(spec, topic.Signals, () => _blocks.Signals(topic.Signals));
                break;
            case GrammarBlock.Rules:
                Custom(spec, topic.Rules, () => _blocks.Rules(topic.Rules));
                break;
            case GrammarBlock.Contrasts:
                Custom(spec, topic.Contrasts, () => _blocks.Contrasts(topic.Contrasts, topic.Title), Count(topic.Contrasts.Count, "组"));
                break;
            case GrammarBlock.Exceptions:
                Custom(spec, topic.Exceptions, () => _blocks.Exceptions(topic.Exceptions));
                break;
            case GrammarBlock.Mistakes:
                Custom(spec, topic.Mistakes, () => DictionaryInsightBlocks.Mistakes(topic.Mistakes), Count(topic.Mistakes.Count, "条"));
                break;
            case GrammarBlock.ExamPoints:
                Custom(spec, topic.ExamPoints, () => GrammarBlocks.ExamPoints(topic.ExamPoints));
                break;
            case GrammarBlock.Mnemonic:
                Custom(spec, topic.Mnemonic, () => GrammarBlocks.Mnemonic(topic.Mnemonic));
                break;
            case GrammarBlock.Exercises:
                Custom(spec, topic.Exercises, () => _blocks.Exercises(topic.Exercises), Count(topic.Exercises.Count, "题"));
                break;
            case GrammarBlock.Related:
                Custom(spec, topic.Related, () => _blocks.Related(topic.Related));
                break;
        }
    }

    private static string? Count(int count, string unit) => count > 1 ? $"{count} {unit}" : null;

    private void Custom(GrammarBlockSpec spec, object? data, Func<Control?> build, string? caption = null)
    {
        var section = Ensure(spec);
        var signature = JsonSerializer.Serialize(data) + (_complete ? "|done" : "");
        if (section.Signature == signature)
        {
            section.Host.IsVisible = section.Body.Content is not null;
            return;
        }
        section.Signature = signature;
        var content = build();
        section.Body.Content = content;
        section.Host.IsVisible = content is not null;
        section.Section!.Caption.Text = caption;
        section.Section.Caption.IsVisible = caption is not null && content is not null;
    }

    private void SetStatus(string text, StatusKind kind)
    {
        if (_statusKey == (text, kind)) return;
        _statusKey = (text, kind);
        _status.Text = text;
        _status.Foreground = kind == StatusKind.Error ? AppTheme.Danger : AppTheme.TextMuted;
        _statusDot.Fill = kind switch { StatusKind.Working => AppTheme.Accent, StatusKind.Error => AppTheme.Danger, _ => AppTheme.Hues.Sage.Solid };
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
}
