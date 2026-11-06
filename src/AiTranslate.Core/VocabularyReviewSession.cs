namespace AiTranslate.Core;

/// <summary>
/// One sitting of flash-card review. A card leaves the queue once it is recognised; a card that was
/// forgotten or fuzzy returns a few cards later. Only a card's first answer is a scheduling event,
/// so the retries that follow do not penalise its memory state again.
/// </summary>
public sealed class VocabularyReviewSession
{
    public const int RetryGap = 3;

    private readonly List<VocabularyEntry> _queue;
    private readonly Dictionary<string, ReviewRating> _firstAnswers = [];
    private readonly HashSet<string> _finished = [];

    public VocabularyReviewSession(IEnumerable<VocabularyEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _queue = entries.DistinctBy(e => e.Id).ToList();
        Total = _queue.Count;
    }

    public int Total { get; }
    public int Finished => _finished.Count;
    public bool IsComplete => _queue.Count == 0;
    public VocabularyEntry? Current => _queue.Count > 0 ? _queue[0] : null;
    public bool IsRetry => Current is { } card && _firstAnswers.ContainsKey(card.Id);

    public int Known => CountFirst(ReviewRating.Good, ReviewRating.Easy);
    public int Fuzzy => CountFirst(ReviewRating.Hard);
    public int Forgotten => CountFirst(ReviewRating.Again);

    /// <summary>Answers the current card. Returns true when this answer must be recorded for scheduling.</summary>
    public bool Answer(ReviewRating rating)
    {
        if (_queue.Count == 0) throw new InvalidOperationException("本轮复习已经结束。");
        var card = _queue[0];
        _queue.RemoveAt(0);
        var first = _firstAnswers.TryAdd(card.Id, rating);
        if (rating is ReviewRating.Good or ReviewRating.Easy) _finished.Add(card.Id);
        else _queue.Insert(Math.Min(RetryGap, _queue.Count), card);
        return first;
    }

    private int CountFirst(params ReviewRating[] ratings) => _firstAnswers.Values.Count(ratings.Contains);
}
