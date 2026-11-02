using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Platform.Linux;
using AiTranslate.Platform.MacOS;
using AiTranslate.Platform.Windows;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AiTranslate.Desktop;

public sealed class App : Application
{
    private IDesktopPlatformService? _platform;
    private ISpeechSynthesisService? _speechService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _platform = CreatePlatformService();
            var settingsStore = new AppSettingsStore();
            var vocabularyService = new VocabularyService();
            var dictionaryService = new AiDictionaryService(() => settingsStore.CreateTransport());
            _speechService = new KokoroSpeechService();

            var mainWindow = new MainWindow(
                _platform,
                settingsStore,
                dictionaryService,
                vocabularyService,
                _speechService);

            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                _speechService?.Dispose();
                _platform?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static IDesktopPlatformService CreatePlatformService()
    {
        if (OperatingSystem.IsWindows()) return new WindowsDesktopPlatformService();
        if (OperatingSystem.IsMacOS()) return new MacDesktopPlatformService();
        if (OperatingSystem.IsLinux()) return new LinuxDesktopPlatformService();
        throw new PlatformNotSupportedException("Unsupported OS platform.");
    }
}
