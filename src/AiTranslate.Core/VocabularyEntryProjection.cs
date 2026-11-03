namespace AiTranslate.Core;

public static class VocabularyEntryProjection
{
    /// <summary>
    /// Project a saved entry back to a dictionary result so the notebook renders with the same blocks as a live lookup.
    /// Anchored notes whose quotes no longer match the saved text are dropped rather than shown stale.
    /// </summary>
    public static DictionaryEntryResult ToDictionaryResult(this VocabularyEntry entry) => new()
    {
        SourceText = entry.Headword,
        LookupContext = entry.GetDictionaryContext(),
        PrimaryTranslation = entry.Translation,
        Origin = DictionaryResultOrigin.Ai,
        ItemType = entry.ItemType,
        DetectedSourceLanguage = entry.SourceLanguage,
        TargetLanguage = entry.TargetLanguage,
        UkPhonetic = entry.UkPhonetic,
        UsPhonetic = entry.UsPhonetic,
        Pronunciation = entry.Pronunciation,
        Senses = entry.Senses,
        Inflections = entry.Inflections,
        WordFamily = entry.WordFamily,
        Collocations = entry.Collocations,
        Examples = entry.Examples,
        Synonyms = entry.Synonyms,
        Antonyms = entry.Antonyms,
        EtymologyAndRoot = entry.EtymologyAndRoot,
        MemoryMnemonic = entry.MemoryMnemonic,
        UsageNotes = entry.GrammarOrUsageNote,
        CommonMistakes = entry.CommonMistakes,
        WordChoice = entry.WordChoice,
        UsageContrasts = entry.UsageContrasts,
        GrammarHighlights = entry.GrammarHighlights
            .Where(h => DictionaryTextMatch.IndexOf(entry.Headword, h.Quote, comparison: StringComparison.Ordinal) >= 0).ToArray(),
        TranslationDecisions = entry.TranslationDecisions.Where(d => d.IsAnchoredIn(entry.Headword, entry.Translation)).ToArray(),
        Clauses = entry.Clauses
            .Where(c => DictionaryTextMatch.IndexOf(entry.Headword, c.Quote, comparison: StringComparison.Ordinal) >= 0).ToArray(),
        Alignment = entry.Alignment
            .Where(a => DictionaryTextMatch.IndexOf(entry.Headword, a.Source, comparison: StringComparison.Ordinal) >= 0).ToArray(),
        Variants = entry.Variants,
        KeyPoints = entry.KeyPoints,
        Tone = entry.Tone,
        LiteralMeaning = entry.LiteralMeaning,
        CulturalNote = entry.CulturalNote,
        Domains = entry.Domains,
        Morphemes = entry.Morphemes,
        SynonymNuances = entry.SynonymNuances,
        Mistakes = entry.Mistakes
    };
}
