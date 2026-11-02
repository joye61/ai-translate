using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using AiTranslate.Core;

namespace AiTranslate.Platform.Windows;

/// <summary>
/// Full Win32 implementation for AI Translate:
/// - Multi-monitor physical pixel screen capture
/// - Multi-hotkey message pump
/// - Clipboard polling / sequence listener with self-copy suppression
/// - Global mouse drag-selection detector + Ctrl+C text extraction with clipboard snapshot & restoration
/// </summary>
public sealed class WindowsDesktopPlatformService : IDesktopPlatformService
{
    private readonly HotkeyPumpThread _hotkeyPump;
    private readonly Timer _monitorTimer;
    private int _disposed;
    private uint _lastClipboardSeq;
    private string _lastClipboardText = "";
    private volatile bool _suppressClipboardEvents;
    private bool _wasLeftButtonDown;
    private PointD _mouseDownPos;
    private DateTimeOffset _mouseDownTime;
    private int _selectionCaptureInFlight;

    public string Name => "Windows";

    public bool IsClipboardMonitoringEnabled { get; set; }
    public bool IsMouseSelectionMonitoringEnabled { get; set; }

    public event EventHandler<CapturedTextEvent>? ClipboardTextChanged;
    public event EventHandler<CapturedTextEvent>? MouseSelectionCaptured;

    public WindowsDesktopPlatformService()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WindowsDesktopPlatformService requires Windows.");
        _hotkeyPump = new HotkeyPumpThread();
        _lastClipboardSeq = NativeMethods.GetClipboardSequenceNumber();
        _monitorTimer = new Timer(OnMonitorTick, null, 150, 120);
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        ThrowIfDisposed();
        using var awareness = new DpiAwarenessScope();
        var displays = new List<DisplayInfo>();
        Exception? callbackError = null;
        NativeMethods.MonitorCallback callback = (nint monitor, nint dc, ref NativeMethods.Rect bounds, nint data) =>
        {
            try
            {
                var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(), Device = string.Empty };
                if (!NativeMethods.GetMonitorInfo(monitor, ref info)) throw NativeFailure("GetMonitorInfo");
                Marshal.ThrowExceptionForHR(NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _));
                var rectangle = ToRectangle(info.Monitor);
                if (!rectangle.IsEmpty)
                {
                    displays.Add(new(
                        info.Device,
                        info.Device,
                        rectangle,
                        checked((int)rectangle.Width),
                        checked((int)rectangle.Height),
                        dpiX / 96d,
                        (info.Flags & 1) != 0));
                }
                return true;
            }
            catch (Exception ex) { callbackError = ex; return false; }
        };
        var succeeded = NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        if (callbackError is not null) throw callbackError;
        if (!succeeded) throw NativeFailure("EnumDisplayMonitors");
        if (displays.Count == 0) throw new InvalidOperationException("系统未报告任何已连接的显示器。");
        return displays;
    }

    public PointD GetCursorPosition()
    {
        ThrowIfDisposed();
        using var awareness = new DpiAwarenessScope();
        return NativeMethods.GetCursorPos(out var pt) ? new PointD(pt.X, pt.Y) : new PointD(0, 0);
    }

    public string GetForegroundWindowTitle()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return "";
        var sb = new StringBuilder(512);
        var len = NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : "";
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        ThrowIfDisposed();
        using var awareness = new DpiAwarenessScope();
        var windows = new List<WindowInfo>();
        NativeMethods.WindowCallback callback = (window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window)) return true;
            if (NativeMethods.GetWindowThreadProcessId(window, out var processId) == 0 || processId == (uint)Environment.ProcessId) return true;
            if (NativeMethods.GetWindowCloaked(window, 14, out var cloaked, sizeof(uint)) == 0 && cloaked != 0) return true;
            var className = new StringBuilder(256);
            if (NativeMethods.GetClassName(window, className, className.Capacity) == 0) return true;
            if (className.ToString() is "Progman" or "WorkerW") return true;
            if (NativeMethods.GetWindowFrame(window, 9, out var bounds, Marshal.SizeOf<NativeMethods.Rect>()) != 0 &&
                !NativeMethods.GetWindowRect(window, out bounds)) return true;
            var rectangle = ToRectangle(bounds);
            if (rectangle.Width >= 2 && rectangle.Height >= 2)
            {
                var titleSb = new StringBuilder(256);
                NativeMethods.GetWindowText(window, titleSb, titleSb.Capacity);
                windows.Add(new(window.ToString(CultureInfo.InvariantCulture), rectangle, titleSb.ToString()));
            }
            return true;
        };
        NativeMethods.EnumWindows(callback, 0);
        return windows;
    }

    public Task<IReadOnlyList<DisplayFrame>> CaptureDisplaysAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return Task.Run<IReadOnlyList<DisplayFrame>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var awareness = new DpiAwarenessScope();
            var displays = GetDisplays();
            var frames = new DisplayFrame[displays.Count];
            Parallel.For(0, displays.Count, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken }, index =>
            {
                using var workerAwareness = new DpiAwarenessScope();
                frames[index] = CaptureDisplay(displays[index]);
            });
            cancellationToken.ThrowIfCancellationRequested();
            return frames;
        }, cancellationToken);
    }

    public bool TryRegisterHotkey(GlobalHotkeyId id, HotkeyGesture gesture, Action callback, out string? error)
    {
        ThrowIfDisposed();
        return _hotkeyPump.TryRegister((int)id, gesture, callback, out error);
    }

    public void UnregisterHotkey(GlobalHotkeyId id)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _hotkeyPump.Unregister((int)id);
    }

    public async Task<CapturedTextEvent?> TryCaptureSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.CompareExchange(ref _selectionCaptureInFlight, 1, 0) != 0)
            return null;

        _suppressClipboardEvents = true;
        try
        {
            var fgHwnd = NativeMethods.GetForegroundWindow();
            if (fgHwnd != 0 && NativeMethods.GetWindowThreadProcessId(fgHwnd, out var pid) != 0 && pid == (uint)Environment.ProcessId)
                return null;

            var windowTitle = GetForegroundWindowTitle();
            var cursor = GetCursorPosition();
            var clipboardSnapshot = TrySnapshotClipboard();
            var beforeSeq = NativeMethods.GetClipboardSequenceNumber();

            SendCtrlC();

            string? captured = null;
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
                var currentSeq = NativeMethods.GetClipboardSequenceNumber();
                if (currentSeq != beforeSeq)
                {
                    captured = TryReadClipboardUnicodeText();
                    break;
                }
            }

            // Ctrl+C only mutated the clipboard if its sequence number actually changed; restore the
            // full original snapshot (every format, not just text) so non-text content isn't lost.
            if (clipboardSnapshot is not null && NativeMethods.GetClipboardSequenceNumber() != beforeSeq)
                TryRestoreClipboard(clipboardSnapshot);

            _lastClipboardSeq = NativeMethods.GetClipboardSequenceNumber();
            if (string.IsNullOrWhiteSpace(captured))
                return null;

            return new CapturedTextEvent(
                Text: captured.Trim(),
                Source: TranslationTriggerSource.SelectionPopup,
                ScreenPoint: cursor,
                SourceWindowTitle: windowTitle);
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
            var seq = NativeMethods.GetClipboardSequenceNumber();
            if (seq != _lastClipboardSeq)
            {
                _lastClipboardSeq = seq;
                if (IsClipboardMonitoringEnabled && !_suppressClipboardEvents)
                {
                    var text = TryReadClipboardUnicodeText()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text) && text.Length <= 8000 && !string.Equals(text, _lastClipboardText, StringComparison.Ordinal))
                    {
                        _lastClipboardText = text;
                        var cursor = GetCursorPosition();
                        var title = GetForegroundWindowTitle();
                        ClipboardTextChanged?.Invoke(this, new CapturedTextEvent(
                            Text: text,
                            Source: TranslationTriggerSource.ClipboardMonitor,
                            ScreenPoint: cursor,
                            SourceWindowTitle: title));
                    }
                }
            }

            if (IsMouseSelectionMonitoringEnabled && Volatile.Read(ref _selectionCaptureInFlight) == 0)
            {
                const int VkLButton = 0x01;
                var isDown = (NativeMethods.GetAsyncKeyState(VkLButton) & 0x8000) != 0;
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
                        var fg = NativeMethods.GetForegroundWindow();
                        if (fg != 0 && NativeMethods.GetWindowThreadProcessId(fg, out var pid) != 0 && pid != (uint)Environment.ProcessId)
                        {
                            _ = Task.Run(async () =>
                            {
                                var captured = await TryCaptureSelectedTextAsync().ConfigureAwait(false);
                                if (captured is not null && !string.IsNullOrWhiteSpace(captured.Text))
                                    MouseSelectionCaptured?.Invoke(this, captured);
                            });
                        }
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

    private static void SendCtrlC()
    {
        const ushort VkControl = 0x11;
        const ushort VkC = 0x43;
        const uint KeyeventfKeyup = 0x0002;
        const uint InputKeyboard = 1;

        var inputs = new NativeMethods.Input[]
        {
            new() { Type = InputKeyboard, Data = new() { Keyboard = new() { Vk = VkControl } } },
            new() { Type = InputKeyboard, Data = new() { Keyboard = new() { Vk = VkC } } },
            new() { Type = InputKeyboard, Data = new() { Keyboard = new() { Vk = VkC, Flags = KeyeventfKeyup } } },
            new() { Type = InputKeyboard, Data = new() { Keyboard = new() { Vk = VkControl, Flags = KeyeventfKeyup } } }
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
    }

    public static string? TryReadClipboardUnicodeText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                try
                {
                    if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText))
                        return null;
                    var handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
                    if (handle == 0) return null;
                    var ptr = NativeMethods.GlobalLock(handle);
                    if (ptr == 0) return null;
                    try { return Marshal.PtrToStringUni(ptr); }
                    finally { NativeMethods.GlobalUnlock(handle); }
                }
                finally { NativeMethods.CloseClipboard(); }
            }
            Thread.Sleep(10);
        }
        return null;
    }

    public static void TryWriteClipboardUnicodeText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                try
                {
                    NativeMethods.EmptyClipboard();
                    var bytes = (nuint)((text.Length + 1) * 2);
                    const uint GmemMoveable = 0x0002;
                    var hGlobal = NativeMethods.GlobalAlloc(GmemMoveable, bytes);
                    if (hGlobal == 0) return;
                    var target = NativeMethods.GlobalLock(hGlobal);
                    if (target == 0) { NativeMethods.GlobalFree(hGlobal); return; }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally { NativeMethods.GlobalUnlock(hGlobal); }
                    if (NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, hGlobal) == 0)
                        NativeMethods.GlobalFree(hGlobal);
                    return;
                }
                finally { NativeMethods.CloseClipboard(); }
            }
            Thread.Sleep(10);
        }
    }

    /// <summary>Snapshots every clipboard format (not just text) so <see cref="TryRestoreClipboard"/> can put back
    /// whatever the user actually had on the clipboard, instead of only restoring Unicode text.</summary>
    private static List<(uint Format, byte[] Data)>? TrySnapshotClipboard()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                try
                {
                    var snapshot = new List<(uint, byte[])>();
                    uint format = 0;
                    while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
                    {
                        var handle = NativeMethods.GetClipboardData(format);
                        if (handle == 0) continue;
                        var size = NativeMethods.GlobalSize(handle);
                        if (size == 0 || size > int.MaxValue) continue;
                        var ptr = NativeMethods.GlobalLock(handle);
                        if (ptr == 0) continue;
                        try
                        {
                            var data = new byte[(int)size];
                            Marshal.Copy(ptr, data, 0, data.Length);
                            snapshot.Add((format, data));
                        }
                        finally { NativeMethods.GlobalUnlock(handle); }
                    }
                    return snapshot;
                }
                finally { NativeMethods.CloseClipboard(); }
            }
            Thread.Sleep(10);
        }
        return null;
    }

    private static void TryRestoreClipboard(List<(uint Format, byte[] Data)> snapshot)
    {
        const uint GmemMoveable = 0x0002;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                try
                {
                    NativeMethods.EmptyClipboard();
                    foreach (var (format, data) in snapshot)
                    {
                        var hGlobal = NativeMethods.GlobalAlloc(GmemMoveable, (nuint)data.Length);
                        if (hGlobal == 0) continue;
                        var target = NativeMethods.GlobalLock(hGlobal);
                        if (target == 0) { NativeMethods.GlobalFree(hGlobal); continue; }
                        try { Marshal.Copy(data, 0, target, data.Length); }
                        finally { NativeMethods.GlobalUnlock(hGlobal); }
                        if (NativeMethods.SetClipboardData(format, hGlobal) == 0)
                            NativeMethods.GlobalFree(hGlobal);
                    }
                    return;
                }
                finally { NativeMethods.CloseClipboard(); }
            }
            Thread.Sleep(10);
        }
    }

    private static DisplayFrame CaptureDisplay(DisplayInfo display)
    {
        var width = display.PixelWidth;
        var height = display.PixelHeight;
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        nint screen = 0, memory = 0, bitmap = 0, original = 0;
        try
        {
            screen = NativeMethods.GetDC(0);
            if (screen == 0) throw NativeFailure("GetDC");
            memory = NativeMethods.CreateCompatibleDC(screen);
            if (memory == 0) throw NativeFailure("CreateCompatibleDC");
            var info = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)pixels.Length
                }
            };
            bitmap = NativeMethods.CreateDIBSection(screen, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw NativeFailure("CreateDIBSection");
            original = NativeMethods.SelectObject(memory, bitmap);
            if (original == 0 || original == NativeMethods.InvalidHandle) throw NativeFailure("SelectObject");
            if (!NativeMethods.BitBlt(memory, 0, 0, width, height, screen, checked((int)display.Bounds.X), checked((int)display.Bounds.Y), 0x00CC0020 | 0x40000000))
                throw NativeFailure("BitBlt");
            if (!NativeMethods.GdiFlush()) throw NativeFailure("GdiFlush");
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var alpha = 3; alpha < pixels.Length; alpha += 4) pixels[alpha] = 255;
            return new(display, pixels, stride);
        }
        finally
        {
            if (memory != 0 && original != 0 && original != NativeMethods.InvalidHandle) NativeMethods.SelectObject(memory, original);
            if (bitmap != 0) NativeMethods.DeleteObject(bitmap);
            if (memory != 0) NativeMethods.DeleteDC(memory);
            if (screen != 0) NativeMethods.ReleaseDC(0, screen);
        }
    }

    private static RectD ToRectangle(NativeMethods.Rect rectangle) =>
        new(rectangle.Left, rectangle.Top, (double)rectangle.Right - rectangle.Left, (double)rectangle.Bottom - rectangle.Top);

    private static Win32Exception NativeFailure(string operation) => new(Marshal.GetLastWin32Error(), $"{operation} failed.");
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _monitorTimer.Dispose();
            _hotkeyPump.Dispose();
        }
    }

    internal sealed class DpiAwarenessScope : IDisposable
    {
        private readonly nint _previous;
        internal DpiAwarenessScope()
        {
            _previous = NativeMethods.SetThreadDpiAwarenessContext(new nint(-4));
            if (_previous == 0) _previous = NativeMethods.SetThreadDpiAwarenessContext(new nint(-3));
        }
        public void Dispose()
        {
            if (_previous != 0) NativeMethods.SetThreadDpiAwarenessContext(_previous);
        }
    }
}
