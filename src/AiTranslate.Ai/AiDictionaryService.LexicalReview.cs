using System.Text.Json.Nodes;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    // Normalize only known lexical row shapes. The shared validator still checks the
    // complete row and the section after all the edits have been applied.
    private static bool NormalizeLexicalReviewEdit(string[] path, JsonNode edit, JsonObject wire)
    {
        if (edit["value"] is not { } value || path.Length < 2) return true;
        var field = path[0];
        if (field is not ("wordFamily" or "inflections" or "usageContrasts" or "collocations")) return true;
        var rows = wire[field] as JsonArray;
        var indexed = int.TryParse(path[1], out var index) && index >= 0;
        var old = indexed && rows is not null && index < rows.Count ? rows[index] as JsonObject : null;
        if (field == "wordFamily" && path.Length == 2 && value is JsonValue && edit["label"] is not null)
        {
            var familyRow = new JsonObject { ["value"] = value.DeepClone() };
            CopyReviewRowFields(edit, familyRow, ["label", "partOfSpeech", "relation", "usage", "expression", "example"]);
            edit["value"] = value = familyRow;
        }
        else if (field == "wordFamily" && path.Length == 2 && old is not null && value is JsonValue &&
                 (string?)edit["op"] is null or "replace" && JsonNode.DeepEquals(value, old["value"]))
        {
            // Some reviews echo just the spelling of the existing related word. This carries
            // no change; keep its usage, relation, expression and example intact.
            edit["value"] = old.DeepClone();
            return true;
        }
        if (field == "wordFamily" && path.Length == 2 && value is JsonObject form &&
            (path[1] == "-" || indexed && (index == rows?.Count || (string?)edit["op"] == "add")))
        {
            // Inflections, synonyms and collocations are already reviewed in their own sections.
            var relation = (string?)form["relation"];
            if (relation is "plural" or "inflection" or "synonym" or "antonym" or "collocation") return false;
            var term = (string?)form["value"];
            var headword = (string?)wire["sourceText"] ?? "";
            if (relation == "derived" && headword.Contains(' ') &&
                headword.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(term, StringComparer.OrdinalIgnoreCase)) return false;
            if (term is { Length: > 0 } && wire["inflections"] is JsonArray inflections && inflections.OfType<JsonObject>()
                .Any(i => string.Equals((string?)i["value"], term, StringComparison.OrdinalIgnoreCase))) return false;
            if (relation == "sameForm" && !string.Equals(term, headword, StringComparison.OrdinalIgnoreCase) &&
                wire["collocations"] is JsonArray pairs && pairs.OfType<JsonObject>().Any(p =>
                    string.Equals((string?)p["phrase"], term, StringComparison.OrdinalIgnoreCase))) return false;
            return true;
        }
        if (field == "collocations" && path is [_, _, "example"] && value is JsonObject example &&
            old?["example"] is JsonObject previous && old["phrase"]?.GetValue<string>() is { Length: > 0 } phrase &&
            (string?)previous["sourceSentence"] is { } current && (string?)example["sourceSentence"] is { } proposed)
        {
            // Keep a demonstrated expression rather than replacing its good paired example
            // with a sentence illustrating a different collocation of the same headword.
            return DictionaryTextMatch.IndexOf(current, phrase) < 0 || DictionaryTextMatch.IndexOf(proposed, phrase) >= 0;
        }
        if (field == "usageContrasts" && path.Length == 2 && value is JsonObject contrast)
            CopyReviewRowFields(edit, contrast, ["leftForm", "leftUse", "rightForm", "rightUse"]);
        if (field != "inflections") return true;
        if (path is [_, _, "partOfSpeech"] && IsInflectionLabel(value) && (string?)old?["label"] is "变体" or "形式")
        {
            path[2] = "label";
            return true;
        }
        if (path.Length != 2) return true;
        var inflection = value as JsonObject;
        if (inflection is null && value is JsonValue && edit["label"] is not null)
            edit["value"] = inflection = new JsonObject { ["value"] = value.DeepClone() };
        if (inflection is null) return true;
        CopyReviewRowFields(edit, inflection, ["label", "partOfSpeech"]);
        if ((string?)inflection["label"] is "变体" or "形式" && IsInflectionLabel(inflection["partOfSpeech"]))
        {
            inflection["label"] = inflection["partOfSpeech"]!.DeepClone();
            inflection.Remove("partOfSpeech");
        }
        return true;
    }

    private static bool IsInflectionLabel(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) &&
        s is "过去式" or "过去分词" or "现在分词" or "第三人称单数" or "复数" or "比较级" or "最高级";

    private static void CopyReviewRowFields(JsonNode edit, JsonObject row, string[] fields)
    {
        foreach (var key in fields)
        {
            if (edit[key] is not { } sibling) continue;
            if (row[key] is { } existing && !JsonNode.DeepEquals(existing, sibling))
                throw InvalidEdits("同一行给出了互相冲突的字段。");
            row[key] = sibling.DeepClone();
        }
    }
}
