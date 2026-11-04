using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class LexicalBlockReviewTests
{
    private static readonly DictionaryEntryResult Provide = new()
    {
        SourceText = "provide", DetectedSourceLanguage = "en", PrimaryTranslation = "提供", Senses = [new("v.", "提供")],
        WordFamily = [new("供应；条款", "provision", "n.", "derived", "指供应行为", "the provision of clean water")],
        Collocations = [new("provide for the future", "为未来做准备", Example: new("We provide for the future.", "我们为未来做准备。"))]
    };

    [Fact]
    public async Task RetainsFamilyExampleAndSkipsNewRowsThatExplicitlyBelongToAnotherSection()
    {
        var service = Service("""
            {"status":"improved","edits":[{"path":"/wordFamily/0/usage","value":"可指供应行为，也可指法律条款。"},
            {"path":"/wordFamily/0/example","value":{"sourceSentence":"The provision of clean water is essential.","translatedSentence":"清洁用水的供应至关重要。","highlightTerm":"provision"}},
            {"path":"/wordFamily/-","value":{"label":"供养","value":"provide for","relation":"collocation"}},
            {"path":"/wordFamily/-","value":{"label":"预作安排","value":"provide for the future","relation":"sameForm"}}]}
            """);
        var result = await service.ReviewBlockAsync(Provide, EntryBlock.WordFamily, default);
        var form = Assert.Single(result.WordFamily);
        Assert.Equal("可指供应行为，也可指法律条款。", form.Usage);
        Assert.Equal("清洁用水的供应至关重要。", form.Example?.TranslatedSentence);
        Assert.Equal(Provide.Collocations, result.Collocations);
        Assert.Same(result, await Service("""{"status":"improved","edits":[{"path":"/wordFamily/0","value":"provision"}]}""")
            .ReviewBlockAsync(result, EntryBlock.WordFamily, default));
        await Assert.ThrowsAsync<AiException>(() => Service("""
            {"status":"improved","edits":[{"path":"/wordFamily/-","value":{"value":"provides","relation":"plural"}}]}
            """).ReviewBlockAsync(Provide, EntryBlock.WordFamily, default));
    }

    [Fact]
    public async Task FlattendedRelatedTermIsRestoredButAnExistingInflectionIsNotAddedAsADerivative()
    {
        var entry = Provide with { SourceText = "feedback loop", ItemType = VocabularyItemType.TechnicalTerm,
            WordFamily = [], Inflections = [new("复数", "feedback loops", "n.")] };
        var updated = await Service("""
            {"status":"improved","edits":[
             {"path":"/wordFamily/-","value":{"value":"feedback loops","label":"反馈回路","relation":"derived","partOfSpeech":"n.","usage":"复数","expression":"two feedback loops"}},
             {"path":"/wordFamily/-","value":"feedback","label":"反馈","relation":"derived","partOfSpeech":"n.","usage":"词组的组成部分","expression":"positive feedback"},
             {"path":"/wordFamily/-","value":"control loop","partOfSpeech":"n.","relation":"confusable","usage":"侧重控制系统的回路。","expression":"a control loop","label":"控制回路"}]}
            """).ReviewBlockAsync(entry, EntryBlock.WordFamily, default);
        Assert.Equal("control loop", Assert.Single(updated.WordFamily).Value);
        Assert.Equal(entry.Inflections, updated.Inflections);
    }

    [Fact]
    public async Task KeepsAnAnchoredExampleWhenAnotherSuggestionIsOnlyAboutTheHeadword()
    {
        var updated = await Service("""
            {"status":"improved","edits":[{"path":"/collocations/0/usageNote","value":"搭配中的 for 指向未来。"},
             {"path":"/collocations/0/example","value":{"sourceSentence":"Trees provide shade for the village.","translatedSentence":"树木为村庄提供阴凉。"}}]}
            """).ReviewBlockAsync(Provide, EntryBlock.Collocations, default);
        var row = Assert.Single(updated.Collocations);
        Assert.Equal("搭配中的 for 指向未来。", row.UsageNote);
        Assert.Equal(Provide.Collocations[0].Example, row.Example);
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "words.json");
            new VocabularyService(path).AddOrUpdateFromDictionary(updated);
            var saved = new VocabularyService(path).FindByHeadword("provide")!;
            Assert.Equal(row, Assert.Single(saved.ToDictionaryResult().Collocations));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task InflectionLabelsAndPairedContrastFieldsSurviveProviderRowFormats()
    {
        var entry = Provide with
        {
            ItemType = VocabularyItemType.Idiom, SourceText = "break the ice", PrimaryTranslation = "打破僵局",
            Inflections = [new("变体", "broke the ice")], UsageContrasts = [new("break the ice", "开场", "ease tension", "缓和")]
        };
        var inflected = await Service("""
            {"status":"improved","edits":[{"path":"/inflections/0/partOfSpeech","value":"过去式"},
             {"path":"/inflections/-","value":"breaks the ice","partOfSpeech":"第三人称单数","label":"变体"}]}
            """).ReviewBlockAsync(entry, EntryBlock.Inflections, default);
        Assert.Equal([new("过去式", "broke the ice"), new("第三人称单数", "breaks the ice")], inflected.Inflections);
        var compared = await Service("""
            {"status":"improved","edits":[{"path":"/usageContrasts/0","value":{"leftForm":"break the ice","leftUse":"打开社交局面"},
              "rightForm":"ease tension","rightUse":"缓和紧张气氛"}]}
            """).ReviewBlockAsync(entry, EntryBlock.UsageContrasts, default);
        Assert.Equal("缓和紧张气氛", Assert.Single(compared.UsageContrasts).RightUse);
    }

    private static AiDictionaryService Service(string reply) => new(() => new Reply(reply));

    private sealed class Reply(string reply) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText,
            CancellationToken token) => Task.FromResult(new AiCompletion(reply, []));
    }
}
