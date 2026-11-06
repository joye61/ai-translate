using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public sealed partial class AiDictionaryService
{
    private const string UsageContrastGuide = """
        用法对照 usageContrasts：逐一检查补语、介词、句型、语域和易混表达中会影响含义或选用的独立差别。
        如 stop、remember、try 的动名词与不定式有意义差别，须成对展示。存在多个常用对照时逐组列出，不在第一组后停止；通常二至四组，有更多重要差别可继续，只有一组或没有时如实给出，不凑数。
        每组含 leftForm、leftUse、rightForm、rightUse，并用 leftExample、rightExample 给成对的简短双语例句，各含 sourceSentence、translatedSentence、highlightTerm。
        两边都必须是自然、正确且直接关乎查询词的表达，说明各自含义和选择条件，例句真正体现区别。同一对照换宾语不算新组，倒置左右也不重列。
        不重复 usageCases、translationOptions 或 synonymNuances 已讲清的差别；不把错误说法当作合法对照。缩写须保留大小写与领域限制，不凭字面相似推断用法。
        """;

    private const string UsageGuidanceGuide = """
        用法与辨析使用 usageGuidance 数组，每项 {title,explanation,example}。title 为简短主题，explanation 用读者母语说明适用条件、限制及如何选择，example 为含 sourceSentence、translatedSentence、highlightTerm 的双语例句。
        按实际难点分别检查语域、领域与缩写消歧、语法限制、书写习惯和语用。通常二至五条，复杂词可更多；仅一条有价值就只给一条，没有就省略，不设凑数下限。
        每条只讲一个可用于判断或表达的要点，不把多个领域的区别塞进一个长段。例句示范这一要点，不能只复述释义；不重复其他栏目已说明的义项、搭配或对照。
        句段只解释原文中尚未说明的难点，允许省略额外造句。usageNotes 是旧版单段字段，新结果优先用 usageGuidance，不再重复输出 usageNotes。
        """;

    private const string PronunciationDrillGuide = """
        发音练习 pronunciationDrills 为数组，每项 {focus,text,tip}。focus 是练习目标，text 是可直接朗读的纯源语词语或短句，tip 用读者母语给具体的发音动作、重音或节奏要领。
        针对词头实际难点从音节与重音、易错音、词组衔接或句中节奏选不同练习，通常二至三项；简单词可更少，无可靠提示则省略。不能每项都泛称“多听多读”。
        练习文本应含查询词或其自然词形，先单词再短语或短句。缩写应核实字母逐读还是整词读，保留大小写，不套用普通单词的音节拆分；逐字母读时 text 可用空格分隔字母（如 G E O）。
        text 不夹带 IPA、分音符号、中文指令或人为拼写；这些说明放 tip。只适用于 Word 类型且有可靠读法的词，不能臆造地域差异。pronunciationTip 为旧版单段提示，新结果不重复输出。
        """;

    private static IReadOnlyList<UsageGuidance> ReadUsageGuidance(JsonElement root, bool preview)
    {
        if (!root.TryGetProperty("usageGuidance", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var result = new List<UsageGuidance>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = GetString(item, "title").Trim();
            var explanation = GetString(item, "explanation").Trim();
            if (title.Length == 0 || !preview && explanation.Length == 0) continue;
            var note = new UsageGuidance(title, explanation) { Example = ReadUsageExample(item, preview) };
            if (!result.Contains(note)) result.Add(note);
        }
        return result;
    }

    private static IReadOnlyList<PronunciationDrill> ReadPronunciationDrills(JsonElement root, bool preview)
    {
        if (!root.TryGetProperty("pronunciationDrills", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var result = new List<PronunciationDrill>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var focus = GetString(item, "focus").Trim();
            var text = GetString(item, "text").Trim();
            var tip = GetString(item, "tip").Trim();
            if (focus.Length == 0 || !preview && (text.Length == 0 || tip.Length == 0)) continue;
            var drill = new PronunciationDrill(focus, text, tip);
            if (!result.Contains(drill)) result.Add(drill);
        }
        return result;
    }
}
