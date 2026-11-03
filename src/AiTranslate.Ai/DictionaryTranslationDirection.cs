using System.Text;
using AiTranslate.Core;

namespace AiTranslate.Ai;

public static class DictionaryTranslationDirection
{
    public const string AutoId = "auto";
    private static readonly TranslationLanguage Automatic = new(AutoId, "自动 · 中英互译", "中英互译");
    public static IReadOnlyList<TranslationLanguage> Modes { get; } =
        Array.AsReadOnly<TranslationLanguage>([Automatic, .. TranslationLanguages.All]);

    public static string GetMode(AppSettings settings)
    {
        if (settings.DictionaryTargetMode is { } configured)
            return Modes.FirstOrDefault(m => string.Equals(m.Id, configured, StringComparison.OrdinalIgnoreCase))?.Id ?? AutoId;

        // Previous releases wrote zh-Hans even when the user never selected a language.
        // Keep a different legacy default; an explicit choice in the new selector always wins.
        var legacy = TranslationLanguages.GetOrDefault(settings.DefaultTargetLanguage);
        return legacy.Id == TranslationLanguages.DefaultId ? AutoId : legacy.Id;
    }

    public static string ResolveTargetId(string text, string modeId)
    {
        if (modeId != AutoId) return TranslationLanguages.Get(modeId).Id;
        var han = 0;
        var otherWords = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var code = rune.Value;
            // Kana or Hangul distinguish Japanese and Korean input containing shared ideographs.
            if (code is >= 0x3041 and <= 0x30FA or >= 0xFF66 and <= 0xFF9D or
                >= 0x1100 and <= 0x11FF or >= 0x3131 and <= 0x318E or >= 0xAC00 and <= 0xD7A3)
                return TranslationLanguages.DefaultId;

            var isHan = code is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
                >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x33479;
            if (isHan) han++;
            var wordLetter = !isHan && Rune.IsLetter(rune);
            if (wordLetter && !inWord) otherWords++;
            inWord = wordLetter;
        }

        // Compare ideographs to whole words so one long identifier does not outweigh Chinese prose.
        return han > 0 && han >= otherWords ? "en" : TranslationLanguages.DefaultId;
    }

    // Providers may return a generic language or a regional BCP 47 code (zh, zh-TW, en-US).
    public static TranslationLanguage? FromSourceCode(string sourceLanguage)
    {
        var code = sourceLanguage.Trim().Replace('_', '-').ToLowerInvariant();
        var primary = code.Split('-')[0];
        if (primary == "zh")
            return TranslationLanguages.Get(code.Contains("hant") || code is "zh-tw" or "zh-hk" or "zh-mo" ? "zh-Hant" : "zh-Hans");
        return TranslationLanguages.All.FirstOrDefault(l => l.Id == primary);
    }
}
