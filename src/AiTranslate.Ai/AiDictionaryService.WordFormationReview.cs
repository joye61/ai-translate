using System.Text.Json.Nodes;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    private const string WordFormationReviewGuide = """
        etymologyAndRoot 是独立的词源说明。仅修订它时，省略 morphemes；历史词源不足以证明现代词形可以拆分。
        拼写相似、译义相近或其他栏目的助记联想都不能证明或否定同源关系。证据不足时省略该断言。
        词素按词中顺序给出实际可解释的部分，可以是二项，也可以是三至五项。不要为凑数拆词或编造同源关系。
        每项形如 {"part":"un-","meaning":"不","kind":"prefix","note":"可选说明"}。kind 取 prefix、root、suffix；
        meaning 保持简短，较长的解释放 note。已有拆分为空且可以可靠补充时，可一次填写完整的 morphemes 数组。
        """;

    // A review of a legacy entry with no morphemes has no sample row to copy.
    // Accept the alternate spellings observed in provider replies before validating the typed rows.
    private static JsonNode NormalizeMorphemeEdit(string[] path, JsonNode edit)
    {
        var value = edit["value"];
        if (path.Length == 1 && value is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonObject>()) NormalizeMorphemeRow(row);
        }
        else if (path.Length == 2 && value is JsonObject row) NormalizeMorphemeRow(row);
        else if (path.Length == 3)
        {
            path[2] = path[2] switch { "text" => "part", "type" => "kind", _ => path[2] };
            if (path[2] == "kind") NormalizeMorphemeKind(edit.AsObject(), "value");
        }
        return edit;
    }

    private static void NormalizeMorphemeRow(JsonObject row)
    {
        UseCanonicalName("text", "part");
        NormalizeMorphemeKind(row, "kind");
        NormalizeMorphemeKind(row, "type");
        UseCanonicalName("type", "kind");

        void UseCanonicalName(string alias, string canonical)
        {
            if (!row.Remove(alias, out var alternative)) return;
            if (row[canonical] is { } existing && !RetainsContent(existing, alternative))
                throw InvalidEdits("模型为同一个词素字段给出了互相冲突的值。");
            row[canonical] = alternative;
        }
    }

    private static void NormalizeMorphemeKind(JsonObject owner, string property)
    {
        if (owner[property] is not JsonValue value || !value.TryGetValue<string>(out var kind)) return;
        owner[property] = kind.Trim().ToLowerInvariant() switch
        {
            "prefix" or "前缀" => "prefix",
            "root" or "词根" => "root",
            "suffix" or "后缀" => "suffix",
            _ => throw InvalidEdits("词素类型需为前缀、词根或后缀。")
        };
    }
}
