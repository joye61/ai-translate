using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class LearningBlocksTests
{
    internal const string Fixture = """
        {"primaryTranslation":"停止；停留","itemType":"Word","detectedSourceLanguage":"en",
         "usageContrasts":[
          {"leftForm":"stop doing","leftUse":"结束正在做的事。","rightForm":"stop to do","rightUse":"停下当前活动，去做另一件事。",
           "leftExample":{"sourceSentence":"She stopped working.","translatedSentence":"她停止了工作。","highlightTerm":"stopped working"},
           "rightExample":{"sourceSentence":"She stopped to rest.","translatedSentence":"她停下来休息。","highlightTerm":"stopped to rest"}},
          {"leftForm":"stop by","leftUse":"顺便短暂拜访。","rightForm":"stop over","rightUse":"旅途中在某地停留。"}],
         "usageGuidance":[
          {"title":"礼貌与语气","explanation":"直接要求对方 **停止** 时语气较强；需要客气时可加 please，并说明原因。",
           "example":{"sourceSentence":"Please stop the music for a moment.","translatedSentence":"请把音乐停一下。","highlightTerm":"stop"}},
          {"title":"进行中的动作","explanation":"描述动作正在逐步停止时，可以使用进行时，不必把 stop 当成不能用于进行时的状态动词。",
           "example":{"sourceSentence":"The rain is stopping.","translatedSentence":"雨渐渐停了。","highlightTerm":"is stopping"}}],
         "pronunciationDrills":[
          {"focus":"词首辅音群","text":"stop","tip":"/s/ 与 /t/ 连在一起，前面不添加额外元音。"},
          {"focus":"短句中的衔接","text":"Stop it, please.","tip":"把词尾 /p/ 与后面的元音连贯读出，please 保持清楚。"}]}
        """;

    private static DictionaryEntryResult Entry() => AiDictionaryService.ParseDictionaryResponse("stop", "zh-Hans", Fixture);

    [Fact]
    public void ReadsMultipleTopicsAndPairedExamplesWithLegacyAndMalformedFallbacks()
    {
        var entry = Entry();
        Assert.Equal(2, entry.UsageGuidance.Count);
        Assert.Equal(2, entry.PronunciationDrills.Count);
        Assert.Equal("她停下来休息。", entry.UsageContrasts[0].RightExample!.TranslatedSentence);
        var malformed = JsonNode.Parse(Fixture)!.AsObject();
        malformed["usageGuidance"]!.AsArray().Add(new JsonObject { ["title"] = "没有说明" });
        malformed["pronunciationDrills"]!.AsArray().Add(new JsonObject { ["focus"] = "没有练习文本" });
        malformed["usageGuidance"]!.AsArray().Add(null);
        Assert.Equal(entry.UsageGuidance, AiDictionaryService.ParseDictionaryResponse("stop", "zh-Hans", malformed.ToJsonString()).UsageGuidance);
        Assert.Equal(entry.PronunciationDrills, AiDictionaryService.ParseDictionaryResponse("stop", "zh-Hans", malformed.ToJsonString()).PronunciationDrills);
        var legacy = AiDictionaryService.ParseDictionaryResponse("stop", "zh-Hans",
            """{"primaryTranslation":"停止","usageNotes":"旧提示","pronunciationTip":"旧发音提示","usageGuidance":null,"pronunciationDrills":null}""");
        Assert.Empty(legacy.UsageGuidance);
        Assert.Empty(legacy.PronunciationDrills);
        Assert.Equal("旧提示", legacy.UsageNotes);
        var sentence = AiDictionaryService.ParseDictionaryResponse("Stop it.", "zh-Hans", Fixture.Replace("\"Word\"", "\"Sentence\""));
        Assert.Empty(sentence.PronunciationDrills);
        Assert.Empty(sentence.UsageContrasts);
        Assert.Equal(2, sentence.UsageGuidance.Count);
    }

    [Fact]
    public async Task StreamsLearningRowsBeforeTheLastFieldIsComplete()
    {
        var frames = new List<DictionaryEntryResult>();
        var reply = new Reply(Fixture, frame =>
        {
            if (frame == 0) Assert.Contains(frames, f => f.UsageGuidance.Count == 2 && f.PronunciationDrills.Count == 0);
            if (frame == 1) Assert.Contains(frames, f => f.PronunciationDrills.Count == 2 && f.PronunciationDrills[1].Tip == "把词尾");
        });
        var entry = await new AiDictionaryService(() => reply).LookupOrTranslateAsync("stop", "zh-Hans", onEntryPreview: frames.Add);
        Assert.Equal(Entry().PronunciationDrills, entry.PronunciationDrills);
    }

    [Fact]
    public async Task ReviewAndRecollectionPreserveLearningContentAndPersonalProgress()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(folder.FullName, "cards.json");
            var cards = new VocabularyService(path);
            var original = Entry() with { LookupContext = "Please stop the music." };
            var collected = cards.AddOrUpdateFromDictionary(original, contextSentence: original.LookupContext, sourceTitle: "课堂");
            collected = cards.SaveEntry(collected with { UserPersonalNote = "练习停止与停留" });
            var progress = cards.SubmitReview(collected.Id, ReviewRating.Good);
            var service = new AiDictionaryService(() => new Reply("""
                {"status":"improved","edits":[{"path":"/usageGuidance/-","value":
                {"title":"请求的对象","explanation":"说明要停止哪一项活动，避免单独使用 Stop 引起误解。"}}]}
                """), refreshCollectedEntries: cards.RefreshCollectedEntries);
            var reviewed = await service.ReviewBlockAsync(original, EntryBlock.UsageNotes, default);
            Assert.Equal(3, reviewed.UsageGuidance.Count);
            Assert.Equal(original.PronunciationDrills, reviewed.PronunciationDrills);
            cards.AddOrUpdateFromDictionary(original with { UsageGuidance = [], PronunciationDrills = [],
                UsageContrasts = original.UsageContrasts.Select(c => c with { LeftExample = null, RightExample = null }).ToArray() });
            var reopened = new VocabularyService(path);
            var saved = reopened.FindByHeadword("stop")!;
            Assert.Equal(reviewed.UsageGuidance, saved.ToDictionaryResult().UsageGuidance);
            Assert.Equal(original.PronunciationDrills, saved.ToDictionaryResult().PronunciationDrills);
            Assert.Equal(original.UsageContrasts, saved.ToDictionaryResult().UsageContrasts);
            Assert.Equal(progress.TotalReviews, saved.TotalReviews);
            Assert.Equal(progress.NextReviewAt, saved.NextReviewAt);
            Assert.Equal("课堂", saved.SourceTitle);
            Assert.Equal(original.LookupContext, saved.ContextSentence);
            Assert.Equal("练习停止与停留", saved.UserPersonalNote);
            var filter = new VocabularyQueryFilter();
            foreach (var export in new[] { reopened.ExportToCsv(filter), reopened.ExportToAnkiTsv(filter), reopened.ExportToMarkdown(filter) })
            {
                Assert.Contains("词首辅音群", export);
                Assert.Contains("礼貌与语气", export);
                Assert.Contains("她停下来休息。", export);
                Assert.DoesNotContain("**停止**", export);
            }
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task FocusedReviewsCanAddDrillsAndContrastExamplesWithoutLosingOtherFields()
    {
        var original = Entry();
        var updated = await new AiDictionaryService(() => new Reply("""
            {"status":"improved","edits":[{"path":"/pronunciationDrills/-","value":
            {"focus":"过去式的词尾","text":"stopped","tip":"词尾 -ed 读 /t/，不另加一个音节。"}}]}
            """)).ReviewBlockAsync(original, EntryBlock.PronunciationPractice, default);
        Assert.Equal(3, updated.PronunciationDrills.Count);
        Assert.Equal(original.UsageGuidance, updated.UsageGuidance);
        updated = await new AiDictionaryService(() => new Reply("""
            {"status":"improved","edits":[{"path":"/usageContrasts/1/leftExample","value":
            {"sourceSentence":"I will stop by tomorrow.","translatedSentence":"我明天会顺便来看看。"}}]}
            """)).ReviewBlockAsync(updated, EntryBlock.UsageContrasts, default);
        Assert.Equal("I will stop by tomorrow.", updated.UsageContrasts[1].LeftExample!.SourceSentence);
        Assert.Equal(original.UsageContrasts[0], updated.UsageContrasts[0]);
        await Assert.ThrowsAsync<AiException>(() => new AiDictionaryService(() => new Reply("""
            {"status":"improved","edits":[{"path":"/pronunciationDrills/0","value":{"focus":"词首辅音群","text":"stop"}}]}
            """)).ReviewBlockAsync(original, EntryBlock.PronunciationPractice, default));
    }

    [Fact]
    public async Task LearningRowsFitWideAndNarrowViewsAndReadTheirOwnPracticeText()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new RecordingSpeech();
                var details = new DictionaryDetails(speech, new AppSettingsStore(Path.Combine(folder.FullName, "settings.json")), (_, _, _) => { });
                var window = new Window { Width = 800, Height = 700, Content = new ScrollViewer { Content = details } };
                window.Show();
                try
                {
                    details.ShowEntry(Entry(), complete: false);
                    window.UpdateLayout();
                    Assert.DoesNotContain(details.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b)?.Contains("短句中的衔接") == true);
                    details.ShowEntry(Entry(), complete: true);
                    foreach (var width in new[] { 760, 354 })
                    {
                        details.Width = width;
                        window.UpdateLayout();
                        var sections = details.GetVisualDescendants().OfType<DictionarySection>().Where(s => s.IsEffectivelyVisible).ToArray();
                        Assert.Equal(new[] { "用法对照", "用法与辨析", "发音练习" }, sections.Select(s => s.Title));
                        var text = details.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).ToArray();
                        Assert.Contains(text, t => t.Text == "她停下来休息。");
                        Assert.Contains(text, t => t.Text == "礼貌与语气");
                        Assert.Contains(text, t => t.Text == "Stop it, please.");
                        foreach (var t in text)
                            Assert.InRange(t.TranslatePoint(new Point(t.Bounds.Width, 0), details)!.Value.X, 0, width + 1);
                        var left = text.Single(t => t.Text == "stop doing").TranslatePoint(default, details)!.Value;
                        var right = text.Single(t => t.Text == "stop to do").TranslatePoint(default, details)!.Value;
                        Assert.Equal(width < 520, right.Y > left.Y);
                    }
                    var read = details.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "慢速跟读 · 短句中的衔接");
                    read.Focus();
                    read.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(("Stop it, please.", 0.68f), Assert.Single(speech.Readings));
                    details.ShowEntry(Entry() with { DetectedSourceLanguage = "auto", UsPhonetic = "/stɑp/" }, complete: true);
                    window.UpdateLayout();
                    Assert.Contains(details.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b) == "示范朗读 · 词首辅音群");
                    details.ShowEntry(Entry() with { DetectedSourceLanguage = "fr" }, complete: true);
                    window.UpdateLayout();
                    Assert.DoesNotContain(details.GetVisualDescendants().OfType<Button>(), b => AutomationProperties.GetName(b)?.Contains("词首辅音群") == true);
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { folder.Delete(true); }
    }

    private sealed class Reply(string response, Action<int>? inspect = null) : IAiTransport
    {
        private int _calls;
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            if (_calls++ > 0) return Task.FromResult(new AiCompletion("{}", []));
            if (inspect is not null)
            {
                var first = response.IndexOf("\"pronunciationDrills\"", StringComparison.Ordinal);
                var second = response.IndexOf("把词尾", StringComparison.Ordinal) + "把词尾".Length;
                onText(response[..first]);
                inspect(0);
                onText(response[first..second]);
                inspect(1);
                onText(response[second..]);
            }
            else onText(response);
            return Task.FromResult(new AiCompletion(response, []));
        }
    }

    private sealed class RecordingSpeech : ISpeechSynthesisService
    {
        public List<(string, float)> Readings { get; } = [];
        public bool IsKokoroModelLoaded => false;
        public bool IsSpeaking => false;
        public string EngineStatus => "测试";
        public event EventHandler<string>? StatusChanged { add { } remove { } }
        public IReadOnlyList<KokoroSpeakerProfile> GetAvailableVoices(SpeechAccent? accentFilter = null) => [];
        public Task<bool> EnsureKokoroModelReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task SpeakAsync(string text, SpeechAccent accent = SpeechAccent.AmericanEnglish, string? ipaPhonetic = null,
            float speed = 1, string? preferredVoiceId = null, CancellationToken cancellationToken = default, SpeechPlayback? playback = null)
        { Readings.Add((text, speed)); return Task.CompletedTask; }
        public Task SpeakPhonicsBreakdownAsync(string headword, string? ipaPhonetic = null, SpeechAccent accent = SpeechAccent.AmericanEnglish,
            CancellationToken cancellationToken = default, SpeechPlayback? playback = null) => Task.CompletedTask;
        public Task SpeakVocabularyCardAsync(string headword, string? usIpa, string? ukIpa, string? exampleSentence,
            CancellationToken cancellationToken = default, SpeechPlayback? playback = null) => Task.CompletedTask;
        public void Stop() { }
        public void Dispose() { }
    }
}
