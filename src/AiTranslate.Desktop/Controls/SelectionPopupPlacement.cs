using Avalonia;

namespace AiTranslate.Desktop.Controls;

internal static class SelectionPopupPlacement
{
    private const int Margin = 8;
    public const double HeightLimit = 460;

    public static Size MaximumSize(PixelRect workArea, double scaling) => new(
        Math.Max(1, workArea.Width - Margin * 2) / scaling,
        Math.Min(HeightLimit, Math.Max(1, workArea.Height - Margin * 2) / scaling));

    public static PixelPoint Constrain(PixelPoint position, Size size, double scaling, PixelRect workArea)
    {
        var width = (int)Math.Ceiling(size.Width * scaling);
        var height = (int)Math.Ceiling(size.Height * scaling);
        var left = workArea.X + Margin;
        var top = workArea.Y + Margin;
        return new PixelPoint(
            Math.Clamp(position.X, left, Math.Max(left, workArea.Right - Margin - width)),
            Math.Clamp(position.Y, top, Math.Max(top, workArea.Bottom - Margin - height)));
    }
}
