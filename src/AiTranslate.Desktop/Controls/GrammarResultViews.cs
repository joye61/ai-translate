using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop.Controls;

/// <summary>Reading forms for the results of the grammar studio that are not a full article: a sentence's grammar points and a list of candidates.</summary>
internal static class GrammarResultViews
{
    private static Control Header(Action? goBack, string kind, Hue hue, string? icon, params string[] extra)
    {
        var meta = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Center };
        if (goBack is not null)
        {
            var back = StudioUi.CreateCompactButton("arrow-left", "返回上一级", goBack);
            back.Margin = new Thickness(-AppTheme.Space.S, 0, 0, 0);
            ToolTip.SetTip(back, "回到上一个结果");
            meta.Children.Add(back);
        }
        meta.Children.Add(StudioUi.CreateChip(kind, icon, hue.Ink, hue.Soft));
        foreach (var text in extra) meta.Children.Add(StudioUi.CreateChip(text, null, AppTheme.TextSecondary, AppTheme.CardElevatedBg));
        return meta;
    }

    //  句子语法解析 · 英语
    //  She was reading ¹ when I called ² her.            ← numbered marks in the sentence
    //  01 过去进行时   was reading   怎么构成、起什么作用      [精解 ›]
    public static Control Sentence(GrammarSentenceAnalysis analysis, Func<string, bool> isKnown, Action<string> open,
        Action<string, string> lookup, Action? goBack)
    {
        var page = new StackPanel { Spacing = AppTheme.Space.M, Classes = { "dictionary-reading" } };
        page.Children.Add(Header(goBack, "句子语法解析", AppTheme.Hues.Honey, "sitemap",
            analysis.Language == "en" ? [] : [UiLabels.Language(analysis.Language)]));

        var source = TextStyles.Selectable(null, TextRole.PassageTranslation);
        source.FontSize = AppTheme.Type.Title;
        source.LineHeight = 30;
        FillMarkedSource(source, analysis);
        DictionaryReadingText.EnableSelectionLookup(source, lookup);
        var reading = new StackPanel { Spacing = AppTheme.Space.S, Children = { source } };
        if (!string.IsNullOrWhiteSpace(analysis.Translation))
        {
            var translation = TextStyles.Selectable(analysis.Translation, TextRole.ExampleTarget);
            DictionaryReadingText.EnableSelectionLookup(translation, lookup);
            reading.Children.Add(translation);
        }
        var translate = StudioUi.CreateCompactButton("language", "在查词翻译中打开整句", () => lookup(analysis.Source, ""));
        translate.HorizontalAlignment = HorizontalAlignment.Left;
        translate.Margin = new Thickness(-AppTheme.Space.S, AppTheme.Space.XXS, 0, -AppTheme.Space.XS);
        ToolTip.SetTip(translate, "回到查词翻译，查看整句译文与逐词释义");
        reading.Children.Add(translate);
        page.Children.Add(new Border
        {
            Background = AppTheme.InputBg, CornerRadius = new CornerRadius(AppTheme.Radius.L),
            Padding = new Thickness(AppTheme.Space.L, AppTheme.Space.M + 2), Child = reading
        });

        if (!string.IsNullOrWhiteSpace(analysis.Structure))
        {
            var label = TextStyles.Create("句子主干", TextRole.Label);
            label.Foreground = AppTheme.Insight;
            page.Children.Add(RichBlocks.Callout("sitemap",
                new StackPanel { Spacing = 2, Children = { label, RichText.Create(analysis.Structure, TextRole.Body) } }, RichBlocks.CalloutTone.Insight));
        }

        var cards = new StackPanel { Spacing = AppTheme.Space.S };
        for (var i = 0; i < analysis.Points.Count; i++) cards.Children.Add(PointCard(i + 1, analysis.Points[i], isKnown, open));
        var section = new DictionarySection("句中语法", cards, insight: true);
        section.Caption.Text = $"{analysis.Points.Count} 项";
        section.Caption.IsVisible = true;
        page.Children.Add(section);
        return page;
    }

    private static void FillMarkedSource(SelectableTextBlock block, GrammarSentenceAnalysis analysis)
    {
        var text = analysis.Source;
        var marks = new List<(int Start, int End, int Number)>();
        for (var i = 0; i < analysis.Points.Count; i++)
        {
            var quote = analysis.Points[i].Quote;
            var start = DictionaryTextMatch.IndexOf(text, quote, comparison: StringComparison.Ordinal);
            if (start < 0 || marks.Any(m => start < m.End && start + quote.Length > m.Start)) continue;
            marks.Add((start, start + quote.Length, i + 1));
        }
        if (marks.Count == 0)
        {
            block.Text = text;
            return;
        }
        var position = 0;
        foreach (var mark in marks.OrderBy(m => m.Start))
        {
            if (mark.Start > position) block.Inlines!.Add(new Run(text[position..mark.Start]));
            // Color and a tint only: a weight change mid-sentence makes later CJK runs render bold.
            block.Inlines!.Add(new Run(text[mark.Start..mark.End]) { Foreground = AppTheme.Insight, Background = AppTheme.InsightSoft });
            block.Inlines.Add(new Run(mark.Number.ToString())
            {
                FontSize = 11, BaselineAlignment = BaselineAlignment.Superscript, Foreground = AppTheme.Insight
            });
            position = mark.End;
        }
        if (position < text.Length) block.Inlines!.Add(new Run(text[position..]));
    }

    private static Control PointCard(int number, GrammarSentencePoint point, Func<string, bool> isKnown, Action<string> open)
    {
        // The canonical name heads the card, because it is what the action below opens; the expression
        // and its role in this sentence come next, then the explanation.
        var heading = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS };
        if (!string.IsNullOrWhiteSpace(point.Category))
            heading.Children.Add(StudioUi.CreateChip(point.Category, HueMap.GrammarCategory(point.Category)));
        heading.Children.Add(TextStyles.Selectable(point.Topic, TextRole.Term));
        var quote = new Border
        {
            Background = AppTheme.InsightSoft, CornerRadius = new CornerRadius(AppTheme.Radius.S), VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(AppTheme.Space.S, 2),
            Child = TextStyles.Selectable(point.Quote, TextRole.ExampleSource)
        };
        ((SelectableTextBlock)quote.Child).Foreground = AppTheme.Insight;
        var expression = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS, Children = { quote } };
        var role = point.Label.Trim();
        if (role.Length > 0 && !role.Equals(point.Quote.Trim(), StringComparison.OrdinalIgnoreCase) &&
            !role.Equals(point.Topic.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var text = TextStyles.Selectable(role, TextRole.Secondary);
            text.VerticalAlignment = VerticalAlignment.Center;
            expression.Children.Add(text);
        }
        var body = new StackPanel { Spacing = AppTheme.Space.S, Children = { heading, expression, RichText.Create(point.Explanation, TextRole.Body) } };

        var known = isKnown(point.Topic);
        var action = StudioUi.CreateCompactButton("graduation-cap", $"精解「{point.Topic}」", () => open(point.Topic));
        action.HorizontalAlignment = HorizontalAlignment.Left;
        action.Margin = new Thickness(-AppTheme.Space.S, 0, 0, 0);
        ToolTip.SetTip(action, known ? "已在本地语法库，秒开" : "由 AI 现场讲解，并存入本地语法库");
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Space.XS, Children = { action } };
        if (known)
        {
            var mark = GrammarBlocks.KnownMark();
            var note = TextStyles.Create("已收录", TextRole.Meta, wrap: false);
            note.VerticalAlignment = VerticalAlignment.Center;
            footer.Children.Add(mark);
            footer.Children.Add(note);
        }
        body.Children.Add(footer);

        var ordinal = StudioUi.CreateOrdinal(number);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.M, Children = { ordinal, body } };
        Grid.SetColumn(body, 1);
        return GrammarBlocks.Tile(row);
    }

    public static Control Choices(string message, IReadOnlyList<GrammarChoice> choices, Func<string, bool> isKnown, Action<string> open, Action? goBack)
    {
        var page = new StackPanel { Spacing = AppTheme.Space.M, Classes = { "dictionary-reading" } };
        page.Children.Add(Header(goBack, "选择语法点", AppTheme.Hues.Violet, "list-check"));
        page.Children.Add(TextStyles.Create(string.IsNullOrWhiteSpace(message) ? "你想了解哪一个语法点？" : message, TextRole.PageTitle));
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var choice in choices)
        {
            var title = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS };
            if (!string.IsNullOrWhiteSpace(choice.Category)) title.Children.Add(StudioUi.CreateChip(choice.Category, HueMap.GrammarCategory(choice.Category)));
            title.Children.Add(TextStyles.Create(choice.Title, TextRole.Term));
            if (isKnown(choice.Title)) title.Children.Add(GrammarBlocks.KnownMark());
            var text = new StackPanel { Spacing = 2, Children = { title } };
            if (!string.IsNullOrWhiteSpace(choice.Summary)) text.Children.Add(TextStyles.Create(choice.Summary, TextRole.Secondary));
            var arrow = new FontAwesomeIcon("arrow-right", 14, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 1);
            var target = choice.Title;
            var card = new Button
            {
                Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(AppTheme.Radius.M), Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2),
                Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.M, Children = { text, arrow } }
            };
            AutomationProperties.SetName(card, $"精解 {choice.Title}");
            card.Click += (_, _) => open(target);
            list.Children.Add(card);
        }
        page.Children.Add(list);
        return page;
    }
}
