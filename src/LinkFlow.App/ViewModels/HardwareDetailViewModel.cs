using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LinkFlow.App.Resources;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>Leitura ao vivo de um sensor no modal de detalhes do hardware.</summary>
public sealed record LiveTile(string Label, string Value, string? Hint = null);

/// <summary>Título, ícone e cor de cada tipo de componente (cartões e modal).</summary>
public static class HardwareVisuals
{
    public static string Title(HardwareKind kind) => kind switch
    {
        HardwareKind.Processor => Strings.Hwi_CardProcessor,
        HardwareKind.Motherboard => Strings.Hwi_CardBoard,
        HardwareKind.Memory => Strings.Hwi_CardMemory,
        HardwareKind.Graphics => Strings.Hwi_CardGraphics,
        HardwareKind.Storage => Strings.Hwi_CardStorage,
        HardwareKind.Network => Strings.Hwi_CardNetwork,
        HardwareKind.Audio => Strings.Hwi_CardAudio,
        HardwareKind.Monitor => Strings.Hwi_CardMonitors,
        HardwareKind.OperatingSystem => Strings.Hwi_CardOs,
        HardwareKind.IcueLink => "iCUE LINK",
        _ => Strings.Hwi_CardPeripherals
    };

    public static string Icon(HardwareKind kind) => kind switch
    {
        HardwareKind.Processor => "",
        HardwareKind.Motherboard => "",
        HardwareKind.Memory => "",
        HardwareKind.Graphics => "",
        HardwareKind.Storage => "",
        HardwareKind.Network => "",
        HardwareKind.Audio => "",
        HardwareKind.Monitor => "",
        HardwareKind.OperatingSystem => "",
        HardwareKind.IcueLink => "",
        _ => ""
    };

    public static string Color(HardwareKind kind) => kind switch
    {
        HardwareKind.Processor => "#38BDF8",
        HardwareKind.Motherboard => "#FBBF24",
        HardwareKind.Memory => "#A78BFA",
        HardwareKind.Graphics => "#34D399",
        HardwareKind.Storage => "#FB923C",
        HardwareKind.Network => "#22D3EE",
        HardwareKind.Audio => "#F472B6",
        HardwareKind.Monitor => "#818CF8",
        HardwareKind.OperatingSystem => "#60A5FA",
        HardwareKind.IcueLink => "#00E5FF",
        _ => "#94A3B8"
    };

    /// <summary>Série do histórico do Painel que acompanha o componente (null = sem sensores no engine).</summary>
    internal static string? SeriesGroup(HardwareKind kind) => kind switch
    {
        HardwareKind.Processor => "cpu",
        HardwareKind.Graphics => "gpu",
        HardwareKind.Memory => "ram",
        HardwareKind.Storage => "disk",
        HardwareKind.Network => "net",
        HardwareKind.IcueLink => "fans",
        _ => null
    };
}

/// <summary>Cartão da aba "Meu PC": resumo de um componente.</summary>
public sealed class HardwareCard
{
    public HardwareCard(HardwareComponent component) => Component = component;

    public HardwareComponent Component { get; }
    public string Title => HardwareVisuals.Title(Component.Kind);
    public string Icon => HardwareVisuals.Icon(Component.Kind);
    public string Color => HardwareVisuals.Color(Component.Kind);
    public string Name => Component.Name;
    public IReadOnlyList<SpecRow> Rows => Component.Summary.Take(HardwareComponent.CardRows).ToArray();
    public string? More => Component.Summary.Count > HardwareComponent.CardRows
        ? string.Format(UiLanguage.Culture, Strings.Hwi_MoreItems, Component.Summary.Count - HardwareComponent.CardRows) : null;
    public string? Note => Component.Note;
    public string OpenName => string.Format(UiLanguage.Culture, Strings.Hwi_OpenDetails, Title);

    /// <summary>Cartões com vários dispositivos (discos, adaptadores, monitores…): nome em cima e especificação embaixo.</summary>
    public bool Stacked => Component.Kind is HardwareKind.Storage or HardwareKind.Network or HardwareKind.Audio or HardwareKind.Monitor
        or HardwareKind.Peripherals;

    public bool Matches(string filter)
    {
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Contains(string? value) => value != null &&
            compare.IndexOf(value, filter, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        return Contains(Title) || Contains(Name) || Component.Summary.Any(r => Contains(r.Label) || Contains(r.Value)) ||
               Component.Sections.Any(s => Contains(s.Title) || s.Rows.Any(r => Contains(r.Label) || Contains(r.Value)));
    }
}

/// <summary>
/// Modal de detalhes de um componente: a ficha completa do inventário e, quando o engine mede aquele componente,
/// a telemetria ao vivo do mesmo snapshot usado pelo Painel (nada é lido de novo).
/// </summary>
public sealed class HardwareDetailViewModel : ObservableObject
{
    private const int ChartSeconds = 300;
    private static CultureInfo Ui => UiLanguage.Culture;

    private readonly MainViewModel _main;
    private string _seriesKey;
    private ICommand? _setSeriesCommand, _closeCommand, _copyCommand;

    public HardwareDetailViewModel(MainViewModel main, HardwareComponent component)
    {
        _main = main;
        Component = component;
        SeriesOptions = HardwareVisuals.SeriesGroup(component.Kind) is { } group ? MainViewModel.SeriesFor(group) : [];
        _seriesKey = SeriesOptions.FirstOrDefault()?.Key ?? string.Empty;
        _main.SnapshotApplied += Refresh;
        Refresh();
    }

    public HardwareComponent Component { get; }
    public HardwareKind Kind => Component.Kind;
    public string Title => HardwareVisuals.Title(Kind);
    public string Subtitle => Component.Name;
    public string Icon => HardwareVisuals.Icon(Kind);
    public string Color => HardwareVisuals.Color(Kind);
    public IReadOnlyList<SpecSection> Sections => Component.Sections;
    public string? Note => Component.Note;

    public bool HasLive => HardwareVisuals.SeriesGroup(Kind) != null;
    public ObservableCollection<LiveTile> LiveTiles { get; } = new();
    public string LiveSource { get; private set; } = string.Empty;

    public IReadOnlyList<DetailSeries> SeriesOptions { get; }
    public bool HasChart => SeriesOptions.Count > 0;
    public string SeriesKey => _seriesKey;
    public DetailSeries? CurrentSeries => SeriesOptions.FirstOrDefault(s => s.Key == _seriesKey);
    public double[]? History { get; private set; }
    public string ChartColor => CurrentSeries?.Color ?? Color;
    public string ChartUnit => CurrentSeries?.Unit ?? string.Empty;
    public double ChartMinRange => CurrentSeries?.MinRange ?? 10;
    public string ChartStats { get; private set; } = string.Empty;

    public ICommand SetSeriesCommand => _setSeriesCommand ??= new RelayCommand(p =>
    {
        if (p is not string key || key == _seriesKey) return;
        _seriesKey = key;
        OnPropertyChanged(nameof(SeriesKey));
        Refresh();
    });

    public ICommand CloseCommand => _closeCommand ??= new RelayCommand(() => _main.CloseHardwareDetail());

    public ICommand CopyCommand => _copyCommand ??= new RelayCommand(() =>
    {
        try
        {
            System.Windows.Clipboard.SetText(Component.ToText(Title));
            _main.ShowToast(Strings.Hwi_Copied, canUndo: false);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Log.Warn($"Não foi possível copiar as especificações: {ex.Message}");
        }
    });

    /// <summary>Para de acompanhar os snapshots (o modal fechou).</summary>
    internal void Detach() => _main.SnapshotApplied -= Refresh;

    private void Refresh()
    {
        if (!HasLive) return;
        var s = _main.Snapshot;
        var tiles = LiveTilesFor(Kind, s, Ui);
        if (!tiles.SequenceEqual(LiveTiles))
        {
            if (tiles.Count != LiveTiles.Count)
            {
                LiveTiles.Clear();
                foreach (var t in tiles) LiveTiles.Add(t);
            }
            else
            {
                for (int i = 0; i < tiles.Count; i++)
                    if (LiveTiles[i] != tiles[i]) LiveTiles[i] = tiles[i];
            }
        }
        LiveSource = LiveSourceFor(Kind, s);

        if (CurrentSeries is { } series && _main.HistoryFor(series.Key, ChartSeconds) is { Length: > 0 } window)
        {
            History = window.Length >= 2 ? window : null;
            string F(double v) => $"{v.ToString(series.Format, Ui)} {series.Unit}".Replace(" °C", "°C");
            ChartStats = string.Format(Ui, Strings.Hwi_ChartStats, F(window.Min()), F(window.Max()), F(window.Average()));
        }
        else
        {
            History = null;
            ChartStats = Strings.Det_NoReadings;
        }
        OnPropertiesChanged(nameof(LiveSource), nameof(History), nameof(ChartStats), nameof(ChartColor), nameof(ChartUnit),
            nameof(ChartMinRange), nameof(CurrentSeries));
    }

    private static string LiveSourceFor(HardwareKind kind, EngineSnapshot s) => kind switch
    {
        HardwareKind.Processor => s.CpuName ?? string.Empty,
        HardwareKind.Graphics => s.GpuName ?? string.Empty,
        HardwareKind.Network => s.Network?.Adapter ?? string.Empty,
        HardwareKind.IcueLink => s.HubConnected ? "Corsair iCUE LINK System Hub" : string.Empty,
        _ => string.Empty
    };

    /// <summary>Leituras atuais do snapshot do engine para o componente (o que não houver sai como "—").</summary>
    internal static IReadOnlyList<LiveTile> LiveTilesFor(HardwareKind kind, EngineSnapshot s, CultureInfo ui)
    {
        string Temp(float? v) => v is { } t ? $"{t.ToString("0.0", ui)} °C" : Fmt.Missing;
        string Pct(float? v) => v is { } p ? $"{p.ToString("0", ui)}%" : Fmt.Missing;
        string Watts(float? v) => v is { } w ? $"{w.ToString("0", ui)} W" : Fmt.Missing;
        string Mhz(float? v) => v is { } m ? HardwareSpecs.Clock(m, ui) : Fmt.Missing;
        string Gb(double? v) => v is { } g ? $"{g.ToString("0.0", ui)} GB" : Fmt.Missing;
        string Rpm(int? v) => v is { } r ? $"{r.ToString("N0", ui)} RPM" : Fmt.Missing;

        var tiles = new List<LiveTile>();
        switch (kind)
        {
            case HardwareKind.Processor:
                tiles.Add(new(Strings.Dash_Temperature, s.CpuSensorAvailable ? Temp(s.CpuTemp) : Fmt.Missing,
                    s.CpuSensorAvailable ? null : Strings.Det_CpuSensorHint));
                tiles.Add(new(Strings.Det_Usage, Pct(s.CpuLoad)));
                tiles.Add(new(Strings.Det_AvgClock, Mhz(s.CpuClockMhz)));
                tiles.Add(new(Strings.Det_PackagePower, Watts(s.CpuPower)));
                if (s.CpuCoreLoads is { Length: > 0 } cores)
                    tiles.Add(new(Strings.Hwi_BusiestCore, Pct(cores.Max()), string.Format(ui, Strings.Det_Cores, cores.Length)));
                break;
            case HardwareKind.Graphics:
                tiles.Add(new(Strings.Dash_Temperature, Temp(s.GpuTemp)));
                tiles.Add(new(Strings.Det_Hotspot, Temp(s.GpuHotspot)));
                tiles.Add(new(Strings.Det_Usage, Pct(s.GpuLoad)));
                tiles.Add(new(Strings.Dash_Power, Watts(s.GpuPower)));
                tiles.Add(new(Strings.Det_CoreClock, Mhz(s.GpuClockMhz)));
                tiles.Add(new(Strings.Det_MemClock, Mhz(s.GpuMemClockMhz)));
                tiles.Add(new(Strings.Det_VideoMemory, Gb(s.GpuVramUsedGb), s.GpuVramUsedGb is { } used && s.GpuVramTotalGb is { } total and > 0
                    ? string.Format(ui, Strings.Det_VramValue, used, total, used / total * 100) : null));
                tiles.Add(new(Strings.Det_GpuFans, Rpm(s.GpuFanRpm1), s.GpuFanPercent is { } fan ? $"{fan.ToString("0", ui)}%" : null));
                break;
            case HardwareKind.Memory:
                tiles.Add(new(Strings.Det_Usage, Pct(s.RamUsage)));
                tiles.Add(new(Strings.Det_Used, Gb(s.RamUsedGb)));
                tiles.Add(new(Strings.Det_Free, s.RamUsedGb is { } u && s.RamTotalGb is { } t ? Gb(t - u) : Fmt.Missing));
                tiles.Add(new(Strings.Hwi_Visible, Gb(s.RamTotalGb)));
                break;
            case HardwareKind.Storage:
                if (s.Disks == null) tiles.Add(new(Strings.Det_PhysicalDisks, Strings.Det_ReadingDisks));
                foreach (var d in s.Disks ?? [])
                    tiles.Add(new(d.Name, d.Temperature is { } dt ? $"{dt.ToString("0", ui)} °C" : d.ActivityPercent is { } a ? $"{a.ToString("0", ui)}%" : Fmt.Missing,
                        string.Format(ui, Strings.Det_DiskRates, SensorCatalog.Rate(d.ReadBytesPerSec ?? 0, ui), SensorCatalog.Rate(d.WriteBytesPerSec ?? 0, ui)) +
                        (d.ActivityPercent is { } act && d.Temperature != null ? $" • {string.Format(ui, Strings.Dash_DiskActivity, act)}" : string.Empty)));
                foreach (var v in s.Volumes)
                    tiles.Add(new($"{v.Letter} {v.Label}".Trim(), $"{v.UsedPercent.ToString("0", ui)}%",
                        $"{string.Format(ui, Strings.Det_VolumeFree, v.FreeGb)} {string.Format(ui, Strings.Det_VolumeOf, v.TotalGb)}"));
                break;
            case HardwareKind.Network:
                var net = s.Network;
                tiles.Add(new(Strings.Dash_Download, net != null ? SensorCatalog.Rate(net.DownBytesPerSec, ui) : Fmt.Missing));
                tiles.Add(new(Strings.Det_Upload, net != null ? SensorCatalog.Rate(net.UpBytesPerSec, ui) : Fmt.Missing));
                tiles.Add(new(Strings.Det_LinkSpeed, net?.LinkBitsPerSec is > 0 and var bits ? HardwareReport.LinkSpeed((ulong)bits) : Fmt.Missing));
                tiles.Add(new(Strings.Det_Session, net != null
                    ? $"↓ {(net.SessionDownBytes / 1e9).ToString("0.00", ui)} GB • ↑ {(net.SessionUpBytes / 1e9).ToString("0.00", ui)} GB" : Fmt.Missing));
                break;
            case HardwareKind.IcueLink:
                tiles.Add(new(Strings.Det_AvgRpm, Rpm(s.AverageFanRpm)));
                tiles.Add(new(Strings.Det_Pump, Rpm(s.PumpRpm)));
                tiles.Add(new(Strings.Det_Coolant, Temp(s.CoolantTemp)));
                tiles.Add(new(Strings.Dash_Power, $"{s.EffectiveGlobalPercent}%"));
                break;
        }
        return tiles;
    }
}
