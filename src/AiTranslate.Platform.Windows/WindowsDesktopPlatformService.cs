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
/// - Global mouse selection capture via UI Automation, with a clipboard-preserving fallback
/// </summary>
public sealed class WindowsDesktopPlatformService : IDesktopPlatformService
{
    private readonly HotkeyPumpThread _hotkeyPump;
    private readonly Timer _monitorTimer;
    private int _disposed;
    private uint _lastClipboardSeq;
    private string _lastClipboardText = "";
    private volatile bool _suppressClipboardEvents;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly WindowsTextSelection _textSelection = new();
    private volatile bool _monitorSelection;
    private int _gestureVersion;
    private NativeMethods.Point _downPoint;
    private long _downTime;
    private nint _downWindow;
    private bool _multiClick;
    private Action<PointD>? _pointerPressed;
    private nint _localSelectionWindow;

    public event Action<PointD>? PointerPressed
    {
        add { _pointerPressed += value; UpdateMouseHook(); }
        remove { _pointerPressed -= value; UpdateMouseHook(); }
    }

    private void UpdateMouseHook(bool renew = false) => _hotkeyPump.SetMouseHook(
        _monitorSelection || _pointerPressed is not null || Volatile.Read(ref _localSelectionWindow) != 0 ? OnMouse : null, renew);

    public void SetLocalTextSelection(nint window)
    {
        Interlocked.Exchange(ref _localSelectionWindow, window);
        // A translator may have installed its hook after our background capture was enabled.
        UpdateMouseHook(renew: window != 0);
    }

    public string Name => "Windows";

    public bool IsClipboardMonitoringEnabled { get; set; }
    public bool IsMouseSelectionMonitoringEnabled
    {
        get => _monitorSelection;
        set
        {
            if (_monitorSelection == value) return;
            _monitorSelection = value;
            Interlocked.Increment(ref _gestureVersion);
            UpdateMouseHook();
        }
    }

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

    public Task<CapturedTextEvent?> TryCaptureSelectedTextAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return CaptureSelectionAsync(NativeMethods.GetForegroundWindow(), GetCursorPosition(), null, true, cancellationToken);
    }

    private nint OnMouse(int code, nuint message, nint data)
    {
        if (code >= 0 && message is 0x0201 or 0x0202)
        {
            var point = Marshal.PtrToStructure<NativeMethods.Point>(data);
            var window = NativeMethods.GetForegroundWindow();
            var local = Volatile.Read(ref _localSelectionWindow);
            if (message == 0x0201)
            {
                // A new press ends any stale gesture, e.g. if the old window lost mouse capture.
                Interlocked.Exchange(ref _localSelectionWindow, 0);
                _pointerPressed?.Invoke(new(point.X, point.Y));
                Interlocked.Increment(ref _gestureVersion);
                var now = Environment.TickCount64;
                _multiClick = _downWindow == window && now - _downTime <= NativeMethods.GetDoubleClickTime() &&
                    Math.Abs(point.X - _downPoint.X) <= NativeMethods.GetSystemMetrics(36) / 2 &&
                    Math.Abs(point.Y - _downPoint.Y) <= NativeMethods.GetSystemMetrics(37) / 2;
                _downTime = now;
                _downPoint = point;
                _downWindow = window;
            }
            else if (local != 0 && (window == local || NativeMethods.WindowFromPoint(point) == local))
            {
                // Zero delivers the release to Avalonia. Omitting CallNextHookEx for this one
                // event stops downstream gesture observers from harvesting the local selection.
                return 0;
            }
            else if (_monitorSelection && IsExternalWindow(window) && IsExternalWindow(NativeMethods.WindowFromPoint(point)))
            {
                var canCopy = _multiClick || Math.Abs(point.X - _downPoint.X) >= NativeMethods.GetSystemMetrics(68) ||
                    Math.Abs(point.Y - _downPoint.Y) >= NativeMethods.GetSystemMetrics(69) ||
                    (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
                _ = CaptureAfterReleaseAsync(window, new(point.X, point.Y), Volatile.Read(ref _gestureVersion), canCopy);
            }
        }
        return NativeMethods.CallNextHookEx(0, code, message, data);
    }

    private async Task CaptureAfterReleaseAsync(nint window, PointD point, int version, bool canCopy)
    {
        // Low-level hooks run before the target processes the release. Give it time to commit the selection.
        await Task.Delay(60).ConfigureAwait(false);
        var result = await CaptureSelectionAsync(window, point, version, canCopy, CancellationToken.None).ConfigureAwait(false);
        if (result is not null && version == Volatile.Read(ref _gestureVersion) && _monitorSelection && Volatile.Read(ref _disposed) == 0)
            MouseSelectionCaptured?.Invoke(this, result);
    }

    private async Task<CapturedTextEvent?> CaptureSelectionAsync(nint window, PointD point, int? version, bool canCopy, CancellationToken token)
    {
        bool Current() => IsExternalWindow(window) && window == NativeMethods.GetForegroundWindow() &&
            Volatile.Read(ref _disposed) == 0 && (version is null || version == Volatile.Read(ref _gestureVersion));
        await _captureGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                if (!Current()) return null;
                var title = GetForegroundWindowTitle();
                var text = _textSelection.Read(point, version.HasValue);
                if (text is null && canCopy && Current())
                {
                    token.ThrowIfCancellationRequested();
                    text = CopySelectedText(window, Current);
                }
                token.ThrowIfCancellationRequested();
                return string.IsNullOrWhiteSpace(text) || !Current() ? null :
                    new CapturedTextEvent(text.Trim(), TranslationTriggerSource.SelectionPopup, point, title);
            }, token).ConfigureAwait(false);
        }
        finally { _captureGate.Release(); }
    }

    private static bool IsExternalWindow(nint window) => window != 0 &&
        NativeMethods.GetWindowThreadProcessId(window, out var pid) != 0 && pid != (uint)Environment.ProcessId;

    private string? CopySelectedText(nint window, Func<bool> current)
    {
        // Let a selection hotkey or Shift+click finish before injecting an unmodified Ctrl+C.
        static bool ModifierDown() => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0);
        for (var i = 0; i < 20 && ModifierDown() && current(); i++) Thread.Sleep(20);
        if (ModifierDown() || !current()) return null;
        using var snapshot = ClipboardSnapshot.TryTake();
        if (snapshot is null || !current() || snapshot.Sequence != NativeMethods.GetClipboardSequenceNumber()) return null;
        // A real owner is needed for EmptyClipboard followed by SetClipboardData.
        var owner = NativeMethods.CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, -3, 0, 0, 0);
        if (owner == 0) return null;
        _suppressClipboardEvents = true;
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var targetPid);
            if (!current() || snapshot.Sequence != NativeMethods.GetClipboardSequenceNumber() || !SendCtrlC()) return null;
            for (var i = 0; i < 12; i++)
            {
                Thread.Sleep(25);
                if (NativeMethods.GetClipboardSequenceNumber() == snapshot.Sequence) continue;
                if (!ClipboardSnapshot.TryOpen(owner)) continue;
                try
                {
                    // Read and restore under the same lock. Another application's copy always wins.
                    var clipboardOwner = NativeMethods.GetClipboardOwner();
                    if (clipboardOwner == 0 || NativeMethods.GetWindowThreadProcessId(clipboardOwner, out var pid) == 0 ||
                        pid != targetPid) return null;
                    try { return ReadOpenClipboardText(); }
                    finally { snapshot.Restore(); _lastClipboardSeq = NativeMethods.GetClipboardSequenceNumber(); }
                }
                finally { NativeMethods.CloseClipboard(); }
            }
            return null;
        }
        finally
        {
            _suppressClipboardEvents = false;
            NativeMethods.DestroyWindow(owner);
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
                var owner = NativeMethods.GetClipboardOwner();
                if (IsClipboardMonitoringEnabled && !_suppressClipboardEvents && (owner == 0 || IsExternalWindow(owner)))
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

        }
        catch
        {
        }
    }

    private static bool SendCtrlC()
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
        return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) == inputs.Length;
    }

    public static string? TryReadClipboardUnicodeText()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (NativeMethods.OpenClipboard(0))
            {
                try { return ReadOpenClipboardText(); }
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

    private static string? ReadOpenClipboardText()
    {
        if (!NativeMethods.IsClipboardFormatAvailable(NativeMethods.CfUnicodeText)) return null;
        var handle = NativeMethods.GetClipboardData(NativeMethods.CfUnicodeText);
        if (handle == 0) return null;
        var size = NativeMethods.GlobalSize(handle);
        if (size < 2 || size > int.MaxValue) return null;
        var ptr = NativeMethods.GlobalLock(handle);
        if (ptr == 0) return null;
        try
        {
            var text = Marshal.PtrToStringUni(ptr, (int)size / 2);
            var end = text.IndexOf('\0');
            return end < 0 ? text : text[..end];
        }
        finally { NativeMethods.GlobalUnlock(handle); }
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
