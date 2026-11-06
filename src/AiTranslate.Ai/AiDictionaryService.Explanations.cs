using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    private const string BlockCompletenessGuide = """
        每个适用栏目独立检查覆盖度，不把“一条示例”当作已经讲全。不按固定行数截断有价值的内容，也不为了数量重写同一事实。
        词条按常用义与实际学习难点展开；句段按原文逐处检查关键结构、信息和翻译决策。内容越复杂，所需条目可以越多。
        同一信息放在最贴切的栏目：义项内条件归 usageCases，跨表达对照归 usageContrasts，近义差异归 synonymNuances；事实词源与人为助记分开。
        原文、主译文、直译、字面义及特定语境义各自围绕同一原文，不为增加条数引入无关含义。仅有一个可靠要点就保留一个，不适用则省略。
        """;

    private const string EtymologyGuide = """
        构词与词源除 morphemes 外，使用 etymologyNotes 数组，每项 {title,explanation}，分别讲清有依据的历史来源、词义演变、同源关系或现代词形的解释边界。
        不只列出某个古词就结束：能可靠说明时，交代古义与现代意义如何联系，或为何是不规则词形、不能按现代拼写硬拆。
        title 为简短主题，explanation 用读者母语。不同层面分别列项，只有一个可证实层面时只给一个。不编造年代、人物、典故、文献、同源关系；存在争议就明确不确定。
        etymologyAndRoot 为旧版单段字段，新结果优先给 etymologyNotes，避免重复；人工联想只能放 mnemonics。
        """;

    private const string MnemonicGuide = """
        记忆助记用 mnemonics 数组，每项 {method,cue,explanation}：method 是具体记忆方法，cue 是可直接记住的短口诀、画面或表达，explanation 说明怎样从线索回忆本词的拼写、含义或用法。
        从词形联系、语义对比、场景画面、短句提取等角度选真正不同的有效方法。能提供多种有效路径时分别列出，不只写一句泛泛口诀；单一方法已经足够时可以只列一条。
        每条必须给具体线索和回忆步骤，不要只说“联想记忆”“多读多背”。同一句改写或重复释义不算新方法。
        人工联想明确标为联想，不能冒充词源；谐音不能代替标准发音，不把错误拆词当事实。method、cue 用纯文本，explanation 可用说明性行内标记。
        memoryMnemonic 为旧版字段，新结果优先给 mnemonics，不重复输出同一助记。
        """;

    private const string CultureGuide = """
        文化与典故用 culturalNotes 数组，每项 {title,explanation}，将确有依据的背景、交际含义、适用场合或跨文化误解分开说明。
        背景须直接帮助理解当前表达或原文，不用一句泛泛的“文化差异”代替具体解释。起源不确定时说明不确定，不编造故事或把群体习惯写成绝对规则。
        仅在适用类型（短语、习语、句段）且有学习价值时给出，可有多条。culturalNote 为旧版单段字段，新结果不重复输出。
        """;

    private const string ToneGuide = """
        语气与语域用 tone 给整体概况：formality 为 1 至 5 的整数，tones 是实际存在且不重复的语气标签，note 可写总体说明。
        用 toneEvidence 数组解释判断依据，每项 {quote,aspect,explanation}。quote 逐字摘录原文的连续片段，aspect 为语气或语域特征，explanation 结合该措辞解释对态度、礼貌、距离或译法的影响。
        有多个不同特征时逐项给依据，不能只贴标签。不要从单一词语武断推断说话者情绪，不重复关键语法或译法依据已解释的同一事实。仅适用于句子、段落。
        """;

    private static IReadOnlyList<EntryExplanation> ReadExplanations(JsonElement root, string field, bool preview)
    {
        var notes = new List<EntryExplanation>();
        foreach (var row in Objects(root, field))
        {
            var title = GetString(row, "title").Trim();
            var explanation = GetString(row, "explanation").Trim();
            if (title.Length == 0 || !preview && explanation.Length == 0) continue;
            var note = new EntryExplanation(title, explanation);
            if (!notes.Contains(note)) notes.Add(note);
        }
        return notes;
    }

    private static IReadOnlyList<MnemonicStrategy> ReadMnemonics(JsonElement root, bool preview)
    {
        var strategies = new List<MnemonicStrategy>();
        foreach (var row in Objects(root, "mnemonics"))
        {
            var method = GetString(row, "method").Trim();
            var cue = GetString(row, "cue").Trim();
            var explanation = GetString(row, "explanation").Trim();
            if (method.Length == 0 || !preview && (cue.Length == 0 || explanation.Length == 0)) continue;
            if (!strategies.Any(m => m.Method == method && m.Cue == cue)) strategies.Add(new(method, cue, explanation));
        }
        return strategies;
    }

    private static IReadOnlyList<ToneEvidence> ReadToneEvidence(string source, JsonElement root, bool preview)
    {
        var evidence = new List<ToneEvidence>();
        foreach (var row in Objects(root, "toneEvidence"))
        {
            var quote = GetString(row, "quote").Trim();
            var aspect = GetString(row, "aspect").Trim();
            var explanation = GetString(row, "explanation").Trim();
            if (quote.Length == 0 || aspect.Length == 0 || !preview && explanation.Length == 0 ||
                DictionaryTextMatch.IndexOf(source, quote, comparison: StringComparison.Ordinal) < 0) continue;
            if (!evidence.Any(e => e.Quote == quote && e.Aspect == aspect)) evidence.Add(new(quote, aspect, explanation));
        }
        return evidence;
    }
}
