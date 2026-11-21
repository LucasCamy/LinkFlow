namespace LinkFlow.Core;

/// <summary>
/// Avalia a curva de temperatura com suavização (média móvel exponencial) e histerese:
/// sobe imediatamente, mas só desce quando a temperatura cai <c>Hysteresis</c> °C
/// abaixo do ponto em que a velocidade atual foi definida. Evita o "sobe e desce" das ventoinhas.
/// </summary>
public sealed class CurveController
{
    private float? _smoothed;
    private byte? _current;
    private float _tempAtCurrent;

    public float? SmoothedTemp => _smoothed;
    public byte? Current => _current;

    public void Reset()
    {
        _smoothed = null;
        _current = null;
    }

    public byte Evaluate(FanCurveConfig cfg, float temp)
    {
        _smoothed = _smoothed is { } s ? s + (temp - s) * cfg.Smoothing : temp;
        float t = _smoothed.Value;
        byte target = cfg.Evaluate(t);

        if (_current is not { } cur || target > cur)
        {
            _current = target;
            _tempAtCurrent = t;
        }
        else if (target < cur && t <= _tempAtCurrent - cfg.Hysteresis)
        {
            _current = target;
            _tempAtCurrent = t;
        }

        return _current.Value;
    }

    public static float? SelectTemp(CurveSource source, float? cpu, float? gpu, float? coolant)
    {
        cpu = Valid(cpu); gpu = Valid(gpu); coolant = Valid(coolant);
        float? maxCpuGpu = (cpu, gpu) switch
        {
            ({ } c, { } g) => Math.Max(c, g),
            ({ } c, null) => c,
            (null, { } g) => g,
            _ => null
        };

        return source switch
        {
            CurveSource.Cpu => cpu ?? maxCpuGpu ?? coolant,
            CurveSource.Gpu => gpu ?? maxCpuGpu ?? coolant,
            CurveSource.Coolant => coolant ?? maxCpuGpu,
            _ => maxCpuGpu ?? coolant
        };
    }

    private static float? Valid(float? value) => value is > 0 and < 150 && float.IsFinite(value.Value) ? value : null;

    public static CurveTemperatureSelection SelectTemperature(FanCurveConfig cfg, float? cpu, float? gpu, float? coolant)
    {
        cpu = Valid(cpu); gpu = Valid(gpu); coolant = Valid(coolant);
        (float? Value, CurveSource? Source) primary = cfg.Source switch
        {
            CurveSource.Cpu => (cpu, cpu != null ? CurveSource.Cpu : null),
            CurveSource.Gpu => (gpu, gpu != null ? CurveSource.Gpu : null),
            CurveSource.Coolant => (coolant, coolant != null ? CurveSource.Coolant : null),
            _ => cpu != null && gpu != null ? (Math.Max(cpu.Value, gpu.Value), CurveSource.MaxCpuGpu)
                : cpu != null ? (cpu, CurveSource.Cpu) : (gpu, gpu != null ? CurveSource.Gpu : null)
        };
        if (primary.Value != null || cfg.FallbackPolicy == CurveFallbackPolicy.SafeSpeed)
            return new(primary.Value, cfg.Source, primary.Source, false);
        if (cfg.Source != CurveSource.Coolant || cfg.FallbackPolicy == CurveFallbackPolicy.AllowAnyAvailable)
        {
            if (cpu != null || gpu != null)
                return cpu >= gpu || gpu == null ? new(cpu, cfg.Source, CurveSource.Cpu, true) : new(gpu, cfg.Source, CurveSource.Gpu, true);
        }
        if (cfg.FallbackPolicy == CurveFallbackPolicy.AllowAnyAvailable && coolant != null)
            return new(coolant, cfg.Source, CurveSource.Coolant, true);
        return new(null, cfg.Source, null, false);
    }
}
