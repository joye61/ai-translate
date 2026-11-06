using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class DictionaryBlockReviewTests
{
    private static readonly DictionaryEntryResult Coffee = new()
    {
        SourceText = "coffee", PrimaryTranslation = "咖啡", LookupContext = "Two coffees, please.",
        DetectedSourceLanguage = "en", ReaderLanguage = "zh-Hans",
        Senses = [new("n.", "咖啡饮品", Example: new("I like coffee.", "我喜欢咖啡。", "coffee")),
            new("n.", "咖啡豆")],
        Collocations = [new("black coffee", "黑咖啡")]
    };

    [Fact]
    public async Task SavesOnlyReviewedSectionAndReopensFromDiskWithoutContactingAi()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "lookup.db"));
            var cards = new VocabularyService(Path.Combine(dir.FullName, "cards.json"));
            var card = cards.AddOrUpdateFromDictionary(Coffee, contextSentence: Coffee.LookupContext, sourceTitle: "菜单");
            card = cards.SaveEntry(card with { UserPersonalNote = "点单" });
            var progress = cards.SubmitReview(card.Id, ReviewRating.Good);
            var otherBook = cards.CreateNotebook("听力");
            var otherCard = cards.AddOrUpdateFromDictionary(Coffee, otherBook.Id);
            BilingualExample personalExample = new("Is there coffee?", "有咖啡吗？");
            cards.SaveEntry(otherCard with { Examples = [personalExample] });
            var calls = 0;
            var service = new AiDictionaryService(() => new Reply(payload =>
            {
                calls++;
                Assert.Equal(Coffee.LookupContext, (string?)payload["context_sentence"]);
                Assert.Equal("zh-Hans", (string?)payload["reader_language"]);
                Assert.Equal("词性与释义", (string?)payload["section"]);
                Assert.Equal("black coffee", (string?)payload["entry_context"]!["collocations"]![0]!["phrase"]);
                Assert.Equal("咖啡豆", (string?)payload["current_section"]!["senses"]![1]!["definitionZh"]);
                return calls == 1 ? """
                    {"status":"improved","edits":[{"path":"/senses/0/scenario","value":"按杯点单时可数。"}]}
                    """ : """{"status":"unchanged"}""";
            }), cache, refreshCollectedEntries: cards.RefreshCollectedEntries, readerLanguageId: () => "zh-Hans");
            var updated = await service.ReviewBlockAsync(card.ToDictionaryResult(), EntryBlock.Senses, default);
            Assert.Equal(Coffee.Collocations, updated.Collocations);
            Assert.Equal(Coffee.Senses[1], updated.Senses[1]);
            Assert.Equal(Coffee.Senses[0].Example, updated.Senses[0].Example);
            Assert.Equal("按杯点单时可数。", updated.Senses[0].Scenario);
            Assert.Same(updated, await service.ReviewBlockAsync(updated, EntryBlock.Senses, default));
            var offline = new AiDictionaryService(() => throw new Exception("unexpected AI request"), cache,
                readerLanguageId: () => "zh-Hans");
            var reopened = await offline.LookupOrTranslateAsync("coffee", "zh-Hans", Coffee.LookupContext);
            Assert.Equal(updated.Senses, reopened.Senses);
            Assert.Equal(updated.CacheStamp, reopened.CacheStamp);
            var reopenedCards = new VocabularyService(Path.Combine(dir.FullName, "cards.json")).QueryEntries(new());
            var saved = reopenedCards.Single(c => c.Id == card.Id);
            Assert.Equal(personalExample, Assert.Single(reopenedCards.Single(c => c.Id == otherCard.Id).Examples));
            Assert.Equal(updated.CacheStamp, saved.ToDictionaryResult().CacheStamp);
            Assert.Equal(updated.Senses, saved.Senses);
            Assert.Equal(progress.NextReviewAt, saved.NextReviewAt);
            Assert.Equal("菜单", saved.SourceTitle);
            Assert.Equal("点单", saved.UserPersonalNote);
            Assert.Equal(1, calls); // The second click reused the review for this content and context.
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task PrimaryReviewRefreshesTheSavedTranslationAndDropsObsoleteQuotes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var entry = new DictionaryEntryResult
            {
                SourceText = "Close the door.", ItemType = VocabularyItemType.Sentence, PrimaryTranslation = "靠近门。",
                LiteralTranslation = "关上那扇门。", ReaderLanguage = "zh-Hans", LookupContext = "He pointed to the door.",
                TranslationDecisions = [new("Close", "靠近", "取接近义。")], UsageNotes = "祈使句。"
            };
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "lookup.db"));
            var path = Path.Combine(dir.FullName, "cards.json");
            var cards = new VocabularyService(path);
            var card = cards.AddOrUpdateFromDictionary(entry, contextSentence: entry.LookupContext, sourceTitle: "课堂");
            card = cards.SaveEntry(card with { UserPersonalNote = "听力" });
            var progress = cards.SubmitReview(card.Id, ReviewRating.Good);
            var service = new AiDictionaryService(() => new Reply(payload =>
            {
                Assert.Equal("自然译文", (string?)payload["section"]);
                Assert.Equal("primaryTranslation", Assert.Single(payload["current_section"]!.AsObject()).Key);
                return """{"status":"improved","fields":{"primaryTranslation":"请关门。"}}""";
            }), cache, refreshCollectedEntries: cards.RefreshCollectedEntries);
            var result = await service.ReviewBlockAsync(entry, EntryBlock.PrimaryTranslation, default);
            Assert.Empty(result.TranslationDecisions);
            Assert.True(result.Processing!.CanResumeExtension);
            Assert.Equal(entry.UsageNotes, result.UsageNotes);
            var saved = new VocabularyService(path).QueryEntries(new() { NotebookId = card.NotebookId }).Single(c => c.Id == card.Id);
            Assert.Equal("请关门。", saved.Translation);
            Assert.Equal(entry.LiteralTranslation, saved.LiteralTranslation);
            Assert.Empty(saved.TranslationDecisions);
            Assert.Equal(entry.LookupContext, saved.ContextSentence);
            Assert.Equal("听力", saved.UserPersonalNote);
            Assert.Equal(progress.NextReviewAt, saved.NextReviewAt);
            Assert.Same(result, await service.ReviewBlockAsync(result, EntryBlock.PrimaryTranslation, default));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task PrimaryWordReviewRemovesNotesForReplacedRenderings()
    {
        var entry = Coffee with { PrimaryTranslation = "咖啡；茶",
            TranslationOptions = [new("咖啡", "饮品", "通常用法"), new("茶", "饮品", "错误译义")] };
        var service = new AiDictionaryService(() => new Reply(_ =>
            """{"status":"improved","fields":{"primaryTranslation":"咖啡；咖啡豆"}}"""));
        var result = await service.ReviewBlockAsync(entry, EntryBlock.PrimaryTranslation, default);
        Assert.Equal(entry.TranslationOptions[0], Assert.Single(result.TranslationOptions));
        Assert.Equal(entry.Senses, result.Senses);
        Assert.Equal(entry.Collocations, result.Collocations);
    }

    [Fact]
    public async Task OlderReviewCannotOverwriteAConcurrentRevision()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.db"));
            var first = new AiDictionaryService(() => new Reply(_ =>
                """{"status":"improved","edits":[{"path":"/senses/0/scenario","value":"点单"}]}"""), cache);
            var entry = await first.ReviewBlockAsync(Coffee, EntryBlock.Senses, default);
            var stamp = entry.CacheStamp!;
            var late = new AiDictionaryService(() => new Reply(_ =>
            {
                Assert.True(cache.TryReplace(stamp, entry with { PrimaryTranslation = "更新的译文" }, stamp.Revision + 1));
                return """{"status":"improved","edits":[{"path":"/usageNotes","value":"餐馆点单"}]}""";
            }), cache);
            // A different, still-unchecked block must use the same compare-and-swap guard.
            await Assert.ThrowsAsync<AiException>(() => late.ReviewBlockAsync(entry, EntryBlock.UsageNotes, default));
            Assert.Equal("更新的译文", cache.Read(stamp.Key)!.PrimaryTranslation);
            var stale = new AiDictionaryService(() => throw new Exception("stale review contacted AI"), cache);
            await Assert.ThrowsAsync<AiException>(() => stale.ReviewBlockAsync(entry, EntryBlock.Senses, default));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task EveryCatalogBlockHasAReviewableFieldAndCanReturnUnchanged()
    {
        foreach (var type in Enum.GetValues<VocabularyItemType>())
        foreach (var section in EntryBlockCatalog.For(type))
        {
            var service = new AiDictionaryService(() => new Reply(payload =>
            {
                var current = Assert.IsType<JsonObject>(payload["current_section"]);
                Assert.NotEmpty(current);
                Assert.All(current, field => Assert.True(typeof(DictionaryEntryResult).GetProperties()
                    .Any(p => p.Name.Equals(field.Key, StringComparison.OrdinalIgnoreCase)), field.Key));
                Assert.Equal(section.Title, (string?)payload["section"]);
                return """{"status":"unchanged"}""";
            }));
            var entry = Coffee with { ItemType = type };
            Assert.Same(entry, await service.ReviewBlockAsync(entry, section.Block, default));
        }
    }

    [Theory]
    [InlineData("""{"path":"/primaryTranslation","value":"别的译文"}""")]
    [InlineData("""{"path":"/senses","value":[]}""")]
    [InlineData("""{"path":"/senses/0","value":{"definitionZh":"简化"}}""")]
    [InlineData("""{"path":"/senses/1/definitionZh","value":" "}""")]
    [InlineData("""{"path":"/senses/10/scenario","value":"越界"}""")]
    [InlineData("""{"path":"/senses/0/unknown","value":"未知字段"}""")]
    [InlineData("""{"path":"/senses/-","value":{"scenario":"缺少释义"}}""")]
    public async Task InvalidEditRejectsWholeReply(string invalid)
    {
        var service = new AiDictionaryService(() => new Reply(_ =>
            """{"status":"improved","edits":[{"path":"/senses/0/scenario","value":"测试"},""" + invalid + "]}"));
        await Assert.ThrowsAsync<AiException>(() => service.ReviewBlockAsync(Coffee, EntryBlock.Senses, default));
        Assert.Equal("", Coffee.Senses[0].Scenario);
    }

    [Fact]
    public async Task ProvideReviewKeepsWordFormationEditsWhenTheEngineAlsoReviewsOtherSections()
    {
        var entry = new DictionaryEntryResult { SourceText = "provide", PrimaryTranslation = "提供",
            Senses = [new("v.", "提供")], Collocations = [new("provide for sb", "供养某人")],
            Morphemes = [new("pro-", "向前", MorphemeKind.Prefix), new("vide", "看", MorphemeKind.Root),
                new("-e", "动词词尾（拉丁语）", MorphemeKind.Suffix)],
            SynonymNuances = [new("provide", "中性"), new("supply", "强调供应物资"), new("offer", "主动提出")] };
        // A real reply for this entry mixed valid morpheme edits with both forms of background paths.
        var service = new AiDictionaryService(() => new Reply(_ => """
            {"status":"improved","edits":[
              {"path":"/morphemes/2/meaning","value":"英语拼写中的词尾，不单独表义"},
              {"path":"/entry_context/pronunciationTip","value":"重音在第二音节"},
              {"path":"/senses/2/usageCases/-","value":{"condition":"供养某人"}},
              {"path":"/collocations/2/senseHint","value":"及物"},
              {"path":"/synonymNuances/1/nuance","value":"商业和物流"},
              {"path":"/etymologyAndRoot","value":"来自拉丁语 providēre。"}]}
            """));
        var result = await service.ReviewBlockAsync(entry, EntryBlock.WordFormation, default);
        Assert.Equal("英语拼写中的词尾，不单独表义", result.Morphemes[2].Meaning);
        Assert.Equal("来自拉丁语 providēre。", result.EtymologyAndRoot);
        Assert.Equal(entry.PronunciationTip, result.PronunciationTip);
        Assert.Equal(entry.Senses, result.Senses);
        Assert.Equal(entry.Collocations, result.Collocations);
        Assert.Equal(entry.SynonymNuances, result.SynonymNuances);
    }

    [Fact]
    public async Task WordFormationReviewAcceptsACompleteReplacementRowReturnedByTheEngine()
    {
        var entry = Coffee with { SourceText = "provide", Morphemes = [new("pro-", "向前", MorphemeKind.Prefix),
            new("vide", "看", MorphemeKind.Root), new("-e", "动词词尾", MorphemeKind.Suffix)] };
        var service = new AiDictionaryService(() => new Reply(_ => """
            {"status":"improved","edits":[{"path":"/morphemes/2","value":{"part":"-e","meaning":"此处不单独表义","kind":"suffix"},"op":"replace"},
            {"path":"/etymologyAndRoot","value":"拉丁语 providēre 有预先看见之意。"}]}
            """));
        var result = await service.ReviewBlockAsync(entry, EntryBlock.WordFormation, default);
        Assert.Equal(entry.Morphemes.Take(2), result.Morphemes.Take(2));
        Assert.Equal(new Morpheme("-e", "此处不单独表义", MorphemeKind.Suffix), result.Morphemes[2]);
        Assert.Equal("拉丁语 providēre 有预先看见之意。", result.EtymologyAndRoot);
    }

    [Fact]
    public async Task WordFormationReviewPreservesAnExplicitBoundRootAndAcceptsTrimmedText()
    {
        var entry = Coffee with { SourceText = "provide", Morphemes = [new("pro-", "向前", MorphemeKind.Prefix),
            new("vide", "看", MorphemeKind.Root)] };
        var service = new AiDictionaryService(() => new Reply(_ => """
            {"status":"improved","edits":[{"path":"/morphemes/1/part","value":" vid- "}]}
            """));
        var result = await service.ReviewBlockAsync(entry, EntryBlock.WordFormation, default);
        Assert.Equal(new Morpheme("vid-", "看", MorphemeKind.Root), result.Morphemes[1]);
    }

    [Fact]
    public async Task SynonymReviewAcceptsExplicitReplaceAndAppendWithoutChangingOtherBlocks()
    {
        var entry = new DictionaryEntryResult { SourceText = "provide", PrimaryTranslation = "提供",
            SynonymNuances = [new("provide", "中性"), new("supply", "供应物资"), new("offer", "主动提出")] };
        var service = new AiDictionaryService(() => new Reply(_ => """
            {"status":"improved","edits":[{"op":"replace","path":"/current_section/synonymNuances/0/nuance","value":"按需要提供"},
            {"op":"add","path":"/synonymNuances/-","value":{"word":"furnish","nuance":"较正式地提供"}},
            {"path":"/etymologyAndRoot","value":"其他栏目的建议"}]}
            """));
        var result = await service.ReviewBlockAsync(entry, EntryBlock.SynonymNuances, default);
        Assert.Equal("按需要提供", result.SynonymNuances[0].Nuance);
        Assert.Equal("furnish", result.SynonymNuances[3].Word);
        Assert.Equal(entry.EtymologyAndRoot, result.EtymologyAndRoot);
    }

    [Fact]
    public async Task ReordersExistingSensesWithoutRewritingTheirExamples()
    {
        var service = new AiDictionaryService(() => new Reply(_ =>
            """{"status":"improved","edits":[{"op":"move","from":"/senses/1","path":"/senses/0"}]}"""));
        var updated = await service.ReviewBlockAsync(Coffee, EntryBlock.Senses, default);
        Assert.Equal(Coffee.Senses.Reverse(), updated.Senses);
    }

    [Fact]
    public async Task SourceReviewWritesAnAnnotationAndCannotRewriteSourceOrOtherSections()
    {
        var sentence = Coffee with { ItemType = VocabularyItemType.Sentence, SourceText = "Two coffees, please." };
        var service = new AiDictionaryService(() => new Reply(_ => """
            {"status":"improved","edits":[{"path":"/sourceReadingNote","value":"点单时 two coffees 指两杯咖啡。"}]}
            """));
        var updated = await service.ReviewBlockAsync(sentence, EntryBlock.Source, default);
        Assert.Equal(sentence.SourceText, updated.SourceText);
        Assert.Equal(sentence.PrimaryTranslation, updated.PrimaryTranslation);
        Assert.NotEmpty(updated.SourceReadingNote);
        Assert.Same(updated, await service.ReviewBlockAsync(updated, EntryBlock.Source, default));
        await Assert.ThrowsAsync<AiException>(() => service.ReviewBlockAsync(sentence, EntryBlock.Variants, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamsDecodedEditsBeforeCompletionAndOnlyCommitsAValidFinalReply(bool invalidFinal)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.db"));
            Assert.True(cache.TryAdd("coffee", Coffee, 1));
            var original = cache.Read("coffee")!;
            var frames = new List<DictionaryEntryResult>();
            var service = new AiDictionaryService(() => new StreamReply(emit =>
            {
                var previousFrames = frames.Count;
                const string start = """{"status":"improved","edits":[{"path":"/senses/0/definitionZh","value":"咖啡（""";
                emit(start);
                Assert.Equal(previousFrames + 1, frames.Count);
                Assert.Equal("咖啡（", frames[^1].Senses[0].DefinitionZh);
                const string next = """饮品）"}""";
                emit(next);
                Assert.Equal("咖啡（饮品）", frames[^1].Senses[0].DefinitionZh);
                Assert.All(frames, frame => Assert.Equal(original.Collocations, frame.Collocations));
                Assert.Equal(original.Senses, cache.Read("coffee")!.Senses);
                var end = invalidFinal ? """,{"path":"/primaryTranslation","value":"越界"}]}""" : "]}";
                emit(end);
                return start + next + end;
            }), cache);
            var request = service.ReviewBlockAsync(original, EntryBlock.Senses, default, frames.Add);
            if (invalidFinal) await Assert.ThrowsAsync<AiException>(() => request);
            else Assert.Equal("咖啡（饮品）", (await request).Senses[0].DefinitionZh);
            var saved = cache.Read("coffee")!;
            Assert.Equal(invalidFinal ? original.Senses[0].DefinitionZh : "咖啡（饮品）", saved.Senses[0].DefinitionZh);
            Assert.Equal(invalidFinal, saved.CacheStamp == original.CacheStamp);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task ReclassifiesSuggestedTagsWithoutChangingPersonalNotebookTags()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var cards = new VocabularyService(Path.Combine(directory.FullName, "cards.json"));
            var entry = Coffee with { SuggestedTags = ["英语表达"] };
            var card = cards.AddOrUpdateFromDictionary(entry);
            cards.SaveEntry(card with { Tags = ["我的复习课"] });
            var service = new AiDictionaryService(() => new Reply(_ => """
                {"status":"improved","edits":[{"path":"/suggestedTags","value":["饮品","餐厅点单","可数与不可数"]}]}
                """), refreshCollectedEntries: cards.RefreshCollectedEntries);
            var updated = await service.ReviewBlockAsync(entry, EntryBlock.Tags, default);
            var stored = cards.QueryEntries(new() { NotebookId = card.NotebookId }).Single(c => c.Id == card.Id);
            Assert.Equal(new[] { "饮品", "餐厅点单", "可数与不可数" }, updated.SuggestedTags);
            Assert.Equal(updated.SuggestedTags, stored.SuggestedTags);
            Assert.Equal(new[] { "我的复习课" }, stored.Tags);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task PlainTextFieldsStreamWithoutPathsAndAClippedReplyCannotCommitAPreview()
    {
        const string start = """{"status":"improved","fields":{"usageNotes":"按杯点单""";
        const string end = """时可数。","grammarBreakdown":"此处为复数。"}}""";
        var frames = new List<DictionaryEntryResult>();
        var service = new AiDictionaryService(() => new StreamReply(emit =>
        {
            emit(start);
            Assert.Equal("按杯点单", frames[^1].UsageNotes);
            emit(end);
            return start + end;
        }));
        var result = await service.ReviewBlockAsync(Coffee, EntryBlock.UsageNotes, default, frames.Add);
        Assert.Equal("按杯点单时可数。", result.UsageNotes);
        Assert.Equal("此处为复数。", result.GrammarBreakdown);
        Assert.Equal(Coffee.Senses, result.Senses);

        var clipped = new AiDictionaryService(() => new StreamReply(emit => { emit(start); return start; }));
        await Assert.ThrowsAsync<AiException>(() => clipped.ReviewBlockAsync(Coffee, EntryBlock.UsageNotes, default, frames.Add));
        Assert.True(string.IsNullOrEmpty(Coffee.UsageNotes));
    }

    [Fact]
    public async Task ReviewAcceptsFiveNuancesInASingleListAndRejectsDroppingAnOldRegister()
    {
        var original = Coffee with { SynonymNuances = [new("coffee", "泛指咖啡", "通用"), new("espresso", "浓缩咖啡")] };
        const string rows = """
            [{"word":"coffee","nuance":"咖啡的统称","register":"通用"},{"word":"latte","nuance":"牛奶咖啡"},
             {"word":"mocha","nuance":"含巧克力的咖啡"},{"word":"espresso","nuance":"一小份浓缩咖啡"},{"word":"cappuccino","nuance":"含奶泡的咖啡"}]
            """;
        AiDictionaryService Review(string values) => new(() => new Reply(_ =>
            """{"status":"improved","edits":[{"path":"/synonymNuances","value":""" + values + "}]}"));
        var result = await Review(rows).ReviewBlockAsync(original, EntryBlock.SynonymNuances, default);
        Assert.Equal(5, result.SynonymNuances.Count);
        Assert.Equal("通用", result.SynonymNuances[0].Register);
        await Assert.ThrowsAsync<AiException>(() => Review(rows.Replace(",\"register\":\"通用\"", ""))
            .ReviewBlockAsync(original, EntryBlock.SynonymNuances, default));
    }

    [Fact]
    public async Task ReviewMaintainsVariantIndexesUntilEveryEditHasBeenApplied()
    {
        var phrase = Coffee with { SourceText = "carry out", PrimaryTranslation = "执行", ItemType = VocabularyItemType.Phrase,
            Variants = [new("书面", "实施", "用于计划")] };
        var service = new AiDictionaryService(() => new Reply(_ => """
            {"status":"improved","edits":[{"path":"/variants/-","value":{"style":"中性","text":"执行"}},
            {"path":"/variants/2","value":{"style":"口语","text":"做"}},
            {"path":"/variants/2/note","value":"适用于日常对话"}]}
            """));
        var result = await service.ReviewBlockAsync(phrase, EntryBlock.Variants, default);
        Assert.Equal(2, result.Variants.Count);
        Assert.Equal(phrase.Variants[0], result.Variants[0]);
        Assert.Equal("适用于日常对话", result.Variants[1].Note);
    }

    private sealed class StreamReply(Func<Action<string>, string> stream) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token) =>
            Task.FromResult(new AiCompletion(stream(onText), []));
    }

    private sealed class Reply(Func<JsonObject, string> respond) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token) =>
            Task.FromResult(new AiCompletion(respond(JsonNode.Parse((string)messages[1]["content"]!)!.AsObject()), []));
    }
}
