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
        var table = new StackPanel { Spacing = AppTheme.Space.XS, Margin = new Thickness(0, AppTheme.Space.XS, 0, 0) };
        Grid.SetIsSharedSizeScope(table, true);
        var lines = sense.UsageCases.Select(usage =>
        {
            var grammar = usage.Grammar == sense.GrammarLabel || usage.Grammar == usage.Condition ? "" : usage.Grammar;
            var meaning = usage.Meaning == sense.DefinitionZh || usage.Meaning == usage.Condition ? "" : usage.Meaning;
            return usage with { Grammar = grammar, Meaning = meaning };
        }).ToArray();
        // Short labels share just enough width to align the expressions. Long explanations
        // flow alongside the expression rather than consuming an entire column on narrow windows.
        var alignGrammar = lines.Length > 1 && lines.Any(c => c.Grammar.Length > 0) && lines.All(c => c.Grammar.Length <= 10);
        foreach (var line in lines)
            table.Children.Add(CreateUsageLine(line.Condition, line.Grammar, line.Expression, line.Meaning, alignGrammar));
        return table;
    }

    private static Control CreateUsageLine(string condition, string grammar, string expression, string meaning, bool alignGrammar = false)
    {
        var content = new WrapPanel { ItemSpacing = AppTheme.Space.S, LineSpacing = AppTheme.Space.XXS };
        if (!alignGrammar && !string.IsNullOrWhiteSpace(grammar))
            content.Children.Add(TextStyles.Create(grammar, TextRole.FieldLabel));
        if (!string.IsNullOrWhiteSpace(expression))
            content.Children.Add(TextStyles.Selectable(expression, TextRole.Term));
        if (!string.IsNullOrWhiteSpace(meaning))
            content.Children.Add(TextStyles.Selectable(meaning, TextRole.Secondary));
        foreach (var element in content.Children) element.VerticalAlignment = VerticalAlignment.Center;
        if (alignGrammar)
        {
            var label = TextStyles.Create(grammar, TextRole.FieldLabel);
            // With a wrapped expression the grammatical label belongs on the first line.
            label.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(content, 1);
            var pair = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = "UsageCaseGrammar" },
                    new ColumnDefinition(GridLength.Star)
                },
                ColumnSpacing = AppTheme.Space.S, Children = { label, content }
            };
            return StudioUi.CreateKeyValue(condition, pair, "UsageCaseCondition", labelTop: 0);
        }
        return StudioUi.CreateKeyValue(condition, content, "UsageCaseCondition", labelTop: 0);
    }
}
