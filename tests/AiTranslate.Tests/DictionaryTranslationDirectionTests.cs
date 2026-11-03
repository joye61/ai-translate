using AiTranslate.Ai;
using AiTranslate.Core;
using Xunit;

namespace AiTranslate.Tests;

public sealed class DictionaryTranslationDirectionTests
{
    [Theory]
    [InlineData("行", "en")]
    [InlineData("請稍候。", "en")]
    [InlineData("𠮷", "en")]
    [InlineData("如何使用 CancellationTokenSource？", "en")]
    [InlineData("Good morning!", "zh-Hans")]
    [InlineData("What does 银行 mean here?", "zh-Hans")]
    [InlineData("明日は晴れです。", "zh-Hans")]
    [InlineData("韓國 사람", "zh-Hans")]
    [InlineData("123.45%", "zh-Hans")]
    public void AutomaticDirectionUsesTheQueriedText(string query, string target) =>
        Assert.Equal(target, DictionaryTranslationDirection.ResolveTargetId(query, DictionaryTranslationDirection.AutoId));

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
