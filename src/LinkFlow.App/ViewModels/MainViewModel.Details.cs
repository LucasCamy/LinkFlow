using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LinkFlow.Core;
using LinkFlow.App.Services;
using LinkFlow.App.Resources;

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
    private static CultureInfo Ui => UiLanguage.Culture;

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
        var page = _detailKind == "fans" ? 1 : 5;
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
        "cpu" => Strings.Dash_Cpu,
        "gpu" => Strings.Dash_Gpu,
        "coolant" => Strings.Dash_CoolantPump,
        "fans" => Strings.Nav_Fans,
        "ram" => Strings.Dash_Memory,
        "net" => Strings.Dash_Network,
        "disk" => Strings.Det_Disks,
        _ => string.Empty
    };

    public string DetailSubtitle => _detailKind switch
    {
        "cpu" => CpuName,
        "gpu" => GpuName,
        "coolant" => Fans.FirstOrDefault(f => f.IsPump)?.Name ?? Strings.Det_PumpFallback,
        "fans" => string.Format(Strings.Det_FansSubtitle, Fans.Count(f => !f.IsPump && f.HasTachometer), ActivePresetLabel),
        "ram" => RamDetail,
        "net" => NetAdapter,
        "disk" => string.Format(Strings.Det_DisksSubtitle, Snapshot.Volumes.Count, Snapshot.Disks?.Count ?? 0),
        _ => string.Empty
    };

    public string DetailIcon => _detailKind switch
    {
        "cpu" => "",
        "gpu" => "",
        "coolant" => "",
        "fans" => "",
        "ram" => "",
        "net" => "",
        "disk" => "",
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
    public string DetailListTitle => _detailKind == "disk" ? Strings.Det_ListDisks : Strings.Det_ListDevices;
    public string DetailActionLabel => _detailKind == "fans" ? Strings.Dash_OpenFansPage : Strings.Det_AdjustLimits;
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

    internal static IReadOnlyList<DetailSeries> SeriesFor(string? kind) => kind switch
    {
        "cpu" => new DetailSeries[]
        {
            new("cpu.temp", Strings.Dash_Temperature, "°C", "#38BDF8", 6),
            new("cpu.load", Strings.Det_Usage, "%", "#818CF8", 10, "0"),
            new("cpu.power", Strings.Dash_Power, "W", "#F59E0B", 10, "0"),
            new("cpu.clock", Strings.Det_Clock, "MHz", "#22D3EE", 200, "0"),
        },
        "gpu" => new DetailSeries[]
        {
            new("gpu.temp", Strings.Dash_Temperature, "°C", "#34D399", 6),
            new("gpu.hotspot", Strings.Det_Hotspot, "°C", "#F97316", 6),
            new("gpu.load", Strings.Det_Usage, "%", "#818CF8", 10, "0"),
            new("gpu.power", Strings.Dash_Power, "W", "#F59E0B", 10, "0"),
            new("gpu.fan", Strings.Det_GpuFans, "RPM", "#A3E635", 200, "0"),
        },
        "coolant" => new DetailSeries[]
        {
            new("coolant.temp", Strings.Det_Coolant, "°C", "#60A5FA", 2),
            new("pump.rpm", Strings.Det_Pump, "RPM", "#38BDF8", 200, "0"),
        },
        "fans" => new DetailSeries[]
        {
            new("fans.rpm", Strings.Det_AvgRpm, "RPM", "#00E5FF", 200, "0"),
            new("fans.power", Strings.Dash_Power, "%", "#FBBF24", 10, "0"),
        },
        "ram" => new DetailSeries[]
        {
            new("ram.usage", Strings.Det_Usage, "%", "#A78BFA", 5, "0"),
            new("ram.used", Strings.Det_Used, "GB", "#C084FC", 1),
        },
        "net" => new DetailSeries[]
        {
            new("net.down", Strings.Dash_Download, "MB/s", "#34D399", 1, "0.00"),
            new("net.up", Strings.Det_Upload, "MB/s", "#F472B6", 1, "0.00"),
        },
        "disk" => new DetailSeries[]
        {
            new("disk.activity", Strings.Det_Activity, "%", "#FB923C", 10, "0"),
            new("disk.read", Strings.Det_Read, "MB/s", "#38BDF8", 5, "0.0"),
            new("disk.write", Strings.Det_Write, "MB/s", "#F59E0B", 5, "0.0"),
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
        Push("disk.activity", ReadDiskActivity(s));
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
            string F(double v) => $"{v.ToString(series.Format, Ui)} {series.Unit}".Replace(" °C", "°C");
            DetailNow = F(window[^1]);
            DetailMin = F(window.Min());
            DetailMax = F(window.Max());
            DetailAvg = F(window.Average());
            DetailSamples = window.Length < _detailRangeSeconds
                ? string.Format(Strings.Det_LastPartial, FormatDuration(window.Length))
                : string.Format(Strings.Det_Last, FormatDuration(_detailRangeSeconds));
        }
        else
        {
            _detailHistory = null;
            DetailNow = DetailMin = DetailMax = DetailAvg = Fmt.Missing;
            DetailSamples = Strings.Det_NoReadings;
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
        string Mhz(float? v) => v is { } x ? (x >= 1000 ? $"{(x / 1000).ToString("0.00", Ui)} GHz" : $"{x:0} MHz") : Fmt.Missing;
        string W(float? v) => v is { } x ? $"{x:0} W" : Fmt.Missing;

        switch (_detailKind)
        {
            case "cpu":
                rows.Add(new(Strings.Det_Model, CpuName));
                rows.Add(new(Strings.Dash_Temperature, CpuTempFormatted, s.CpuSensorAvailable ? null : Strings.Det_CpuSensorHint));
                rows.Add(new(Strings.Det_Usage, CpuLoadFormatted));
                rows.Add(new(Strings.Det_AvgClock, Mhz(s.CpuClockMhz), s.CpuCores is { } n ? string.Format(Strings.Det_Cores, n) : null));
                rows.Add(new(Strings.Det_PackagePower, W(s.CpuPower)));
                rows.Add(new(Strings.Det_AlertCritical, $"{CpuWarn:0}°C / {CpuCritical:0}°C"));
                break;
            case "gpu":
                rows.Add(new(Strings.Det_Model, GpuName));
                rows.Add(new(Strings.Dash_Temperature, GpuTempFormatted));
                rows.Add(new(Strings.Det_Hotspot, GpuHotspotFormatted));
                rows.Add(new(Strings.Det_Usage, GpuLoadFormatted));
                rows.Add(new(Strings.Det_CoreClock, Mhz(s.GpuClockMhz)));
                rows.Add(new(Strings.Det_MemClock, Mhz(s.GpuMemClockMhz)));
                rows.Add(new(Strings.Dash_Power, W(s.GpuPower)));
                rows.Add(new(Strings.Det_VideoMemory, s.GpuVramUsedGb is { } u && s.GpuVramTotalGb is { } t
                    ? string.Format(Strings.Det_VramValue, u, t, u / t * 100) : Fmt.Missing));
                rows.Add(new(Strings.Det_GpuFans, GpuFanModeText, GpuFanRpmFormatted));
                rows.Add(new(Strings.Det_AlertCritical, $"{GpuWarn:0}°C / {GpuCritical:0}°C"));
                break;
            case "coolant":
                rows.Add(new(Strings.Det_CoolantTemp, CoolantTempFormatted));
                rows.Add(new(Strings.Det_Pump, PumpRpmFormatted, s.Fans.FirstOrDefault(f => f.IsPump) is { } p ? string.Format(Strings.Det_PumpPower, p.AppliedPercent) : null));
                rows.Add(new(Strings.Det_CoolantToCpu, s.CoolantTemp is { } c && s.CpuSensorAvailable && s.CpuTemp is { } cpu
                    ? $"{(cpu - c).ToString("0.0", Ui)}°C" : Fmt.Missing, Strings.Det_CoolantToCpuHint));
                rows.Add(new(Strings.Det_AlertCritical, $"{CoolantWarn:0}°C / {CoolantCritical:0}°C"));
                rows.Add(new(Strings.Det_CurveUsesCoolant, CurveSourceValue == CurveSource.Coolant ? Strings.Det_Yes : Strings.Det_No));
                foreach (var f in Fans.Where(f => f.IsPump)) fanRows.Add(FanRow(f));
                break;
            case "fans":
                int stopped = Fans.Count(f => !f.IsPump && f.ShowStoppedWarning);
                rows.Add(new(Strings.Det_Profile, ActivePresetLabel));
                rows.Add(new(Strings.Det_Mode, IsCurveMode ? CurveStatus : string.Format(Strings.Det_FixedPower, GlobalSpeedFormatted)));
                rows.Add(new(Strings.Det_AvgRpm, AverageFanRpmFormatted));
                rows.Add(new(Strings.Det_ManualControl, string.Format(Strings.Det_OfTotal, Fans.Count(f => !f.IsLinked), Fans.Count)));
                rows.Add(new(Strings.Det_Stopped, stopped == 0 ? Strings.Det_NoneStopped : $"{stopped}", stopped > 0 ? Strings.Det_CheckCables : null));
                foreach (var f in Fans.Where(f => !f.IsPump && f.HasTachometer)) fanRows.Add(FanRow(f));
                break;
            case "ram":
                rows.Add(new(Strings.Det_Usage, RamUsageFormatted));
                rows.Add(new(Strings.Det_Used, s.RamUsedGb is { } used ? $"{used.ToString("0.0", Ui)} GB" : Fmt.Missing));
                rows.Add(new(Strings.Det_Total, s.RamTotalGb is { } tot ? $"{tot:0} GB" : Fmt.Missing));
                rows.Add(new(Strings.Det_Free, s.RamUsedGb is { } u2 && s.RamTotalGb is { } t2 ? $"{(t2 - u2).ToString("0.0", Ui)} GB" : Fmt.Missing));
                break;
            case "net":
                var net = s.Network;
                rows.Add(new(Strings.Det_Adapter, NetAdapter));
                rows.Add(new(Strings.Dash_Download, NetDownFormatted));
                rows.Add(new(Strings.Det_Upload, NetUpFormatted));
                rows.Add(new(Strings.Det_LinkSpeed, NetLinkFormatted));
                rows.Add(new(Strings.Det_Session, net != null ? $"↓ {(net.SessionDownBytes / 1e9).ToString("0.00", Ui)} GB • ↑ {(net.SessionUpBytes / 1e9).ToString("0.00", Ui)} GB" : Fmt.Missing));
                break;
            case "disk":
                foreach (var v in s.Volumes)
                    fanRows.Add(new FanDetailRow($"{v.Letter} {v.Label}".Trim(), string.Format(Strings.Det_VolumeFree, v.FreeGb), $"{v.UsedPercent:0}%",
                        v.UsedPercent >= 90 ? "#F43F5E" : v.UsedPercent >= 75 ? "#F59E0B" : "#10B981", string.Format(Strings.Det_VolumeOf, v.TotalGb)));
                foreach (var d in s.Disks ?? new())
                {
                    rows.Add(new(d.Name, d.ActivityPercent is { } a ? string.Format(Strings.Dash_DiskActivity, a) : Fmt.Missing,
                        (d.Temperature is { } dt ? $"{dt:0}°C • " : "") + string.Format(Strings.Det_DiskRates,
                            SensorCatalog.Rate(d.ReadBytesPerSec ?? 0, Ui), SensorCatalog.Rate(d.WriteBytesPerSec ?? 0, Ui))));
                }
                if (s.Disks == null) rows.Add(new(Strings.Det_PhysicalDisks, Strings.Det_ReadingDisks, Strings.Det_DisksOnDemand));
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
        new(f.Name, f.RpmFormatted, f.PowerFormatted, f.StatusColor, f.IsLinked ? Strings.Det_KindGlobal : Strings.Det_KindManual);

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
