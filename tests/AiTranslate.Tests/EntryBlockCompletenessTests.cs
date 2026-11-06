using System.Text.Json;
using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop;
using AiTranslate.Desktop.Controls;
using AiTranslate.Desktop.Views;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Xunit;

namespace AiTranslate.Tests;

[Collection("Dictionary UI")]
public sealed class EntryBlockCompletenessTests
{
    internal const string Best = """
        {"primaryTranslation":"最好的；最好地","itemType":"Word","detectedSourceLanguage":"en",
         "etymologyNotes":[
          {"title":"不规则词形","explanation":"best 是 good 和 well 的不规则最高级，不能按规则给 good 加 -est。"},
          {"title":"形容词与副词","explanation":"描述事物品质时联系 good；描述动作完成得怎样时联系 well。最高级形式都是 best。"},
          {"title":"拆分边界","explanation":"现代英语中不能把 best 拆成 be 和 st 来解释词义；记忆时把它与原级、比较级一起联系。"}],
         "mnemonics":[
          {"method":"梯度对比","cue":"good, better, best","explanation":"把三者想成逐级上升的台阶，best 对应 **最高一级**。"},
          {"method":"场景提取","cue":"This is my best work.","explanation":"想象拿出自己最满意的作品，借整句记住 best 表示比较范围中的最好。"},
          {"method":"反向联想","cue":"best / worst","explanation":"把最好与最差放在两端，用鲜明对比提取词义；这是人为联想，不是词源解释。"}]}
        """;

    [Fact]
    public async Task LookupStreamsIndividualExplanationsAndMemoryCues()
    {
        var frames = new List<DictionaryEntryResult>();
        var first = Best.IndexOf("best 是", StringComparison.Ordinal) + "best 是".Length;
        var second = Best.IndexOf("good, better", StringComparison.Ordinal) + "good, better".Length;
        var result = await new AiDictionaryService(() => new Reply(Best, chunks: [Best[..first], Best[first..second], Best[second..]]))
            .LookupOrTranslateAsync("best", "zh-Hans", onEntryPreview: frames.Add);
        Assert.Contains(frames, f => f.EtymologyNotes.Count == 1 && f.EtymologyNotes[0].Explanation.Length > 0 &&
            f.EtymologyNotes[0].Explanation != result.EtymologyNotes[0].Explanation && f.Mnemonics.Count == 0);
        Assert.Contains(frames, f => f.EtymologyNotes.Count == 3 && f.Mnemonics.Count == 1);
        Assert.Equal(3, result.Mnemonics.Count);
    }

    [Fact]
    public void RichCollectionsAreNotTruncatedByInitialLookupLimits()
    {
        var indexes = Enumerable.Range(0, 9).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            primaryTranslation = "测试", itemType = "Word", detectedSourceLanguage = "en",
            senses = new[] { new { partOfSpeech = "v.", definitionZh = "测试", usageCases = indexes.Select(i =>
                new { condition = $"条件{i}", expression = $"test case {i}", grammar = $"语法{i}", meaning = $"含义{i}" }) } },
            wordFamily = indexes.Select(i => new { label = $"相关词{i}", value = $"term{i}" }),
            synonymNuances = indexes.Select(i => new { word = $"term{i}", nuance = $"区别{i}" }),
            mistakes = indexes.Select(i => new { wrong = $"错误{i}", right = $"改正{i}", why = $"原因{i}" }),
            domains = indexes.Select(i => $"领域{i}"),
            morphemes = indexes.Select(i => new { part = $"部分{i}", meaning = $"含义{i}", kind = "root" })
        });
        var word = AiDictionaryService.ParseDictionaryResponse("test", "zh-Hans", json);
        Assert.Equal(9, word.Senses[0].UsageCases.Count);
        Assert.Equal(9, word.WordFamily.Count);
        Assert.Equal(9, word.SynonymNuances.Count);
        Assert.Equal(9, word.Mistakes.Count);
        Assert.Equal(9, word.Domains.Count);
        Assert.Equal(9, word.Morphemes.Count);
        var phrase = AiDictionaryService.ParseDictionaryResponse("test case", "zh-Hans", json.Replace("\"Word\"", "\"Phrase\""));
        Assert.Equal(9, phrase.Senses[0].UsageCases.Count);

        var source = string.Join(" ", indexes.Select(i => $"Clause {i}."));
        var passageJson = JsonSerializer.Serialize(new
        {
            primaryTranslation = "完整译文", itemType = "Sentence",
            clauses = indexes.Select(i => new { quote = $"Clause {i}.", role = $"结构{i}", depth = i, note = $"解释{i}" }),
            variants = indexes.Select(i => new { style = "正式", text = $"不同译文{i}", note = $"不同条件{i}" }),
            tone = new { formality = 3, tones = indexes.Select(i => $"特征{i}"), note = "总体说明" },
            keyPoints = indexes.Select(i => $"独立要点{i}")
        });
        var sentence = AiDictionaryService.ParseDictionaryResponse(source, "zh-Hans", passageJson);
        Assert.Equal(9, sentence.Clauses.Count);
        Assert.Equal(8, sentence.Clauses[^1].Depth);
        Assert.Equal(9, sentence.Variants.Count);
        Assert.Equal(9, sentence.Tone!.Tones.Count);
        var paragraph = AiDictionaryService.ParseDictionaryResponse(source, "zh-Hans", passageJson.Replace("\"Sentence\"", "\"Paragraph\""));
        Assert.Equal(9, paragraph.KeyPoints.Count);
    }

    [Fact]
    public async Task CoverageAndExampleCompletionReachRowsBeyondTheOldLimits()
    {
        var entry = new DictionaryEntryResult { SourceText = "test", PrimaryTranslation = "测试", DetectedSourceLanguage = "en",
            Senses = [new("v.", "测试") { UsageCases = Enumerable.Range(0, 3).Select(i => new SenseUsageCase($"条件{i}", "语法", $"case {i}")).ToArray() }],
            WordFamily = Enumerable.Range(0, 4).Select(i => new RelatedForm($"相关词{i}", $"term{i}")).ToArray() };
        var additions = JsonSerializer.Serialize(new
        {
            senseGrammar = new[] { new { id = "sense-0", partOfSpeech = "v.", definitionZh = "测试",
                usageCases = new[] { new { condition = "新增条件", grammar = "语法", expression = "another case" } } } },
            wordFamily = new[] { new { label = "新增相关词", value = "another" } }
        });
        var filled = await new AiDictionaryService(() => new Reply(additions)).CheckLexicalCoverageAsync(entry, null, default);
        Assert.Equal("another case", filled.Senses[0].UsageCases[^1].Expression);
        Assert.Equal("another", filled.WordFamily[^1].Value);

        var phrases = Enumerable.Range(0, 10).Select(i => new CollocationItem($"test {i}", $"测试{i}")).ToArray();
        var reply = JsonSerializer.Serialize(new { examples = phrases.Select((p, i) => new
        { id = $"phrase-{i}", sourceSentence = $"This is {p.Phrase}.", translatedSentence = $"这是测试{i}。", highlightTerm = p.Phrase }) });
        var completed = await new AiDictionaryService(() => new Reply(reply, messages =>
            Assert.Equal(10, JsonNode.Parse(messages[1]["content"]!.GetValue<string>())!["items"]!.AsArray().Count)))
            .CompleteExamplesAsync(entry with { Collocations = phrases }, null, default);
        Assert.All(completed.Collocations, c => Assert.NotNull(c.Example));
        Assert.Empty(completed.MissingUsageExamples());
    }

    [Fact]
    public async Task FocusedReviewCanGrowAnAlreadyFullList()
    {
        var word = new DictionaryEntryResult { SourceText = "sample", PrimaryTranslation = "样本",
            SynonymNuances = Enumerable.Range(0, 5).Select(i => new SynonymNuance($"term{i}", $"区别{i}")).ToArray() };
        var reviewed = await new AiDictionaryService(() => new Reply("""
            {"status":"improved","edits":[{"path":"/synonymNuances/-","value":{"word":"another","nuance":"另一种明确的选用条件"}}]}
            """)).ReviewBlockAsync(word, EntryBlock.SynonymNuances, default);
        Assert.Equal(6, reviewed.SynonymNuances.Count);
        Assert.Equal(word.SynonymNuances, reviewed.SynonymNuances.Take(5));
    }

    [Theory]
    [InlineData(EntryBlock.WordFormation, VocabularyItemType.Word, "etymologyNotes")]
    [InlineData(EntryBlock.Mnemonic, VocabularyItemType.Word, "mnemonics")]
    [InlineData(EntryBlock.Culture, VocabularyItemType.Idiom, "culturalNotes")]
    [InlineData(EntryBlock.Tone, VocabularyItemType.Sentence, "toneEvidence")]
    public async Task NewStructuredNotesStreamReviewPersistAndKeepProgress(EntryBlock block, VocabularyItemType type, string field)
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var original = new DictionaryEntryResult { SourceText = "Please wait.", PrimaryTranslation = "请稍等。", ItemType = type,
                DetectedSourceLanguage = "en", EtymologyAndRoot = "旧词源", MemoryMnemonic = "旧助记", CulturalNote = "旧文化说明",
                EtymologyNotes = [new("原有主题", "原有解释")], Mnemonics = [new("原有方法", "原有线索", "原有解释")],
                CulturalNotes = [new("原有背景", "原有解释")], ToneEvidence = [new("Please", "礼貌", "原有解释")] };
            var cards = new VocabularyService(Path.Combine(folder.FullName, "cards.json"));
            var collected = cards.AddOrUpdateFromDictionary(original, sourceTitle: "阅读笔记");
            collected = cards.SaveEntry(collected with { UserPersonalNote = "个人记录" });
            var progress = cards.SubmitReview(collected.Id, ReviewRating.Good);
            var cache = new DictionaryEntryCache(Path.Combine(folder.FullName, "cache.db"));
            JsonObject value = block switch
            {
                EntryBlock.Mnemonic => new() { ["method"] = "场景联想", ["cue"] = "等待的画面", ["explanation"] = "新的 **有效** 说明。" },
                EntryBlock.Tone => new() { ["quote"] = "wait", ["aspect"] = "请求动作", ["explanation"] = "新的 **有效** 说明。" },
                _ => new() { ["title"] = "补充层面", ["explanation"] = "新的 **有效** 说明。" }
            };
            var response = new JsonObject { ["status"] = "improved", ["edits"] = new JsonArray(
                new JsonObject { ["path"] = $"/{field}/-", ["value"] = value }) }.ToJsonString();
            var service = new AiDictionaryService(() => new Reply(response), cache, refreshCollectedEntries: cards.RefreshCollectedEntries);
            var frames = new List<DictionaryEntryResult>();
            var reviewed = await service.ReviewBlockAsync(original, block, default, frames.Add);
            Assert.NotEmpty(frames);
            var reopened = new VocabularyService(Path.Combine(folder.FullName, "cards.json"));
            var saved = reopened.FindByHeadword(original.SourceText)!;
            var fieldName = char.ToUpperInvariant(field[0]) + field[1..];
            object? Read(DictionaryEntryResult e) => typeof(DictionaryEntryResult).GetProperty(fieldName)!.GetValue(e);
            Assert.Equal(JsonSerializer.Serialize(Read(reviewed)), JsonSerializer.Serialize(Read(saved.ToDictionaryResult())));
            Assert.Equal(JsonSerializer.Serialize(Read(reviewed)), JsonSerializer.Serialize(Read(cache.Read(reviewed.CacheStamp!.Key)!)));
            Assert.Equal(progress.TotalReviews, saved.TotalReviews);
            Assert.Equal(progress.NextReviewAt, saved.NextReviewAt);
            Assert.Equal("阅读笔记", saved.SourceTitle);
            Assert.Equal("个人记录", saved.UserPersonalNote);
            Assert.Equal("旧词源", saved.EtymologyAndRoot);
            Assert.Equal("旧助记", saved.MemoryMnemonic);
            Assert.Equal("旧文化说明", saved.CulturalNote);
            var filter = new VocabularyQueryFilter();
            Assert.Contains("新的 有效 说明。", reopened.ExportToCsv(filter));
            Assert.Contains("新的 有效 说明。", reopened.ExportToAnkiTsv(filter));
            Assert.DoesNotContain("**有效**", reopened.ExportToCsv(filter));
            Assert.DoesNotContain("**有效**", reopened.ExportToAnkiTsv(filter));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public void NewFieldsRespectTypeAnchorsAndLegacyEntries()
    {
        var best = AiDictionaryService.ParseDictionaryResponse("best", "zh-Hans", Best);
        Assert.Equal(3, best.EtymologyNotes.Count);
        Assert.Equal(3, best.Mnemonics.Count);
        var legacy = AiDictionaryService.ParseDictionaryResponse("best", "zh-Hans", """{"primaryTranslation":"最好","etymologyAndRoot":"旧词源","memoryMnemonic":"旧助记"}""");
        Assert.Empty(legacy.EtymologyNotes);
        Assert.Empty(legacy.Mnemonics);
        var sentence = AiDictionaryService.ParseDictionaryResponse("Please wait.", "zh-Hans", """
            {"primaryTranslation":"请稍等。","itemType":"Sentence","toneEvidence":[
             {"quote":"Please","aspect":"礼貌请求","explanation":"使用礼貌标记"},
             {"quote":"Invented","aspect":"伪引文","explanation":"不应保留"}],
             "mnemonics":[{"method":"联想","cue":"画面","explanation":"不适用"}],
             "culturalNotes":[null,{"title":"缺少说明"},{"title":"礼貌习惯","explanation":"简短说明"}]}
            """);
        Assert.Single(sentence.ToneEvidence);
        Assert.Single(sentence.CulturalNotes);
        Assert.Empty(sentence.Mnemonics);
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var cards = new VocabularyService(Path.Combine(folder.FullName, "cards.json"));
            cards.AddOrUpdateFromDictionary(best);
            cards.AddOrUpdateFromDictionary(legacy);
            var saved = cards.FindByHeadword("best")!;
            Assert.Equal(best.Mnemonics, saved.ToDictionaryResult().Mnemonics);
            Assert.Equal(best.EtymologyNotes, saved.ToDictionaryResult().EtymologyNotes);
            var terms = Enumerable.Range(0, 10).Select(i => $"technical term {i}").ToArray();
            foreach (var term in terms) cards.AddOrUpdateFromDictionary(new() { SourceText = term, PrimaryTranslation = "术语", ItemType = VocabularyItemType.TechnicalTerm });
            Assert.Equal(10, cards.FindGlossaryTerms(string.Join("; ", terms), "zh-Hans").Count);
            Assert.Empty(cards.FindGlossaryTerms("unrelated", "zh-Hans"));
            Assert.Empty(cards.FindGlossaryTerms(string.Join("; ", terms), "ja"));
        }
        finally { folder.Delete(true); }
    }

    [Fact]
    public async Task FactualNotesAndMemoryCuesRemainDistinctAtReadingAndNarrowWidths()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(DictionaryInputTests));
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            await session.Dispatch(() =>
            {
                using var speech = new KokoroSpeechService(folder.FullName);
                var details = new DictionaryDetails(speech, new AppSettingsStore(Path.Combine(folder.FullName, "settings.json")), (_, _, _) => { });
                var window = new Window { Width = 800, Height = 700, Content = new ScrollViewer { Content = details } };
                window.Show();
                try
                {
                    var word = AiDictionaryService.ParseDictionaryResponse("best", "zh-Hans", Best);
                    foreach (var width in new[] { 760, 354 })
                    {
                        details.Width = width;
                        details.ShowEntry(word, complete: false);
                        window.UpdateLayout();
                        Assert.Contains(Texts(), t => t.Text == "拆分边界");
                        details.ShowEntry(word, complete: true);
                        window.UpdateLayout();
                        Assert.All(word.EtymologyNotes, n => Assert.Contains(Texts(), t => t.Text == n.Title));
                        Assert.All(word.Mnemonics, m => Assert.Contains(Texts(), t => t.Text == m.Cue));
                        var cue = Texts().Single(t => t.Text == word.Mnemonics[0].Cue);
                        var explanation = Texts().Single(t => t.Inlines?.Text == InlineMarkup.Strip(word.Mnemonics[0].Explanation));
                        Assert.True(cue.FontSize > explanation.FontSize);
                        Assert.NotEqual(cue.Foreground, explanation.Foreground);
                        cue.SelectAll();
                        Assert.Equal(word.Mnemonics[0].Cue, cue.SelectedText);
                        foreach (var t in Texts()) Assert.InRange(t.TranslatePoint(new Point(t.Bounds.Width, 0), details)!.Value.X, 0, width + 1);
                    }
                    details.ShowEntry(word with { EtymologyNotes = [], Mnemonics = [], EtymologyAndRoot = "旧词源", MemoryMnemonic = "旧助记" }, complete: true);
                    window.UpdateLayout();
                    Assert.Contains(Texts(), t => t.Text == "旧词源" || t.Inlines?.Text == "旧词源");
                    Assert.Contains(Texts(), t => t.Text == "旧助记" || t.Inlines?.Text == "旧助记");
                    var popup = new SelectionPopupBody(speech, new AppSettingsStore(Path.Combine(folder.FullName, "popup.json")), (_, _) => { });
                    window.Content = popup;
                    popup.ShowEntry(word, complete: true);
                    window.UpdateLayout();
                    Assert.Contains(popup.GetVisualDescendants().OfType<TextBlock>(), t => (t.Text ?? t.Inlines?.Text)?.Contains(word.Mnemonics[0].Cue) == true);
                    var cards = new VocabularyService(Path.Combine(folder.FullName, "cards.json"));
                    var notebook = cards.CreateNotebook("栏目验收");
                    var saved = cards.AddOrUpdateFromDictionary(word, notebook.Id);
                    var reading = new DictionaryResultView(saved.Headword, saved.TargetLanguage, speech,
                        new AppSettingsStore(Path.Combine(folder.FullName, "review.json")), (_, _, _) => { });
                    window.Content = new ScrollViewer { Content = reading };
                    reading.ShowEntry(saved.ToDictionaryResult(), complete: true);
                    window.UpdateLayout();
                    Assert.All(word.Mnemonics, m => Assert.Contains(reading.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == m.Cue));
                    Assert.All(word.EtymologyNotes, n => Assert.Contains(reading.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == n.Title));
                    SelectableTextBlock[] Texts() => details.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).ToArray();
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { folder.Delete(true); }
    }

    private sealed class Reply(string response, Action<IReadOnlyList<JsonObject>>? inspect = null, IReadOnlyList<string>? chunks = null) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            inspect?.Invoke(messages);
            foreach (var chunk in chunks ?? response.Chunk(32).Select(c => new string(c)).ToArray()) onText(chunk);
            return Task.FromResult(new AiCompletion(response, []));
        }
    }
}
