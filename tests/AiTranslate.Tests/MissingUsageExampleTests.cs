using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class MissingUsageExampleTests
{
    [Fact]
    public async Task RepairsOnlyMissingRowsAndReopensWithTheSameTranslation()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(directory.FullName, "cache.db"));
            var service = new AiDictionaryService(() => new Stub("""
                {"primaryTranslation":"指望；银行","itemType":"Word","senses":[
                    {"definitionZh":"指望","grammarPattern":"bank on something"},
                    {"definitionZh":"银行","grammarPattern":"可数"}],
                "examples":[{"sourceSentence":"The bank opens at nine.","translatedSentence":"银行九点开门。","senseHint":"银行"}],
                "collocations":[{"phrase":"bank on","meaning":"指望"}]}
                """), cache);
            var entry = await service.LookupOrTranslateAsync("bank", "zh-Hans", "I bank on you.");
            var vocabulary = new VocabularyService(Path.Combine(directory.FullName, "words.json"));
            var collected = vocabulary.AddOrUpdateFromDictionary(entry);
            Assert.Equal(new[] { "sense-0", "phrase-0" }, entry.MissingUsageExamples().Select(g => g.Id));
            var repair = new AiDictionaryService(() => new Stub("""
                {"examples":[
                    {"id":"phrase-0","sourceSentence":"Count on it.","translatedSentence":"相信吧。","highlightTerm":"bank"},
                    {"id":"sense-1","sourceSentence":"A bank.","translatedSentence":"银行。","highlightTerm":"bank"},
                    {"id":"sense-0","sourceSentence":"We bank on you.","translatedSentence":"我们指望你。","highlightTerm":"bank on"},
                    {"id":"phrase-0","sourceSentence":"They bank on us.","translatedSentence":"他们指望我们。","highlightTerm":"bank on"}]}
                """, payload =>
                {
                    Assert.Equal("I bank on you.", payload["context_sentence"]?.GetValue<string>());
                    Assert.Equal(2, payload["items"]!.AsArray().Count);
                }), cache, refreshCollectedEntries: vocabulary.RefreshCollectedEntries);
            var result = await repair.CompleteExamplesAsync(entry, "I bank on you.", default);
            Assert.Equal(entry.PrimaryTranslation, result.PrimaryTranslation);
            Assert.Empty(result.MissingUsageExamples());
            Assert.Equal(entry.Senses[1], result.Senses[1]);
            var updatedCard = vocabulary.FindByHeadword("bank");
            Assert.Equal(collected.Id, updatedCard?.Id);
            Assert.Equal("他们指望我们。", updatedCard?.Collocations[0].Example?.TranslatedSentence);
            var offline = new AiDictionaryService(() => null, cache);
            var saved = await offline.LookupOrTranslateAsync("bank", "zh-Hans", "I bank on you.");
            Assert.Equal("他们指望我们。", saved.Collocations[0].Example?.TranslatedSentence);
            Assert.Same(saved, await offline.CompleteExamplesAsync(saved, null, default));
            Assert.False(cache.TryReplace(entry.CacheStamp!, entry, DateTime.UtcNow.Ticks));
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task InvalidSupplementsLeaveOriginalDataUsableAndPassagesHaveNoExampleQuota()
    {
        var entry = new DictionaryEntryResult
        {
            SourceText = "lead", PrimaryTranslation = "带领", Collocations = [new("lead the way", "带路")]
        };
        var service = new AiDictionaryService(() => new Stub("""{"examples":[{"id":"unknown"}]}"""));
        await Assert.ThrowsAsync<AiException>(() => service.CompleteExamplesAsync(entry, null, default));
        Assert.Null(entry.Collocations[0].Example);
        var sentence = entry with { ItemType = VocabularyItemType.Sentence };
        Assert.Same(sentence, await service.CompleteExamplesAsync(sentence, null, default));
    }

    private sealed class Stub(string reply, Action<JsonNode>? inspect = null) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            inspect?.Invoke(JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!);
            return Task.FromResult(new AiCompletion(reply, []));
        }
    }
}
