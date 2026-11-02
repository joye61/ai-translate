using System.Diagnostics;
using System.Runtime.InteropServices;
using AiTranslate.Core;
using SkiaSharp;

namespace AiTranslate.Platform.Linux;

/// <summary>
/// Linux (X11 & Wayland) Desktop Platform Service:
/// - Primary Selection (`xclip -o -selection primary` / `wl-paste --primary`) for instant zero-intrusion Linux mouse selection translation
/// - Clipboard monitoring (`xclip -o -selection clipboard` / `wl-paste`)
/// - Full-screen capture (`grim` on Wayland / `import -window root` or `gnome-screenshot` on X11) into BGRA8 DisplayFrames
/// </summary>
public sealed class LinuxDesktopPlatformService : IDesktopPlatformService
{
    private readonly Timer _monitorTimer;
    private string _lastClipboardText = "";
    private string _lastPrimarySelectionText = "";
    private int _disposed;

    public string Name => "Linux";

    public bool IsClipboardMonitoringEnabled { get; set; }
    public bool IsMouseSelectionMonitoringEnabled { get; set; }

    public event EventHandler<CapturedTextEvent>? ClipboardTextChanged;
    public event EventHandler<CapturedTextEvent>? MouseSelectionCaptured;

    public LinuxDesktopPlatformService()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("LinuxDesktopPlatformService requires Linux.");
        _monitorTimer = new Timer(OnMonitorTick, null, 350, 350);
    }

    public IReadOnlyList<DisplayInfo> GetDisplays() =>
        [new DisplayInfo("linux-0", "Linux Display", new RectD(0, 0, 1920, 1080), 1920, 1080, 1.0, true)];

    public PointD GetCursorPosition() => new(400, 300);

    public IReadOnlyList<WindowInfo> GetWindows() => [];

    public string GetForegroundWindowTitle()
    {
        try
        {
            return TryRun("xdotool", ["getactivewindow", "getwindowname"])?.Trim() ?? "Linux Window";
        }
        catch
        {
            return "Linux Window";
        }
    }

    public Task<IReadOnlyList<DisplayFrame>> CaptureDisplaysAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DisplayFrame>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPng = Path.Combine(Path.GetTempPath(), $"aitranslate_linux_{Guid.NewGuid():N}.png");
            try
            {
                // Try Wayland grim first, then gnome-screenshot, then ImageMagick import
                if (!TryCaptureFile("grim", [tempPng], tempPng) &&
                    !TryCaptureFile("gnome-screenshot", ["-f", tempPng], tempPng))
                {
                    TryCaptureFile("import", ["-window", "root", tempPng], tempPng);
                }

                if (!File.Exists(tempPng)) return [];
                using var stream = File.OpenRead(tempPng);
                using var codec = SKCodec.Create(stream);
                if (codec is null) return [];
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bmp = SKBitmap.Decode(codec, info);
                if (bmp is null) return [];

                var stride = bmp.Width * 4;
                var pixels = new byte[stride * bmp.Height];
                Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
                var display = new DisplayInfo("linux-0", "Linux Display", new RectD(0, 0, bmp.Width, bmp.Height), bmp.Width, bmp.Height, 1.0, true);
                return [new DisplayFrame(display, pixels, stride)];
            }
            finally
            {
                try { if (File.Exists(tempPng)) File.Delete(tempPng); } catch { }
            }
        }, cancellationToken);
    }

    public bool TryRegisterHotkey(GlobalHotkeyId id, HotkeyGesture gesture, Action callback, out string? error)
    {
        error = null;
        return true;
    }

    public void UnregisterHotkey(GlobalHotkeyId id)
    {
    }

    public Task<CapturedTextEvent?> TryCaptureSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        // On Linux X11/Wayland, reading the PRIMARY selection directly retrieves highlighted text without touching CLIPBOARD
        var text = ReadLinuxPrimarySelection()?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return Task.FromResult<CapturedTextEvent?>(null);

        return Task.FromResult<CapturedTextEvent?>(new CapturedTextEvent(
            text,
            TranslationTriggerSource.SelectionPopup,
            GetCursorPosition(),
            GetForegroundWindowTitle()));
    }

    private void OnMonitorTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            if (IsClipboardMonitoringEnabled)
            {
                var clip = ReadLinuxClipboard()?.Trim();
                if (!string.IsNullOrWhiteSpace(clip) && clip.Length <= 8000 && !string.Equals(clip, _lastClipboardText, StringComparison.Ordinal))
                {
                    _lastClipboardText = clip;
                    ClipboardTextChanged?.Invoke(this, new CapturedTextEvent(clip, TranslationTriggerSource.ClipboardMonitor, GetCursorPosition(), GetForegroundWindowTitle()));
                }
            }

            if (IsMouseSelectionMonitoringEnabled)
            {
                var primary = ReadLinuxPrimarySelection()?.Trim();
                if (!string.IsNullOrWhiteSpace(primary) && primary.Length <= 4000 && !string.Equals(primary, _lastPrimarySelectionText, StringComparison.Ordinal))
                {
                    _lastPrimarySelectionText = primary;
                    MouseSelectionCaptured?.Invoke(this, new CapturedTextEvent(primary, TranslationTriggerSource.SelectionPopup, GetCursorPosition(), GetForegroundWindowTitle()));
                }
            }
        }
        catch
        {
        }
    }

    private static string? ReadLinuxPrimarySelection() =>
        TryRun("wl-paste", ["--primary", "--no-newline"]) ??
        TryRun("xclip", ["-o", "-selection", "primary"]);

    private static string? ReadLinuxClipboard() =>
        TryRun("wl-paste", ["--no-newline"]) ??
        TryRun("xclip", ["-o", "-selection", "clipboard"]);

    private static bool TryCaptureFile(string cmd, string[] args, string expectedFile)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(cmd, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            proc?.WaitForExit(4000);
            return File.Exists(expectedFile);
        }
        catch
        {
            return false;
        }
    }

    private static string? TryRun(string cmd, string[] args)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(cmd, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (proc is null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _monitorTimer.Dispose();
    }
}
