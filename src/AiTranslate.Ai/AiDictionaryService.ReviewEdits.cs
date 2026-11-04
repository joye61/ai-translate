using System.Text.Json.Nodes;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    private static JsonArray ReviewEdits(JsonObject reply, JsonObject current)
    {
        if (reply["fields"] is null && reply["edits"] is JsonArray { Count: > 0 } edits) return edits;
        if (reply["edits"] is not null || reply["fields"] is not JsonObject { Count: > 0 } values ||
            current.Any(p => p.Value is JsonArray or JsonObject)) throw InvalidEdits();
        var result = new JsonArray();
        foreach (var (name, value) in values)
        {
            if (name.Length == 0 || name.Contains('/') || value is not JsonValue scalar || !scalar.TryGetValue<string>(out _))
                throw InvalidEdits();
            result.Add(new JsonObject { ["path"] = "/" + name, ["value"] = value.DeepClone() });
        }
        return result;
    }

    private static bool RepeatsTranslation(DictionaryEntryResult entry, JsonNode variant)
    {
        if (variant is not JsonObject row || row["style"] is not JsonValue style ||
            !style.TryGetValue<string>(out var label) || string.IsNullOrWhiteSpace(label) ||
            row["text"] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
            return false;
        return SameRendering(text, entry.PrimaryTranslation) || SameRendering(text, entry.LiteralTranslation);
    }

    private static void NormalizeThesaurusEdit(string[] path, JsonNode edit)
    {
        // This block displays lookup chips. Providers sometimes annotate a chip with a nuance
        // or example. Keep the headword here; a review of this block cannot edit the nuance table.
        if (path.Length == 1 && edit["value"] is JsonArray words)
        {
            for (var i = 0; i < words.Count; ++i)
                if (words[i] is JsonObject row && row["word"] is JsonValue word) words[i] = word.DeepClone();
        }
        else if (path.Length == 2 && edit["value"] is JsonObject row && row["word"] is JsonValue word)
            edit["value"] = word.DeepClone();
    }

    private static string[] EditPath(JsonNode? node)
    {
        var parts = node?.GetValue<string>()?.Split('/');
        if (parts is not ["", _, ..] || parts.Skip(1).Any(string.IsNullOrEmpty)) throw InvalidEdits();
        return parts is ["", "current_section", _, ..] ? parts[2..] : parts[1..];
    }

    private static void ApplyBlockEdit(JsonObject wire, string[] path, JsonNode edit)
    {
        JsonNode parent = wire;
        foreach (var part in path.SkipLast(1))
        {
            parent = parent switch
            {
                JsonObject obj => obj[part],
                JsonArray array when int.TryParse(part, out var index) && index >= 0 && index < array.Count => array[index],
                _ => null
            } ?? throw InvalidEdits();
        }
        var key = path[^1];
        var op = edit["op"]?.GetValue<string>();
        if (op == "move")
        {
            var from = EditPath(edit["from"]);
            if (parent is not JsonArray rowsToMove ||
                !int.TryParse(from[^1], out var source) || source < 0 || source >= rowsToMove.Count) throw InvalidEdits();
            var target = rowsToMove.Count - 1;
            if (key != "-" && (!int.TryParse(key, out target) || target < 0 || target >= rowsToMove.Count)) throw InvalidEdits();
            var row = rowsToMove[source];
            rowsToMove.RemoveAt(source);
            rowsToMove.Insert(target, row);
            return;
        }
        if (op is not (null or "replace" or "add") || edit["value"] is not { } value ||
            value is JsonValue scalar && scalar.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s)) throw InvalidEdits();
        if (parent is JsonArray list && key == "-" && op != "replace")
        {
            if (!list.Any(row => RetainsContent(value, row))) list.Add(value.DeepClone());
        }
        else if (parent is JsonArray insert && int.TryParse(key, out var at) &&
            (op == "add" && at >= 0 && at <= insert.Count || op is null && at == insert.Count))
        {
            if (!insert.Any(row => RetainsContent(value, row))) insert.Insert(at, value.DeepClone());
        }
        else if (parent is JsonObject obj && (op != "replace" || obj.ContainsKey(key)))
        {
            if (obj[key] is JsonArray saved && !(value is JsonArray updated && PreservesList(key, saved, updated)))
                throw InvalidEdits("新列表遗漏了原有条目或其中的内容。");
            if (obj[key] is JsonObject old && !(value is JsonObject revised && PreservesRowFields(old, revised)))
                throw InvalidEdits("修改遗漏了原有字段。");
            obj[key] = value.DeepClone();
        }
        else if (parent is JsonArray rows && int.TryParse(key, out var i) && i >= 0 && i < rows.Count)
        {
            if (!(rows[i] is JsonValue && value is JsonValue) &&
                !(rows[i] is JsonObject oldRow && value is JsonObject newRow && PreservesRowFields(oldRow, newRow)))
                throw InvalidEdits("模型返回的替换行缺少原有字段，或改写了嵌套内容。");
            rows[i] = value.DeepClone();
        }
        else throw InvalidEdits();
    }

    // A complete row can express several leaf edits in one operation. A shorter object must
    // not erase an existing register, example or usage case. Nested changes use their own paths.
    private static bool PreservesRowFields(JsonObject original, JsonObject replacement) => original.All(p => p.Value switch
    {
        null => true,
        JsonArray array => array.Count == 0 || replacement[p.Key] is JsonArray rows && PreservesList(p.Key, array, rows),
        JsonObject obj => replacement[p.Key] is JsonObject next && PreservesRowFields(obj, next),
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text) ||
            replacement[p.Key] is JsonValue next && next.TryGetValue<string>(out var revised) && !string.IsNullOrWhiteSpace(revised),
        _ => JsonNode.DeepEquals(p.Value, replacement[p.Key])
    });

    // Whole-list updates are common when a section was empty. Once populated, match each old
    // row exactly once. Named rows can improve their explanations without losing existing detail.
    private static bool PreservesList(string field, JsonArray saved, JsonArray proposed)
    {
        // Suggested labels are a classification the model may revise. The user's notebook
        // tags are stored separately. A new classification must still contain usable labels.
        if (field == "suggestedTags" && proposed.Count > 0)
            return proposed.All(node => node is JsonValue v && v.TryGetValue<string>(out var label) && !string.IsNullOrWhiteSpace(label));
        if (saved.Count > proposed.Count) return false;
        var remaining = proposed.ToList();
        string[] identity = field switch
        {
            "senses" => ["partOfSpeech", "definitionZh"], "usageCases" => ["condition", "expression"],
            "examples" => ["sourceSentence"], "collocations" => ["phrase", "meaning"], "inflections" => ["label", "value"],
            "wordFamily" => ["value", "relation"], "usageContrasts" => ["leftForm", "rightForm"],
            "synonymNuances" => ["word"], "morphemes" => ["part"], "variants" or "translationOptions" => ["text"],
            "translationDecisions" => ["sourceQuote", "targetQuote"], "clauses" or "grammarHighlights" => ["quote"],
            "alignment" => ["source"], "mistakes" => ["wrong"], _ => []
        };
        foreach (var old in saved)
        {
            var i = remaining.FindIndex(row => RetainsContent(old, row));
            if (i < 0 && identity.Length > 0 && old is JsonObject original)
                i = remaining.FindIndex(row => row is JsonObject updated &&
                    identity.All(key => original[key] is not null && RetainsContent(original[key], updated[key])) &&
                    PreservesRowFields(original, updated));
            if (i < 0) return false;
            remaining.RemoveAt(i);
        }
        return true;
    }

    // The parser may fill optional values. Reject any reply where it discarded supplied information.
    private static bool RetainsContent(JsonNode? proposed, JsonNode? parsed) => proposed switch
    {
        null => true,
        JsonObject obj => parsed is JsonObject result && obj.All(p => result.ContainsKey(p.Key) && RetainsContent(p.Value, result[p.Key])),
        JsonArray list => parsed is JsonArray rows && list.Count == rows.Count &&
            list.Zip(rows).All(pair => RetainsContent(pair.First, pair.Second)),
        JsonValue value when value.TryGetValue<string>(out var text) => parsed is JsonValue normalized &&
            normalized.TryGetValue<string>(out var result) && text.Trim() == result.Trim(),
        _ => JsonNode.DeepEquals(proposed, parsed)
    };
}
