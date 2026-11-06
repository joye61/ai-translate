using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class GrammarTextTests
{
    [Theory]
    [InlineData("过去进行时的用法", "过去进行时")]
    [InlineData("过去进行时态", "过去进行时")]
    [InlineData(" 过去进行时 讲解 ", "过去进行时")]
    [InlineData("Past Continuous", "pastcontinuous")]
    [InlineData("was/were + doing", "waswere" + "doing")]
    public void NormalizeCollapsesWordingVariants(string input, string expected) =>
        Assert.Equal(expected, GrammarText.Normalize(input));

    [Fact]
    public void NormalizeKeepsShortNamesIntact() => Assert.Equal("语法", GrammarText.Normalize("语法"));

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("zh-Hant", "zh")]
    [InlineData("auto", "en")]
    [InlineData(null, "en")]
    [InlineData("ja", "ja")]
    public void LanguageCodeUsesPrimarySubtag(string? detected, string expected) =>
        Assert.Equal(expected, GrammarText.LanguageCode(detected));
}

public sealed class GrammarLibraryTests
{
    private static GrammarTopic Topic(string title, params string[] aliases) => new()
    {
        Title = title, Category = "时态", Summary = $"{title}的一句话", ReaderLanguage = "zh-Hans", Aliases = aliases,
        Usages = [new("用法一", "说明")]
    };

    [Fact]
    public void FindsByTitleAndLearnedAliasAndSurvivesReopen()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "grammar.sqlite3");
        var library = new GrammarLibrary(path);
        library.Save(Topic("过去进行时", "过去进行式", "Past Continuous"), ["was/were doing"]);

        var reopened = new GrammarLibrary(path);
        Assert.Equal(1, reopened.Count);
        foreach (var name in new[] { "过去进行时", "过去进行时的用法", "过去进行式", "past continuous", "WAS/WERE DOING" })
            Assert.Equal("过去进行时", reopened.Find("zh-Hans", null, name)?.Title);
        Assert.Equal(GrammarOrigin.Library, reopened.Find("zh-Hans", "en", "过去进行时")!.Origin);
        Assert.Null(reopened.Find("zh-Hans", "ja", "过去进行时"));
        Assert.Null(reopened.Find("en", null, "过去进行时"));
        Assert.True(reopened.Contains("zh-Hans", null, "过去进行式"));
        Assert.Equal("过去进行时", reopened.ResolveTitle("zh-Hans", null, "Past Continuous"));
    }

    [Fact]
    public void AliasNeverStealsAnotherGrammarPointsTitle()
    {
        using var folder = new TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(Topic("过去进行时"), []);
        library.Save(Topic("过去完成时", "过去进行时"), []);

        Assert.Equal("过去进行时", library.Find("zh-Hans", null, "过去进行时")!.Title);
        Assert.Equal("过去完成时", library.Find("zh-Hans", null, "过去完成时")!.Title);
    }

    [Fact]
    public void SearchRanksExactBeforeContainmentAndIgnoresUnrelated()
    {
        using var folder = new TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(Topic("过去进行时"), []);
        library.Save(Topic("过去进行时与一般过去时的区别"), []);
        library.Save(Topic("定语从句"), []);

        Assert.Equal(["过去进行时", "过去进行时与一般过去时的区别"],
            library.Search("zh-Hans", "过去进行时", 5).Select(s => s.Title));
        Assert.Equal("过去进行时", library.Search("zh-Hans", "请问过去进行时怎么用", 5).First().Title);
        Assert.Empty(library.Search("zh-Hans", "虚拟语气", 5));
    }

    [Fact]
    public void SavingAgainKeepsUseCountsAndRemoveDeletesAliases()
    {
        using var folder = new TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(Topic("过去进行时", "过去进行式"), []);
        library.Find("zh-Hans", null, "过去进行时");
        library.Find("zh-Hans", null, "过去进行时");
        library.Save(Topic("过去进行时", "过去进行式") with { Summary = "新的一句话" }, []);

        Assert.Equal(2, library.Recent("zh-Hans", 5).Single().Hits);
        Assert.Equal("新的一句话", library.Recent("zh-Hans", 5).Single().Summary);
        library.Remove("zh-Hans", "en", "过去进行时");
        Assert.Equal(0, library.Count);
        Assert.Null(library.Find("zh-Hans", null, "过去进行式"));
    }

    [Fact]
    public void TitlesListMostUsedFirst()
    {
        using var folder = new TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(Topic("定语从句"), []);
        library.Save(Topic("被动语态"), []);
        library.Find("zh-Hans", null, "被动语态");

        Assert.Equal(["被动语态", "定语从句"], library.Titles("zh-Hans", 10));
    }

    internal sealed class TempFolder : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory().FullName;
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* A leftover temp folder is harmless. */ }
        }
    }
}

public sealed class AiGrammarServiceTests
{
    private const string PastContinuousPlan = """
        {"kind":"topic","language":"en","topics":[{"title":"过去进行时","sourceTerm":"Past Continuous Tense","category":"时态",
        "aliases":["过去进行式","Past Continuous","was/were doing"],"summary":"表示过去某一时刻正在进行的动作。"}]}
        """;

    private const string PastContinuousCore = """
        {"title":"过去进行时","sourceTerm":"Past Continuous Tense","aliases":["过去进行式"],"category":"时态","level":"初中",
         "summary":"描述过去某个时间点正在发生的动作。","concept":"想象时间轴上的一个过去时刻，动作正在进行。",
         "formulas":[{"label":"肯定句","pattern":"主语 + was/were + 动词-ing","example":{"sourceSentence":"She was reading at nine.","translatedSentence":"九点时她在看书。","highlightTerm":"was reading"},"note":""}],
         "usages":[
           {"title":"过去某一时刻正在进行","explanation":"看到 at nine 这类具体时刻就用。","examples":[
              {"sourceSentence":"They were playing football at five.","translatedSentence":"五点时他们在踢足球。","highlightTerm":"not in sentence"},
              {"sourceSentence":"Missing translation.","translatedSentence":""}],"signals":["at that time"],"tip":"强调过程。"},
           {"title":"没有说明的用法","explanation":""}],
         "signals":[{"expression":"at that time","meaning":"在那时"}],
         "rules":[{"title":"加 -ing 的规则","explanation":"多数直接加。","cases":[{"form":"run","result":"running","note":"双写"},{"form":"make","result":""}]}]}
        """;

    private const string PastContinuousExtension = """
        {"contrasts":[{"versus":"一般过去时","difference":"进行时强调过程。","leftLabel":"过去进行时","leftUse":"强调动作正在进行","rightLabel":"一般过去时","rightUse":"强调动作已发生",
          "leftExample":{"sourceSentence":"I was cooking.","translatedSentence":"我当时在做饭。","highlightTerm":"was cooking"},
          "rightExample":{"sourceSentence":"I cooked.","translatedSentence":"我做了饭。","highlightTerm":"cooked"}}],
         "exceptions":[{"title":"状态动词","explanation":"know 等一般不用进行时。"}],
         "mistakes":[{"wrong":"She was read.","right":"She was reading.","why":"进行时用 -ing。"}],
         "examPoints":["看到 when 引导的过去短动作，主句常用过去进行时。"],"mnemonic":"was/were 加 ing，过去那刻正在进行。",
         "exercises":[
           {"question":"At nine she ____ (read).","choices":["read","was reading","is reading"],"answer":"was reading","explanation":"at nine 指过去某刻。"},
           {"question":"Bad choice ____","choices":["a","b"],"answer":"c","explanation":"答案不在选项里。"},
           {"question":"I ____ (cook) when he came.","choices":[],"answer":"was cooking","explanation":"when 引出短动作。"}],
         "related":[{"title":"一般过去时","relation":"易混"},{"title":"过去进行时","relation":"自己"}]}
        """;

    private sealed class Script(Func<string, JsonObject, string?> respond) : IAiTransport
    {
        public List<string> Roles { get; } = [];

        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            var system = messages[0]["content"]!.GetValue<string>();
            var role = system.Contains("语法点识别器") ? "plan" : system.Contains("输出核心部分") ? "core" : system.Contains("补充进阶部分") ? "extension" : "other";
            Roles.Add(role);
            var user = JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!.AsObject();
            var reply = respond(role, user) ?? throw new AiException("脚本没有这个回复。");
            onText(reply);
            return Task.FromResult(new AiCompletion(reply, []));
        }
    }

    private static string Reply(string role, JsonObject user) => role switch
    {
        "plan" => PastContinuousPlan, "core" => PastContinuousCore, "extension" => PastContinuousExtension, _ => "{}"
    };

    [Fact]
    public async Task TopicIsWrittenOnceThenAnyWordingOfItIsServedLocally()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        var script = new Script(Reply);
        var service = new AiGrammarService(() => script, library, () => "zh-Hans");

        var first = await service.AskAsync("过去进行时怎么用", default);

        Assert.Equal(GrammarLookupKind.Topic, first.Kind);
        Assert.Equal(["plan", "core", "extension"], script.Roles);
        var topic = first.Topic!;
        Assert.True(topic.Extended);
        Assert.Equal("过去进行时", topic.Title);
        // Rows a student cannot use are not kept: an unusable usage, an example without a translation, a half case.
        Assert.Equal("过去某一时刻正在进行", Assert.Single(topic.Usages).Title);
        var example = Assert.Single(topic.Usages[0].Examples);
        Assert.Equal("", example.HighlightTerm);
        Assert.Equal("running", Assert.Single(topic.Rules[0].Cases).Result);
        Assert.Equal("was reading", topic.Formulas[0].Example!.HighlightTerm);
        Assert.Equal(["一般过去时"], topic.Related.Select(r => r.Title));
        Assert.Contains("过去进行式", topic.Aliases);
        Assert.Equal(["at nine she ____ (read)."], topic.Exercises.Where(e => e.Choices.Count > 0).Select(e => e.Question.ToLowerInvariant()));
        Assert.Equal(2, topic.Exercises.Count);

        // The exact repeat, a learned alias and a term from the plan need no model call.
        foreach (var again in new[] { "过去进行时怎么用", "过去进行时", "Past Continuous", "was/were doing" })
        {
            var repeat = await service.AskAsync(again, default);
            Assert.Equal(GrammarOrigin.Library, repeat.Topic!.Origin);
        }
        Assert.Equal(3, script.Roles.Count);
        Assert.Equal(1, library.Count);
    }

    [Fact]
    public async Task DifferentQuestionReusesTheLibraryEntryAfterOnePlanningCall()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        var seeded = new Script(Reply);
        await new AiGrammarService(() => seeded, library, () => "zh-Hans").AskAsync("过去进行时", default);

        var script = new Script((role, user) =>
        {
            Assert.Equal("plan", role);
            Assert.Contains("过去进行时", user["library_titles"]!.AsArray().Select(n => n!.GetValue<string>()));
            return """{"kind":"topic","language":"en","topics":[{"title":"过去进行体","reuse":"过去进行时","aliases":["那个表示当时正在做的时态"],"summary":"x"}]}""";
        });
        var service = new AiGrammarService(() => script, library, () => "zh-Hans");

        var result = await service.AskAsync("表示当时正在做某事的时态是哪个", default);

        Assert.Equal(["plan"], script.Roles);
        Assert.Equal("过去进行时", result.Topic!.Title);
        Assert.Equal(1, library.Count);
        // The wording the student used is learned and answers itself next time.
        var third = await service.AskAsync("表示当时正在做某事的时态是哪个", default);
        Assert.Equal(GrammarOrigin.Library, third.Topic!.Origin);
        Assert.Single(script.Roles);
    }

    [Fact]
    public async Task SentenceKeepsOnlyQuotedPointsAndUsesLibraryNames()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(new GrammarTopic { Title = "过去进行时", ReaderLanguage = "zh-Hans", Summary = "s", Aliases = ["过去进行式"] }, []);
        var script = new Script((_, _) => """
            {"kind":"sentence","language":"en","sentence":{"translation":"我打电话时她正在看书。","structure":"when 引导时间状语从句。",
             "points":[
               {"topic":"过去进行式","category":"时态","quote":"was reading","label":"过去进行时","explanation":"表示当时正在进行。"},
               {"topic":"时间状语从句","category":"从句","quote":"when I called her","label":"when 引导","explanation":"说明时间。"},
               {"topic":"虚拟语气","category":"虚拟语气","quote":"would have","label":"编造","explanation":"原文没有这个片段。"}]}}
            """);
        var service = new AiGrammarService(() => script, library, () => "zh-Hans");

        var result = await service.AskAsync("She was reading when I called her.", default);

        Assert.Equal(GrammarLookupKind.Sentence, result.Kind);
        var points = result.Sentence!.Points;
        Assert.Equal(["过去进行时", "时间状语从句"], points.Select(p => p.Topic));
        Assert.Equal("She was reading when I called her.", result.Sentence.Source);
        Assert.Equal(["plan"], script.Roles);
    }

    [Fact]
    public async Task AdvancedBlocksCanFailWithoutLosingTheCoreArticleAndBeCompletedLater()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        var extensionWorks = false;
        var script = new Script((role, user) => role == "extension" && !extensionWorks ? "{}" : Reply(role, user));
        var service = new AiGrammarService(() => script, library, () => "zh-Hans");
        var stages = new List<string>();

        var result = await service.AskAsync("过去进行时", default, onStage: stages.Add);

        Assert.False(result.Topic!.Extended);
        Assert.NotEmpty(result.Topic.Usages);
        Assert.Contains(stages, s => s.Contains("进阶内容未能生成"));
        var saved = library.Find("zh-Hans", null, "过去进行时")!;
        Assert.False(saved.Extended);

        extensionWorks = true;
        var completed = await service.CompleteTopicAsync(saved, default);
        Assert.True(completed.Extended);
        Assert.NotEmpty(completed.Contrasts);
        Assert.True(library.Find("zh-Hans", null, "过去进行时")!.Extended);
    }

    [Fact]
    public async Task ArticleBlocksArriveProgressivelyWhileTheModelStreams()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        var transport = new Streaming(PastContinuousPlan, PastContinuousCore, PastContinuousExtension);
        var service = new AiGrammarService(() => transport, library, () => "zh-Hans");
        var previews = new List<GrammarTopic>();

        var result = await service.AskAsync("过去进行时", default, onPreview: previews.Add);

        Assert.True(result.Topic!.Extended);
        // The one-line summary shows up before any usage, and usages before the advanced blocks.
        var firstSummary = previews.FindIndex(p => p.Summary.Length > 0);
        var firstUsage = previews.FindIndex(p => p.Usages.Count > 0);
        var firstContrast = previews.FindIndex(p => p.Contrasts.Count > 0);
        Assert.InRange(firstSummary, 0, firstUsage - 1);
        Assert.InRange(firstUsage, 0, firstContrast - 1);
        Assert.All(previews, p => Assert.Equal("过去进行时", p.Title));
    }

    // Splits every reply into small chunks, like a streamed response.
    private sealed class Streaming(string plan, string core, string extension) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            var system = messages[0]["content"]!.GetValue<string>();
            var reply = system.Contains("语法点识别器") ? plan : system.Contains("输出核心部分") ? core : extension;
            for (var i = 0; i < reply.Length; i += 40) onText(reply[i..Math.Min(reply.Length, i + 40)]);
            return Task.FromResult(new AiCompletion(reply, []));
        }
    }

    [Fact]
    public async Task NonGrammarInputGetsAFriendlyHintAndWritesNothing()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        var script = new Script((_, _) => """{"kind":"none","message":"这是一个单词，可以到「查词翻译」查看词义。"}""");
        var service = new AiGrammarService(() => script, library, () => "zh-Hans");

        var result = await service.AskAsync("apple", default);

        Assert.Equal(GrammarLookupKind.NotGrammar, result.Kind);
        Assert.Contains("查词翻译", result.Message);
        Assert.Equal(0, library.Count);
    }

    [Fact]
    public async Task BroadQuestionOffersChoicesWithLibraryNames()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(new GrammarTopic { Title = "现在完成时", ReaderLanguage = "zh-Hans", Summary = "s", Aliases = ["现在完成式"] }, []);
        var script = new Script((_, _) => """
            {"kind":"topic","language":"en","topics":[
              {"title":"现在完成式","category":"时态","summary":"强调对现在的影响。"},
              {"title":"过去进行时","category":"时态","summary":"强调过去正在进行。"}]}
            """);
        var service = new AiGrammarService(() => script, library, () => "zh-Hans");

        var result = await service.AskAsync("时态", default);

        Assert.Equal(GrammarLookupKind.Choices, result.Kind);
        Assert.Equal(["现在完成时", "过去进行时"], result.Choices.Select(c => c.Title));
    }

    [Fact]
    public async Task WithoutAnEngineTheLibraryStillAnswersOrExplainsWhatIsMissing()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(new GrammarTopic { Title = "过去进行时", ReaderLanguage = "zh-Hans", Summary = "一句话" }, []);
        var offline = new AiGrammarService(() => null, library, () => "zh-Hans");

        Assert.Equal("过去进行时", (await offline.AskAsync("过去进行时的用法", default)).Topic!.Title);
        var related = await offline.AskAsync("过去进行时和过去完成时有什么不同", default);
        Assert.Equal(GrammarLookupKind.Choices, related.Kind);
        Assert.Equal("过去进行时", Assert.Single(related.Choices).Title);
        await Assert.ThrowsAsync<AiException>(() => offline.AskAsync("虚拟语气", default));
    }

    [Fact]
    public async Task OpeningAnUnknownSpellingChecksTheLibraryBeforeWritingAnotherArticle()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(new GrammarTopic { Title = "过去进行时", ReaderLanguage = "zh-Hans", Summary = "s" }, []);
        var script = new Script((role, _) => role == "plan"
            ? """{"kind":"topic","language":"en","topics":[{"title":"过去进行时","reuse":"过去进行时","aliases":["过去进行体"]}]}""" : null);
        var service = new AiGrammarService(() => script, library, () => "zh-Hans");

        var topic = await service.OpenTopicAsync("过去进行时态的写法", "en", default);

        Assert.Equal("过去进行时", topic.Title);
        Assert.Equal(["plan"], script.Roles);
        Assert.Equal(1, library.Count);
    }

    [Fact]
    public async Task SuggestionCompletesFromLibraryTitles()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(new GrammarTopic { Title = "过去进行时与一般过去时的区别", ReaderLanguage = "zh-Hans", Summary = "s" }, []);
        library.Save(new GrammarTopic { Title = "过去进行时", ReaderLanguage = "zh-Hans", Summary = "s" }, []);
        var service = new AiGrammarService(() => null, library, () => "zh-Hans");

        Assert.Equal("过去进行时", await service.FindSuggestionAsync("过去", default));
        Assert.Equal("过去进行时与一般过去时的区别", await service.FindSuggestionAsync("过去进行时", default));
        Assert.Null(await service.FindSuggestionAsync("虚拟", default));
    }
}

public sealed class TranslationGrammarLinkTests
{
    [Fact]
    public void GrammarHighlightsCarryTheCanonicalTopicAndOlderRowsStillLoad()
    {
        const string source = "She was reading when I called her.";
        const string json = """
            {"primaryTranslation":"我打电话时她正在看书。","itemType":"Sentence","grammarHighlights":[
              {"quote":"was reading","topic":"过去进行时","label":"was reading 表示当时正在进行","explanation":"过去某一刻正在发生。"},
              {"quote":"when I called her","label":"时间状语从句","explanation":"没有 topic 的旧写法。"}]}
            """;

        var result = AiDictionaryService.ParseDictionaryResponse(source, "zh-Hans", json);

        Assert.Equal(["过去进行时", ""], result.GrammarHighlights.Select(h => h.Topic));
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<DictionaryEntryResult>(System.Text.Json.JsonSerializer.Serialize(result))!;
        Assert.Equal("过去进行时", roundTrip.GrammarHighlights[0].Topic);
        var old = System.Text.Json.JsonSerializer.Deserialize<GrammarHighlight>("""{"Quote":"q","Label":"l","Explanation":"e"}""")!;
        Assert.Equal("", old.Topic);
    }

    [Fact]
    public async Task SentenceTranslationOffersLibraryNamesAndWordsDoNot()
    {
        using var folder = new GrammarLibraryTests.TempFolder();
        var library = new GrammarLibrary(Path.Combine(folder.Path, "grammar.sqlite3"));
        library.Save(new GrammarTopic { Title = "过去进行时", ReaderLanguage = "zh-Hans", Summary = "s" }, []);
        var requests = new List<(string System, JsonObject User)>();
        var transport = new Capture((system, user) =>
        {
            requests.Add((system, user));
            return """
                {"primaryTranslation":"她正在看书。","itemType":"Sentence","grammarHighlights":[
                  {"quote":"was reading","topic":"过去进行时","label":"过去进行","explanation":"当时正在进行。"}]}
                """;
        });
        var service = new AiDictionaryService(() => transport, readerLanguageId: () => "zh-Hans", grammarLibrary: library);

        var sentence = await service.LookupOrTranslateAsync("She was reading a book at that time.", "zh-Hans");

        var first = requests[0];
        Assert.Contains("句段语法", first.System);
        Assert.Equal(["过去进行时"], first.User["known_grammar_topics"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("过去进行时", Assert.Single(sentence.GrammarHighlights).Topic);

        requests.Clear();
        await service.LookupOrTranslateAsync("book", "zh-Hans", intent: DictionaryLookupIntent.Quick);
        Assert.False(requests[0].User.ContainsKey("known_grammar_topics"));
    }

    private sealed class Capture(Func<string, JsonObject, string> respond) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            var reply = respond(messages[0]["content"]!.GetValue<string>(), JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!.AsObject());
            onText(reply);
            return Task.FromResult(new AiCompletion(reply, []));
        }
    }
}
