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
        await new DictionaryCacheTests().CachePersistsAcrossRestartsAndRefreshOnlyReplacesSuccessfulResults();
        await new DictionaryCacheTests().GlossaryIsBoundedToThisTextAndChangesInvalidateSentenceTranslations();
        await dictionaryTests.StreamingPreview_EmitsDecodedTranslationBeforeDetailsArrive();
        await dictionaryTests.StreamingPreview_FindsTranslationAfterOtherFields();
        dictionaryTests.ParseDictionaryResponse_HandlesNonObjectArrayItemsGracefully();
        dictionaryTests.ParseDictionaryResponse_RejectsBrokenStructuredResultsAndAcceptsPlainText();
        await dictionaryTests.StreamingFields_ShowsRowsInsideOpenArrays();
        await dictionaryTests.OfflineLookup_OnlyUsesRealEntriesInTheRequestedLanguage();

        var transportTests = new AiTransportThinkingTests();
        await new DictionaryReadingTests().GrammarAndTermCanOverlapWithoutBreakingSelectionOrClickLookup();
        await transportTests.RequestsDisableThinkingForEachProvider();
        await transportTests.StopsWhenEndpointRejectsOrIgnoresTheOptOut();

        await new DictionaryInputTests().ShiftEnterAddsLineAndEnterStartsStreamingTranslation();
        await new DictionaryInputTests().OnlyCurrentLookupCanDisplayOrSaveItsResult();
        await new DictionaryInputTests().RefreshUsesTheDisplayedLanguageAndKeepsThePreviousResultOnFailure();
        await new DictionaryInputTests().DictionarySectionsArriveDuringLookupAndPassagesHaveTheirOwnLayout();
        await new DictionaryInputTests().StreamingKeepsComposerInPlaceAndRespectsReadingPosition();
        Console.WriteLine("ALL TEST SUITES PASSED SUCCESSFULLY.");
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
    public void GrammarHighlightsMustBeDistinctExactSpansOfTheSource()
    {
        const string source = "Had I known, I would have waited.";
        const string json = """
            {"primaryTranslation":"我要是早知道，就会等一等。","itemType":"Sentence","grammarHighlights":[
              {"quote":"would have waited","label":"虚拟结果","explanation":"说明未发生的结果。"},
              {"quote":"known","label":"","explanation":"没有标签"},
              {"quote":"I","label":"代词","explanation":"两处都可能匹配"},
              {"quote":"would have","label":"重叠","explanation":"与上面的片段重叠"},
              {"quote":"wait","label":"词干","explanation":"不构成独立词"},
              {"quote":"If I had known","label":"条件","explanation":"这是改写"},
              {"quote":"Had I known","label":"倒装条件","explanation":"省略 if，had 提到主语前。"}]}
            """;
        var result = AiDictionaryService.ParseDictionaryResponse(source, "zh-Hans", json);
        Assert.Equal(new[] { "Had I known", "would have waited" }, result.GrammarHighlights.Select(h => h.Quote));
    }
    [Fact]
    public async Task StreamingPreview_EmitsDecodedTranslationBeforeDetailsArrive()
    {
        const string first = "```json\n{\"primaryTranslation\":\"译\\n\\\"文\\\"\\\\\\uD83D\\uDE00\"";
        const string expected = "译\n\"文\"\\😀";
        var previews = new List<string>();
        var transport = new SplitDictionaryReply(first, ",\"usageNotes\":\"用法\"}\n```", () =>
        {
            Assert.Equal("译", previews[0]);
            Assert.Equal(expected, previews[^1]);
        });

        var entry = await new AiDictionaryService(() => transport)
            .LookupOrTranslateAsync("sample", "zh-Hans", onStreamingPreview: previews.Add);

        Assert.All(previews, text => Assert.StartsWith(text, expected));
        Assert.Equal(expected, entry.PrimaryTranslation);
        Assert.Equal("用法", entry.UsageNotes);
    }

    [Fact]
    public async Task StreamingPreview_FindsTranslationAfterOtherFields()
    {
        var previews = new List<string>();
        var transport = new SplitDictionaryReply("""{"ignored":{"primaryTranslation":"嵌套值"},"usageNotes":"用法","primaryTranslation":"译""",
            "文\"}", () => Assert.Equal("译", Assert.Single(previews)));
        var entry = await new AiDictionaryService(() => transport)
            .LookupOrTranslateAsync("sample", "zh-Hans", onStreamingPreview: previews.Add);

        Assert.Equal("译文", previews[^1]);
        Assert.Equal("译文", entry.PrimaryTranslation);
    }

    private sealed class SplitDictionaryReply(string prefix, string rest, Action beforeDetails) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            foreach (var character in prefix) onText(character.ToString());
            beforeDetails();
            onText(rest);
            return Task.FromResult(new AiCompletion(prefix + rest, []));
        }
    }

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
    public void ParseDictionaryResponse_RejectsBrokenStructuredResultsAndAcceptsPlainText()
    {
        foreach (var bad in new[] { "```json\n{ this is not valid json\n```", "[\"unexpected\",\"array\"]", "{\"senses\":[]}", "" })
            Assert.Throws<AiException>(() => AiDictionaryService.ParseDictionaryResponse("broken", "zh-Hans", bad));
        var fallback = AiDictionaryService.ParseDictionaryResponse("Look up", "zh-Hans", "查阅");
        Assert.Equal(VocabularyItemType.Phrase, fallback.ItemType);
        Assert.Equal("查阅", fallback.PrimaryTranslation);
    }

    [Fact]
    public async Task StreamingFields_ShowsRowsInsideOpenArrays()
    {
        var snapshots = new List<DictionaryEntryResult>();
        const string prefix = """{"primaryTranslation":"集合","senses":[{"partOfSpeech":"n.","definitionZh":"含有 {x,y} 和 \"引号\" 的集合"}],"examples":[{"sourceSentence":"A set","translatedSentence":"一组"}""";
        var transport = new SplitDictionaryReply(prefix, "]}", () =>
        {
            Assert.Equal("含有 {x,y} 和 \"引号\" 的集合", Assert.Single(snapshots[^1].Senses).DefinitionZh);
            Assert.Equal("一组", Assert.Single(snapshots[^1].Examples).TranslatedSentence);
        });
        var result = await new AiDictionaryService(() => transport).LookupOrTranslateAsync("set", "zh-Hans", onEntryPreview: snapshots.Add);
        Assert.Single(result.Examples);
        Assert.Single(snapshots[^1].Examples);
    }

    [Theory]
    [InlineData("{\"senses\":[{\"partOfSpeech\":\"n.\",\"definitionZh\":\"医学检", "查\"}],\"primaryTranslation\":\"检验\"}", "sense", "医学检")]
    [InlineData("{\"primaryTranslation\":\"检验\",\"senses\":[{\"definitionZh\":\"检验\",\"example\":{\"sourceSentence\":\"They te", "st it.\",\"translatedSentence\":\"他们检验它。\"}}]}", "senseExample", "They te")]
    [InlineData("{\"primaryTranslation\":\"检验\",\"collocations\":[{\"phrase\":\"run a test\",\"example\":{\"sourceSentence\":\"Run it.\",\"translatedSentence\":\"运行", "测试。\"}}]}", "pairExample", "运行")]
    [InlineData("{\"primaryTranslation\":\"检验\",\"examples\":[{\"sourceSentence\":\"Say \\\"test\\\" \\uD83D\\uDE00 \\u4E", "2D\",\"translatedSentence\":\"说测试。\"}]}", "example", "Say \"test\" 😀 ")]
    [InlineData("{\"primaryTranslation\":\"检验\",\"usageNotes\":\"用于", "医学。\"}", "notes", "用于")]
    public async Task PreviewProjectsUnfinishedTextBeforeTheProviderContinues(string prefix, string rest, string section, string expected)
    {
        DictionaryEntryResult? snapshot = null;
        // One real SSE delta can end inside any field, including an escaped Unicode character.
        var transport = new PausedDictionaryReply(prefix, rest, () =>
        {
            Assert.NotNull(snapshot);
            var visible = section switch
            {
                "sense" => Assert.Single(snapshot.Senses).DefinitionZh,
                "senseExample" => Assert.Single(snapshot.Senses).Example?.SourceSentence,
                "pairExample" => Assert.Single(snapshot.Collocations).Example?.TranslatedSentence,
                "example" => Assert.Single(snapshot.Examples).SourceSentence,
                _ => snapshot.UsageNotes
            };
            Assert.Equal(expected, visible);
        });
        var result = await new AiDictionaryService(() => transport).LookupOrTranslateAsync("test", "zh-Hans", onEntryPreview: s => snapshot = s);
        Assert.Equal("检验", result.PrimaryTranslation);
    }

    private sealed class PausedDictionaryReply(string first, string last, Action inspect) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            onText(first);
            inspect();
            onText(last);
            return Task.FromResult(new AiCompletion(first + last, []));
        }
    }

    [Fact]
    public async Task OfflineLookup_OnlyUsesRealEntriesInTheRequestedLanguage()
    {
        var offline = new AiDictionaryService(() => null);
        var sample = await offline.LookupOrTranslateAsync("serendipity", "zh-Hans");
        Assert.NotEmpty(sample.Senses);
        await Assert.ThrowsAsync<AiException>(() => offline.LookupOrTranslateAsync("serendipity", "ja"));
        await Assert.ThrowsAsync<AiException>(() => offline.LookupOrTranslateAsync("arbitrary query", "zh-Hans"));
    }
}

public sealed class AiTransportThinkingTests
{
    [Fact]
    public async Task RequestsDisableThinkingForEachProvider()
    {
        foreach (var model in new[] { "deepseek-flash", "deepseek-chat", "deepseek-v4-pro", "custom-model" })
            Assert.Equal("disabled", (string?)(await CaptureBodyAsync("deepseek", model))["thinking"]?["type"]);
        foreach (var model in new[] { "gpt-5.1", "o1-preview", "gpt-5", "o3-mini", "custom-model" })
            Assert.Equal("none", (string?)(await CaptureBodyAsync("openai", model))["reasoning_effort"]);
        foreach (var model in new[] { "gpt-4o-mini", "gpt-4.1", "ft:gpt-4o-mini-2024-07-18:org:name:id", "gpt-5-chat-latest" })
            Assert.False((await CaptureBodyAsync("openai", model)).ContainsKey("reasoning_effort"));

        Assert.False((bool?)(await CaptureBodyAsync("compatible", "kimi-k2-thinking"))["enable_thinking"]);
        Assert.False((bool?)(await CaptureBodyAsync("compatible", "qwen-plus", "https://dashscope.aliyuncs.com/compatible-mode/v1"))["enable_thinking"]);
        Assert.Equal("none", (string?)(await CaptureBodyAsync("ollama", "qwen3:8b"))["reasoning_effort"]);
        Assert.Equal("none", (string?)(await CaptureBodyAsync("compatible", "local-model", "http://localhost:8000/v1"))["reasoning_effort"]);
        Assert.Equal("disabled", (string?)(await CaptureBodyAsync("compatible", "deepseek-flash", "https://api.deepseek.com/v1"))["thinking"]?["type"]);
    }

    [Fact]
    public async Task StopsWhenEndpointRejectsOrIgnoresTheOptOut()
    {
        foreach (var (status, body) in new[]
        {
            (HttpStatusCode.BadRequest, """{"error":{"param":"reasoning_effort"}}"""),
            (HttpStatusCode.OK, "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"working\",\"content\":\"answer\"}}]}\n\n"),
            (HttpStatusCode.OK, "data: {\"choices\":[{\"delta\":{\"reasoning_details\":[{\"type\":\"reasoning.encrypted\"}]}}]}\n\n"),
            (HttpStatusCode.OK, "data: {\"choices\":[],\"usage\":{\"completion_tokens_details\":{\"reasoning_tokens\":1}}}\n\n")
        })
        {
            var requests = 0;
            using var client = new HttpClient(new CapturingHandler(_ => requests++, status, body));
            using var transport = new AiTransport(new("deepseek", "deepseek-flash"), client);
            var shown = new StringBuilder();
            var error = await Assert.ThrowsAsync<AiException>(() =>
                transport.CompleteAsync([], [], text => shown.Append(text), CancellationToken.None));
            Assert.Contains("非思考模式", error.Message);
            Assert.Equal(1, requests);
            Assert.Empty(shown.ToString());
        }
    }

    private static async Task<JsonObject> CaptureBodyAsync(string providerId, string modelId, string? baseUrl = null)
    {
        JsonObject? capturedBody = null;
        using var client = new HttpClient(new CapturingHandler(body => capturedBody = body));
        using var transport = new AiTransport(new AiProviderConfig(providerId, modelId, baseUrl, "test-key"), client);
        await transport.CompleteAsync(
            [new JsonObject { ["role"] = "user", ["content"] = "hi" }], [], _ => { }, CancellationToken.None);
        Assert.NotNull(capturedBody);
        return capturedBody!;
    }

    private sealed class CapturingHandler(Action<JsonObject> onRequest, HttpStatusCode status = HttpStatusCode.OK,
        string sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":null,\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n") : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            onRequest(JsonNode.Parse(json)!.AsObject());
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        }
    }
}
