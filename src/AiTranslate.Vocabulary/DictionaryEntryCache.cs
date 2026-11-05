using System.Text.Json;
using AiTranslate.Core;
using Microsoft.Data.Sqlite;

namespace AiTranslate.Vocabulary;

/// <summary>Local translation storage, independent of the user's vocabulary notebooks.</summary>
public sealed class DictionaryEntryCache(string? databasePath = null) : IDictionaryEntryCache, IVerifiedTranslationCache
{
    private readonly string _path = Path.GetFullPath(databasePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiTranslate", "dictionary-cache.sqlite3"));
    private readonly object _schemaLock = new();
    private volatile bool _initialized;

    public IEnumerable<(string Source, string Key)> FindCompletions(string prefix, string? targetLanguage, string readerLanguage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prefix)) yield break;
        cancellationToken.ThrowIfCancellationRequested();
        using var db = Open();
        using var command = db.CreateCommand();
        // FF sorts after every UTF-8 continuation. The exclusive bounds also treat %, _ and [ literally.
        command.CommandText = """
            SELECT source, cache_key FROM dictionary_suggestions
            WHERE source > $prefix AND source < ($prefix || X'FF')
              AND ($target IS NULL OR target = $target) AND (reader IS NULL OR reader = $reader)
            ORDER BY length(source), request_ticks DESC, source, cache_key
            """;
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$target", (object?)targetLanguage ?? DBNull.Value);
        command.Parameters.AddWithValue("$reader", readerLanguage);
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (rows.GetString(0), rows.GetString(1));
        }
    }

    public DictionaryEntryResult? Read(string key)
    {
        var (entry, revision) = Read<DictionaryEntryResult>("dictionary", key);
        return !string.IsNullOrWhiteSpace(entry?.PrimaryTranslation)
            ? entry with { CacheStamp = new(key, revision) } : null;
    }

    public string[]? ReadTranslations(string key) => Read<string[]>("verified_translations", key).Value;

    private (T? Value, long Revision) Read<T>(string table, string key) where T : class
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT entry, request_ticks FROM {table} WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", key);
        using var row = command.ExecuteReader();
        if (!row.Read()) return default;
        try
        {
            return (JsonSerializer.Deserialize<T>(row.GetString(0)), row.GetInt64(1));
        }
        catch (JsonException)
        {
            return default; // A successful lookup can repair an unreadable row.
        }
    }

    public bool Write(string key, DictionaryEntryResult result, long requestTicks) =>
        Upsert("dictionary", key, JsonSerializer.Serialize(result), requestTicks);

    public bool TryAdd(string key, DictionaryEntryResult result, long requestTicks) =>
        Upsert("dictionary", key, JsonSerializer.Serialize(result), requestTicks, insertOnly: true);

    public void WriteTranslations(string key, string[] translations, long requestTicks) =>
        Upsert("verified_translations", key, JsonSerializer.Serialize(translations), requestTicks);

    private bool Upsert(string table, string key, string json, long requestTicks, bool insertOnly = false)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {table}(cache_key, entry, request_ticks) VALUES($key, $entry, $ticks)
            ON CONFLICT(cache_key) DO UPDATE SET entry = excluded.entry, request_ticks = excluded.request_ticks
            WHERE NOT $insertOnly AND excluded.request_ticks >= {table}.request_ticks
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$entry", json);
        command.Parameters.AddWithValue("$insertOnly", insertOnly);
        command.Parameters.AddWithValue("$ticks", requestTicks);
        return command.ExecuteNonQuery() == 1;
    }

    // A supplement belongs to the exact entry the reader saw. Never overwrite a newer translation.
    public bool TryReplace(DictionaryCacheStamp previous, DictionaryEntryResult result, long requestTicks)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE dictionary SET entry = $entry, request_ticks = $ticks
            WHERE cache_key = $key AND request_ticks = $previous
            """;
        command.Parameters.AddWithValue("$key", previous.Key);
        command.Parameters.AddWithValue("$previous", previous.Revision);
        command.Parameters.AddWithValue("$ticks", requestTicks);
        command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(result));
        return command.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        if (!_initialized) Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, DefaultTimeout = 2, Pooling = false }.ToString());
        try
        {
            db.Open();
            if (_initialized) return db;
            lock (_schemaLock)
            {
                if (_initialized) return db;
                using var schema = db.CreateCommand();
                schema.CommandText = """
                CREATE TABLE IF NOT EXISTS dictionary (
                  cache_key TEXT PRIMARY KEY, entry TEXT NOT NULL, request_ticks INTEGER NOT NULL
                ) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS verified_translations (
                  cache_key TEXT PRIMARY KEY, entry TEXT NOT NULL, request_ticks INTEGER NOT NULL
                ) WITHOUT ROWID;
                """;
                schema.ExecuteNonQuery();
                EnsureSuggestionIndex(db);
                _initialized = true;
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static void EnsureSuggestionIndex(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='dictionary_suggestions'";
        if (command.ExecuteScalar() is not null) return;

        // Serialize the backfill with other processes. Triggers also track writes by older app versions.
        using var transaction = db.BeginTransaction();
        command.Transaction = transaction;
        if (command.ExecuteScalar() is null)
        {
            static string Project(string row) => $"""
                INSERT INTO dictionary_suggestions(cache_key, source, target, reader, request_ticks)
                SELECT {row}.cache_key, json_extract({row}.entry, '$.SourceText'), json_extract({row}.entry, '$.TargetLanguage'),
                       json_extract({row}.entry, '$.ReaderLanguage'), {row}.request_ticks
                {(row == "NEW" ? "" : "FROM dictionary")}
                WHERE CASE WHEN json_valid({row}.entry) THEN length(trim(json_extract({row}.entry, '$.PrimaryTranslation'))) > 0
                      AND json_type({row}.entry, '$.SourceText') = 'text' END;
                """;
            command.CommandText = $"""
                CREATE TABLE dictionary_suggestions (
                    cache_key TEXT PRIMARY KEY, source TEXT NOT NULL, target TEXT, reader TEXT, request_ticks INTEGER NOT NULL
                );
                {Project("dictionary")}
                CREATE INDEX dictionary_suggestions_prefix ON dictionary_suggestions
                    (source, length(source), request_ticks DESC, cache_key, target, reader);
                CREATE TRIGGER dictionary_suggestions_insert AFTER INSERT ON dictionary BEGIN
                    {Project("NEW")}
                END;
                CREATE TRIGGER dictionary_suggestions_update AFTER UPDATE ON dictionary BEGIN
                    DELETE FROM dictionary_suggestions WHERE cache_key = OLD.cache_key;
                    {Project("NEW")}
                END;
                CREATE TRIGGER dictionary_suggestions_delete AFTER DELETE ON dictionary BEGIN
                    DELETE FROM dictionary_suggestions WHERE cache_key = OLD.cache_key;
                END;
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }
}
