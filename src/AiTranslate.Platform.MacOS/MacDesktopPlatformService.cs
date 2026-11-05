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
    private uint _lastMouseUp;
    private bool _wasLeftButtonDown;
    private long _lastReleaseTime;
    private PointD _lastReleasePoint;
    private PointD _mouseDownPos;
    private int _selectionCaptureInFlight;
    private int _monitorInFlight;

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
        _lastMouseUp = CGEventSourceCounterForEventType(0, 2);
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

    public Task<CapturedTextEvent?> TryCaptureSelectedTextAsync(CancellationToken cancellationToken = default) =>
        CaptureSelectionAsync(allowCopy: true, GetCursorPosition(), cancellationToken);

    private async Task<CapturedTextEvent?> CaptureSelectionAsync(bool allowCopy, PointD point, CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _selectionCaptureInFlight, 1, 0) != 0) return null;
        _suppressClipboardEvents = true;
        try
        {
            var text = await Task.Run(() => RunCommand("osascript", ["-l", "JavaScript", "-e", SelectionScript,
                allowCopy ? "copy" : "selection", Environment.ProcessId.ToString()]).Trim(), token).ConfigureAwait(false);
            if (text.Length == 0) return null;
            return new(text, TranslationTriggerSource.SelectionPopup, point, GetForegroundWindowTitle());
        }
        finally
        {
            _suppressClipboardEvents = false;
            Interlocked.Exchange(ref _selectionCaptureInFlight, 0);
        }
    }

    // Prefer AXSelectedText. For copy-only applications, materialize every pasteboard item and type before Cmd+C.
    private const string SelectionScript = """
        ObjC.import('AppKit');
        function run(args) {
            const events = Application('System Events');
            const app = events.applicationProcesses.whose({frontmost: true})[0];
            if (app.unixId() == Number(args[1])) return '';
            try {
                const focused = app.attributes.byName('AXFocusedUIElement').value();
                try { if (focused.subrole() === 'AXSecureTextField') return ''; } catch (_) {}
                const text = focused.attributes.byName('AXSelectedText').value();
                if (typeof text === 'string') return text;
            } catch (_) {}
            if (args[0] !== 'copy') return '';
            const board = $.NSPasteboard.generalPasteboard, original = board.changeCount;
            const saved = $.NSMutableArray.alloc.init, items = board.pasteboardItems;
            for (let i = 0; i < items.count; i++) {
                const old = items.objectAtIndex(i), item = $.NSPasteboardItem.alloc.init;
                const types = old.types;
                for (let j = 0; j < types.count; j++) {
                    const type = types.objectAtIndex(j), data = old.dataForType(type);
                    if (data.isNil() || !item.setDataForType(data, type)) return '';
                }
                saved.addObject(item);
            }
            if (board.changeCount !== original || !app.frontmost()) return '';
            events.keystroke('c', {using: 'command down'});
            for (let i = 0; i < 20 && board.changeCount === original; i++) delay(0.025);
            const copied = board.changeCount;
            if (copied === original || !app.frontmost()) return '';
            try { return ObjC.unwrap(board.stringForType($.NSPasteboardTypeString)) || ''; }
            finally {
                if (board.changeCount === copied) {
                    board.clearContents;
                    if (saved.count) board.writeObjects(saved);
                }
            }
        }
        """;

    private void OnMonitorTick(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _monitorInFlight, 1) != 0) return;
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

            // Quartz counts releases even when both clicks of a double-click fall between timer ticks.
            var releases = CGEventSourceCounterForEventType(0, 2); // kCGEventLeftMouseUp
            if (IsMouseSelectionMonitoringEnabled)
            {
                var isDown = CGEventSourceButtonState(0, 0);
                var point = GetCursorPosition();
                if (isDown && !_wasLeftButtonDown) _mouseDownPos = point;
                if (!isDown && releases != _lastMouseUp && Volatile.Read(ref _selectionCaptureInFlight) == 0)
                {
                    var now = Environment.TickCount64;
                    var multiple = unchecked(releases - _lastMouseUp) > 1 ||
                        now - _lastReleaseTime <= 500 && Math.Abs(point.X - _lastReleasePoint.X) <= 4 && Math.Abs(point.Y - _lastReleasePoint.Y) <= 4;
                    _lastMouseUp = releases;
                    _lastReleaseTime = now;
                    _lastReleasePoint = point;
                    _ = PublishSelectionAsync(multiple || _wasLeftButtonDown && point != _mouseDownPos, point, releases);
                }
                _wasLeftButtonDown = isDown;
            }
            else
            {
                _lastMouseUp = releases;
                _wasLeftButtonDown = false;
            }
        }
        catch
        {
        }
        finally { Volatile.Write(ref _monitorInFlight, 0); }
    }

    private static string ReadPbpaste() => RunCommand("pbpaste", []);

    private async Task PublishSelectionAsync(bool allowCopy, PointD point, uint release)
    {
        try
        {
            var captured = await CaptureSelectionAsync(allowCopy, point, CancellationToken.None).ConfigureAwait(false);
            if (captured is not null && Volatile.Read(ref _disposed) == 0 && IsMouseSelectionMonitoringEnabled &&
                CGEventSourceCounterForEventType(0, 2) == release) RaiseMouseSelection(captured);
        }
        catch (System.ComponentModel.Win32Exception) { }
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
    private static extern uint CGEventSourceCounterForEventType(int stateId, int eventType);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CGEventSourceButtonState(int stateId, int button);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(nint cf);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint { internal double X, Y; }
}
