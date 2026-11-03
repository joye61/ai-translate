namespace AiTranslate.Core;

public static class DictionaryTextMatch
{
    // Latin-script terms have word boundaries. Han and kana expressions commonly adjoin
    // other characters with no spaces, including in mixed text such as “用AI翻译”.
    public static int IndexOf(string text, string expression, int startIndex = 0,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (string.IsNullOrEmpty(expression)) return -1;
        while (startIndex <= text.Length - expression.Length)
        {
            var index = text.IndexOf(expression, startIndex, comparison);
            if (index < 0) break;
            var end = index + expression.Length;
            var startsInside = index > 0 && IsWordCharacter(expression[0]) && IsWordCharacter(text[index - 1]);
            var endsInside = end < text.Length && IsWordCharacter(expression[^1]) && IsWordCharacter(text[end]);
            if (!startsInside && !endsInside) return index;
            startIndex = index + 1;
        }
        return -1;
    }

    private static bool IsWordCharacter(char c) =>
        (char.IsLetterOrDigit(c) || c == '_') && c is not (>= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff');
}
