using System.Globalization;

namespace LinkFlow.Core;

/// <summary>Como exibir uma informação que tem as duas formas (ex.: RAM em % ou em GB).</summary>
public enum SensorDisplay { Value, Percent }

/// <summary>Valor pronto para a tela.</summary>
public readonly record struct SensorReading(string Text, double? Ratio, bool Available)
{
    public static readonly SensorReading Missing = new("--", null, false);
}

/// <summary>Uma informação exibível (widget, LCD, painel).</summary>
public sealed record SensorDef(
    string Id,
    string Group,
    string Label,
    string ShortLabel,
    string Color,
    bool HasPercent,
    bool HasValue,
    Func<EngineSnapshot, SensorDisplay, SensorReading> Read,
    SensorDemand Demand = SensorDemand.None)
{
    public override string ToString() => Label;
}

/// <summary>
/// Catálogo de tudo que o LinkFlow sabe ler. Itens dinâmicos (cada ventoinha, cada volume, cada disco)
/// aparecem conforme o hardware encontrado.
/// </summary>
public static class SensorCatalog
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public const string Cpu = "Processador", Gpu = "Placa de vídeo", Cooling = "Refrigeração", Memory = "Memória",
        Network = "Rede", Storage = "Discos", System = "Sistema";

    private static string N(double v, string fmt = "0") => v.ToString(fmt, Pt);
    private static SensorReading Pct(double? v) => v is { } x ? new($"{N(x)}%", x / 100, true) : SensorReading.Missing;
    private static SensorReading Temp(double? v, double max = 100, string fmt = "0") =>
        v is { } x ? new($"{N(x, fmt)}°C", Math.Clamp(x / max, 0, 1), true) : SensorReading.Missing;
    private static SensorReading Val(double? v, string unit, double max, string fmt = "#,0") =>
        v is { } x ? new($"{N(x, fmt)} {unit}", Math.Clamp(x / max, 0, 1), true) : SensorReading.Missing;

    /// <summary>Bytes/s em texto curto: KB/s, MB/s.</summary>
    public static string Rate(double bytesPerSec) => bytesPerSec switch
    {
        >= 1e9 => $"{N(bytesPerSec / 1e9, "0.0")} GB/s",
        >= 1e6 => $"{N(bytesPerSec / 1e6, bytesPerSec >= 1e7 ? "0" : "0.0")} MB/s",
        >= 1e3 => $"{N(bytesPerSec / 1e3, "0")} KB/s",
        _ => $"{N(bytesPerSec)} B/s"
    };

    private static SensorReading NetRate(NetworkState? n, Func<NetworkState, double> rate, SensorDisplay d)
    {
        if (n == null) return SensorReading.Missing;
        double r = rate(n);
        double? ratio = n.LinkBitsPerSec > 0 ? Math.Clamp(r * 8 / n.LinkBitsPerSec, 0, 1) : null;
        if (d == SensorDisplay.Percent && ratio is { } p) return new($"{N(p * 100, p < 0.1 ? "0.0" : "0")}%", p, true);
        return new(Rate(r), ratio ?? Math.Clamp(r / 12.5e6, 0, 1), true); // sem velocidade do link: escala de 100 Mbps
    }

    private static readonly SensorDef[] Static =
    {
        new("cpu.temp", Cpu, "Temperatura da CPU", "CPU", "#38BDF8", false, true,
            (s, _) => Temp(s.CpuSensorAvailable ? s.CpuTemp : null)),
        new("cpu.load", Cpu, "Uso da CPU", "CPU %", "#818CF8", true, false, (s, _) => Pct(s.CpuLoad)),
        new("cpu.coremax", Cpu, "Núcleo mais ocupado", "Núcleo", "#A78BFA", true, false,
            (s, _) => Pct(s.CpuCoreLoads is { Length: > 0 } c ? c.Max() : null)),
        new("cpu.clock", Cpu, "Clock da CPU", "Clock", "#22D3EE", false, true,
            (s, _) => s.CpuClockMhz is { } c ? new($"{N(c / 1000, "0.00")} GHz", c / 6000, true) : SensorReading.Missing),
        new("cpu.power", Cpu, "Potência da CPU", "CPU W", "#F59E0B", false, true, (s, _) => Val(s.CpuPower, "W", 200)),

        new("gpu.temp", Gpu, "Temperatura da GPU", "GPU", "#34D399", false, true, (s, _) => Temp(s.GpuTemp)),
        new("gpu.hotspot", Gpu, "Hotspot da GPU", "Hotspot", "#F97316", false, true, (s, _) => Temp(s.GpuHotspot, 110)),
        new("gpu.load", Gpu, "Uso da GPU", "GPU %", "#818CF8", true, false, (s, _) => Pct(s.GpuLoad)),
        new("gpu.vram", Gpu, "Memória de vídeo", "VRAM", "#C084FC", true, true, (s, d) =>
            s.GpuVramUsedGb is { } u && s.GpuVramTotalGb is { } t && t > 0
                ? d == SensorDisplay.Percent ? new($"{N(u / t * 100)}%", u / t, true) : new($"{N(u, "0.0")} GB", u / t, true)
                : SensorReading.Missing),
        new("gpu.clock", Gpu, "Clock da GPU", "GPU MHz", "#22D3EE", false, true, (s, _) => Val(s.GpuClockMhz, "MHz", 3200)),
        new("gpu.power", Gpu, "Potência da GPU", "GPU W", "#F59E0B", false, true, (s, _) => Val(s.GpuPower, "W", 450)),
        new("gpu.fan", Gpu, "Fans da GPU", "Fan GPU", "#A3E635", true, true, (s, d) =>
            d == SensorDisplay.Percent ? Pct(s.GpuFanPercent) : Val(s.GpuFanRpm1, "RPM", 3500)),

        new("coolant.temp", Cooling, "Temperatura do líquido", "Líquido", "#60A5FA", false, true, (s, _) => Temp(s.CoolantTemp, 50, "0.0")),
        new("pump.rpm", Cooling, "Bomba", "Bomba", "#38BDF8", true, true, (s, d) =>
            s.Fans.FirstOrDefault(f => f.IsPump) is { } p
                ? d == SensorDisplay.Percent ? Pct(p.AppliedPercent) : Val(p.Rpm, "RPM", 3200)
                : SensorReading.Missing),
        new("fans.avg", Cooling, "Ventoinhas (média)", "Fans", "#00E5FF", true, true, (s, d) =>
            d == SensorDisplay.Percent ? Pct(s.EffectiveGlobalPercent) : Val(s.AverageFanRpm, "RPM", 2400)),

        new("ram.usage", Memory, "Memória RAM", "RAM", "#A78BFA", true, true, (s, d) =>
            d == SensorDisplay.Percent || s.RamUsedGb == null ? Pct(s.RamUsage)
                : new($"{N(s.RamUsedGb.Value, "0.0")} GB", (s.RamUsage ?? 0) / 100, true)),

        new("net.down", Network, "Download", "↓ Rede", "#34D399", true, true, (s, d) => NetRate(s.Network, n => n.DownBytesPerSec, d)),
        new("net.up", Network, "Upload", "↑ Rede", "#F472B6", true, true, (s, d) => NetRate(s.Network, n => n.UpBytesPerSec, d)),
        new("net.session", Network, "Tráfego desde que ligou", "Tráfego", "#94A3B8", false, true, (s, _) =>
            s.Network is { } n ? new($"↓{N((n.SessionDownBytes) / 1e9, "0.0")} ↑{N(n.SessionUpBytes / 1e9, "0.0")} GB", null, true) : SensorReading.Missing),

        new("sys.time", System, "Hora", "Hora", "#E2E8F0", false, true, (_, _) => new(DateTime.Now.ToString("HH:mm", Pt), null, true)),
        new("sys.uptime", System, "Tempo ligado", "Ligado", "#94A3B8", false, true, (_, _) =>
        {
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            return new(up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes:00}m", null, true);
        }),
    };

    /// <summary>Todos os itens disponíveis agora (estáticos + ventoinhas, volumes e discos encontrados).</summary>
    public static IReadOnlyList<SensorDef> All(EngineSnapshot s)
    {
        var list = new List<SensorDef>(Static);
        foreach (var f in s.Fans.Where(f => !f.IsPump))
        {
            string key = f.Key;
            list.Add(new($"fan:{key}", Cooling, f.DefaultName, f.DefaultName.Replace(" Fan", ""), "#00E5FF", true, true, (snap, d) =>
                snap.Fans.FirstOrDefault(x => x.Key == key) is { } x
                    ? d == SensorDisplay.Percent ? Pct(x.AppliedPercent) : Val(x.Rpm, "RPM", 2400)
                    : SensorReading.Missing));
        }
        foreach (var v in s.Volumes)
        {
            string letter = v.Letter;
            list.Add(new($"vol:{letter}", Storage, $"Espaço em {letter}" + (string.IsNullOrEmpty(v.Label) ? "" : $" ({v.Label})"), letter, "#FBBF24", true, true,
                (snap, d) => snap.Volumes.FirstOrDefault(x => x.Letter == letter) is { } x
                    ? d == SensorDisplay.Percent ? new($"{N(x.UsedPercent)}% usado", x.UsedPercent / 100, true)
                        : new($"{N(x.FreeGb, x.FreeGb >= 100 ? "0" : "0.0")} GB livres", x.UsedPercent / 100, true)
                    : SensorReading.Missing));
        }
        foreach (var disk in s.Disks ?? new())
        {
            int i = disk.Index;
            string name = disk.Name;
            DiskIoState? D(EngineSnapshot snap) => snap.Disks?.FirstOrDefault(x => x.Index == i);
            list.Add(new($"disk:{i}:activity", Storage, $"{name} • atividade", $"Disco {i + 1}", "#FB923C", true, false,
                (snap, _) => Pct(D(snap)?.ActivityPercent), SensorDemand.DiskIo));
            list.Add(new($"disk:{i}:rw", Storage, $"{name} • leitura/escrita", $"Disco {i + 1}", "#FB923C", false, true,
                (snap, _) => D(snap) is { } x && x.ReadBytesPerSec is { } r && x.WriteBytesPerSec is { } w
                    ? new($"L {Rate(r)} • E {Rate(w)}", null, true) : SensorReading.Missing, SensorDemand.DiskIo));
            list.Add(new($"disk:{i}:temp", Storage, $"{name} • temperatura", $"Disco {i + 1}", "#F87171", false, true,
                (snap, _) => Temp(D(snap)?.Temperature, 80), SensorDemand.DiskIo));
        }
        return list;
    }

    /// <summary>Itens fixos (sem depender do hardware encontrado).</summary>
    public static IReadOnlyList<SensorDef> StaticItems => Static;

    public static SensorDef? Find(string id, EngineSnapshot s) =>
        Static.FirstOrDefault(d => d.Id == id) ?? All(s).FirstOrDefault(d => d.Id == id);

    /// <summary>Discos só aparecem no catálogo depois de ligados: itens "disk:" pedem a leitura.</summary>
    public static SensorDemand DemandFor(IEnumerable<string> ids) =>
        ids.Any(id => id.StartsWith("disk:", StringComparison.Ordinal)) ? SensorDemand.DiskIo : SensorDemand.None;
}
