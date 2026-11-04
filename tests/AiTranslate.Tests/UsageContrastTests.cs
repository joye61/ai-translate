using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class UsageContrastTests
{
    [Fact]
    public void OnlyCompleteDistinctContrastsReachTheFinalWordEntry()
    {
        const string json = """
            {"primaryTranslation":"停止","itemType":"Word","usageContrasts":[
              null,{"leftForm":"stop doing","rightForm":"stop to do"},
              {"leftForm":"stop","leftUse":"停","rightForm":"stop","rightUse":"终止"},
              {"leftForm":" stop doing ","leftUse":"结束这个动作","rightForm":"stop to do","rightUse":"停下以便做另一件事"},
              {"leftForm":"stop to do","leftUse":"B","rightForm":"stop doing","rightUse":"A"},
              {"leftForm":"stop by","leftUse":"顺便拜访","rightForm":"stop over","rightUse":"旅途中短暂停留"},
              {"leftForm":"stop for lunch","leftUse":"停下来吃饭，说明目的","rightForm":"stop at the café","rightUse":"在咖啡馆停留，说明地点"}]}
            """;
        var contrasts = AiDictionaryService.ParseDictionaryResponse("stop", "zh-Hans", json).UsageContrasts;
        Assert.Equal(3, contrasts.Count);
        Assert.Equal("stop doing", contrasts[0].LeftForm);
        Assert.Equal("停下以便做另一件事", contrasts[0].RightUse);
        Assert.Equal("stop over", contrasts[1].RightForm);
        Assert.Equal("stop at the café", contrasts[2].RightForm);
        Assert.Empty(AiDictionaryService.ParseDictionaryResponse("We stopped.", "zh-Hans",
            json.Replace("\"Word\"", "\"Sentence\"")).UsageContrasts);
    }

    [Fact]
    public void SavedContrastsSurviveReopeningAndUpdatesFromOlderCachedEntries()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "words.json");
            var words = new VocabularyService(path);
            var legacy = AiDictionaryService.ParseDictionaryResponse("remember", "zh-Hans", """{"primaryTranslation":"记得"}""");
            var pair = new UsageContrast("remember doing", "记得已做过的事", "remember to do", "记得要做的事");
            var rich = legacy with { UsageContrasts = [pair] };
            Assert.Empty(words.AddOrUpdateFromDictionary(legacy).UsageContrasts);
            words.AddOrUpdateFromDictionary(rich);
            words.AddOrUpdateFromDictionary(legacy);
            Assert.Equal(pair, Assert.Single(new VocabularyService(path).FindByHeadword("remember")!.UsageContrasts));
            var fresh = new VocabularyService(Path.Combine(dir.FullName, "new.json"));
            Assert.Equal(pair, Assert.Single(fresh.AddOrUpdateFromDictionary(rich).UsageContrasts));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void DerivationsAreSeparateFromInflectionsInResponsesAndSavedCards()
    {
        const string json = """
            {"primaryTranslation":"测试","itemType":"Word","detectedSourceLanguage":"en",
             "inflections":[{"label":"过去式","value":"tested"}],
             "wordFamily":[null,{"label":"名词"},{"label":"名词","value":"test"},{"label":"名词","value":"test anxiety"},
               {"label":"名词","value":"tester"},{"label":"名词","value":"tester"},{"label":"形容词","value":"testable"}]}
            """;
        var result = AiDictionaryService.ParseDictionaryResponse("test", "zh-Hans", json);
        Assert.Equal("tested", Assert.Single(result.Inflections).Value);
        Assert.Equal(new[] { "tester", "testable" }, result.WordFamily.Select(f => f.Value));
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "words.json");
            var words = new VocabularyService(path);
            words.AddOrUpdateFromDictionary(result);
            Assert.Equal(result.WordFamily, new VocabularyService(path).FindByHeadword("test")!.WordFamily);
            var seed = Assert.Single(BuiltInShowcaseLexicon.All, e => e.SourceText == "ephemeral");
            Assert.Equal(["more ephemeral", "most ephemeral"], seed.Inflections.Select(f => f.Value));
            Assert.Contains(seed.WordFamily, f => f.Value == "ephemerality");
            Assert.Equal(seed.WordFamily, words.FindByHeadword("ephemeral")!.WordFamily);
        }
        finally { dir.Delete(true); }
    }
}
