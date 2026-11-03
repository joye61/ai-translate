using System.Text.Json.Nodes;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class LexicalFormDetailsTests
{
    [Fact]
    public void CasesKeepTheirSenseAndRichRelationshipsRequireAnAnchoredTarget()
    {
        var hang = AiDictionaryService.ParseDictionaryResponse("hang", "zh-Hans", """
            {"primaryTranslation":"悬挂；绞死","itemType":"Word","detectedSourceLanguage":"en",
             "senses":[{"partOfSpeech":"v.","definitionZh":"悬挂","usageCases":[
                 {"condition":"描述过去悬挂的动作","grammar":"过去式","expression":"hung the painting"},
                 {"condition":"描述过去悬挂的动作","grammar":"过去式","expression":"hung the painting"},
                 {"grammar":"过去分词","expression":"been hung"}]},
                {"partOfSpeech":"v.","definitionZh":"绞死","usageCases":[
                 {"condition":"指执行绞刑","grammar":"过去分词通常用 hanged","expression":"was hanged","meaning":"被处以绞刑"}]}]}
            """);
        Assert.Equal("hung the painting", Assert.Single(hang.Senses[0].UsageCases).Expression);
        Assert.Equal("was hanged", Assert.Single(hang.Senses[1].UsageCases).Expression);
        Assert.Empty(hang.Inflections);

        var hard = AiDictionaryService.ParseDictionaryResponse("hard", "zh-Hans", """
            {"primaryTranslation":"艰难的；努力地","detectedSourceLanguage":"en","itemType":"Word",
             "senses":[{"partOfSpeech":"adj.","definitionZh":"艰难的"},{"partOfSpeech":"adv.","definitionZh":"努力地"}],
             "wordFamily":[{"label":"副词","value":"hard"},
               {"value":"hard","label":"努力地","partOfSpeech":"adv.","relation":"sameForm","usage":"修饰动作","expression":"work hard"},
               {"value":"hard","label":"努力地","partOfSpeech":"adv.","relation":"sameForm","usage":"修饰动作","expression":"train hard"},
               {"value":"hardly","label":"几乎不","partOfSpeech":"adv.","relation":"confusable","usage":"表达接近否定","expression":"hardly working"},
               {"value":"harden","label":"变硬","partOfSpeech":"v.","relation":"derived","usage":"表达状态变化","expression":"hardening steel"},
               {"value":"hard","label":"错误分类","partOfSpeech":"adj.","relation":"derived","usage":"近形","expression":"hard work"},
               {"value":"hardship","label":"困苦","partOfSpeech":"n.","relation":"unknown","usage":"困难","expression":"face hardship"}]}
            """);
        Assert.Equal(["sameForm", "confusable"], hard.WordFamily.Select(r => r.Relation));
        Assert.True(hard.WordFamily[0].IsSameFormOf("hard"));
        Assert.False(hard.WordFamily[1].IsSameFormOf("hard"));
        var cacheDir = Directory.CreateTempSubdirectory();
        try
        {
            var db = Path.Combine(cacheDir.FullName, "dictionary.sqlite3");
            var cache = new DictionaryEntryCache(db);
            Assert.True(cache.Write("hang", hang, 1));
            Assert.Equal(hang.Senses, new DictionaryEntryCache(db).Read("hang")!.Senses);
            var path = Path.Combine(cacheDir.FullName, "books.json");
            var store = new VocabularyService(path);
            var card = store.AddOrUpdateFromDictionary(hard);
            store.AddOrUpdateFromDictionary(hang);
            store.AddOrUpdateFromDictionary(hang with
            {
                Senses = hang.Senses.Select(s => s with { UsageCases = [] }).ToArray()
            });
            var reopened = new VocabularyService(path);
            Assert.Equal(hang.Senses, reopened.FindByHeadword("hang")!.ToDictionaryResult().Senses);
            Assert.Equal(hard.WordFamily, reopened.FindByHeadword("hard")!.ToDictionaryResult().WordFamily);
            Assert.Equal(card.Id, reopened.FindByHeadword("hard")!.Id);
        }
        finally { cacheDir.Delete(true); }
    }

    [Fact]
    public async Task SupplementUsesOnlyAddressedOriginalSensesAndCanUpgradeALegacyRelation()
    {
        var entry = new DictionaryEntryResult
        {
            SourceText = "hard", PrimaryTranslation = "用力的", DetectedSourceLanguage = "en",
            Senses = [new("adj.", "强的", Scenario: "力气"), new("adjective", "强的", Scenario: "程度")],
            WordFamily = [new("副词", "hardly")]
        };
        const string reply = """
            {"senseGrammar":[
               {"partOfSpeech":"adj.","definitionZh":"强的","grammarLabel":"歧义旧定位"},
               {"id":"sense-3","partOfSpeech":"adj.","definitionZh":"强的","grammarLabel":"无效编号"},
               {"id":"sense-0","partOfSpeech":"adv.","definitionZh":"强的","grammarLabel":"词性不匹配"},
               {"id":"sense-0","partOfSpeech":"adj.","definitionZh":"别的定义","grammarLabel":"义项不匹配"},
               {"id":"sense-1","partOfSpeech":"adj.","definitionZh":"强的","usageCases":[
                 {"condition":"说严重程度","grammar":"修饰名词","expression":"a hard frost","meaning":"严霜"}]}],
             "wordFamily":[{"value":"hardly","label":"几乎不","partOfSpeech":"adv.","relation":"confusable",
                 "usage":"表示接近否定","expression":"hardly working"}]}
            """;
        var transport = new Reply(reply);
        var service = new AiDictionaryService(() => transport);
        var repaired = await service.CheckLexicalCoverageAsync(entry, null, default);
        Assert.Empty(repaired.Senses[0].GrammarLabel);
        Assert.Empty(repaired.Senses[0].UsageCases);
        Assert.Equal("a hard frost", Assert.Single(repaired.Senses[1].UsageCases).Expression);
        Assert.Equal("confusable", Assert.Single(repaired.WordFamily).Relation);
        Assert.Same(repaired, await service.CheckLexicalCoverageAsync(repaired, null, default));
        var senses = transport.Payload!["senses"]!.AsArray();
        Assert.Equal("sense-1", senses[1]!["id"]!.GetValue<string>());
        Assert.Single(senses[1]!["usageCases"]!.AsArray());
        Assert.Single(transport.Payload["wordFamily"]!.AsArray());
    }

    private sealed class Reply(string text) : IAiTransport
    {
        public JsonNode? Payload { get; private set; }
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            Payload = JsonNode.Parse(messages[1]["content"]!.GetValue<string>());
            return Task.FromResult(new AiCompletion(text, []));
        }
    }
}
