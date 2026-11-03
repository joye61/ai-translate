using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using Xunit;

namespace AiTranslate.Tests;

public sealed class LexicalCoverageTests
{
    private static readonly DictionaryEntryResult Entry = new()
    {
        SourceText = "take", PrimaryTranslation = "拿", TargetLanguage = "zh-Hans",
        Senses = [new("v.", "拿")], Inflections = [new("第三人称单数", "takes")],
        Collocations = [new("take off", "起飞")], UsageContrasts = [new("take A", "甲", "take B", "乙")]
    };

    [Fact]
    public async Task CoverageAppendsOnlySupportedGapsAndKeepsDifferentMeaningsOfAPhrase()
    {
        const string reply = """
            {"primaryTranslation":"替换尝试","senses":[
              {"partOfSpeech":"verb","definitionZh":"拿。","example":{"sourceSentence":"I take it.","translatedSentence":"我拿着。","highlightTerm":"take"}},
              {"partOfSpeech":"v.","definitionZh":"耗费","example":{"sourceSentence":"It takes an hour.","translatedSentence":"需要一小时。","highlightTerm":"takes"}},
              {"partOfSpeech":"v.","definitionZh":"无对应高亮","example":{"sourceSentence":"It takes an hour.","translatedSentence":"需要一小时。","highlightTerm":"take"}},
              {"partOfSpeech":"v.","definitionZh":"无译句","example":{"sourceSentence":"We take turns.","highlightTerm":"take"}}],
             "collocations":[
              {"phrase":"take off","meaning":"起飞","example":{"sourceSentence":"Planes take off.","translatedSentence":"飞机起飞。","highlightTerm":"take off"}},
              {"phrase":"make up","meaning":"组成","example":{"sourceSentence":"We make up a team.","translatedSentence":"我们组成一个队。","highlightTerm":"make up"}},
              {"phrase":"take off","meaning":"脱下","example":{"sourceSentence":"Please take off your gloves.","translatedSentence":"请脱下手套。","highlightTerm":"take off"}}],
             "usageContrasts":[
              {"leftForm":"take B","leftUse":"乙","rightForm":"take A","rightUse":"甲"},
              {"leftForm":"take time","leftUse":"花费时间","rightForm":"take your time","rightUse":"不必着急"}]}
            """;
        var replyTransport = new Reply(reply);
        var updated = await new AiDictionaryService(() => replyTransport).CheckLexicalCoverageAsync(Entry, "It takes an hour.", default);
        Assert.Equal(Entry.PrimaryTranslation, updated.PrimaryTranslation);
        Assert.Equal(new[] { "拿", "耗费" }, updated.Senses.Select(s => s.DefinitionZh));
        Assert.Equal(new[] { "起飞", "脱下" }, updated.Collocations.Select(s => s.Meaning));
        Assert.Equal(2, updated.UsageContrasts.Count);
        Assert.Single(Entry.Collocations);
        Assert.Equal("It takes an hour.", replyTransport.Payload!["context_sentence"]!.GetValue<string>());
        Assert.NotEmpty(replyTransport.Payload["senses"]!.AsArray());
    }

    [Fact]
    public async Task SupplementFillsOnlyMissingGrammarForKnownSensesAndTheirForms()
    {
        var service = new AiDictionaryService(() => new Reply("""
            {"senseGrammar":[{"partOfSpeech":"verb","definitionZh":"拿","grammarLabel":"及物"},
                {"partOfSpeech":"v.","definitionZh":"拿","grammarLabel":"不及物"},
                {"partOfSpeech":"v.","definitionZh":"不存在","grammarLabel":"不及物"}],
             "inflections":[{"partOfSpeech":"v.","label":"过去式","value":"took"},
                {"partOfSpeech":"verb","label":"过去分词","value":"taken"},
                {"partOfSpeech":"v.","label":"第三人称单数","value":"takes"},
                {"partOfSpeech":"adj.","label":"比较级","value":"more take"}]}
            """));
        var repaired = await service.CheckLexicalCoverageAsync(Entry, null, default);
        Assert.Equal("及物", Assert.Single(repaired.Senses).GrammarLabel);
        Assert.Equal(["takes", "took", "taken"], repaired.Inflections.Select(f => f.Value));
        Assert.Same(repaired, await service.CheckLexicalCoverageAsync(repaired, null, default));
    }

    [Fact]
    public async Task CoverageIsBoundedAndMalformedOrEmptyRepliesNeverReplaceAnEntry()
    {
        var additions = Enumerable.Range(1, 5).Select(i => new
        {
            partOfSpeech = "v.", definitionZh = $"义项{i}",
            example = new { sourceSentence = "I take it.", translatedSentence = "我来拿。", highlightTerm = "take" }
        });
        AiDictionaryService Service(string json) => new(() => new Reply(json));
        var full = await Service(System.Text.Json.JsonSerializer.Serialize(new { senses = additions }))
            .CheckLexicalCoverageAsync(Entry, null, default);
        Assert.Equal(Entry.Senses.Count + 3, full.Senses.Count);
        Assert.Same(Entry, await Service("""{"senses":[],"collocations":[],"usageContrasts":[]}""").CheckLexicalCoverageAsync(Entry, null, default));
        Assert.Same(Entry, await Service("""{"collocations":[false,{"phrase":"take back","meaning":"收回"}]}""").CheckLexicalCoverageAsync(Entry, null, default));
        await Assert.ThrowsAsync<AiException>(() => Service("""{"senses":42}""").CheckLexicalCoverageAsync(Entry, null, default));
        await Assert.ThrowsAsync<AiException>(() => Service("""{"primaryTranslation":"X"}""").CheckLexicalCoverageAsync(Entry, null, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service("").CheckLexicalCoverageAsync(Entry, null, new CancellationToken(true)));
    }

    private sealed class Reply(string json) : IAiTransport
    {
        public JsonNode? Payload { get; private set; }
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            Payload = JsonNode.Parse(messages[1]["content"]!.GetValue<string>());
            return Task.FromResult(new AiCompletion(json, []));
        }
    }
}
