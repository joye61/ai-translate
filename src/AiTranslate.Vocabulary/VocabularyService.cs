using System.Text;
using System.Text.Json;
using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

internal sealed class VocabularyStoreSnapshot
{
    public List<VocabularyNotebook> Notebooks { get; set; } = [];
    public List<VocabularyEntry> Entries { get; set; } = [];
    public List<ReviewLogRecord> ReviewLogs { get; set; } = [];
}

/// <summary>
/// Thread-safe, atomic local persistent store for Vocabulary Notebooks, Entries, and FSRS Review Logs.
/// </summary>
public sealed partial class VocabularyService : IVocabularyService
{
    private readonly string _storagePath;
    private readonly object _gate = new();
    private VocabularyStoreSnapshot _store;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public event EventHandler? Changed;

    public VocabularyService(string? storagePath = null)
    {
        _storagePath = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AiTranslate",
            "vocabulary-store.json");
        _store = LoadOrInitialize();
    }

    public IReadOnlyList<VocabularyNotebook> GetNotebooks()
    {
        lock (_gate)
        {
            return _store.Notebooks.OrderByDescending(n => n.IsSystemDefault).ThenBy(n => n.CreatedAt).ToArray();
        }
    }

    // Look only at saved terminology matching this text and language. Notes, screenshots
    // and unrelated notebook entries must never be sent as model context.
    public IReadOnlyList<CollocationItem> FindGlossaryTerms(string text, string targetLanguage)
    {
        lock (_gate)
        {
            return _store.Entries
                .Where(e => e.ItemType is VocabularyItemType.TechnicalTerm or VocabularyItemType.Phrase or VocabularyItemType.Idiom
                    && e.TargetLanguage == targetLanguage && e.Headword.Length is > 0 and <= 100
                    && e.Translation.Length is > 0 and <= 240 && e.Headword != text
                    && DictionaryTextMatch.IndexOf(text, e.Headword, comparison: StringComparison.Ordinal) >= 0)
                .OrderByDescending(e => e.Headword.Length).ThenBy(e => e.Headword, StringComparer.Ordinal)
                .Select(e => new CollocationItem(e.Headword, e.Translation)).Distinct().Take(8).ToArray();
        }
    }

    public IReadOnlyList<string> GetAllTags()
    {
        lock (_gate)
        {
            return _store.Entries
                .SelectMany(e => e.Tags)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public VocabularyEntry? FindByHeadword(string headword)
    {
        if (string.IsNullOrWhiteSpace(headword)) return null;
        var normalized = headword.Trim();
        lock (_gate)
        {
            return _store.Entries.FirstOrDefault(e => string.Equals(e.Headword, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IReadOnlyList<VocabularyEntry> QueryEntries(VocabularyQueryFilter filter, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var currentTime = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            IEnumerable<VocabularyEntry> query = _store.Entries;

            if (!string.IsNullOrWhiteSpace(filter.NotebookId) && filter.NotebookId != "all")
                query = query.Where(e => string.Equals(e.NotebookId, filter.NotebookId, StringComparison.OrdinalIgnoreCase));

            if (filter.ItemType.HasValue)
                query = query.Where(e => e.ItemType == filter.ItemType.Value);

            if (filter.Stage.HasValue)
                query = query.Where(e => e.Stage == filter.Stage.Value);

            if (filter.OnlyStarred)
                query = query.Where(e => e.IsStarred);

            if (filter.OnlyDue)
                query = query.Where(e => e.IsDue(currentTime));

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
        }
    }

    public IReadOnlyList<VocabularyEntry> GetDueQueue(string? notebookId = null, int maxCount = 50, DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        lock (_gate)
        {
            IEnumerable<VocabularyEntry> query = _store.Entries;
            if (!string.IsNullOrWhiteSpace(notebookId) && notebookId != "all")
                query = query.Where(e => string.Equals(e.NotebookId, notebookId, StringComparison.OrdinalIgnoreCase));

            return query
                .Where(e => e.IsDue(currentTime))
                .OrderBy(e => e.Stage == MemoryStage.Learning ? 0 : e.Stage == MemoryStage.Reviewing ? 1 : 2)
                .ThenBy(e => e.NextReviewAt)
                .ThenByDescending(e => e.IsStarred)
                .Take(Math.Max(1, maxCount))
                .ToArray();
        }
    }

    public VocabularyDashboardStats GetDashboardStats(DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        // "Today" must be the user's local calendar day, not the UTC day: computing the boundary
        // from the UTC date would misclassify reviews done late at night (or early morning, for
        // timezones ahead of UTC) as belonging to the wrong day.
        var localNow = currentTime.ToLocalTime();
        var todayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
        lock (_gate)
        {
            var entries = _store.Entries;
            var reviewedToday = _store.ReviewLogs.Count(l => l.ReviewedAt >= todayStart);
            var activeEntries = entries.Where(e => e.Stage != MemoryStage.New).ToArray();
            var avgRetention = activeEntries.Length == 0
                ? 1.0
                : activeEntries.Average(e => e.EstimateRetrievability(currentTime));

            var byNotebook = _store.Notebooks.ToDictionary(
                n => n.Id,
                n => entries.Count(e => string.Equals(e.NotebookId, n.Id, StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            var byType = Enum.GetValues<VocabularyItemType>()
                .ToDictionary(t => t, t => entries.Count(e => e.ItemType == t));

            return new VocabularyDashboardStats(
                TotalEntries: entries.Count,
                DueTodayCount: entries.Count(e => e.IsDue(currentTime)),
                NewCount: entries.Count(e => e.Stage == MemoryStage.New),
                LearningCount: entries.Count(e => e.Stage == MemoryStage.Learning),
                ReviewingCount: entries.Count(e => e.Stage == MemoryStage.Reviewing),
                MasteredCount: entries.Count(e => e.Stage == MemoryStage.Mastered),
                StarredCount: entries.Count(e => e.IsStarred),
                ReviewedTodayCount: reviewedToday,
                AverageRetentionRate: Math.Round(avgRetention * 100.0, 1),
                CountByNotebook: byNotebook,
                CountByType: byType);
        }
    }

    public VocabularyNotebook CreateNotebook(string name, string description, string iconName = "book-open-variant", string accentHex = "#3B82F6")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        VocabularyNotebook created;
        lock (_gate)
        {
            var existing = _store.Notebooks.FirstOrDefault(n => string.Equals(n.Name, trimmed, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            created = new VocabularyNotebook
            {
                Id = $"nb_{Guid.NewGuid():N}"[..12],
                Name = trimmed,
                Description = description.Trim(),
                IconName = iconName,
                AccentColorHex = accentHex,
                CreatedAt = DateTimeOffset.UtcNow
            };
            CommitOrganization([.. _store.Notebooks, created], _store.Entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return created;
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
        VocabularyEntry saved;

        lock (_gate)
        {
            notebookId = ResolveNotebookId(notebookId);
            var index = _store.Entries.FindIndex(e => e.NotebookId == notebookId &&
                MatchesDictionary(e, headword, result.TargetLanguage, result.LookupContext));
            var entries = new List<VocabularyEntry>(_store.Entries);
            if (index >= 0)
            {
                var current = _store.Entries[index];
                if (result.CacheStamp is { } stamp && stamp.Revision < current.DictionaryRevision)
                    return current;
                var translation = string.IsNullOrWhiteSpace(result.PrimaryTranslation) ? current.Translation : result.PrimaryTranslation;
                var mergedTags = current.Tags.Concat(result.SuggestedTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                saved = current with
                {
                    DictionaryContext = result.LookupContext,
                    DictionaryRevision = result.CacheStamp?.Revision ?? current.DictionaryRevision,
                    Translation = translation,
                    TranslationDecisions = result.TranslationDecisions.Count == 0 && current.Translation == translation &&
                        current.TargetLanguage == result.TargetLanguage ? current.TranslationDecisions : result.TranslationDecisions,
                    UkPhonetic = string.IsNullOrWhiteSpace(result.UkPhonetic) ? current.UkPhonetic : result.UkPhonetic!,
                    UsPhonetic = string.IsNullOrWhiteSpace(result.UsPhonetic) ? current.UsPhonetic : result.UsPhonetic!,
                    Senses = MergeRecapturedSenses(result.Senses, current.Senses),
                    Pronunciation = string.IsNullOrWhiteSpace(result.Pronunciation) ? current.Pronunciation : result.Pronunciation,
                    CommonMistakes = string.IsNullOrWhiteSpace(result.CommonMistakes) ? current.CommonMistakes : result.CommonMistakes,
                    WordChoice = string.IsNullOrWhiteSpace(result.WordChoice) ? current.WordChoice : result.WordChoice,
                    UsageContrasts = result.UsageContrasts.Count > 0 ? result.UsageContrasts : current.UsageContrasts,
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
                entries[index] = saved;
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
                entries.Insert(0, saved);
            }
            CommitOrganization(_store.Notebooks, entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    public VocabularyEntry SaveEntry(VocabularyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var updated = entry with { UpdatedAt = DateTimeOffset.UtcNow };
        lock (_gate)
        {
            var idx = _store.Entries.FindIndex(e => e.Id == entry.Id);
            if (idx >= 0) _store.Entries[idx] = updated;
            else _store.Entries.Insert(0, updated);
            PersistLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public void ToggleStar(string entryId)
    {
        lock (_gate)
        {
            var idx = _store.Entries.FindIndex(e => e.Id == entryId);
            if (idx < 0) return;
            var current = _store.Entries[idx];
            _store.Entries[idx] = current with { IsStarred = !current.IsStarred, UpdatedAt = DateTimeOffset.UtcNow };
            PersistLocked();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteEntry(string entryId)
    {
        lock (_gate)
        {
            var entries = _store.Entries.Where(e => e.Id != entryId).ToList();
            if (entries.Count == _store.Entries.Count) return;
            CommitOrganization(_store.Notebooks, entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public VocabularyEntry SubmitReview(
        string entryId,
        ReviewRating rating,
        ReviewQuizMode quizMode = ReviewQuizMode.Flashcard,
        int durationMs = 1500,
        DateTimeOffset? now = null)
    {
        var reviewTime = now ?? DateTimeOffset.UtcNow;
        VocabularyEntry updated;
        lock (_gate)
        {
            var idx = _store.Entries.FindIndex(e => e.Id == entryId);
            if (idx < 0) throw new InvalidOperationException("找不到对应的生词卡片。");
            var before = _store.Entries[idx];
            updated = SpacedRepetitionScheduler.ApplyReview(before, rating, reviewTime);
            _store.Entries[idx] = updated;
            _store.ReviewLogs.Add(new ReviewLogRecord(
                Id: $"rev_{Guid.NewGuid():N}"[..16],
                EntryId: entryId,
                Rating: rating,
                QuizMode: quizMode,
                PreviousStage: before.Stage,
                NewStage: updated.Stage,
                PreviousStability: before.StabilityDays,
                NewStability: updated.StabilityDays,
                DurationMs: durationMs,
                ReviewedAt: reviewTime));
            PersistLocked();
        }
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
        var count = 0;
        lock (_gate)
        {
            foreach (var sourceEntry in imported)
            {
                var entry = sourceEntry with { NotebookId = ResolveNotebookId(sourceEntry.NotebookId) };
                if (string.IsNullOrWhiteSpace(entry.Headword)) continue;
                var idx = _store.Entries.FindIndex(e => e.NotebookId == entry.NotebookId &&
                    MatchesDictionary(e, entry.Headword, entry.TargetLanguage, entry.GetDictionaryContext()));
                if (idx >= 0)
                {
                    _store.Entries[idx] = entry with { Id = _store.Entries[idx].Id };
                }
                else
                {
                    // Guard against duplicate IDs: an imported entry whose Id collides with a different
                    // existing entry (same Id, different headword) would otherwise corrupt Id-based
                    // lookups (SaveEntry/ToggleStar/DeleteEntry/SubmitReview all match by first Id found).
                    var toAdd = _store.Entries.Any(e => e.Id == entry.Id)
                        ? entry with { Id = $"voc_{Guid.NewGuid():N}"[..16] }
                        : entry;
                    _store.Entries.Add(toAdd);
                }
                count++;
            }
            if (count > 0) PersistLocked();
        }
        if (count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    private VocabularyStoreSnapshot LoadOrInitialize()
    {
        try
        {
            if (File.Exists(_storagePath))
            {
                var json = File.ReadAllText(_storagePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<VocabularyStoreSnapshot>(json, JsonOptions);
                if (loaded is { Notebooks.Count: > 0 })
                {
                    var ids = loaded.Notebooks.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < loaded.Entries.Count; i++)
                        if (!ids.Contains(loaded.Entries[i].NotebookId))
                            loaded.Entries[i] = loaded.Entries[i] with { NotebookId = "default" };
                    return loaded;
                }
            }
        }
        catch
        {
        }

        var seeded = VocabularySeeder.CreateDefaultSeededStore();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
            File.WriteAllText(_storagePath, JsonSerializer.Serialize(seeded, JsonOptions), Encoding.UTF8);
        }
        catch
        {
        }
        return seeded;
    }

    private void PersistLocked()
    {
        try
        {
            WriteSnapshot(_store);
        }
        catch
        {
        }
    }

    private void WriteSnapshot(VocabularyStoreSnapshot snapshot)
    {
        var dir = Path.GetDirectoryName(_storagePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tempPath = _storagePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions), Encoding.UTF8);
        File.Move(tempPath, _storagePath, overwrite: true);
    }
}
