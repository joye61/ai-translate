using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiTranslate.Core;
using Microsoft.Data.Sqlite;

namespace AiTranslate.Vocabulary;

/// <summary>Each operation sees one consistent snapshot. Writers acquire the SQLite write lock before reading.</summary>
internal sealed class VocabularyDatabase
{
    private readonly string _connectionString;

    public VocabularyDatabase(string? storagePath)
    {
        var path = Path.GetFullPath(storagePath ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "AiTranslate", "vocabulary-store.sqlite3"));
        var legacy = Path.ChangeExtension(path, ".json");
        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
        {
            legacy = path;
            path = Path.ChangeExtension(path, ".sqlite3");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true, DefaultTimeout = 3 }.ToString();
        Write(session =>
        {
            var version = session.Number("PRAGMA user_version");
            if (version == 1) return true;
            if (version != 0 || session.Number("SELECT COUNT(*) FROM sqlite_schema WHERE type='table'") != 0)
                throw new InvalidDataException($"无法识别生词本数据库版本：{path}");
            var snapshot = LoadLegacy(legacy);
            session.Execute("""
                CREATE TABLE notebooks(id TEXT PRIMARY KEY, payload TEXT NOT NULL);
                CREATE TABLE cards(id TEXT PRIMARY KEY, book TEXT NOT NULL REFERENCES notebooks(id),
                    head_key BLOB NOT NULL, target TEXT NOT NULL, kind INTEGER NOT NULL,
                    stage INTEGER NOT NULL, due INTEGER NOT NULL, starred INTEGER NOT NULL,
                    stability REAL NOT NULL, last_review INTEGER, glossary_term TEXT, glossary_meaning TEXT, payload TEXT NOT NULL);
                CREATE INDEX cards_head ON cards(head_key);
                CREATE INDEX cards_book ON cards(book);
                CREATE INDEX cards_glossary ON cards(target,glossary_term,glossary_meaning) WHERE glossary_term IS NOT NULL;
                CREATE INDEX cards_due ON cards(due);
                CREATE TABLE review_logs(id TEXT PRIMARY KEY, card TEXT NOT NULL, reviewed INTEGER NOT NULL, payload TEXT NOT NULL);
                CREATE INDEX reviews_date ON review_logs(reviewed);
                """);
            foreach (var book in snapshot.Notebooks) session.InsertBook(book);
            var books = snapshot.Notebooks.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var card in snapshot.Entries.AsEnumerable().Reverse())
            {
                var bookId = card.NotebookId is not null && books.TryGetValue(card.NotebookId, out var book) ? book.Id : "default";
                session.SaveCard(card with { NotebookId = bookId }, insertOnly: true);
            }
            // Historical reviews can refer to a card the user subsequently deleted.
            foreach (var review in snapshot.ReviewLogs) session.AddReview(review);
            session.Execute("PRAGMA user_version=1");
            return true;
        });
    }

    private static VocabularyStoreSnapshot LoadLegacy(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var data = JsonSerializer.Deserialize<VocabularyStoreSnapshot>(stream, VocabularyService.JsonOptions);
            if (data is not { Notebooks.Count: > 0, Entries: not null, ReviewLogs: not null } ||
                data.Notebooks.Any(n => n is null || string.IsNullOrEmpty(n.Id)) ||
                !data.Notebooks.Any(n => n.Id == "default") ||
                data.Notebooks.Select(n => n.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != data.Notebooks.Count ||
                data.Entries.Any(e => e is null || string.IsNullOrEmpty(e.Id) || e.Headword is null || e.Translation is null) ||
                data.ReviewLogs.Any(r => r is null || string.IsNullOrEmpty(r.Id)))
                throw new InvalidDataException($"旧生词本结构不完整，请检查 {path}");
            return data;
        }
        catch (FileNotFoundException) { return VocabularySeeder.CreateDefaultSeededStore(); }
        catch (JsonException ex) { throw new InvalidDataException($"无法读取旧生词本，请从备份恢复 {path}", ex); }
    }

    public T Read<T>(Func<VocabularySession, T> operation) => Run(operation, write: false);
    public T Write<T>(Func<VocabularySession, T> operation) => Run(operation, write: true);

    private T Run<T>(Func<VocabularySession, T> operation, bool write)
    {
        try
        {
            using var db = new SqliteConnection(_connectionString);
            db.Open();
            using var transaction = db.BeginTransaction(deferred: !write);
            var result = operation(new(db, transaction));
            transaction.Commit();
            return result;
        }
        catch (SqliteException ex) { throw new IOException($"生词本数据库操作失败。{ex.Message}", ex); }
    }
}

internal sealed class VocabularySession(SqliteConnection db, SqliteTransaction transaction)
{
    // IDs, indexed columns and the full payload are committed together.
    private static byte[] HeadKey(string word) => SHA256.HashData(Encoding.UTF8.GetBytes(word.ToUpperInvariant()));

    private SqliteCommand Command(string sql, params object?[] values)
    {
        var cmd = db.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        for (var i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue($"$p{i}", values[i] ?? DBNull.Value);
        return cmd;
    }

    public int Execute(string sql, params object?[] values)
    {
        using var cmd = Command(sql, values);
        return cmd.ExecuteNonQuery();
    }

    public long Number(string sql, params object?[] values)
    {
        using var cmd = Command(sql, values);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<T> Rows<T>(string sql, Func<SqliteDataReader, T> read, params object?[] values)
    {
        using var cmd = Command(sql, values);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    public List<VocabularyNotebook> Books() => Rows("SELECT payload FROM notebooks", Payload<VocabularyNotebook>);
    public void InsertBook(VocabularyNotebook book) =>
        Execute("INSERT INTO notebooks VALUES($p0,$p1)", book.Id, JsonSerializer.Serialize(book));
    public void UpdateBook(VocabularyNotebook book) =>
        Execute("UPDATE notebooks SET payload=$p1 WHERE id=$p0", book.Id, JsonSerializer.Serialize(book));

    private static T Payload<T>(SqliteDataReader row) =>
        JsonSerializer.Deserialize<T>(row.GetString(0)) ?? throw new InvalidDataException("生词本记录不完整。");

    public List<VocabularyEntry> Cards(string clause = "", params object?[] values) =>
        Rows("SELECT payload FROM cards " + clause, Payload<VocabularyEntry>, values);

    public VocabularyEntry? Card(string id) => Cards("WHERE id=$p0", id).FirstOrDefault();
    public List<VocabularyEntry> ByHead(string word) => Cards("WHERE head_key=$p0 ORDER BY rowid DESC", HeadKey(word));

    public void SaveCard(VocabularyEntry card, bool insertOnly = false)
    {
        var glossary = card.ItemType is VocabularyItemType.Phrase or VocabularyItemType.TechnicalTerm or VocabularyItemType.Idiom
            && card.Headword.Length is > 0 and <= 100 && card.Translation.Length is > 0 and <= 240;
        const string update = """
             ON CONFLICT(id) DO UPDATE SET book=excluded.book,head_key=excluded.head_key,target=excluded.target,
             kind=excluded.kind,stage=excluded.stage,due=excluded.due,starred=excluded.starred,
             stability=excluded.stability,last_review=excluded.last_review,glossary_term=excluded.glossary_term,
             glossary_meaning=excluded.glossary_meaning,payload=excluded.payload
            """;
        Execute("INSERT INTO cards(id,book,head_key,target,kind,stage,due,starred,stability,last_review,glossary_term,glossary_meaning,payload) " +
            "VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9,$p10,$p11,$p12)" + (insertOnly ? "" : " " + update),
            card.Id, card.NotebookId, HeadKey(card.Headword), card.TargetLanguage, (int)card.ItemType, (int)card.Stage,
            card.NextReviewAt.UtcTicks, card.IsStarred, card.StabilityDays, card.LastReviewedAt?.UtcTicks,
            glossary ? card.Headword : null, glossary ? card.Translation : null, JsonSerializer.Serialize(card));
    }

    public void AddReview(ReviewLogRecord review) =>
        Execute("INSERT INTO review_logs VALUES($p0,$p1,$p2,$p3)", review.Id, review.EntryId, review.ReviewedAt.UtcTicks, JsonSerializer.Serialize(review));
}

