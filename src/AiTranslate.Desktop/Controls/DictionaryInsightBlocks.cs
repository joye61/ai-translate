using AiTranslate.Core;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Controls;

/// <summary>Visual forms for the AI-judged structured blocks of a dictionary result. Each method returns null when empty.</summary>
internal static partial class DictionaryInsightBlocks
{
    private static Control TargetText(string text, TextRole role, bool passage, Action<string, string>? lookup)
    {
        if (!passage && lookup is not null) return LinkText(text, () => lookup(text, ""), role);
        var reading = TextStyles.Selectable(text, role);
        if (lookup is not null) DictionaryReadingText.EnableSelectionLookup(reading, lookup);
        return reading;
    }

    public static Control? Options(IReadOnlyList<TranslationOption> options, bool passage, Action<string, string>? lookup)
    {
        if (options.Count < 2) return null;
        return RichBlocks.Table(passage ? "3*,2*" : "2*,3*", ["译法与含义", "异同与选用"], options.Select(option =>
        {
            var expression = TargetText(option.Text, passage ? TextRole.Definition : TextRole.Term, passage, lookup);
            var meaning = new StackPanel
            {
                Spacing = AppTheme.Space.XS, VerticalAlignment = VerticalAlignment.Top, Children = { expression }
            };
            if (!string.IsNullOrWhiteSpace(option.Meaning))
                meaning.Children.Add(RichBlocks.Annotation("含义", TextStyles.Selectable(option.Meaning, TextRole.Annotation)));
            var usage = RichText.Create(option.Usage, TextRole.Body);
            usage.VerticalAlignment = VerticalAlignment.Top;
            return (IReadOnlyList<Control>)[meaning, usage];
        }).ToArray());
    }

    public static Control? Variants(IReadOnlyList<TranslationVariant> variants, bool passage, Action<string>? copy, Action<string, string>? lookup)
    {
        if (variants.Count == 0) return null;
        return RichBlocks.RuledRows(variants.Select(variant =>
        {
            var tag = StudioUi.CreateTag(variant.Style, TextRole.Label, AppTheme.Hues.Violet, wrap: true);
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = AppTheme.Space.S };
            if (!string.IsNullOrWhiteSpace(variant.Style)) header.Children.Add(tag);
            if (copy is not null)
            {
                var button = CopyButton(() => copy(variant.Text));
                Grid.SetColumn(button, 1);
                header.Children.Add(button);
            }
            var role = passage ? TextRole.PassageTranslation : TextRole.Definition;
            var body = new StackPanel { Spacing = AppTheme.Space.XS };
            if (header.Children.Count > 0) body.Children.Add(header);
            body.Children.Add(TargetText(variant.Text, role, passage, lookup));
            if (!string.IsNullOrWhiteSpace(variant.Note))
                body.Children.Add(StudioUi.CreateKeyValue("选用说明", RichText.Create(variant.Note, TextRole.Annotation)));
            return body;
        }), framed: true);
    }

    public static Control? KeyPoints(IReadOnlyList<string> points) =>
        points.Count == 0 ? null : RichBlocks.List(points.Select(p => (Control)RichText.Create(p, TextRole.Body)), numbered: true);

    //  Not all  →  并非所有
    //  reason …
    //  备选  所有…都不   条件  若上下文…
    public static Control? Decisions(IReadOnlyList<TranslationDecision> decisions, Action<string>? lookup)
    {
        if (decisions.Count == 0) return null;
        var list = new StackPanel { Spacing = AppTheme.Space.S };
        foreach (var d in decisions)
        {
            var anchors = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = 2 };
            anchors.Children.Add(Centered(TextStyles.Selectable(d.SourceQuote, TextRole.ExampleSource)));
            if (!string.IsNullOrWhiteSpace(d.TargetQuote))
            {
                Control target = lookup is null ? TextStyles.Selectable(d.TargetQuote, TextRole.Term)
                    : LinkText(d.TargetQuote, () => lookup(d.TargetQuote));
                Grid.SetColumn(target, 1);
                anchors.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S,
                    Children = { new MdiIcon("arrow-right", 14, AppTheme.TextMuted)
                        { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) }, target }
                });
            }
            var item = new StackPanel { Spacing = AppTheme.Space.XS, Children = { anchors } };
            if (!string.IsNullOrWhiteSpace(d.Reason)) item.Children.Add(RichText.Create(d.Reason, TextRole.Secondary));
            if (!string.IsNullOrWhiteSpace(d.Condition))
            {
                var alt = new StackPanel { Spacing = 2 };
                Grid.SetIsSharedSizeScope(alt, true);
                if (!string.IsNullOrWhiteSpace(d.Alternative))
                    alt.Children.Add(StudioUi.CreateKeyValue("备选译法", lookup is null ? TextStyles.Selectable(d.Alternative, TextRole.Body)
                        : LinkText(d.Alternative, () => lookup(d.Alternative), TextRole.Body), "DecisionAlt"));
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
        return RichBlocks.RuledRows(highlights.Select(h =>
        {
            var head = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS };
            if (!string.IsNullOrWhiteSpace(h.Label))
                head.Children.Add(StudioUi.CreateTag(h.Label, TextRole.Label, AppTheme.Hues.Violet, wrap: true));
            if (!string.IsNullOrWhiteSpace(h.Quote)) head.Children.Add(TextStyles.Selectable(h.Quote, TextRole.ExampleSource));
            var analysis = new StackPanel { Spacing = AppTheme.Space.XS, Children = { head } };
            if (!string.IsNullOrWhiteSpace(h.Explanation)) analysis.Children.Add(RichText.Create(h.Explanation, TextRole.Secondary));
            return analysis;
        }));
    }

    public static Control? Structure(IReadOnlyList<ClauseNode> clauses)
    {
        if (clauses.Count == 0) return null;
        return RichBlocks.Tree(clauses.Select(c =>
        {
            var role = c.Depth == 0
                ? StudioUi.CreateChip(c.Role, foreground: AppTheme.Insight, background: AppTheme.InsightSoft)
                : StudioUi.CreateTag(c.Depth > 3 ? $"{c.Role} · 第 {c.Depth + 1} 层" : c.Role, TextRole.Label, wrap: true);
            role.VerticalAlignment = VerticalAlignment.Top;
            role.Margin = new Thickness(0, 1, 0, 0);
            var quote = TextStyles.Selectable(c.Quote, TextRole.ExampleSource);
            var text = new StackPanel { Spacing = 1, Children = { quote } };
            if (!string.IsNullOrWhiteSpace(c.Note)) text.Children.Add(RichText.Create(c.Note, TextRole.Annotation));
            var row = new ReadingGrid("Auto,*") { ColumnSpacing = AppTheme.Space.S, Children = { role, text } };
            Grid.SetColumn(text, 1);
            // Keep the full semantic depth in the label without consuming a narrow panel with indentation.
            return (Math.Min(c.Depth, 3), (Control)row);
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
            foreach (var t in tone.Tones) chips.Children.Add(StudioUi.CreateTag(t, TextRole.Secondary, wrap: true));
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
        var rows = new StackPanel { Spacing = AppTheme.Space.S };
        Grid.SetIsSharedSizeScope(rows, true);
        rows.Children.Add(StudioUi.CreateKeyValue("字面", TextStyles.Selectable(literal, TextRole.Body), "LiteralLabel"));
        if (!string.IsNullOrWhiteSpace(actual))
            rows.Children.Add(StudioUi.CreateKeyValue("实际", TextStyles.Selectable(actual, TextRole.Body), "LiteralLabel"));
        return new Border { Background = AppTheme.InputBg, CornerRadius = new CornerRadius(AppTheme.Radius.M),
            Padding = new Thickness(AppTheme.Space.M), Child = rows };
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
                var tile = MorphemeTile(parts[i]);
                equation.Children.Add(i == 0 ? tile : WithOperator("+", tile));
            }
            if (!string.IsNullOrWhiteSpace(headword))
            {
                equation.Children.Add(WithOperator("=", Centered(TextStyles.Create(headword, TextRole.Term))));
            }
            body.Children.Add(equation);
            var notes = new StackPanel { Spacing = AppTheme.Space.XS };
            Grid.SetIsSharedSizeScope(notes, true);
            foreach (var part in parts.Where(p => !string.IsNullOrWhiteSpace(p.Note)))
                notes.Children.Add(StudioUi.CreateKeyValue(part.Part, RichText.Create(part.Note, TextRole.Annotation), "MorphemeNote"));
            if (notes.Children.Count > 0) body.Children.Add(notes);
        }
        if (!string.IsNullOrWhiteSpace(etymology))
            body.Children.Add(parts.Count == 0 ? RichText.Create(etymology, TextRole.Secondary)
                : StudioUi.CreateKeyValue("词源", RichText.Create(etymology, TextRole.Secondary), "FormationLabel"));
        return body;

        // Wrap an operator with its operand so neither a plus nor an equals sign can end a line alone.
        static Control WithOperator(string symbol, Control operand)
        {
            var text = TextStyles.Create(symbol, TextRole.SectionTitle, wrap: false);
            text.Foreground = AppTheme.TextMuted;
            Grid.SetColumn(operand, 1);
            return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = AppTheme.Space.S,
                Children = { Centered(text), operand } };
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
        var form = TextStyles.Create(part.Part, TextRole.Term);
        form.FontSize = AppTheme.Type.BodyLarge;
        var meaning = TextStyles.Create(part.Meaning, TextRole.Secondary);
        meaning.FontSize = AppTheme.Type.Small;
        foreach (var t in new[] { kind, form, meaning }) { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; }
        return new Border
        {
            Background = hue.Soft, CornerRadius = new CornerRadius(AppTheme.Radius.M), MinWidth = 68, MaxWidth = 220,
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
                Control tag = string.IsNullOrWhiteSpace(r.Register) ? new Control() : StudioUi.CreateTag(r.Register, TextRole.Meta, wrap: true);
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
            AddLine(0, "close-circle", AppTheme.Danger, wrong);
            if (!string.IsNullOrWhiteSpace(m.Right))
            {
                var right = TextStyles.Selectable(m.Right, TextRole.Term);
                right.Foreground = AppTheme.Hues.Sage.Ink;
                AddLine(1, "check-circle", AppTheme.Hues.Sage.Ink, right);
            }
            if (!string.IsNullOrWhiteSpace(m.Why))
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var why = new Border { Background = AppTheme.InputBg, CornerRadius = new CornerRadius(AppTheme.Radius.S),
                    Padding = new Thickness(AppTheme.Space.S, AppTheme.Space.XS), Margin = new Thickness(0, AppTheme.Space.XS, 0, 0),
                    Child = RichText.Create(m.Why, TextRole.Annotation) };
                Grid.SetRow(why, grid.RowDefinitions.Count - 1);
                Grid.SetColumnSpan(why, 2);
                grid.Children.Add(why);
            }
            list.Children.Add(new Border
            {
                Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(AppTheme.Radius.M),
                Padding = new Thickness(AppTheme.Space.M, AppTheme.Space.S + 2), Child = grid
            });

            void AddLine(int row, string icon, IBrush brush, TextBlock text)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                const double iconSize = 15;
                // The visible letters sit slightly below the center of the first line box.
                var iconTop = (text.LineHeight - iconSize) / 2 + 1.5;
                var glyph = new MdiIcon(icon, iconSize, brush)
                {
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, iconTop, 0, 0)
                };
                Grid.SetRow(glyph, row);
                Grid.SetRow(text, row);
                Grid.SetColumn(text, 1);
                grid.Children.Add(glyph);
                grid.Children.Add(text);
            }
        }
        return list;
    }

    public static Control? Alignment(IReadOnlyList<AlignedSentence> rows, Func<string, SelectableTextBlock> source,
        Action<string, string>? targetLookup)
    {
        if (rows.Count == 0) return null;
        return RichBlocks.TableFrame(new SentenceAlignmentGrid(rows, source, targetLookup));
    }

    internal static Button LinkText(string text, Action click, TextRole role = TextRole.Term)
    {
        var button = new Button
        {
            Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(AppTheme.Space.XXS, 0), Margin = new Thickness(-AppTheme.Space.XXS, 0, 0, 0),
            CornerRadius = new CornerRadius(AppTheme.Radius.S), Background = Brushes.Transparent, BorderThickness = default,
            Content = TextStyles.Apply(new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, TextDecorations = DictionaryReadingText.LinkUnderline()
            }, role)
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
