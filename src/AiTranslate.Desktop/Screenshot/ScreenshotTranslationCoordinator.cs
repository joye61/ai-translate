using AiTranslate.Core;
using AiTranslate.Ocr;
using Avalonia.Threading;

namespace AiTranslate.Desktop;

/// <summary>
/// Coordinates multi-display screen capture and launches the XCapture-engine in-place translation overlay.
/// </summary>
public sealed class ScreenshotTranslationCoordinator : IDisposable
{
    private readonly IDesktopPlatformService _platform;
    private readonly AppSettingsStore _settings;
    private readonly Action<string, string?> _onOpenInDictionary;
    private readonly Action<string, string> _onQuickSaveToNotebook;
    private LocalOcrService? _ocrService;
    private ScreenshotOverlayWindow? _activeWindow;
    private bool _disposed;

    public bool IsActive => _activeWindow is not null;

    public ScreenshotTranslationCoordinator(
        IDesktopPlatformService platform,
        AppSettingsStore settings,
        Action<string, string?> onOpenInDictionary,
        Action<string, string> onQuickSaveToNotebook)
    {
        _platform = platform;
        _settings = settings;
        _onOpenInDictionary = onOpenInDictionary;
        _onQuickSaveToNotebook = onQuickSaveToNotebook;
    }

    public async Task StartCaptureAndTranslateAsync()
    {
        if (_disposed || _activeWindow is not null) return;

        IReadOnlyList<DisplayFrame> frames;
        try
        {
            frames = await _platform.CaptureDisplaysAsync();
        }
        catch
        {
            return;
        }
        if (frames.Count == 0) return;

        var cursor = _platform.GetCursorPosition();
        var frame = frames.FirstOrDefault(f => f.Display.Bounds.Contains(cursor))
            ?? frames.FirstOrDefault(f => f.Display.IsPrimary)
            ?? frames[0];

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _ocrService ??= new LocalOcrService();
            var win = new ScreenshotOverlayWindow(
                frame,
                _ocrService,
                _settings,
                _onOpenInDictionary,
                _onQuickSaveToNotebook,
                () => _activeWindow = null);
            _activeWindow = win;
            win.Show();
            win.Activate();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeWindow?.Close();
        _ocrService?.Dispose();
    }
}
