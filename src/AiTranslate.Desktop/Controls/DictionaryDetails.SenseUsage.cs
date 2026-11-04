using AiTranslate.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace AiTranslate.Desktop.Controls;

internal sealed partial class DictionaryDetails
{
    private static bool IsPlural(string label) => label is "复数" or "复数形式" or "名词复数" or "plural";

    // Old cards have no sense anchor. One unqualified plural can be placed only when
    // exactly one sense is explicitly countable and every other sense is explicitly mass.
    private static (WordInflection? Form, int Owner) FindSensePlural(WordInflection[] forms, PartOfSpeechSense[] senses)
    {
        if (senses.Length < 2) return (null, -1);
        var plurals = forms.Where(f => IsPlural(BaseFormLabel(f.Label))).ToArray();
        if (plurals.Length != 1 || !IsPlural(plurals[0].Label.Trim().ToLowerInvariant())) return (null, -1);
        var owner = -1;
        for (var i = 0; i < senses.Length; i++)
        {
            var counted = Countability(senses[i].GrammarLabel);
            if (counted is null || senses[i].UsageCases.Any(c => Countability(c.Grammar) != counted &&
                !(counted == true && IsPlural(c.Grammar.Trim().ToLowerInvariant())))) return (null, -1);
            if (counted == false) continue;
            if (owner >= 0) return (null, -1);
            owner = i;
        }
        return owner < 0 ? (null, -1) : (plurals[0], owner);

    }

    private static bool? Countability(string label) => label.Trim().ToLowerInvariant() switch
    {
        "可数" or "可数名词" or "countable" or "c" or "[c]" => true,
        "不可数" or "不可数名词" or "uncountable" or "u" or "[u]" => false,
        _ => null
    };

    private static string BaseFormLabel(string label) => label.Split(['(', '（'], 2)[0].Trim().ToLowerInvariant();

    // A POS identifies a paradigm, but does not prove that each spelling applies to every sense.
    // Keep older, unassigned alternatives available without labelling them as the group's default.
    private static IReadOnlyList<WordInflection> SharedForms(IEnumerable<WordInflection> forms,
        PartOfSpeechSense[] senses, List<WordInflection> unassigned)
    {
        var common = new List<WordInflection>();
        var cases = senses.SelectMany(s => s.UsageCases).ToArray();
        foreach (var slot in forms.GroupBy(f => BaseFormLabel(f.Label)))
        {
            var plural = IsPlural(slot.Key);
            var degree = slot.Key is "比较级" or "最高级" or "comparative" or "superlative";
            bool ExplainsForm(SenseUsageCase usage) => usage.Grammar.Contains(slot.Key, StringComparison.OrdinalIgnoreCase) ||
                (plural && usage.Grammar.Contains("可数", StringComparison.Ordinal));

            var scoped = cases.Where(ExplainsForm).ToArray();
            var grammarLabels = senses.Select(s => s.GrammarLabel).Concat(cases.Select(c => c.Grammar));
            var varies = scoped.Length > 0 ||
                (senses.Length > 1 && slot.Select(f => f.Value).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()) ||
                (plural && ((senses.Length > 1 && senses.Any(s => Countability(s.GrammarLabel) != true)) ||
                    grammarLabels.Any(g => Countability(g) == false || g.Contains("不可数", StringComparison.Ordinal)))) ||
                (degree && grammarLabels.Any(g => g.Contains("不分级", StringComparison.Ordinal) ||
                    g.Contains("不可分级", StringComparison.Ordinal) || g.Contains("无比较级", StringComparison.Ordinal)));
            foreach (var form in slot)
            {
                if (scoped.Any(c => DictionaryTextMatch.IndexOf(c.Expression, form.Value) >= 0)) continue;
                if (varies) unassigned.Add(form);
                else common.Add(form);
            }
        }
        return common;
    }

    private static Control CreateUsageCases(PartOfSpeechSense sense)
    {
        var table = new ReadingGrid("Auto,Auto,*,*")
        {
            Margin = new Thickness(0, AppTheme.Space.XS, 0, 0), RowSpacing = AppTheme.Space.XS
        };
        foreach (var usage in sense.UsageCases)
        {
            var grammar = usage.Grammar == sense.GrammarLabel || usage.Grammar == usage.Condition ? "" : usage.Grammar;
            var meaning = usage.Meaning == sense.DefinitionZh || usage.Meaning == usage.Condition ? "" : usage.Meaning;
            AddUsageLine(table, usage.Condition, grammar, usage.Expression, meaning);
        }
        return table;
    }

    private static Control CreateUsageLine(string condition, string expression, string meaning)
    {
        var table = new ReadingGrid("Auto,Auto,*,*");
        AddUsageLine(table, condition, "", expression, meaning);
        return table;
    }

    private static void AddUsageLine(ReadingGrid table, string condition, string grammar, string expression, string meaning)
    {
        var row = table.RowDefinitions.Count;
        table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        if (condition.Length > 0) Add(TextStyles.Create(condition, TextRole.Label), 0);
        if (grammar.Length > 0) Add(TextStyles.Create(grammar, TextRole.FieldLabel), 1);
        if (expression.Length > 0) Add(TextStyles.Selectable(expression, TextRole.Term), 2);
        if (meaning.Length > 0) Add(TextStyles.Selectable(meaning, TextRole.ExampleTarget), 3);

        void Add(Control text, int column)
        {
            text.VerticalAlignment = VerticalAlignment.Top;
            if (column < 3) text.Margin = new Thickness(0, 0, AppTheme.Space.S, 0);
            table.AddCell(text, row, column);
        }
    }
}
