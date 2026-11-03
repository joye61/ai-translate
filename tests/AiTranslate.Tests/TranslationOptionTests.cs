using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.Json;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class TranslationOptionTests
{
    [Fact]
    public async Task CompletesOnlyMissingOptionsInTheReadersLanguageAndKeepsTheOriginalEntry()
    {
        var calls = new List<JsonNode>();
        var transport = new Replies(calls,
            """
            {"primaryTranslation":"compare; contrast; comparison","detectedSourceLanguage":"ja","itemType":"Word",
             "senses":[{"partOfSpeech":"n.","definitionZh":"二つのものを比べること"}],
             "translationOptions":[{"text":"compare","meaning":"比べる","usage":"共通点と相違点の両方を見る"},
                {"text":"contrast","meaning":"対比する"},{"text":"contrast","meaning":"重複"}]}
            """,
            """
            {"translationOptions":[{"text":"comparison","meaning":"比較","usage":"比較という行為を表す名詞"},
                {"text":"contrast","meaning":"対比する","usage":"違いを際立たせる"},
                {"text":"unrelated","meaning":"無関係","usage":"含めない"}]}
            """);
        var service = new AiDictionaryService(() => transport, readerLanguageId: () => "ja");
        var entry = await service.LookupOrTranslateAsync("比較", "en");
        Assert.Equal(2, calls.Count);
        Assert.Equal("ja", calls[1]["reader_language"]!.GetValue<string>());
        Assert.Equal(["contrast", "comparison"], calls[1]["missing"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("compare; contrast; comparison", entry.PrimaryTranslation);
        Assert.Equal("二つのものを比べること", Assert.Single(entry.Senses).DefinitionZh);
        Assert.Equal(["compare", "contrast", "comparison"], entry.TranslationOptions.Select(o => o.Text));
        Assert.Equal(["比べる", "対比する", "比較"], entry.TranslationOptions.Select(o => o.Meaning));
    }

    [Fact]
    public async Task CompleteComparisonDoesNotNeedAnotherRequest()
    {
        var calls = new List<JsonNode>();
        var service = new AiDictionaryService(() => new Replies(calls, """
            {"primaryTranslation":"compare; contrast","translationOptions":[
              {"text":"compare","meaning":"比较","usage":"可讨论相同点和不同点"},
              {"text":"contrast","meaning":"对照","usage":"强调差别"}]}
            """), readerLanguageId: () => "zh-Hans");
        var entry = await service.LookupOrTranslateAsync("对比", "en");
        Assert.Single(calls);
        Assert.Equal(2, entry.TranslationOptions.Count);
    }

    [Fact]
    public async Task OlderEntriesStayReadableOfflineAndGainOnlyTheMissingComparisonWhenOnline()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "dictionary.sqlite3"));
            var original = new DictionaryEntryResult { SourceText = "对比", PrimaryTranslation = "compare; contrast",
                DetectedSourceLanguage = "zh", TargetLanguage = "en", Senses = [new("v.", "比较两者")] };
            var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                { schema = 3, source = "对比", target = "en", contextSentence = "", terms = Array.Empty<CollocationItem>() })));
            cache.Write(key, original, 1);
            var offline = new AiDictionaryService(() => null, cache, readerLanguageId: () => "zh-Hans");
            Assert.Equal(original.PrimaryTranslation, (await offline.LookupOrTranslateAsync("对比", "en")).PrimaryTranslation);

            var calls = new List<JsonNode>();
            var online = new AiDictionaryService(() => new Replies(calls, """
                {"translationOptions":[{"text":"compare","meaning":"比较","usage":"找出异同"},
                    {"text":"contrast","meaning":"对照","usage":"突出差异"}]}
                """), cache, readerLanguageId: () => "zh-Hans");
            var repaired = await online.LookupOrTranslateAsync("对比", "en");
            Assert.Equal(DictionaryResultOrigin.Supplemented, repaired.Origin);
            Assert.NotNull(Assert.Single(calls)["missing"]);
            var cached = await offline.LookupOrTranslateAsync("对比", "en");
            Assert.Equal(repaired.TranslationOptions, cached.TranslationOptions);
            Assert.Equal(original.Senses[0].DefinitionZh, Assert.Single(cached.Senses).DefinitionZh);
        }
        finally { dir.Delete(true); }
    }

    private sealed class Replies(List<JsonNode> calls, params string[] responses) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            calls.Add(JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!);
            return Task.FromResult(new AiCompletion(responses[calls.Count - 1], []));
        }
    }
}
