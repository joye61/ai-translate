using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class VerifiedTranslationCacheTests
{
    [Fact]
    public async Task IdenticalScreenshotsRequestNewTranslationsAndVerificationWithoutOpeningTheCache()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "lookup.sqlite3");
            var replies = new Replies(
                """{"regions":[{"id":"a","text":"OpenAI"},{"id":"b","text":"打开"}]}""",
                """{"regions":[{"id":"a","reason":"protected_content"}]}""",
                """{"regions":[{"id":"a","text":"OpenAI"},{"id":"b","text":"开启"}]}""",
                """{"regions":[{"id":"a","reason":"protected_content"}]}""",
                """{"regions":[]}""");
            var online = new AiTranslationService(replies, new DictionaryEntryCache(path));
            TranslationSourceRegion[] regions = [new("a", "OpenAI"), new("b", "Open")];
            var first = await online.TranslateRegionsAsync(regions, "zh-Hans", default);
            Assert.Equal(new TranslationRegionText("b", "打开"), first[1]);
            Assert.Equal(2, replies.Calls);
            var next = await online.TranslateRegionsAsync(regions, "zh-Hans", default);
            Assert.Equal(new TranslationRegionText("b", "开启"), next[1]);
            Assert.Equal(4, replies.Calls);
            await Assert.ThrowsAsync<AiException>(() => online.TranslateRegionsAsync(regions, "zh-Hans", default, forceRefresh: true));
            Assert.Equal(5, replies.Calls);
            var offline = new AiTranslationService(null, new DictionaryEntryCache(path));
            await Assert.ThrowsAsync<AiException>(() => offline.TranslateRegionsAsync(regions, "zh-Hans", default));
            await Assert.ThrowsAsync<AiException>(() => offline.TranslateRegionsAsync([new("a", "coffee")], "zh-Hans", default));
            Assert.False(File.Exists(path));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task TextRefreshReplacesPreviousTranslationAndRejectedReviewNeverBecomesAHit()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "lookup.sqlite3"));
            var replies = new Replies("草稿", "初稿", "draft", "{}");
            var service = new AiTranslationService(replies, cache);
            Assert.Equal("草稿", await service.TranslateAsync("draft", "zh-Hans", default));
            Assert.Equal("草稿", await service.TranslateAsync("draft", "zh-Hans", default));
            Assert.Equal(1, replies.Calls);
            Assert.Equal("初稿", await service.TranslateAsync("draft", "zh-Hans", default, forceRefresh: true));
            await Assert.ThrowsAsync<AiException>(() => service.TranslateAsync("draft", "zh-Hans", default, forceRefresh: true));
            Assert.Equal("初稿", await service.TranslateAsync("draft", "zh-Hans", default));
            Assert.Equal(4, replies.Calls);
        }
        finally { dir.Delete(true); }
    }

    private sealed class Replies(params string[] responses) : IAiTransport
    {
        public int Calls { get; private set; }
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            var reply = responses[Calls++];
            return Task.FromResult(new AiCompletion(reply, []));
        }
    }
}
