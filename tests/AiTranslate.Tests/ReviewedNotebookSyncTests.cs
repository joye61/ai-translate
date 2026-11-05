using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class ReviewedNotebookSyncTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CacheCommitSurvivesNotebookFailureAndNextLookupRepairsOnlyOlderCards(bool cached)
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(folder.FullName, "lookup.db"));
            var store = new VocabularyService(Path.Combine(folder.FullName, "cards.json"));
            var seed = new AiDictionaryService(() => null, cache, readerLanguageId: () => "zh-Hans");
            var original = cached ? await seed.LookupOrTranslateAsync("coffee", "zh-Hans") :
                new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡",
                    ReaderLanguage = "zh-Hans", Senses = [new("n.", "咖啡")] };
            var card = store.AddOrUpdateFromDictionary(original, sourceTitle: "菜单", userNote: "点单");
            var progress = store.SubmitReview(card.Id, ReviewRating.Good);
            var other = store.AddOrUpdateFromDictionary(original, store.CreateNotebook("其他词本", "").Id);
            VocabularyEntry ReadCard(string id) => store.QueryEntries(new() { SearchKeyword = "coffee" }).Single(c => c.Id == id);
            var calls = 0;
            var service = new AiDictionaryService(() => { calls++; return new ReviewReply(); }, cache,
                refreshCollectedEntries: (_, _) => throw new IOException("词本写入失败"), readerLanguageId: () => "zh-Hans");
            var reviewed = await service.ReviewBlockAsync(original, EntryBlock.Senses, default);
            Assert.True(reviewed.NotebookSyncPending);
            Assert.Equal(1, calls);
            Assert.Equal(ReviewReply.Definition, reviewed.Senses[0].DefinitionZh);
            Assert.Equal(original.Senses, ReadCard(card.Id).Senses);
            var stamp = Assert.IsType<DictionaryCacheStamp>(reviewed.CacheStamp);
            var fromDb = cache.Read(stamp.Key)!;
            Assert.Equal(stamp, fromDb.CacheStamp);
            Assert.Equal(reviewed.Senses, fromDb.Senses);
            Assert.False(fromDb.NotebookSyncPending);
            Assert.DoesNotContain(nameof(DictionaryEntryResult.NotebookSyncPending), JsonSerializer.Serialize(reviewed));
            Assert.True((await service.LookupOrTranslateAsync("coffee", "zh-Hans", cacheOnly: true)).NotebookSyncPending);
            Assert.Equal(1, calls);

            // A concurrent, newer card must keep its revision during repair.
            var newer = store.SaveEntry(ReadCard(other.Id) with { DictionaryRevision = stamp.Revision + 1, Senses = [new("n.", "新释义")] });
            var recovered = new AiDictionaryService(() => throw new Exception("缓存修复不应请求模型"), cache,
                refreshCollectedEntries: store.RefreshCollectedEntries, readerLanguageId: () => "zh-Hans");
            var repaired = await recovered.LookupOrTranslateAsync("coffee", "zh-Hans");
            Assert.False(repaired.NotebookSyncPending);
            Assert.Equal(stamp, repaired.CacheStamp);
            var saved = ReadCard(card.Id);
            Assert.Equal(stamp.Revision, saved.DictionaryRevision);
            Assert.Equal(reviewed.Senses, saved.Senses);
            Assert.Equal("点单", saved.UserPersonalNote);
            Assert.Equal("菜单", saved.SourceTitle);
            Assert.Equal(progress.NextReviewAt, saved.NextReviewAt);
            Assert.Equal(newer.DictionaryRevision, ReadCard(other.Id).DictionaryRevision);
            Assert.Equal(newer.Senses, ReadCard(other.Id).Senses);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task NotebookFailureWithoutADurableCacheIsStillAFailedReview()
    {
        var entry = new DictionaryEntryResult { SourceText = "coffee", PrimaryTranslation = "咖啡", Senses = [new("n.", "咖啡")] };
        var service = new AiDictionaryService(() => new ReviewReply(), refreshCollectedEntries: (_, _) => throw new IOException("写入失败"));
        await Assert.ThrowsAsync<IOException>(() => service.ReviewBlockAsync(entry, EntryBlock.Senses, default));
    }

    internal sealed class ReviewReply : IAiTransport
    {
        internal const string Definition = "咖啡（用咖啡豆冲泡的饮品）";

        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token) =>
            Task.FromResult(new AiCompletion($$"""{"status":"improved","edits":[{"path":"/senses/0/definitionZh","value":"{{Definition}}"}]}""", []));
    }
}
