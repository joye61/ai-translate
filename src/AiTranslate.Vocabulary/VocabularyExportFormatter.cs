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
            sb.AppendLine($"- **类型**：`{item.ItemType}` · **掌握度**：{new string('★', item.MasteryLevel)}{new string('☆', 5 - item.MasteryLevel)} ({item.Stage})");
            if (item.Tags.Count > 0)
                sb.AppendLine($"- **标签**：{string.Join(", ", item.Tags.Select(t => $"`#{t}`"))}");
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
            if (!string.IsNullOrWhiteSpace(item.UserPersonalNote))
                sb.AppendLine($"- **个人笔记**：{item.UserPersonalNote}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    public static string ToAnkiTsv(IReadOnlyList<VocabularyEntry> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#separator:tab");
        sb.AppendLine("#html:true");
        sb.AppendLine("#columns:Front\tBack\tPhonetic\tMnemonic\tExample\tTags");
        foreach (var item in items)
        {
            static string Clean(string s) => s.Replace("\t", " ").Replace("\r", "").Replace("\n", "<br>");
            var example = item.Examples.FirstOrDefault() is { } ex ? $"{ex.SourceSentence}<br>{ex.TranslatedSentence}" : item.ContextSentence;
            sb.Append(Clean(item.Headword)).Append('\t')
              .Append(Clean(string.IsNullOrWhiteSpace(item.LiteralTranslation) ? item.Translation :
                  $"自然译文：{item.Translation}\n直译对照：{item.LiteralTranslation}")).Append('\t')
              .Append(Clean(item.UsPhonetic)).Append('\t')
              .Append(Clean(InlineMarkup.Strip(item.MemoryMnemonic))).Append('\t')
              .Append(Clean(example)).Append('\t')
              .AppendLine(Clean(string.Join(" ", item.Tags)));
        }
        return sb.ToString();
    }

    public static string ToCsv(IReadOnlyList<VocabularyEntry> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Headword,Phonetic,Translation,ItemType,Stage,MasteryLevel,Etymology,Mnemonic,ContextSentence,PersonalNote,Tags,LiteralTranslation");
        static string CsvEscape(string s) => $"\"{s.Replace("\"", "\"\"")}\"";
        foreach (var item in items)
        {
            sb.AppendLine(string.Join(",",
                CsvEscape(item.Headword),
                CsvEscape(item.UsPhonetic),
                CsvEscape(item.Translation),
                CsvEscape(item.ItemType.ToString()),
                CsvEscape(item.Stage.ToString()),
                item.MasteryLevel,
                CsvEscape(InlineMarkup.Strip(item.EtymologyAndRoot)),
                CsvEscape(InlineMarkup.Strip(item.MemoryMnemonic)),
                CsvEscape(item.ContextSentence),
                CsvEscape(item.UserPersonalNote),
                CsvEscape(string.Join(";", item.Tags)),
                CsvEscape(item.LiteralTranslation ?? "")));
        }
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<VocabularyEntry> items) =>
        JsonSerializer.Serialize(items, VocabularyService.JsonOptions);
}
