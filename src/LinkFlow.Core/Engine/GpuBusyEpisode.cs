using System.Diagnostics;

namespace LinkFlow.Core;

/// <summary>
/// Envios de cor recusados pela GPU com o controlador presente. O barramento I²C da placa fica ocupado por instantes
/// (NVAPI -1 em rajadas de 0,5 a 4 s, em horários aleatórios): tenta de novo com espera crescente e registra um
/// episódio por linha de log, não uma linha por envio recusado.
/// </summary>
public sealed class GpuBusyEpisode
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(2);

    private long _start;
    private bool _warned;

    public int Failures { get; private set; }

    /// <summary>Quando tentar de novo (timestamp do <see cref="Stopwatch"/>), ou null sem nada pendente.</summary>
    public long? RetryAt { get; private set; }

    public bool RetryDue(long now) => RetryAt is { } at && now >= at;

    /// <summary>Registra um envio recusado. Devolve o episódio uma única vez, quando ele passa de <paramref name="warnAfter"/>.</summary>
    public (TimeSpan Duration, int Failures)? Failed(long now, TimeSpan warnAfter)
    {
        if (Failures == 0) _start = now;
        Failures++;
        var delay = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, FirstRetry.Ticks << Math.Min(Failures - 1, 4)));
        RetryAt = now + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        var duration = Stopwatch.GetElapsedTime(_start, now);
        if (_warned || duration < warnAfter) return null;
        _warned = true;
        return (duration, Failures);
    }

    /// <summary>Envio aceito: devolve o episódio que terminou (para o log), ou null se não havia nenhum.</summary>
    public (TimeSpan Duration, int Failures)? Recovered(long now)
    {
        if (Failures == 0) return null;
        var episode = (Stopwatch.GetElapsedTime(_start, now), Failures);
        Reset();
        return episode;
    }

    public void Reset()
    {
        Failures = 0;
        RetryAt = null;
        _warned = false;
    }
}
