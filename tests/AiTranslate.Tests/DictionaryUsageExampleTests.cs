using System.Text.Json;
using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Vocabulary;
using Xunit;

namespace AiTranslate.Tests;

public sealed class DictionaryUsageExampleTests
{
    [Fact]
    public void ChineseUsageAndCollocationsKeepTheirOwnBilingualExamplesAfterSaving()
    {
        var result = AiDictionaryService.ParseDictionaryResponse("把握", "en", """
        {
          "primaryTranslation":"grasp; seize",
          "senses":[{"definitionZh":"seize","grammarPattern":"把握 + 时机",
            "example":{"sourceSentence":"她把握住了最佳时机。","translatedSentence":"She seized the moment.","highlightTerm":"把握住了"}}],
          "collocations":[
            {"phrase":"把握机会","meaning":"seize an opportunity",
             "example":{"sourceSentence":"要懂得把握机会。","translatedSentence":"Learn to seize an opportunity.","highlightTerm":"把握机会"}},
            {"phrase":"没有把握","meaning":"be unsure","example":{"sourceSentence":"我没有把握。"}},
            {"phrase":"有把握","meaning":"be confident","example":"malformed"}
          ]
        }
        """);
        var pattern = Assert.Single(result.Senses).Example;
        Assert.Equal("把握住了", pattern?.HighlightTerm);
        var collocation = result.Collocations[0];
        Assert.Equal("Learn to seize an opportunity.", result.GetCollocationExample(collocation)?.TranslatedSentence);
        Assert.Null(result.Collocations[1].Example);
        Assert.Null(result.Collocations[2].Example);

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "words.json");
            new VocabularyService(path).AddOrUpdateFromDictionary(result);
            var saved = new VocabularyService(path).FindByHeadword("把握");
            Assert.NotNull(saved);
            Assert.Equal(pattern, Assert.Single(saved.Senses).Example);
            Assert.Equal(collocation, saved.Collocations[0]);

            var cache = new DictionaryEntryCache(Path.Combine(dir.FullName, "cache.sqlite3"));
            cache.Write("word", result, 1);
            Assert.Equal(collocation, cache.Read("word")?.Collocations[0]);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void LegacyExamplesAreReusedOnlyForAnExpressionActuallyPresentInABilingualSentence()
    {
        var old = JsonSerializer.Deserialize<CollocationItem>("""{"Phrase":"take off","Meaning":"起飞"}""")!;
        var entry = new DictionaryEntryResult
        {
            SourceText = "take", PrimaryTranslation = "拿",
            Collocations = [old],
            Examples = [
                new("An outtake off the album.", "专辑中未收录的一段。"),
                new("Take offense at the remark.", "因这句话生气。"),
                new("We take off at dawn.", ""),
                new("Take off at noon.", "正午起飞。")]
        };
        Assert.Null(old.Example);
        Assert.Equal("Take off", entry.GetCollocationExample(old)?.HighlightTerm);
        Assert.Equal("正午起飞。", entry.GetCollocationExample(old)?.TranslatedSentence);
        Assert.Null(entry.GetCollocationExample(new CollocationItem("take after", "像")));
        var ambiguous = entry with { Collocations = [old, old with { Meaning = "脱下" }] };
        Assert.Null(ambiguous.GetCollocationExample(old));
        Assert.Equal(1, DictionaryTextMatch.IndexOf("用AI把握机会", "ai"));
        Assert.Equal(3, DictionaryTextMatch.IndexOf("用AI把握机会", "把握机会"));
    }

    [Fact]
    public void EveryBuiltInPatternAndCollocationContainsAnIllustrativeBilingualExample()
    {
        foreach (var entry in BuiltInShowcaseLexicon.All.Where(e => !e.ItemType.IsPassage()))
        {
            var examples = entry.Senses.Where(s => !string.IsNullOrEmpty(s.GrammarPattern)).Select(s => s.Example)
                .Concat(entry.Collocations.Select(c => c.Example));
            foreach (var example in examples)
            {
                Assert.NotNull(example);
                Assert.False(string.IsNullOrWhiteSpace(example.TranslatedSentence));
                Assert.False(string.IsNullOrWhiteSpace(example.HighlightTerm));
                Assert.Contains(example.HighlightTerm, example.SourceSentence);
            }
        }
    }
}
