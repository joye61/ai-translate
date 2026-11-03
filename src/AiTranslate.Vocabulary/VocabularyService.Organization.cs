using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

public sealed partial class VocabularyService
{
    private string ResolveNotebookId(string? id) =>
        _store.Notebooks.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? "default";

    public VocabularyNotebook UpdateNotebook(string id, string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        VocabularyNotebook updated;
        lock (_gate)
        {
            var book = _store.Notebooks.FirstOrDefault(n => n.Id == id)
                ?? throw new InvalidOperationException("这个词本已被删除。");
            name = name.Trim();
            if (_store.Notebooks.Any(n => n.Id != id && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已有同名词本，请换一个名称。");
            updated = book with { Name = name, Description = description.Trim() };
            CommitOrganization(_store.Notebooks.Select(n => n.Id == id ? updated : n).ToList(), _store.Entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public void DeleteNotebook(string id)
    {
        lock (_gate)
        {
            var book = _store.Notebooks.FirstOrDefault(n => n.Id == id);
            if (book is null) return;
            if (book.IsSystemDefault || book.Id == "default")
                throw new InvalidOperationException("默认词本用于接收新词条，需要保留。");
            var entries = _store.Entries.Select(e => string.Equals(e.NotebookId, id, StringComparison.OrdinalIgnoreCase)
                ? e with { NotebookId = "default" } : e).ToList();
            CommitOrganization(_store.Notebooks.Where(n => n.Id != id).ToList(), entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateEntryDetails(string entryId, string notebookId, VocabularyItemType type, string translation, IReadOnlyList<string> tags, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translation);
        translation = translation.Trim();
        lock (_gate)
        {
            if (!_store.Notebooks.Any(n => n.Id == notebookId))
                throw new InvalidOperationException("目标词本已被删除，请重新选择。");
            var index = _store.Entries.FindIndex(e => e.Id == entryId);
            if (index < 0) throw new InvalidOperationException("这个词条已被删除。");
            var entry = _store.Entries[index];
            var entries = new List<VocabularyEntry>(_store.Entries);
            entries[index] = entry with
            {
                NotebookId = notebookId, ItemType = type, Translation = translation, UserPersonalNote = note.Trim(),
                Tags = tags.ToArray(), UpdatedAt = DateTimeOffset.UtcNow,
                TranslationDecisions = entry.Translation == translation ? entry.TranslationDecisions :
                    entry.TranslationDecisions.Where(d => d.IsAnchoredIn(entry.Headword, translation)).ToArray()
            };
            CommitOrganization(_store.Notebooks, entries);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Publish the book change and its entry memberships only after both are on disk.
    private void CommitOrganization(List<VocabularyNotebook> notebooks, List<VocabularyEntry> entries)
    {
        var next = new VocabularyStoreSnapshot { Notebooks = notebooks, Entries = entries, ReviewLogs = _store.ReviewLogs };
        WriteSnapshot(next);
        _store = next;
    }
}
