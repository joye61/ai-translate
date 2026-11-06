using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Ai;

/// <summary>
/// Lenient readers for the grammar wire format. A preview keeps rows that are still arriving; a final read keeps only
/// rows a student can use, so a half-written example is never saved to the library.
/// </summary>
internal static class GrammarJson
{
    public static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? "" : "";

    public static IEnumerable<JsonElement> Objects(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.Object) yield return item;
    }

    public static IReadOnlyList<string> Strings(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<string>();
        foreach (var item in list.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString()?.Trim() is { Length: > 0 } text && !result.Contains(text))
                result.Add(text);
        return result;
    }

    public static BilingualExample? Example(JsonElement element, string name, bool preview)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.Object)
            return null;
        var source = Str(item, "sourceSentence");
        var translation = Str(item, "translatedSentence");
        if (source.Length == 0 || !preview && translation.Length == 0) return null;
        var highlight = Str(item, "highlightTerm");
        if (highlight.Length > 0 && DictionaryTextMatch.IndexOf(source, highlight, comparison: StringComparison.Ordinal) < 0) highlight = "";
        return new(source, translation, highlight);
    }

    private static IReadOnlyList<BilingualExample> Examples(JsonElement element, string name, bool preview)
    {
        var result = new List<BilingualExample>();
        foreach (var item in Objects(element, name))
        {
            var source = Str(item, "sourceSentence");
            var translation = Str(item, "translatedSentence");
            if (source.Length == 0 || !preview && translation.Length == 0) continue;
            var highlight = Str(item, "highlightTerm");
            if (highlight.Length > 0 && DictionaryTextMatch.IndexOf(source, highlight, comparison: StringComparison.Ordinal) < 0) highlight = "";
            var example = new BilingualExample(source, translation, highlight);
            if (!result.Any(e => e.SourceSentence == source)) result.Add(example);
        }
        return result;
    }

    /// <summary>Core blocks: identity, summary, concept, formulas, usages, signals and form rules.</summary>
    public static GrammarTopic ReadCore(JsonElement root, GrammarTopic seed, bool preview)
    {
        var formulas = new List<GrammarFormula>();
        foreach (var item in Objects(root, "formulas"))
        {
            var label = Str(item, "label");
            var pattern = Str(item, "pattern");
            if (pattern.Length == 0 || !preview && label.Length == 0) continue;
            var formula = new GrammarFormula(label, pattern, Example(item, "example", preview), Str(item, "note"));
            if (!formulas.Any(f => f.Label == label && f.Pattern == pattern)) formulas.Add(formula);
        }
        var usages = new List<GrammarUsage>();
        foreach (var item in Objects(root, "usages"))
        {
            var title = Str(item, "title");
            var explanation = Str(item, "explanation");
            if (title.Length == 0 || !preview && explanation.Length == 0) continue;
            var usage = new GrammarUsage(title, explanation)
            {
                Examples = Examples(item, "examples", preview), Signals = Strings(item, "signals"), Tip = Str(item, "tip")
            };
            if (!usages.Any(u => u.Title == title)) usages.Add(usage);
        }
        var signals = new List<GrammarSignal>();
        foreach (var item in Objects(root, "signals"))
        {
            var expression = Str(item, "expression");
            if (expression.Length > 0 && !signals.Any(s => s.Expression == expression))
                signals.Add(new(expression, Str(item, "meaning")));
        }
        var rules = new List<GrammarRule>();
        foreach (var item in Objects(root, "rules"))
        {
            var title = Str(item, "title");
            var explanation = Str(item, "explanation");
            if (title.Length == 0 || !preview && explanation.Length == 0) continue;
            var cases = new List<GrammarCase>();
            foreach (var row in Objects(item, "cases"))
            {
                var form = Str(row, "form");
                var result = Str(row, "result");
                if (form.Length > 0 && (preview || result.Length > 0)) cases.Add(new(form, result, Str(row, "note")));
            }
            if (!rules.Any(r => r.Title == title)) rules.Add(new GrammarRule(title, explanation) { Cases = cases });
        }
        return seed with
        {
            SourceTerm = Str(root, "sourceTerm") is { Length: > 0 } term ? term : seed.SourceTerm,
            Category = Str(root, "category") is { Length: > 0 } category ? category : seed.Category,
            Level = Str(root, "level"),
            Summary = Str(root, "summary"),
            Concept = Str(root, "concept"),
            Aliases = Strings(root, "aliases"),
            Formulas = formulas, Usages = usages, Signals = signals, Rules = rules
        };
    }

    /// <summary>Extended blocks: contrasts, exceptions, mistakes, exam points, mnemonic, exercises and related points.</summary>
    public static GrammarTopic ReadExtension(JsonElement root, GrammarTopic topic, bool preview)
    {
        var contrasts = new List<GrammarContrast>();
        foreach (var item in Objects(root, "contrasts"))
        {
            var versus = Str(item, "versus");
            var difference = Str(item, "difference");
            if (versus.Length == 0 || !preview && difference.Length == 0) continue;
            if (contrasts.Any(c => c.Versus == versus)) continue;
            contrasts.Add(new GrammarContrast(versus, difference)
            {
                LeftLabel = Str(item, "leftLabel"), LeftUse = Str(item, "leftUse"), LeftExample = Example(item, "leftExample", preview),
                RightLabel = Str(item, "rightLabel"), RightUse = Str(item, "rightUse"), RightExample = Example(item, "rightExample", preview)
            });
        }
        var exceptions = new List<GrammarException>();
        foreach (var item in Objects(root, "exceptions"))
        {
            var title = Str(item, "title");
            var explanation = Str(item, "explanation");
            if (title.Length > 0 && (preview || explanation.Length > 0) && !exceptions.Any(e => e.Title == title))
                exceptions.Add(new GrammarException(title, explanation) { Example = Example(item, "example", preview) });
        }
        var mistakes = new List<UsageMistake>();
        foreach (var item in Objects(root, "mistakes"))
        {
            var wrong = Str(item, "wrong");
            var right = Str(item, "right");
            if (wrong.Length > 0 && (preview || right.Length > 0) && !mistakes.Any(m => m.Wrong == wrong))
                mistakes.Add(new(wrong, right, Str(item, "why")));
        }
        var exercises = new List<GrammarExercise>();
        foreach (var item in Objects(root, "exercises"))
        {
            var question = Str(item, "question");
            var answer = Str(item, "answer");
            if (question.Length == 0 || !preview && answer.Length == 0) continue;
            var choices = Strings(item, "choices");
            // A multiple-choice answer must be one of its own choices.
            if (!preview && choices.Count > 0 && !choices.Contains(answer)) continue;
            if (!exercises.Any(e => e.Question == question))
                exercises.Add(new GrammarExercise(question, answer, Str(item, "explanation")) { Choices = choices });
        }
        var related = new List<GrammarRelation>();
        foreach (var item in Objects(root, "related"))
        {
            var title = Str(item, "title");
            if (title.Length > 0 && GrammarText.Normalize(title) != GrammarText.Normalize(topic.Title) &&
                !related.Any(r => GrammarText.Normalize(r.Title) == GrammarText.Normalize(title)))
                related.Add(new(title, Str(item, "relation")));
        }
        return topic with
        {
            Contrasts = contrasts, Exceptions = exceptions, Mistakes = mistakes, ExamPoints = Strings(root, "examPoints"),
            Mnemonic = Str(root, "mnemonic"), Exercises = exercises, Related = related
        };
    }

    public static IReadOnlyList<GrammarSentencePoint> ReadPoints(string source, JsonElement container, string name, int limit = 8)
    {
        var points = new List<GrammarSentencePoint>();
        foreach (var item in Objects(container, name))
        {
            var topic = Str(item, "topic");
            var quote = Str(item, "quote");
            var label = Str(item, "label");
            var explanation = Str(item, "explanation");
            if (topic.Length == 0 || quote.Length == 0 || explanation.Length == 0) continue;
            if (DictionaryTextMatch.IndexOf(source, quote, comparison: StringComparison.Ordinal) < 0) continue;
            if (points.Any(p => p.Quote == quote && GrammarText.Normalize(p.Topic) == GrammarText.Normalize(topic))) continue;
            points.Add(new(topic, quote, label.Length > 0 ? label : topic, explanation, Str(item, "category")));
            if (points.Count == limit) break;
        }
        return points;
    }
}
