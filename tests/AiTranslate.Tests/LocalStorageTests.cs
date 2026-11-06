using System.Text.Json;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AiTranslate.Tests;

public sealed class LocalStorageTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory();
    private string Legacy => Path.Combine(_folder.FullName, "cards.json");
    private string Database => Path.ChangeExtension(Legacy, ".sqlite3");
    public void Dispose() => _folder.Delete(recursive: true);

    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void LegacyMigrationPreservesCardLogAndSourceFileAndRunsOnlyOnce()
    {
        var due = DateTimeOffset.Parse("2026-09-11T08:00:00+08:00");
        var card = new VocabularyEntry { Id = "b", Headword = "bank", Translation = "河岸", ContextSentence = "We sat by the bank.",
            DictionaryContext = "We sat by the bank.", SourceTitle = "阅读", UserPersonalNote = "河边", DictionaryRevision = 17,
            LastReviewedAt = due.AddDays(-7), NextReviewAt = due, Stage = MemoryStage.Reviewing, TotalReviews = 3, StabilityDays = 7, Tags = ["随笔"] };
        var log = new ReviewLogRecord("r", "b", ReviewRating.Good, MemoryStage.Learning, MemoryStage.Reviewing,
            2, 7, 800, due.AddDays(-7)) { LegacyQuizMode = 2 };
        var original = JsonSerializer.Serialize(new { notebooks = new[] { new VocabularyNotebook { Id = "default", Name = "我的词本" } },
            entries = new[] { card }, reviewLogs = new[] { log } });
        File.WriteAllText(Legacy, original);
        var service = new VocabularyService(Legacy);
        var reopened = new VocabularyService(Database);
        var summary = Assert.Single(reopened.GetNotebookSummaries(due));
        Assert.Equal((1, 1), (summary.EntryCount, summary.DueCount));
        Assert.Equal(JsonSerializer.Serialize(card), JsonSerializer.Serialize(reopened.FindByHeadword("BANK")));
        using (var db = Open(Database))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT payload FROM review_logs";
            Assert.Equal(log, JsonSerializer.Deserialize<ReviewLogRecord>((string)cmd.ExecuteScalar()!));
        }
        service.DeleteEntry("b");
        Assert.Null(new VocabularyService(Legacy).FindByHeadword("bank"));
        Assert.Equal(original, File.ReadAllText(Legacy));
    }

    [Fact]
    public void DamagedOrConflictingLegacyRowsRollbackAndCanBeRetried()
    {
        const string broken = """{"notebooks":[""";
        File.WriteAllText(Legacy, broken);
        Assert.Throws<InvalidDataException>(() => new VocabularyService(Legacy));
        Assert.Equal(broken, File.ReadAllText(Legacy));
        const string card = """{"id":"b","headword":"bank","translation":"岸"}""";
        string Snapshot(string cards) => $$"""{"notebooks":[{"id":"default","name":"词本"}],"entries":[{{cards}}]}""";
        var duplicates = Snapshot(card + "," + card);
        File.WriteAllText(Legacy, duplicates);
        Assert.Throws<IOException>(() => new VocabularyService(Legacy));
        Assert.Equal(duplicates, File.ReadAllText(Legacy));
        using (var db = Open(Database))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table'";
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
        File.WriteAllText(Legacy, Snapshot(card));
        Assert.Equal("岸", new VocabularyService(Legacy).FindByHeadword("bank")!.Translation);
    }

    [Fact]
    public async Task ConcurrentServicesKeepBothWritersAndReviewAndLogCommitTogether()
    {
        var first = new VocabularyService(Database);
        var other = new VocabularyService(Database);
        var saved = first.AddOrUpdateFromDictionary(new() { SourceText = "sample term", PrimaryTranslation = "示例" });
        other.AddOrUpdateFromDictionary(new() { SourceText = "next term", PrimaryTranslation = "下一条" });
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            (i % 2 == 0 ? first : other).SubmitReview(saved.Id, ReviewRating.Good))));
        Assert.Equal(8, first.FindByHeadword("sample term")!.TotalReviews);
        Assert.NotNull(first.FindByHeadword("next term"));
        using var db = Open(Database);
        Execute(db, "CREATE TRIGGER reject_log BEFORE INSERT ON review_logs BEGIN SELECT RAISE(ABORT,'test disk failure'); END;");
        var changed = false;
        first.Changed += (_, _) => changed = true;
        Assert.Throws<IOException>(() => first.SubmitReview(saved.Id, ReviewRating.Good));
        Assert.False(changed);
        Assert.Equal(8, other.FindByHeadword("sample term")!.TotalReviews);
        using var count = db.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM review_logs";
        Assert.Equal(8L, count.ExecuteScalar());
    }

    [Fact]
    public void LongAndContextualHistoryStillReopensWithoutEnteringShortWordSuggestions()
    {
        var path = Path.Combine(_folder.FullName, "lookup.sqlite3");
        const string legacyRequest = """{"mode":"regions","target":"zh-Hans","source":["The first line.","The next line."]}""";
        const string textRequest = """{"mode":"text","target":"zh-Hans","source":["The first line."]}""";
        var entry = new DictionaryEntryResult { SourceText = "old sentence", PrimaryTranslation = "旧句", ItemType = VocabularyItemType.Sentence };
        using (var db = Open(path))
        {
            Execute(db, """
                CREATE TABLE dictionary(cache_key TEXT PRIMARY KEY,entry TEXT,request_ticks INTEGER) WITHOUT ROWID;
                CREATE TABLE verified_translations(cache_key TEXT PRIMARY KEY,entry TEXT,request_ticks INTEGER) WITHOUT ROWID;
                """);
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO dictionary VALUES('legacy',$entry,3);
                INSERT INTO verified_translations VALUES($ocr,'["首行","下一行"]',5);
                INSERT INTO verified_translations VALUES($text,'["首行"]',5);
                """;
            cmd.Parameters.AddWithValue("$entry", JsonSerializer.Serialize(entry));
            cmd.Parameters.AddWithValue("$ocr", legacyRequest);
            cmd.Parameters.AddWithValue("$text", textRequest);
            cmd.ExecuteNonQuery();
        }
        var cache = new DictionaryEntryCache(path);
        Assert.Null(cache.ReadTranslations(legacyRequest));
        using (var db = Open(path))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT source_request FROM verified_translations WHERE length(cache_key)=64";
            Assert.Equal(textRequest, cmd.ExecuteScalar());
        }
        var stamp = cache.Read("legacy")!.CacheStamp!;
        Assert.Empty(cache.FindCompletions("old", null, "zh-Hans", default));
        var term = entry with { SourceText = "old school", ItemType = VocabularyItemType.Phrase };
        Assert.True(cache.TryReplace(stamp, term, 7));
        Assert.Equal("old school", Assert.Single(cache.FindCompletions("old", null, "zh-Hans", default)).Source);
        Assert.True(cache.Write("legacy", entry, 8));
        Assert.False(cache.TryReplace(stamp, term, 9));
        Assert.True(cache.Write("context", term with { LookupContext = "old school methods" }, 10));
        Assert.True(cache.Write("length", term with { SourceText = "old " + new string('x', 2000) }, 11));
        Assert.Empty(cache.FindCompletions("old", null, "zh-Hans", default));
        var offline = new DictionaryEntryCache(path);
        Assert.Equal(2004, offline.Read("length")!.SourceText.Length);
        Assert.Equal("旧句", offline.Read("legacy")!.PrimaryTranslation);
        cache.WriteTranslations(textRequest, ["过时"], 1);
        Assert.Equal(["首行"], offline.ReadTranslations(textRequest)!);
    }

    [Fact]
    public void UpgradeRemovesBothFormsOfScreenshotKeyAndPreservesTextInVersionOne()
    {
        var path = Path.Combine(_folder.FullName, "translations.sqlite3");
        var cache = new DictionaryEntryCache(path);
        cache.UpgradeExistingDatabase();
        Assert.False(File.Exists(path));
        using (var db = Open(path))
        {
            Execute(db, """
                CREATE TABLE verified_translations(cache_key TEXT PRIMARY KEY, entry TEXT, request_ticks INTEGER, source_request TEXT);
                INSERT INTO verified_translations VALUES
                    ('digest-of-screenshot','["旧译文"]',1,'{"mode":"regions","source":["example"]}'),
                    ('{"mode":"regions","source":["from older app"]}','["旧译文"]',2,NULL),
                    ('digest-of-text','["普通文本"]',3,'{"mode":"text","source":["regions"]}'),
                    ('unknown-key','["旧文本"]',4,NULL);
                PRAGMA user_version=1;
                """);
        }
        cache.UpgradeExistingDatabase();
        new DictionaryEntryCache(path).UpgradeExistingDatabase();
        using var migrated = Open(path);
        using var cmd = migrated.CreateCommand();
        cmd.CommandText = "SELECT cache_key FROM verified_translations ORDER BY cache_key";
        using var rows = cmd.ExecuteReader();
        var keys = new List<string>();
        while (rows.Read()) keys.Add(rows.GetString(0));
        Assert.Equal(["digest-of-text", "unknown-key"], keys);
    }
}

