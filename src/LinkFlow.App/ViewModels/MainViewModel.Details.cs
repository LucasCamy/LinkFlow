using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public sealed record DetailRow(string Label, string Value, string? Hint = null);

public sealed record DetailSeries(string Key, string Label, string Unit, string Color, double MinRange, string Format = "0.0")
{
    public override string ToString() => Label;
}

public sealed class FanDetailRow : ObservableObject
{
    public FanDetailRow(string name, string rpm, string power, string color, string kind)
    {
        Name = name; Rpm = rpm; Power = power; Color = color; Kind = kind;
    }
    public string Name { get; }
    public string Rpm { get; }
    public string Power { get; }
    public string Color { get; }
    public string Kind { get; }
}

/// <summary>Painel de detalhes do dashboard: clicar num cartão abre histórico, estatísticas e informações completas.</summary>
public partial class MainViewModel
{
    private const int LongHistoryLength = 600; // 10 minutos a 1 amostra/s
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private readonly Dictionary<string, Queue<double>> _history = new();
    private string? _detailKind;
    private string _detailSeriesKey = string.Empty;
    private int _detailRangeSeconds = 300;
    private double[]? _detailHistory;
    private ICommand? _openDetailCommand, _closeDetailCommand, _setDetailSeriesCommand, _setDetailRangeCommand, _detailActionCommand;

    public ObservableCollection<DetailRow> DetailRows { get; } = new();
    public ObservableCollection<FanDetailRow> DetailFans { get; } = new();

    public ICommand OpenDetailCommand => _openDetailCommand ??= new RelayCommand(p => OpenDetail(p?.ToString()));
    public ICommand CloseDetailCommand => _closeDetailCommand ??= new RelayCommand(() => CloseDetail());
    public ICommand SetDetailSeriesCommand => _setDetailSeriesCommand ??= new RelayCommand(p =>
    {
        if (p is string key) { _detailSeriesKey = key; RefreshDetail(); OnPropertyChanged(nameof(DetailSeriesKey)); }
    });
    public ICommand SetDetailRangeCommand => _setDetailRangeCommand ??= new RelayCommand(p =>
    {
        if (int.TryParse(p?.ToString(), out var s)) { _detailRangeSeconds = s; OnPropertyChanged(nameof(DetailRangeSeconds)); RefreshDetail(); }
    });
    public ICommand DetailActionCommand => _detailActionCommand ??= new RelayCommand(() =>
    {
        var page = _detailKind == "fans" ? 1 : 4;
        CloseDetail();
        SelectedPage = page;
    });

    public bool IsDetailOpen => _detailKind != null;
    public string? DetailKind => _detailKind;
    public string DetailSeriesKey => _detailSeriesKey;
    public int DetailRangeSeconds => _detailRangeSeconds;
    public double[]? DetailHistory => _detailHistory;

    public IReadOnlyList<DetailSeries> DetailSeriesOptions => SeriesFor(_detailKind);
    public DetailSeries? CurrentDetailSeries => DetailSeriesOptions.FirstOrDefault(s => s.Key == _detailSeriesKey) ?? DetailSeriesOptions.FirstOrDefault();

    public string DetailTitle => _detailKind switch
    {
        "cpu" => "Processador",
        "gpu" => "Placa de vídeo",
        "coolant" => "Líquido e bomba",
        "fans" => "Ventoinhas",
        "ram" => "Memória",
        "net" => "Rede",
        "disk" => "Discos",
        _ => string.Empty
    };

    public string DetailSubtitle => _detailKind switch
    {
        "cpu" => CpuName,
        "gpu" => GpuName,
        "coolant" => Fans.FirstOrDefault(f => f.IsPump)?.Name ?? "Bomba do AIO",
        "fans" => $"{Fans.Count(f => !f.IsPump)} ventoinha(s) • perfil {ActivePresetLabel}",
        "ram" => RamDetail,
        "net" => NetAdapter,
        "disk" => $"{Snapshot.Volumes.Count} volume(s) • {Snapshot.Disks?.Count ?? 0} disco(s) físico(s)",
        _ => string.Empty
    };

    public string DetailIcon => _detailKind switch
    {
        "cpu" => "",
        "gpu" => "",
        "coolant" => "",
        "fans" => "",
        "ram" => "",
        "net" => "uE968",
        "disk" => "uEDA2",
        _ => ""
    };

    public string DetailColor => CurrentDetailSeries?.Color ?? "#00E5FF";
    public string DetailUnit => CurrentDetailSeries?.Unit ?? string.Empty;
    public double DetailMinRange => CurrentDetailSeries?.MinRange ?? 10;

    public string DetailNow { get; private set; } = Fmt.Missing;
    public string DetailMin { get; private set; } = Fmt.Missing;
    public string DetailMax { get; private set; } = Fmt.Missing;
    public string DetailAvg { get; private set; } = Fmt.Missing;
    public string DetailSamples { get; private set; } = string.Empty;
    public bool DetailHasFans => _detailKind is "fans" or "coolant" or "disk";
    public string DetailListTitle => _detailKind == "disk" ? "Volumes e discos" : "Dispositivos";
    public string DetailActionLabel => _detailKind == "fans" ? "Abrir Ventoinhas" : "Ajustar limites de alerta";
    public bool DetailHasAction => _detailKind is "cpu" or "gpu" or "coolant" or "fans";

    public void OpenDetail(string? kind)
    {
        if (kind is not ("cpu" or "gpu" or "coolant" or "fans" or "ram" or "net" or "disk")) return;
        _detailKind = kind;
        _detailSeriesKey = SeriesFor(kind)[0].Key;
        RefreshDetail();
        OnPropertiesChanged(nameof(IsDetailOpen), nameof(DetailKind), nameof(DetailTitle), nameof(DetailIcon),
            nameof(DetailSeriesOptions), nameof(DetailSeriesKey), nameof(DetailHasFans), nameof(DetailListTitle), nameof(DetailActionLabel), nameof(DetailHasAction));
    }

    public bool CloseDetail()
    {
        if (_detailKind == null) return false;
        _detailKind = null;
        OnPropertyChanged(nameof(IsDetailOpen));
        return true;
    }

    private static IReadOnlyList<DetailSeries> SeriesFor(string? kind) => kind switch
    {
        "cpu" => new DetailSeries[]
        {
            new("cpu.temp", "Temperatura", "°C", "#38BDF8", 6),
            new("cpu.load", "Uso", "%", "#818CF8", 10, "0"),
            new("cpu.power", "Potência", "W", "#F59E0B", 10, "0"),
            new("cpu.clock", "Clock", "MHz", "#22D3EE", 200, "0"),
        },
        "gpu" => new DetailSeries[]
        {
            new("gpu.temp", "Temperatura", "°C", "#34D399", 6),
            new("gpu.hotspot", "Hotspot", "°C", "#F97316", 6),
            new("gpu.load", "Uso", "%", "#818CF8", 10, "0"),
            new("gpu.power", "Potência", "W", "#F59E0B", 10, "0"),
            new("gpu.fan", "Fans da GPU", "RPM", "#A3E635", 200, "0"),
        },
        "coolant" => new DetailSeries[]
        {
            new("coolant.temp", "Líquido", "°C", "#60A5FA", 2),
            new("pump.rpm", "Bomba", "RPM", "#38BDF8", 200, "0"),
        },
        "fans" => new DetailSeries[]
        {
            new("fans.rpm", "RPM médio", "RPM", "#00E5FF", 200, "0"),
            new("fans.power", "Potência", "%", "#FBBF24", 10, "0"),
        },
        "ram" => new DetailSeries[]
        {
            new("ram.usage", "Uso", "%", "#A78BFA", 5, "0"),
            new("ram.used", "Usada", "GB", "#C084FC", 1),
        },
        "net" => new DetailSeries[]
        {
            new("net.down", "Download", "MB/s", "#34D399", 1, "0.00"),
            new("net.up", "Upload", "MB/s", "#F472B6", 1, "0.00"),
        },
        "disk" => new DetailSeries[]
        {
            new("disk.activity", "Atividade", "%", "#FB923C", 10, "0"),
            new("disk.read", "Leitura", "MB/s", "#38BDF8", 5, "0.0"),
            new("disk.write", "Escrita", "MB/s", "#F59E0B", 5, "0.0"),
        },
        _ => Array.Empty<DetailSeries>()
    };

    private void RecordHistory(EngineSnapshot s)
    {
        void Push(string key, double? v)
        {
            if (v is not { } x || double.IsNaN(x)) return;
            if (!_history.TryGetValue(key, out var q)) _history[key] = q = new Queue<double>(LongHistoryLength + 1);
            q.Enqueue(x);
            while (q.Count > LongHistoryLength) q.Dequeue();
        }

        Push("cpu.temp", s.CpuSensorAvailable ? s.CpuTemp : null);
        Push("cpu.load", s.CpuLoad);
        Push("cpu.power", s.CpuPower);
        Push("cpu.clock", s.CpuClockMhz);
        Push("gpu.temp", s.GpuTemp);
        Push("gpu.hotspot", s.GpuHotspot);
        Push("gpu.load", s.GpuLoad);
        Push("gpu.power", s.GpuPower);
        Push("gpu.fan", s.GpuFanRpm1);
        Push("coolant.temp", s.CoolantTemp);
        Push("pump.rpm", s.PumpRpm);
        Push("fans.rpm", s.AverageFanRpm);
        Push("fans.power", s.EffectiveGlobalPercent);
        Push("ram.usage", s.RamUsage);
        Push("ram.used", s.RamUsedGb);
        Push("net.down", s.Network?.DownBytesPerSec / 1e6);
        Push("net.up", s.Network?.UpBytesPerSec / 1e6);
        Push("disk.activity", s.Disks is { Count: > 0 } da ? da.Max(x => x.ActivityPercent ?? 0) : null);
        Push("disk.read", s.Disks is { Count: > 0 } dr ? dr.Sum(x => x.ReadBytesPerSec ?? 0) / 1e6 : null);
        Push("disk.write", s.Disks is { Count: > 0 } dw ? dw.Sum(x => x.WriteBytesPerSec ?? 0) / 1e6 : null);

        if (_detailKind != null) RefreshDetail();
    }

    private void RefreshDetail()
    {
        if (_detailKind == null) return;
        var series = CurrentDetailSeries;
        if (series != null && _history.TryGetValue(series.Key, out var q) && q.Count > 0)
        {
            var all = q.ToArray();
            var window = all.Length > _detailRangeSeconds ? all[^_detailRangeSeconds..] : all;
            _detailHistory = window.Length >= 2 ? window : null;
            string F(double v) => $"{v.ToString(series.Format, Pt)} {series.Unit}".Replace(" °C", "°C");
            DetailNow = F(window[^1]);
            DetailMin = F(window.Min());
            DetailMax = F(window.Max());
            DetailAvg = F(window.Average());
            DetailSamples = window.Length < _detailRangeSeconds
                ? $"Últimos {FormatDuration(window.Length)} (o histórico começa quando o LinkFlow abre)"
                : $"Últimos {FormatDuration(_detailRangeSeconds)}";
        }
        else
        {
            _detailHistory = null;
            DetailNow = DetailMin = DetailMax = DetailAvg = Fmt.Missing;
            DetailSamples = "Sem leituras deste sensor ainda";
        }

        BuildDetailRows();
        OnPropertiesChanged(nameof(DetailHistory), nameof(DetailNow), nameof(DetailMin), nameof(DetailMax), nameof(DetailAvg),
            nameof(DetailSamples), nameof(DetailColor), nameof(DetailUnit), nameof(DetailMinRange), nameof(DetailSubtitle), nameof(CurrentDetailSeries));
    }

    private static string FormatDuration(int seconds) => seconds < 90 ? $"{seconds} s" : $"{seconds / 60} min";

    private void BuildDetailRows()
    {
        var s = Snapshot;
        var rows = new List<DetailRow>();
        var fanRows = new List<FanDetailRow>();
        string Mhz(float? v) => v is { } x ? (x >= 1000 ? $"{(x / 1000).ToString("0.00", Pt)} GHz" : $"{x:0} MHz") : Fmt.Missing;
        string W(float? v) => v is { } x ? $"{x:0} W" : Fmt.Missing;

        switch (_detailKind)
        {
            case "cpu":
                rows.Add(new("Modelo", CpuName));
                rows.Add(new("Temperatura", CpuTempFormatted, s.CpuSensorAvailable ? null : "Sensor indisponível sem administrador/serviço"));
                rows.Add(new("Uso", CpuLoadFormatted));
                rows.Add(new("Clock médio", Mhz(s.CpuClockMhz), s.CpuCores is { } n ? $"{n} núcleos" : null));
                rows.Add(new("Potência do pacote", W(s.CpuPower)));
                rows.Add(new("Alerta / crítico", $"{CpuWarn:0}°C / {CpuCritical:0}°C"));
                break;
            case "gpu":
                rows.Add(new("Modelo", GpuName));
                rows.Add(new("Temperatura", GpuTempFormatted));
                rows.Add(new("Hotspot", GpuHotspotFormatted));
                rows.Add(new("Uso", GpuLoadFormatted));
                rows.Add(new("Clock do núcleo", Mhz(s.GpuClockMhz)));
                rows.Add(new("Clock da memória", Mhz(s.GpuMemClockMhz)));
                rows.Add(new("Potência", W(s.GpuPower)));
                rows.Add(new("Memória de vídeo", s.GpuVramUsedGb is { } u && s.GpuVramTotalGb is { } t
                    ? $"{u.ToString("0.0", Pt)} de {t:0} GB ({u / t * 100:0}%)" : Fmt.Missing));
                rows.Add(new("Fans da GPU", GpuFanModeText, GpuFanRpmFormatted));
                rows.Add(new("Alerta / crítico", $"{GpuWarn:0}°C / {GpuCritical:0}°C"));
                break;
            case "coolant":
                rows.Add(new("Temperatura do líquido", CoolantTempFormatted));
                rows.Add(new("Bomba", PumpRpmFormatted, s.Fans.FirstOrDefault(f => f.IsPump) is { } p ? $"{p.AppliedPercent}% de potência" : null));
                rows.Add(new("Diferença líquido → CPU", s.CoolantTemp is { } c && s.CpuSensorAvailable && s.CpuTemp is { } cpu
                    ? $"{(cpu - c).ToString("0.0", Pt)}°C" : Fmt.Missing, "Quanto menor, melhor a troca de calor"));
                rows.Add(new("Alerta / crítico", $"{CoolantWarn:0}°C / {CoolantCritical:0}°C"));
                rows.Add(new("Curva usando o líquido", CurveSourceValue == CurveSource.Coolant ? "Sim" : "Não"));
                foreach (var f in Fans.Where(f => f.IsPump)) fanRows.Add(FanRow(f));
                break;
            case "fans":
                int stopped = Fans.Count(f => !f.IsPump && f.ShowStoppedWarning);
                rows.Add(new("Perfil", ActivePresetLabel));
                rows.Add(new("Modo", IsCurveMode ? CurveStatus : $"Potência fixa {GlobalSpeedFormatted}"));
                rows.Add(new("RPM médio", AverageFanRpmFormatted));
                rows.Add(new("Em controle manual", $"{Fans.Count(f => !f.IsLinked)} de {Fans.Count}"));
                rows.Add(new("Paradas", stopped == 0 ? "Nenhuma" : $"{stopped}", stopped > 0 ? "Verifique cabos/conectores" : null));
                foreach (var f in Fans.Where(f => !f.IsPump)) fanRows.Add(FanRow(f));
                break;
            case "ram":
                rows.Add(new("Uso", RamUsageFormatted));
                rows.Add(new("Usada", s.RamUsedGb is { } used ? $"{used.ToString("0.0", Pt)} GB" : Fmt.Missing));
                rows.Add(new("Total", s.RamTotalGb is { } tot ? $"{tot:0} GB" : Fmt.Missing));
                rows.Add(new("Livre", s.RamUsedGb is { } u2 && s.RamTotalGb is { } t2 ? $"{(t2 - u2).ToString("0.0", Pt)} GB" : Fmt.Missing));
                break;
            case "net":
                var net = s.Network;
                rows.Add(new("Adaptador", NetAdapter));
                rows.Add(new("Download", NetDownFormatted));
                rows.Add(new("Upload", NetUpFormatted));
                rows.Add(new("Velocidade do link", net is { LinkBitsPerSec: > 0 } ? (net.LinkBitsPerSec >= 1_000_000_000 ? $"{net.LinkBitsPerSec / 1e9:0.#} Gbps" : $"{net.LinkBitsPerSec / 1e6:0} Mbps") : Fmt.Missing));
                rows.Add(new("Desde que o LinkFlow abriu", net != null ? $"↓ {(net.SessionDownBytes / 1e9).ToString("0.00", Pt)} GB • ↑ {(net.SessionUpBytes / 1e9).ToString("0.00", Pt)} GB" : Fmt.Missing));
                break;
            case "disk":
                foreach (var v in s.Volumes)
                    fanRows.Add(new FanDetailRow($"{v.Letter} {v.Label}".Trim(), $"{v.FreeGb.ToString("0", Pt)} GB livres", $"{v.UsedPercent:0}%",
                        v.UsedPercent >= 90 ? "#F43F5E" : v.UsedPercent >= 75 ? "#F59E0B" : "#10B981", $"de {v.TotalGb.ToString("0", Pt)} GB"));
                foreach (var d in s.Disks ?? new())
                {
                    rows.Add(new(d.Name, d.ActivityPercent is { } a ? $"{a:0}% de atividade" : Fmt.Missing,
                        $"{(d.Temperature is { } dt ? $"{dt:0}°C • " : "")}L {SensorCatalog.Rate(d.ReadBytesPerSec ?? 0)} • E {SensorCatalog.Rate(d.WriteBytesPerSec ?? 0)}"));
                }
                if (s.Disks == null) rows.Add(new("Discos físicos", "Lendo...", "A leitura de discos liga só com o painel aberto (economia)"));
                break;
        }

        Sync(DetailRows, rows);
        if (!fanRows.Select(r => (r.Name, r.Rpm, r.Power)).SequenceEqual(DetailFans.Select(r => (r.Name, r.Rpm, r.Power))))
        {
            DetailFans.Clear();
            foreach (var r in fanRows) DetailFans.Add(r);
        }
    }

    private static FanDetailRow FanRow(FanViewModel f) =>
        new(f.Name, f.RpmFormatted, f.PowerFormatted, f.StatusColor, f.IsLinked ? "Global" : "Manual");

    private static void Sync(ObservableCollection<DetailRow> target, List<DetailRow> rows)
    {
        if (target.SequenceEqual(rows)) return;
        if (target.Count != rows.Count)
        {
            target.Clear();
            foreach (var r in rows) target.Add(r);
            return;
        }
        for (int i = 0; i < rows.Count; i++)
            if (target[i] != rows[i]) target[i] = rows[i];
    }

    // ------------------------------------------------------------------ Memória no dashboard

    private readonly Queue<double> _ramHist = new();
    public double[]? RamHistory { get; private set; }
}
