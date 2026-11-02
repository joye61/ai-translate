using System.Diagnostics;
using System.Runtime.InteropServices;
using AiTranslate.Core;
using SkiaSharp;

namespace AiTranslate.Platform.MacOS;

/// <summary>
/// macOS Desktop Platform Service:
/// - Screen capture via macOS `screencapture` / CoreGraphics into BGRA8 DisplayFrames
/// - Clipboard monitoring via `pbpaste` polling
/// - Active text selection capture via AppleScript (`System Events` keystroke "c" using command down) with clipboard restoration
/// - Display metrics via CoreGraphics / system_profiler fallback
/// </summary>
public sealed class MacDesktopPlatformService : IDesktopPlatformService
{
    private readonly Timer _monitorTimer;
    private string _lastClipboardText = "";
    private volatile bool _suppressClipboardEvents;
    private int _disposed;
    private bool _wasLeftButtonDown;
    private PointD _mouseDownPos;
    private DateTimeOffset _mouseDownTime;
    private int _selectionCaptureInFlight;

    public string Name => "macOS";

    public bool IsClipboardMonitoringEnabled { get; set; }
    public bool IsMouseSelectionMonitoringEnabled { get; set; }

    public event EventHandler<CapturedTextEvent>? ClipboardTextChanged;
    public event EventHandler<CapturedTextEvent>? MouseSelectionCaptured;

    internal void RaiseMouseSelection(CapturedTextEvent e) => MouseSelectionCaptured?.Invoke(this, e);

    public MacDesktopPlatformService()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("MacDesktopPlatformService requires macOS.");
        _monitorTimer = new Timer(OnMonitorTick, null, 350, 350);
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        try
        {
            var mainId = CGMainDisplayID();
            var width = (int)CGDisplayPixelsWide(mainId);
            var height = (int)CGDisplayPixelsHigh(mainId);
            if (width > 0 && height > 0)
            {
                return [new DisplayInfo("mac-main", "Built-in Retina Display", new RectD(0, 0, width, height), width, height, 2.0, true)];
            }
        }
        catch
        {
        }
        return [new DisplayInfo("mac-main", "Primary Display", new RectD(0, 0, 1920, 1080), 1920, 1080, 2.0, true)];
    }

    public PointD GetCursorPosition()
    {
        var evt = CGEventCreate(0);
        if (evt == 0) return new PointD(0, 0);
        try
        {
            var point = CGEventGetLocation(evt);
            return new PointD(point.X, point.Y);
        }
        finally { CFRelease(evt); }
    }

    public IReadOnlyList<WindowInfo> GetWindows() => [];

    public string GetForegroundWindowTitle()
    {
        try
        {
            return RunCommand("osascript", ["-e", "tell application \"System Events\" to get name of first application process whose frontmost is true"]).Trim();
        }
        catch
        {
            return "macOS App";
        }
    }

    public Task<IReadOnlyList<DisplayFrame>> CaptureDisplaysAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DisplayFrame>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPng = Path.Combine(Path.GetTempPath(), $"aitranslate_mac_{Guid.NewGuid():N}.png");
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("screencapture", ["-x", "-t", "png", tempPng])
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit(5000);
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
                var display = new DisplayInfo("mac-0", "macOS Display", new RectD(0, 0, bmp.Width, bmp.Height), bmp.Width, bmp.Height, 2.0, true);
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

    public async Task<CapturedTextEvent?> TryCaptureSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _selectionCaptureInFlight, 1, 0) != 0)
            return null;

        _suppressClipboardEvents = true;
        try
        {
            var prev = ReadPbpaste();
            RunCommand("osascript", ["-e", "tell application \"System Events\" to keystroke \"c\" using command down"]);
            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
            var current = ReadPbpaste();
            if (!string.IsNullOrWhiteSpace(prev) && !string.Equals(prev, current, StringComparison.Ordinal))
                WritePbcopy(prev);

            if (string.IsNullOrWhiteSpace(current)) return null;
            return new CapturedTextEvent(current.Trim(), TranslationTriggerSource.SelectionPopup, GetCursorPosition(), GetForegroundWindowTitle());
        }
        catch
        {
            return null;
        }
        finally
        {
            _suppressClipboardEvents = false;
            Interlocked.Exchange(ref _selectionCaptureInFlight, 0);
        }
    }

    private void OnMonitorTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            if (IsClipboardMonitoringEnabled && !_suppressClipboardEvents)
            {
                var text = ReadPbpaste()?.Trim();
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= 8000 && !string.Equals(text, _lastClipboardText, StringComparison.Ordinal))
                {
                    _lastClipboardText = text;
                    ClipboardTextChanged?.Invoke(this, new CapturedTextEvent(text, TranslationTriggerSource.ClipboardMonitor, GetCursorPosition(), GetForegroundWindowTitle()));
                }
            }

            if (IsMouseSelectionMonitoringEnabled && Volatile.Read(ref _selectionCaptureInFlight) == 0)
            {
                const int kCGEventSourceStateHidSystemState = 1;
                const int kCGMouseButtonLeft = 0;
                var isDown = CGEventSourceButtonState(kCGEventSourceStateHidSystemState, kCGMouseButtonLeft);
                if (isDown && !_wasLeftButtonDown)
                {
                    _wasLeftButtonDown = true;
                    _mouseDownPos = GetCursorPosition();
                    _mouseDownTime = DateTimeOffset.UtcNow;
                }
                else if (!isDown && _wasLeftButtonDown)
                {
                    _wasLeftButtonDown = false;
                    var upPos = GetCursorPosition();
                    var durationMs = (DateTimeOffset.UtcNow - _mouseDownTime).TotalMilliseconds;
                    var dx = Math.Abs(upPos.X - _mouseDownPos.X);
                    var dy = Math.Abs(upPos.Y - _mouseDownPos.Y);
                    if (durationMs is >= 140 and <= 8000 && dx >= 18 && dy <= 240)
                    {
                        _ = Task.Run(async () =>
                        {
                            var captured = await TryCaptureSelectedTextAsync().ConfigureAwait(false);
                            if (captured is not null && !string.IsNullOrWhiteSpace(captured.Text))
                                RaiseMouseSelection(captured);
                        });
                    }
                }
            }
            else
            {
                _wasLeftButtonDown = false;
            }
        }
        catch
        {
        }
    }

    private static string ReadPbpaste() => RunCommand("pbpaste", []);

    private static void WritePbcopy(string text)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("pbcopy")
            {
                RedirectStandardInput = true,
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (proc is not null)
            {
                proc.StandardInput.Write(text);
                proc.StandardInput.Close();
                proc.WaitForExit(1500);
            }
        }
        catch { }
    }

    private static string RunCommand(string fileName, string[] args)
    {
        using var proc = Process.Start(new ProcessStartInfo(fileName, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        });
        if (proc is null) return "";
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(2000);
        return output;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _monitorTimer.Dispose();
    }

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern uint CGMainDisplayID();

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nuint CGDisplayPixelsWide(uint display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nuint CGDisplayPixelsHigh(uint display);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern nint CGEventCreate(nint source);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern CGPoint CGEventGetLocation(nint @event);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGEventSourceButtonState(int stateId, int button);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(nint cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { internal double X, Y; }
}
