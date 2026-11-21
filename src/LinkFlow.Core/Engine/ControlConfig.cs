namespace LinkFlow.Core;

public enum FanControlMode { Fixed, Curve }

public enum CurveSource { MaxCpuGpu, Cpu, Gpu, Coolant }

/// <summary>
/// Configuração de controle persistida (compartilhada entre app e serviço).
/// O engine trata cada instância como imutável: alterações são feitas sobre um clone.
/// </summary>
public sealed partial class ControlConfig
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

    /// <summary>Grupos com curva própria (ex.: radiador segue o líquido). Chave = nome do grupo.</summary>
    public Dictionary<string, FanCurveConfig> GroupCurves { get; set; } = new();

    /// <summary>Perfis automáticos por jogo/app e horário silencioso (aplicados pela interface).</summary>
    public AutomationConfig Automation { get; set; } = new();

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
        ConfigurationSchema.ValidateAndMigrate(this);
        GlobalPercent = Math.Clamp(GlobalPercent, (byte)FanLimits.MinPercent, (byte)100);
        GpuFanPercent = Math.Clamp(GpuFanPercent, (byte)30, (byte)100);
        Curve ??= new FanCurveConfig();
        Curve.Normalize();
        Fans ??= new();
        // Itens nulos (JSON editado à mão ou mensagem do pipe malformada) quebrariam quem percorre as coleções
        foreach (var key in Fans.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) Fans.Remove(key);
        foreach (var f in Fans.Values)
        {
            f.Percent = Math.Clamp(f.Percent, (byte)FanLimits.MinPercent, (byte)100);
            f.Calibration?.Normalize();
        }
        Rgb ??= new RgbConfig();
        Rgb.Normalize();
        Lcd ??= new LcdConfig();
        Lcd.Normalize();
        Safety ??= new SafetyConfig();
        Safety.Normalize();
        Profiles ??= new();
        Profiles.RemoveAll(p => p == null);
        GroupCurves ??= new();
        foreach (var key in GroupCurves.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) GroupCurves.Remove(key);
        foreach (var gc in GroupCurves.Values) gc.Normalize();
        Automation ??= new();
        Automation.Normalize();
    }
}

public static class FanLimits
{
    public const int MinPercent = 20;
    public const int PumpMinPercent = 60;
}

public sealed partial class FanOverride
{
    /// <summary>True = segue a potência global/curva. False = usa <see cref="Percent"/>.</summary>
    public bool Linked { get; set; } = true;
    public byte Percent { get; set; } = 55;
    public string? Name { get; set; }
    public string? Group { get; set; }

    /// <summary>Resultado da última calibração desta ventoinha (null = nunca calibrada).</summary>
    public FanCalibration? Calibration { get; set; }
}

public sealed partial class CurvePoint
{
    public float Temp { get; set; }
    public byte Percent { get; set; }

    public CurvePoint() { }
    public CurvePoint(float temp, byte percent) { Temp = temp; Percent = percent; }
}

public sealed partial class FanCurveConfig
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

    /// <summary>
    /// Mesma curva (fonte, pontos, histerese e suavização). A fila de sensores compara a cada ciclo para saber
    /// se a curva foi editada; antes serializava a curva em JSON a cada leitura.
    /// </summary>
    public bool SameAs(FanCurveConfig? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other == null || Source != other.Source || Hysteresis != other.Hysteresis || Smoothing != other.Smoothing ||
            FallbackPolicy != other.FallbackPolicy || FallbackMinPercent != other.FallbackMinPercent) return false;
        if (Points.Count != other.Points.Count) return false;
        for (int i = 0; i < Points.Count; i++)
            if (Points[i].Temp != other.Points[i].Temp || Points[i].Percent != other.Points[i].Percent) return false;
        return true;
    }

    public void Normalize()
    {
        Points ??= DefaultPoints();
        Points.RemoveAll(p => p == null);
        if (Points.Count < 2) Points = DefaultPoints();
        foreach (var p in Points)
        {
            p.Temp = Math.Clamp(p.Temp, 0f, 110f);
            p.Percent = Math.Clamp(p.Percent, (byte)FanLimits.MinPercent, (byte)100);
        }
        Points = Points.OrderBy(p => p.Temp).ToList();
        Hysteresis = Math.Clamp(Hysteresis, 0f, 15f);
        Smoothing = Math.Clamp(Smoothing, 0.05f, 1f);
        if (!Enum.IsDefined(FallbackPolicy)) FallbackPolicy = CurveFallbackPolicy.SafeSpeed;
        FallbackMinPercent = Math.Clamp(FallbackMinPercent, (byte)FanLimits.MinPercent, (byte)100);
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

public sealed partial class RgbConfig
{
    public byte R { get; set; } = 0;
    public byte G { get; set; } = 240;
    public byte B { get; set; } = 255;
    public byte Brightness { get; set; } = 100;

    public bool SyncHub { get; set; } = true;
    public bool SyncLcd { get; set; } = true;
    public bool SyncGpu { get; set; } = true;
    public bool SyncMotherboard { get; set; } = true;
    public bool SyncRam { get; set; } = true;
    public bool SyncPeripherals { get; set; } = true;

    public List<string> RecentColors { get; set; } = new();

    /// <summary>
    /// Correção de gama para LEDs (1 = desligada). A cor da tela é codificada em gama; o LED é linear,
    /// então sem correção os canais "secundários" acendem demais e a cor fica lavada/pastel.
    /// </summary>
    public float LedGamma { get; set; } = 2.0f;

    /// <summary>Brilho extra por dispositivo (0–100), multiplicado pelo brilho geral. Chaves em <see cref="RgbTargets"/>.</summary>
    public Dictionary<string, byte> DeviceBrightness { get; set; } = new();

    /// <summary>Efeito animado (calculado no PC; todos os dispositivos ficam no mesmo compasso).</summary>
    public RgbEffect Effect { get; set; } = RgbEffect.Static;

    /// <summary>Velocidade do efeito, 1 (lento) a 10 (rápido).</summary>
    public byte EffectSpeed { get; set; } = 5;

    /// <summary>Cores do efeito Paleta (#RRGGBB).</summary>
    public List<string> Palette { get; set; } = new() { "#00F0FF", "#7C3AED", "#FF2D95" };

    /// <summary>Espalha o efeito pelas ventoinhas (cada uma num ponto do ciclo) em vez de todas iguais.</summary>
    public bool SpreadOnFans { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsAnimated => Effect != RgbEffect.Static;

    [System.Text.Json.Serialization.JsonIgnore]
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    private bool SameColorProgramAs(RgbConfig o) =>
        R == o.R && G == o.G && B == o.B &&
        Math.Abs(LedGamma - o.LedGamma) < 0.001f &&
        Effect == o.Effect && EffectSpeed == o.EffectSpeed &&
        (Palette ?? new()).SequenceEqual(o.Palette ?? new());

    public bool SameOutputAs(RgbConfig o) => SameColorProgramAs(o) && Brightness == o.Brightness &&
        SyncHub == o.SyncHub && SyncLcd == o.SyncLcd && SyncGpu == o.SyncGpu &&
        SyncMotherboard == o.SyncMotherboard && SyncRam == o.SyncRam && SyncPeripherals == o.SyncPeripherals &&
        SpreadOnFans == o.SpreadOnFans &&
        DeviceBrightness.Count == o.DeviceBrightness.Count && DeviceBrightness.All(kv => o.DeviceBrightness.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>
    /// Compara o programa completo de um destino, sem amostrar um quadro animado. Histórico de cores,
    /// brilho/sync de outros destinos e SyncLcd não alteram esta saída. Ausência de brilho equivale a 100%.
    /// </summary>
    public bool SameOutputFor(RgbConfig other, string target)
    {
        bool Synced(RgbConfig value) => target switch
        {
            RgbTargets.Hub => value.SyncHub,
            RgbTargets.Gpu => value.SyncGpu,
            RgbTargets.Motherboard => value.SyncMotherboard,
            RgbTargets.Ram => value.SyncRam,
            RgbTargets.Peripherals => value.SyncPeripherals,
            _ => throw new ArgumentException("Destino RGB desconhecido.", nameof(target))
        };
        bool enabled = Synced(this), otherEnabled = Synced(other);
        if (enabled != otherEnabled) return false;
        if (!enabled) return true;
        return SameColorProgramAs(other) && (target != RgbTargets.Hub || SpreadOnFans == other.SpreadOnFans) &&
            Brightness * DeviceBrightness.GetValueOrDefault(target, (byte)100) ==
            other.Brightness * other.DeviceBrightness.GetValueOrDefault(target, (byte)100);
    }

    /// <summary>Programa da cor usado pelo compositor do LCD; brilho, gama e destinos LED não participam.</summary>
    public bool SameLcdThemeAs(RgbConfig other)
    {
        if (SyncLcd != other.SyncLcd) return false;
        if (!SyncLcd) return true;
        bool Constant(RgbConfig value) => value.Effect is RgbEffect.Static or RgbEffect.Breathing;
        (byte R, byte G, byte B) Readable(RgbConfig value) => value.R + value.G + value.B < 60
            ? ((byte)0, (byte)240, (byte)255) : (value.R, value.G, value.B);
        // Respirar altera só o nível luminoso, que RgbForLcd/Compose não usam na cor do tema.
        if (Constant(this) && Constant(other)) return Readable(this) == Readable(other);
        if (Effect != other.Effect || EffectSpeed != other.EffectSpeed) return false;
        return Effect != RgbEffect.Palette || (Palette ?? new()).SequenceEqual(other.Palette ?? new());
    }

    /// <summary>Cor final agora (efeito incluído) para um dispositivo de LED.</summary>
    public (byte R, byte G, byte B, byte Brightness) OutputFor(string target) => OutputFor(target, RgbEffects.Now, 0);

    /// <summary>Cor final no instante <paramref name="timeMs"/> (relógio do sistema, igual em todos os processos).</summary>
    public (byte R, byte G, byte B, byte Brightness) OutputFor(string target, long timeMs, double phase)
    {
        var (r, g, b, level) = RgbEffects.ColorAt(this, timeMs, phase);
        return Finish(target, r, g, b, level);
    }

    /// <summary>Gama + brilho geral × brilho do dispositivo × nível do efeito (respirar).</summary>
    public (byte R, byte G, byte B, byte Brightness) Finish(string target, byte r, byte g, byte b, double level = 1)
    {
        float gamma = Math.Clamp(LedGamma, 1f, 3f);
        byte Corr(byte v) => gamma <= 1.001f ? v : (byte)Math.Round(255 * Math.Pow(v / 255.0, gamma));
        int device = DeviceBrightness.TryGetValue(target, out var d) ? d : 100;
        byte brightness = (byte)Math.Clamp(Math.Round(Brightness * device / 100.0 * Math.Clamp(level, 0, 1)), 0, 100);
        return (Corr(r), Corr(g), Corr(b), brightness);
    }

    public void Normalize()
    {
        Brightness = Math.Min(Brightness, (byte)100);
        RecentColors ??= new();
        DeviceBrightness ??= new();
        foreach (var k in DeviceBrightness.Keys.ToList()) DeviceBrightness[k] = Math.Min(DeviceBrightness[k], (byte)100);
        LedGamma = Math.Clamp(LedGamma, 1f, 3f);
        EffectSpeed = Math.Clamp(EffectSpeed, (byte)1, (byte)10);
        if (!Enum.IsDefined(Effect)) Effect = RgbEffect.Static;
        Palette = (Palette ?? new()).Where(h => RgbEffects.TryParseHex(h, out _)).Take(RgbEffects.MaxPalette).ToList();
        if (Palette.Count == 0) Palette = new() { Hex };
    }
}

/// <summary>Cor fixa, respirar, arco-íris, paleta (passa pelas cores escolhidas) e aleatório.</summary>
public enum RgbEffect { Static, Breathing, Rainbow, Palette, Random }

/// <summary>Chaves dos dispositivos RGB (brilho por dispositivo).</summary>
public static class RgbTargets
{
    public const string Hub = "hub", Gpu = "gpu", Motherboard = "mb", Ram = "ram", Peripherals = "peripherals";
}

public enum LcdMode { Metrics, Media }

public enum LcdFit { Cover, Contain }

/// <summary>Informações que podem ser exibidas no LCD.</summary>
public enum LcdMetric
{
    None, CpuTemp, CpuLoad, CpuPower, CpuClock, GpuTemp, GpuHotspot, GpuLoad, GpuPower,
    CoolantTemp, PumpRpm, FanRpm, FanPower, RamUsage, Clock, NetDown, NetUp, DiskUsage, CpuCoreMax
}

public sealed partial class LcdMediaItem
{
    /// <summary>Caminho absoluto (normalmente em %ProgramData%\LinkFlow\media).</summary>
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Tempo na tela antes de passar para o próximo item (apresentação de slides).</summary>
    public int DurationSeconds { get; set; } = 10;

    /// <summary>Enquadramento escolhido no editor (zoom e posição).</summary>
    public LcdCrop Crop { get; set; } = new();
}

public sealed partial class LcdConfig
{
    public bool AutoStream { get; set; } = true;

    /// <summary>
    /// Idioma dos textos desenhados no LCD ("pt-BR", "en"). O serviço não sabe o idioma do usuário: a interface grava
    /// o dela aqui ao conectar. Null = português.
    /// </summary>
    public string? Language { get; set; }
    public LcdMode Mode { get; set; } = LcdMode.Metrics;

    /// <summary>Rotação do quadro (0, 90, 180 ou 270) para acompanhar a posição da bomba.</summary>
    public int Rotation { get; set; }

    // Modo métricas (quadro montado pelo usuário)
    public string Title { get; set; } = "LINKFLOW";
    public LcdMetric MainMetric { get; set; } = LcdMetric.CpuTemp;
    public List<LcdMetric> SecondaryMetrics { get; set; } = new() { LcdMetric.GpuTemp, LcdMetric.CoolantTemp };
    public bool ShowRing { get; set; } = true;
    public string? BackgroundPath { get; set; }
    public byte BackgroundDim { get; set; } = 55;
    public LcdCrop BackgroundCrop { get; set; } = new();

    /// <summary>Modo telemetria com foto de fundo: false esconde os números (só a imagem/GIF).</summary>
    public bool ShowTelemetry { get; set; } = true;

    // Modo imagem/GIF (lista com apresentação de slides)
    public List<LcdMediaItem> Media { get; set; } = new();
    public LcdFit Fit { get; set; } = LcdFit.Cover;
    public bool MediaOverlay { get; set; }
    public LcdMetric OverlayMetric { get; set; } = LcdMetric.CpuTemp;

    public const int MaxSecondary = 4;

    public void Normalize()
    {
        Rotation = ((Rotation % 360) + 360) % 360 / 90 * 90;
        Title ??= string.Empty;
        if (Title.Length > 24) Title = Title[..24];
        SecondaryMetrics ??= new();
        SecondaryMetrics = SecondaryMetrics.Where(m => m != LcdMetric.None).Distinct().Take(MaxSecondary).ToList();
        Media ??= new();
        Media.RemoveAll(m => string.IsNullOrWhiteSpace(m.Path));
        foreach (var m in Media) m.DurationSeconds = Math.Clamp(m.DurationSeconds, 2, 3600);
        BackgroundDim = Math.Min(BackgroundDim, (byte)95);
        BackgroundCrop ??= new();
        BackgroundCrop.Normalize();
        foreach (var m in Media) { m.Crop ??= new(); m.Crop.Normalize(); }
    }
}

public sealed partial class SafetyConfig
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

    /// <summary>
    /// Limites aceitos pelo engine, iguais aos da tela de Configurações. A configuração pode chegar pelo pipe
    /// de qualquer processo da sessão: "crítico = 500 °C" ou "bomba mínima = 0 RPM" desligariam a proteção
    /// sem o usuário ver. Desligar o modo de segurança continua possível, mas de forma explícita.
    /// </summary>
    public void Normalize()
    {
        CpuCritical = Clamp(CpuCritical, 70, 105, 95);
        CpuWarn = Math.Min(Clamp(CpuWarn, 60, 100, 90), CpuCritical);
        GpuCritical = Clamp(GpuCritical, 70, 100, 88);
        GpuWarn = Math.Min(Clamp(GpuWarn, 60, 95, 83), GpuCritical);
        CoolantCritical = Clamp(CoolantCritical, 40, 60, 50);
        CoolantWarn = Math.Min(Clamp(CoolantWarn, 35, 55, 45), CoolantCritical);
        PumpMinRpm = Math.Clamp(PumpMinRpm, 300, 2000);
    }

    private static float Clamp(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public sealed partial class FanProfile
{
    public string Name { get; set; } = "Perfil";
    public FanControlMode Mode { get; set; }
    public byte GlobalPercent { get; set; } = 55;
    public FanCurveConfig Curve { get; set; } = new();
    public bool GpuFanAuto { get; set; } = true;
    public byte GpuFanPercent { get; set; } = 50;

    /// <summary>Estado Linked/Percent por ventoinha no momento em que o perfil foi salvo.</summary>
    public Dictionary<string, FanOverride> Fans { get; set; } = new();

    /// <summary>Curvas por grupo no momento em que o perfil foi salvo (null = perfil antigo, mantém as atuais).</summary>
    public Dictionary<string, FanCurveConfig>? GroupCurves { get; set; }

    public static FanProfile Capture(string name, ControlConfig c) => new()
    {
        Name = name,
        Mode = c.Mode,
        GlobalPercent = c.GlobalPercent,
        Curve = c.Curve.Clone(),
        GpuFanAuto = c.GpuFanAuto,
        GpuFanPercent = c.GpuFanPercent,
        Fans = c.Fans.ToDictionary(kv => kv.Key, kv => new FanOverride { Linked = kv.Value.Linked, Percent = kv.Value.Percent }),
        GroupCurves = ConfigCopy.Dictionary(c.GroupCurves, g => g.Clone())
    };

    public void ApplyTo(ControlConfig c)
    {
        c.Mode = Mode;
        c.GlobalPercent = GlobalPercent;
        c.Curve = Curve.Clone();
        c.GpuFanAuto = GpuFanAuto;
        c.GpuFanPercent = GpuFanPercent;
        if (GroupCurves != null) c.GroupCurves = ConfigCopy.Dictionary(GroupCurves, g => g.Clone());
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

/// <summary>Perfil pronto. <see cref="Key"/> é o que fica salvo; nome e descrição saem no idioma da interface.</summary>
public sealed record FanPreset(string Key, string LabelCode, string Icon, string DescriptionCode, Action<ControlConfig> Apply)
{
    public string Label => EngineTexts.Get(LabelCode, EngineTexts.DisplayCulture);
    public string Description => EngineTexts.Get(DescriptionCode, EngineTexts.DisplayCulture);
}

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
        new(Silent, EngineTexts.PresetSilent, Glyph(0xE74F), EngineTexts.PresetSilentDesc, c => Fixed(c, 40, gpuAuto: true, gpu: 50)),
        new(Balanced, EngineTexts.PresetBalanced, Glyph(0xEC49), EngineTexts.PresetBalancedDesc, c => Fixed(c, 55, gpuAuto: true, gpu: 50)),
        new(Performance, EngineTexts.PresetPerformance, Glyph(0xEC4A), EngineTexts.PresetPerformanceDesc, c => Fixed(c, 75, gpuAuto: false, gpu: 65)),
        new(Max, EngineTexts.PresetMax, Glyph(0xE945), EngineTexts.PresetMaxDesc, c => Fixed(c, 100, gpuAuto: false, gpu: 100)),
        new(Auto, EngineTexts.PresetAuto, Glyph(0xE9D9), EngineTexts.PresetAutoDesc, c =>
        {
            c.Mode = FanControlMode.Curve;
            c.GpuFanAuto = true;
            RelinkAll(c);
        }),
    };

    /// <summary>Ícone da fonte Segoe Fluent Icons / MDL2 Assets.</summary>
    private static string Glyph(int codePoint) => char.ConvertFromUtf32(codePoint);

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

/// <summary>Regra: enquanto o programa estiver aberto (em primeiro plano ao começar), usa o perfil.</summary>
public sealed partial class AppRule
{
    /// <summary>Nome do processo sem ".exe" (ex.: "cs2", "Cyberpunk2077").</summary>
    public string Process { get; set; } = string.Empty;
    /// <summary>Chave de perfil pronto (ex.: "Desempenho") ou nome de perfil salvo.</summary>
    public string Profile { get; set; } = FanPresets.Performance;
    public bool Enabled { get; set; } = true;
}

public sealed partial class QuietHours
{
    public bool Enabled { get; set; }
    public int StartHour { get; set; } = 23;
    public int EndHour { get; set; } = 7;
    public string Profile { get; set; } = FanPresets.Silent;

    /// <summary>Hora dentro da janela (aceita janelas que passam da meia-noite).</summary>
    public bool Contains(DateTime now) => StartHour == EndHour ? Enabled
        : StartHour < EndHour ? now.Hour >= StartHour && now.Hour < EndHour
        : now.Hour >= StartHour || now.Hour < EndHour;
}

public sealed partial class AutomationConfig
{
    public List<AppRule> Rules { get; set; } = new();
    public QuietHours Quiet { get; set; } = new();

    public void Normalize()
    {
        Rules ??= new();
        Rules.RemoveAll(r => r == null);
        foreach (var rule in Rules) rule.Priority = Math.Clamp(rule.Priority, 0, 1000);
        foreach (var r in Rules) r.Process = (r.Process ?? string.Empty).Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        Rules.RemoveAll(r => r.Process.Length == 0);
        Quiet ??= new();
        Quiet.StartHour = Math.Clamp(Quiet.StartHour, 0, 23);
        Quiet.EndHour = Math.Clamp(Quiet.EndHour, 0, 23);
    }
}

/// <summary>
/// Enquadramento de uma imagem no LCD: zoom (1 = encaixe escolhido) e deslocamento normalizado
/// (-1..1 = até a borda da imagem), então qualquer valor salvo é sempre válido.
/// </summary>
public sealed partial class LcdCrop
{
    public double Zoom { get; set; } = 1;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }

    public const double MaxZoom = 5;

    public bool IsDefault => Math.Abs(Zoom - 1) < 0.001 && Math.Abs(OffsetX) < 0.001 && Math.Abs(OffsetY) < 0.001;

    public void Normalize()
    {
        Zoom = Math.Clamp(double.IsFinite(Zoom) ? Zoom : 1, 1, MaxZoom);
        OffsetX = Math.Clamp(double.IsFinite(OffsetX) ? OffsetX : 0, -1, 1);
        OffsetY = Math.Clamp(double.IsFinite(OffsetY) ? OffsetY : 0, -1, 1);
    }
}
