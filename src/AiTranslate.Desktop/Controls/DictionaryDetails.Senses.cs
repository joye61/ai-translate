using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Controls;

internal sealed partial class DictionaryDetails
{
    private sealed record SenseHeading(string Name, string Abbreviation, string Grammar, bool HasSameForm, IReadOnlyList<WordInflection> Forms)
    {
        public bool Equals(SenseHeading? other) => other is not null && Name == other.Name &&
            Abbreviation == other.Abbreviation && Grammar == other.Grammar && HasSameForm == other.HasSameForm && Forms.SequenceEqual(other.Forms);
        public override int GetHashCode() => HashCode.Combine(Name, Abbreviation, Grammar, HasSameForm, Forms.Count);
    }

    private static Row[] SenseRows(DictionaryEntryResult entry, bool complete, out IEnumerable<WordInflection> remainingForms)
    {
        var groups = entry.Senses.GroupBy(s => LexicalPart.From(s.PartOfSpeech).Name).ToArray();
        var parts = groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var forms = entry.Inflections.ToLookup(f => entry.ItemType.IsWordOrTerm() ? FormOwner(f, parts) : "");
        var unassigned = forms[""].ToList();
        remainingForms = unassigned;
        var rows = new List<Row>();
        foreach (var group in groups)
        {
            var senses = group.Select(s =>
            {
                var display = DisplayGrammar(s);
                if (display.UsageCases.Any(c => c.Grammar == display.GrammarLabel) &&
                    display.UsageCases.Any(c => c.Grammar.Length > 0 && c.Grammar != display.GrammarLabel))
                    display = display with { GrammarLabel = "" };
                return (Sense: display, Example: complete ? entry.GetSenseExample(s) : s.Example);
            }).ToArray();
            var grammar = senses[0].Sense.GrammarLabel;
            var named = group.Key is not ("" or "释义");
            var shared = named && senses.All(s => s.Sense.GrammarLabel == grammar && s.Sense.UsageCases.Count == 0) ? grammar : "";
            var sameForms = entry.WordFamily.Where(f => f.IsSameFormOf(entry.SourceText) &&
                LexicalPart.From(f.PartOfSpeech).Name == group.Key).ToArray();
            var groupForms = forms[group.Key].Distinct().ToArray();
            var definitions = senses.Select(s => s.Sense).ToArray();
            var (plural, owner) = group.Key == "名词" ? FindSensePlural(groupForms, definitions) : (null, -1);
            if (named)
            {
                var heading = new SenseHeading(group.Key, LexicalPart.From(group.First().PartOfSpeech).Abbreviation,
                    shared, sameForms.Length > 0, SharedForms(groupForms.Where(f => f != plural), definitions, unassigned));
                rows.Add(new("", "", Heading: heading));
            }
            for (var i = 0; i < senses.Length; i++)
            {
                var (sense, example) = senses[i];
                if (shared.Length > 0) sense = sense with { GrammarLabel = "" };
                var form = i == owner && plural is not null && !sense.UsageCases.Any(c => DictionaryTextMatch.IndexOf(c.Expression, plural.Value) >= 0)
                    ? plural : null;
                rows.Add(new("", sense.DefinitionZh, Example: example, Sense: sense, Number: senses.Length > 1 ? i + 1 : 0, SenseForm: form));
            }
            foreach (var form in sameForms)
            {
                var use = senses.Any(s => s.Sense.DefinitionZh == form.Usage || s.Sense.Scenario == form.Usage ||
                    s.Sense.GrammarPattern == form.Usage || s.Sense.UsageCases.Any(c => c.Condition == form.Usage || c.Meaning == form.Usage))
                    ? "" : form.Usage;
                var expression = senses.Any(s => DictionaryTextMatch.IndexOf(s.Example?.SourceSentence ?? "", form.Expression) >= 0 ||
                    s.Sense.UsageCases.Any(c => string.Equals(c.Expression, form.Expression, StringComparison.OrdinalIgnoreCase)))
                    ? "" : form.Expression;
                if (!string.IsNullOrWhiteSpace(form.Label) && form.Label != use &&
                    !senses.Any(s => DictionaryTextMatch.IndexOf(s.Sense.DefinitionZh, form.Label) >= 0))
                    use = string.IsNullOrWhiteSpace(use) ? form.Label : $"{form.Label} · {use}";
                var example = senses.Any(s => s.Example?.SourceSentence == form.Example?.SourceSentence &&
                    s.Example?.TranslatedSentence == form.Example?.TranslatedSentence) ? null : form.Example;
                if (!string.IsNullOrWhiteSpace(use) || !string.IsNullOrWhiteSpace(expression) || example is not null)
                    rows.Add(new("同形用法", use, Note: expression, Example: example));
            }
        }
        return rows.ToArray();
    }

    // Older entries put attributes at the start of the usage note. Only lift a complete,
    // unambiguous clause; retain its qualifications and the rest of the author's text.
    private static PartOfSpeechSense DisplayGrammar(PartOfSpeechSense sense)
    {
        var usage = sense.GrammarPattern?.Trim() ?? "";
        var separator = usage.IndexOfAny(['，', '；', '。', ',', ';']);
        var first = (separator < 0 ? usage : usage[..separator]).Trim();
        var label = first switch
        {
            "可数名词" or "可数" => "可数",
            "不可数名词" or "不可数" => "不可数",
            "通常不可数" or "通常不可数名词" => "通常不可数",
            "通常可数" or "通常可数名词" => "通常可数",
            "及物动词" or "及物" => "及物",
            "不及物动词" or "不及物" => "不及物",
            _ => ""
        };
        if (label.Length > 0 && (string.IsNullOrWhiteSpace(sense.GrammarLabel) || sense.GrammarLabel == label))
            return sense with { GrammarLabel = label, GrammarPattern = separator < 0 ? "" : usage[(separator + 1)..].Trim() };
        return string.IsNullOrWhiteSpace(sense.GrammarLabel)
            ? sense with { GrammarLabel = LexicalPart.From(sense.PartOfSpeech).Grammar } : sense;
    }

    private static string FormOwner(WordInflection form, HashSet<string> parts)
    {
        var owner = LexicalPart.From(form.PartOfSpeech).Name;
        if (owner.Length == 0)
        {
            // Older inflections have only a label and value. Ambiguous forms and related
            // words stay in the separate forms block rather than acquiring a guessed POS.
            var label = form.Label.Split(['(', '（'], 2)[0].Trim().ToLowerInvariant();
            owner = label switch
            {
                "单数" or "复数" or "名词复数" or "复数形式" or "plural" => "名词",
                "三单" or "第三人称单数" or "过去式" or "过去分词" or "现在分词" or "动名词" or "past tense" or "past participle" or "present participle"
                    when parts.Contains("动词") != parts.Contains("助动词") => parts.Contains("动词") ? "动词" : "助动词",
                "主格" or "宾格" or "反身形式" or "反身代词" => "代词",
                "比较级" or "最高级" or "comparative" or "superlative" when parts.Contains("形容词") != parts.Contains("副词")
                    => parts.Contains("形容词") ? "形容词" : "副词",
                _ => ""
            };
        }
        return parts.Contains(owner) && owner != "释义" ? owner : "";
    }

    private static Control CreateSenseHeading(SenseHeading heading)
    {
        var title = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XS };
        title.Children.Add(TextStyles.Create(heading.Name, TextRole.SectionTitle));
        if (heading.Abbreviation.Length > 0)
            title.Children.Add(StudioUi.CreateTag(heading.Abbreviation, TextRole.PartOfSpeech, HueMap.PartOfSpeech(heading.Abbreviation)));
        if (heading.HasSameForm) title.Children.Add(TextStyles.Create("与词头同形", TextRole.Meta));
        if (heading.Grammar.Length > 0)
        {
            var grammar = TextStyles.Create(heading.Grammar, TextRole.Secondary);
            grammar.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(grammar);
        }
        foreach (var form in heading.Forms)
        {
            var field = CreateForm(form.Label, form.Value);
            field.Margin = new Thickness(AppTheme.Space.S, 0, 0, 0);
            title.Children.Add(field);
        }
        foreach (var item in title.Children) item.VerticalAlignment = VerticalAlignment.Center;
        return title;
    }

    private static Control CreateForm(string label, string value)
    {
        var term = TextStyles.Selectable(value, TextRole.Term);
        var pair = StudioUi.CreateKeyValue(label, term, labelTop: 0);
        Grid.SetIsSharedSizeScope(pair, true);
        return pair;
    }

    private Control CreateSenseRow(Row row, PartOfSpeechSense sense)
    {
        var body = new StackPanel { Spacing = AppTheme.Space.S };
        var head = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XXS };
        var definition = TextStyles.Selectable(sense.DefinitionZh, TextRole.Definition);
        definition.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(definition);
        if (!string.IsNullOrWhiteSpace(sense.GrammarLabel))
        {
            var grammar = TextStyles.Create(sense.GrammarLabel, TextRole.FieldLabel);
            grammar.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(grammar);
        }
        if (!string.IsNullOrWhiteSpace(sense.Register)) head.Children.Add(Pill(sense.Register, TextRole.Meta));
        if (row.SenseForm is { } form) head.Children.Add(CreateForm(form.Label, form.Value));
        if (!string.IsNullOrWhiteSpace(sense.DefinitionEn) && !string.Equals(sense.DefinitionEn, sense.DefinitionZh, StringComparison.OrdinalIgnoreCase))
            body.Children.Add(TextStyles.Selectable(sense.DefinitionEn, TextRole.Gloss));
        if (sense.UsageCases.Count > 0) body.Children.Add(CreateUsageCases(sense));
        var fields = SenseFields(sense).ToArray();
        if (fields.Length > 0)
        {
            var table = new StackPanel { Spacing = AppTheme.Space.XXS };
            Grid.SetIsSharedSizeScope(table, true);
            foreach (var (label, value) in fields)
                table.Children.Add(StudioUi.CreateKeyValue(label, RichText.Create(value, TextRole.Secondary), "SenseField"));
            body.Children.Add(table);
        }
        if (row.Example is { } example) AddExample(body, example);

        var ordinal = Math.Max(row.Number, 1);
        var toggle = body.Children.Count == 0 ? null : _disclosures.Toggle(EntryBlock.Senses,
            LexicalPart.From(sense.PartOfSpeech).Name, ordinal, sense.DefinitionZh, "详解", body, ordinal == 1);
        return ReadingRow(row.Number, head, toggle, body);
    }
}
