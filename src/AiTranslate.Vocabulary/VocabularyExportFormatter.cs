using System.Text;
using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

/// <summary>
/// Multi-format exporter (Markdown, Anki TSV, Excel/CSV, JSON) for Vocabulary Entries.
/// </summary>
internal static class VocabularyExportFormatter
{
    public static string ToMarkdown(IReadOnlyList<VocabularyEntry> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# AI Translate 精选生词本导出");
        sb.AppendLine($"> 导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm} · 共 {items.Count} 条词条");
        sb.AppendLine();

        foreach (var item in items)
        {
            sb.AppendLine($"## {item.Headword} {(string.IsNullOrWhiteSpace(item.UsPhonetic) ? "" : $"`{item.UsPhonetic}`")}");
            if (string.IsNullOrWhiteSpace(item.LiteralTranslation))
                sb.AppendLine($"- **核心释义**：{item.Translation}");
            else
            {
                sb.AppendLine($"- **自然译文**：{item.Translation}");
                sb.AppendLine($"- **直译对照**：{item.LiteralTranslation}");
            }
            sb.AppendLine($"- **类型**：`{item.ItemType}`");
            if (!string.IsNullOrWhiteSpace(item.EtymologyAndRoot))
                sb.AppendLine($"- **词根词源**：{item.EtymologyAndRoot}");
            if (!string.IsNullOrWhiteSpace(item.MemoryMnemonic))
                sb.AppendLine($"- **AI 巧记**：{item.MemoryMnemonic}");
            if (!string.IsNullOrWhiteSpace(item.ContextSentence))
                sb.AppendLine($"- **来源原句**：{item.ContextSentence}");
            if (item.Examples.Count > 0)
            {
                sb.AppendLine("- **双语例句**：");
                foreach (var ex in item.Examples)
                    sb.AppendLine($"  - *{ex.SourceSentence}* — {ex.TranslatedSentence}");
            }
            foreach (var note in LearningNotes(item)) sb.AppendLine($"- {note}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToAnkiTsv(IReadOnlyList<VocabularyEntry> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#separator:tab");
        sb.AppendLine("#html:true");
        sb.AppendLine("#columns:Front\tBack\tPhonetic\tMnemonic\tExample");
        foreach (var item in items)
        {
            static string Clean(string s) => s.Replace("\t", " ").Replace("\r", "").Replace("\n", "<br>");
            var example = item.Examples.FirstOrDefault() is { } ex ? $"{ex.SourceSentence}<br>{ex.TranslatedSentence}" : item.ContextSentence;
            sb.Append(Clean(item.Headword)).Append('\t')
              .Append(Clean((string.IsNullOrWhiteSpace(item.LiteralTranslation) ? item.Translation :
                  $"自然译文：{item.Translation}\n直译对照：{item.LiteralTranslation}") +
                  string.Concat(LearningNotes(item, includeMnemonics: false).Select(note => "\n" + System.Net.WebUtility.HtmlEncode(note))))).Append('\t')
              .Append(Clean(item.UsPhonetic)).Append('\t')
              .Append(Clean(System.Net.WebUtility.HtmlEncode(MnemonicText(item)))).Append('\t')
              .AppendLine(Clean(example));
        }
        return sb.ToString();
    }

    public static string ToCsv(IReadOnlyList<VocabularyEntry> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Headword,Phonetic,Translation,ItemType,Etymology,Mnemonic,ContextSentence,LiteralTranslation,LearningNotes");
        static string CsvEscape(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
        foreach (var item in items)
        {
            sb.AppendLine(string.Join(",",
                CsvEscape(item.Headword),
                CsvEscape(item.UsPhonetic),
                CsvEscape(item.Translation),
                CsvEscape(item.ItemType.ToString()),
                CsvEscape(EtymologyText(item)),
                CsvEscape(MnemonicText(item)),
                CsvEscape(item.ContextSentence),
                CsvEscape(item.LiteralTranslation ?? ""),
                CsvEscape(string.Join("\n", LearningNotes(item, includeMnemonics: false, includeEtymology: false)))));
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<VocabularyEntry> items) =>
        JsonSerializer.Serialize(items, VocabularyService.JsonOptions);

    private static string EtymologyText(VocabularyEntry item) => string.Join("\n",
        item.EtymologyNotes.Select(n => $"{InlineMarkup.Strip(n.Title)}：{InlineMarkup.Strip(n.Explanation)}")
            .Prepend(InlineMarkup.Strip(item.EtymologyAndRoot)).Where(s => !string.IsNullOrWhiteSpace(s)));

    private static string MnemonicText(VocabularyEntry item) => string.Join("\n",
        item.Mnemonics.Select(m => $"{m.Method}：{m.Cue} — {InlineMarkup.Strip(m.Explanation)}")
            .Prepend(InlineMarkup.Strip(item.MemoryMnemonic)).Where(s => !string.IsNullOrWhiteSpace(s)));

    private static IEnumerable<string> LearningNotes(VocabularyEntry item, bool includeMnemonics = true, bool includeEtymology = true)
    {
        static string Example(BilingualExample? e) => e is null ? "" : $" — {e.SourceSentence} / {e.TranslatedSentence}";
        foreach (var pair in item.UsageContrasts)
        {
            yield return $"用法对照：{pair.LeftForm} — {InlineMarkup.Strip(pair.LeftUse)}{Example(pair.LeftExample)}";
            yield return $"对照表达：{pair.RightForm} — {InlineMarkup.Strip(pair.RightUse)}{Example(pair.RightExample)}";
        }
        foreach (var note in item.UsageGuidance)
            yield return $"用法 · {InlineMarkup.Strip(note.Title)}：{InlineMarkup.Strip(note.Explanation)}{Example(note.Example)}";
        if (!string.IsNullOrWhiteSpace(item.GrammarOrUsageNote))
            yield return $"用法说明：{InlineMarkup.Strip(item.GrammarOrUsageNote)}";
        foreach (var drill in item.PronunciationDrills)
            yield return $"发音 · {InlineMarkup.Strip(drill.Focus)}：{drill.Text} — {InlineMarkup.Strip(drill.Tip)}";
        if (!string.IsNullOrWhiteSpace(item.PronunciationTip))
            yield return $"发音提示：{InlineMarkup.Strip(item.PronunciationTip)}";
        if (includeEtymology)
            foreach (var note in item.EtymologyNotes)
                yield return $"词源 · {InlineMarkup.Strip(note.Title)}：{InlineMarkup.Strip(note.Explanation)}";
        if (includeMnemonics)
            foreach (var strategy in item.Mnemonics)
                yield return $"助记 · {InlineMarkup.Strip(strategy.Method)}：{strategy.Cue} — {InlineMarkup.Strip(strategy.Explanation)}";
        foreach (var note in item.CulturalNotes)
            yield return $"文化 · {InlineMarkup.Strip(note.Title)}：{InlineMarkup.Strip(note.Explanation)}";
        foreach (var evidence in item.ToneEvidence.Where(e => DictionaryTextMatch.IndexOf(item.Headword, e.Quote, comparison: StringComparison.Ordinal) >= 0))
            yield return $"语气 · {InlineMarkup.Strip(evidence.Aspect)}：{evidence.Quote} — {InlineMarkup.Strip(evidence.Explanation)}";
    }
}
