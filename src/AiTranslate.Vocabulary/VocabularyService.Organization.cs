using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

public sealed partial class VocabularyService
{
    private static string? FindNotebookId(VocabularySession db, string? id) =>
        db.Books().FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase))?.Id;

    public VocabularyNotebook RenameNotebook(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var updated = _database.Write(db =>
        {
            var books = db.Books();
            var book = books.FirstOrDefault(n => n.Id == id)
                ?? throw new InvalidOperationException("这个生词本已被删除。");
            name = name.Trim();
            if (books.Any(n => n.Id != id && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("已有同名生词本，请换一个名称。");
            var renamed = book with { Name = name };
            db.UpdateBook(renamed);
            return renamed;
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public VocabularyNotebook SetNotebookTranslationsHidden(string id, bool hidden)
    {
        var (book, changed) = _database.Write(db =>
        {
            var book = db.Books().FirstOrDefault(n => n.Id == id)
                ?? throw new InvalidOperationException("这个生词本已被删除。");
            if (book.HideTranslations == hidden) return (book, false);
            var updated = book with { HideTranslations = hidden };
            db.UpdateBook(updated);
            return (updated, true);
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return book;
    }

    public void DeleteNotebook(string id)
    {
        var changed = _database.Write(db =>
        {
            var book = db.Books().FirstOrDefault(n => n.Id == id);
            if (book is null) return false;
            if (book.IsSystemDefault || book.Id == "default")
                throw new InvalidOperationException("默认生词本用于接收新词条，需要保留。");
            foreach (var card in db.Cards("WHERE book=$p0", id))
                db.SaveCard(card with { NotebookId = "default" });
            db.Execute("DELETE FROM notebooks WHERE id=$p0", id);
            return true;
        });
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
}
