using AiTranslate.Core;
using Xunit;

namespace AiTranslate.Tests;

public sealed class SentenceClozeTests
{
    [Fact]
    public void UsesSurfaceFormAndMasksAllBoundedOccurrences()
    {
        var example = new BilingualExample("She went home, then went shopping.", "她先回家，再去购物。", "went");
        var cloze = SentenceCloze.FromExample(example, "go", []);
        Assert.Equal("She _____ home, then _____ shopping.", cloze?.Masked);
        Assert.Equal("went", cloze?.Answer);
        Assert.Null(SentenceCloze.FromExample(new("We had a party.", ""), "art", []));
        Assert.Null(SentenceCloze.FromExample(new("Art!", ""), "art", []));
        Assert.Null(SentenceCloze.FromExample(new("We wait...", "", "..."), "go", []));
        Assert.Equal("在此_____。", SentenceCloze.FromExample(new("在此等候。", ""), "等候", [])?.Masked);
    }

    [Fact]
    public void ReviewPrefersCapturedInflectionsAndCanFallBackToNestedExamples()
    {
        var entry = new VocabularyEntry
        {
            Id = "go", Headword = "go", Translation = "去", ContextSentence = "We went there.",
            Inflections = [new("过去式", "went")], Collocations = [new("go ahead", "先行",
                Example: new("She went ahead of us.", "她先走了。", "went ahead"))]
        };
        var captured = SentenceCloze.ForReview(entry);
        Assert.Equal("We _____ there.", captured?.Masked);
        Assert.Equal("", captured?.Translation);
        var fallback = SentenceCloze.ForReview(entry with { ContextSentence = "Unrelated context." });
        Assert.Equal("went ahead", fallback?.Answer);
        Assert.Equal("她先走了。", fallback?.Translation);
        Assert.Null(SentenceCloze.ForReview(entry with { ContextSentence = "Unrelated.", Collocations = [] }));
    }
}
