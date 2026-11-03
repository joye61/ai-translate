using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    // Extension blocks are judged per query: each must earn its place, and none is required.
    private const string ExtensionGuide = """
        扩展栏目由你按价值判断，宁缺毋滥，不适用就省略字段，不要为凑栏目编造：
        • variants（句子、段落、短语、习语）：当存在风格明显不同且都正确的译法时给二至三个 {"style":"直译|意译|口语|正式|书面|简洁|文学 之一","text":"完整译文","note":"何时选用（可选）"}。text 必须与 primaryTranslation 不同；段落最多两个。
        • keyPoints（段落）：信息较多时给二至四条要点，每条一句目标语短句，概括而非逐句复述。
        • alignment（段落）：逐句对照数组 {"source":"原文中逐字摘录的一句","target":"主译文中对应的部分"}，按原文顺序覆盖全文。
        • clauses（结构复杂的句子：从句、长修饰、并列、倒装、插入语）：句子骨架数组 {"quote":"原文连续片段","role":"主干|主语从句|宾语从句|定语从句|状语从句|同位语|非谓语|插入语|并列分句 等","depth":0,"note":"作用简述（可选）"}。depth 0 为主干，从属成分依层级递增，最多三层六项，按原文顺序。简单句省略。
        • tone（句子、段落）：{"formality":1到5（1 口语随意，5 正式书面）,"tones":["客观","委婉"等不超过三个],"note":"语气对译法的影响（可选）"}。
        • literalMeaning（习语、比喻性短语）：字面直译，仅在与实际含义不同时给出。
        • culturalNote（习语、俚语、典故、文化特有概念）：简述来源或文化背景及使用时需注意的地方。
        • domains（术语或带专业义的词条）：所属领域，最多三个，如 ["计算机","金融"]。
        • morphemes（可拆分的派生词或合成词）：按词中顺序 {"part":"in-","meaning":"向内","kind":"prefix|root|suffix"}，至少两部分；单语素词或不确定时省略。etymologyAndRoot 只写词源演变，不重复拆分。
        • synonymNuances（有易混近义词时）：二至四行对比表，首行为查询词自身，每行 {"word":"词","nuance":"侧重点或区别","register":"语域（可选）"}。已给 synonymNuances 时不再写 wordChoice。
        说明性文字（reason、explanation、note、nuance、why、usageNotes、contextMeaning、wordChoice、etymologyAndRoot、memoryMnemonic、culturalNote、scenario）可用 **关键词** 强调至多两处，用 `原文词形` 引用源语片段；不使用其它 Markdown、标题或列表符号。译文、释义、例句及所有引文字段保持纯文本。
        """;

    private static IReadOnlyList<TranslationVariant> ReadVariants(JsonElement root, string primary, bool preview)
    {
        var variants = new List<TranslationVariant>();
        foreach (var item in Objects(root, "variants"))
        {
            var style = GetString(item, "style").Trim();
            var text = GetString(item, "text").Trim();
            if (style.Length == 0 || text.Length == 0) continue;
            if (!preview && string.Equals(Normalize(text), Normalize(primary), StringComparison.OrdinalIgnoreCase)) continue;
            if (variants.Any(v => v.Text == text || v.Style == style)) continue;
            variants.Add(new(style, text, GetString(item, "note").Trim()));
            if (variants.Count == 3) break;
        }
        return variants;

        static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)));
    }

    private static IReadOnlyList<ClauseNode> ReadClauses(string source, JsonElement root)
    {
        var clauses = new List<ClauseNode>();
        foreach (var item in Objects(root, "clauses"))
        {
            var quote = GetString(item, "quote").Trim();
            var role = GetString(item, "role").Trim();
            if (quote.Length == 0 || role.Length == 0) continue;
            if (DictionaryTextMatch.IndexOf(source, quote, comparison: StringComparison.Ordinal) < 0) continue;
            var depth = item.TryGetProperty("depth", out var d) && d.TryGetInt32(out var level) ? Math.Clamp(level, 0, 2) : 0;
            // A node may be at most one level deeper than the previous one; otherwise the tree has a gap.
            depth = Math.Min(depth, clauses.Count == 0 ? 0 : clauses[^1].Depth + 1);
            clauses.Add(new(quote, role, depth, GetString(item, "note").Trim()));
            if (clauses.Count == 6) break;
        }
        return clauses.Count >= 2 ? clauses : [];
    }

    private static IReadOnlyList<AlignedSentence> ReadAlignment(string source, JsonElement root)
    {
        var rows = new List<AlignedSentence>();
        var cursor = 0;
        foreach (var item in Objects(root, "alignment"))
        {
            var src = GetString(item, "source").Trim();
            var target = GetString(item, "target").Trim();
            if (src.Length == 0 || target.Length == 0) continue;
            var at = DictionaryTextMatch.IndexOf(source, src, cursor, StringComparison.Ordinal);
            if (at < 0) continue;
            cursor = at + src.Length;
            rows.Add(new(src, target));
        }
        return rows.Count >= 2 ? rows : [];
    }

    private static ToneProfile? ReadTone(JsonElement root)
    {
        if (!root.TryGetProperty("tone", out var tone) || tone.ValueKind != JsonValueKind.Object) return null;
        var formality = tone.TryGetProperty("formality", out var f) && f.TryGetInt32(out var level) && level is >= 1 and <= 5 ? level : 0;
        var profile = new ToneProfile(formality, GetStringArray(tone, "tones").Take(3).ToArray(), GetString(tone, "note").Trim());
        return profile.IsEmpty ? null : profile;
    }

    private static IReadOnlyList<Morpheme> ReadMorphemes(JsonElement root)
    {
        var parts = new List<Morpheme>();
        foreach (var item in Objects(root, "morphemes"))
        {
            var part = GetString(item, "part").Trim();
            var meaning = GetString(item, "meaning").Trim();
            if (part.Length == 0 || meaning.Length == 0) continue;
            var kind = GetString(item, "kind").Trim().ToLowerInvariant() switch
            {
                "prefix" or "前缀" => MorphemeKind.Prefix,
                "suffix" or "后缀" => MorphemeKind.Suffix,
                _ => part.EndsWith('-') ? MorphemeKind.Prefix : part.StartsWith('-') ? MorphemeKind.Suffix : MorphemeKind.Root
            };
            parts.Add(new(part, meaning, kind));
            if (parts.Count == 5) break;
        }
        return parts.Count >= 2 ? parts : [];
    }

    private static IReadOnlyList<SynonymNuance> ReadSynonymNuances(JsonElement root)
    {
        var rows = new List<SynonymNuance>();
        foreach (var item in Objects(root, "synonymNuances"))
        {
            var word = GetString(item, "word").Trim();
            var nuance = GetString(item, "nuance").Trim();
            if (word.Length == 0 || nuance.Length == 0 || rows.Any(r => string.Equals(r.Word, word, StringComparison.OrdinalIgnoreCase))) continue;
            rows.Add(new(word, nuance, GetString(item, "register").Trim()));
            if (rows.Count == 4) break;
        }
        return rows.Count >= 2 ? rows : [];
    }

    private static IReadOnlyList<UsageMistake> ReadMistakes(JsonElement root, bool preview)
    {
        var rows = new List<UsageMistake>();
        foreach (var item in Objects(root, "mistakes"))
        {
            var wrong = GetString(item, "wrong").Trim();
            var right = GetString(item, "right").Trim();
            if (wrong.Length == 0 || (!preview && (right.Length == 0 || wrong == right))) continue;
            rows.Add(new(wrong, right, GetString(item, "why").Trim()));
            if (rows.Count == 3) break;
        }
        return rows;
    }

    private static IEnumerable<JsonElement> Objects(JsonElement root, string property) =>
        root.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
            : [];
}
