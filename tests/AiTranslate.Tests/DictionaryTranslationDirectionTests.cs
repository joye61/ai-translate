using System.Globalization;
using AiTranslate.Ai;
using AiTranslate.Core;
using Xunit;

namespace AiTranslate.Tests;

public sealed class DictionaryTranslationDirectionTests
{
    [Theory]
    [InlineData("zh-CN", "行", "en")]
    [InlineData("zh-TW", "請稍候。", "en")]
    [InlineData("zh-CN", "𠮷", "en")]
    [InlineData("zh-CN", "如何使用 CancellationTokenSource？", "en")]
    [InlineData("zh-CN", "Good morning!", "zh-Hans")]
    [InlineData("zh-CN", "What does 银行 mean here?", "zh-Hans")]
    [InlineData("zh-CN", "明日は晴れです。", "zh-Hans")]
    [InlineData("zh-CN", "図書館", "zh-Hans")]
    [InlineData("zh-HK", "韓國 사람", "zh-Hant")]
    [InlineData("pl-PL", "明日は晴れです。", "pl")]
    [InlineData("ja-JP", "明日は晴れです。", "en")]
    [InlineData("ja-JP", "123.45%", "ja")]
    [InlineData("en-US", "Bonjour tout le monde.", "en")]
    [InlineData("en-US", "Good morning!", "zh-Hans")]
    [InlineData("fr-CA", "Good morning!", "fr")]
    [InlineData("fr-CA", "Bonjour tout le monde.", "en")]
    [InlineData("es-ES", "hola", "en")]
    public void AutomaticDirectionUsesTheDisplayLanguageAndQueriedText(string culture, string query, string target)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(target, DictionaryTranslationDirection.ResolveTargetId(query, DictionaryTranslationDirection.AutoId));
            Assert.Equal("de", DictionaryTranslationDirection.ResolveTargetId(query, "de"));
        }
        finally { CultureInfo.CurrentUICulture = original; }
    }

    [Fact]
    public void LegacyCustomTargetsAndExplicitChoicesTakePriority()
    {
        var settings = new AppSettings();
        Assert.Equal("auto", DictionaryTranslationDirection.GetMode(settings));
        Assert.Equal("zh-Hant", DictionaryTranslationDirection.GetMode(settings with { DefaultTargetLanguage = "zh-Hant" }));
        Assert.Equal("zh-Hans", DictionaryTranslationDirection.GetMode(settings with { DictionaryTargetMode = "zh-Hans" }));
        Assert.Equal("ja", DictionaryTranslationDirection.ResolveTargetId("明天见", "ja"));
        Assert.Equal("auto", DictionaryTranslationDirection.GetMode(settings with { DictionaryTargetMode = "unsupported" }));
    }
}
