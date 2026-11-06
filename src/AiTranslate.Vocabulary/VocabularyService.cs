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

    public IReadOnlyList<string> GetAllTags() => _database.Read(db =>
        db.Rows("SELECT value FROM cards, json_each(cards.payload,'$.Tags')", r => r.GetString(0))
            .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray());

    public VocabularyEntry? FindByHeadword(string headword)
    {
        if (string.IsNullOrWhiteSpace(headword)) return null;
        var word = headword.Trim();
        return _database.Read(db => db.ByHead(word).FirstOrDefault(e => string.Equals(e.Headword, word, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyList<VocabularyEntry> QueryEntries(VocabularyQueryFilter filter, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var currentTime = now ?? DateTimeOffset.UtcNow;
        return _database.Read(db =>
        {
            var conditions = new List<string>();
            var parameters = new List<object?>();
            void Filter(string comparison, object value)
            {
                conditions.Add(comparison + "$p" + parameters.Count);
                parameters.Add(value);
            }
            if (!string.IsNullOrWhiteSpace(filter.NotebookId) && filter.NotebookId != "all")
                Filter("book = ", FindNotebookId(db, filter.NotebookId) ?? filter.NotebookId);
            if (filter.ItemType is { } kind) Filter("kind = ", (int)kind);
            if (filter.Stage is { } stage) Filter("stage = ", (int)stage);
            if (filter.OnlyStarred) conditions.Add("starred = 1");
            if (filter.OnlyDue)
            {
                Filter("stage <> ", (int)MemoryStage.Mastered);
                Filter("due <= ", currentTime.UtcTicks);
            }
            var clause = conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions);
            IEnumerable<VocabularyEntry> query = db.Cards(clause + " ORDER BY rowid DESC", parameters.ToArray());
            if (!string.IsNullOrWhiteSpace(filter.Tag))
                query = query.Where(e => e.Tags.Any(t => string.Equals(t, filter.Tag, StringComparison.OrdinalIgnoreCase)));

            if (!string.IsNullOrWhiteSpace(filter.SearchKeyword))
            {
                var kw = filter.SearchKeyword.Trim();
                query = query.Where(e =>
                    e.Headword.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    e.Translation.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    e.ContextSentence.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    e.MemoryMnemonic.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    e.EtymologyAndRoot.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    e.EtymologyNotes.Concat(e.CulturalNotes).Any(n => n.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        n.Explanation.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                    e.Mnemonics.Any(m => m.Method.Contains(kw, StringComparison.OrdinalIgnoreCase) || m.Cue.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                        m.Explanation.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                    e.UserPersonalNote.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    e.Tags.Any(t => t.Contains(kw, StringComparison.OrdinalIgnoreCase)));
            }

            query = filter.SortOrder switch
            {
                VocabularySortOrder.NextReviewDue => query.OrderBy(e => e.NextReviewAt).ThenByDescending(e => e.Difficulty),
                VocabularySortOrder.Alphabetical => query.OrderBy(e => e.Headword, StringComparer.OrdinalIgnoreCase),
                VocabularySortOrder.MostLookedUp => query.OrderByDescending(e => e.LookupCount).ThenByDescending(e => e.UpdatedAt),
                VocabularySortOrder.HighestDifficulty => query.OrderByDescending(e => e.Difficulty).ThenBy(e => e.StabilityDays),
                VocabularySortOrder.LowestMastery => query.OrderBy(e => e.MasteryLevel).ThenBy(e => e.StabilityDays),
                // RecentlyAdded must reflect creation time, not last-updated time, otherwise reviewing an
                // old entry would incorrectly bump it to the top of a "recently added" list.
                _ => query.OrderByDescending(e => e.CreatedAt)
            };

            return query.ToArray();
        });
    }

    public IReadOnlyList<VocabularyEntry> GetDueQueue(string? notebookId = null, int maxCount = 50, DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var all = string.IsNullOrWhiteSpace(notebookId) || notebookId == "all";
        var bookFilter = all ? "" : " AND book=$p4";
        var clause = "WHERE stage<>$p0 AND due<=$p1" + bookFilter +
            " ORDER BY (stage=$p2) DESC, due, starred DESC, rowid DESC LIMIT $p3";
        object?[] values = [(int)MemoryStage.Mastered, currentTime.UtcTicks, (int)MemoryStage.Learning, Math.Max(1, maxCount)];
        return _database.Read(db => db.Cards(clause, all ? values : [.. values, FindNotebookId(db, notebookId) ?? notebookId]));
    }

    public VocabularyDashboardStats GetDashboardStats(DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        // "Today" must be the user's local calendar day, not the UTC day: computing the boundary
        // from the UTC date would misclassify reviews done late at night (or early morning, for
        // timezones ahead of UTC) as belonging to the wrong day.
        var localNow = currentTime.ToLocalTime();
        var todayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
        return _database.Read(db =>
        {
            // Statistics need scheduling fields only, even when a saved passage has a large reading payload.
            var reviewedToday = (int)db.Number("SELECT COUNT(*) FROM review_logs WHERE reviewed >= $p0", todayStart.UtcTicks);
            var byNotebook = db.Books().ToDictionary(n => n.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
            var byType = Enum.GetValues<VocabularyItemType>().ToDictionary(t => t, _ => 0);
            var byStage = Enum.GetValues<MemoryStage>().ToDictionary(stage => stage, _ => 0);
            var due = 0;
            var starred = 0;
            var active = 0;
            double retention = 0;
            var rows = db.Rows("SELECT book,kind,stage,due,starred,stability,last_review FROM cards", r => (
                Book: r.GetString(0), Kind: (VocabularyItemType)r.GetInt32(1), Stage: (MemoryStage)r.GetInt32(2),
                Due: r.GetInt64(3), Starred: r.GetBoolean(4), Stability: r.GetDouble(5),
                Reviewed: r.IsDBNull(6) ? (DateTimeOffset?)null : new DateTimeOffset(r.GetInt64(6), TimeSpan.Zero)));
            foreach (var row in rows)
            {
                byNotebook[row.Book]++;
                byType[row.Kind] = byType.GetValueOrDefault(row.Kind) + 1;
                byStage[row.Stage] = byStage.GetValueOrDefault(row.Stage) + 1;
                if (row.Due <= currentTime.UtcTicks && row.Stage != MemoryStage.Mastered) due++;
                if (row.Starred) starred++;
                if (row.Stage == MemoryStage.New) continue;
                active++;
                retention += SpacedRepetitionScheduler.EstimateRetrievability(row.Stage, row.Reviewed, row.Stability, currentTime);
            }
            return new VocabularyDashboardStats(rows.Count, due, byStage[MemoryStage.New], byStage[MemoryStage.Learning],
                byStage[MemoryStage.Reviewing], byStage[MemoryStage.Mastered], starred, reviewedToday,
                active == 0 ? 100.0 : Math.Round(retention / active * 100.0, 1), byNotebook, byType);
        });
    }

    public VocabularyNotebook CreateNotebook(string name, string description, string iconName = "book-open-variant", string accentHex = "#3B82F6")
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
                Description = description.Trim(),
                IconName = iconName,
                AccentColorHex = accentHex,
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
        string sourceTitle = "",
        string userNote = "")
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
                var mergedTags = current.Tags.Concat(result.SuggestedTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
                    UserPersonalNote = !string.IsNullOrWhiteSpace(userNote) ? userNote.Trim() : current.UserPersonalNote,
                    Tags = mergedTags,
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
                    UserPersonalNote = userNote.Trim(),
                    ContextSentence = contextSentence?.Trim() ?? (result.Examples.FirstOrDefault()?.SourceSentence ?? ""),
                    ContextTranslation = result.ContextMeaning ?? (result.Examples.FirstOrDefault()?.TranslatedSentence ?? ""),
                    CaptureSource = source,
                    SourceTitle = sourceTitle,
                    Tags = result.SuggestedTags,
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

    public void ToggleStar(string entryId)
    {
        var changed = _database.Write(db =>
        {
            if (db.Card(entryId) is not { } card) return false;
            db.SaveCard(card with { IsStarred = !card.IsStarred, UpdatedAt = DateTimeOffset.UtcNow });
            return true;
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
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
