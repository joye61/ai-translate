using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

public sealed partial class VocabularyService
{
    private static bool MatchesDictionary(VocabularyEntry entry, string headword, string target, string context) =>
        entry.Headword == headword && entry.TargetLanguage == target && entry.GetDictionaryContext() == context;

    public bool IsCollected(DictionaryEntryResult result, string notebookId)
    {
        lock (_gate)
            return _store.Entries.Any(e => e.NotebookId == notebookId &&
                MatchesDictionary(e, result.SourceText.Trim(), result.TargetLanguage, result.LookupContext));
    }

    public void RemoveFromNotebook(DictionaryEntryResult result, string notebookId)
    {
        lock (_gate)
        {
            var entries = _store.Entries.Where(e => e.NotebookId != notebookId ||
                !MatchesDictionary(e, result.SourceText.Trim(), result.TargetLanguage, result.LookupContext)).ToList();
            if (entries.Count == _store.Entries.Count) return;
            CommitOrganization(_store.Notebooks, entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshCollectedEntries(DictionaryEntryResult result, long revision)
    {
        lock (_gate)
        {
            List<VocabularyEntry>? updated = null;
            for (var i = 0; i < _store.Entries.Count; i++)
            {
                var card = _store.Entries[i];
                // A cache hit can repair an interrupted notebook write. Legacy or manually created
                // cards without a revision adopt AI content only on a fresh request.
                if (card.DictionaryRevision >= revision || (result.Origin == DictionaryResultOrigin.Cache && card.DictionaryRevision == 0) ||
                    !MatchesDictionary(card, result.SourceText.Trim(), result.TargetLanguage, result.LookupContext)) continue;
                updated ??= new(_store.Entries);
                updated[i] = ApplyDictionary(card, result, revision) with
                {
                    DictionaryContext = result.LookupContext, UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            if (updated is null) return;
            CommitOrganization(_store.Notebooks, updated);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string GetGrammarNotes(DictionaryEntryResult result) =>
        string.Join("\n", new[] { result.GrammarBreakdown, result.UsageNotes }.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());

    // An old offline result may be collected again after a card has been enriched.
    // Preserve details only when the older response identifies the same sense unambiguously.
    private static IReadOnlyList<PartOfSpeechSense> MergeRecapturedSenses(IReadOnlyList<PartOfSpeechSense> incoming, IReadOnlyList<PartOfSpeechSense> saved)
    {
        if (incoming.Count == 0) return saved;
        return incoming.Select(s =>
        {
            if (s.UsageCases.Count != 0 && !string.IsNullOrWhiteSpace(s.GrammarLabel)) return s;
            var matches = saved.Where(old => old.DefinitionZh == s.DefinitionZh &&
                LexicalPart.From(old.PartOfSpeech) == LexicalPart.From(s.PartOfSpeech)).Take(2).ToArray();
            if (matches.Length != 1) return s;
            return s with
            {
                UsageCases = s.UsageCases.Count == 0 ? matches[0].UsageCases : s.UsageCases,
                GrammarLabel = string.IsNullOrWhiteSpace(s.GrammarLabel) ? matches[0].GrammarLabel : s.GrammarLabel
            };
        }).ToArray();
    }

    private static IReadOnlyList<RelatedForm> MergeRecapturedFamily(IReadOnlyList<RelatedForm> incoming, IReadOnlyList<RelatedForm> saved)
    {
        if (incoming.Count == 0) return saved;
        return incoming.SelectMany(f =>
        {
            if (!string.IsNullOrEmpty(f.Relation)) return new[] { f };
            var matches = saved.Where(old => old.Value == f.Value && !string.IsNullOrEmpty(old.Relation) &&
                (old.Label == f.Label || LexicalPart.From(old.PartOfSpeech).Name == LexicalPart.From(f.Label).Name)).ToArray();
            return matches.Length > 0 ? matches : [f];
        }).Distinct().ToArray();
    }

    private static VocabularyEntry ApplyDictionary(VocabularyEntry card, DictionaryEntryResult result, long revision) => card with
    {
        Translation = result.PrimaryTranslation,
        DictionaryRevision = revision,
        ItemType = result.ItemType,
        SourceLanguage = result.DetectedSourceLanguage,
        TargetLanguage = result.TargetLanguage,
        UkPhonetic = result.UkPhonetic ?? "",
        UsPhonetic = result.UsPhonetic ?? "",
        Senses = result.Senses,
        Pronunciation = result.Pronunciation,
        CommonMistakes = result.CommonMistakes,
        WordChoice = result.WordChoice,
        UsageContrasts = result.UsageContrasts,
        Variants = result.Variants,
        KeyPoints = result.KeyPoints,
        Alignment = result.Alignment,
        Clauses = result.Clauses,
        Tone = result.Tone,
        LiteralMeaning = result.LiteralMeaning,
        CulturalNote = result.CulturalNote,
        Domains = result.Domains,
        Morphemes = result.Morphemes,
        SynonymNuances = result.SynonymNuances,
        Mistakes = result.Mistakes,
        Inflections = result.Inflections,
        WordFamily = result.WordFamily,
        Collocations = result.Collocations,
        Examples = result.Examples,
        Synonyms = result.Synonyms,
        Antonyms = result.Antonyms,
        EtymologyAndRoot = result.EtymologyAndRoot ?? "",
        MemoryMnemonic = result.MemoryMnemonic ?? "",
        GrammarOrUsageNote = GetGrammarNotes(result),
        GrammarHighlights = result.GrammarHighlights,
        TranslationDecisions = result.TranslationDecisions
    };
}
