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
        vocabTests.VocabularyService_SeedsAndSupportsCrudFilterAndMultiFormatExport();

        var pipelineTests = new TranslationPipelineTests();
        pipelineTests.TranslationLayout_GroupsParagraphLinesAndPreservesHeadings();
        await pipelineTests.AiTranslationService_TranslatesRegionsAndBindsById();

        var speechTests = new KokoroSpeechPhonicsTests();
        speechTests.KokoroSpeechService_FormatsIpaPhonemeLiteralsAndSplitsPhonicsSyllables();

        Console.WriteLine("ALL 5 TEST SUITES PASSED SUCCESSFULLY.");
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
