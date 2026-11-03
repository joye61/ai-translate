using AiTranslate.Ai;
using AiTranslate.Core;

namespace AiTranslate.Desktop.Controls;

/// <summary>
/// Centralized Chinese display labels for domain enums. Views must use these instead of raw
/// `enum.ToString()` so the app never mixes bare English identifiers (e.g. "Mastered",
/// "ManualInput") into otherwise-Chinese sentences.
/// </summary>
public static class UiLabels
{
    public static string ItemType(VocabularyItemType type) => type switch
    {
        VocabularyItemType.Word => "单词",
        VocabularyItemType.Phrase => "短语",
        VocabularyItemType.Sentence => "句子",
        VocabularyItemType.Paragraph => "段落",
        VocabularyItemType.Idiom => "习语",
        VocabularyItemType.TechnicalTerm => "专业术语",
        _ => type.ToString()
    };

    public static string Stage(MemoryStage stage) => stage switch
    {
        MemoryStage.New => "新词",
        MemoryStage.Learning => "学习中",
        MemoryStage.Reviewing => "复习中",
        MemoryStage.Mastered => "已掌握",
        _ => stage.ToString()
    };

    public static string Rating(ReviewRating rating) => rating switch
    {
        ReviewRating.Again => "忘记重来",
        ReviewRating.Hard => "印象模糊",
        ReviewRating.Good => "认识良好",
        ReviewRating.Easy => "熟练简单",
        _ => rating.ToString()
    };

    public static string CaptureSource(TranslationTriggerSource source) => source switch
    {
        TranslationTriggerSource.ManualInput => "手动查询",
        TranslationTriggerSource.ClipboardMonitor => "剪贴板监听",
        TranslationTriggerSource.SelectionPopup => "划词速译",
        TranslationTriggerSource.ScreenshotOcr => "截图翻译",
        TranslationTriggerSource.VocabularyReview => "单词复习",
        _ => source.ToString()
    };

    /// <summary>Resolves a language id to its Chinese display name without throwing on unrecognized/"auto" ids.</summary>
    public static string Language(string id) =>
        string.Equals(id, "zh", StringComparison.OrdinalIgnoreCase) ? "中文" :
            DictionaryTranslationDirection.FromSourceCode(id)?.ChineseName ?? id;
}
