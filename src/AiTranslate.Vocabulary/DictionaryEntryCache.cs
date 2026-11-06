using System.Security.Cryptography;
using System.Text;
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

    public void UpgradeExistingDatabase()
    {
        if (!File.Exists(_path)) return;
        using var db = Open();
    }

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

    public string[]? ReadTranslations(string key) => Read<string[]>("verified_translations", VerifiedKey(key), key).Value;

    private static string VerifiedKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private (T? Value, long Revision) Read<T>(string table, string key, string? legacyKey = null) where T : class
    {
        using var db = Open();
        using var command = db.CreateCommand();
        // A still-running older version may have written an original OCR key since the migration.
        command.CommandText = $"SELECT entry, request_ticks FROM {table} WHERE " + (legacyKey is null ? "cache_key = $key"
            : "cache_key IN ($key,$legacy) ORDER BY request_ticks DESC LIMIT 1");
        if (legacyKey is not null) command.Parameters.AddWithValue("$legacy", legacyKey);
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
        WriteDictionary(key, result, requestTicks, insertOnly: false);

    public bool TryAdd(string key, DictionaryEntryResult result, long requestTicks) =>
        WriteDictionary(key, result, requestTicks, insertOnly: true);

    private bool WriteDictionary(string key, DictionaryEntryResult result, long requestTicks, bool insertOnly)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO dictionary(cache_key, entry, request_ticks, standalone) VALUES($key, $entry, $ticks, $standalone)
            ON CONFLICT(cache_key) DO UPDATE SET entry=excluded.entry, request_ticks=excluded.request_ticks, standalone=excluded.standalone
            WHERE NOT $insertOnly AND excluded.request_ticks >= dictionary.request_ticks
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(result));
        command.Parameters.AddWithValue("$standalone", string.IsNullOrWhiteSpace(result.LookupContext));
        command.Parameters.AddWithValue("$insertOnly", insertOnly);
        command.Parameters.AddWithValue("$ticks", requestTicks);
        return command.ExecuteNonQuery() == 1;
    }

    public void WriteTranslations(string key, string[] translations, long requestTicks)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO verified_translations(cache_key,entry,request_ticks,source_request) VALUES($key,$entry,$ticks,$source)
            ON CONFLICT(cache_key) DO UPDATE SET entry=excluded.entry, request_ticks=excluded.request_ticks
            WHERE excluded.request_ticks >= verified_translations.request_ticks
            """;
        command.Parameters.AddWithValue("$key", VerifiedKey(key));
        command.Parameters.AddWithValue("$source", key);
        command.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(translations));
        command.Parameters.AddWithValue("$ticks", requestTicks);
        command.ExecuteNonQuery();
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
                EnsureSchema(db);
                _initialized = true;
            }
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private static void EnsureSchema(SqliteConnection db)
    {
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        if ((long)command.ExecuteScalar()! == 2) return;
        using var transaction = db.BeginTransaction();
        command.Transaction = transaction;
        var version = (long)command.ExecuteScalar()!;
        if (version == 0)
        {
            db.CreateFunction<string, string>("verified_key", VerifiedKey, isDeterministic: true);
            // Preserve dictionary payloads and plain-text history. Legacy OCR requests are identifiable by their mode.
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS dictionary (
                    cache_key TEXT PRIMARY KEY, entry TEXT NOT NULL, request_ticks INTEGER NOT NULL
                ) WITHOUT ROWID;
                ALTER TABLE dictionary ADD COLUMN standalone INTEGER NOT NULL DEFAULT 1;
                CREATE TABLE IF NOT EXISTS verified_translations (
                    cache_key TEXT PRIMARY KEY, entry TEXT NOT NULL, request_ticks INTEGER NOT NULL
                ) WITHOUT ROWID;
                ALTER TABLE verified_translations RENAME TO previous_verified;
                CREATE TABLE verified_translations (
                    cache_key TEXT NOT NULL PRIMARY KEY, entry TEXT NOT NULL, request_ticks INTEGER NOT NULL, source_request TEXT
                );
                INSERT INTO verified_translations(cache_key,entry,request_ticks,source_request)
                    SELECT verified_key(cache_key),entry,request_ticks,cache_key FROM previous_verified
                    WHERE json_extract(CASE WHEN json_valid(cache_key) THEN cache_key END,'$.mode') IS NOT 'regions';
                DROP TABLE previous_verified;
                DROP TRIGGER IF EXISTS dictionary_suggestions_insert;
                DROP TRIGGER IF EXISTS dictionary_suggestions_update;
                DROP TRIGGER IF EXISTS dictionary_suggestions_delete;
                DROP TABLE IF EXISTS dictionary_suggestions;
                """;
            command.ExecuteNonQuery();
            static string Project(string row) => $"""
                INSERT INTO dictionary_suggestions(cache_key, source, target, reader, request_ticks)
                SELECT {row}.cache_key, json_extract({row}.entry, '$.SourceText'), json_extract({row}.entry, '$.TargetLanguage'),
                       json_extract({row}.entry, '$.ReaderLanguage'), {row}.request_ticks
                {(row == "NEW" ? "" : "FROM dictionary")}
                WHERE CASE WHEN json_valid({row}.entry) THEN length(trim(json_extract({row}.entry, '$.PrimaryTranslation'))) > 0
                      AND {row}.standalone AND COALESCE(json_extract({row}.entry,'$.ItemType'),0) NOT IN ({(int)VocabularyItemType.Sentence},{(int)VocabularyItemType.Paragraph})
                      AND length(json_extract({row}.entry,'$.SourceText')) BETWEEN 1 AND 100
                      AND instr(json_extract({row}.entry,'$.SourceText'),char(10))=0
                      AND instr(json_extract({row}.entry,'$.SourceText'),char(13))=0
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
        else if (version == 1)
        {
            // v1 uses a digest and stores the original request separately. Older writers used the raw request as the key.
            command.CommandText = """
                DELETE FROM verified_translations
                WHERE json_extract(CASE WHEN json_valid(source_request) THEN source_request
                                        WHEN json_valid(cache_key) THEN cache_key END,'$.mode') = 'regions';
                """;
            command.ExecuteNonQuery();
        }
        else if (version != 2) throw new InvalidDataException($"无法识别翻译缓存版本 {version}。");
        command.CommandText = "PRAGMA user_version=2";
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
