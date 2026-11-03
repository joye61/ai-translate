using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class VerifiedTranslationCacheTests
{
    [Fact]
    public async Task ScreenshotReopensOfflineWithNewRegionIdsAndOnlySuccessfulRefreshReplacesIt()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "lookup.sqlite3");
            var replies = new Replies(
                """{"regions":[{"id":"a","text":"OpenAI"},{"id":"b","text":"打开"}]}""",
                """{"regions":[{"id":"a","reason":"protected_content"}]}""",
                """{"regions":[{"id":"x","text":"OpenAI"},{"id":"y","text":"开启"}]}""",
                """{"regions":[{"id":"x","reason":"protected_content"}]}""",
                """{"regions":[]}""");
            var online = new AiTranslationService(replies, new DictionaryEntryCache(path));
            TranslationSourceRegion[] first = [new("a", "OpenAI"), new("b", "Open")];
            TranslationSourceRegion[] next = [new("x", "OpenAI"), new("y", "Open")];
            await online.TranslateRegionsAsync(first, "zh-Hans", default);
            Assert.Equal(2, replies.Calls);
            var hit = await online.TranslateRegionsAsync(next, "zh-Hans", default);
            Assert.Equal(new TranslationRegionText("y", "打开"), hit[1]);
            Assert.Equal(2, replies.Calls);

            await online.TranslateRegionsAsync(next, "zh-Hans", default, forceRefresh: true);
            await Assert.ThrowsAsync<AiException>(() => online.TranslateRegionsAsync(next, "zh-Hans", default, forceRefresh: true));
            var offline = new AiTranslationService(null, new DictionaryEntryCache(path));
            Assert.Equal("开启", (await offline.TranslateRegionsAsync(first, "zh-Hans", default))[1].Text);
            Assert.Equal(5, replies.Calls);
            await Assert.ThrowsAsync<AiException>(() => offline.TranslateRegionsAsync(first.Reverse().ToArray(), "zh-Hans", default));
            await Assert.ThrowsAsync<AiException>(() => offline.TranslateRegionsAsync(first, "ja", default));
            await Assert.ThrowsAsync<AiException>(() => offline.TranslateRegionsAsync([first[0], first[0]], "zh-Hans", default));
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
