using AiTranslate.Core;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Single source of truth for which accent hue codes which domain category, so the same concept
/// (a memory stage, a part of speech, a notebook) is the same color on every screen.
/// </summary>
public static class HueMap
{
    public static Hue Stage(MemoryStage stage) => stage switch
    {
        MemoryStage.New => AppTheme.Hues.Sky,
        MemoryStage.Learning => AppTheme.Hues.Honey,
        MemoryStage.Reviewing => AppTheme.Hues.Violet,
        MemoryStage.Mastered => AppTheme.Hues.Sage,
        _ => AppTheme.Hues.Neutral
    };

    public static Hue ItemType(VocabularyItemType type) => type switch
    {
        VocabularyItemType.Word => AppTheme.Hues.Sky,
        VocabularyItemType.Phrase => AppTheme.Hues.Jade,
        VocabularyItemType.Idiom => AppTheme.Hues.Rose,
        VocabularyItemType.TechnicalTerm => AppTheme.Hues.Violet,
        VocabularyItemType.Sentence or VocabularyItemType.Paragraph => AppTheme.Hues.Honey,
        _ => AppTheme.Hues.Neutral
    };

    /// <summary>Major word classes get a hue (as in printed learner's dictionaries); function words stay neutral.</summary>
    public static Hue PartOfSpeech(string? pos) => pos?.Trim().TrimEnd('.').ToLowerInvariant() switch
    {
        "n" or "noun" or "名词" => AppTheme.Hues.Sky,
        "v" or "verb" or "vt" or "vi" or "aux" or "动词" or "及物动词" or "不及物动词" or "助动词" => AppTheme.Hues.Jade,
        "adj" or "a" or "形容词" => AppTheme.Hues.Violet,
        "adv" or "ad" or "副词" => AppTheme.Hues.Honey,
        "phr" or "phrase" or "idiom" or "短语" or "习语" => AppTheme.Hues.Rose,
        _ => AppTheme.Hues.Neutral
    };

    private static readonly Hue Again = new(AppTheme.Danger, AppTheme.DangerSoft, AppTheme.Danger);

    public static Hue Rating(ReviewRating rating) => rating switch
    {
        ReviewRating.Again => Again,
        ReviewRating.Hard => AppTheme.Hues.Honey,
        ReviewRating.Good => AppTheme.Hues.Sage,
        ReviewRating.Easy => AppTheme.Hues.Sky,
        _ => AppTheme.Hues.Neutral
    };

    /// <summary>Stable hue for a user-created item such as a notebook (same id → same color across sessions).</summary>
    public static Hue ForKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return AppTheme.Hues.Coral;
        var hash = 0u;
        foreach (var ch in key) hash = unchecked(hash * 31 + ch);
        var rotation = AppTheme.Hues.Rotation;
        return rotation[(int)(hash % (uint)rotation.Count)];
    }
}
