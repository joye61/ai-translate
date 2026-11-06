using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AiTranslate.Desktop;

/// <summary>
/// Font Awesome Free (https://fontawesome.com/icons) vector icon control.
/// Every icon in AI Translate is a filled Font Awesome glyph, fitted into a square of <see cref="IconSize"/>.
/// </summary>
public sealed class FontAwesomeIcon : Control
{
    public static readonly StyledProperty<string> IconNameProperty =
        AvaloniaProperty.Register<FontAwesomeIcon, string>(nameof(IconName), FontAwesomeIconLibrary.DefaultIcon);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<FontAwesomeIcon, IBrush?>(nameof(Foreground), Brushes.White);

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<FontAwesomeIcon, double>(nameof(IconSize), 18.0);

    public string IconName
    {
        get => GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    static FontAwesomeIcon()
    {
        AffectsRender<FontAwesomeIcon>(IconNameProperty, ForegroundProperty, IconSizeProperty);
        AffectsMeasure<FontAwesomeIcon>(IconSizeProperty);
    }

    public FontAwesomeIcon()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
    }

    public FontAwesomeIcon(string iconName, double size = 18.0, IBrush? foreground = null) : this()
    {
        IconName = iconName;
        IconSize = size;
        if (foreground is not null)
            Foreground = foreground;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Math.Max(8, IconSize);
        return new Size(s, s);
    }

    public override void Render(DrawingContext context)
    {
        if (Foreground is null) return;
        var (geometry, width, height) = FontAwesomeIconLibrary.GetGeometry(IconName);
        var targetSize = Math.Max(8, IconSize) * FontAwesomeIconLibrary.OpticalScale;
        var scale = targetSize / Math.Max(width, height);
        // Center the drawn shape, not the glyph's em box: some glyphs (window-minimize) sit at the edge of it.
        var ink = geometry.Bounds;
        var offsetX = (Bounds.Width - ink.Width * scale) / 2.0 - ink.X * scale;
        var offsetY = (Bounds.Height - ink.Height * scale) / 2.0 - ink.Y * scale;

        var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);
        using (context.PushTransform(matrix))
        {
            context.DrawGeometry(Foreground, null, geometry);
        }
    }
}

/// <summary>
/// Registry of Font Awesome Free (https://fontawesome.com/icons) icons. The path data lives in the generated
/// <c>FontAwesomeIconPaths.cs</c>; extend it through <c>tools/generate-fontawesome-icons.mjs</c>.
/// Names are Font Awesome names: solid by default, <c>regular/name</c> for the regular style.
/// </summary>
public static partial class FontAwesomeIconLibrary
{
    public const string DefaultIcon = "language";

    /// <summary>Font Awesome glyphs fill their whole em box, so they are drawn slightly inside the nominal icon size.</summary>
    internal const double OpticalScale = 0.9;

    private readonly record struct IconShape(double Width, double Height, string Path);

    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsRegistered(string iconName) => Shapes.ContainsKey(iconName);

    internal static IReadOnlyCollection<string> Names => Shapes.Keys;

    internal static (Geometry Geometry, double Width, double Height) GetGeometry(string iconName)
    {
        if (!Shapes.TryGetValue(iconName, out var shape))
        {
            iconName = DefaultIcon;
            shape = Shapes[DefaultIcon];
        }

        if (!Cache.TryGetValue(iconName, out var geometry))
        {
            // Font Awesome glyphs rely on the non-zero fill rule (F1); the parser default is even-odd.
            geometry = StreamGeometry.Parse("F1 " + shape.Path);
            Cache[iconName] = geometry;
        }

        return (geometry, shape.Width, shape.Height);
    }
}
