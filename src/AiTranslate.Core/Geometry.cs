namespace AiTranslate.Core;

public readonly record struct SizeD(double Width, double Height);

public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>Half-open native desktop coordinates. Negative origins are valid.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public bool Contains(PointD p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;
    public RectD Translate(PointD delta) => this with { X = X + delta.X, Y = Y + delta.Y };
    public RectD Intersect(RectD other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        return new(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }
    public static RectD Between(PointD a, PointD b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    public static RectD Union(IEnumerable<RectD> rectangles)
    {
        var items = rectangles.Where(r => !r.IsEmpty).ToArray();
        return items.Length == 0
            ? default
            : new(items.Min(r => r.X), items.Min(r => r.Y), items.Max(r => r.Right) - items.Min(r => r.X), items.Max(r => r.Bottom) - items.Min(r => r.Y));
    }
}
