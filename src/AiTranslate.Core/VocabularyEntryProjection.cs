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
        CacheStamp = entry.DictionaryCacheKey is { } key ? new(key, entry.DictionaryRevision) : null,
        ReaderLanguage = entry.ReaderLanguage,
        Processing = entry.DictionaryProcessing,
        ContextMeaning = entry.DictionaryContextMeaning,
        SuggestedTags = entry.SuggestedTags,
        GlossaryReferences = entry.GlossaryReferences,
        SourceReadingNote = entry.SourceReadingNote,
        GlossaryUsageNote = entry.GlossaryUsageNote,
        PronunciationTip = entry.PronunciationTip,
        PronunciationDrills = entry.PronunciationDrills,
        UsageGuidance = entry.UsageGuidance,
        EtymologyNotes = entry.EtymologyNotes,
        Mnemonics = entry.Mnemonics,
        CulturalNotes = entry.CulturalNotes,
        ToneEvidence = entry.ToneEvidence.Where(e => DictionaryTextMatch.IndexOf(entry.Headword, e.Quote,
            comparison: StringComparison.Ordinal) >= 0).ToArray(),
        PrimaryTranslation = entry.Translation,
        LiteralTranslation = entry.LiteralTranslation,
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
            .Where(a => DictionaryTextMatch.IndexOf(entry.Headword, a.Source, comparison: StringComparison.Ordinal) >= 0 &&
                DictionaryTextMatch.IndexOf(entry.Translation, a.Target, comparison: StringComparison.Ordinal) >= 0).ToArray(),
        TranslationOptions = entry.TranslationOptions,
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
