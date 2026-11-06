using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using System.Text.Json.Nodes;
using Xunit;

namespace AiTranslate.Tests;

public sealed class TranslationDecisionTests
{
    [Fact]
    public async Task StreamsTheReasonWhileTheDecisionArrayIsStillOpen()
    {
        var previews = new List<DictionaryEntryResult>();
        var service = new AiDictionaryService(() => new PartialDecisionReply(() =>
            Assert.Equal("说明", Assert.Single(previews[^1].TranslationDecisions).Reason)));
        var entry = await service.LookupOrTranslateAsync("Not all passed.", "zh-Hans", onEntryPreview: previews.Add);
        Assert.Equal("说明部分否定。", Assert.Single(entry.TranslationDecisions).Reason);
    }

    private sealed class PartialDecisionReply(Action inspectPreview) : IAiTransport
    {
        public Task<AiCompletion> CompleteAsync(IReadOnlyList<JsonObject> messages, JsonArray tools, Action<string> onText, CancellationToken token)
        {
            if (PipelineReplies.TryEmptyStage(messages, out var stage)) return Task.FromResult(stage);
            const string first = """{"primaryTranslation":"并非全部通过。","itemType":"Sentence","translationDecisions":[{"sourceQuote":"Not all","targetQuote":"并非全部","reason":"说明""";
            const string last = "部分否定。\"}]}";
            onText(first);
            inspectPreview();
            onText(last);
            return Task.FromResult(new AiCompletion(first + last, []));
        }
    }

    [Fact]
    public void RejectsUnanchoredNotesAndKeepsOnlyAlternativesWithConditions()
    {
        var entry = AiDictionaryService.ParseDictionaryResponse("We saw her duck. It was quick.", "zh-Hans", """
            {
              "itemType":"Paragraph","primaryTranslation":"我们看到她低下了头。动作很快。",
              "translationDecisions":[
                {"sourceQuote":"her duck","targetQuote":"她低下了头","reason":"duck 可作动作或名词，这里选择动作读法。","alternative":"她的鸭子","condition":"若上下文谈论她养的动物"},
                {"sourceQuote":"her duck","targetQuote":"她低下了头","reason":"重复"},
                {"sourceQuote":"It","targetQuote":"动作","reason":"在此次译法中回指低头的动作。","alternative":"它"},
                {"sourceQuote":"ducked","targetQuote":"低下了头","reason":"改写了原文词形"},
                {"sourceQuote":"We saw","targetQuote":"我们发现","reason":"主译中没有这一表述"},
                {"sourceQuote":"uck","targetQuote":"头","reason":"单词内部的伪锚点"},
                {"sourceQuote":"quick","targetQuote":"很快","reason":""},
                17
              ]
            }
            """);
        Assert.Equal(2, entry.TranslationDecisions.Count);
        Assert.Equal(("她的鸭子", "若上下文谈论她养的动物"),
            (entry.TranslationDecisions[0].Alternative, entry.TranslationDecisions[0].Condition));
        Assert.Equal("", entry.TranslationDecisions[1].Alternative);
    }

    [Fact]
    public void NotesPersistWithTheSentenceAndAreRemovedWhenItsTranslationChanges()
    {
        const string source = "Not all samples passed.";
        var result = AiDictionaryService.ParseDictionaryResponse(source, "zh-Hans", """
            {"itemType":"Sentence","primaryTranslation":"并非所有样本都通过了。",
             "translationDecisions":[{"sourceQuote":"Not all","targetQuote":"并非所有","reason":"否定的是全体通过这一判断。"}]}
            """);
        var decision = Assert.Single(result.TranslationDecisions);
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(directory.FullName, "words.json");
            new VocabularyService(path).AddOrUpdateFromDictionary(result);
            var reopened = new VocabularyService(path);
            Assert.Equal(decision, Assert.Single(reopened.FindByHeadword(source)!.TranslationDecisions));
            var compatibleUpdate = result with { TranslationDecisions = [] };
            Assert.Single(reopened.AddOrUpdateFromDictionary(compatibleUpdate).TranslationDecisions);
            Assert.Empty(reopened.AddOrUpdateFromDictionary(compatibleUpdate with { PrimaryTranslation = "部分样本未通过。" }).TranslationDecisions);
        }
        finally { directory.Delete(true); }
    }
}
