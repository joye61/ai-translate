using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    // Extension blocks are judged per query: each must earn its place, and none is required.
    private const string ExtensionGuide = """
        扩展栏目由你按价值判断，宁缺毋滥，不适用就省略字段，不要为凑栏目编造：
        • variants（句子、段落、短语、习语）：仅在另有确切的场合或语域差异值得讲解时给 {"style":"口语|正式|书面|文学 之一","text":"完整译文","note":"适用场合"}。不要用此栏目给直译与自然译文，也不要在 translationOptions、literalMeaning 重复这组对照。
        • keyPoints（段落）：信息较多时按独立的重要内容列要点，每条一句目标语短句，概括而非逐句复述。
        • alignment（段落）：逐句对照数组 {"source":"原文中逐字摘录的一句","target":"主译文中对应的部分"}，按原文顺序覆盖全文。
        • clauses（结构复杂的句子：从句、长修饰、并列、倒装、插入语）：句子骨架数组 {"quote":"原文连续片段","role":"主干|主语从句|宾语从句|定语从句|状语从句|同位语|非谓语|插入语|并列分句 等","depth":0,"note":"作用简述（可选）"}。depth 0 为主干，从属成分依层级递增，节点数量和深度按实际结构确定，按原文顺序。简单句省略。
        • tone（句子、段落）：{"formality":1到5（1 口语随意，5 正式书面）,"tones":["客观","委婉"等实际成立的不同特征],"note":"语气对译法的影响（可选）"}。
        • 习语、比喻性短语的字面形象与交际含义不同时，同样在开头用 literalTranslation 给直译，把实际要表达的意思放在 primaryTranslation。
        • culturalNotes（习语、俚语、典故、文化特有概念）：按独立背景与使用难点分条说明。
        • domains（术语或带专业义的词条）：所属的实际常见领域，不重复列上位与下位标签，如 ["计算机","金融"]。
        • morphemes（可可靠拆分的词）：按词中顺序给出所有可可靠解释的实际组成部分，每项 {"part":"in-","meaning":"向内","kind":"prefix|root|suffix","note":"可选的词形或用法说明"}。按实际结构拆分，单语素词或无法可靠拆分时省略。etymologyNotes 可分条解释可靠的词源与演变。
        • synonymNuances（有易混近义词时）：按实际易混词列对比表，首行为查询词自身，每行 {"word":"词","nuance":"侧重点或区别","register":"语域（可选）"}。已给 synonymNuances 时不再写 wordChoice。
        说明性文字（reason、explanation、note、nuance、why、tip、usageNotes、contextMeaning、wordChoice、etymologyAndRoot、memoryMnemonic、culturalNote、scenario）可用 **关键词** 强调至多两处，用 `原文词形` 引用源语片段；不使用其它 Markdown、标题或列表符号。译文、释义、例句及所有引文字段保持纯文本。
        """;

    private static IReadOnlyList<TranslationOption> ReadTranslationOptions(JsonElement root)
    {
        var options = new List<TranslationOption>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Objects(root, "translationOptions"))
        {
            var text = GetString(item, "text").Trim();
            var meaning = GetString(item, "meaning").Trim();
            if (text.Length == 0 || meaning.Length == 0 || !seen.Add(text)) continue;
            options.Add(new(text, meaning, GetString(item, "usage").Trim()));
        }
        return options;
    }

    private static bool SameRendering(string first, string second)
    {
        static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)));
        return string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadLiteralTranslation(JsonElement root, string primary, VocabularyItemType type, bool preview)
    {
        var literal = GetString(root, "literalTranslation").Trim();
        if (literal.Length == 0 || preview && primary.StartsWith(literal, StringComparison.OrdinalIgnoreCase)) return "";
        if (SameRendering(primary, literal)) return "";
        if (!type.IsPassage() && primary.Split([';', '；']).Any(part => SameRendering(part, literal))) return "";
        return literal;
    }

    private static IReadOnlyList<TranslationVariant> ReadVariants(JsonElement root, string primary, string literal, bool preview)
    {
        var variants = new List<TranslationVariant>();
        foreach (var item in Objects(root, "variants"))
        {
            var style = GetString(item, "style").Trim();
            var text = GetString(item, "text").Trim();
            if (style.Length == 0 || text.Length == 0) continue;
            if (!preview && (SameRendering(text, primary) || SameRendering(text, literal))) continue;
            if (variants.Any(v => SameRendering(v.Text, text))) continue;
            variants.Add(new(style, text, GetString(item, "note").Trim()));
        }
        return variants;
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
            var depth = item.TryGetProperty("depth", out var d) && d.ValueKind == JsonValueKind.Number &&
                d.TryGetInt32(out var level) ? Math.Max(level, 0) : 0;
            // A node may be at most one level deeper than the previous one; otherwise the tree has a gap.
            depth = Math.Min(depth, clauses.Count == 0 ? 0 : clauses[^1].Depth + 1);
            clauses.Add(new(quote, role, depth, GetString(item, "note").Trim()));
        }
        return clauses.Count >= 2 ? clauses : [];
    }

    private static IReadOnlyList<AlignedSentence> ReadAlignment(string source, string translation, JsonElement root)
    {
        var rows = new List<AlignedSentence>();
        var cursor = 0;
        foreach (var item in Objects(root, "alignment"))
        {
            var src = GetString(item, "source").Trim();
            var target = GetString(item, "target").Trim();
            if (src.Length == 0 || target.Length == 0 ||
                DictionaryTextMatch.IndexOf(translation, target, comparison: StringComparison.Ordinal) < 0) continue;
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
        var formality = tone.TryGetProperty("formality", out var f) && f.ValueKind == JsonValueKind.Number &&
            f.TryGetInt32(out var level) && level is >= 1 and <= 5 ? level : 0;
        var profile = new ToneProfile(formality, GetStringArray(tone, "tones").Distinct().ToArray(), GetString(tone, "note").Trim());
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
                "root" or "词根" => MorphemeKind.Root,
                "suffix" or "后缀" => MorphemeKind.Suffix,
                _ => part.EndsWith('-') ? MorphemeKind.Prefix : part.StartsWith('-') ? MorphemeKind.Suffix : MorphemeKind.Root
            };
            parts.Add(new(part, meaning, kind, GetString(item, "note").Trim()));
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
            var mistake = new UsageMistake(wrong, right, GetString(item, "why").Trim());
            if (!rows.Any(r => r.Wrong == wrong && r.Right == right)) rows.Add(mistake);
        }
        return rows;
    }

    private static IEnumerable<JsonElement> Objects(JsonElement root, string property) =>
        root.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object)
            : [];
}
