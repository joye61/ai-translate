using System.Text;
using System.Text.Json;
using AiTranslate.Ai;
using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

public enum VocabularySortOrder
{
    RecentlyAdded = 0,
    Alphabetical = 1,
    ReviewDue = 2
}

public sealed record VocabularyQueryFilter
{
    public string? NotebookId { get; init; }
    public VocabularySortOrder SortOrder { get; init; } = VocabularySortOrder.RecentlyAdded;
}

public sealed record VocabularyNotebookSummary(VocabularyNotebook Notebook, int EntryCount, int DueCount);

public interface IVocabularyService
{
    event EventHandler? Changed;

    IReadOnlyList<VocabularyNotebook> GetNotebooks();
    IReadOnlyList<VocabularyNotebookSummary> GetNotebookSummaries(DateTimeOffset? now = null);
    IReadOnlyList<VocabularyEntry> QueryEntries(VocabularyQueryFilter filter);
    IReadOnlyList<VocabularyEntry> GetDueQueue(string notebookId, int maxCount = 50, DateTimeOffset? now = null);
    VocabularyEntry? FindByHeadword(string headword);
    bool IsCollected(DictionaryEntryResult result, string notebookId);
    void CompleteCollection(VocabularyEntry collected, DictionaryEntryResult result, long revision);
    void RemoveFromNotebook(DictionaryEntryResult result, string notebookId);

    VocabularyNotebook CreateNotebook(string name);
    VocabularyNotebook RenameNotebook(string id, string name);
    VocabularyNotebook SetNotebookTranslationsHidden(string id, bool hidden);
    void DeleteNotebook(string id);
    VocabularyEntry AddOrUpdateFromDictionary(
        DictionaryEntryResult result,
        string notebookId = "default",
        string? contextSentence = null,
        TranslationTriggerSource source = TranslationTriggerSource.ManualInput,
        string sourceTitle = "");
    VocabularyEntry SaveEntry(VocabularyEntry entry);
    /// <summary>Moves a saved entry to another notebook. An equivalent entry already there absorbs it and is returned.</summary>
    VocabularyEntry MoveEntry(string entryId, string notebookId);
    void DeleteEntry(string entryId);
    VocabularyEntry SubmitReview(string entryId, ReviewRating rating, int durationMs = 1500, DateTimeOffset? now = null);

    string ExportToMarkdown(VocabularyQueryFilter? filter = null);
    string ExportToAnkiTsv(VocabularyQueryFilter? filter = null);
    string ExportToCsv(VocabularyQueryFilter? filter = null);
    string ExportToJson(VocabularyQueryFilter? filter = null);
    int ImportFromJson(string json);
}
