using System.Text;

namespace AiTranslate.Core;

/// <summary>A translation unit anchored to the source screenshot's pixel coordinates.</summary>
public sealed record TranslationRegion(string Id, string Text, RectD Bounds, IReadOnlyList<OcrLine> Lines,
    RectD? LayoutBounds = null, bool Reflow = false, bool IsTableCell = false);

/// <summary>Conservative paragraph grouping that preserves headings, columns and isolated UI labels.</summary>
public static class TranslationLayout
{
    public static IReadOnlyList<TranslationRegion> CreateRegions(OcrResult source, IReadOnlyList<RectD>? cells = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0) return [];
        var image = new RectD(0, 0, source.Width, source.Height);
        var groups = new List<List<OcrLine>>();
        var groupCells = new List<int>();
        var validCells = (cells ?? []).Where(Finite).Select(cell => cell.Intersect(image)).Where(cell => !cell.IsEmpty).ToArray();
        foreach (var original in source.Lines)
        {
            if (string.IsNullOrWhiteSpace(original.Text) || !Finite(original.Bounds)) continue;
            var line = original with { Bounds = original.Bounds.Intersect(image) };
            if (line.Bounds.IsEmpty) continue;
            var cell = Array.FindIndex(validCells, candidate =>
            {
                var overlap = candidate.Intersect(line.Bounds);
                return overlap.Width * overlap.Height >= line.Bounds.Width * line.Bounds.Height * .98;
            });
            var previous = Enumerable.Range(0, groups.Count)
                .Where(index => groupCells[index] == cell &&
                    ((cell >= 0 ? ContinuesInCell(groups[index][^1], line) : Continues(groups[index][^1], line))
                    || InlineContinuation(groups[index], line)))
                .OrderBy(index => Math.Abs(line.Bounds.Y - groups[index][^1].Bounds.Bottom))
                .FirstOrDefault(-1);
            if (previous >= 0) groups[previous].Add(line);
            else { groups.Add([line]); groupCells.Add(cell); }
        }

        if (groups.Count == 0)
            return string.IsNullOrWhiteSpace(source.Text) ? [] : [new("r0", source.Text.Trim(), image, [])];
        var regions = groups.Select((lines, index) =>
        {
            var text = RegionText(lines);
            var reflow = lines.Count > 1 && !HasStructuralBreaks(text);
            return new TranslationRegion($"r{index}", reflow ? ReflowText(text) : text,
                RectD.Union(lines.Select(line => line.Bounds)), lines.AsReadOnly(), Reflow: reflow);
        }).ToArray();
        for (var index = 0; index < regions.Length; index++)
        {
            if (groupCells[index] < 0) continue;
            var region = regions[index]; var cell = validCells[groupCells[index]];
            var height = region.Lines.Select(line => line.Bounds.Height).Order().ElementAt(region.Lines.Count / 2);
            var padding = Math.Clamp(Math.Min(region.Bounds.X - cell.X, cell.Right - region.Bounds.Right), 2, Math.Max(2, height));
            var right = Math.Max(region.Bounds.Right, cell.Right - padding);
            var bottom = Math.Max(region.Bounds.Bottom, cell.Bottom - Math.Max(2, height * .4));
            foreach (var other in regions.Where((_, otherIndex) => otherIndex != index && groupCells[otherIndex] == groupCells[index]))
            {
                if (other.Bounds.Y >= region.Bounds.Bottom)
                    bottom = Math.Min(bottom, Math.Max(region.Bounds.Bottom, other.Bounds.Y - height * .25));
                if (other.Bounds.X >= region.Bounds.Right && other.Bounds.Y < bottom && other.Bounds.Bottom > region.Bounds.Y)
                    right = Math.Min(right, Math.Max(region.Bounds.Right, other.Bounds.X - height * .25));
            }
            var reflow = !HasStructuralBreaks(region.Text);
            regions[index] = region with
            {
                LayoutBounds = new(region.Bounds.X, region.Bounds.Y, right - region.Bounds.X, bottom - region.Bounds.Y),
                Reflow = reflow,
                Text = reflow ? ReflowText(region.Text) : region.Text,
                IsTableCell = true
            };
        }
        AllocateParagraphSpace(regions, validCells, image);
        return regions;
    }

    private static void AllocateParagraphSpace(TranslationRegion[] regions, IReadOnlyList<RectD> cells, RectD image)
    {
        var prose = regions.Where(IsProseParagraph).ToArray();
        foreach (var region in prose)
        {
            var height = LineHeight(region);
            var otherBounds = regions.Where(other => other.Id != region.Id).Select(other => other.Bounds)
                .Concat(cells).ToArray();
            if (otherBounds.Any(other => !other.Intersect(region.Bounds).IsEmpty)) continue;
            var right = prose.Where(other =>
                    Math.Abs(other.Bounds.X - region.Bounds.X) <= Math.Max(3, height * .65)
                    && Math.Max(LineHeight(other), height) <= Math.Min(LineHeight(other), height) * 1.4
                    && Math.Max(0, Math.Max(other.Bounds.Y - region.Bounds.Bottom, region.Bounds.Y - other.Bounds.Bottom)) <= height * 8)
                .Max(other => other.Bounds.Right);
            right = Math.Min(image.Right, Math.Max(region.Bounds.Right, right));
            var bottom = Math.Max(region.Bounds.Bottom, image.Bottom - Math.Min(2, height * .1));
            var gap = Math.Max(2, height * .25);
            foreach (var other in otherBounds)
                if (other.X >= region.Bounds.Right && other.Y < region.Bounds.Bottom && other.Bottom > region.Bounds.Y)
                    right = Math.Min(right, Math.Max(region.Bounds.Right, other.X - gap));
            foreach (var other in otherBounds)
                if (other.Y >= region.Bounds.Bottom && other.X < right && other.Right > region.Bounds.X)
                    bottom = Math.Min(bottom, Math.Max(region.Bounds.Bottom, other.Y - gap));
            foreach (var other in otherBounds)
                if (other.X >= region.Bounds.Right && other.Y < bottom && other.Bottom > region.Bounds.Y)
                    right = Math.Min(right, Math.Max(region.Bounds.Right, other.X - gap));
            var bounds = new RectD(region.Bounds.X, region.Bounds.Y, right - region.Bounds.X, bottom - region.Bounds.Y);
            var index = Array.IndexOf(regions, region);
            regions[index] = region with
            {
                LayoutBounds = bounds != region.Bounds ? bounds : null,
                Reflow = true,
                Text = ReflowText(region.Text)
            };
        }
    }

    private static double LineHeight(TranslationRegion region) =>
        region.Lines.Select(line => line.Bounds.Height).Order().ElementAt(region.Lines.Count / 2);

    private static bool IsProseParagraph(TranslationRegion region)
    {
        if (region.IsTableCell || region.Lines.Count == 0 || HasStructuralBreaks(region.Text)) return false;
        var letters = region.Text.Count(char.IsLetter);
        return region.Lines.Max(line => line.Bounds.Width) >= LineHeight(region) * 10
            && letters >= 24 && (region.Text.IndexOfAny(['.', '!', '?', '。', '！', '？']) >= 0
                || region.Lines.Count > 1 && letters >= 50);
    }

    private static bool ContinuesInCell(OcrLine previous, OcrLine current)
    {
        var a = previous.Bounds; var b = current.Bounds;
        var height = Math.Min(a.Height, b.Height);
        var gap = b.Y - a.Bottom;
        return gap >= -height * .2 && gap <= height * 1.05
            && Math.Max(a.Height, b.Height) <= height * 1.4
            && Math.Abs(a.X - b.X) <= Math.Max(3, height * .65)
            && !StartsListItem(previous.Text) && !StartsListItem(current.Text);
    }

    private static bool HasStructuralBreaks(string text) => text.Split('\n').Any(line =>
    {
        var value = line.Trim();
        var colon = value.IndexOf(':');
        var configurationKey = colon > 0 && value[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
        var parenthesis = value.IndexOf('(');
        var call = parenthesis > 0 && (char.IsAsciiLetterOrDigit(value[parenthesis - 1]) || value[parenthesis - 1] == '_') && value.Contains(')');
        return StartsListItem(value) || value.StartsWith('#') || value.StartsWith("//", StringComparison.Ordinal)
            || value.StartsWith("/*", StringComparison.Ordinal) || value.StartsWith('*') || value.Contains("```")
            || value.IndexOfAny(['{', '}', ';', '=', '<', '>']) >= 0
            || new[] { "def ", "class ", "return ", "import ", "from ", "using ", "const ", "var ", "let ", "if ", "while ", "for ", "public ", "private ", "function ", "$ ", "> ", "@" }
                .Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal))
            || value.Contains("\"\"\"", StringComparison.Ordinal) || value.Contains("'''", StringComparison.Ordinal)
            || call || configurationKey;
    });

    /// <summary>Reflow visual prose wraps only; blank paragraphs and structured text retain their breaks.</summary>
    public static string ReflowText(string text)
    {
        if (HasStructuralBreaks(text)) return text;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            var value = line.Trim();
            if (value.Length == 0)
            {
                if (result.Length > 0 && result[^1] != '\n') result.Append("\n\n");
                continue;
            }
            if (result.Length > 0 && result[^1] != '\n' && !IsCjk(result[^1]) && !IsCjk(value[0])) result.Append(' ');
            result.Append(value);
        }
        return result.ToString().TrimEnd();
    }

    private static bool IsCjk(char value) => value is >= '\u2e80' and <= '\u9fff' or >= '\uf900' and <= '\ufaff' or >= '\uff00' and <= '\uffef';

    private static bool InlineContinuation(IReadOnlyList<OcrLine> paragraph, OcrLine current)
    {
        if (paragraph.Count < 2) return false;
        var first = paragraph[0].Bounds; var last = paragraph[^1].Bounds; var next = current.Bounds;
        if (last.Y - first.Y < Math.Min(first.Height, last.Height) * .7 || !SameRow(last, next)) return false;
        var bounds = RectD.Union(paragraph.Select(line => line.Bounds));
        var height = Math.Min(last.Height, next.Height);
        var gap = next.X - last.Right;
        return next.X > last.X && gap >= -Math.Min(next.Width * .35, height * .6) && gap <= height * .75
            && next.Width <= bounds.Width * .25
            && next.Right <= bounds.Right + Math.Max(3, height * .15);
    }

    private static string RegionText(IReadOnlyList<OcrLine> lines)
    {
        var text = new StringBuilder(lines[0].Text.Trim());
        for (var index = 1; index < lines.Count; index++)
            text.Append(SameRow(lines[index - 1].Bounds, lines[index].Bounds) ? ' ' : '\n').Append(lines[index].Text.Trim());
        return text.ToString();
    }

    private static bool SameRow(RectD a, RectD b)
    {
        var height = Math.Min(a.Height, b.Height);
        return Math.Max(a.Height, b.Height) <= height * 1.25
            && Math.Abs(a.Y + a.Height / 2 - b.Y - b.Height / 2) <= height * .25;
    }

    private static bool Continues(OcrLine previous, OcrLine current)
    {
        var a = previous.Bounds; var b = current.Bounds;
        var height = Math.Min(a.Height, b.Height);
        var gap = b.Y - a.Bottom;
        if (gap < -height * .1 || gap > height * .55 || Math.Max(a.Height, b.Height) > height * 1.15)
            return false;
        if (Math.Abs(a.X - b.X) > Math.Max(3, height * .35)) return false;
        if (a.Width < b.Width * .85) return false;
        if (StartsListItem(previous.Text) || StartsListItem(current.Text)) return false;
        return true;
    }

    private static bool StartsListItem(string text)
    {
        var value = text.AsSpan().TrimStart();
        if (value.Length < 2) return false;
        if (value[0] is '•' or '●' or '▪' or '◦') return true;
        if (value[0] is '-' or '*' && char.IsWhiteSpace(value[1])) return true;
        var index = 0;
        while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
        return index > 0 && index + 1 < value.Length && value[index] is '.' or ')' or '、'
            && char.IsWhiteSpace(value[index + 1]);
    }

    private static bool Finite(RectD bounds) => double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
        && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height);
}
