using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace AiTranslate.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return;

        instance.ListenForActivation(() => Dispatcher.UIThread.Post(() =>
        {
            if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
                is MainWindow window)
                window.ShowAndActivate();
        }));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
