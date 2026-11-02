using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public static class TestProgram
{
    public static async Task<int> Main()
    {
        var vocabTests = new VocabularyAndFsrsTests();
        vocabTests.FsrsScheduler_ProgressesFromNewToLearningReviewingAndMastered();
        vocabTests.FsrsScheduler_HardRatingDoesNotForceArtificialGrowthFloor();
        vocabTests.VocabularyService_SeedsAndSupportsCrudFilterAndMultiFormatExport();
        vocabTests.VocabularyService_DefaultSortOrderUsesCreatedAtNotUpdatedAt();
        vocabTests.VocabularyService_ImportFromJson_AvoidsDuplicateIdCollisions();
        vocabTests.VocabularyExportFormatter_AnkiTsv_SanitizesTabsAndNewlinesInTags();

        var pipelineTests = new TranslationPipelineTests();
        pipelineTests.TranslationLayout_GroupsParagraphLinesAndPreservesHeadings();
        await pipelineTests.AiTranslationService_TranslatesRegionsAndBindsById();

        var speechTests = new KokoroSpeechPhonicsTests();
        speechTests.KokoroSpeechService_FormatsIpaPhonemeLiteralsAndSplitsPhonicsSyllables();

        var dictionaryTests = new AiDictionaryServiceTests();
        dictionaryTests.ParseDictionaryResponse_HandlesNonObjectArrayItemsGracefully();
        dictionaryTests.ParseDictionaryResponse_UnparsableFencedJson_FallbackStripsMarkdownFence();
        dictionaryTests.ParseDictionaryResponse_NonObjectJsonRoot_FallsBackInsteadOfThrowing();

        var transportTests = new AiTransportModelHeuristicsTests();
        await transportTests.CompleteAsync_OpenAiReasoningModels_SendReasoningEffortOnlyWhenSupported();
        await transportTests.CompleteAsync_HybridThinkingModels_SkipDedicatedThinkingOnlyModel();

        Console.WriteLine("ALL 7 TEST SUITES PASSED SUCCESSFULLY.");
        return 0;
    }
}

public sealed class KokoroSpeechPhonicsTests
{
    [Fact]
    public void KokoroSpeechService_FormatsIpaPhonemeLiteralsAndSplitsPhonicsSyllables()
    {
        var literal = KokoroSpeechService.FormatKokoroIpaInput("serendipity", "/ˌser.ənˈdɪp.ə.ti/");
        Assert.Equal("[serendipity](/ˌser.ənˈdɪp.ə.ti/)", literal);

        var syllables = KokoroSpeechService.SplitIntoPhonicsSyllables("serendipity");
        Assert.True(syllables.Count >= 3);
        Assert.Equal("serendipity", string.Concat(syllables));

        var usVoices = new KokoroSpeechService().GetAvailableVoices(SpeechAccent.AmericanEnglish);
        var ukVoices = new KokoroSpeechService().GetAvailableVoices(SpeechAccent.BritishEnglish);
        Assert.Contains(usVoices, v => v.VoiceId == "af_heart");
        Assert.Contains(ukVoices, v => v.VoiceId == "bf_emma");
    }
}

public sealed class VocabularyAndFsrsTests
{
    [Fact]
    public void FsrsScheduler_ProgressesFromNewToLearningReviewingAndMastered()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new VocabularyEntry
        {
            Id = "test-1",
            Headword = "ubiquitous",
            Translation = "无所不在的",
            Stage = MemoryStage.New,
            StabilityDays = 0,
            Difficulty = 5.0
        };

        // Preview intervals should be strictly ordered: Again < Hard < Good < Easy
        var preview = SpacedRepetitionScheduler.PreviewIntervals(entry, now);
        Assert.True(preview.AgainInterval < preview.HardInterval);
        Assert.True(preview.HardInterval < preview.GoodInterval);
        Assert.True(preview.GoodInterval < preview.EasyInterval);

        // First Good review moves to Reviewing with positive stability
        var afterFirstGood = SpacedRepetitionScheduler.ApplyReview(entry, ReviewRating.Good, now);
        Assert.Equal(MemoryStage.Reviewing, afterFirstGood.Stage);
        Assert.True(afterFirstGood.StabilityDays >= 1.5);
        Assert.Equal(1, afterFirstGood.CorrectStreak);

        // Repeated Easy reviews increase stability and eventually reach Mastered (5 stars)
        var current = afterFirstGood;
        var reviewTime = now;
        for (var i = 0; i < 5; i++)
        {
            reviewTime = current.NextReviewAt.AddHours(1);
            current = SpacedRepetitionScheduler.ApplyReview(current, ReviewRating.Easy, reviewTime);
        }

        Assert.Equal(MemoryStage.Mastered, current.Stage);
        Assert.Equal(5, current.MasteryLevel);
        Assert.True(current.StabilityDays >= 45.0);

        // A lapse (Again) resets streak and transitions back to Learning
        var afterLapse = SpacedRepetitionScheduler.ApplyReview(current, ReviewRating.Again, reviewTime.AddDays(10));
        Assert.Equal(MemoryStage.Learning, afterLapse.Stage);
        Assert.Equal(0, afterLapse.CorrectStreak);
        Assert.Equal(1, afterLapse.LapseCount);
    }

    [Fact]
    public void FsrsScheduler_HardRatingDoesNotForceArtificialGrowthFloor()
    {
        // Regression test: the stability growth factor used to be floored at 1.15 (forcing a
        // minimum +15% stability growth on every successful review), even when the computed
        // FSRS growth factor was lower (e.g. a well-retained, high-difficulty item reviewed
        // well before it was due). The floor must only prevent shrinkage (>= 1.0), not force
        // artificial growth.
        var now = DateTimeOffset.UtcNow;
        var entry = new VocabularyEntry
        {
            Id = "test-hard-floor",
            Headword = "entrenched",
            Translation = "根深蒂固的",
            Stage = MemoryStage.Reviewing,
            StabilityDays = 400,
            Difficulty = 8.5,
            CorrectStreak = 5,
            LastReviewedAt = now.AddDays(-1)
        };

        var afterHard = SpacedRepetitionScheduler.ApplyReview(entry, ReviewRating.Hard, now);

        Assert.True(afterHard.StabilityDays < entry.StabilityDays * 1.15 - 0.01);
        Assert.True(afterHard.StabilityDays >= entry.StabilityDays);
    }

    [Fact]
    public void VocabularyService_DefaultSortOrderUsesCreatedAtNotUpdatedAt()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ai_translate_vocab_sort_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new VocabularyService(tempFile);
            var nb = service.CreateNotebook("排序测试专用词本", "");

            var now = DateTimeOffset.UtcNow;
            var olderWord = new VocabularyEntry
            {
                Id = $"voc_{Guid.NewGuid():N}",
                Headword = "old-word",
                Translation = "旧词",
                NotebookId = nb.Id,
                CreatedAt = now.AddDays(-10)
            };
            var newerWord = new VocabularyEntry
            {
                Id = $"voc_{Guid.NewGuid():N}",
                Headword = "new-word",
                Translation = "新词",
                NotebookId = nb.Id,
                CreatedAt = now
            };
            service.SaveEntry(olderWord);
            service.SaveEntry(newerWord);

            // Starring the OLD word bumps its UpdatedAt well after the newer word's UpdatedAt,
            // but it must not jump ahead of the truly newer word in the default "recently added" order.
            service.ToggleStar(olderWord.Id);

            var results = service.QueryEntries(new VocabularyQueryFilter { NotebookId = nb.Id });
            Assert.Equal("new-word", results[0].Headword);
            Assert.Equal("old-word", results[1].Headword);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void VocabularyService_ImportFromJson_AvoidsDuplicateIdCollisions()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ai_translate_vocab_import_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new VocabularyService(tempFile);
            var existing = service.QueryEntries(new VocabularyQueryFilter())[0];

            // An imported entry whose Id collides with a different, unrelated existing entry
            // must not be inserted verbatim (that would leave two different entries sharing
            // the same Id, corrupting Id-based lookups like SaveEntry/ToggleStar/DeleteEntry).
            var colliding = new VocabularyEntry
            {
                Id = existing.Id,
                Headword = "totally-different-imported-word",
                Translation = "导入冲突词"
            };
            var json = JsonSerializer.Serialize(new[] { colliding });
            var count = service.ImportFromJson(json);
            Assert.Equal(1, count);

            var all = service.QueryEntries(new VocabularyQueryFilter());
            Assert.Equal(1, all.Count(e => e.Id == existing.Id));
            Assert.Contains(all, e => e.Id == existing.Id && e.Headword == existing.Headword);
            Assert.Contains(all, e => e.Headword == "totally-different-imported-word" && e.Id != existing.Id);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void VocabularyExportFormatter_AnkiTsv_SanitizesTabsAndNewlinesInTags()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ai_translate_vocab_anki_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new VocabularyService(tempFile);
            var entry = new VocabularyEntry
            {
                Id = $"voc_{Guid.NewGuid():N}",
                Headword = "tag-sanitize-check",
                Translation = "标签清洗测试",
                Tags = ["normal", "has\ttab", "has\nnewline"]
            };
            service.SaveEntry(entry);

            var anki = service.ExportToAnkiTsv(new VocabularyQueryFilter { SearchKeyword = "tag-sanitize-check" });
            var row = anki.Split('\n').Single(l => l.Contains("tag-sanitize-check"));

            // Exactly 6 columns (Front/Back/Phonetic/Mnemonic/Example/Tags): a stray raw tab or
            // newline inside the Tags field would otherwise introduce extra columns/rows.
            Assert.Equal(6, row.TrimEnd('\r').Split('\t').Length);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void VocabularyService_SeedsAndSupportsCrudFilterAndMultiFormatExport()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"ai_translate_vocab_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new VocabularyService(tempFile);
            var stats = service.GetDashboardStats();
            Assert.True(stats.TotalEntries >= 5);
            Assert.True(stats.DueTodayCount >= 3);

            // Lookup built-in lexicon and add/update
            Assert.True(BuiltInShowcaseLexicon.TryGet("serendipity", out var dictEntry));
            var updated = service.AddOrUpdateFromDictionary(
                dictEntry,
                notebookId: "default",
                contextSentence: "Pure serendipity led to the discovery.",
                source: TranslationTriggerSource.SelectionPopup,
                sourceTitle: "UnitTest",
                userNote: "重点掌握");

            Assert.Equal("重点掌握", updated.UserPersonalNote);
            Assert.True(updated.LookupCount >= 4);

            // Create custom notebook
            var nb = service.CreateNotebook("测试专属词本", "用于单元测试");
            Assert.Contains(service.GetNotebooks(), x => x.Id == nb.Id);

            // Export formats
            var md = service.ExportToMarkdown();
            var anki = service.ExportToAnkiTsv();
            var csv = service.ExportToCsv();
            var json = service.ExportToJson();

            Assert.Contains("serendipity", md);
            Assert.Contains("#columns:Front\tBack", anki);
            Assert.Contains("Headword,Phonetic,Translation", csv);
            Assert.Contains("serendipity", json);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}

public sealed class TranslationPipelineTests
{
    [Fact]
    public void TranslationLayout_GroupsParagraphLinesAndPreservesHeadings()
    {
        var ocr = new OcrResult(
            "Heading\nFirst line of a prose paragraph that is fairly long.\nSecond line of the same prose paragraph.",
            [
                new OcrLine("Heading", new RectD(20, 20, 90, 22)),
                new OcrLine("First line of a prose paragraph that is fairly long.", new RectD(20, 60, 420, 20)),
                new OcrLine("Second line of the same prose paragraph.", new RectD(20, 84, 340, 20))
            ],
            500,
            200);

        var regions = TranslationLayout.CreateRegions(ocr);
        Assert.Equal(2, regions.Count);
        Assert.Equal("Heading", regions[0].Text);
        Assert.Contains("First line", regions[1].Text);
        Assert.Contains("Second line", regions[1].Text);
    }

    [Fact]
    public async Task AiTranslationService_TranslatesRegionsAndBindsById()
    {
        var transport = new FakeAiTransport(_ =>
            """{"regions":[{"id":"r1","text":"第二个区域译文"},{"id":"r0","text":"第一个区域译文"}]}""");
        var service = new AiTranslationService(transport);

        var result = await service.TranslateRegionsAsync(
            [
                new TranslationSourceRegion("r0", "First region text"),
                new TranslationSourceRegion("r1", "Second region text")
            ],
            "zh-Hans",
            CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("r0", result[0].Id);
        Assert.Equal("第一个区域译文", result[0].Text);
        Assert.Equal("r1", result[1].Id);
        Assert.Equal("第二个区域译文", result[1].Text);
    }

    private sealed class FakeAiTransport(Func<IReadOnlyList<JsonObject>, string> responder) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            var reply = responder(messages);
            onText(reply);
            return Task.FromResult(new AiCompletion(reply, []));
        }
    }
}

public sealed class AiDictionaryServiceTests
{
    [Fact]
    public void ParseDictionaryResponse_HandlesNonObjectArrayItemsGracefully()
    {
        // senses/inflections/collocations/examples items that aren't JSON objects (e.g. the model
        // emits a bare string instead of the requested {..} shape) used to make JsonElement.TryGetProperty
        // throw InvalidOperationException, which escaped the catch(JsonException) handler below as a raw
        // .NET exception instead of being skipped like any other malformed entry.
        const string malformed = """
        {
          "primaryTranslation": "测试译文",
          "itemType": "Word",
          "senses": ["not-an-object", {"partOfSpeech": "n.", "definitionZh": "名词释义"}],
          "inflections": [42, {"label": "复数", "value": "tests"}],
          "collocations": [null, {"phrase": "run a test", "meaning": "进行测试"}],
          "examples": ["plain string example", {"sourceSentence": "This is a test.", "translatedSentence": "这是一个测试。"}]
        }
        """;

        var result = AiDictionaryService.ParseDictionaryResponse("test", "zh-Hans", malformed);

        Assert.Equal("测试译文", result.PrimaryTranslation);
        Assert.Single(result.Senses);
        Assert.Equal("名词释义", result.Senses[0].DefinitionZh);
        Assert.Single(result.Inflections);
        Assert.Equal("tests", result.Inflections[0].Value);
        Assert.Single(result.Collocations);
        Assert.Equal("run a test", result.Collocations[0].Phrase);
        Assert.Single(result.Examples);
        Assert.Equal("This is a test.", result.Examples[0].SourceSentence);
    }

    [Fact]
    public void ParseDictionaryResponse_UnparsableFencedJson_FallbackStripsMarkdownFence()
    {
        // When the whole response fails to parse as JSON, the fallback must show the fence-stripped
        // text, not the raw response with a leaked ```json wrapper, as the plain-text translation.
        const string fenced = "```json\n{ this is not valid json\n```";

        var result = AiDictionaryService.ParseDictionaryResponse("broken", "zh-Hans", fenced);

        Assert.DoesNotContain("```", result.PrimaryTranslation);
        Assert.Equal("{ this is not valid json", result.PrimaryTranslation);
    }

    [Fact]
    public void ParseDictionaryResponse_NonObjectJsonRoot_FallsBackInsteadOfThrowing()
    {
        // A syntactically valid JSON value that isn't an object at the top level (e.g. the model
        // wraps its reply in an array) used to make root.TryGetProperty throw InvalidOperationException
        // instead of degrading to the plain-text fallback like any other unparsable response.
        const string arrayRoot = """["unexpected", "array"]""";

        var result = AiDictionaryService.ParseDictionaryResponse("hello", "zh-Hans", arrayRoot);

        Assert.Equal("hello", result.SourceText);
        Assert.Equal(arrayRoot, result.PrimaryTranslation);
    }
}

public sealed class AiTransportModelHeuristicsTests
{
    [Fact]
    public async Task CompleteAsync_OpenAiReasoningModels_SendReasoningEffortOnlyWhenSupported()
    {
        // gpt-5-chat-latest is the non-reasoning chat variant and rejects reasoning_effort.
        Assert.False(await SendsFieldAsync("openai", "gpt-5-chat-latest", "reasoning_effort"));
        // o1-mini/o1-preview never supported reasoning_effort, unlike full o1.
        Assert.False(await SendsFieldAsync("openai", "o1-mini", "reasoning_effort"));
        Assert.False(await SendsFieldAsync("openai", "o1-preview", "reasoning_effort"));
        // "minimal" is only valid for GPT-5; the o-series only accepts low/medium/high.
        Assert.Equal("minimal", await GetFieldValueAsync("openai", "gpt-5", "reasoning_effort"));
        Assert.Equal("low", await GetFieldValueAsync("openai", "o1", "reasoning_effort"));
        Assert.Equal("low", await GetFieldValueAsync("openai", "o3-mini", "reasoning_effort"));
    }

    [Fact]
    public async Task CompleteAsync_HybridThinkingModels_SkipDedicatedThinkingOnlyModel()
    {
        Assert.True(await SendsFieldAsync("compatible", "Qwen/Qwen3-32B", "enable_thinking"));
        // kimi-k2-thinking is a dedicated always-thinking model with no non-thinking toggle, unlike
        // the hybrid Qwen3/GLM models that expose enable_thinking on the same model id.
        Assert.False(await SendsFieldAsync("compatible", "kimi-k2-thinking", "enable_thinking"));
    }

    private static async Task<bool> SendsFieldAsync(string providerId, string modelId, string field)
    {
        var body = await CaptureBodyAsync(providerId, modelId);
        return body.ContainsKey(field);
    }

    private static async Task<string?> GetFieldValueAsync(string providerId, string modelId, string field)
    {
        var body = await CaptureBodyAsync(providerId, modelId);
        return body.TryGetPropertyValue(field, out var value) ? value!.GetValue<string>() : null;
    }

    private static async Task<JsonObject> CaptureBodyAsync(string providerId, string modelId)
    {
        JsonObject? capturedBody = null;
        using var client = new HttpClient(new CapturingHandler(body => capturedBody = body));
        using var transport = new AiTransport(new AiProviderConfig(providerId, modelId, null, "test-key"), client);
        await transport.CompleteAsync(
            [new JsonObject { ["role"] = "user", ["content"] = "hi" }], [], _ => { }, CancellationToken.None);
        Assert.NotNull(capturedBody);
        return capturedBody!;
    }

    private sealed class CapturingHandler(Action<JsonObject> onRequest) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            onRequest(JsonNode.Parse(json)!.AsObject());
            const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
