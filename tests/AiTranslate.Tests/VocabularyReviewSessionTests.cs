using AiTranslate.Core;
using Xunit;

namespace AiTranslate.Tests;

public sealed class VocabularyReviewSessionTests
{
    private static VocabularyEntry Card(string word) => new() { Id = word, Headword = word, Translation = word + "义" };

    [Fact]
    public void RecognisedCardsLeaveTheQueueAndOthersReturnAfterAGap()
    {
        var session = new VocabularyReviewSession(new[] { "a", "b", "c", "d", "e" }.Select(Card));
        Assert.Equal(5, session.Total);
        Assert.True(session.Answer(ReviewRating.Again)); // a goes back behind the next three cards
        Assert.Equal("b", session.Current!.Headword);
        Assert.Equal(0, session.Finished);

        session.Answer(ReviewRating.Good);
        session.Answer(ReviewRating.Good);
        session.Answer(ReviewRating.Good);
        Assert.Equal("a", session.Current!.Headword);
        Assert.True(session.IsRetry);
        Assert.Equal(3, session.Finished);

        Assert.False(session.Answer(ReviewRating.Hard)); // a retry is practice, not a new scheduling event
        Assert.Equal("e", session.Current!.Headword);
        session.Answer(ReviewRating.Easy);
        session.Answer(ReviewRating.Good);
        Assert.True(session.IsComplete);
        Assert.Equal(5, session.Finished);
        Assert.Null(session.Current);
        Assert.Throws<InvalidOperationException>(() => session.Answer(ReviewRating.Good));
    }

    [Fact]
    public void SummaryCountsFirstAnswersOnlyAndDuplicateCardsAppearOnce()
    {
        var session = new VocabularyReviewSession([Card("a"), Card("b"), Card("c"), Card("a")]);
        Assert.Equal(3, session.Total);
        session.Answer(ReviewRating.Good);
        session.Answer(ReviewRating.Hard);
        session.Answer(ReviewRating.Again);
        while (!session.IsComplete) session.Answer(ReviewRating.Good);
        Assert.Equal((1, 1, 1), (session.Known, session.Fuzzy, session.Forgotten));
        Assert.Equal(3, session.Finished);
    }

    [Fact]
    public void AnEmptySessionIsAlreadyComplete()
    {
        var session = new VocabularyReviewSession([]);
        Assert.True(session.IsComplete);
        Assert.Equal(0, session.Total);
    }
}
