using System.Text.RegularExpressions;

namespace AiTranslate.Core;

public static partial class DictionaryQuery
{
    public static bool IsPassage(this VocabularyItemType type) =>
        type is VocabularyItemType.Sentence or VocabularyItemType.Paragraph;

    public static bool IsWordOrTerm(this VocabularyItemType type) =>
        type is VocabularyItemType.Word or VocabularyItemType.TechnicalTerm;

    public static IEnumerable<string> PrimaryRenderings(this DictionaryEntryResult entry)
    {
        if (entry.ItemType.IsPassage()) return [entry.PrimaryTranslation];
        var options = entry.TranslationOptions.Select(o => o.Text).ToArray();
        return entry.PrimaryTranslation.Split([';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part =>
            {
                // A provider can use commas or other punctuation between structured candidates.
                // Keep punctuation inside an expression intact when there is no structured match.
                var matches = options.Where(o => DictionaryTextMatch.IndexOf(part, o, comparison: StringComparison.Ordinal) >= 0).ToArray();
                return matches.Length > 1 && !matches.Contains(part, StringComparer.Ordinal) ? matches : [part];
            });
    }

    // A layout hint while the request is pending, also used for providers that omit itemType.
    // The structured response can distinguish a short sentence from a phrase more accurately.
    public static VocabularyItemType SuggestType(string text)
    {
        text = text.Trim();
        if (text.Contains('\n') || text.Contains('\r') || NextSentence().IsMatch(text))
            return VocabularyItemType.Paragraph;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (text.IndexOfAny(['!', '?', '。', '！', '？', ';', '；']) >= 0 ||
            (words > 1 && text.EndsWith('.')) || words > 7 ||
            text.Count(c => c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or >= '\uac00' and <= '\ud7af') >= 14)
            return VocabularyItemType.Sentence;
        return words > 1 ? VocabularyItemType.Phrase : VocabularyItemType.Word;
    }

    [GeneratedRegex("""(?:[。！？]+[”’」』]*\s*|[.!?]+["'”’)]*\s+)\p{L}""")]
    private static partial Regex NextSentence();
}
