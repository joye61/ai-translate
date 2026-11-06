using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiTranslate.Core;
using Microsoft.Data.Sqlite;

namespace AiTranslate.Vocabulary;

/// <summary>
/// SQLite-backed grammar library, independent of the notebooks and the translation cache. Each grammar point is stored once
/// under its canonical title; the alias table lets differently worded questions resolve to the same entry without a model call.
/// </summary>
public sealed class GrammarLibrary(string? databasePath = null) : IGrammarLibrary
{
    private const int MinimumAliasLength = 3;
    private readonly string _path = Path.GetFullPath(databasePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiTranslate", "grammar-library.sqlite3"));
    private readonly object _schemaLock = new();
    private volatile bool _initialized;

    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM topics";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private static string Id(string reader, string language, string norm) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{reader}|{language}|{norm}")));

    public GrammarTopic? Find(string reader, string? language, string query)
    {
        var norm = GrammarText.Normalize(query);
        if (norm.Length == 0) return null;
        using var db = Open();
        var id = FindId(db, reader, language, norm);
        if (id is null) return null;
        using var read = db.CreateCommand();
        read.CommandText = "SELECT payload FROM topics WHERE id = $id";
        read.Parameters.AddWithValue("$id", id);
        GrammarTopic? topic;
        try { topic = JsonSerializer.Deserialize<GrammarTopic>((string)read.ExecuteScalar()!); }
        catch (JsonException) { return null; } // A later lookup regenerates and replaces the unreadable row.
        if (topic is null) return null;
        using var touch = db.CreateCommand();
        touch.CommandText = "UPDATE topics SET hits = hits + 1, used = $now WHERE id = $id";
        touch.Parameters.AddWithValue("$id", id);
        touch.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
        touch.ExecuteNonQuery();
        return topic with { Origin = GrammarOrigin.Library };
    }

    public bool Contains(string reader, string? language, string query) => ResolveTitle(reader, language, query) is not null;

    public string? ResolveTitle(string reader, string? language, string query)
    {
        var norm = GrammarText.Normalize(query);
        if (norm.Length == 0) return null;
        using var db = Open();
        var id = FindId(db, reader, language, norm);
        if (id is null) return null;
        using var command = db.CreateCommand();
        command.CommandText = "SELECT title FROM topics WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static string? FindId(SqliteConnection db, string reader, string? language, string norm)
    {
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id FROM (
                SELECT id, hits, used FROM topics WHERE reader = $reader AND norm = $norm AND ($language IS NULL OR language = $language)
                UNION ALL
                SELECT t.id, t.hits, t.used FROM aliases a JOIN topics t ON t.id = a.topic_id
                WHERE a.reader = $reader AND a.alias = $norm AND ($language IS NULL OR a.language = $language))
            ORDER BY hits DESC, used DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$reader", reader);
        command.Parameters.AddWithValue("$norm", norm);
        command.Parameters.AddWithValue("$language", (object?)language ?? DBNull.Value);
        return command.ExecuteScalar() as string;
    }

    public IReadOnlyList<GrammarTopicSummary> Search(string reader, string query, int limit)
    {
        var norm = GrammarText.Normalize(query);
        if (norm.Length < 2 || limit <= 0) return [];
        using var db = Open();
        var scores = new Dictionary<string, int>();
        void Score(string id, string text)
        {
            if (text.Length == 0) return;
            var score = text == norm ? 100
                : text.Contains(norm, StringComparison.Ordinal) && norm.Length >= 2 ? 60 + Math.Min(norm.Length, 30)
                : norm.Contains(text, StringComparison.Ordinal) && text.Length >= MinimumAliasLength - (text.Any(c => c > 0x2E80) ? 1 : 0)
                    ? 30 + Math.Min(text.Length, 30) : 0;
            if (score > 0 && (!scores.TryGetValue(id, out var best) || score > best)) scores[id] = score;
        }
        using (var titles = db.CreateCommand())
        {
            titles.CommandText = "SELECT id, norm FROM topics WHERE reader = $reader";
            titles.Parameters.AddWithValue("$reader", reader);
            using var rows = titles.ExecuteReader();
            while (rows.Read()) Score(rows.GetString(0), rows.GetString(1));
        }
        using (var aliases = db.CreateCommand())
        {
            aliases.CommandText = "SELECT topic_id, alias FROM aliases WHERE reader = $reader";
            aliases.Parameters.AddWithValue("$reader", reader);
            using var rows = aliases.ExecuteReader();
            while (rows.Read()) Score(rows.GetString(0), rows.GetString(1));
        }
        if (scores.Count == 0) return [];
        var ranked = Summaries(db, "WHERE reader = $reader", ("$reader", reader))
            .Where(s => scores.ContainsKey(s.Id)).OrderByDescending(s => scores[s.Id]).ThenByDescending(s => s.Hits).Take(limit).ToArray();
        return ranked;
    }

    public IReadOnlyList<GrammarTopicSummary> Recent(string reader, int limit)
    {
        if (limit <= 0) return [];
        using var db = Open();
        return Summaries(db, "WHERE reader = $reader ORDER BY used DESC LIMIT " + limit, ("$reader", reader));
    }

    public IReadOnlyList<string> Titles(string reader, int limit)
    {
        if (limit <= 0) return [];
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT title FROM topics WHERE reader = $reader ORDER BY hits DESC, used DESC LIMIT " + limit;
        command.Parameters.AddWithValue("$reader", reader);
        using var rows = command.ExecuteReader();
        var titles = new List<string>();
        while (rows.Read()) titles.Add(rows.GetString(0));
        return titles;
    }

    private static GrammarTopicSummary[] Summaries(SqliteConnection db, string clause, params (string Name, object Value)[] values)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT id, title, language, category, summary, updated, hits, extended FROM topics " + clause;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        using var rows = command.ExecuteReader();
        var result = new List<GrammarTopicSummary>();
        while (rows.Read())
            result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2), rows.GetString(3), rows.GetString(4),
                new DateTimeOffset(rows.GetInt64(5), TimeSpan.Zero), rows.GetInt32(6), rows.GetInt64(7) != 0));
        return [.. result];
    }

    public void Save(GrammarTopic topic, IEnumerable<string> aliases)
    {
        var norm = GrammarText.Normalize(topic.Title);
        if (norm.Length == 0) return;
        var merged = Distinct(topic.Aliases.Concat(aliases).Append(topic.SourceTerm), topic.Title);
        var stored = topic with { Aliases = merged, Origin = GrammarOrigin.Ai };
        var id = Id(topic.ReaderLanguage, topic.Language, norm);
        var now = DateTimeOffset.UtcNow.UtcTicks;
        using (var db = Open())
        {
            using var transaction = db.BeginTransaction();
            using (var command = db.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO topics(id, reader, language, title, norm, category, summary, extended, hits, updated, used, payload)
                    VALUES($id, $reader, $language, $title, $norm, $category, $summary, $extended, 0, $now, $now, $payload)
                    ON CONFLICT(id) DO UPDATE SET title=excluded.title, category=excluded.category, summary=excluded.summary,
                        extended=excluded.extended, updated=excluded.updated, payload=excluded.payload
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$reader", topic.ReaderLanguage);
                command.Parameters.AddWithValue("$language", topic.Language);
                command.Parameters.AddWithValue("$title", topic.Title);
                command.Parameters.AddWithValue("$norm", norm);
                command.Parameters.AddWithValue("$category", topic.Category);
                command.Parameters.AddWithValue("$summary", topic.Summary);
                command.Parameters.AddWithValue("$extended", topic.Extended ? 1 : 0);
                command.Parameters.AddWithValue("$now", now);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(stored));
                command.ExecuteNonQuery();
            }
            InsertAliases(db, transaction, topic.ReaderLanguage, topic.Language, id, norm, merged);
            transaction.Commit();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddAliases(string reader, string language, string title, IEnumerable<string> aliases)
    {
        var norm = GrammarText.Normalize(title);
        var id = Id(reader, language, norm);
        var additions = Distinct(aliases, title);
        if (additions.Count == 0) return;
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT payload FROM topics WHERE id = $id";
            read.Parameters.AddWithValue("$id", id);
            if (read.ExecuteScalar() is not string payload) return;
            GrammarTopic? topic;
            try { topic = JsonSerializer.Deserialize<GrammarTopic>(payload); }
            catch (JsonException) { return; }
            if (topic is null) return;
            var merged = Distinct(topic.Aliases.Concat(additions), topic.Title);
            using var update = db.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE topics SET payload = $payload WHERE id = $id";
            update.Parameters.AddWithValue("$id", id);
            update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(topic with { Aliases = merged }));
            update.ExecuteNonQuery();
            InsertAliases(db, transaction, reader, language, id, norm, merged);
        }
        transaction.Commit();
    }

    public void Remove(string reader, string language, string title)
    {
        var id = Id(reader, language, GrammarText.Normalize(title));
        using (var db = Open())
        {
            using var transaction = db.BeginTransaction();
            foreach (var table in new[] { "aliases WHERE topic_id", "topics WHERE id" })
            {
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM " + table + " = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static List<string> Distinct(IEnumerable<string> aliases, string title)
    {
        var own = GrammarText.Normalize(title);
        var seen = new HashSet<string>(StringComparer.Ordinal) { own };
        var result = new List<string>();
        foreach (var alias in aliases)
        {
            var text = alias?.Trim() ?? "";
            var norm = GrammarText.Normalize(text);
            if (norm.Length >= MinimumAliasLength && text.Length <= 60 && seen.Add(norm)) result.Add(text);
        }
        return result;
    }

    // An alias that already names another grammar point stays with its owner.
    private static void InsertAliases(SqliteConnection db, SqliteTransaction transaction, string reader, string language, string id,
        string ownNorm, IEnumerable<string> aliases)
    {
        foreach (var alias in aliases)
        {
            var norm = GrammarText.Normalize(alias);
            if (norm.Length == 0 || norm == ownNorm) continue;
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO aliases(reader, language, alias, topic_id)
                SELECT $reader, $language, $alias, $id
                WHERE NOT EXISTS (SELECT 1 FROM topics WHERE reader = $reader AND language = $language AND norm = $alias AND id <> $id)
                """;
            command.Parameters.AddWithValue("$reader", reader);
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$alias", norm);
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        if (!_initialized) Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, DefaultTimeout = 3, Pooling = false, ForeignKeys = true
        }.ToString());
        try
        {
            db.Open();
            if (_initialized) return db;
            lock (_schemaLock)
            {
                if (_initialized) return db;
                using var command = db.CreateCommand();
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS topics (
                        id TEXT PRIMARY KEY, reader TEXT NOT NULL, language TEXT NOT NULL, title TEXT NOT NULL, norm TEXT NOT NULL,
                        category TEXT NOT NULL, summary TEXT NOT NULL, extended INTEGER NOT NULL, hits INTEGER NOT NULL,
                        updated INTEGER NOT NULL, used INTEGER NOT NULL, payload TEXT NOT NULL);
                    CREATE UNIQUE INDEX IF NOT EXISTS topics_norm ON topics(reader, language, norm);
                    CREATE TABLE IF NOT EXISTS aliases (
                        reader TEXT NOT NULL, language TEXT NOT NULL, alias TEXT NOT NULL,
                        topic_id TEXT NOT NULL REFERENCES topics(id) ON DELETE CASCADE,
                        PRIMARY KEY (reader, language, alias));
                    CREATE INDEX IF NOT EXISTS aliases_topic ON aliases(topic_id);
                    """;
                command.ExecuteNonQuery();
                _initialized = true;
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }
}
