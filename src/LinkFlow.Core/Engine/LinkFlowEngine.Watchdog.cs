using System.Diagnostics;
using System.Globalization;

namespace LinkFlow.Core;

/// <summary>Watchdog das filas críticas (hub e sensores).</summary>
public sealed partial class LinkFlowEngine
{
    // ------------------------------------------------------------------ Watchdog das filas críticas

    private const int HubLane = 0, SensorLane = 1;
    private static readonly string[] WatchedLaneNames = { "hub", "sensores" };
    private readonly long[] _laneBeats = new long[2];
    private int _stallReported;

    /// <summary>Marca que a fila completou mais um ciclo. Zero = ainda não começou (ex.: sensores abrindo).</summary>
    private void Beat(int lane) => Volatile.Write(ref _laneBeats[lane], Stopwatch.GetTimestamp());

    private void CheckLanes(object? _)
    {
        if (Volatile.Read(ref _stopped) == 1 || _cts.IsCancellationRequested) return;
        for (int i = 0; i < WatchedLaneNames.Length; i++)
        {
            long beat = Volatile.Read(ref _laneBeats[i]);
            string? problem = null;
            if (_lanes.Count > i && _lanes[i].IsCompleted) problem = $"a fila '{WatchedLaneNames[i]}' encerrou sozinha";
            else if (beat != 0 && Stopwatch.GetElapsedTime(beat) > _opt.LaneStallTimeout)
                problem = $"a fila '{WatchedLaneNames[i]}' está travada há {Stopwatch.GetElapsedTime(beat).TotalSeconds:0} s";
            if (problem != null)
            {
                OnLaneStalled(problem);
                return;
            }
        }

        // A fila voltou a responder (modo local, sem reinício do processo): limpa o aviso
        if (Volatile.Read(ref _stallReported) == 1 && Monitor.TryEnter(_stateLock, TimeSpan.FromSeconds(1)))
        {
            try { ClearAlert("engine-stalled"); }
            finally { Monitor.Exit(_stateLock); }
            Interlocked.Exchange(ref _stallReported, 0);
        }
    }

    private void OnLaneStalled(string problem)
    {
        if (Interlocked.Exchange(ref _stallReported, 1) == 1) return;
        Log.Error($"WATCHDOG: {problem}. Devolvendo o hub ao modo hardware.");

        // Quem travou pode estar segurando o lock: não espera para sempre
        if (Monitor.TryEnter(_stateLock, TimeSpan.FromSeconds(1)))
        {
            try
            {
                RaiseAlert("engine-stalled", AlertSeverity.Critical, "Controle das ventoinhas travado",
                    $"O LinkFlow detectou que {problem}. O hub voltou ao controle próprio (modo hardware).");
            }
            finally { Monitor.Exit(_stateLock); }
        }

        // Modo hardware: o hub segue a curva interna dele em vez de ficar parado na última velocidade
        var release = Task.Run(() => _hub.Disconnect(restoreHardwareMode: true));
        if (!release.Wait(TimeSpan.FromSeconds(2))) Log.Error("WATCHDOG: o hub não liberou a tempo.");

        try { _opt.LaneStalled?.Invoke(problem); }
        catch (Exception ex) { Log.Error("WATCHDOG: falha no tratamento de travamento", ex); }
    }
}
