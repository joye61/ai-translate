using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    private const int MaxUsageCases = 3;
    private const int MaxRelatedForms = 4;

    private static IReadOnlyList<SenseUsageCase> ReadUsageCases(JsonElement sense)
    {
        if (!sense.TryGetProperty("usageCases", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var cases = new List<SenseUsageCase>();
        foreach (var row in items.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var condition = GetString(row, "condition").Trim();
            var expression = GetString(row, "expression").Trim();
            var grammar = GetLabels(row, "grammar").Trim();
            var meaning = GetString(row, "meaning").Trim();
            if (condition.Length == 0 || expression.Length == 0 || (grammar.Length == 0 && meaning.Length == 0)) continue;
            var item = new SenseUsageCase(condition, grammar, expression, meaning);
            if (!cases.Any(c => SameCase(c, item))) cases.Add(item);
            if (cases.Count == MaxUsageCases) break;
        }
        return cases.ToArray();
    }

    private static bool SameCase(SenseUsageCase a, SenseUsageCase b) =>
        SameText(a.Condition, b.Condition) && SameText(a.Expression, b.Expression);

    private static IReadOnlyList<RelatedForm> ReadWordFamily(JsonElement root, string headword, bool englishWord,
        IReadOnlyList<PartOfSpeechSense> senses, IReadOnlyList<RelatedForm>? previous = null)
    {
        var existing = previous ?? [];
        if (!root.TryGetProperty("wordFamily", out var items) || items.ValueKind != JsonValueKind.Array) return existing;
        var forms = existing.ToList();
        foreach (var row in items.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            var value = GetString(row, "value").Trim();
            var label = GetString(row, "label").Trim();
            var part = GetString(row, "partOfSpeech").Trim();
            var relation = GetString(row, "relation").Trim();
            var usage = GetString(row, "usage").Trim();
            var expression = GetString(row, "expression").Trim();
            if (value.Length == 0 || (englishWord && value.Any(char.IsWhiteSpace))) continue;
            var form = new RelatedForm(label, value, part, relation, usage, expression);
            var self = string.Equals(value, headword.Trim(), StringComparison.OrdinalIgnoreCase);
            if (relation.Length == 0)
            {
                // Legacy label/value rows are still readable without an inferred semantic relationship.
                if (self || label.Length == 0 || usage.Length > 0 || expression.Length > 0) continue;
            }
            else
            {
                if (relation is not ("derived" or "counterpart" or "sameForm" or "confusable") ||
                    part.Length == 0 || label.Length == 0 || usage.Length == 0 || DictionaryTextMatch.IndexOf(expression, value) < 0) continue;
                if (self != (relation == "sameForm")) continue;
                if (self)
                {
                    var target = LexicalPart.From(part);
                    if (target.Abbreviation.Length == 0 || !senses.Any(s => LexicalPart.From(s.PartOfSpeech) is { Abbreviation.Length: > 0 } source &&
                        source.Name != target.Name)) continue;
                }
            }
            var at = forms.FindIndex(f => SameRelatedForm(f, form));
            if (at < 0 && forms.Count < MaxRelatedForms) forms.Add(form);
            else if (at >= 0 && string.IsNullOrEmpty(forms[at].Relation) && relation.Length > 0) forms[at] = form;
        }
        return forms.SequenceEqual(existing) ? existing : forms.ToArray();
    }

    private static bool SameRelatedForm(RelatedForm a, RelatedForm b)
    {
        if (!SameText(a.Value, b.Value)) return false;
        if (!string.IsNullOrEmpty(a.Relation) && !string.IsNullOrEmpty(b.Relation))
            return a.Relation == b.Relation && LexicalPart.From(a.PartOfSpeech).Name == LexicalPart.From(b.PartOfSpeech).Name &&
                SameText(a.Usage, b.Usage);
        if (SameText(a.Label, b.Label)) return true;
        var first = LexicalPart.From(string.IsNullOrWhiteSpace(a.PartOfSpeech) ? a.Label : a.PartOfSpeech);
        var second = LexicalPart.From(string.IsNullOrWhiteSpace(b.PartOfSpeech) ? b.Label : b.PartOfSpeech);
        return first.Abbreviation.Length > 0 && first.Name == second.Name;
    }

    private static int FindGrammarSense(JsonElement row, IReadOnlyList<PartOfSpeechSense> original)
    {
        var pos = GetString(row, "partOfSpeech").Trim();
        var definition = GetString(row, "definitionZh").Trim();
        bool Matches(PartOfSpeechSense s) => (pos.Length == 0 || LexicalPart.From(s.PartOfSpeech) == LexicalPart.From(pos)) &&
            (definition.Length == 0 || s.DefinitionZh.Trim() == definition);
        if (row.TryGetProperty("id", out _))
        {
            var id = GetString(row, "id");
            if (!id.StartsWith("sense-", StringComparison.Ordinal) || !int.TryParse(id.AsSpan(6), out var i) ||
                i < 0 || i >= original.Count || id != $"sense-{i}" || !Matches(original[i])) return -1;
            return i;
        }
        if (pos.Length == 0 || definition.Length == 0) return -1;
        var matches = Enumerable.Range(0, original.Count).Where(i => Matches(original[i])).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : -1;
    }
}
