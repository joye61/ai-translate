using System.Text;

namespace AiTranslate.Core;

public sealed record SentenceCloze(string Source, string Masked, string Answer, string Translation)
{
    public static SentenceCloze? FromExample(BilingualExample example, string headword,
        IReadOnlyList<WordInflection> inflections, string? expression = null)
    {
        var source = example.SourceSentence;
        if (string.IsNullOrWhiteSpace(source)) return null;
        var candidates = new[] { example.HighlightTerm, expression, headword }.Concat(inflections.Select(f => f.Value));
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var term = candidate.Trim();
            if (!term.EnumerateRunes().Any(Rune.IsLetterOrDigit)) continue;
            var index = DictionaryTextMatch.IndexOf(source, term);
            if (index < 0) continue;
            var answer = source.Substring(index, term.Length);
            var masked = new StringBuilder();
            var offset = 0;
            do
            {
                masked.Append(source, offset, index - offset).Append("_____");
                offset = index + term.Length;
                index = DictionaryTextMatch.IndexOf(source, term, offset);
            } while (index >= 0);
            masked.Append(source, offset, source.Length - offset);
            var prompt = masked.ToString();
            if (prompt.EnumerateRunes().Any(Rune.IsLetterOrDigit))
                return new(source, prompt, answer, example.TranslatedSentence);
        }
        return null;
    }

    public static SentenceCloze? ForReview(VocabularyEntry entry)
    {
        var examples = entry.Senses.Select(s => (Example: s.Example, Expression: (string?)null))
            .Concat(entry.Collocations.Select(c => (Example: c.Example, Expression: (string?)c.Phrase)))
            .Concat(entry.Examples.Select(e => (Example: (BilingualExample?)e, Expression: (string?)null)))
            .Where(e => e.Example is not null).ToArray();
        if (!string.IsNullOrWhiteSpace(entry.ContextSentence))
        {
            var known = examples.FirstOrDefault(e => e.Example!.SourceSentence == entry.ContextSentence);
            var original = known.Example ?? new BilingualExample(entry.ContextSentence, "");
            if (FromExample(original, entry.Headword, entry.Inflections, known.Expression) is { } cloze) return cloze;
        }
        return examples.Select(e => FromExample(e.Example!, entry.Headword, entry.Inflections, e.Expression))
            .FirstOrDefault(c => c is not null);
    }
}
