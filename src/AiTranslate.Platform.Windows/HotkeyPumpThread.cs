using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using AiTranslate.Core;

namespace AiTranslate.Platform.Windows;

/// <summary>Supports multiple simultaneous global hotkeys on a dedicated Win32 message queue thread.</summary>
internal sealed class HotkeyPumpThread : IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly Dictionary<int, Action> _callbacks = [];
    private readonly TaskCompletionSource<uint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AutoResetEvent _workSignal = new(false);
    private readonly Thread _thread;
    private bool _disposed;
    private bool _stop;

    internal HotkeyPumpThread()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AiTranslate global hotkeys" };
        _thread.Start();
        try { _ready.Task.GetAwaiter().GetResult(); }
        catch { _thread.Join(); _workSignal.Dispose(); throw; }
    }

    internal bool TryRegister(int id, HotkeyGesture gesture, Action callback, out string? error)
    {
        if (!TryParseKey(gesture.Key, out var key))
        {
            error = "不支持此快捷键。支持字母、数字、F1–F24。";
            return false;
        }
        var command = new Command(() =>
        {
            if (_callbacks.ContainsKey(id))
                NativeMethods.UnregisterHotKey(0, id);

            if (!NativeMethods.RegisterHotKey(0, id, (uint)gesture.Modifiers | NativeMethods.ModNoRepeat, key))
            {
                var code = Marshal.GetLastWin32Error();
                return new(false, code == NativeMethods.ErrorHotkeyAlreadyRegistered
                    ? $"快捷键 {gesture} 已被其他软件占用。"
                    : $"无法注册快捷键：{new Win32Exception(code).Message}");
            }
            _callbacks[id] = callback;
            return new(true, null);
        });
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _commands.Enqueue(command);
            _workSignal.Set();
        }
        var res = command.Completion.Task.GetAwaiter().GetResult();
        error = res.Error;
        return res.Success;
    }

    internal void Unregister(int id)
    {
        var command = new Command(() =>
        {
            if (_callbacks.Remove(id))
                NativeMethods.UnregisterHotKey(0, id);
            return new(true, null);
        });
        lock (_gate)
        {
            if (_disposed) return;
            _commands.Enqueue(command);
            _workSignal.Set();
        }
        command.Completion.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        try
        {
            NativeMethods.PeekMessage(out _, 0, 0, 0, 0);
            _ready.TrySetResult(NativeMethods.GetCurrentThreadId());
            nint[] handles = [_workSignal.SafeWaitHandle.DangerousGetHandle()];
            while (!_stop)
            {
                NativeMethods.MsgWaitForMultipleObjectsEx(1, handles, uint.MaxValue, 0x04FF, 4);
                while (_commands.TryDequeue(out var command))
                {
                    command.Completion.TrySetResult(command.Execute());
                    if (_stop) break;
                }
                if (_stop) break;
                while (NativeMethods.PeekMessage(out var message, 0, 0, 0, 1))
                {
                    if (message.Id == 0x0012) { _stop = true; break; }
                    if (message.Id == NativeMethods.WmHotkey && _callbacks.TryGetValue((int)message.WParam, out var cb))
                    {
                        _ = Task.Run(() =>
                        {
                            try { cb(); }
                            catch (Exception ex) { Debug.WriteLine($"Hotkey error: {ex}"); }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _ready.TrySetException(ex);
        }
        finally
        {
            foreach (var id in _callbacks.Keys)
                NativeMethods.UnregisterHotKey(0, id);
            _callbacks.Clear();
            lock (_gate)
            {
                while (_commands.TryDequeue(out var cmd))
                    cmd.Completion.TrySetResult(new(false, "Stopped"));
                _exited.TrySetResult();
            }
        }
    }

    private static bool TryParseKey(string? name, out uint key)
    {
        key = 0;
        var value = name?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length == 1 && (value[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        {
            key = value[0];
            return true;
        }
        if (value[0] == 'F' && int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 24)
        {
            key = (uint)(0x70 + number - 1);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_exited.Task.IsCompleted)
            {
                _commands.Enqueue(new Command(() => { _stop = true; return new(true, null); }));
                _workSignal.Set();
            }
        }
        if (Thread.CurrentThread != _thread) _thread.Join();
        _workSignal.Dispose();
    }

    private readonly record struct Result(bool Success, string? Error);
    private sealed class Command(Func<Result> execute)
    {
        internal TaskCompletionSource<Result> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Result Execute() => execute();
    }
}
