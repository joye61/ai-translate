using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

public sealed partial class VocabularyService
{
    private static string? FindNotebookId(VocabularySession db, string? id) =>
        db.Books().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))?.Id;

    public VocabularyNotebook UpdateNotebook(string id, string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var updated = _database.Write(db =>
        {
            var books = db.Books();
            var book = books.FirstOrDefault(n => n.Id == id)
                ?? throw new InvalidOperationException("这个词本已被删除。");
            name = name.Trim();
            if (books.Any(n => n.Id != id && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已有同名词本，请换一个名称。");
            var renamed = book with { Name = name, Description = description.Trim() };
            db.UpdateBook(renamed);
            return renamed;
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public void DeleteNotebook(string id)
    {
        var changed = _database.Write(db =>
        {
            var book = db.Books().FirstOrDefault(n => n.Id == id);
            if (book is null) return false;
            if (book.IsSystemDefault || book.Id == "default")
                throw new InvalidOperationException("默认词本用于接收新词条，需要保留。");
            foreach (var card in db.Cards("WHERE book=$p0", id))
                db.SaveCard(card with { NotebookId = "default" });
            db.Execute("DELETE FROM notebooks WHERE id=$p0", id);
            return true;
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateEntryDetails(string entryId, string notebookId, VocabularyItemType type, string translation, IReadOnlyList<string> tags, string note)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translation);
        translation = translation.Trim();
        _database.Write(db =>
        {
            if (!db.Books().Any(n => n.Id == notebookId))
                throw new InvalidOperationException("目标词本已被删除，请重新选择。");
            var entry = db.Card(entryId) ?? throw new InvalidOperationException("这个词条已被删除。");
            db.SaveCard(entry with
            {
                NotebookId = notebookId, ItemType = type, Translation = translation, UserPersonalNote = note.Trim(),
                LiteralTranslation = entry.Translation == translation ? entry.LiteralTranslation : "",
                Tags = tags.ToArray(), UpdatedAt = DateTimeOffset.UtcNow,
                TranslationDecisions = entry.Translation == translation ? entry.TranslationDecisions :
                    entry.TranslationDecisions.Where(d => d.IsAnchoredIn(entry.Headword, translation)).ToArray()
            });
            return true;
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
