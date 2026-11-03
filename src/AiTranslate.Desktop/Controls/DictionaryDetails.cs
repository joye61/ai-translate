using System.Text.Json;
using AiTranslate.Core;
using AiTranslate.Speech;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiTranslate.Desktop.Controls;

internal sealed partial class DictionaryDetails(ISpeechSynthesisService speech, AppSettingsStore settings, Action<string, string, bool> lookup,
    Action<SentenceCloze> practice, DictionaryDisclosures? disclosures = null) : StackPanel
{
    private sealed record Row(string Label, string Text, string? Note = null, BilingualExample? Example = null, bool Link = false,
        UsageContrast? Contrast = null, PartOfSpeechSense? Sense = null, int Number = 0, bool Chips = false, SenseHeading? Heading = null,
        WordInflection? SenseForm = null);
    private enum Layout { Columns, Stacked, Forms, Senses, Collocations }
    private readonly DictionaryDisclosures _disclosures = disclosures ?? new();
    private const string ChipSeparator = "  ·  ";
    private sealed class SectionState
    {
        public ContentControl Host { get; } = new() { IsVisible = false, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        public ContentControl Body { get; } = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        public DictionarySection? Section { get; set; }
        public Panel? Items { get; set; }
        public Row[] Rows { get; set; } = [];
        public bool Interactive { get; set; }
        public bool HasLabels { get; set; }
        public string? Signature { get; set; }
    }
    private readonly Dictionary<EntryBlock, SectionState> _sections = [];
    private VocabularyItemType? _type;
    private DictionaryEntryResult? _finished;
    private SpeechAccent? _readAccent;

    public void ShowSource(string source)
    {
        var spec = EntryBlockCatalog.For(VocabularyItemType.Sentence).Single(s => s.Block == EntryBlock.Source);
        Custom(spec, source, () => TextStyles.Selectable(source, TextRole.PassageTranslation));
    }

    public void ShowEntry(DictionaryEntryResult entry, bool complete)
    {
        _finished = complete ? entry : null;
        _readAccent = PhonicsAudioBar.SourceAccent(entry.DetectedSourceLanguage);
        if (_type != entry.ItemType)
        {
            Children.Clear();
            _sections.Clear();
            _type = entry.ItemType;
        }
        var pairs = entry.Collocations.Select((c, i) => new Row(c.Phrase, c.Meaning, c.SenseHint,
            complete ? entry.GetCollocationExample(c) : c.Example, Link: true, Number: i + 1)).ToArray();
        var senses = SenseRows(entry, complete, out var remainingForms);
        foreach (var spec in EntryBlockCatalog.For(entry.ItemType)) Render(spec, entry, pairs, senses, remainingForms);
    }

    private void Render(EntryBlockSpec spec, DictionaryEntryResult entry, Row[] pairs, Row[] senses, IEnumerable<WordInflection> remainingForms)
    {
        var passage = entry.ItemType.IsPassage();
        switch (spec.Block)
        {
            case EntryBlock.Source:
                var aligned = entry.ItemType == VocabularyItemType.Paragraph && entry.Alignment.Count > 0;
                object source = _finished is null ? new { entry.SourceText, entry.Alignment }
                    : new { entry.SourceText, entry.Alignment, entry.Collocations, entry.GlossaryReferences, entry.GrammarHighlights };
                Custom(spec with { Title = aligned ? "原文对照" : spec.Title }, source,
                    () => aligned ? DictionaryInsightBlocks.Alignment(entry.Alignment, SourceText) : SourceText(entry.SourceText));
                break;
            case EntryBlock.KeyPoints:
                Custom(spec, entry.KeyPoints, () => DictionaryInsightBlocks.KeyPoints(entry.KeyPoints));
                break;
            case EntryBlock.Variants:
                Custom(spec, entry.Variants, () => DictionaryInsightBlocks.Variants(entry.Variants, passage, _finished is null ? null : Copy));
                break;
            case EntryBlock.Decisions:
                Custom(spec, entry.TranslationDecisions, () => DictionaryInsightBlocks.Decisions(entry.TranslationDecisions));
                break;
            case EntryBlock.Structure:
                Custom(spec, entry.Clauses, () => DictionaryInsightBlocks.Structure(entry.Clauses));
                break;
            case EntryBlock.Grammar:
                Custom(spec, entry.GrammarHighlights, () => DictionaryInsightBlocks.Grammar(entry.GrammarHighlights));
                break;
            case EntryBlock.Senses:
                Section(spec, senses, Layout.Senses, unit: "个义项");
                break;
            case EntryBlock.LiteralMeaning:
                Custom(spec, new { entry.LiteralMeaning, entry.PrimaryTranslation },
                    () => DictionaryInsightBlocks.Literal(entry.LiteralMeaning, entry.PrimaryTranslation));
                break;
            case EntryBlock.ContextMeaning:
                Custom(spec, entry.ContextMeaning, () => DictionaryInsightBlocks.Note(entry.ContextMeaning, "format-quote-close", RichBlocks.CalloutTone.Insight));
                break;
            case EntryBlock.Inflections:
                var title = entry.ItemType == VocabularyItemType.Word && entry.Origin == DictionaryResultOrigin.Cache && entry.WordFamily.Count == 0
                    ? "词形与相关形式" : spec.Title;
                Section(spec with { Title = title }, remainingForms.Select(i => new Row(i.Label, i.Value)), Layout.Forms);
                break;
            case EntryBlock.Collocations:
                Section(spec, pairs, Layout.Collocations, unit: passage ? null : "条");
                break;
            case EntryBlock.Examples:
                var attached = pairs.Select(p => p.Example?.SourceSentence).Concat(senses.Select(row => row.Example?.SourceSentence))
                    .OfType<string>().ToHashSet(StringComparer.Ordinal);
                var extra = entry.Examples.Where(e => !attached.Contains(e.SourceSentence)).ToArray();
                // Sense hints label the examples when present; otherwise they share the ordinal badge used by every numbered list.
                var hinted = extra.Any(e => !string.IsNullOrWhiteSpace(e.SenseHint));
                Section(spec, extra.Select((e, i) => hinted
                    ? new Row(string.IsNullOrWhiteSpace(e.SenseHint) ? $"{i + 1:D2}" : e.SenseHint, "", Example: e)
                    : new Row("", "", Example: e, Number: i + 1)), unit: "句");
                break;
            case EntryBlock.UsageContrasts:
                Section(spec, entry.UsageContrasts.Select(c => new Row("", "", Contrast: c)));
                break;
            case EntryBlock.SynonymNuances:
                Custom(spec, entry.SynonymNuances, () => DictionaryInsightBlocks.Synonyms(entry.SynonymNuances, entry.SourceText,
                    _finished is null ? null : word => lookup(word, "", false)));
                break;
            case EntryBlock.Mistakes:
                Custom(spec, entry.Mistakes, () => DictionaryInsightBlocks.Mistakes(entry.Mistakes));
                break;
            case EntryBlock.UsageNotes:
                Section(spec, passage
                    ? Notes(("句式", entry.GrammarBreakdown), ("用法", entry.UsageNotes), ("语境", entry.ContextMeaning))
                    : Notes(("使用提示", entry.UsageNotes), ("近义辨析", entry.SynonymNuances.Count > 0 ? null : entry.WordChoice),
                        ("易错用法", entry.Mistakes.Count > 0 ? null : entry.CommonMistakes), ("语法", entry.GrammarBreakdown)));
                break;
            case EntryBlock.Tone:
                Custom(spec, entry.Tone, () => DictionaryInsightBlocks.Tone(entry.Tone));
                break;
            case EntryBlock.Culture:
                Custom(spec, entry.CulturalNote, () => DictionaryInsightBlocks.Note(entry.CulturalNote, "earth", RichBlocks.CalloutTone.Neutral));
                break;
            case EntryBlock.Thesaurus:
                // Words already compared in the nuance table are not repeated as bare chips.
                var compared = entry.SynonymNuances.Select(n => n.Word).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Section(spec, new[] { ("近义词", entry.Synonyms.Where(s => !compared.Contains(s)).ToArray()), ("反义词", entry.Antonyms.ToArray()) }
                    .Where(p => p.Item2.Length > 0)
                    .Select(p => new Row(p.Item1, string.Join(ChipSeparator, p.Item2), Link: true, Chips: true)));
                break;
            case EntryBlock.WordFamily:
                var related = entry.WordFamily.Where(f => !f.IsSameFormOf(entry.SourceText) ||
                    !entry.Senses.Any(s => LexicalPart.From(s.PartOfSpeech).Name == LexicalPart.From(f.PartOfSpeech).Name)).ToArray();
                Custom(spec, related, () => CreateRelatedTable(related));
                break;
            case EntryBlock.WordFormation:
                Custom(spec, new { entry.Morphemes, entry.EtymologyAndRoot },
                    () => DictionaryInsightBlocks.WordFormation(entry.Morphemes, entry.EtymologyAndRoot, entry.SourceText));
                break;
            case EntryBlock.Mnemonic:
                Custom(spec, entry.MemoryMnemonic, () => DictionaryInsightBlocks.Note(entry.MemoryMnemonic, "lightbulb-on-outline", RichBlocks.CalloutTone.Attention));
                break;
            case EntryBlock.Tags:
                Section(spec, entry.SuggestedTags.Count == 0 ? [] : [new Row("", string.Join(ChipSeparator, entry.SuggestedTags), Chips: true)]);
                break;
            case EntryBlock.Glossary:
                Section(spec, entry.GlossaryReferences.Select(t => new Row(t.Phrase, t.Meaning, Link: true)), Layout.Stacked);
                break;
        }
    }

    private SelectableTextBlock SourceText(string text) => _finished is { } entry
        ? TextStyles.Apply(new DictionaryReadingText(text, (q, context) => lookup(q, context, false),
            entry.Collocations.Select(c => c.Phrase).Concat(entry.GlossaryReferences.Select(c => c.Phrase)), entry.GrammarHighlights),
            TextRole.PassageTranslation)
        : TextStyles.Selectable(text, TextRole.PassageTranslation);

    private void Copy(string text) => _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);

    private static IEnumerable<(string Label, string Value)> SenseFields(PartOfSpeechSense sense) =>
        new[] { ("场景", sense.Scenario), ("用法", sense.GrammarPattern) }.Where(f => !string.IsNullOrWhiteSpace(f.Item2) &&
            !sense.UsageCases.Any(c => c.Condition == f.Item2 || c.Grammar == f.Item2 || c.Expression == f.Item2 || c.Meaning == f.Item2));

    private static IEnumerable<Row> Notes(params (string Label, string? Text)[] notes) =>
        notes.Where(n => !string.IsNullOrWhiteSpace(n.Text)).Select(n => new Row(n.Label, n.Text!));

    // Reserve each block's position (catalog order) even if its JSON field has not arrived yet.
    private SectionState Ensure(EntryBlockSpec spec)
    {
        if (!_sections.TryGetValue(spec.Block, out var section))
        {
            section = new();
            section.Section = new DictionarySection(spec.Title, section.Body, spec.Insight);
            section.Host.Content = section.Section;
            _sections.Add(spec.Block, section);
            Children.Add(section.Host);
        }
        section.Section!.Title = spec.Title;
        return section;
    }

    // A structured block is rebuilt only when its data (or interactivity) changes, so streaming keeps selection stable.
    private void Custom(EntryBlockSpec spec, object? data, Func<Control?> build)
    {
        var section = Ensure(spec);
        var signature = JsonSerializer.Serialize(data) + (_finished is null ? "" : "|done");
        if (section.Signature == signature) return;
        section.Signature = signature;
        var content = build();
        section.Body.Content = content;
        section.Host.IsVisible = content is not null;
    }

    // Unchanged rows retain their controls, text selection and keyboard focus.
    private void Section(EntryBlockSpec spec, IEnumerable<Row> values, Layout layout = Layout.Columns, string? unit = null)
    {
        var section = Ensure(spec);
        var rows = values.ToArray();
        var interactive = _finished is not null && rows.Any(r => r.Link || r.Example is not null);
        if (section.Items is not null && section.Rows.SequenceEqual(rows) && section.Interactive == interactive) return;
        section.Host.IsVisible = rows.Length > 0;
        if (section.Items is null)
        {
            section.Items = layout switch
            {
                Layout.Forms => new WrapPanel { ItemSpacing = AppTheme.Space.XXL, LineSpacing = AppTheme.Space.S },
                Layout.Senses or Layout.Collocations => new StackPanel(),
                _ => new StackPanel { Spacing = layout == Layout.Stacked ? AppTheme.Space.M + 2 : AppTheme.Space.L }
            };
            Grid.SetIsSharedSizeScope(section.Items, true);
            section.Body.Content = layout is Layout.Senses or Layout.Collocations
                ? RichBlocks.TableFrame(section.Items) : section.Items;
        }
        var counter = section.Section!.Caption;
        var count = layout == Layout.Senses ? rows.Count(r => r.Sense is not null) : rows.Length;
        counter.Text = unit is not null && count > 1 ? $"{count} {unit}" : null;
        counter.IsVisible = counter.Text is not null;
        var items = section.Items.Children;
        var hasLabels = rows.Any(r => !string.IsNullOrEmpty(r.Label));
        for (var i = 0; i < rows.Length; i++)
        {
            if (section.Interactive == interactive && section.HasLabels == hasLabels &&
                i < section.Rows.Length && section.Rows[i] == rows[i]) continue;
            var control = CreateRow(rows[i], layout, hasLabels);
            if (layout is Layout.Senses or Layout.Collocations)
            {
                var band = rows[i].Heading is not null;
                control = new Border
                {
                    Background = band ? AppTheme.InputBg : null,
                    BorderBrush = AppTheme.BorderSubtle, BorderThickness = new Thickness(0, i == 0 ? 0 : 1, 0, 0),
                    Padding = new Thickness(AppTheme.Space.L, band ? AppTheme.Space.S : AppTheme.Space.M),
                    Child = control
                };
            }
            if (i < items.Count) items[i] = control;
            else items.Add(control);
        }
        while (items.Count > rows.Length) items.RemoveAt(items.Count - 1);
        section.Rows = rows;
        section.HasLabels = hasLabels;
        section.Interactive = interactive;
    }

    private Control CreateRow(Row row, Layout layout, bool hasLabels)
    {
        if (layout == Layout.Collocations) return CreateCollocation(row);
        if (row.Contrast is { } contrast) return UsageContrastView.Create(contrast);
        if (row.Heading is { } heading) return CreateSenseHeading(heading);
        if (row.Sense is { } sense) return CreateSenseRow(row, sense);
        if (layout == Layout.Senses) return CreateUsageLine(row.Label, "", row.Note ?? "", row.Text);
        if (layout == Layout.Forms) return CreateForm(row.Label, row.Text);
        if (row.Chips) return hasLabels ? StudioUi.CreateKeyValue(row.Label, CreateChips(row), "RowLabel", labelTop: row.Link ? 5 : 2) : CreateChips(row);
        if (row.Number > 0 && row.Example is { } numbered && string.IsNullOrEmpty(row.Label))
        {
            var tile = new StackPanel();
            AddExample(tile, numbered, null);
            var ordinal = StudioUi.CreateOrdinal(row.Number);
            // Centre the 22px badge on the tile's first 22px text line (tile padding top = Space.S).
            ordinal.Margin = new Thickness(0, AppTheme.Space.S, AppTheme.Space.M, 0);
            var numberedRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { ordinal, tile } };
            Grid.SetColumn(tile, 1);
            return numberedRow;
        }
        var stacked = layout == Layout.Stacked;
        var body = new StackPanel { Spacing = AppTheme.Space.XXS };
        if (stacked)
        {
            // Expression and meaning share a line when they fit; long ones wrap naturally instead of always stacking.
            var head = new WrapPanel { ItemSpacing = AppTheme.Space.M, LineSpacing = 2 };
            Control term = _finished is not null && row.Link ? TermButton(row) : TextStyles.Selectable(row.Label, TextRole.Term);
            term.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(term);
            if (!string.IsNullOrWhiteSpace(row.Text))
            {
                // A bare part-of-speech label (词族: serendipitous 形容词) reads better as a tinted tag.
                Control meaning = HueMap.PartOfSpeech(row.Text) is var hue && !ReferenceEquals(hue, AppTheme.Hues.Neutral)
                    ? StudioUi.CreateTag(row.Text, TextRole.Label, hue)
                    : TextStyles.Selectable(row.Text, TextRole.Body);
                meaning.VerticalAlignment = VerticalAlignment.Center;
                head.Children.Add(meaning);
            }
            body.Children.Add(head);
        }
        else if (!string.IsNullOrWhiteSpace(row.Text)) body.Children.Add(RichText.Create(row.Text, TextRole.Body));
        if (!string.IsNullOrWhiteSpace(row.Note)) body.Children.Add(NoteBlock(row.Note));
        if (row.Example is { } example) AddExample(body, example, row.Link ? row.Label : null);
        return stacked || !hasLabels ? body : StudioUi.CreateKeyValue(row.Label, body, "RowLabel");
    }

    private static Border Pill(string text, TextRole role) => StudioUi.CreateTag(text, role);

    private WrapPanel CreateChips(Row row)
    {
        var chips = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.S };
        foreach (var word in row.Text.Split(ChipSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!row.Link || _finished is null)
            {
                chips.Children.Add(Pill(word, TextRole.Secondary));
                continue;
            }
            var chip = new Button
            {
                Classes = { "ghost" }, Background = AppTheme.CardBg, BorderBrush = AppTheme.BorderStrong, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(AppTheme.Radius.Pill), Padding = new Thickness(AppTheme.Space.M - 2, 3),
                Content = TextStyles.Create(word, TextRole.Body, wrap: false)
            };
            AutomationProperties.SetName(chip, $"查词 {word}");
            ToolTip.SetTip(chip, $"查询 “{word}”");
            chip.Click += (_, _) => lookup(word, "", false);
            chips.Children.Add(chip);
        }
        return chips;
    }

    // Notes mix plain explanation lines, an optional "用法　pattern" line and English glosses; give each its own role.
    private static Control NoteBlock(string note)
    {
        var lines = new StackPanel { Spacing = 2 };
        Grid.SetIsSharedSizeScope(lines, true);
        foreach (var line in note.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("用法　", StringComparison.Ordinal))
            {
                lines.Children.Add(StudioUi.CreateKeyValue("用法", RichText.Create(line["用法　".Length..], TextRole.Secondary), "NoteLabel"));
                continue;
            }
            lines.Children.Add(RichText.Create(line, IsLatin(line) ? TextRole.Gloss : TextRole.Secondary));
        }
        return lines;
    }

    private static bool IsLatin(string text)
    {
        var letters = text.Count(char.IsLetter);
        return letters > 0 && text.Count(c => c < 0x2E80 && char.IsLetter(c)) * 10 >= letters * 9;
    }

    private Button TermButton(Row row, string? exampleContext = null)
    {
        var button = new Button
        {
            Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(AppTheme.Space.XS, 1), Margin = new Thickness(-AppTheme.Space.XS, 0, 0, 0),
            CornerRadius = new CornerRadius(AppTheme.Radius.S), Background = Brushes.Transparent, BorderThickness = default,
            Content = TextStyles.Apply(new TextBlock
            {
                Text = row.Label, TextWrapping = TextWrapping.Wrap, TextDecorations = DictionaryReadingText.LinkUnderline()
            }, TextRole.Term)
        };
        AutomationProperties.SetName(button, $"查词 {row.Label}");
        ToolTip.SetTip(button, "在语境中查词");
        button.Click += (_, _) =>
        {
            var source = _finished!.SourceText;
            var at = DictionaryTextMatch.IndexOf(source, row.Label);
            var context = !string.IsNullOrWhiteSpace(exampleContext) ? exampleContext
                : at >= 0 ? DictionaryReadingText.SentenceAt(source, at, row.Label.Length) : row.Example?.SourceSentence ?? "";
            lookup(row.Label, context, false);
        };
        return button;
    }

    public static Button CreateCopyButton(Action copy)
    {
        var copyIcon = new MdiIcon("content-copy", 17, AppTheme.TextSecondary);
        var copyButton = StudioUi.CreateIconButton("content-copy", "复制译文", () => { });
        copyButton.Content = copyIcon;
        copyButton.Click += (_, _) =>
        {
            copy();
            copyIcon.IconName = "check";
            ToolTip.SetTip(copyButton, "已复制");
            DispatcherTimer.RunOnce(() =>
            {
                copyIcon.IconName = "content-copy";
                ToolTip.SetTip(copyButton, "复制译文");
            }, TimeSpan.FromMilliseconds(1400));
        };
        return copyButton;
    }
}
