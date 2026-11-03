namespace AiTranslate.Core;

public sealed record MissingUsageExample(string Id, string Expression, string Meaning, string Usage);

public static class DictionaryUsageExamples
{
    public static BilingualExample? GetSenseExample(this DictionaryEntryResult entry, PartOfSpeechSense sense)
    {
        if (sense.Example is { } paired && IsBilingual(paired)) return paired;
        if (string.IsNullOrWhiteSpace(sense.DefinitionZh) || entry.Senses.Count(s => s.DefinitionZh == sense.DefinitionZh) != 1)
            return null;
        return entry.Examples.FirstOrDefault(e => IsBilingual(e) && e.SenseHint?.Trim() == sense.DefinitionZh.Trim());
    }

    // Only count observable gaps in material the entry actually lists.
    public static IEnumerable<MissingUsageExample> MissingUsageExamples(this DictionaryEntryResult entry)
    {
        if (entry.ItemType.IsPassage()) yield break;
        for (var i = 0; i < entry.Senses.Count; i++)
        {
            var sense = entry.Senses[i];
            if (!string.IsNullOrWhiteSpace(sense.GrammarPattern) && entry.GetSenseExample(sense) is null)
                yield return new($"sense-{i}", entry.SourceText, sense.DefinitionZh, $"{sense.PartOfSpeech} {sense.GrammarPattern}");
        }
        for (var i = 0; i < entry.Collocations.Count; i++)
        {
            var pair = entry.Collocations[i];
            if (entry.GetCollocationExample(pair) is null)
                yield return new($"phrase-{i}", pair.Phrase, pair.Meaning, pair.SenseHint);
        }
    }

    public static BilingualExample? GetCollocationExample(this DictionaryEntryResult entry, CollocationItem collocation)
    {
        if (collocation.Example is { } paired && IsBilingual(paired)) return paired;

        // Older entries keep all examples at the headword. Only reuse a sentence if its text
        // actually contains this expression. A shared headword or topic is not sufficient.
        var phrase = collocation.Phrase.Trim();
        if (phrase.Length == 0 || entry.Examples.Count == 0) return null;
        var ambiguous = entry.Collocations.Count(c => string.Equals(c.Phrase, phrase, StringComparison.OrdinalIgnoreCase)) > 1;
        foreach (var example in entry.Examples)
        {
            if (!IsBilingual(example)) continue;
            if (ambiguous && (string.IsNullOrWhiteSpace(collocation.SenseHint) || collocation.SenseHint != example.SenseHint)) continue;
            var index = DictionaryTextMatch.IndexOf(example.SourceSentence, phrase);
            if (index >= 0) return example with { HighlightTerm = example.SourceSentence.Substring(index, phrase.Length) };
        }
        return null;
    }

    private static bool IsBilingual(BilingualExample example) =>
        !string.IsNullOrWhiteSpace(example.SourceSentence) && !string.IsNullOrWhiteSpace(example.TranslatedSentence);
}
