using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiTranslate.Core;

public static partial class DictionaryQuery
{
    private enum Edge { Content, Padding, Separator, Colon, Bracket, Apostrophe, Face, Period, Dash, Intonation }

    public static bool HasContent([NotNullWhen(true)] string? input)
    {
        var rest = input.AsSpan();
        while (!rest.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(rest);
            if (ClassifyEdge(rest[..length]) == Edge.Content) return true;
            rest = rest[length..];
        }
        return false;
    }

    /// <summary>Remove incidental selection boundaries without changing the spelling or layout inside them.</summary>
    public static string CleanInput(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length == 0 || char.IsLetterOrDigit(text[0]) && char.IsLetterOrDigit(text[^1])) return text;

        // Work with whole text elements so a skin tone, variation selector or combining accent stays with its base.
        var offsets = StringInfo.ParseCombiningCharacters(text);
        ReadOnlySpan<char> Element(int i) => text.AsSpan(offsets[i], Offset(i + 1) - offsets[i]);
        int Offset(int i) => i == offsets.Length ? text.Length : offsets[i];

        var first = 0;
        var last = offsets.Length - 1;
        while (first <= last && ClassifyEdge(Element(first)) != Edge.Content) first++;
        if (first > last) return "";
        while (ClassifyEdge(Element(last)) != Edge.Content) last--;
        var core = text.AsSpan(offsets[first], Offset(last + 1) - offsets[first]);
        var prefix = text.AsSpan(0, offsets[first]);
        var suffix = text.AsSpan(Offset(last + 1));
        var term = PlainTerm().IsMatch(core.TrimEnd("+#"));
        var word = term && !core.ContainsAny(' ', '\t');
        var quoted = prefix.ContainsAny('\'', '‘', '’') && suffix.ContainsAny('\'', '’');
        // Brackets which open after the term belong to expressions such as f() and int[].
        var bracketed = term && !suffix.ContainsAny("([（【「『《") && !prefix.ContainsAny(")]）】」』》");
        var labelColon = term && !prefix.Contains(':') && !suffix.Contains("::", StringComparison.Ordinal);
        var ordinaryWord = word && core.Length > 1 && char.IsLower(core[^1]) && !Abbreviation().IsMatch(core);
        // A whole sentence in an unspaced script can look like one word. Its final emoji may supply the tone.
        var lexicalFace = word && (core.Length <= 2 || char.IsLower(core[0]) || char.IsUpper(core[0])) &&
            !suffix.ContainsAny("?!？！…");
        var spacedDashBefore = term && !prefix.IsEmpty && char.IsWhiteSpace(prefix[^1]) && char.IsLetter(core[0]);
        var spacedDashAfter = term && !suffix.IsEmpty && char.IsWhiteSpace(suffix[0]) && char.IsLetter(core[^1]);

        bool CanDrop(int i, bool trailing) => ClassifyEdge(Element(i)) switch
        {
            Edge.Padding => true,
            Edge.Separator => term,
            Edge.Bracket => bracketed,
            Edge.Colon => trailing && labelColon,
            Edge.Apostrophe => term && quoted,
            Edge.Face => lexicalFace,
            Edge.Dash => trailing ? spacedDashAfter : spacedDashBefore,
            Edge.Period => trailing && ordinaryWord && i == last + 1 &&
                (i + 1 == offsets.Length || ClassifyEdge(Element(i + 1)) != Edge.Period),
            _ => false
        };

        var start = 0;
        var end = offsets.Length;
        while (start < first && CanDrop(start, trailing: false)) start++;
        while (end > last + 1 && CanDrop(end - 1, trailing: true)) end--;
        return text[offsets[start]..Offset(end)];
    }

    private static Edge ClassifyEdge(ReadOnlySpan<char> element)
    {
        Rune.DecodeFromUtf16(element, out var rune, out _);
        if (Rune.IsWhiteSpace(rune)) return Edge.Padding;
        return rune.Value switch
        {
            >= 0x200B and <= 0x200F or 0x2060 or 0xFEFF or 0xFE0E or 0xFE0F or
                ',' or '，' or '、' or '،' or '✨' or '⭐' or
                0x1F31F or 0x1F389 or 0x1F38A => Edge.Padding,
            ';' or '；' or '：' or '•' or '●' or '▪' or '★' or '☆' or '"' or '“' or '”' or '«' or '»' => Edge.Separator,
            ':' => Edge.Colon,
            '(' or ')' or '[' or ']' or '（' or '）' or '【' or '】' or '「' or '」' or '『' or '』' or '《' or '》' => Edge.Bracket,
            '\'' or '‘' or '’' => Edge.Apostrophe,
            >= 0x1F600 and <= 0x1F64F or 0x263A or 0x1F44B or 0x1F44D or 0x1F44F or 0x1F973 => Edge.Face,
            '.' or '。' => Edge.Period,
            '-' or '‐' or '‑' or '‒' or '–' or '—' => Edge.Dash,
            '!' or '?' or '！' or '？' or '…' => Edge.Intonation,
            _ => Edge.Content
        };
    }

    [GeneratedRegex(@"\A[\p{L}\p{M}\p{N}]+(?:[ '\t’\-][\p{L}\p{M}\p{N}]+)*\z", RegexOptions.NonBacktracking)]
    private static partial Regex PlainTerm();

    [GeneratedRegex(@"\A(?:mr|mrs|ms|dr|jr|sr|st|vs|etc|prof|approx|dept|inc|ltd|corp|fig|no)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Abbreviation();
}
