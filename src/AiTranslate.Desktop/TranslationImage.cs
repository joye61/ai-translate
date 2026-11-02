using AiTranslate.Ai;
using AiTranslate.Core;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using SkiaSharp;

namespace AiTranslate.Desktop;

internal sealed record TranslationPlacement(string Id, Rect Bounds, double FontSize, Rect TextBounds, string Text, bool PreservedOriginal = false);

/// <summary>
/// One composed screenshot in original image pixels. Display and export share these pixels.
/// Ported directly from XCapture.Desktop.TranslationImage.
/// </summary>
internal sealed class TranslationImage : IDisposable
{
    private const int MaxImageEdge = 8192;
    private const long MaxImagePixels = 16_000_000;
    public Bitmap Bitmap { get; }
    public int Width => Bitmap.PixelSize.Width;
    public int Height => Bitmap.PixelSize.Height;
    public string Text { get; }
    internal IReadOnlyList<TranslationPlacement> Placements { get; }

    private TranslationImage(Bitmap bitmap, string text, IReadOnlyList<TranslationPlacement> placements)
    {
        Bitmap = bitmap;
        Text = text;
        Placements = placements;
    }

    internal static TranslationImage Create(byte[] png, OcrResult source,
        IReadOnlyList<TranslationRegion> regions, IReadOnlyList<TranslationRegionText> texts, bool rightToLeft, double pixelsPerDip = 1)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0) throw new ArgumentOutOfRangeException(nameof(pixelsPerDip));
        using var encoded = new SKMemoryStream(png);
        using var codec = SKCodec.Create(encoded) ?? throw new InvalidOperationException("无法读取原始截图。");
        if (codec.Info.Width > MaxImageEdge || codec.Info.Height > MaxImageEdge || (long)codec.Info.Width * codec.Info.Height > MaxImagePixels)
            throw new InvalidOperationException("截图超过译文图片的安全尺寸，请缩小选区后重试。");
        using var pixels = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("无法读取原始截图。");
        if (source.Width <= 0 || source.Height <= 0 || regions.Count == 0)
            throw new InvalidOperationException("没有可用于对齐译文的识别区域。");
        var byId = texts.ToDictionary(t => t.Id, StringComparer.Ordinal);
        if (byId.Count != regions.Count || regions.Any(r => !byId.ContainsKey(r.Id)))
            throw new InvalidOperationException("译文区域与原文不一致，请重新翻译。");
        var sx = (double)pixels.Width / source.Width;
        var sy = (double)pixels.Height / source.Height;
        var canvas = new Rect(0, 0, pixels.Width, pixels.Height);
        Rect Scale(RectD rect) => new Rect(rect.X * sx, rect.Y * sy, rect.Width * sx, rect.Height * sy).Intersect(canvas);
        string DisplayText(TranslationRegion region) => region.Reflow ? TranslationLayout.ReflowText(byId[region.Id].Text) : byId[region.Id].Text;
        var preservedRegions = regions.Where(r => string.Equals(DisplayText(r), r.Text, StringComparison.Ordinal))
            .Select(r => Scale(r.Bounds)).ToArray();
        var tableMetrics = regions.Where(region => region.IsTableCell).SelectMany(region => region.Lines)
            .Where(line => line.Bounds.Width >= line.Bounds.Height * 2)
            .Select(line =>
            {
                var box = Scale(line.Bounds);
                var otherPreserved = preservedRegions.Where(keep => !keep.Contains(box.Center)).ToArray();
                return (BoxHeight: box.Height, InkHeight: FindInk(pixels, box, SampleBackground(pixels, box), otherPreserved).Height);
            }).ToArray();
        var drawing = new List<(TranslationPlacement Placement, TextLayout Layout, IReadOnlyList<(Rect Bounds, IBrush Brush)> Masks)>();
        var placements = new List<TranslationPlacement>();
        RenderTargetBitmap? output = null;
        try
        {
            foreach (var region in regions)
            {
                var text = DisplayText(region);
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("译文区域为空，请重新翻译。");
                var bounds = Scale(region.LayoutBounds ?? region.Bounds);
                if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("识别区域超出截图范围。");
                if (string.Equals(text, region.Text, StringComparison.Ordinal))
                {
                    placements.Add(new(region.Id, bounds, 0, bounds, text, PreservedOriginal: true));
                    continue;
                }
                var lineBoxes = region.Lines.Count > 0 ? region.Lines.Select(l => Scale(l.Bounds)).ToArray() : [bounds];
                var masks = new List<(Rect, IBrush)>();
                var background = SampleBackground(pixels, lineBoxes[0]);
                if (!region.IsTableCell && region.LayoutBounds != null)
                    bounds = ConstrainToBackground(pixels, Scale(region.Bounds), bounds, background);
                var foreground = SampleForeground(pixels, lineBoxes, background, preservedRegions);
                var inkBoxes = lineBoxes.Select(line => FindInk(pixels, line, SampleBackground(pixels, line), preservedRegions)).ToArray();
                foreach (var line in lineBoxes)
                    masks.Add((line, new SolidColorBrush(ToColor(SampleBackground(pixels, line))).ToImmutable()));
                var brush = new SolidColorBrush(ToColor(foreground)).ToImmutable();
                var sourceLineHeight = inkBoxes.Select(r => r.Height).Order().ElementAt(inkBoxes.Length / 2);
                if (region.IsTableCell && tableMetrics.Length >= 3)
                {
                    var boxHeight = lineBoxes.Select(box => box.Height).Order().ElementAt(lineBoxes.Length / 2);
                    var peers = tableMetrics.Where(metric => metric.BoxHeight >= boxHeight * .85 && metric.BoxHeight <= boxHeight * 1.15)
                        .Select(metric => metric.InkHeight).Order().ToArray();
                    if (peers.Length >= 3) sourceLineHeight = peers[peers.Length / 2];
                }
                var maxFont = Math.Max(.5, sourceLineHeight * 1.1);
                var inkLeft = rightToLeft ? bounds.X : inkBoxes[0].X;
                var inkRight = rightToLeft && region.LayoutBounds == null ? inkBoxes.Max(r => r.Right) : bounds.Right;
                var inkTop = inkBoxes[0].Y;
                foreach (var other in regions)
                {
                    if (other.Id == region.Id) continue;
                    var obstacle = Scale(other.Bounds);
                    if (!bounds.Intersects(obstacle) || obstacle.Width >= bounds.Width * .35) continue;
                    var otherExclusions = preservedRegions.Where(r => r != obstacle).ToArray();
                    var otherLines = other.Lines.Count > 0 ? other.Lines.Select(l => Scale(l.Bounds)).ToArray() : [obstacle];
                    var otherInk = otherLines.Select(line => FindInk(pixels, line, SampleBackground(pixels, line), otherExclusions)).ToArray();
                    if (!inkBoxes.Any(ink => otherInk.Any(keep => ink.Bottom > keep.Top && ink.Top < keep.Bottom))) continue;
                    if (obstacle.X > bounds.X + bounds.Width * .5) inkRight = Math.Min(inkRight, obstacle.X - 1);
                    else if (obstacle.Right < bounds.X + bounds.Width * .5) inkLeft = Math.Max(inkLeft, obstacle.Right + 1);
                }
                var available = new Rect(inkLeft, inkTop, Math.Max(.1, inkRight - inkLeft), Math.Max(.1, bounds.Bottom - inkTop));
                var layout = Fit(text, available, maxFont, brush, rightToLeft, pixelsPerDip, out var fontSize);
                var ink = Measure(layout);
                var inkX = rightToLeft ? available.Right - ink.Width : available.X;
                var placement = new TranslationPlacement(region.Id, bounds, fontSize,
                    new Rect(inkX, available.Y, ink.Width, ink.Height), text);
                placements.Add(placement);
                drawing.Add((placement, layout, masks));
            }
            using var input = new MemoryStream(png, writable: false);
            using var original = new Bitmap(input);
            output = new RenderTargetBitmap(new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96));
            using (var context = output.CreateDrawingContext())
            {
                context.DrawImage(original, canvas);
                foreach (var item in drawing)
                    foreach (var (bounds, brush) in item.Masks)
                        foreach (var mask in Exclude(bounds, preservedRegions)) context.FillRectangle(brush, mask);
                foreach (var item in drawing)
                {
                    var extent = Measure(item.Layout);
                    var ink = item.Placement.TextBounds;
                    item.Layout.Draw(context, new Point(ink.X - extent.X, ink.Y - extent.Y));
                }
            }
            var result = new TranslationImage(output, string.Join("\n\n", regions.Select(DisplayText)),
                placements.ToArray());
            output = null;
            return result;
        }
        finally
        {
            output?.Dispose();
            foreach (var item in drawing) item.Layout.Dispose();
        }
    }

    private static TextLayout Fit(string text, Rect bounds, double maximum, IBrush brush, bool rtl, double pixelsPerDip, out double fontSize)
    {
        TextLayout Make(double size)
        {
            var wrapWidth = Math.Max(.1, bounds.Width);
            for (var attempt = 0; ; attempt++)
            {
                var layout = new TextLayout(text, new Typeface(AppTheme.BodyFont), size, brush,
                    textAlignment: rtl ? TextAlignment.Right : TextAlignment.Left,
                    textWrapping: TextWrapping.Wrap,
                    flowDirection: rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                    maxWidth: wrapWidth, lineHeight: size * 1.2, textRunCache: null);
                var extraInk = Measure(layout).Width - bounds.Width;
                if (extraInk <= .01 || attempt == 3 || wrapWidth <= .1) return layout;
                layout.Dispose();
                wrapWidth = Math.Max(.1, wrapWidth - extraInk - .05);
            }
        }
        bool Fits(TextLayout layout)
        {
            var extent = Measure(layout);
            return extent.Width <= bounds.Width + .01 && extent.Height <= bounds.Height + .01 &&
                layout.TextLines.All(line => !line.HasCollapsed && !line.HasOverflowed);
        }
        var candidate = Make(maximum);
        if (Fits(candidate)) { fontSize = maximum; return candidate; }
        candidate.Dispose();
        var low = Math.Min(12 * pixelsPerDip, maximum) * .5;
        var high = maximum;
        var best = Make(low);
        if (!Fits(best))
        {
            best.Dispose();
            throw new InvalidOperationException("译文过长，无法以可读字号完整放入选区。请缩小选区后重试。");
        }
        for (var i = 0; i < 15; i++)
        {
            var middle = (low + high) / 2;
            candidate = Make(middle);
            if (Fits(candidate)) { best.Dispose(); best = candidate; low = middle; }
            else { candidate.Dispose(); high = middle; }
        }
        fontSize = low;
        return best;
    }

    private static Rect Measure(TextLayout layout)
    {
        var left = layout.TextLines.Min(line => line.Start + Math.Min(0, line.OverhangLeading));
        var right = layout.TextLines.Max(line => line.Start + line.Width - Math.Min(0, line.OverhangTrailing));
        var first = layout.TextLines[0];
        var last = layout.TextLines[^1];
        var bottom = layout.Height + last.OverhangAfter;
        var top = first.Height + first.OverhangAfter - first.Extent;
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static Rect ConstrainToBackground(SKBitmap pixels, Rect source, Rect candidate, SKColor background)
    {
        var right = candidate.Right;
        var bottom = candidate.Bottom;
        bool Occupied(int x, int y) => Distance(pixels.GetPixel(x, y), background) > 48;
        for (var x = (int)Math.Ceiling(source.Right); x < Math.Min(pixels.Width, Math.Ceiling(right)); x++)
        {
            var blocked = false;
            for (var y = Math.Max(0, (int)Math.Floor(source.Top)); y < Math.Min(pixels.Height, Math.Ceiling(source.Bottom)); y++)
                if (Occupied(x, y)) { blocked = true; break; }
            if (blocked) { right = Math.Max(source.Right, x - 1); break; }
        }
        for (var y = (int)Math.Ceiling(source.Bottom); y < Math.Min(pixels.Height, Math.Ceiling(bottom)); y++)
        {
            var blocked = false;
            for (var x = Math.Max(0, (int)Math.Floor(source.Left)); x < Math.Min(pixels.Width, Math.Ceiling(right)); x++)
                if (Occupied(x, y)) { blocked = true; break; }
            if (blocked) { bottom = Math.Max(source.Bottom, y - 1); break; }
        }
        return new(source.X, source.Y, right - source.X, bottom - source.Y);
    }

    private static SKColor SampleBackground(SKBitmap pixels, Rect bounds)
    {
        var samples = new List<SKColor>();
        var stride = Math.Max(1, (int)Math.Ceiling((bounds.Width + bounds.Height) / 180));
        for (var x = (int)bounds.Left; x <= bounds.Right; x += stride)
        {
            samples.Add(Pixel(pixels, x, (int)Math.Floor(bounds.Top) - 2));
            samples.Add(Pixel(pixels, x, (int)Math.Ceiling(bounds.Bottom) + 1));
        }
        for (var y = (int)bounds.Top; y <= bounds.Bottom; y += stride)
        {
            samples.Add(Pixel(pixels, (int)Math.Floor(bounds.Left) - 2, y));
            samples.Add(Pixel(pixels, (int)Math.Ceiling(bounds.Right) + 1, y));
        }
        return Dominant(samples);
    }

    private static Rect FindInk(SKBitmap pixels, Rect bounds, SKColor background, IReadOnlyList<Rect> preserved)
    {
        var excluded = preserved.Where(r => r.Intersects(bounds)).ToArray();
        var left = Math.Clamp((int)Math.Floor(bounds.Left), 0, pixels.Width - 1);
        var right = Math.Clamp((int)Math.Ceiling(bounds.Right), left + 1, pixels.Width);
        var top = Math.Clamp((int)Math.Floor(bounds.Top), 0, pixels.Height - 1);
        var bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom), top + 1, pixels.Height);
        var minX = right; var maxX = left; var minY = bottom; var maxY = top;
        for (var y = top; y < bottom; y++)
        {
            var count = 0; var rowLeft = right; var rowRight = left;
            for (var x = left; x < right; x++)
                if (!IsExcluded(x, y, excluded) && Distance(pixels.GetPixel(x, y), background) > 80)
                { count++; rowLeft = Math.Min(rowLeft, x); rowRight = x + 1; }
            if (count < Math.Max(2, (right - left) / 400) || count > (right - left) * .9) continue;
            minX = Math.Min(minX, rowLeft); maxX = Math.Max(maxX, rowRight);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y + 1);
        }
        return minX < maxX && minY < maxY ? new Rect(minX, minY, maxX - minX, maxY - minY).Intersect(bounds) : bounds;
    }

    private static bool IsExcluded(int x, int y, IReadOnlyList<Rect> excluded)
    {
        foreach (var rect in excluded)
            if (x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom) return true;
        return false;
    }

    private static IEnumerable<Rect> Exclude(Rect mask, IReadOnlyList<Rect> preserved)
    {
        var pieces = new List<Rect> { mask };
        foreach (var keep in preserved)
        {
            var remaining = new List<Rect>();
            foreach (var piece in pieces)
            {
                var overlap = piece.Intersect(keep);
                if (overlap.Width <= 0 || overlap.Height <= 0) { remaining.Add(piece); continue; }
                if (overlap.Top > piece.Top) remaining.Add(new Rect(piece.Left, piece.Top, piece.Width, overlap.Top - piece.Top));
                if (overlap.Bottom < piece.Bottom) remaining.Add(new Rect(piece.Left, overlap.Bottom, piece.Width, piece.Bottom - overlap.Bottom));
                if (overlap.Left > piece.Left) remaining.Add(new Rect(piece.Left, overlap.Top, overlap.Left - piece.Left, overlap.Height));
                if (overlap.Right < piece.Right) remaining.Add(new Rect(overlap.Right, overlap.Top, piece.Right - overlap.Right, overlap.Height));
            }
            pieces = remaining;
        }
        return pieces;
    }

    private static SKColor SampleForeground(SKBitmap pixels, IReadOnlyList<Rect> lines, SKColor background, IReadOnlyList<Rect> preserved)
    {
        var samples = new List<SKColor>();
        foreach (var line in lines)
        {
            var excluded = preserved.Where(r => r.Intersects(line)).ToArray();
            var stride = Math.Max(1, (int)Math.Sqrt(line.Width * line.Height / 2500));
            for (var y = (int)line.Top; y < line.Bottom; y += stride)
                for (var x = (int)line.Left; x < line.Right; x += stride)
                {
                    if (IsExcluded(x, y, excluded)) continue;
                    var color = Pixel(pixels, x, y);
                    if (Distance(color, background) > 80) samples.Add(color);
                }
        }
        if (samples.Count == 0) return background.Red + background.Green + background.Blue > 384 ? SKColors.Black : SKColors.White;
        return Dominant(samples);
    }

    private static int Distance(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
    private static SKColor Pixel(SKBitmap pixels, int x, int y) => pixels.GetPixel(Math.Clamp(x, 0, pixels.Width - 1), Math.Clamp(y, 0, pixels.Height - 1));
    private static Color ToColor(SKColor color) => Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue);
    private static SKColor Dominant(IEnumerable<SKColor> samples)
    {
        var group = samples.GroupBy(c => (c.Red >> 4, c.Green >> 4, c.Blue >> 4)).MaxBy(g => g.Count());
        if (group == null) return SKColors.White;
        var colors = group.ToArray();
        return new SKColor((byte)colors.Average(c => c.Red), (byte)colors.Average(c => c.Green), (byte)colors.Average(c => c.Blue));
    }

    public byte[] ExportPng()
    {
        Dispatcher.UIThread.VerifyAccess();
        using var stream = new MemoryStream();
        Bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    public void Dispose() => Bitmap.Dispose();
}
