using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiTranslate.Desktop.Controls;

internal static class AppIcons
{
    private const string Logo = "linux/hicolor/256x256/apps/ai-translate.png";
    private static readonly Lazy<Bitmap> LogoBitmap = new(() =>
    {
        using var stream = Open(Logo);
        return new Bitmap(stream);
    });

    public static Image CreateLogo(double size)
    {
        var image = new Image
        {
            Source = LogoBitmap.Value, Width = size, Height = size, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        return image;
    }

    public static WindowIcon CreateWindowIcon() =>
        LoadIcon(OperatingSystem.IsWindows() ? "windows/ai-translate.ico" : Logo);

    public static WindowIcon CreateTrayIcon() => LoadIcon(OperatingSystem.IsMacOS() ? "macos/TrayTemplate@2x.png" :
        OperatingSystem.IsWindows() ? "windows/tray.ico" : "linux/tray.png");

    private static WindowIcon LoadIcon(string name)
    {
        using var stream = Open(name);
        return new WindowIcon(stream);
    }

    private static Stream Open(string name) =>
        AssetLoader.Open(new Uri($"avares://AiTranslate.Desktop/Assets/Brand/{name}"));
}
