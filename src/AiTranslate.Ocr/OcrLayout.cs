using AiTranslate.Core;
using SkiaSharp;

namespace AiTranslate.Ocr;

internal sealed record DetectedBox(SKPointI[] Points, float Score, int Tile)
{
    public RectD Bounds { get; } = OcrLayout.Bounds(Points);
}

/// <summary>Screenshot geometry policy, independent of the recognition model.</summary>
internal static class OcrLayout
{
    internal static IReadOnlyList<SKRectI> Tiles(int width, int height)
    {
        const int size = 1024, overlap = 128;
        static IEnumerable<int> Starts(int length)
        {
            yield return 0;
            for (var start = size; start < length;)
            {
                var next = Math.Min(start - overlap, length - size);
                yield return Math.Max(0, next);
                start = next + size;
            }
        }
        return (from y in Starts(height) from x in Starts(width)
                select new SKRectI(x, y, Math.Min(width, x + size), Math.Min(height, y + size))).ToArray();
    }

    internal static RectD Bounds(SKPointI[] points)
    {
        var x = points.Min(p => p.X); var y = points.Min(p => p.Y);
        return new RectD(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
    }

    internal static IReadOnlyList<DetectedBox> MergeTiles(IEnumerable<DetectedBox> boxes, CancellationToken token = default)
    {
        var result = new List<DetectedBox>();
        foreach (var incoming in boxes.OrderByDescending(b => b.Bounds.Width * b.Bounds.Height))
        {
            token.ThrowIfCancellationRequested();
            var current = incoming;
            for (var i = result.Count - 1; i >= 0; i--)
            {
                if ((i & 63) == 0) token.ThrowIfCancellationRequested();
                var existing = result[i];
                if (existing.Tile >= 0 && existing.Tile == current.Tile) continue;
                var a = current.Bounds; var b = existing.Bounds;
                var intersection = a.Intersect(b);
                var minArea = Math.Min(a.Width * a.Height, b.Width * b.Height);
                var collinear = intersection.Width > 4 && intersection.Height > Math.Min(a.Height, b.Height) * .65
                    && Math.Abs(a.Y + a.Height / 2 - b.Y - b.Height / 2) < Math.Min(a.Height, b.Height) * .35
                    && Math.Max(a.Height, b.Height) < Math.Min(a.Height, b.Height) * 1.5
                    && IsHorizontal(current.Points) && IsHorizontal(existing.Points);
                if (collinear)
                {
                    var union = RectD.Union([a, b]);
                    current = new DetectedBox([
                        new((int)union.X, (int)union.Y), new((int)union.Right, (int)union.Y),
                        new((int)union.Right, (int)union.Bottom), new((int)union.X, (int)union.Bottom)],
                        Math.Max(current.Score, existing.Score), -1);
                    result.RemoveAt(i);
                    i = result.Count;
                }
                else if (intersection.Width * intersection.Height / minArea >= .65)
                {
                    if (a.Width * a.Height < b.Width * b.Height) current = existing;
                    result.RemoveAt(i);
                    i = result.Count;
                }
            }
            result.Add(current);
        }
        return result;
    }

    internal static IReadOnlyList<DetectedBox> JoinRowFragments(IReadOnlyList<DetectedBox> boxes, CancellationToken token = default)
    {
        var chains = new List<List<DetectedBox>>();
        var passthrough = new List<DetectedBox>();
        var ordered = boxes.Where(b => IsHorizontal(b.Points)).OrderBy(b => b.Bounds.X).ToArray();
        passthrough.AddRange(boxes.Where(b => !IsHorizontal(b.Points)));
        var tails = new List<RectD>();
        foreach (var box in ordered)
        {
            token.ThrowIfCancellationRequested();
            var a = box.Bounds;
            var best = -1; var bestGap = double.MaxValue;
            for (var i = 0; i < tails.Count; i++)
            {
                var b = tails[i];
                var minHeight = Math.Min(a.Height, b.Height);
                var gap = a.X - b.Right;
                if (gap > minHeight * .8 || gap < -minHeight * 2) continue;
                if (Math.Abs(a.Y + a.Height / 2 - b.Y - b.Height / 2) > minHeight * .3) continue;
                if (Math.Max(a.Height, b.Height) > minHeight * 1.5) continue;
                if (gap < bestGap) { best = i; bestGap = gap; }
            }
            if (best < 0) { chains.Add([box]); tails.Add(a); continue; }
            chains[best].Add(box);
            tails[best] = RectD.Union([tails[best], a]);
        }
        var result = new List<DetectedBox>(passthrough);
        foreach (var chain in chains)
        {
            if (chain.Count == 1) { result.Add(chain[0]); continue; }
            var union = RectD.Union(chain.Select(b => b.Bounds).ToArray());
            result.Add(new DetectedBox([
                new((int)union.X, (int)union.Y), new((int)union.Right, (int)union.Y),
                new((int)union.Right, (int)union.Bottom), new((int)union.X, (int)union.Bottom)],
                chain.Max(b => b.Score), -1));
        }
        return result;
    }

    private static bool IsHorizontal(SKPointI[] points) =>
        Math.Abs(points[1].Y - points[0].Y) <= Math.Max(2, Math.Abs(points[1].X - points[0].X) * .03);

    internal static IReadOnlyList<OcrLine> ReadingOrder(IEnumerable<OcrLine> source) =>
        ReadingRows(source).SelectMany(row => row).ToArray();

    internal static string ComposeText(IEnumerable<OcrLine[]> rows) =>
        string.Join('\n', rows.Select(row => string.Join(' ', row.Select(l => l.Text))));

    internal static IReadOnlyList<OcrLine[]> ReadingRows(IEnumerable<OcrLine> source)
    {
        var lines = source.ToArray();
        if (lines.Length == 0) return [];
        if (lines.Length == 1) return [lines];
        var medianHeight = lines.Select(l => l.Bounds.Height).Order().ElementAt(lines.Length / 2);
        return Split(lines, medianHeight, 0);
    }

    private static List<OcrLine[]> Split(OcrLine[] lines, double lineHeight, int depth)
    {
        if (lines.Length < 3 || depth >= 16) return Rows(lines);
        foreach (var horizontal in new[] { true, false })
        {
            var sorted = lines.OrderBy(l => horizontal ? l.Bounds.Y : l.Bounds.X).ToArray();
            double End(OcrLine line) => horizontal ? line.Bounds.Bottom : line.Bounds.Right;
            double Start(OcrLine line) => horizontal ? line.Bounds.Y : line.Bounds.X;
            var end = End(sorted[0]);
            for (var i = 1; i < sorted.Length; i++)
            {
                var threshold = horizontal ? lineHeight * 1.5 : Math.Max(24, lineHeight * 2);
                if (Start(sorted[i]) - end > threshold && (horizontal || i >= 2 && sorted.Length - i >= 2))
                    return [.. Split(sorted[..i], lineHeight, depth + 1), .. Split(sorted[i..], lineHeight, depth + 1)];
                end = Math.Max(end, End(sorted[i]));
            }
        }
        return Rows(lines);
    }

    private static List<OcrLine[]> Rows(OcrLine[] lines)
    {
        var rows = new List<List<OcrLine>>();
        foreach (var line in lines.OrderBy(l => l.Bounds.Y + l.Bounds.Height / 2))
        {
            var row = rows.LastOrDefault();
            if (row == null || Math.Abs(row[0].Bounds.Y + row[0].Bounds.Height / 2 - line.Bounds.Y - line.Bounds.Height / 2)
                > Math.Min(row[0].Bounds.Height, line.Bounds.Height) * .5)
            {
                row = []; rows.Add(row);
            }
            row.Add(line);
        }
        return rows.Select(row => row.OrderBy(l => l.Bounds.X).ToArray()).ToList();
    }
}
