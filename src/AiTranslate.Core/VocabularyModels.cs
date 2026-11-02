namespace AiTranslate.Core;

public enum VocabularyItemType
{
    Word = 0,
    Phrase = 1,
    Sentence = 2,
    Idiom = 3,
    TechnicalTerm = 4
}

public enum MemoryStage
{
    New = 0,
    Learning = 1,
    Reviewing = 2,
    Mastered = 3
}

public enum ReviewRating
{
    Again = 1, // 忘记 / 重来
    Hard = 2,  // 困难 / 模糊
    Good = 3,  // 认识 / 良好
    Easy = 4   // 熟练 / 简单
}

public enum ReviewQuizMode
{
    Flashcard = 0,
    ClozeContext = 1,
    SpellingDictation = 2
}

public sealed record PartOfSpeechSense(
    string PartOfSpeech,
    string DefinitionZh,
    string DefinitionEn = "");

public sealed record WordInflection(
    string Label,
    string Value);

public sealed record BilingualExample(
    string SourceSentence,
    string TranslatedSentence,
    string HighlightTerm = "");

public sealed record CollocationItem(
    string Phrase,
    string Meaning);

/// <summary>
/// Rich AI dictionary & translation analysis result for both single words/phrases and long sentences/paragraphs.
/// </summary>
public sealed record DictionaryEntryResult
{
    public required string SourceText { get; init; }
    public required string PrimaryTranslation { get; init; }
    public string DetectedSourceLanguage { get; init; } = "auto";
    public string TargetLanguage { get; init; } = "zh-Hans";
    public VocabularyItemType ItemType { get; init; } = VocabularyItemType.Word;
    public string?UkPhonetic { get; init; }
    public string? UsPhonetic { get; init; }
    public string? ContextMeaning { get; init; }
    public string? EtymologyAndRoot { get; init; }
    public string? MemoryMnemonic { get; init; }
    public string? GrammarBreakdown { get; init; }
    public string? UsageNotes { get; init; }
    public IReadOnlyList<PartOfSpeechSense> Senses { get; init; } = [];
    public IReadOnlyList<WordInflection> Inflections { get; init; } = [];
    public IReadOnlyList<CollocationItem> Collocations { get; init; } = [];
    public IReadOnlyList<string> Synonyms { get; init; } = [];
    public IReadOnlyList<string> Antonyms { get; init; } = [];
    public IReadOnlyList<BilingualExample> Examples { get; init; } = [];
    public IReadOnlyList<string> SuggestedTags { get; init; } = [];
}

/// <summary>
/// A user-defined vocabulary notebook / deck (e.g., "日常阅读生词", "技术文档核心词", "CET6/雅思核心", "金句摘录").
/// </summary>
public sealed record VocabularyNotebook
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string IconName { get; init; } = "book-open-variant";
    public string AccentColorHex { get; init; } = "#3B82F6";
    public bool IsSystemDefault { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Best-in-class Vocabulary Entry combining Etymology, Context, AI Mnemonics, and FSRS Spaced Repetition state.
/// </summary>
public sealed record VocabularyEntry
{
    public required string Id { get; init; }
    public required string Headword { get; init; }
    public required string Translation { get; init; }
    public string NotebookId { get; init; } = "default";
    public VocabularyItemType ItemType { get; init; } = VocabularyItemType.Word;
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "zh-Hans";
    public string UkPhonetic { get; init; } = "";
    public string UsPhonetic { get; init; } = "";

    // Rich Linguistic & AI Memory Helpers
    public IReadOnlyList<PartOfSpeechSense> Senses { get; init; } = [];
    public IReadOnlyList<WordInflection> Inflections { get; init; } = [];
    public IReadOnlyList<CollocationItem> Collocations { get; init; } = [];
    public IReadOnlyList<BilingualExample> Examples { get; init; } = [];
    public IReadOnlyList<string> Synonyms { get; init; } = [];
    public IReadOnlyList<string> Antonyms { get; init; } = [];
    public string EtymologyAndRoot { get; init; } = "";
    public string MemoryMnemonic { get; init; } = "";
    public string GrammarOrUsageNote { get; init; } = "";
    public string UserPersonalNote { get; init; } = "";

    // Context Preservation (Where the user encountered it)
    public string ContextSentence { get; init; } = "";
    public string ContextTranslation { get; init; } = "";
    public TranslationTriggerSource CaptureSource { get; init; } = TranslationTriggerSource.ManualInput;
    public string SourceTitle { get; init; } = "";

    // Organization
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool IsStarred { get; init; }
    public int LookupCount { get; init; } = 1;

    // FSRS / Spaced Repetition Memory State
    public MemoryStage Stage { get; init; } = MemoryStage.New;
    /// <summary>0 to 5 stars mastery indicator.</summary>
    public int MasteryLevel { get; init; }
    /// <summary>Memory stability S in days (how long until retrievability drops to 90%).</summary>
    public double StabilityDays { get; init; }
    /// <summary>Memory difficulty D in [1.0, 10.0].</summary>
    public double Difficulty { get; init; } = 5.0;
    public int TotalReviews { get; init; }
    public int CorrectStreak { get; init; }
    public int LapseCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastReviewedAt { get; init; }
    public DateTimeOffset NextReviewAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsDue(DateTimeOffset now) => Stage != MemoryStage.Mastered && NextReviewAt <= now;

    /// <summary>Calculates current memory retention probability R in [0, 1] using FSRS forgetting curve.</summary>
    public double EstimateRetrievability(DateTimeOffset now)
    {
        if (Stage == MemoryStage.New || LastReviewedAt is null || StabilityDays <= 0.01)
            return Stage == MemoryStage.Mastered ? 1.0 : 0.0;
        var elapsedDays = Math.Max(0, (now - LastReviewedAt.Value).TotalDays);
        // FSRS power-law forgetting curve: R(t, S) = (1 + 19/81 * t / S)^(-0.5)
        var r = Math.Pow(1.0 + (19.0 / 81.0) * (elapsedDays / StabilityDays), -0.5);
        return Math.Clamp(r, 0.0, 1.0);
    }
}

public sealed record ReviewLogRecord(
    string Id,
    string EntryId,
    ReviewRating Rating,
    ReviewQuizMode QuizMode,
    MemoryStage PreviousStage,
    MemoryStage NewStage,
    double PreviousStability,
    double NewStability,
    int DurationMs,
    DateTimeOffset ReviewedAt);

/// <summary>
/// FSRS-inspired Spaced Repetition Scheduler with previewable intervals for Again / Hard / Good / Easy.
/// </summary>
public static class SpacedRepetitionScheduler
{
    public sealed record SchedulePreview(
        TimeSpan AgainInterval,
        TimeSpan HardInterval,
        TimeSpan GoodInterval,
        TimeSpan EasyInterval);

    public static SchedulePreview PreviewIntervals(VocabularyEntry entry, DateTimeOffset now)
    {
        var again = ApplyReview(entry, ReviewRating.Again, now).NextReviewAt - now;
        var hard = ApplyReview(entry, ReviewRating.Hard, now).NextReviewAt - now;
        var good = ApplyReview(entry, ReviewRating.Good, now).NextReviewAt - now;
        var easy = ApplyReview(entry, ReviewRating.Easy, now).NextReviewAt - now;
        return new(again, hard, good, easy);
    }

    public static string FormatInterval(TimeSpan span)
    {
        if (span.TotalMinutes < 60) return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} 分钟";
        if (span.TotalHours < 24) return $"{Math.Max(1, (int)Math.Round(span.TotalHours))} 小时";
        if (span.TotalDays < 30) return $"{Math.Max(1, (int)Math.Round(span.TotalDays))} 天";
        if (span.TotalDays < 365) return $"{(span.TotalDays / 30.0):F1} 个月";
        return $"{(span.TotalDays / 365.0):F1} 年";
    }

    public static VocabularyEntry ApplyReview(VocabularyEntry entry, ReviewRating rating, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var prevStability = entry.StabilityDays;
        var prevDifficulty = entry.Difficulty <= 0 ? 5.0 : entry.Difficulty;
        var elapsedDays = entry.LastReviewedAt.HasValue
            ? Math.Max(0.01, (now - entry.LastReviewedAt.Value).TotalDays)
            : 0;

        // Update Difficulty D in [1.0, 10.0]
        var deltaD = rating switch
        {
            ReviewRating.Again => +1.2,
            ReviewRating.Hard => +0.4,
            ReviewRating.Good => -0.15,
            ReviewRating.Easy => -0.6,
            _ => 0
        };
        // Mean reversion towards D0 = 5.0
        var newDifficulty = Math.Clamp(0.9 * (prevDifficulty + deltaD) + 0.1 * 5.0, 1.0, 10.0);

        double newStability;
        TimeSpan nextInterval;
        MemoryStage newStage;
        int newStreak;
        int newLapses = entry.LapseCount;

        if (entry.Stage == MemoryStage.New || prevStability <= 0.05)
        {
            // Initial acquisition stability (FSRS initial stability parameters)
            newStability = rating switch
            {
                ReviewRating.Again => 0.15, // ~3.6 hours
                ReviewRating.Hard => 0.6,   // ~14 hours
                ReviewRating.Good => 1.8,   // ~2 days
                ReviewRating.Easy => 4.5,   // ~4.5 days
                _ => 1.0
            };
            nextInterval = rating switch
            {
                ReviewRating.Again => TimeSpan.FromMinutes(10),
                ReviewRating.Hard => TimeSpan.FromHours(6),
                ReviewRating.Good => TimeSpan.FromDays(1),
                ReviewRating.Easy => TimeSpan.FromDays(4),
                _ => TimeSpan.FromDays(1)
            };
            newStage = rating switch
            {
                ReviewRating.Again or ReviewRating.Hard => MemoryStage.Learning,
                _ => MemoryStage.Reviewing
            };
            newStreak = rating == ReviewRating.Again ? 0 : entry.CorrectStreak + 1;
            if (rating == ReviewRating.Again) newLapses++;
        }
        else
        {
            var retrievability = entry.EstimateRetrievability(now);
            if (rating == ReviewRating.Again)
            {
                // Post-lapse stability
                newStability = Math.Max(0.2, prevStability * 0.28);
                nextInterval = TimeSpan.FromMinutes(15);
                newStage = MemoryStage.Learning;
                newStreak = 0;
                newLapses++;
            }
            else
            {
                var hardPenalty = rating == ReviewRating.Hard ? 0.82 : 1.0;
                var easyBonus = rating == ReviewRating.Easy ? 1.38 : 1.0;
                var difficultyFactor = (11.0 - newDifficulty) * 0.22;
                var retentionGain = 1.0 + difficultyFactor * Math.Pow(prevStability, -0.18) * (Math.Exp(0.75 * (1.0 - retrievability)) - 0.1) * hardPenalty * easyBonus;
                newStability = Math.Clamp(prevStability * Math.Max(1.15, retentionGain), 0.5, 3650.0);

                var intervalDays = Math.Max(rating == ReviewRating.Hard ? 0.5 : 1.0, newStability);
                nextInterval = TimeSpan.FromDays(intervalDays);
                newStreak = entry.CorrectStreak + 1;
                newStage = newStability >= 45.0 && newStreak >= 4
                    ? MemoryStage.Mastered
                    : MemoryStage.Reviewing;
            }
        }

        var masteryLevel = CalculateMasteryLevel(newStage, newStability, newStreak);

        return entry with
        {
            Stage = newStage,
            MasteryLevel = masteryLevel,
            StabilityDays = Math.Round(newStability, 2),
            Difficulty = Math.Round(newDifficulty, 2),
            TotalReviews = entry.TotalReviews + 1,
            CorrectStreak = newStreak,
            LapseCount = newLapses,
            LastReviewedAt = now,
            NextReviewAt = now + nextInterval,
            UpdatedAt = now
        };
    }

    private static int CalculateMasteryLevel(MemoryStage stage, double stabilityDays, int streak)
    {
        if (stage == MemoryStage.Mastered || stabilityDays >= 45) return 5;
        if (stabilityDays >= 21 || streak >= 5) return 4;
        if (stabilityDays >= 7 || streak >= 3) return 3;
        if (stabilityDays >= 2 || streak >= 2) return 2;
        if (stabilityDays >= 0.4 || streak >= 1) return 1;
        return 0;
    }
}
