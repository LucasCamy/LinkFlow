namespace LinkFlow.Core;

public enum FanControlMode { Fixed, Curve }

public enum CurveSource { MaxCpuGpu, Cpu, Gpu, Coolant }

/// <summary>
/// Configuração de controle persistida (compartilhada entre app e serviço).
/// O engine trata cada instância como imutável: alterações são feitas sobre um clone.
/// </summary>
public sealed class ControlConfig
{
    public int SchemaVersion { get; set; } = 1;

    public FanControlMode Mode { get; set; } = FanControlMode.Fixed;
    public byte GlobalPercent { get; set; } = 55;
    public string ActivePreset { get; set; } = FanPresets.Balanced;
    public FanCurveConfig Curve { get; set; } = new();

    /// <summary>Ajustes por ventoinha. Chave = DeviceId do iCUE LINK (ou "ch{N}").</summary>
    public Dictionary<string, FanOverride> Fans { get; set; } = new();

    public bool GpuFanAuto { get; set; } = true;
    public byte GpuFanPercent { get; set; } = 50;

    public RgbConfig Rgb { get; set; } = new();
    public LcdConfig Lcd { get; set; } = new();
    public SafetyConfig Safety { get; set; } = new();
    public List<FanProfile> Profiles { get; set; } = new();

    public FanOverride GetFan(string key) => Fans.TryGetValue(key, out var f) ? f : new FanOverride();

    public FanOverride EditFan(string key)
    {
        if (!Fans.TryGetValue(key, out var f))
        {
            f = new FanOverride();
            Fans[key] = f;
        }
        return f;
    }

    public void Normalize()
    {
        GlobalPercent = Math.Clamp(GlobalPercent, (byte)FanLimits.MinPercent, (byte)100);
        GpuFanPercent = Math.Clamp(GpuFanPercent, (byte)30, (byte)100);
        Curve ??= new FanCurveConfig();
        Curve.Normalize();
        Fans ??= new();
        foreach (var f in Fans.Values) f.Percent = Math.Clamp(f.Percent, (byte)FanLimits.MinPercent, (byte)100);
        Rgb ??= new RgbConfig();
        Rgb.Brightness = Math.Min(Rgb.Brightness, (byte)100);
        Rgb.RecentColors ??= new();
        Lcd ??= new LcdConfig();
        Safety ??= new SafetyConfig();
        Profiles ??= new();
    }
}

public static class FanLimits
{
    public const int MinPercent = 20;
    public const int PumpMinPercent = 60;
}

public sealed class FanOverride
{
    /// <summary>True = segue a potência global/curva. False = usa <see cref="Percent"/>.</summary>
    public bool Linked { get; set; } = true;
    public byte Percent { get; set; } = 55;
    public string? Name { get; set; }
    public string? Group { get; set; }
}

public sealed class CurvePoint
{
    public float Temp { get; set; }
    public byte Percent { get; set; }

    public CurvePoint() { }
    public CurvePoint(float temp, byte percent) { Temp = temp; Percent = percent; }
}

public sealed class FanCurveConfig
{
    public CurveSource Source { get; set; } = CurveSource.MaxCpuGpu;
    public List<CurvePoint> Points { get; set; } = DefaultPoints();

    /// <summary>Queda mínima de temperatura (°C) antes de reduzir a velocidade.</summary>
    public float Hysteresis { get; set; } = 3f;

    /// <summary>Fator da média móvel exponencial (0..1). Menor = mais suave.</summary>
    public float Smoothing { get; set; } = 0.35f;

    public static List<CurvePoint> DefaultPoints() => new()
    {
        new(40, 30), new(55, 45), new(70, 65), new(80, 85), new(90, 100)
    };

    public void Normalize()
    {
        Points ??= DefaultPoints();
        if (Points.Count < 2) Points = DefaultPoints();
        foreach (var p in Points)
        {
            p.Temp = Math.Clamp(p.Temp, 0f, 110f);
            p.Percent = Math.Clamp(p.Percent, (byte)FanLimits.MinPercent, (byte)100);
        }
        Points = Points.OrderBy(p => p.Temp).ToList();
        Hysteresis = Math.Clamp(Hysteresis, 0f, 15f);
        Smoothing = Math.Clamp(Smoothing, 0.05f, 1f);
    }

    public byte Evaluate(float temp)
    {
        var pts = Points;
        if (pts.Count == 0) return 100;
        if (temp <= pts[0].Temp) return pts[0].Percent;
        for (int i = 1; i < pts.Count; i++)
        {
            if (temp <= pts[i].Temp)
            {
                var a = pts[i - 1];
                var b = pts[i];
                float span = b.Temp - a.Temp;
                if (span <= 0.001f) return b.Percent;
                float ratio = (temp - a.Temp) / span;
                return (byte)Math.Round(a.Percent + (b.Percent - a.Percent) * ratio, MidpointRounding.AwayFromZero);
            }
        }
        return pts[^1].Percent;
    }
}

public sealed class RgbConfig
{
    public byte R { get; set; } = 0;
    public byte G { get; set; } = 240;
    public byte B { get; set; } = 255;
    public byte Brightness { get; set; } = 100;

    public bool SyncHub { get; set; } = true;
    public bool SyncLcd { get; set; } = true;
    public bool SyncGpu { get; set; } = true;
    public bool SyncMotherboard { get; set; } = true;

    public List<string> RecentColors { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public bool SameOutputAs(RgbConfig o) =>
        R == o.R && G == o.G && B == o.B && Brightness == o.Brightness &&
        SyncHub == o.SyncHub && SyncLcd == o.SyncLcd && SyncGpu == o.SyncGpu && SyncMotherboard == o.SyncMotherboard;
}

public sealed class LcdConfig
{
    public bool AutoStream { get; set; } = true;
}

public sealed class SafetyConfig
{
    /// <summary>Força 100% em todas as ventoinhas quando alguma temperatura crítica é atingida.</summary>
    public bool FailSafeEnabled { get; set; } = true;
    public float CpuWarn { get; set; } = 90;
    public float CpuCritical { get; set; } = 95;
    public float GpuWarn { get; set; } = 83;
    public float GpuCritical { get; set; } = 88;
    public float CoolantWarn { get; set; } = 45;
    public float CoolantCritical { get; set; } = 50;
    public bool FanStopAlert { get; set; } = true;
    public int PumpMinRpm { get; set; } = 800;
}

public sealed class FanProfile
{
    public string Name { get; set; } = "Perfil";
    public FanControlMode Mode { get; set; }
    public byte GlobalPercent { get; set; } = 55;
    public FanCurveConfig Curve { get; set; } = new();
    public bool GpuFanAuto { get; set; } = true;
    public byte GpuFanPercent { get; set; } = 50;

    /// <summary>Estado Linked/Percent por ventoinha no momento em que o perfil foi salvo.</summary>
    public Dictionary<string, FanOverride> Fans { get; set; } = new();

    public static FanProfile Capture(string name, ControlConfig c) => new()
    {
        Name = name,
        Mode = c.Mode,
        GlobalPercent = c.GlobalPercent,
        Curve = Json.Clone(c.Curve),
        GpuFanAuto = c.GpuFanAuto,
        GpuFanPercent = c.GpuFanPercent,
        Fans = c.Fans.ToDictionary(kv => kv.Key, kv => new FanOverride { Linked = kv.Value.Linked, Percent = kv.Value.Percent })
    };

    public void ApplyTo(ControlConfig c)
    {
        c.Mode = Mode;
        c.GlobalPercent = GlobalPercent;
        c.Curve = Json.Clone(Curve);
        c.GpuFanAuto = GpuFanAuto;
        c.GpuFanPercent = GpuFanPercent;
        foreach (var (key, f) in c.Fans)
        {
            if (Fans.TryGetValue(key, out var saved))
            {
                f.Linked = saved.Linked;
                f.Percent = saved.Percent;
            }
            else
            {
                f.Linked = true;
            }
        }
        c.ActivePreset = Name;
    }
}

public sealed record FanPreset(string Key, string Label, string Icon, string Description, Action<ControlConfig> Apply);

/// <summary>Perfis prontos. Todos revinculam as ventoinhas à potência global (nomes/grupos são mantidos).</summary>
public static class FanPresets
{
    public const string Silent = "Silencioso";
    public const string Balanced = "Equilibrado";
    public const string Performance = "Desempenho";
    public const string Max = "Máximo";
    public const string Auto = "Automático";

    public static readonly IReadOnlyList<FanPreset> All = new FanPreset[]
    {
        new(Silent, "Silencioso", "", "40% • GPU automática", c => Fixed(c, 40, gpuAuto: true, gpu: 50)),
        new(Balanced, "Equilibrado", "", "55% • GPU automática", c => Fixed(c, 55, gpuAuto: true, gpu: 50)),
        new(Performance, "Desempenho", "", "75% • GPU 65%", c => Fixed(c, 75, gpuAuto: false, gpu: 65)),
        new(Max, "Máximo", "", "100% • GPU 100%", c => Fixed(c, 100, gpuAuto: false, gpu: 100)),
        new(Auto, "Curva inteligente", "", "Segue a temperatura", c =>
        {
            c.Mode = FanControlMode.Curve;
            c.GpuFanAuto = true;
            RelinkAll(c);
        }),
    };

    public static FanPreset? Find(string key) => All.FirstOrDefault(p => p.Key == key);

    public static void Apply(ControlConfig c, string key)
    {
        var preset = Find(key) ?? throw new ArgumentException($"Perfil desconhecido: {key}");
        preset.Apply(c);
        c.ActivePreset = key;
    }

    private static void Fixed(ControlConfig c, byte pct, bool gpuAuto, byte gpu)
    {
        c.Mode = FanControlMode.Fixed;
        c.GlobalPercent = pct;
        c.GpuFanAuto = gpuAuto;
        c.GpuFanPercent = gpu;
        RelinkAll(c);
    }

    private static void RelinkAll(ControlConfig c)
    {
        foreach (var f in c.Fans.Values) f.Linked = true;
    }
}
