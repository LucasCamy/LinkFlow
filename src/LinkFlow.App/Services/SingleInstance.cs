namespace LinkFlow.App.Services;

/// <summary>
/// Garante uma única instância por sessão. Uma segunda execução apenas pede à primeira
/// para mostrar a janela (ex.: clicar no atalho com o app já na bandeja), ou para encerrar (--exit).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly EventWaitHandle _exit;
    private readonly bool _owned;
    private readonly List<RegisteredWaitHandle> _waits = new();

    public SingleInstance(string id)
    {
        _mutex = new Mutex(initiallyOwned: true, $@"Local\{id}.Mutex", out _owned);
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Activate");
        _exit = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{id}.Exit");
    }

    public bool IsFirstInstance => _owned;

    public void SignalActivate() => _activate.Set();
    public void SignalExit() => _exit.Set();

    /// <summary>Espera a instância principal encerrar (após <see cref="SignalExit"/>).</summary>
    public bool WaitForFirstInstanceExit(TimeSpan timeout)
    {
        try
        {
            return _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public void Listen(Action onActivate, Action onExit)
    {
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false));
        _waits.Add(ThreadPool.RegisterWaitForSingleObject(_exit, (_, _) => onExit(), null, Timeout.Infinite, executeOnlyOnce: true));
    }

    public void Dispose()
    {
        foreach (var w in _waits) w.Unregister(null);
        if (_owned)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
        _activate.Dispose();
        _exit.Dispose();
    }
}
