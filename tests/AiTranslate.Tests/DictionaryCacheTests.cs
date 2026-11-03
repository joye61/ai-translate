using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class DictionaryCacheTests
{
    [Fact]
    public async Task OverlappingPopupAndWorkbenchLookupsMakeOneRequestAndCanCancelIndependently()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var transport = new DelayedReply();
            var service = new AiDictionaryService(() => transport, new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.sqlite3")));
            var first = service.LookupOrTranslateAsync("same word", "zh-Hans");
            await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancel = new CancellationTokenSource();
            var canceled = service.LookupOrTranslateAsync("same word", "zh-Hans", cancellationToken: cancel.Token);
            var second = service.LookupOrTranslateAsync("same word", "zh-Hans");
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            transport.Finish.SetResult(new AiCompletion("""{"primaryTranslation":"同一个词"}""", []));
            Assert.Equal(DictionaryResultOrigin.Ai, (await first).Origin);
            Assert.Equal(DictionaryResultOrigin.Cache, (await second).Origin);
            Assert.Equal(1, transport.Calls);
        }
        finally { dir.Delete(true); }
    }

    private sealed class DelayedReply : IAiTransport
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<AiCompletion> Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            return Finish.Task.WaitAsync(token);
        }
    }

    [Fact]
    public async Task CachePersistsAcrossRestartsAndRefreshOnlyReplacesSuccessfulResults()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "cache.sqlite3");
            AiDictionaryService Service(string translation) => new(() => new Reply(translation), new DictionaryEntryCache(path));
            var first = await Service("河岸").LookupOrTranslateAsync("bank", "zh-Hans", "We sat by the water.");
            Assert.Equal("日常 · 地理", first.Senses[0].Scenario);
            Assert.Equal("bank on sth", first.Senses[0].GrammarPattern);
            var offline = new AiDictionaryService(() => null, new DictionaryEntryCache(path));
            var cached = await offline.LookupOrTranslateAsync("bank", "zh-Hans", "We sat by the water.");
            Assert.Equal(DictionaryResultOrigin.Cache, cached.Origin);
            Assert.Equal(first.Senses[0], Assert.Single(cached.Senses));
            Assert.Equal("河岸", cached.PrimaryTranslation);
            await Assert.ThrowsAsync<AiException>(() => offline.LookupOrTranslateAsync("Bank", "zh-Hans", "We sat by the water."));
            await Assert.ThrowsAsync<AiException>(() => offline.LookupOrTranslateAsync("bank", "en", "We sat by the water."));
            await Assert.ThrowsAsync<AiException>(() => offline.LookupOrTranslateAsync("bank", "zh-Hans", "She deposited cash."));
            var fresh = await Service("岸边").LookupOrTranslateAsync("bank", "zh-Hans", "We sat by the water.", forceRefresh: true);
            Assert.Equal(DictionaryResultOrigin.Ai, fresh.Origin);
            await Assert.ThrowsAsync<AiException>(() => offline.LookupOrTranslateAsync("bank", "zh-Hans", "We sat by the water.", forceRefresh: true));
            Assert.Equal("岸边", (await offline.LookupOrTranslateAsync("bank", "zh-Hans", "We sat by the water.")).PrimaryTranslation);

            var cache = new DictionaryEntryCache(path);
            cache.Write("overlap", fresh, 2);
            cache.Write("overlap", first, 1);
            Assert.Equal("岸边", cache.Read("overlap")?.PrimaryTranslation);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task GlossaryIsBoundedToThisTextAndChangesInvalidateSentenceTranslations()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var vocab = new VocabularyService(Path.Combine(dir.FullName, "words.json"));
            var term = new DictionaryEntryResult { SourceText = "charge port", PrimaryTranslation = "充电端口", ItemType = VocabularyItemType.TechnicalTerm };
            var saved = vocab.AddOrUpdateFromDictionary(term, userNote: "private note");
            const string input = "Check the charge port.";
            var requests = 0;
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.sqlite3"));
            var service = new AiDictionaryService(() => new Reply("检查充电口。", request =>
            {
                requests++;
                Assert.Equal(input, request["source_text"]?.GetValue<string>());
                Assert.Single(request["glossary"]!.AsArray());
                Assert.DoesNotContain("private note", request.ToJsonString());
            }, "Sentence"), cache, vocab.FindGlossaryTerms);
            Assert.Empty(vocab.FindGlossaryTerms("One recharge port.", "zh-Hans"));
            Assert.Empty(vocab.FindGlossaryTerms(input, "fr"));
            var first = await service.LookupOrTranslateAsync(input, "zh-Hans");
            Assert.Equal("充电端口", Assert.Single(first.GlossaryReferences).Meaning);
            Assert.Equal(DictionaryResultOrigin.Cache, (await service.LookupOrTranslateAsync(input, "zh-Hans")).Origin);
            vocab.SaveEntry(saved with { Translation = "充电口" });
            var second = await service.LookupOrTranslateAsync(input, "zh-Hans");
            Assert.Equal(2, requests);
            Assert.Equal("充电口", Assert.Single(second.GlossaryReferences).Meaning);

            var paragraph = new AiDictionaryService(() => new Reply("甲。\n\n乙。", itemType: "Paragraph"), cache);
            await paragraph.LookupOrTranslateAsync("A.\n\nB.", "zh-Hans");
            var cachedParagraph = await new AiDictionaryService(() => throw new InvalidOperationException("Unexpected API request"), cache)
                .LookupOrTranslateAsync("A.\r\n\r\nB.", "zh-Hans");
            Assert.Equal(DictionaryResultOrigin.Cache, cachedParagraph.Origin);
            Assert.Equal("甲。\n\n乙。", cachedParagraph.PrimaryTranslation);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task AutoDirectionUsesTheResolvedTargetForGlossaryRequestResultAndCache()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.sqlite3"));
            var service = new AiDictionaryService(() => new Reply("tomorrow", payload =>
                Assert.Equal("en", payload["target_language"]?.GetValue<string>())), cache, (_, target) =>
            {
                Assert.Equal("en", target);
                return [];
            });
            var result = await service.LookupOrTranslateAsync("明天", "auto");
            Assert.Equal("en", result.TargetLanguage);
            var offline = new AiDictionaryService(() => null, cache);
            Assert.Equal(DictionaryResultOrigin.Cache, (await offline.LookupOrTranslateAsync("明天", "en")).Origin);
            await Assert.ThrowsAsync<AiException>(() => offline.LookupOrTranslateAsync("明天", "zh-Hans"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task RefreshUpdatesTheSameCardsAcrossBooksAndPreservesTheirReviewHistory()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "words.json");
            var vocab = new VocabularyService(path);
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.sqlite3"));
            AiDictionaryService Service(string translation) => new(() => new Reply(translation), cache,
                refreshCollectedEntries: vocab.RefreshCollectedEntries);
            const string context = "We sat by the water.";
            var first = await Service("河岸").LookupOrTranslateAsync("bank", "zh-Hans", context);
            var cached = await Service("unused").LookupOrTranslateAsync("bank", "zh-Hans", context);
            var secondBook = vocab.CreateNotebook("阅读", "");
            var card = vocab.AddOrUpdateFromDictionary(cached, contextSentence: context, userNote: "我的笔记");
            var other = vocab.AddOrUpdateFromDictionary(first, secondBook.Id);
            vocab.ToggleStar(card.Id);
            var reviewed = vocab.SubmitReview(card.Id, ReviewRating.Good);
            var bank = await Service("银行").LookupOrTranslateAsync("bank", "zh-Hans", "She deposited cash.");
            var separate = vocab.AddOrUpdateFromDictionary(bank);

            var fresh = await Service("岸边").LookupOrTranslateAsync("bank", "zh-Hans", context, forceRefresh: true);
            vocab.RefreshCollectedEntries(first, first.CacheStamp!.Revision); // a late notification
            await Assert.ThrowsAsync<AiException>(() => new AiDictionaryService(() => null, cache,
                refreshCollectedEntries: vocab.RefreshCollectedEntries).LookupOrTranslateAsync("bank", "zh-Hans", context, forceRefresh: true));
            var cards = new VocabularyService(path).QueryEntries(new() { SearchKeyword = "bank" });
            var updated = Assert.Single(cards, e => e.Id == card.Id);
            Assert.Equal("岸边", updated.Translation);
            Assert.Equal(fresh.Senses[0], updated.Senses[0]);
            Assert.Equal("岸边", Assert.Single(cards, e => e.Id == other.Id).Translation);
            Assert.Equal("银行", Assert.Single(cards, e => e.Id == separate.Id).Translation);
            Assert.Equal(reviewed.NextReviewAt, updated.NextReviewAt);
            Assert.Equal(reviewed.TotalReviews, updated.TotalReviews);
            Assert.True(updated.IsStarred);
            Assert.Equal("我的笔记", updated.UserPersonalNote);
            Assert.Equal(context, updated.ContextSentence);
            vocab.RemoveFromNotebook(fresh, secondBook.Id);
            await Service("河边").LookupOrTranslateAsync("bank", "zh-Hans", context, forceRefresh: true);
            Assert.False(vocab.IsCollected(fresh, secondBook.Id));
            Assert.True(vocab.IsCollected(fresh, "default"));
        }
        finally { dir.Delete(true); }
    }

    private sealed class Reply(string translation, Action<JsonNode>? inspect = null, string itemType = "Word") : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            inspect?.Invoke(JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!);
            var text = JsonSerializer.Serialize(new
            {
                primaryTranslation = translation, itemType,
                senses = new[] { new { partOfSpeech = "n.", definitionZh = translation, scenario = new[] { "日常", "地理" }, grammarPattern = "bank on sth" } }
            });
            onText(text);
            return Task.FromResult(new AiCompletion(text, []));
        }
    }
}
