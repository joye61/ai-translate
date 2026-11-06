using System.Text.Json;
using System.Text.Json.Serialization;
using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

internal sealed class VocabularyStoreSnapshot
{
    [JsonRequired]
    public List<VocabularyNotebook> Notebooks { get; set; } = [];
    [JsonRequired]
    public List<VocabularyEntry> Entries { get; set; } = [];
    public List<ReviewLogRecord> ReviewLogs { get; set; } = [];
}

/// <summary>
/// Thread-safe, atomic local persistent store for Vocabulary Notebooks, Entries, and FSRS Review Logs.
/// </summary>
public sealed partial class VocabularyService : IVocabularyService
{
    private readonly VocabularyDatabase _database;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public event EventHandler? Changed;

    public VocabularyService(string? storagePath = null) => _database = new(storagePath);

    public IReadOnlyList<VocabularyNotebook> GetNotebooks() =>
        _database.Read(db => db.Books().OrderByDescending(n => n.IsSystemDefault).ThenBy(n => n.CreatedAt).ToArray());

    // Look only at saved terminology matching this text and language. Notes, screenshots
    // and unrelated notebook entries must never be sent as model context.
    public IReadOnlyList<CollocationItem> FindGlossaryTerms(string text, string targetLanguage)
    {
        return _database.Read(db => db.Rows("""
                SELECT glossary_term,glossary_meaning FROM cards
                WHERE target=$p0 AND glossary_term IS NOT NULL AND instr($p1,glossary_term)>0 ORDER BY rowid DESC
                """, row => new CollocationItem(row.GetString(0), row.GetString(1)), targetLanguage, text)
            .Where(t => t.Phrase != text &&
                DictionaryTextMatch.IndexOf(text, t.Phrase, comparison: StringComparison.Ordinal) >= 0)
            .OrderByDescending(t => t.Phrase.Length).ThenBy(t => t.Phrase, StringComparer.Ordinal).Distinct().ToArray());
    }

    public VocabularyEntry? FindByHeadword(string headword)
    {
        if (string.IsNullOrWhiteSpace(headword)) return null;
        var word = headword.Trim();
        return _database.Read(db => db.ByHead(word).FirstOrDefault(e => string.Equals(e.Headword, word, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<VocabularyEntry> QueryEntries(VocabularyQueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return _database.Read(db =>
        {
            var hasNotebook = !string.IsNullOrWhiteSpace(filter.NotebookId);
            IEnumerable<VocabularyEntry> query = hasNotebook
                ? db.Cards("WHERE book = $p0 ORDER BY rowid DESC", FindNotebookId(db, filter.NotebookId) ?? filter.NotebookId!)
                : db.Cards("ORDER BY rowid DESC");
            query = filter.SortOrder switch
            {
                VocabularySortOrder.ReviewDue => query.OrderBy(e => e.NextReviewAt).ThenByDescending(e => e.Difficulty),
                VocabularySortOrder.Alphabetical => query.OrderBy(e => e.Headword, StringComparer.OrdinalIgnoreCase),
                // RecentlyAdded must reflect creation time, not last-updated time, otherwise reviewing an
                // old entry would incorrectly bump it to the top of a "recently added" list.
                _ => query.OrderByDescending(e => e.CreatedAt)
            };
            return query.ToArray();
        });
    }

    public IReadOnlyList<VocabularyEntry> GetDueQueue(string notebookId, int maxCount = 50, DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        const string clause = "WHERE book=$p4 AND stage<>$p0 AND due<=$p1 ORDER BY (stage=$p2) DESC, due, rowid DESC LIMIT $p3";
        return _database.Read(db => db.Cards(clause, (int)MemoryStage.Mastered, currentTime.UtcTicks, (int)MemoryStage.Learning,
            Math.Max(1, maxCount), FindNotebookId(db, notebookId) ?? notebookId));
    }

    public IReadOnlyList<VocabularyNotebookSummary> GetNotebookSummaries(DateTimeOffset? now = null)
    {
        var currentTime = (now ?? DateTimeOffset.UtcNow).UtcTicks;
        return _database.Read(db =>
        {
            // Scheduling columns only: a saved passage can carry a large payload.
            var counts = db.Rows("SELECT book,COUNT(*),SUM(stage<>$p0 AND due<=$p1) FROM cards GROUP BY book",
                r => (Book: r.GetString(0), Count: r.GetInt32(1), Due: r.IsDBNull(2) ? 0 : r.GetInt32(2)),
                (int)MemoryStage.Mastered, currentTime).ToDictionary(r => r.Book, StringComparer.OrdinalIgnoreCase);
            return db.Books().OrderByDescending(n => n.IsSystemDefault).ThenBy(n => n.CreatedAt)
                .Select(n => counts.TryGetValue(n.Id, out var c) ? new VocabularyNotebookSummary(n, c.Count, c.Due)
                    : new VocabularyNotebookSummary(n, 0, 0)).ToArray();
        });
    }

    public VocabularyNotebook CreateNotebook(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        var inserted = false;
        var saved = _database.Write(db =>
        {
            var existing = db.Books().FirstOrDefault(n => string.Equals(n.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            var created = new VocabularyNotebook
            {
                Id = $"nb_{Guid.NewGuid():N}"[..12],
                Name = trimmed,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.InsertBook(created);
            inserted = true;
            return created;
        });
        if (inserted) Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public VocabularyEntry AddOrUpdateFromDictionary(
        DictionaryEntryResult result,
        string notebookId = "default",
        string? contextSentence = null,
        TranslationTriggerSource source = TranslationTriggerSource.ManualInput,
        string sourceTitle = "")
    {
        ArgumentNullException.ThrowIfNull(result);
        var now = DateTimeOffset.UtcNow;
        var headword = result.SourceText.Trim();
        var grammarNotes = GetGrammarNotes(result);
        var changed = false;
        var collected = _database.Write(db =>
        {
            notebookId = FindNotebookId(db, notebookId) ?? "default";
            var current = db.ByHead(headword).FirstOrDefault(e => e.NotebookId == notebookId &&
                MatchesDictionary(e, headword, result.TargetLanguage, result.LookupContext));
            VocabularyEntry saved;
            if (current is not null)
            {
                if (result.Processing?.Intent == DictionaryLookupIntent.Quick &&
                    current.DictionaryProcessing?.Intent == DictionaryLookupIntent.Learning) return current;
                if (result.CacheStamp is { } stamp && stamp.Revision < current.DictionaryRevision)
                    return current;
                var translation = string.IsNullOrWhiteSpace(result.PrimaryTranslation) ? current.Translation : result.PrimaryTranslation;
                saved = current with
                {
                    DictionaryContext = result.LookupContext,
                    DictionaryRevision = result.CacheStamp?.Revision ?? current.DictionaryRevision,
                    DictionaryProcessing = result.Processing ?? current.DictionaryProcessing,
                    DictionaryCacheKey = result.CacheStamp?.Key ?? current.DictionaryCacheKey,
                    ReaderLanguage = result.ReaderLanguage ?? current.ReaderLanguage,
                    DictionaryContextMeaning = result.ContextMeaning ?? current.DictionaryContextMeaning,
                    SuggestedTags = result.SuggestedTags,
                    GlossaryReferences = result.GlossaryReferences,
                    SourceReadingNote = result.SourceReadingNote,
                    GlossaryUsageNote = result.GlossaryUsageNote,
                    PronunciationTip = result.PronunciationTip,
                    PronunciationDrills = result.PronunciationDrills.Count > 0 ? result.PronunciationDrills : current.PronunciationDrills,
                    UsageGuidance = result.UsageGuidance.Count > 0 ? result.UsageGuidance : current.UsageGuidance,
                    EtymologyNotes = result.EtymologyNotes.Count > 0 ? result.EtymologyNotes : current.EtymologyNotes,
                    Mnemonics = result.Mnemonics.Count > 0 ? result.Mnemonics : current.Mnemonics,
                    CulturalNotes = result.CulturalNotes.Count > 0 ? result.CulturalNotes : current.CulturalNotes,
                    ToneEvidence = result.ToneEvidence.Count > 0 ? result.ToneEvidence : current.ToneEvidence,
                    Translation = translation,
                    LiteralTranslation = result.LiteralTranslation,
                    TranslationDecisions = result.TranslationDecisions.Count == 0 && current.Translation == translation &&
                        current.TargetLanguage == result.TargetLanguage ? current.TranslationDecisions : result.TranslationDecisions,
                    UkPhonetic = string.IsNullOrWhiteSpace(result.UkPhonetic) ? current.UkPhonetic : result.UkPhonetic!,
                    UsPhonetic = string.IsNullOrWhiteSpace(result.UsPhonetic) ? current.UsPhonetic : result.UsPhonetic!,
                    Senses = MergeRecapturedSenses(result.Senses, current.Senses),
                    Pronunciation = string.IsNullOrWhiteSpace(result.Pronunciation) ? current.Pronunciation : result.Pronunciation,
                    CommonMistakes = string.IsNullOrWhiteSpace(result.CommonMistakes) ? current.CommonMistakes : result.CommonMistakes,
                    WordChoice = string.IsNullOrWhiteSpace(result.WordChoice) ? current.WordChoice : result.WordChoice,
                    UsageContrasts = MergeRecapturedContrasts(result.UsageContrasts, current.UsageContrasts),
                    TranslationOptions = result.TranslationOptions,
                    Variants = result.Variants.Count > 0 ? result.Variants : current.Variants,
                    KeyPoints = result.KeyPoints.Count > 0 ? result.KeyPoints : current.KeyPoints,
                    Alignment = result.Alignment.Count > 0 ? result.Alignment : current.Alignment,
                    Clauses = result.Clauses.Count > 0 ? result.Clauses : current.Clauses,
                    Tone = result.Tone ?? current.Tone,
                    LiteralMeaning = string.IsNullOrWhiteSpace(result.LiteralMeaning) ? current.LiteralMeaning : result.LiteralMeaning,
                    CulturalNote = string.IsNullOrWhiteSpace(result.CulturalNote) ? current.CulturalNote : result.CulturalNote,
                    Domains = result.Domains.Count > 0 ? result.Domains : current.Domains,
                    Morphemes = result.Morphemes.Count > 0 ? result.Morphemes : current.Morphemes,
                    SynonymNuances = result.SynonymNuances.Count > 0 ? result.SynonymNuances : current.SynonymNuances,
                    Mistakes = result.Mistakes.Count > 0 ? result.Mistakes : current.Mistakes,
                    Inflections = result.Inflections.Count > 0 ? result.Inflections :
                        current.Inflections.Where(f => !result.WordFamily.Any(r => r.Value == f.Value && r.Label == f.Label)).ToArray(),
                    WordFamily = MergeRecapturedFamily(result.WordFamily, current.WordFamily),
                    Collocations = result.Collocations.Count > 0 ? result.Collocations : current.Collocations,
                    Examples = result.Examples.Count > 0 ? result.Examples : current.Examples,
                    Synonyms = result.Synonyms.Count > 0 ? result.Synonyms : current.Synonyms,
                    Antonyms = result.Antonyms.Count > 0 ? result.Antonyms : current.Antonyms,
                    EtymologyAndRoot = !string.IsNullOrWhiteSpace(result.EtymologyAndRoot) ? result.EtymologyAndRoot! : current.EtymologyAndRoot,
                    MemoryMnemonic = !string.IsNullOrWhiteSpace(result.MemoryMnemonic) ? result.MemoryMnemonic! : current.MemoryMnemonic,
                    GrammarOrUsageNote = grammarNotes.Length > 0 ? grammarNotes : current.GrammarOrUsageNote,
                    GrammarHighlights = result.GrammarHighlights.Count > 0 ? result.GrammarHighlights : current.GrammarHighlights,
                    ContextSentence = !string.IsNullOrWhiteSpace(contextSentence) ? contextSentence!.Trim() : current.ContextSentence,
                    ContextTranslation = !string.IsNullOrWhiteSpace(result.ContextMeaning) ? result.ContextMeaning! : current.ContextTranslation,
                    LookupCount = current.LookupCount + 1,
                    UpdatedAt = now
                };
                if (result.Processing is { Review: DictionaryStageState.Completed } quality &&
                    (quality.Issues.Count > 0 || result.PrimaryTranslation != current.Translation) ||
                    result.Processing?.Extension == DictionaryStageState.Invalidated)
                    saved = ApplyDictionary(saved, result, result.CacheStamp?.Revision ?? current.DictionaryRevision);
            }
            else
            {
                saved = ApplyDictionary(new VocabularyEntry
                {
                    Id = $"voc_{Guid.NewGuid():N}"[..16],
                    Headword = headword,
                    Translation = result.PrimaryTranslation,
                    NotebookId = notebookId,
                    DictionaryContext = result.LookupContext,
                    ContextSentence = contextSentence?.Trim() ?? (result.Examples.FirstOrDefault()?.SourceSentence ?? ""),
                    ContextTranslation = result.ContextMeaning ?? (result.Examples.FirstOrDefault()?.TranslatedSentence ?? ""),
                    CaptureSource = source,
                    SourceTitle = sourceTitle,
                    LookupCount = 1,
                    Stage = MemoryStage.New,
                    MasteryLevel = 0,
                    StabilityDays = 0,
                    Difficulty = 5.0,
                    CreatedAt = now,
                    UpdatedAt = now,
                    NextReviewAt = now
                }, result, result.CacheStamp?.Revision ?? 0);
            }
            db.SaveCard(saved);
            changed = true;
            return saved;
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return collected;
    }

    public VocabularyEntry SaveEntry(VocabularyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var updated = _database.Write(db =>
        {
            var card = entry with { UpdatedAt = DateTimeOffset.UtcNow, NotebookId = FindNotebookId(db, entry.NotebookId) ?? "default" };
            db.SaveCard(card);
            return card;
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public VocabularyEntry MoveEntry(string entryId, string notebookId)
    {
        var (result, changed) = _database.Write(db =>
        {
            var target = FindNotebookId(db, notebookId) ?? throw new InvalidOperationException("目标生词本已被删除，请重新选择。");
            var entry = db.Card(entryId) ?? throw new InvalidOperationException("这个词条已被删除。");
            if (entry.NotebookId == target) return (entry, false);
            var twin = db.ByHead(entry.Headword).FirstOrDefault(e => e.Id != entry.Id && e.NotebookId == target &&
                MatchesDictionary(e, entry.Headword, entry.TargetLanguage, entry.GetDictionaryContext()));
            if (twin is not null)
            {
                db.Execute("DELETE FROM cards WHERE id=$p0", entry.Id);
                return (twin, true);
            }
            var moved = entry with { NotebookId = target, UpdatedAt = DateTimeOffset.UtcNow };
            db.SaveCard(moved);
            return (moved, true);
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void DeleteEntry(string entryId)
    {
        if (_database.Write(db => db.Execute("DELETE FROM cards WHERE id=$p0", entryId)) > 0)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public VocabularyEntry SubmitReview(string entryId, ReviewRating rating, int durationMs = 1500, DateTimeOffset? now = null)
    {
        var reviewTime = now ?? DateTimeOffset.UtcNow;
        var updated = _database.Write(db =>
        {
            var before = db.Card(entryId) ?? throw new InvalidOperationException("找不到对应的生词卡片。");
            var after = SpacedRepetitionScheduler.ApplyReview(before, rating, reviewTime);
            db.SaveCard(after);
            db.AddReview(new ReviewLogRecord($"rev_{Guid.NewGuid():N}"[..16], entryId, rating, before.Stage,
                after.Stage, before.StabilityDays, after.StabilityDays, durationMs, reviewTime));
            return after;
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public string ExportToMarkdown(VocabularyQueryFilter? filter = null) =>
        VocabularyExportFormatter.ToMarkdown(QueryEntries(filter ?? new VocabularyQueryFilter()));

    public string ExportToAnkiTsv(VocabularyQueryFilter? filter = null) =>
        VocabularyExportFormatter.ToAnkiTsv(QueryEntries(filter ?? new VocabularyQueryFilter()));

    public string ExportToCsv(VocabularyQueryFilter? filter = null) =>
        VocabularyExportFormatter.ToCsv(QueryEntries(filter ?? new VocabularyQueryFilter()));

    public string ExportToJson(VocabularyQueryFilter? filter = null) =>
        VocabularyExportFormatter.ToJson(QueryEntries(filter ?? new VocabularyQueryFilter()));

    public int ImportFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        var imported = JsonSerializer.Deserialize<List<VocabularyEntry>>(json, JsonOptions);
        if (imported is null || imported.Count == 0) return 0;
        var count = _database.Write(db =>
        {
            var books = db.Books().ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var source in imported)
            {
                if (string.IsNullOrWhiteSpace(source.Headword)) continue;
                var entry = source with { NotebookId = books.TryGetValue(source.NotebookId, out var book) ? book.Id : "default" };
                var match = db.ByHead(entry.Headword).FirstOrDefault(e => e.NotebookId == entry.NotebookId &&
                    MatchesDictionary(e, entry.Headword, entry.TargetLanguage, entry.GetDictionaryContext()));
                if (match is not null) entry = entry with { Id = match.Id };
                else if (db.Number("SELECT COUNT(*) FROM cards WHERE id=$p0", entry.Id) != 0)
                    entry = entry with { Id = $"voc_{Guid.NewGuid():N}"[..16] };
                db.SaveCard(entry);
                added++;
            }
            return added;
        });
        if (count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }
}
