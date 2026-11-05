using AiTranslate.Ai;
using AiTranslate.Core;
using AiTranslate.Desktop.Controls;
using AiTranslate.Platform.Linux;
using AiTranslate.Platform.MacOS;
using AiTranslate.Platform.Windows;
using AiTranslate.Speech;
using AiTranslate.Vocabulary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace AiTranslate.Desktop;

public sealed class App : Application
{
    private IDesktopPlatformService? _platform;
    private ISpeechSynthesisService? _speechService;

    static App() => InputElement.PointerPressedEvent.AddClassHandler<Window>(
        ClearSelectionOnOutsidePress, RoutingStrategies.Tunnel, handledEventsToo: true);

    private static void ClearSelectionOnOutsidePress(Window window, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(window).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            window.FocusManager?.GetFocusedElement() is not Control owner ||
            owner is not (SelectableTextBlock or TextBox) || TopLevel.GetTopLevel(owner) != window ||
            e.Source is not Visual hit)
            return;

        // Menus need the original selection until their Copy or Lookup command runs.
        if (hit.GetSelfAndVisualAncestors().Any(v => v == owner || v is PopupRoot or OverlayPopupHost)) return;
        switch (owner)
        {
            case SelectableTextBlock text when text.SelectionStart != text.SelectionEnd:
                text.ClearSelection();
                break;
            case TextBox editor when editor.SelectionStart != editor.SelectionEnd:
                editor.ClearSelection();
                break;
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _platform = CreatePlatformService();
            var selectionPrivacy = _platform is WindowsDesktopPlatformService windows ? new WindowsSelectionPrivacy(windows) : null;
            var settingsStore = new AppSettingsStore();
            var vocabularyService = new VocabularyService();
            var dictionaryService = new AiDictionaryService(() => settingsStore.CreateTransport(),
                new DictionaryEntryCache(), vocabularyService.FindGlossaryTerms, vocabularyService.RefreshCollectedEntries);
            _speechService = new KokoroSpeechService();

            var mainWindow = new MainWindow(
                _platform,
                settingsStore,
                dictionaryService,
                vocabularyService,
                _speechService);

            desktop.MainWindow = mainWindow;
            _ = _speechService.EnsureKokoroModelReadyAsync();
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.Exit += (_, _) =>
            {
                selectionPrivacy?.Dispose();
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
