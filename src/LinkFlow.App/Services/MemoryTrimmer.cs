using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace LinkFlow.App.Services;

/// <summary>
/// Ao esconder a janela (bandeja), devolve ao Windows a memória que só a interface usava:
/// compacta o heap gerenciado e esvazia o working set. A janela continua pronta para reabrir.
/// </summary>
public static class MemoryTrimmer
{
    private static Timer? _timer;

    /// <summary>Agenda a limpeza para daqui a alguns segundos (cancela se a janela reabrir antes).</summary>
    public static void ScheduleTrim(TimeSpan delay)
    {
        _timer?.Dispose();
        _timer = new Timer(_ => TrimNow(), null, delay, Timeout.InfiniteTimeSpan);
    }

    public static void Cancel()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static void TrimNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var p = Process.GetCurrentProcess();
            EmptyWorkingSet(p.Handle);
        }
        catch { /* melhor esforço */ }
    }

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
