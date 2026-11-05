using AiTranslate.Core;
using Xunit;

namespace AiTranslate.Tests;

public sealed class DictionaryQueryInputTests
{
    [Theory]
    [InlineData("loans,", "loans")]
    [InlineData(" \uFEFF✨【“loans，”】👏🏽　", "loans")]
    [InlineData("‘loans’", "loans")]
    [InlineData("（cafe\u0301）；", "cafe\u0301")]
    [InlineData("　「借款」，", "借款")]
    [InlineData("take out a loan；", "take out a loan")]
    [InlineData("loans.", "loans")]
    [InlineData(" ，✨🙂\u200B ", "")]
    [InlineData("“？！”", "")]
    [InlineData("don't,", "don't")]
    [InlineData("borrowers’", "borrowers’")]
    [InlineData("'tis", "'tis")]
    [InlineData("C++,", "C++")]
    [InlineData("“C++”，", "C++")]
    [InlineData("C#", "C#")]
    [InlineData(".NET", ".NET")]
    [InlineData("U.S.", "U.S.")]
    [InlineData("Dr.", "Dr.")]
    [InlineData("$10.50", "$10.50")]
    [InlineData("-5%", "-5%")]
    [InlineData("f(x)", "f(x)")]
    [InlineData("f(),", "f()")]
    [InlineData("int[]", "int[]")]
    [InlineData("std::", "std::")]
    [InlineData(":root", ":root")]
    [InlineData("• apples\n• pears", "• apples\n• pears")]
    [InlineData("— loans —", "loans")]
    [InlineData("——", "")]
    [InlineData("--help", "--help")]
    [InlineData("- 5", "- 5")]
    [InlineData("Really?", "Really?")]
    [InlineData("¡Hola!", "¡Hola!")]
    [InlineData("Wait...", "Wait...")]
    [InlineData("loans, fees, and interest.", "loans, fees, and interest.")]
    [InlineData("I ❤️ NY", "I ❤️ NY")]
    [InlineData("Made in 🇯🇵", "Made in 🇯🇵")]
    [InlineData("I feel 😢", "I feel 😢")]
    [InlineData("我很生气😡", "我很生气😡")]
    [InlineData("First, wait.\r\n\r\nThen go!", "First, wait.\r\n\r\nThen go!")]
    public void OnlyIncidentalEdgesAreRemoved(string input, string expected)
    {
        Assert.Equal(expected, DictionaryQuery.CleanInput(input));
        Assert.Equal(expected, DictionaryQuery.CleanInput(expected));
        Assert.Equal(expected.Length != 0, DictionaryQuery.HasContent(input));
    }
}
