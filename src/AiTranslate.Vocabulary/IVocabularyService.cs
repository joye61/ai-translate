using System.Text;
using System.Text.Json;
using AiTranslate.Ai;
using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

public enum VocabularySortOrder
{
    RecentlyAdded = 0,
    NextReviewDue = 1,
    Alphabetical = 2,
    MostLookedUp = 3,
    HighestDifficulty = 4,
    LowestMastery = 5
}

public sealed record VocabularyQueryFilter
{
    public string? SearchKeyword { get; init; }
    public string? NotebookId { get; init; }
    public VocabularyItemType? ItemType { get; init; }
    public MemoryStage? Stage { get; init; }
    public string? Tag { get; init; }
    public bool OnlyStarred { get; init; }
    public bool OnlyDue { get; init; }
    public VocabularySortOrder SortOrder { get; init; } = VocabularySortOrder.RecentlyAdded;
}

public sealed record VocabularyDashboardStats(
    int TotalEntries,
    int DueTodayCount,
    int NewCount,
    int LearningCount,
    int ReviewingCount,
    int MasteredCount,
    int StarredCount,
    int ReviewedTodayCount,
    double AverageRetentionRate,
    IReadOnlyDictionary<string, int> CountByNotebook,
    IReadOnlyDictionary<VocabularyItemType, int> CountByType);

public interface IVocabularyService
{
    event EventHandler? Changed;

    IReadOnlyList<VocabularyNotebook> GetNotebooks();
    IReadOnlyList<string> GetAllTags();
    IReadOnlyList<VocabularyEntry> QueryEntries(VocabularyQueryFilter filter, DateTimeOffset? now = null);
    IReadOnlyList<VocabularyEntry> GetDueQueue(string? notebookId = null, int maxCount = 50, DateTimeOffset? now = null);
    VocabularyEntry? FindByHeadword(string headword);
    bool IsCollected(DictionaryEntryResult result, string notebookId);
    void RemoveFromNotebook(DictionaryEntryResult result, string notebookId);
    VocabularyDashboardStats GetDashboardStats(DateTimeOffset? now = null);

    VocabularyNotebook CreateNotebook(string name, string description, string iconName = "book-open-variant", string accentHex = "#3B82F6");
    VocabularyNotebook UpdateNotebook(string id, string name, string description);
    void DeleteNotebook(string id);
    void UpdateEntryDetails(string entryId, string notebookId, VocabularyItemType type, string translation, IReadOnlyList<string> tags, string note);
    VocabularyEntry AddOrUpdateFromDictionary(
        DictionaryEntryResult result,
        string notebookId = "default",
        string? contextSentence = null,
        TranslationTriggerSource source = TranslationTriggerSource.ManualInput,
        string sourceTitle = "",
        string userNote = "");
    VocabularyEntry SaveEntry(VocabularyEntry entry);
    void ToggleStar(string entryId);
    void DeleteEntry(string entryId);
    VocabularyEntry SubmitReview(string entryId, ReviewRating rating, ReviewQuizMode quizMode = ReviewQuizMode.Flashcard, int durationMs = 1500, DateTimeOffset? now = null);

    string ExportToMarkdown(VocabularyQueryFilter? filter = null);
    string ExportToAnkiTsv(VocabularyQueryFilter? filter = null);
    string ExportToCsv(VocabularyQueryFilter? filter = null);
    string ExportToJson(VocabularyQueryFilter? filter = null);
    int ImportFromJson(string json);
}
