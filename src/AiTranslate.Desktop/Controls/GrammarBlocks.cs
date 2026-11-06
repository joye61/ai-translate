using AiTranslate.Core;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Reading forms for the blocks of a grammar article. Like <see cref="DictionaryInsightBlocks"/>, every method returns null when
/// its data is empty, and colors and type come from the shared theme: formulas as chip equations, usages as numbered ruled rows,
/// form rules as tables, contrasts as the dictionary's Sky/Honey pair, exercises as self-checking cards.
/// </summary>
internal sealed class GrammarBlocks(ISpeechSynthesisService speech, AppSettingsStore settings, Func<string, bool> isKnown,
    Action<string> open, Action<string, string> lookup)
{
    /// <summary>True once the article is complete: examples can be read aloud and the exercises can be answered.</summary>
    public bool Interactive { get; set; }
    public SpeechAccent? Accent { get; set; }

    private static bool HasCjk(string text) => text.Any(c => c >= '\u2E80');

    public Control? Concept(string text)
    {
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (paragraphs.Length == 0) return null;
        var body = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var paragraph in paragraphs) body.Children.Add(RichText.Create(paragraph, TextRole.Body));
        return body;
    }

    //  [肯定句]
    //  [主语] + [was/were] + [动词-ing]
    //  ❝ She was reading when I called. ❞
    public Control? Formulas(IReadOnlyList<GrammarFormula> formulas)
    {
        if (formulas.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var formula in formulas)
        {
            var body = new StackPanel { Spacing = AppTheme.Space.S };
            if (!string.IsNullOrWhiteSpace(formula.Label))
                body.Children.Add(StudioUi.CreateTag(formula.Label, TextRole.Label, AppTheme.Hues.Violet, wrap: true));
            body.Children.Add(Equation(formula.Pattern));
            if (formula.Example is { } example) AddExample(body, example);
            if (!string.IsNullOrWhiteSpace(formula.Note))
                body.Children.Add(RichBlocks.Annotation("注意", RichText.Create(formula.Note, TextRole.Annotation)));
            list.Children.Add(Tile(body));
        }
        return list;
    }

    private static Control Equation(string pattern)
    {
        var parts = pattern.Contains(" + ") ? pattern.Split(" + ") : pattern.Split('+');
        var equation = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
        var first = true;
        foreach (var part in parts.Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            var role = HasCjk(part);
            var label = TextStyles.Create(part, TextRole.Term, wrap: false);
            label.Foreground = role ? AppTheme.TextSecondary : AppTheme.Hues.Violet.Ink;
            Control chip = new Border
            {
                Background = role ? AppTheme.CardElevatedBg : AppTheme.Hues.Violet.Soft, CornerRadius = new CornerRadius(AppTheme.Radius.S),
                Padding = new Thickness(AppTheme.Space.M - 2, 3), Child = label
            };
            if (!first)
            {
                // An operator travels with the chip after it, so a plus sign can never end a line alone.
                var plus = new FontAwesomeIcon("plus", 12, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(chip, 1);
                chip = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S, Children = { plus, chip } };
            }
            first = false;
            equation.Children.Add(chip);
        }
        return equation;
    }

    public Control? Usages(IReadOnlyList<GrammarUsage> usages)
    {
        if (usages.Count == 0) return null;
        var number = 0;
        var rows = usages.Select(usage =>
        {
            number++;
            var body = new StackPanel { Spacing = AppTheme.Space.S };
            body.Children.Add(TextStyles.Selectable(usage.Title, TextRole.Term));
            if (!string.IsNullOrWhiteSpace(usage.Explanation)) body.Children.Add(RichText.Create(usage.Explanation, TextRole.Body));
            if (usage.Signals.Count > 0)
            {
                var chips = new WrapPanel { ItemSpacing = AppTheme.Space.XS, LineSpacing = AppTheme.Space.XS };
                foreach (var signal in usage.Signals) chips.Children.Add(StudioUi.CreateTag(signal, TextRole.Meta, AppTheme.Hues.Honey, wrap: true));
                body.Children.Add(StudioUi.CreateKeyValue("标志词", chips, "UsageLabel", labelTop: 2));
            }
            foreach (var example in usage.Examples) AddExample(body, example);
            if (!string.IsNullOrWhiteSpace(usage.Tip))
                body.Children.Add(RichBlocks.Callout("lightbulb", RichText.Create(usage.Tip, TextRole.Secondary), RichBlocks.CalloutTone.Attention));
            var ordinal = StudioUi.CreateOrdinal(number);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.M, Children = { ordinal, body } };
            Grid.SetColumn(body, 1);
            return (Control)row;
        }).ToArray();
        var rowsPanel = RichBlocks.RuledRows(rows);
        Grid.SetIsSharedSizeScope(rowsPanel, true);
        return rowsPanel;
    }

    public Control? Signals(IReadOnlyList<GrammarSignal> signals)
    {
        if (signals.Count == 0) return null;
        var chips = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
        foreach (var signal in signals)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS };
            var expression = TextStyles.Create(signal.Expression, TextRole.Term, wrap: false);
            expression.Foreground = AppTheme.Hues.Honey.Ink;
            expression.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(expression);
            if (!string.IsNullOrWhiteSpace(signal.Meaning))
            {
                var meaning = TextStyles.Create(signal.Meaning, TextRole.Annotation, wrap: false);
                meaning.Foreground = AppTheme.TextSecondary;
                meaning.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(meaning);
            }
            chips.Children.Add(new Border
            {
                Background = AppTheme.Hues.Honey.Soft, CornerRadius = new CornerRadius(AppTheme.Radius.S),
                Padding = new Thickness(AppTheme.Space.M - 2, 3), Child = content
            });
        }
        return chips;
    }

    public Control? Rules(IReadOnlyList<GrammarRule> rules)
    {
        if (rules.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.L };
        foreach (var rule in rules)
        {
            var body = new StackPanel { Spacing = AppTheme.Space.S };
            body.Children.Add(TextStyles.Selectable(rule.Title, TextRole.Term));
            if (!string.IsNullOrWhiteSpace(rule.Explanation)) body.Children.Add(RichText.Create(rule.Explanation, TextRole.Body));
            if (rule.Cases.Count > 0)
            {
                var notes = rule.Cases.Any(c => !string.IsNullOrWhiteSpace(c.Note));
                var rows = rule.Cases.Select(c =>
                {
                    var before = TextStyles.Selectable(c.Form, TextRole.Body);
                    var after = TextStyles.Selectable(c.Result, TextRole.Term);
                    after.Foreground = AppTheme.AccentBright;
                    var cells = new List<Control> { before, after };
                    if (notes) cells.Add(RichText.Create(c.Note, TextRole.Annotation));
                    return (IReadOnlyList<Control>)cells;
                }).ToArray();
                body.Children.Add(RichBlocks.Table(notes ? "Auto,Auto,*" : "*,*", notes ? ["变化前", "变化后", "说明"] : ["变化前", "变化后"], rows));
            }
            list.Children.Add(body);
        }
        return list;
    }

    public Control? Contrasts(IReadOnlyList<GrammarContrast> contrasts, string title)
    {
        if (contrasts.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var contrast in contrasts)
        {
            var heading = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS };
            heading.Children.Add(StudioUi.CreateTag("对比", TextRole.Label, AppTheme.Hues.Violet));
            heading.Children.Add(TextStyles.Selectable(contrast.Versus, TextRole.Term));
            if (isKnown(contrast.Versus) && Interactive) heading.Children.Add(KnownMark());
            var body = new StackPanel { Spacing = AppTheme.Space.S, Children = { heading } };
            if (!string.IsNullOrWhiteSpace(contrast.Difference)) body.Children.Add(RichText.Create(contrast.Difference, TextRole.Body));
            body.Children.Add(UsageContrastView.Create(new UsageContrast(
                string.IsNullOrWhiteSpace(contrast.LeftLabel) ? title : contrast.LeftLabel, contrast.LeftUse,
                string.IsNullOrWhiteSpace(contrast.RightLabel) ? contrast.Versus : contrast.RightLabel, contrast.RightUse)
            { LeftExample = contrast.LeftExample, RightExample = contrast.RightExample }, AddExample));
            if (Interactive)
            {
                var jump = OpenButton(contrast.Versus, $"精解「{contrast.Versus}」");
                body.Children.Add(jump);
            }
            list.Children.Add(Tile(body));
        }
        return list;
    }

    public Control? Exceptions(IReadOnlyList<GrammarException> exceptions)
    {
        if (exceptions.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var item in exceptions)
        {
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S };
            heading.Children.Add(new FontAwesomeIcon("triangle-exclamation", 14, AppTheme.Amber) { VerticalAlignment = VerticalAlignment.Center });
            heading.Children.Add(TextStyles.Selectable(item.Title, TextRole.Term));
            var body = new StackPanel { Spacing = AppTheme.Space.S, Children = { heading } };
            if (!string.IsNullOrWhiteSpace(item.Explanation)) body.Children.Add(RichText.Create(item.Explanation, TextRole.Body));
            if (item.Example is { } example) AddExample(body, example);
            list.Children.Add(Tile(body));
        }
        return list;
    }

    public static Control? ExamPoints(IReadOnlyList<string> points) =>
        points.Count == 0 ? null : RichBlocks.List(points.Select(p => (Control)RichText.Create(p, TextRole.Body)), numbered: true);

    public static Control? Mnemonic(string text) => string.IsNullOrWhiteSpace(text) ? null
        : RichBlocks.Callout("lightbulb", RichText.Create(text, TextRole.Body), RichBlocks.CalloutTone.Attention);

    public Control? Exercises(IReadOnlyList<GrammarExercise> exercises)
    {
        if (exercises.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        var number = 0;
        foreach (var exercise in exercises) list.Children.Add(Exercise(++number, exercise));
        return list;
    }

    private Control Exercise(int number, GrammarExercise exercise)
    {
        var question = TextStyles.Selectable(exercise.Question, TextRole.ExampleSource);
        var content = new StackPanel { Spacing = AppTheme.Space.S, Children = { question } };
        if (Interactive)
        {
            var feedback = new StackPanel { Spacing = AppTheme.Space.XS, IsVisible = false };
            var answered = false;
            void Reveal(bool? correct)
            {
                answered = true;
                feedback.IsVisible = true;
                var verdict = correct switch { true => "回答正确", false => "再想想", _ => "参考答案" };
                var hue = correct == false ? new Hue(AppTheme.Danger, AppTheme.DangerSoft, AppTheme.Danger) : AppTheme.Hues.Sage;
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS };
                head.Children.Add(new FontAwesomeIcon(correct == false ? "circle-xmark" : "circle-check", 15, hue.Ink));
                var verdictText = TextStyles.Create(verdict, TextRole.Label, wrap: false);
                verdictText.Foreground = hue.Ink;
                verdictText.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(verdictText);
                feedback.Children.Add(head);
                var answer = TextStyles.Selectable(exercise.Answer, TextRole.Term);
                answer.Foreground = AppTheme.Hues.Sage.Ink;
                feedback.Children.Add(StudioUi.CreateKeyValue("答案", answer, "ExerciseLabel", labelTop: 2));
                if (!string.IsNullOrWhiteSpace(exercise.Explanation))
                    feedback.Children.Add(StudioUi.CreateKeyValue("解析", RichText.Create(exercise.Explanation, TextRole.Secondary), "ExerciseLabel"));
            }
            if (exercise.Choices.Count > 0)
            {
                var options = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
                var buttons = new List<(Button Button, string Choice)>();
                for (var i = 0; i < exercise.Choices.Count; i++)
                {
                    var choice = exercise.Choices[i];
                    var letter = TextStyles.Create(((char)('A' + i)).ToString(), TextRole.Numeral, wrap: false);
                    letter.VerticalAlignment = VerticalAlignment.Center;
                    var text = TextStyles.Create(choice, TextRole.Body, wrap: false);
                    text.VerticalAlignment = VerticalAlignment.Center;
                    var button = new Button
                    {
                        Classes = { "ghost" }, Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderStrong, BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(AppTheme.Radius.M), Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.XS),
                        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.S, Children = { letter, text } }
                    };
                    AutomationProperties.SetName(button, $"选项 {choice}");
                    var picked = choice;
                    button.Click += (_, _) =>
                    {
                        if (answered) return;
                        var correct = picked == exercise.Answer;
                        foreach (var (other, value) in buttons)
                        {
                            other.IsHitTestVisible = false;
                            if (value == exercise.Answer) Tint(other, AppTheme.Hues.Sage);
                            else if (value == picked) Tint(other, new Hue(AppTheme.Danger, AppTheme.DangerSoft, AppTheme.Danger));
                        }
                        Reveal(correct);
                    };
                    buttons.Add((button, choice));
                    options.Children.Add(button);
                }
                content.Children.Add(options);
            }
            else
            {
                var show = StudioUi.CreateCompactButton("eye", "查看答案", () => { });
                show.HorizontalAlignment = HorizontalAlignment.Left;
                show.Click += (_, _) =>
                {
                    if (answered) return;
                    show.IsVisible = false;
                    Reveal(null);
                };
                content.Children.Add(show);
            }
            content.Children.Add(feedback);
        }
        var ordinal = StudioUi.CreateOrdinal(number);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.M, Children = { ordinal, content } };
        Grid.SetColumn(content, 1);
        var scope = new Border { Child = row };
        Grid.SetIsSharedSizeScope(scope, true);
        return Tile(scope);
    }

    private static void Tint(Button button, Hue hue)
    {
        button.Background = hue.Soft;
        button.BorderBrush = hue.Solid;
        if (button.Content is StackPanel panel)
            foreach (var text in panel.Children.OfType<TextBlock>()) text.Foreground = hue.Ink;
    }

    public Control? Related(IReadOnlyList<GrammarRelation> related)
    {
        if (related.Count == 0) return null;
        var chips = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
        foreach (var item in related)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS };
            var title = TextStyles.Create(item.Title, TextRole.Body, wrap: false);
            title.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(item.Relation))
            {
                var relation = TextStyles.Create(item.Relation, TextRole.Meta, wrap: false);
                relation.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(relation);
            }
            if (Interactive && isKnown(item.Title)) content.Children.Add(KnownMark());
            if (!Interactive)
            {
                chips.Children.Add(StudioUi.CreateTag(item.Title, TextRole.Secondary, wrap: true));
                continue;
            }
            var chip = new Button
            {
                Classes = { "ghost" }, Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderStrong, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Padding = new Thickness(AppTheme.Space.M - 2, 3), Content = content
            };
            AutomationProperties.SetName(chip, $"精解 {item.Title}");
            ToolTip.SetTip(chip, $"打开「{item.Title}」的语法精解");
            var target = item.Title;
            chip.Click += (_, _) => open(target);
            chips.Children.Add(chip);
        }
        return chips;
    }

    /// <summary>Small check shown beside a grammar point the local library already holds, so it opens without the model.</summary>
    public static Control KnownMark()
    {
        var mark = new FontAwesomeIcon("circle-check", 13, AppTheme.Hues.Sage.Ink) { VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(mark, "已在本地语法库，打开无需等待");
        return mark;
    }

    public Button OpenButton(string title, string text)
    {
        var button = StudioUi.CreateCompactButton("graduation-cap", text, () => open(title));
        button.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(button, isKnown(title) ? "已在本地语法库，秒开" : "由 AI 现场讲解，并存入本地语法库");
        return button;
    }

    public void AddExample(StackPanel body, BilingualExample example)
    {
        var line = HighlightedSource(example.SourceSentence, example.HighlightTerm, TextRole.ExampleSource);
        var pair = new StackPanel { Spacing = AppTheme.Space.XXS, Children = { line } };
        if (!string.IsNullOrWhiteSpace(example.TranslatedSentence))
        {
            var translation = TextStyles.Selectable(example.TranslatedSentence, TextRole.ExampleTarget);
            if (Interactive) DictionaryReadingText.EnableSelectionLookup(translation, lookup);
            pair.Children.Add(translation);
        }
        Button? read = null;
        if (Interactive && Accent is { } accent)
        {
            read = SpeechPlaybackButton.Create(click => StudioUi.CreateCompactButton("volume-high", "跟读", click),
                playback => PhonicsAudioBar.SpeakSentence(speech, settings, example.SourceSentence, accent, playback));
            ToolTip.SetTip(read, "朗读这句例句");
        }
        body.Children.Add(RichBlocks.Quote(pair, read));
    }

    // The grammar part of an example takes the accent color and a hairline; it is not a dictionary link.
    private SelectableTextBlock HighlightedSource(string sentence, string term, TextRole role)
    {
        var line = TextStyles.Selectable(null, role);
        var at = string.IsNullOrEmpty(term) ? -1 : DictionaryTextMatch.IndexOf(sentence, term, comparison: StringComparison.Ordinal);
        if (at < 0) line.Text = sentence;
        else
        {
            line.Inlines!.Add(new Run(sentence[..at]));
            line.Inlines.Add(new Run(sentence.Substring(at, term.Length))
            {
                Foreground = AppTheme.AccentBright, TextDecorations = DictionaryReadingText.LinkUnderline(AppTheme.AccentUnderline)
            });
            line.Inlines.Add(new Run(sentence[(at + term.Length)..]));
        }
        if (Interactive) DictionaryReadingText.EnableSelectionLookup(line, lookup);
        return line;
    }

    public static Border Tile(Control child) => new()
    {
        Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(AppTheme.Radius.M), Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2), Child = child
    };
}
