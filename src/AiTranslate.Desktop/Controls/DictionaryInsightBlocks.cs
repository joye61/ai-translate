using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Controls;

/// <summary>Visual forms for the AI-judged structured blocks of a dictionary result. Each method returns null when empty.</summary>
internal static class DictionaryInsightBlocks
{
    public static Control? Variants(IReadOnlyList<TranslationVariant> variants, bool passage, Action<string>? copy)
    {
        if (variants.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.M + 2 };
        Grid.SetIsSharedSizeScope(list, true);
        foreach (var variant in variants)
        {
            var tag = StudioUi.CreateTag(variant.Style, TextRole.Label, AppTheme.Hues.Violet);
            tag.VerticalAlignment = VerticalAlignment.Top;
            tag.Margin = new Thickness(0, passage ? 3 : 1, 0, 0);
            var body = new StackPanel { Spacing = 2, Children = { TextStyles.Selectable(variant.Text, passage ? TextRole.PassageTranslation : TextRole.Definition) } };
            if (!string.IsNullOrWhiteSpace(variant.Note)) body.Children.Add(RichText.Create(variant.Note, TextRole.Secondary));
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "VariantStyle" },
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = AppTheme.Space.M,
                Children = { tag, body }
            };
            Grid.SetColumn(body, 1);
            if (copy is not null)
            {
                var button = CopyButton(() => copy(variant.Text));
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
            }
            list.Children.Add(row);
        }
        return list;
    }

    public static Control? KeyPoints(IReadOnlyList<string> points) =>
        points.Count == 0 ? null : RichBlocks.List(points.Select(p => (Control)RichText.Create(p, TextRole.Body)), numbered: true);

    //  Not all  →  并非所有
    //  reason …
    //  备选  所有…都不   条件  若上下文…
    public static Control? Decisions(IReadOnlyList<TranslationDecision> decisions)
    {
        if (decisions.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var d in decisions)
        {
            var anchors = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = 2 };
            anchors.Children.Add(Centered(TextStyles.Selectable(d.SourceQuote, TextRole.ExampleSource)));
            anchors.Children.Add(new MdiIcon("arrow-right", 14, AppTheme.TextMuted) { VerticalAlignment = VerticalAlignment.Center });
            anchors.Children.Add(Centered(TextStyles.Selectable(d.TargetQuote, TextRole.Term)));
            var item = new StackPanel { Spacing = AppTheme.Space.XXS, Children = { anchors, RichText.Create(d.Reason, TextRole.Secondary) } };
            if (!string.IsNullOrWhiteSpace(d.Alternative) && !string.IsNullOrWhiteSpace(d.Condition))
            {
                var alt = new StackPanel { Spacing = 2 };
                Grid.SetIsSharedSizeScope(alt, true);
                alt.Children.Add(StudioUi.CreateKeyValue("备选译法", TextStyles.Selectable(d.Alternative, TextRole.Body), "DecisionAlt"));
                alt.Children.Add(StudioUi.CreateKeyValue("适用条件", RichText.Create(d.Condition, TextRole.Secondary), "DecisionAlt"));
                item.Children.Add(new Border
                {
                    Background = AppTheme.InputBg, CornerRadius = new CornerRadius(AppTheme.Radius.S),
                    Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S), Margin = new Thickness(0, AppTheme.Space.XS, 0, 0), Child = alt
                });
            }
            // Same bounded tile as 常见错误: each source→target decision is one self-contained comparison.
            list.Children.Add(new Border
            {
                Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(AppTheme.Radius.M),
                Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2), Child = item
            });
        }
        return list;
    }

    public static Control? Grammar(IReadOnlyList<GrammarHighlight> highlights)
    {
        if (highlights.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.L };
        foreach (var h in highlights)
        {
            var quote = TextStyles.Selectable(null, TextRole.ExampleSource);
            quote.Inlines!.Add(new Run(h.Quote) { Background = AppTheme.InsightSoft, Foreground = AppTheme.Insight });
            var head = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = 2, Children = { StudioUi.CreateTag(h.Label, TextRole.Label), Centered(quote) } };
            list.Children.Add(new StackPanel { Spacing = AppTheme.Space.XXS, Children = { head, RichText.Create(h.Explanation, TextRole.Secondary) } });
        }
        return list;
    }

    public static Control? Structure(IReadOnlyList<ClauseNode> clauses)
    {
        if (clauses.Count == 0) return null;
        return RichBlocks.Tree(clauses.Select(c =>
        {
            var role = c.Depth == 0
                ? StudioUi.CreateChip(c.Role, foreground: AppTheme.Insight, background: AppTheme.InsightSoft)
                : StudioUi.CreateTag(c.Role, TextRole.Label);
            role.VerticalAlignment = VerticalAlignment.Top;
            role.Margin = new Thickness(0, 1, 0, 0);
            var quote = TextStyles.Selectable(c.Quote, TextRole.ExampleSource);
            var text = new StackPanel { Spacing = 1, Children = { quote } };
            if (!string.IsNullOrWhiteSpace(c.Note)) text.Children.Add(RichText.Create(c.Note, TextRole.Secondary));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S, Children = { role, text } };
            Grid.SetColumn(text, 1);
            return (c.Depth, (Control)row);
        }));
    }

    public static Control? Tone(ToneProfile? tone)
    {
        if (tone is null || tone.IsEmpty) return null;
        var rows = new StackPanel { Spacing = AppTheme.Space.S };
        Grid.SetIsSharedSizeScope(rows, true);
        if (tone.Formality is >= 1 and <= 5)
            rows.Children.Add(StudioUi.CreateKeyValue("正式度", RichBlocks.Meter(tone.Formality, 5, "口语", "正式"), "ToneLabel"));
        if (tone.Tones.Count > 0)
        {
            var chips = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
            foreach (var t in tone.Tones) chips.Children.Add(StudioUi.CreateTag(t, TextRole.Secondary));
            rows.Children.Add(StudioUi.CreateKeyValue("语气", chips, "ToneLabel", labelTop: 3));
        }
        if (!string.IsNullOrWhiteSpace(tone.Note))
            rows.Children.Add(rows.Children.Count == 0 ? RichText.Create(tone.Note, TextRole.Secondary)
                : StudioUi.CreateKeyValue("说明", RichText.Create(tone.Note, TextRole.Secondary), "ToneLabel"));
        return rows;
    }

    public static Control? Literal(string literal, string actual)
    {
        if (string.IsNullOrWhiteSpace(literal)) return null;
        var rows = new StackPanel { Spacing = AppTheme.Space.XS };
        Grid.SetIsSharedSizeScope(rows, true);
        rows.Children.Add(StudioUi.CreateKeyValue("字面", TextStyles.Selectable(literal, TextRole.Secondary), "LiteralLabel"));
        if (!string.IsNullOrWhiteSpace(actual))
            rows.Children.Add(StudioUi.CreateKeyValue("实际", TextStyles.Selectable(actual, TextRole.Body), "LiteralLabel"));
        return rows;
    }

    public static Control? Note(string? text, string icon, RichBlocks.CalloutTone tone) =>
        string.IsNullOrWhiteSpace(text) ? null : RichBlocks.Callout(icon, RichText.Create(text, TextRole.Body), tone);

    //  [in-] + [spect] + [-ion]  =  inspection
    //  词源 …
    public static Control? WordFormation(IReadOnlyList<Morpheme> parts, string? etymology, string headword)
    {
        if (parts.Count == 0 && string.IsNullOrWhiteSpace(etymology)) return null;
        var body = new StackPanel { Spacing = AppTheme.Space.M };
        if (parts.Count > 0)
        {
            var equation = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0) equation.Children.Add(Operator("+"));
                equation.Children.Add(MorphemeTile(parts[i]));
            }
            if (!string.IsNullOrWhiteSpace(headword))
            {
                equation.Children.Add(Operator("="));
                equation.Children.Add(Centered(TextStyles.Create(headword, TextRole.Term, wrap: false)));
            }
            body.Children.Add(equation);
        }
        if (!string.IsNullOrWhiteSpace(etymology))
            body.Children.Add(parts.Count == 0 ? RichText.Create(etymology, TextRole.Secondary)
                : StudioUi.CreateKeyValue("词源", RichText.Create(etymology, TextRole.Secondary), "FormationLabel"));
        return body;

        static Control Operator(string symbol)
        {
            var text = TextStyles.Create(symbol, TextRole.SectionTitle, wrap: false);
            text.Foreground = AppTheme.TextMuted;
            return Centered(text);
        }
    }

    private static Control MorphemeTile(Morpheme part)
    {
        var hue = part.Kind switch
        {
            MorphemeKind.Prefix => AppTheme.Hues.Sky,
            MorphemeKind.Suffix => AppTheme.Hues.Jade,
            _ => AppTheme.Hues.Violet
        };
        var kind = TextStyles.Create(part.Kind switch { MorphemeKind.Prefix => "前缀", MorphemeKind.Suffix => "后缀", _ => "词根" },
            TextRole.Meta, wrap: false);
        kind.Foreground = hue.Ink;
        var form = TextStyles.Create(part.Part, TextRole.Term, wrap: false);
        form.FontSize = AppTheme.Type.BodyLarge;
        var meaning = TextStyles.Create(part.Meaning, TextRole.Secondary);
        meaning.MaxWidth = 140;
        meaning.FontSize = AppTheme.Type.Small;
        foreach (var t in new[] { kind, form, meaning }) { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; }
        return new Border
        {
            Background = hue.Soft, CornerRadius = new CornerRadius(AppTheme.Radius.M), MinWidth = 68,
            Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S - 2, AppTheme.Space.M, AppTheme.Space.S),
            Child = new StackPanel { Spacing = 0, Children = { kind, form, meaning } }
        };
    }

    public static Control? Synonyms(IReadOnlyList<SynonymNuance> rows, string headword, Action<string>? lookup)
    {
        if (rows.Count == 0) return null;
        var register = rows.Any(r => !string.IsNullOrWhiteSpace(r.Register));
        var self = new HashSet<int>();
        var cells = rows.Select((r, i) =>
        {
            var isSelf = string.Equals(r.Word.Trim(), headword.Trim(), StringComparison.OrdinalIgnoreCase);
            if (isSelf) self.Add(i);
            Control word = lookup is null || isSelf ? TextStyles.Selectable(r.Word, TextRole.Term) : LinkText(r.Word, () => lookup(r.Word));
            var cellsInRow = new List<Control> { word, RichText.Create(r.Nuance, TextRole.Body) };
            if (register)
            {
                Control tag = string.IsNullOrWhiteSpace(r.Register) ? new Control() : StudioUi.CreateTag(r.Register, TextRole.Meta);
                // Every cell hangs from the row top; a centred tag drifts away from a wrapped two-line nuance.
                tag.VerticalAlignment = VerticalAlignment.Top;
                tag.Margin = new Thickness(0, 1, 0, 0);
                cellsInRow.Add(tag);
            }
            return (IReadOnlyList<Control>)cellsInRow;
        }).ToArray();
        return RichBlocks.Table(register ? "Auto,*,Auto" : "Auto,*", register ? ["词语", "侧重与区别", "语域"] : ["词语", "侧重与区别"], cells, self);
    }

    //  ┌──────────────────────────────┐
    //  │ ✕ discuss about the plan      │
    //  │ ✓ discuss the plan            │
    //  │   discuss 是及物动词…           │
    //  └──────────────────────────────┘
    public static Control? Mistakes(IReadOnlyList<UsageMistake> mistakes)
    {
        if (mistakes.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var m in mistakes)
        {
            var wrong = TextStyles.Selectable(m.Wrong, TextRole.Body);
            wrong.Foreground = AppTheme.Danger;
            wrong.TextDecorations = [new TextDecoration
            {
                Location = TextDecorationLocation.Strikethrough, Stroke = AppTheme.Danger,
                StrokeThickness = 1, StrokeThicknessUnit = TextDecorationUnit.Pixel
            }];
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S, RowSpacing = 3 };
            AddLine(0, "close-circle-outline", AppTheme.Danger, wrong);
            if (!string.IsNullOrWhiteSpace(m.Right))
            {
                var right = TextStyles.Selectable(m.Right, TextRole.Term);
                right.Foreground = AppTheme.Hues.Sage.Ink;
                AddLine(1, "check-circle-outline", AppTheme.Hues.Sage.Ink, right);
            }
            if (!string.IsNullOrWhiteSpace(m.Why))
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var why = RichText.Create(m.Why, TextRole.Secondary);
                why.Margin = new Thickness(0, 2, 0, 0);
                Grid.SetRow(why, grid.RowDefinitions.Count - 1);
                Grid.SetColumn(why, 1);
                grid.Children.Add(why);
            }
            list.Children.Add(new Border
            {
                Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(AppTheme.Radius.M),
                Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2), Child = grid
            });

            void AddLine(int row, string icon, IBrush brush, Control text)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var glyph = new MdiIcon(icon, 15, brush) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
                Grid.SetRow(glyph, row);
                Grid.SetRow(text, row);
                Grid.SetColumn(text, 1);
                grid.Children.Add(glyph);
                grid.Children.Add(text);
            }
        }
        return list;
    }

    public static Control? Alignment(IReadOnlyList<AlignedSentence> rows, Func<string, SelectableTextBlock> source)
    {
        if (rows.Count == 0) return null;
        return RichBlocks.Table("Auto,*,*", ["#", "原文", "译文"], rows.Select((r, i) => (IReadOnlyList<Control>)
        [
            TextStyles.Create((i + 1).ToString("D2"), TextRole.Numeral, wrap: false),
            TextStyles.Apply(source(r.Source), TextRole.ExampleSource),
            TextStyles.Selectable(r.Target, TextRole.Body)
        ]).ToArray());
    }

    internal static Button LinkText(string text, Action click)
    {
        var button = new Button
        {
            Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(AppTheme.Space.XXS, 0), Margin = new Thickness(-AppTheme.Space.XXS, 0, 0, 0),
            CornerRadius = new CornerRadius(AppTheme.Radius.S), Background = Brushes.Transparent, BorderThickness = default,
            Content = TextStyles.Apply(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, TextDecorations = DictionaryReadingText.LinkUnderline()
            }, TextRole.Term)
        };
        AutomationProperties.SetName(button, $"查词 {text}");
        ToolTip.SetTip(button, $"查询 “{text}”");
        button.Click += (_, _) => click();
        return button;
    }

    private static Button CopyButton(Action copy)
    {
        var button = StudioUi.CreateCompactButton("content-copy", "复制", () => { });
        button.VerticalAlignment = VerticalAlignment.Top;
        ToolTip.SetTip(button, "复制这条译文");
        button.Click += (_, _) =>
        {
            copy();
            ToolTip.SetTip(button, "已复制");
            DispatcherTimer.RunOnce(() => ToolTip.SetTip(button, "复制这条译文"), TimeSpan.FromMilliseconds(1400));
        };
        return button;
    }

    private static T Centered<T>(T control) where T : Control
    {
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }
}
