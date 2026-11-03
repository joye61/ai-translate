namespace AiTranslate.Core;

public enum InlineStyle { Plain, Strong, Quote }

public readonly record struct InlineSpan(string Text, InlineStyle Style);

/// <summary>
/// The light inline markup AI explanations may use: <c>**关键点**</c> for emphasis and <c>`source form`</c> for a quoted
/// source-language expression. Anything unpaired or malformed stays literal, so stray asterisks never vanish.
/// </summary>
public static class InlineMarkup
{
    public static IReadOnlyList<InlineSpan> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var spans = new List<InlineSpan>();
        var plain = 0;
        var i = 0;
        while (i < text.Length)
        {
            var (marker, style) = text[i] == '`' ? ("`", InlineStyle.Quote)
                : string.CompareOrdinal(text, i, "**", 0, 2) == 0 ? ("**", InlineStyle.Strong)
                // Models often italicize foreign words with *x* despite instructions; treat it as a quoted form.
                : text[i] == '*' ? ("*", InlineStyle.Quote) : ("", InlineStyle.Plain);
            if (marker.Length == 0) { i++; continue; }
            var open = i + marker.Length;
            var close = text.IndexOf(marker, open, StringComparison.Ordinal);
            if (close <= open || !IsTight(text, open, close) || IsAsciiWord(text, i - 1) || IsAsciiWord(text, close + marker.Length))
            {
                i += marker.Length;
                continue;
            }
            if (i > plain) spans.Add(new(text[plain..i], InlineStyle.Plain));
            spans.Add(new(text[open..close], style));
            i = plain = close + marker.Length;
        }
        if (plain < text.Length) spans.Add(new(text[plain..], InlineStyle.Plain));
        return spans;
    }

    public static string Strip(string? text) => string.Concat(Parse(text).Select(s => s.Text));

    public static bool HasMarkup(string? text) => Parse(text).Any(s => s.Style != InlineStyle.Plain);

    // "a ** b ** c" is arithmetic-like prose, not emphasis; marked content must hug its markers and stay on one line.
    private static bool IsTight(string text, int start, int end) =>
        !char.IsWhiteSpace(text[start]) && !char.IsWhiteSpace(text[end - 1]) && text.IndexOf('\n', start, end - start) < 0;

    // Markers glued to Latin letters or digits on the outside (a*b, x**2) are operators, not emphasis.
    // CJK neighbors are fine because Chinese prose has no spaces around emphasis.
    private static bool IsAsciiWord(string text, int index) =>
        index >= 0 && index < text.Length && char.IsAsciiLetterOrDigit(text[index]);
}
