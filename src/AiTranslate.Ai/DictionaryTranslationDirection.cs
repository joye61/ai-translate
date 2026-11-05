using System.Globalization;
using System.Text;
using AiTranslate.Core;
using Panlingo.LanguageIdentification.FastText;

namespace AiTranslate.Ai;

public static class DictionaryTranslationDirection
{
    public const string AutoId = "auto";
    private static readonly TranslationLanguage Automatic = new(AutoId, "自动 · 系统语言", "自动");
    private static readonly Lazy<FastTextDetector> Detector = new(() =>
    {
        var detector = new FastTextDetector();
        detector.LoadDefaultModel();
        return detector;
    });
    public static IReadOnlyList<TranslationLanguage> Modes { get; } =
        Array.AsReadOnly<TranslationLanguage>([Automatic, .. TranslationLanguages.All]);

    public static TranslationLanguage ReaderLanguage(string? systemLanguage = null) =>
        FromSourceCode(systemLanguage ?? CultureInfo.CurrentUICulture.Name)
        ?? TranslationLanguages.Get(TranslationLanguages.DefaultId);

    public static string ReverseTarget(DictionaryEntryResult entry, string query)
    {
        if (FromSourceCode(entry.DetectedSourceLanguage) is { } source) return source.Id;
        var reader = ReaderLanguage().Id;
        return reader != entry.TargetLanguage ? reader : ResolveTargetId(query, AutoId);
    }

    public static string GetMode(AppSettings settings)
    {
        if (settings.DictionaryTargetMode is { } configured)
            return Modes.FirstOrDefault(m => string.Equals(m.Id, configured, StringComparison.OrdinalIgnoreCase))?.Id ?? AutoId;

        // Previous releases wrote zh-Hans even when the user never selected a language.
        // Keep a different legacy default; an explicit choice in the new selector always wins.
        var legacy = TranslationLanguages.GetOrDefault(settings.DefaultTargetLanguage);
        return legacy.Id == TranslationLanguages.DefaultId ? AutoId : legacy.Id;
    }

    public static string ResolveTargetId(string text, string modeId) => ResolveTarget(text, modeId, ReaderLanguage()).Id;

    internal static TranslationLanguage ResolveTarget(string text, string modeId, TranslationLanguage reader)
    {
        if (modeId != AutoId) return FromSourceCode(modeId) ?? throw new AiException("不支持此翻译语言，请重新选择。");
        var source = DetectSource(text);
        return source == reader.Id.Split('-')[0]
            ? TranslationLanguages.Get(source == "en" ? TranslationLanguages.DefaultId : "en")
            : reader;
    }

    private static string? DetectSource(string text)
    {
        var han = 0;
        var otherWords = 0;
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var code = rune.Value;
            // Kana or Hangul distinguish Japanese and Korean input containing shared ideographs.
            if (code is >= 0x3041 and <= 0x30FA or >= 0xFF66 and <= 0xFF9D) return "ja";
            if (code is >= 0x1100 and <= 0x11FF or >= 0x3131 and <= 0x318E or >= 0xAC00 and <= 0xD7A3) return "ko";

            var isHan = code is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
                >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x33479;
            if (isHan) han++;
            var wordLetter = !isHan && Rune.IsLetter(rune);
            if (wordLetter && !inWord) otherWords++;
            inWord = wordLetter;
        }

        if (han == 0 && otherWords == 0) return null;
        var prediction = Detector.Value.Predict(text.ReplaceLineEndings(" "), count: 1).FirstOrDefault();
        // Count whole words so one long identifier does not outweigh Chinese prose.
        // Shared Han characters are ambiguous, but a compound such as 図書館 can be identified as Japanese.
        if (han > 0 && han >= otherWords)
            return han > 1 && prediction is { Label: "__label__ja", Probability: > 0.9f } ? "ja" : "zh";

        // The model may only know a standalone greeting in sentence-initial case (hola / Hola).
        if (prediction is null && otherWords == 1 && char.IsLower(text[0]))
            prediction = Detector.Value.Predict(char.ToUpperInvariant(text[0]) + text[1..], count: 1).FirstOrDefault();
        const string prefix = "__label__";
        if (prediction?.Label is { } label && label.StartsWith(prefix, StringComparison.Ordinal))
            return label[prefix.Length..];
        return EnglishLexicalHints.Find(text.ToLowerInvariant()) is null ? null : "en";
    }

    // Providers may return a generic language or a regional BCP 47 code (zh, zh-TW, en-US).
    public static TranslationLanguage? FromSourceCode(string sourceLanguage)
    {
        var code = sourceLanguage.Trim().Replace('_', '-').ToLowerInvariant();
        var subtags = code.Split('-');
        var primary = subtags[0];
        if (primary == "zh")
        {
            var traditional = subtags.Contains("hant") || subtags.Contains("cht") ||
                (!subtags.Contains("hans") && subtags.Any(s => s is "tw" or "hk" or "mo"));
            return TranslationLanguages.Get(traditional ? "zh-Hant" : "zh-Hans");
        }
        if (TranslationLanguages.All.FirstOrDefault(l => l.Id == primary) is { } supported) return supported;
        if (code is "" or AutoId or "und") return null;
        // A system display language can be outside the fixed-target menu.
        try
        {
            var culture = CultureInfo.GetCultureInfo(code, predefinedOnly: true);
            if (!culture.IsNeutralCulture) culture = culture.Parent;
            return culture.Name.Length == 0 ? null : new(culture.Name, culture.NativeName, culture.EnglishName, culture.TextInfo.IsRightToLeft);
        }
        catch (CultureNotFoundException) { return null; }
    }
}
