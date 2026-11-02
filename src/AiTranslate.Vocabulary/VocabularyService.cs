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
public sealed class VocabularyService : IVocabularyService
{
    private readonly string _storagePath;
    private readonly object _gate = new();
    private readonly VocabularyStoreSnapshot _store;

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
                _ => query.OrderByDescending(e => e.UpdatedAt)
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
        var todayStart = new DateTimeOffset(currentTime.UtcDateTime.Date, TimeSpan.Zero);
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
            _store.Notebooks.Add(created);
            PersistLocked();
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
        VocabularyEntry saved;

        lock (_gate)
        {
            var index = _store.Entries.FindIndex(e => string.Equals(e.Headword, headword, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                var current = _store.Entries[index];
                var mergedTags = current.Tags.Concat(result.SuggestedTags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                saved = current with
                {
                    Translation = string.IsNullOrWhiteSpace(result.PrimaryTranslation) ? current.Translation : result.PrimaryTranslation,
                    UkPhonetic = string.IsNullOrWhiteSpace(result.UkPhonetic) ? current.UkPhonetic : result.UkPhonetic!,
                    UsPhonetic = string.IsNullOrWhiteSpace(result.UsPhonetic) ? current.UsPhonetic : result.UsPhonetic!,
                    Senses = result.Senses.Count > 0 ? result.Senses : current.Senses,
                    Inflections = result.Inflections.Count > 0 ? result.Inflections : current.Inflections,
                    Collocations = result.Collocations.Count > 0 ? result.Collocations : current.Collocations,
                    Examples = result.Examples.Count > 0 ? result.Examples : current.Examples,
                    Synonyms = result.Synonyms.Count > 0 ? result.Synonyms : current.Synonyms,
                    Antonyms = result.Antonyms.Count > 0 ? result.Antonyms : current.Antonyms,
                    EtymologyAndRoot = !string.IsNullOrWhiteSpace(result.EtymologyAndRoot) ? result.EtymologyAndRoot! : current.EtymologyAndRoot,
                    MemoryMnemonic = !string.IsNullOrWhiteSpace(result.MemoryMnemonic) ? result.MemoryMnemonic! : current.MemoryMnemonic,
                    GrammarOrUsageNote = !string.IsNullOrWhiteSpace(result.GrammarBreakdown) ? result.GrammarBreakdown! : current.GrammarOrUsageNote,
                    ContextSentence = !string.IsNullOrWhiteSpace(contextSentence) ? contextSentence!.Trim() : current.ContextSentence,
                    ContextTranslation = !string.IsNullOrWhiteSpace(result.ContextMeaning) ? result.ContextMeaning! : current.ContextTranslation,
                    UserPersonalNote = !string.IsNullOrWhiteSpace(userNote) ? userNote.Trim() : current.UserPersonalNote,
                    Tags = mergedTags,
                    LookupCount = current.LookupCount + 1,
                    UpdatedAt = now
                };
                _store.Entries[index] = saved;
            }
            else
            {
                saved = new VocabularyEntry
                {
                    Id = $"voc_{Guid.NewGuid():N}"[..16],
                    Headword = headword,
                    Translation = result.PrimaryTranslation,
                    NotebookId = string.IsNullOrWhiteSpace(notebookId) ? "default" : notebookId,
                    ItemType = result.ItemType,
                    SourceLanguage = result.DetectedSourceLanguage,
                    TargetLanguage = result.TargetLanguage,
                    UkPhonetic = result.UkPhonetic ?? "",
                    UsPhonetic = result.UsPhonetic ?? "",
                    Senses = result.Senses,
                    Inflections = result.Inflections,
                    Collocations = result.Collocations,
                    Examples = result.Examples,
                    Synonyms = result.Synonyms,
                    Antonyms = result.Antonyms,
                    EtymologyAndRoot = result.EtymologyAndRoot ?? "",
                    MemoryMnemonic = result.MemoryMnemonic ?? "",
                    GrammarOrUsageNote = string.Join("\n", new[] { result.GrammarBreakdown, result.UsageNotes }.Where(s => !string.IsNullOrWhiteSpace(s))),
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
                };
                _store.Entries.Insert(0, saved);
            }
            PersistLocked();
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
            if (_store.Entries.RemoveAll(e => e.Id == entryId) == 0) return;
            PersistLocked();
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
            foreach (var entry in imported)
            {
                if (string.IsNullOrWhiteSpace(entry.Headword)) continue;
                var idx = _store.Entries.FindIndex(e => string.Equals(e.Headword, entry.Headword, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) _store.Entries[idx] = entry;
                else _store.Entries.Add(entry);
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
                    return loaded;
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
            var dir = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tempPath = _storagePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_store, JsonOptions), Encoding.UTF8);
            File.Move(tempPath, _storagePath, overwrite: true);
        }
        catch
        {
        }
    }
}
