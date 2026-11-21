using System.Security;

namespace LinkFlow.Core;

/// <summary>
/// Exclusividade do controlador na máquina inteira, inclusive entre SYSTEM e sessões interativas.
/// O mutex pertence a uma thread dedicada para poder ser liberado mesmo depois de awaits em outra thread.
/// Se o processo terminar, o Windows abandona o mutex e o próximo controlador pode recuperá-lo.
/// </summary>
public sealed class HardwareControlLease : IDisposable
{
    public const string DefaultName = @"Global\LinkFlow.Hardware.Control.v1";
    private readonly ManualResetEventSlim _release = new(false);
    private readonly TaskCompletionSource<bool> _acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private HardwareControlLease(string name)
    {
        new Thread(() => Hold(name)) { IsBackground = true, Name = "LinkFlow hardware ownership" }.Start();
    }

    /// <summary>O produto sempre usa a mesma posse global; não há nome configurável pela aplicação.</summary>
    public static HardwareControlLease Acquire() => AcquireNamed(DefaultName);

    /// <summary>Gancho interno para exercitar posse sem sequer abrir o mutex do serviço instalado.</summary>
    internal static HardwareControlLease AcquireForTests(string name)
    {
        const string prefix = @"Local\LinkFlow.Ownership.Test.";
        if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new ArgumentException("A posse de teste exige um nome Local exclusivo com GUID.", nameof(name));
        return AcquireNamed(name);
    }

    private static HardwareControlLease AcquireNamed(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lease = new HardwareControlLease(name);
        try
        {
            if (lease._acquired.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()) return lease;
            throw new InvalidOperationException("Outro controlador LinkFlow já controla o hardware desta máquina. Aguarde o serviço ou encerre o controlador anterior.");
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void Hold(string name)
    {
        bool owned = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, name);
            try { owned = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            _acquired.TrySetResult(owned);
            if (owned) _release.Wait();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            _acquired.TrySetException(new InvalidOperationException("O controle exclusivo do hardware pertence a outro processo ou sessão do Windows.", ex));
        }
        catch (Exception ex) { _acquired.TrySetException(ex); }
        finally
        {
            if (owned) mutex!.ReleaseMutex();
            mutex?.Dispose();
            _ended.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _release.Set();
        if (_ended.Task.Wait(TimeSpan.FromSeconds(5))) _release.Dispose();
    }
}
