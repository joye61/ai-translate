using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
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

    [Theory]
    [InlineData("", "在场的；礼物")]
    [InlineData("Everyone was present.", "在场的")]
    public async Task NormalLookupRecoversMissingPartsAndKeepsTheContextualTranslation(string context, string expected)
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(folder.FullName, "lookup.db"));
            var requests = new List<JsonNode>();
            var service = new AiDictionaryService(() => new LookupReply(request =>
            {
                requests.Add(request);
                if ((string?)request["role"] == "learning") return PipelineReplies.Proposal(request.AsObject(), "not_applicable");
                if (request["role"] is null)
                    return """{"primaryTranslation":"在场的","itemType":"Word","detectedSourceLanguage":"en","senses":[{"partOfSpeech":"adj.","definitionZh":"在场的"}]}""";
                var proposal = JsonNode.Parse(PipelineReplies.Proposal(request.AsObject(), "corrected", issues: new[] {
                    new { field = "senses", sourceQuote = "present", reason = "遗漏常见名词义。", severity = "major" } }))!;
                proposal["lexicalAdditions"] = JsonNode.Parse("""
                        {"senses":[{"partOfSpeech":"n.","definitionZh":"礼物","headTranslation":"礼物",
                          "example":{"sourceSentence":"The presents were wrapped.","translatedSentence":"那些礼品全都包好了。","highlightTerm":"presents","translationQuote":"礼品","definitionQuote":"礼物"}},
                          {"partOfSpeech":"v.","definitionZh":"提出","headTranslation":"提出","example":{
                            "sourceSentence":"We present the award.","translatedSentence":"我们颁奖。","highlightTerm":"present","translationQuote":"颁奖"}}],
                         "inflections":[{"partOfSpeech":"noun","label":"复数","value":"presents"}],
                         "collocations":[{"phrase":"at present","meaning":"目前","example":{
                            "sourceSentence":"We have no vacancies at present.","translatedSentence":"我们目前没有空缺。","highlightTerm":"at present"}}]}
                        """);
                return proposal.ToJsonString();
            }), cache, readerLanguageId: () => "zh-Hans");
            var result = await service.LookupOrTranslateAsync("present", "zh-Hans", context);
            Assert.Equal(3, requests.Count);
            var reference = requests[2]["english_reference"]!;
            Assert.Equal(["n.", "v."], reference["missing_parts"]!.AsArray().Select(p => p!.GetValue<string>()));
            Assert.False(string.IsNullOrWhiteSpace(reference["usage_clues"]!["n."]!.GetValue<string>()));
            Assert.Equal(context, requests[2]["context_sentence"]!.GetValue<string>());
            Assert.Equal(expected, result.PrimaryTranslation);
            Assert.True(result.LexicalCoverageReviewed);
            Assert.Equal(["adj.", "n."], result.Senses.Select(s => s.PartOfSpeech));
            Assert.Equal("at present", Assert.Single(result.Collocations).Phrase);
            var cached = await service.LookupOrTranslateAsync("present", "zh-Hans", context);
            Assert.Equal(DictionaryResultOrigin.Cache, cached.Origin);
            Assert.Equal(result.Senses, cached.Senses);
            Assert.Equal(3, requests.Count);
            var books = new VocabularyService(Path.Combine(folder.FullName, "books.json"));
            Assert.Equal(result.Collocations, books.AddOrUpdateFromDictionary(cached).ToDictionaryResult().Collocations);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task IncompleteReviewKeepsFirstTranslationAndRetriesOnlyTheReview()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(folder.FullName, "lookup.db"));
            var calls = 0;
            var service = new AiDictionaryService(() => new LookupReply(request =>
            {
                calls++;
                if (request["role"] is null) return """{"primaryTranslation":"猫","itemType":"Word","detectedSourceLanguage":"fr"}""";
                Assert.Null(request["english_reference"]);
                if ((string?)request["role"] == "learning") return PipelineReplies.Proposal(request.AsObject(), "not_applicable");
                Assert.NotNull(request["entry"]!["senses"]);
                // Exhaust the one automatic repair before testing the explicit retry from cache.
                return calls <= 4 ? """{"status":"accepted"}""" : PipelineReplies.Proposal(request.AsObject(), "accepted");
            }), cache, readerLanguageId: () => "zh-Hans");
            var first = await service.LookupOrTranslateAsync("chat", "zh-Hans");
            Assert.Equal("猫", first.PrimaryTranslation);
            Assert.False(first.LexicalCoverageReviewed);
            Assert.Equal(4, calls);
            var offline = await new AiDictionaryService(() => null, cache, readerLanguageId: () => "zh-Hans").LookupOrTranslateAsync("chat", "zh-Hans");
            Assert.False(offline.LexicalCoverageReviewed);
            var reviewed = await service.ReviewEntryAsync(offline, default);
            Assert.True(reviewed.LexicalCoverageReviewed);
            Assert.Equal(first.PrimaryTranslation, reviewed.PrimaryTranslation);
            Assert.Empty(reviewed.Collocations);
            Assert.True((await service.LookupOrTranslateAsync("chat", "zh-Hans")).LexicalCoverageReviewed);
            Assert.Equal(5, calls);
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task CancelDuringReviewDoesNotSaveAnUnfinishedLookup()
    {
        using var cancel = new CancellationTokenSource();
        var calls = 0;
        var service = new AiDictionaryService(() => new LookupReply(request =>
        {
            calls++;
            if (request["role"] is null) return """{"primaryTranslation":"书","itemType":"Word"}""";
            cancel.Cancel();
            return """{"senses":[],"collocations":[]}""";
        }), refreshCollectedEntries: (_, _) => Assert.Fail("A cancelled lookup reached the notebook."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LookupOrTranslateAsync("book", "zh-Hans", cancellationToken: cancel.Token));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CoverageKeepsAllSupportedRowsAndMalformedOrEmptyRepliesNeverReplaceAnEntry()
    {
        var phrases = new (string Phrase, string Meaning, string Source, string Target)[]
        {
            ("take care of", "照顾", "I take care of my sister.", "我照顾妹妹。"),
            ("take part in", "参加", "We take part in local races.", "我们参加当地的比赛。"),
            ("take a break", "休息一下", "Let's take a break.", "我们休息一下吧。"),
            ("take place", "举行", "The talks take place in May.", "会谈于五月举行。"),
            ("take into account", "考虑到", "We take into account the cost.", "我们把成本考虑在内。")
        };
        var additions = phrases.Select(p => new { phrase = p.Phrase, meaning = p.Meaning,
            example = new { sourceSentence = p.Source, translatedSentence = p.Target, highlightTerm = p.Phrase } });
        AiDictionaryService Service(string json) => new(() => new Reply(json));
        var full = await Service(System.Text.Json.JsonSerializer.Serialize(new { collocations = additions }))
            .CheckLexicalCoverageAsync(Entry, null, default);
        Assert.Equal(phrases.Select(p => p.Phrase), full.Collocations.Skip(Entry.Collocations.Count).Select(p => p.Phrase));
        Assert.Same(Entry, await Service("""{"senses":[],"collocations":[],"usageContrasts":[]}""").CheckLexicalCoverageAsync(Entry, null, default));
        Assert.Same(Entry, await Service("""{"collocations":[false,{"phrase":"take back","meaning":"收回"}]}""").CheckLexicalCoverageAsync(Entry, null, default));
        await Assert.ThrowsAsync<AiException>(() => Service("""{"senses":42}""").CheckLexicalCoverageAsync(Entry, null, default));
        await Assert.ThrowsAsync<AiException>(() => Service("""{"primaryTranslation":"X"}""").CheckLexicalCoverageAsync(Entry, null, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service("").CheckLexicalCoverageAsync(Entry, null, new CancellationToken(true)));
    }

    private sealed class LookupReply(Func<JsonNode, string> respond) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token) =>
            Task.FromResult(new AiCompletion(respond(JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!), []));
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
