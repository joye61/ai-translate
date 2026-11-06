using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class BlockReviewRecoveryTests
{
    private static readonly DictionaryEntryResult Coffee = new()
    {
        SourceText = "coffee", DetectedSourceLanguage = "en", PrimaryTranslation = "咖啡",
        Senses = [new("n.", "咖啡")],
        WordFamily = [new("咖啡壶", "coffeepot", "n.", "derived", "盛咖啡的壶", "a coffeepot")],
        Processing = new() { Core = DictionaryStageState.Completed }
    };

    // A real failure: the provider puts an open compound into the single-word section.
    private const string MisclassifiedReply = """
        {"status":"improved","edits":[
          {"path":"/wordFamily/0/usage","value":"用于煮咖啡或盛咖啡的壶。"},
          {"path":"/wordFamily/-","value":{"label":"咖啡店","value":"coffee shop","partOfSpeech":"n.",
            "relation":"derived","usage":"售卖咖啡的店铺。","expression":"a coffee shop"}}]}
        """;
    private const string CorrectedReply = """
        {"status":"improved","edits":[{"path":"/wordFamily/0/usage","value":"用于煮咖啡或盛咖啡的壶。"}]}
        """;

    [Fact]
    public async Task RepairsTheCurrentContractAgainstTheOriginalSnapshotAndSavesOnlyTheCorrection()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.db"));
            Assert.True(cache.TryAdd("coffee", Coffee, 1));
            var original = cache.Read("coffee")!;
            var calls = 0;
            var frames = new List<DictionaryEntryResult>();
            var transport = new Reply((messages, emit, _) =>
            {
                calls++;
                var current = JsonNode.Parse((string)messages[1]["content"]!)!["current_section"]!;
                Assert.Equal("盛咖啡的壶", (string?)current["wordFamily"]![0]!["usage"]);
                Assert.Equal(original.CacheStamp, cache.Read("coffee")!.CacheStamp);
                Assert.Empty(cache.Read("coffee")!.Processing!.SectionChecks);
                if (calls == 1)
                {
                    // A valid prefix can preview before a later operation invalidates the response.
                    emit(CorrectedReply);
                    return new(MisclassifiedReply, []);
                }
                Assert.Equal(4, messages.Count);
                Assert.Equal(original.WordFamily, frames[^1].WordFamily);
                Assert.Equal(original.CacheStamp, frames[^1].CacheStamp);
                Assert.Equal(MisclassifiedReply, (string?)messages[2]["content"]);
                var feedback = JsonNode.Parse((string)messages[3]["content"]!)!;
                Assert.Equal("wordFamily", (string?)feedback["field"]);
                Assert.Contains("单个单词", (string)feedback["validation_error"]!);
                Assert.Single(feedback["accepted_content"]!.AsArray());
                return new(CorrectedReply, []);
            });
            var result = await new AiDictionaryService(() => transport, cache)
                .ReviewBlockAsync(original, EntryBlock.WordFamily, default, frames.Add);
            Assert.Equal(2, calls);
            Assert.True(transport.Disposed);
            Assert.Equal("用于煮咖啡或盛咖啡的壶。", Assert.Single(result.WordFamily).Usage);
            Assert.Equal(result.WordFamily, cache.Read("coffee")!.WordFamily);
            Assert.Single(result.Processing!.SectionChecks);
            Assert.Equal(original.WordFamily, Coffee.WordFamily);
        }
        finally { dir.Delete(true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"status\":\"improved\",\"edits\":[")]
    [InlineData(MisclassifiedReply)]
    public async Task ExhaustedRecoveryShowsActionableFeedbackWithoutSavingAFailedReview(string reply)
    {
        var calls = 0;
        var saved = false;
        var service = new AiDictionaryService(() => new Reply((_, _, _) => { calls++; return new(reply, []); }),
            refreshCollectedEntries: (_, _) => saved = true);
        var error = await Assert.ThrowsAsync<AiException>(() => service.ReviewBlockAsync(Coffee, EntryBlock.WordFamily, default));
        Assert.Equal(2, calls);
        Assert.Equal("本次复核未完成，请稍后重试。", error.Message);
        Assert.NotNull(error.InnerException);
        Assert.DoesNotContain("原词条不符", error.Message);
        Assert.Empty(Coffee.Processing!.SectionChecks);
        Assert.False(saved);
    }

    [Fact]
    public async Task ACorrectedUnchangedReplyDoesNotCommitTheRejectedPreview()
    {
        var calls = 0;
        var frames = new List<DictionaryEntryResult>();
        var service = new AiDictionaryService(() => new Reply((_, emit, _) =>
        {
            if (++calls == 1) { emit(CorrectedReply); return new(MisclassifiedReply, []); }
            return new("""{"status":"unchanged"}""", []);
        }));
        var result = await service.ReviewBlockAsync(Coffee, EntryBlock.WordFamily, default, frames.Add);
        Assert.Equal(Coffee.WordFamily, result.WordFamily);
        Assert.Same(Coffee, frames[^1]);
        Assert.False(Assert.Single(result.Processing!.SectionChecks).Changed);
        Assert.Same(result, await service.ReviewBlockAsync(result, EntryBlock.WordFamily, default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TransportFailuresAreNotRetriedAsContentCorrections()
    {
        var calls = 0;
        var service = new AiDictionaryService(() => new Reply((_, _, _) =>
        {
            calls++;
            throw new AiException("连接中断");
        }));
        var error = await Assert.ThrowsAsync<AiException>(() => service.ReviewBlockAsync(Coffee, EntryBlock.WordFamily, default));
        Assert.Equal("连接中断", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancellationBeforeRecoveryDoesNotSendAnotherRequest()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var transport = new Reply((_, _, _) => { calls++; cancellation.Cancel(); return new(MisclassifiedReply, []); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiDictionaryService(() => transport)
            .ReviewBlockAsync(Coffee, EntryBlock.WordFamily, cancellation.Token));
        Assert.Equal(1, calls);
        Assert.True(transport.Disposed);
    }

    private sealed class Reply(Func<IReadOnlyList<JsonObject>, Action<string>, CancellationToken, AiCompletion> respond)
        : IAiTransport, IDisposable
    {
        public bool Disposed { get; private set; }
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText,
            CancellationToken token) => Task.FromResult(respond(messages, onText, token));
        public void Dispose() => Disposed = true;
    }
}
