namespace LinkFlow.Core;

/// <summary>Uma medição da calibração: RPM estável numa potência.</summary>
public sealed partial class CalibrationPoint
{
    public byte Percent { get; set; }
    public int Rpm { get; set; }

    public CalibrationPoint() { }
    public CalibrationPoint(byte percent, int rpm) { Percent = percent; Rpm = rpm; }
}

/// <summary>
/// Resultado da calibração de uma ventoinha (guardado em <see cref="FanOverride.Calibration"/>): o RPM medido em cada
/// potência, a potência mínima em que ela gira com segurança e a data. O engine usa <see cref="MinPercent"/> como piso
/// e <see cref="ExpectedRpm"/> para avisar quando a ventoinha passa a girar bem abaixo do que girava (poeira, rolamento).
/// </summary>
public sealed partial class FanCalibration
{
    /// <summary>Folga somada à menor potência em que a ventoinha girou, para não ficar no limite de parar.</summary>
    public const byte SafetyMargin = 5;

    public DateTime Date { get; set; }
    public int MaxRpm { get; set; }

    /// <summary>Menor potência que o engine aplica nesta ventoinha (nunca abaixo de <see cref="FanLimits.MinPercent"/>).</summary>
    public byte MinPercent { get; set; } = FanLimits.MinPercent;

    /// <summary>Potência necessária para ela voltar a girar depois de parada (null = não parou na calibração).</summary>
    public byte? StartPercent { get; set; }

    /// <summary>Medições em ordem crescente de potência.</summary>
    public List<CalibrationPoint> Points { get; set; } = new();

    /// <summary>RPM esperado numa potência, interpolado entre as medições (null sem medições que girem).</summary>
    public int? ExpectedRpm(byte percent)
    {
        var spinning = Points.Where(p => p.Rpm > 0).OrderBy(p => p.Percent).ToList();
        if (spinning.Count == 0) return null;
        if (percent <= spinning[0].Percent) return percent == spinning[0].Percent ? spinning[0].Rpm : null;
        if (percent >= spinning[^1].Percent) return spinning[^1].Rpm;
        for (int i = 1; i < spinning.Count; i++)
        {
            var (a, b) = (spinning[i - 1], spinning[i]);
            if (percent > b.Percent) continue;
            double t = (percent - a.Percent) / (double)(b.Percent - a.Percent);
            return (int)Math.Round(a.Rpm + t * (b.Rpm - a.Rpm));
        }
        return spinning[^1].Rpm;
    }

    /// <summary>
    /// Monta o resultado a partir da varredura descendente e do teste de partida. A potência mínima é a maior entre a
    /// menor potência em que ela ainda girava e a de partida, mais <see cref="SafetyMargin"/>; sem nenhuma parada, fica
    /// no mínimo do LinkFlow.
    /// </summary>
    public static FanCalibration Analyze(IEnumerable<CalibrationPoint> sweep, byte? startPercent, DateTime date)
    {
        var points = sweep.GroupBy(p => p.Percent).Select(g => g.Last()).OrderBy(p => p.Percent).ToList();
        var spinning = points.Where(p => p.Rpm > 0).ToList();
        bool stalled = points.Any(p => p.Rpm == 0) || startPercent != null;
        int min = FanLimits.MinPercent;
        if (stalled)
        {
            // Acima da última parada: a menor potência em que girou sem interrupção dali para cima
            byte highestStall = points.Where(p => p.Rpm == 0).Select(p => p.Percent).DefaultIfEmpty((byte)0).Max();
            byte lowestSafe = spinning.Where(p => p.Percent > highestStall).Select(p => p.Percent).DefaultIfEmpty((byte)100).Min();
            min = Math.Max(lowestSafe, startPercent ?? 0) + SafetyMargin;
        }
        return new FanCalibration
        {
            Date = date,
            MaxRpm = spinning.Count > 0 ? spinning.Max(p => p.Rpm) : 0,
            MinPercent = (byte)Math.Clamp(min, FanLimits.MinPercent, 100),
            StartPercent = startPercent,
            Points = points
        };
    }

    public void Normalize()
    {
        Points ??= new();
        Points.RemoveAll(p => p == null);
        foreach (var p in Points)
        {
            p.Percent = Math.Clamp(p.Percent, (byte)0, (byte)100);
            p.Rpm = Math.Max(0, p.Rpm);
        }
        Points = Points.GroupBy(p => p.Percent).Select(g => g.Last()).OrderBy(p => p.Percent).ToList();
        MinPercent = Math.Clamp(MinPercent, (byte)FanLimits.MinPercent, (byte)100);
        MaxRpm = Math.Max(0, MaxRpm);
    }
}

public enum CalibrationOutcome { None, Running, Completed, Cancelled, Aborted }

/// <summary>Resumo de uma ventoinha calibrada, para a tela final do assistente.</summary>
public sealed class CalibrationFanResult
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MaxRpm { get; set; }
    public byte MinPercent { get; set; }

    /// <summary>Observação (ex.: "parou abaixo de 30%" ou "37% abaixo das outras do mesmo modelo").</summary>
    public string? Note { get; set; }
    public bool Warning { get; set; }
}

/// <summary>Estado da calibração publicado no snapshot (null = nunca rodou desde que o engine iniciou).</summary>
public sealed class CalibrationStatus
{
    /// <summary>Número do pedido (cresce a cada tentativa, inclusive as recusadas): separa o resultado novo de um antigo.</summary>
    public int RunId { get; set; }

    public CalibrationOutcome Outcome { get; set; }
    public bool Running => Outcome == CalibrationOutcome.Running;

    /// <summary>0 a 1.</summary>
    public double Progress { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>Potência sendo medida agora.</summary>
    public byte? Percent { get; set; }
    public List<CalibrationFanResult> Results { get; set; } = new();
}
