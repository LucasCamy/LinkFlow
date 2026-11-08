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
}
