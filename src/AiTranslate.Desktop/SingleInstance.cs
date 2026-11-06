namespace AiTranslate.Desktop;

/// <summary>Keeps one tray-resident app per user session and lets later launches wake the running one.</summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Name = "AiTranslate.Desktop.SingleInstance";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    public static SingleInstance? TryAcquire()
    {
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, $"{Name}.Activate");
        var mutex = new Mutex(true, Name, out var owned);
        if (owned) return new SingleInstance(mutex, activate);

        activate.Set();
        activate.Dispose();
        mutex.Dispose();
        return null;
    }

    public void ListenForActivation(Action onActivate)
    {
        var thread = new Thread(() =>
        {
            var handles = new[] { _activate, _stop.Token.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0) onActivate();
        })
        { IsBackground = true, Name = "AiTranslate activation listener" };
        thread.Start();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _activate.Dispose();
        _stop.Dispose();
    }
}
