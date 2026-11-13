using System.IO;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>Rede e discos no painel; pedido de leitura de discos só enquanto algo exibe.</summary>
public partial class MainViewModel
{
    private readonly Queue<double> _netHist = new(), _diskHist = new();
    private static readonly string? WindowsVolume = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\');
    private SensorDemand _sentDemand;
    private Func<SensorDemand>? _widgetDemand;

    public double[]? NetHistory { get; private set; }
    public double[]? DiskHistory { get; private set; }

    public string NetDownFormatted => Snapshot.Network is { } n ? SensorCatalog.Rate(n.DownBytesPerSec) : Fmt.Missing;
    public string NetUpFormatted => Snapshot.Network is { } n ? SensorCatalog.Rate(n.UpBytesPerSec) : Fmt.Missing;
    public string NetAdapter => Snapshot.Network is { Adapter.Length: > 0 } n ? n.Adapter : "Sem conexão ativa";
    public string NetLinkFormatted => Snapshot.Network?.LinkBitsPerSec switch
    {
        >= 1_000_000_000 and var bits => $"{bits / 1e9:0.#} Gbps",
        > 0 and var bits => $"{bits / 1e6:0} Mbps",
        _ => Fmt.Missing
    };

    private VolumeState? MainVolume => Snapshot.Volumes.FirstOrDefault(v => v.Letter.TrimEnd('\\').Equals(WindowsVolume, StringComparison.OrdinalIgnoreCase))
                                       ?? Snapshot.Volumes.FirstOrDefault();

    public string DiskMainFormatted => MainVolume is { } v ? $"{v.UsedPercent:0}%" : Fmt.Missing;
    public string DiskMainDetail => MainVolume is { } v
        ? $"{v.Letter} · {v.FreeGb:0} GB livres de {v.TotalGb:0} GB"
        : "Nenhum volume";
    public string DiskActivityFormatted => ReadDiskActivity(Snapshot) is { } percent
        ? $"{percent:0}% de atividade"
        : "Atividade indisponível";

    private static float? ReadDiskActivity(EngineSnapshot snapshot) => snapshot.Disks?.Max(disk => disk.ActivityPercent);

    /// <summary>A aba do widget informa o que precisa (ex.: discos escolhidos no widget).</summary>
    public void SetWidgetDemandSource(Func<SensorDemand> source)
    {
        _widgetDemand = source;
        UpdateSensorDemand();
    }

    /// <summary>
    /// Discos (LibreHardwareMonitor) só são lidos com o painel/detalhes/aba do widget abertos
    /// ou se o widget mostra disco. O resto do tempo, custo zero.
    /// </summary>
    public void UpdateSensorDemand()
    {
        var d = SensorDemand.None;
        if (_isViewActive && (_selectedPage == 0 || _selectedPage == WidgetPageIndex)) d |= SensorDemand.DiskIo;
        if (_widgetDemand?.Invoke() is { } w) d |= w;
        if (d == _sentDemand) return;
        _sentDemand = d;
        _engine.SetSensorDemand(d);
    }

    public const int WidgetPageIndex = 4;

    /// <summary>Último pedido enviado ao engine.</summary>
    public SensorDemand CurrentSensorDemand => _sentDemand;

    private void RecordExtraHistory(EngineSnapshot s)
    {
        static double[]? Push(Queue<double> q, double? v)
        {
            if (v is { } x)
            {
                q.Enqueue(x);
                while (q.Count > HistoryLength) q.Dequeue();
            }
            return q.Count >= 2 ? q.ToArray() : null;
        }
        NetHistory = Push(_netHist, s.Network?.DownBytesPerSec / 1e6);
        DiskHistory = Push(_diskHist, ReadDiskActivity(s));
        OnPropertiesChanged(nameof(NetHistory), nameof(DiskHistory), nameof(NetDownFormatted), nameof(NetUpFormatted), nameof(NetAdapter), nameof(NetLinkFormatted),
            nameof(DiskMainFormatted), nameof(DiskMainDetail), nameof(DiskActivityFormatted));
        RefreshGroupStatus(s);
    }
}
