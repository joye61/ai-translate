using AiTranslate.Core;
using SkiaSharp;

namespace AiTranslate.Desktop;

/// <summary>
/// Finds ruled table cells from actual raster separators, never from empty space alone.
/// Ported from XCapture.Desktop.TranslationTableLayout.
/// </summary>
internal static class TranslationTableLayout
{
    private const int MaxEdge = 8192, MaxPixels = 16_000_000, MaxSegments = 512;
    private const int Tolerance = 5;

    private sealed record Segment(bool Horizontal, double Position, int Start, int End)
    {
        public bool Covers(double start, double end) => Start <= start + Tolerance && End >= end - Tolerance;
    }

    internal static IReadOnlyList<RectD> FindCells(byte[] png, OcrResult source)
    {
        if (source.Width <= 0 || source.Height <= 0 || source.Lines.Count == 0) return [];
        using var encoded = new SKMemoryStream(png);
        using var codec = SKCodec.Create(encoded);
        if (codec == null || codec.Info.Width < 12 || codec.Info.Height < 12
            || codec.Info.Width > MaxEdge || codec.Info.Height > MaxEdge
            || (long)codec.Info.Width * codec.Info.Height > MaxPixels) return [];
        var width = codec.Info.Width; var height = codec.Info.Height;
        using var bitmap = SKBitmap.Decode(codec, new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (bitmap == null) return [];
        var luminance = new byte[width * height];
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var p = y * bitmap.RowBytes + x * 4;
                luminance[y * width + x] = (byte)((pixels[p] * 29 + pixels[p + 1] * 150 + pixels[p + 2] * 77) >> 8);
            }
        var lineHeights = source.Lines.Select(l => l.Bounds.Height * height / source.Height)
            .Where(h => double.IsFinite(h) && h > 0).Order().ToArray();
        var typicalHeight = lineHeights.Length > 0 ? lineHeights[lineHeights.Length / 2] : 20;
        var minimumRun = (int)Math.Clamp(typicalHeight * 3, 40, 120);
        var horizontal = Scan(luminance, width, height, true, minimumRun);
        var vertical = Scan(luminance, width, height, false, minimumRun);
        if (horizontal.Count == 0 || vertical.Count == 0
            || horizontal.Count + vertical.Count > MaxSegments) return [];

        var segments = horizontal.Concat(vertical).ToArray();
        var parents = Enumerable.Range(0, segments.Length).ToArray();
        int Root(int i) { while (parents[i] != i) { parents[i] = parents[parents[i]]; i = parents[i]; } return i; }
        for (var h = 0; h < horizontal.Count; h++)
            for (var v = 0; v < vertical.Count; v++)
                if (Intersects(horizontal[h], vertical[v])) parents[Root(horizontal.Count + v)] = Root(h);

        var cells = new List<RectD>();
        foreach (var component in Enumerable.Range(0, segments.Length).GroupBy(Root))
        {
            var hs = component.Select(i => segments[i]).Where(s => s.Horizontal).ToList();
            var vs = component.Select(i => segments[i]).Where(s => !s.Horizontal).ToList();
            if (hs.Count < 1 || vs.Count < 1) continue;
            var strongGrid = hs.Count >= 3 && vs.Count >= 1 || vs.Count >= 3 && hs.Count >= 1
                || hs.Count >= 2 && vs.Count >= 2;
            if (!strongGrid) continue;
            AddClippedEdges(hs, vs, width, height);
            if (hs.Count < 2 || vs.Count < 2 || hs.Count + vs.Count < 5) continue;
            var xs = Coordinates(vs); var ys = Coordinates(hs);
            if (xs.Length > 128 || ys.Length > 128) continue;
            for (var yi = 1; yi < ys.Length; yi++)
                for (var xi = 1; xi < xs.Length; xi++)
                {
                    var left = xs[xi - 1]; var right = xs[xi]; var top = ys[yi - 1]; var bottom = ys[yi];
                    if (right - left < Math.Max(12, typicalHeight) || bottom - top < Math.Max(10, typicalHeight * .6)) continue;
                    if (!HasSide(hs, top, left, right) || !HasSide(hs, bottom, left, right)
                        || !HasSide(vs, left, top, bottom) || !HasSide(vs, right, top, bottom)) continue;
                    var cell = new RectD(left * source.Width / width, top * source.Height / height,
                        (right - left) * source.Width / width, (bottom - top) * source.Height / height);
                    if (cells.All(existing => existing.Intersect(cell).IsEmpty)) cells.Add(cell);
                }
        }
        return cells.OrderBy(c => c.Y).ThenBy(c => c.X).ToArray();
    }

    private static List<Segment> Scan(byte[] values, int width, int height, bool horizontal, int minimumRun)
    {
        var across = horizontal ? height : width; var along = horizontal ? width : height;
        var normalStep = horizontal ? width : 1; var tangentStep = horizontal ? 1 : width;
        var raw = new List<Segment>();
        for (var position = 2; position < across - 2; position++)
        {
            var start = -1; var last = -1; var hits = 0;
            for (var point = 0; point <= along; point++)
            {
                var offset = position * normalStep + Math.Min(point, along - 1) * tangentStep;
                var ridge = point < along && (IsRidge(values, offset, normalStep * 2)
                    || position >= 4 && position < across - 4 && IsRidge(values, offset, normalStep * 4));
                if (ridge)
                {
                    if (start < 0) { start = point; hits = 0; }
                    last = point; hits++;
                }
                if (start < 0 || point < along && point - last <= 4) continue;
                if (last - start + 1 >= minimumRun && hits >= (last - start + 1) * .88)
                    raw.Add(new(horizontal, position, start, last));
                start = -1;
            }
            if (raw.Count > MaxSegments * 8) return [];
        }
        var merged = new List<Segment>();
        foreach (var line in raw)
        {
            var index = merged.FindLastIndex(other => line.Position - other.Position <= 4
                && line.Position >= other.Position
                && Math.Min(line.End, other.End) - Math.Max(line.Start, other.Start)
                    >= Math.Min(line.End - line.Start, other.End - other.Start) * .8);
            if (index < 0) merged.Add(line);
            else
            {
                var other = merged[index];
                merged[index] = other with
                {
                    Position = (other.Position + line.Position) / 2,
                    Start = Math.Min(other.Start, line.Start), End = Math.Max(other.End, line.End)
                };
            }
            if (merged.Count > MaxSegments) return [];
        }
        return merged;
    }

    private static bool IsRidge(byte[] pixels, int center, int delta)
    {
        var a = pixels[center - delta] - pixels[center];
        var b = pixels[center + delta] - pixels[center];
        return Math.Abs(a) >= 4 && Math.Abs(b) >= 4 && Math.Sign(a) == Math.Sign(b)
            && Math.Abs(a - b) <= Math.Max(14, Math.Min(Math.Abs(a), Math.Abs(b)) * .8);
    }

    private static bool Intersects(Segment horizontal, Segment vertical) =>
        horizontal.Covers(vertical.Position, vertical.Position) && vertical.Covers(horizontal.Position, horizontal.Position);

    private static bool HasSide(IEnumerable<Segment> lines, double position, double start, double end) =>
        lines.Any(line => Math.Abs(line.Position - position) <= 2 && line.Covers(start, end));

    private static double[] Coordinates(IEnumerable<Segment> lines) => lines.Select(l => l.Position).Distinct().Order().ToArray();

    private static void AddClippedEdges(List<Segment> hs, List<Segment> vs, int width, int height)
    {
        var left = hs.Where(l => l.Start <= Tolerance).ToArray();
        var right = hs.Where(l => l.End >= width - 1 - Tolerance).ToArray();
        var top = vs.Where(l => l.Start <= Tolerance).ToArray();
        var bottom = vs.Where(l => l.End >= height - 1 - Tolerance).ToArray();
        if (left.Length >= 2 && vs.All(l => l.Position > Tolerance))
            vs.Add(new(false, 0, (int)left.Min(l => l.Position), (int)Math.Ceiling(left.Max(l => l.Position))));
        if (right.Length >= 2 && vs.All(l => l.Position < width - Tolerance))
            vs.Add(new(false, width, (int)right.Min(l => l.Position), (int)Math.Ceiling(right.Max(l => l.Position))));
        if (top.Length >= 2 && hs.All(l => l.Position > Tolerance))
            hs.Add(new(true, 0, (int)top.Min(l => l.Position), (int)Math.Ceiling(top.Max(l => l.Position))));
        if (bottom.Length >= 2 && hs.All(l => l.Position < height - Tolerance))
            hs.Add(new(true, height, (int)bottom.Min(l => l.Position), (int)Math.Ceiling(bottom.Max(l => l.Position))));
        for (var i = 0; i < vs.Count; i++)
            if (vs[i].Position is 0 || vs[i].Position == width)
                vs[i] = vs[i] with { Start = hs.Any(h => h.Position == 0) ? 0 : vs[i].Start,
                    End = hs.Any(h => h.Position == height) ? height : vs[i].End };
        for (var i = 0; i < hs.Count; i++)
            if (hs[i].Position is 0 || hs[i].Position == height)
                hs[i] = hs[i] with { Start = vs.Any(v => v.Position == 0) ? 0 : hs[i].Start,
                    End = vs.Any(v => v.Position == width) ? width : hs[i].End };
    }
}
