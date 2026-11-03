using System.Text.Json;
using AiTranslate.Core;
using Microsoft.Data.Sqlite;

namespace AiTranslate.Vocabulary;

/// <summary>Local translation storage, independent of the user's vocabulary notebooks.</summary>
public sealed class DictionaryEntryCache(string? databasePath = null) : IDictionaryEntryCache, IVerifiedTranslationCache
{
    private readonly string _path = Path.GetFullPath(databasePath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiTranslate", "dictionary-cache.sqlite3"));

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

    public void WriteTranslations(string key, string[] translations, long requestTicks) =>
        Upsert("verified_translations", key, JsonSerializer.Serialize(translations), requestTicks);

    private bool Upsert(string table, string key, string json, long requestTicks)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {table}(cache_key, entry, request_ticks) VALUES($key, $entry, $ticks)
            ON CONFLICT(cache_key) DO UPDATE SET entry = excluded.entry, request_ticks = excluded.request_ticks
            WHERE excluded.request_ticks >= {table}.request_ticks
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$entry", json);
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
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, DefaultTimeout = 2, Pooling = false }.ToString());
        try
        {
            db.Open();
            using var schema = db.CreateCommand();
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS dictionary (
                  cache_key TEXT PRIMARY KEY, entry TEXT NOT NULL, request_ticks INTEGER NOT NULL
                ) WITHOUT ROWID;
                CREATE TABLE IF NOT EXISTS verified_translations (
                  cache_key TEXT PRIMARY KEY, entry TEXT NOT NULL, request_ticks INTEGER NOT NULL
                ) WITHOUT ROWID
                """;
            schema.ExecuteNonQuery();
            return db;
        }
        catch { db.Dispose(); throw; }
    }
}
