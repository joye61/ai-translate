namespace AiTranslate.Core;

public sealed record DisplayInfo(string Id, string Name, RectD Bounds, int PixelWidth, int PixelHeight, double ScaleFactor, bool IsPrimary);
public sealed record WindowInfo(string Id, RectD Bounds, string Title = "");

/// <summary>Owned top-down, straight-alpha BGRA8 pixels.</summary>
public sealed record DisplayFrame(DisplayInfo Display, byte[] Pixels, int Stride)
{
    public double Density => Display.PixelWidth / Math.Max(1, Display.Bounds.Width);
}

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Meta = 8 }
public sealed record HotkeyGesture(string Key, HotkeyModifiers Modifiers)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) parts.Add("Win");
        parts.Add(Key);
        return string.Join(" + ", parts);
    }
}

public enum GlobalHotkeyId
{
    ShowMainWindow = 1,
    SelectionTranslate = 2,
    ScreenshotTranslate = 3,
    ClipboardTranslate = 4
}

/// <summary>Information captured when the user selects text on screen or copies to clipboard.</summary>
public sealed record CapturedTextEvent(
    string Text,
    TranslationTriggerSource Source,
    PointD ScreenPoint,
    string SourceWindowTitle = "",
    string SourceProcessName = "");

public enum TranslationTriggerSource
{
    ManualInput,
    ClipboardMonitor,
    SelectionPopup,
    ScreenshotOcr,
    VocabularyReview
}

public interface IDesktopPlatformService : IDisposable
{
    string Name { get; }
    IReadOnlyList<DisplayInfo> GetDisplays();
    PointD GetCursorPosition();
    IReadOnlyList<WindowInfo> GetWindows();
    string GetForegroundWindowTitle();
    Task<IReadOnlyList<DisplayFrame>> CaptureDisplaysAsync(CancellationToken cancellationToken = default);
    bool TryRegisterHotkey(GlobalHotkeyId id, HotkeyGesture gesture, Action callback, out string? error);
    void UnregisterHotkey(GlobalHotkeyId id);

    /// <summary>Captures currently highlighted text in the active application without corrupting the user's clipboard.</summary>
    Task<CapturedTextEvent?> TryCaptureSelectedTextAsync(CancellationToken cancellationToken = default);

    event EventHandler<CapturedTextEvent>? ClipboardTextChanged;
    event EventHandler<CapturedTextEvent>? MouseSelectionCaptured;
    // Optional global pointer notification for dismissing a popup that never took focus.
    event Action<PointD>? PointerPressed { add { } remove { } }

    bool IsClipboardMonitoringEnabled { get; set; }
    bool IsMouseSelectionMonitoringEnabled { get; set; }
}
