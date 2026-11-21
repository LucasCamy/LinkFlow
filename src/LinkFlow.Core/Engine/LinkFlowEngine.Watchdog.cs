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

    /// <summary>Marca que a fila completou mais um ciclo; tentativas que lançam exceção não renovam o progresso.</summary>
    private void Beat(int lane) => Volatile.Write(ref _laneBeats[lane], Stopwatch.GetTimestamp());

    private void InitializeLaneBeats(long now)
    {
        for (int i = 0; i < _laneBeats.Length; i++) Volatile.Write(ref _laneBeats[i], now);
    }

    private long _lastLaneCheck;

    /// <summary>Intervalo entre as verificações do watchdog.</summary>
    private TimeSpan WatchdogPeriod => Min(TimeSpan.FromSeconds(5), _opt.LaneStallTimeout / 4);

    /// <summary>Recomeça a contagem das filas que já começaram (ex.: o processo ficou parado na suspensão).</summary>
    private void ResetLaneBeats(long now)
    {
        for (int i = 0; i < _laneBeats.Length; i++)
            if (Volatile.Read(ref _laneBeats[i]) != 0) Volatile.Write(ref _laneBeats[i], now);
    }

    private void CheckLanes(object? _) => CheckLanes(Stopwatch.GetTimestamp());

    internal void CheckLanes(long now)
    {
        if (Volatile.Read(ref _stopped) == 1 || _cts.IsCancellationRequested) return;

        // O próprio watchdog ficou muito tempo sem rodar: o PC estava suspenso (o relógio do Stopwatch continua
        // contando) ou o processo inteiro ficou parado. As batidas são de antes da pausa e acusariam as filas
        // de travadas logo ao acordar, e o serviço se encerraria. Recomeça a contagem a partir de agora.
        long last = Interlocked.Exchange(ref _lastLaneCheck, now);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) > WatchdogPeriod * 3)
        {
            Log.Info($"Watchdog: {Stopwatch.GetElapsedTime(last, now).TotalSeconds:0} s sem verificar as filas (suspensão ou processo parado); recomeçando a contagem.");
            ResetLaneBeats(now);
            return;
        }

        for (int i = 0; i < WatchedLaneNames.Length; i++)
        {
            long beat = Volatile.Read(ref _laneBeats[i]);
            EngineText? problem = null;
            var age = Stopwatch.GetElapsedTime(beat, now);
            if (_lanes.Count > i && _lanes[i].IsCompleted) problem = EngineText.Of(EngineTexts.WatchdogLaneEnded, WatchedLaneNames[i]);
            else if (beat != 0 && age > _opt.LaneStallTimeout)
                problem = EngineText.Of(EngineTexts.WatchdogLaneStuck, WatchedLaneNames[i], age.TotalSeconds);
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

    private void OnLaneStalled(EngineText problem)
    {
        if (Interlocked.Exchange(ref _stallReported, 1) == 1) return;
        Log.Error($"WATCHDOG: {problem}. Devolvendo o hub ao modo hardware.");

        // Quem travou pode estar segurando o lock: não espera para sempre
        if (Monitor.TryEnter(_stateLock, TimeSpan.FromSeconds(1)))
        {
            try
            {
                RaiseAlert("engine-stalled", AlertSeverity.Critical, EngineText.Of(EngineTexts.EngineStalledTitle),
                    EngineText.Of(EngineTexts.EngineStalled, problem));
            }
            finally { Monitor.Exit(_stateLock); }
        }

        // Modo hardware: o hub segue a curva interna dele em vez de ficar parado na última velocidade
        var release = Task.Run(() => _hub.Disconnect(restoreHardwareMode: true));
        try
        {
            if (!release.Wait(TimeSpan.FromSeconds(2))) Log.Error("WATCHDOG: o hub não liberou a tempo.");
        }
        catch (Exception ex) { Log.Error("WATCHDOG: não foi possível confirmar a devolução ao modo hardware", ex); }

        try { _opt.LaneStalled?.Invoke(problem.ToPortuguese()); }
        catch (Exception ex) { Log.Error("WATCHDOG: falha no tratamento de travamento", ex); }
    }
}
