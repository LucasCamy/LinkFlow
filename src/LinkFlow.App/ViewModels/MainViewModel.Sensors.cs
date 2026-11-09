using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>Rede e discos no painel; pedido de leitura de discos só enquanto algo exibe.</summary>
public partial class MainViewModel
{
    private readonly Queue<double> _netHist = new(), _diskHist = new();
    private SensorDemand _sentDemand;
    private Func<SensorDemand>? _widgetDemand;

    public double[]? NetHistory { get; private set; }
    public double[]? DiskHistory { get; private set; }

    public string NetDownFormatted => Snapshot.Network is { } n ? SensorCatalog.Rate(n.DownBytesPerSec) : Fmt.Missing;
    public string NetUpFormatted => Snapshot.Network is { } n ? SensorCatalog.Rate(n.UpBytesPerSec) : Fmt.Missing;
    public string NetAdapter => Snapshot.Network is { Adapter.Length: > 0 } n ? n.Adapter : "Sem conexão ativa";

    /// <summary>Volume do sistema (C:) ou o primeiro encontrado.</summary>
    private VolumeState? MainVolume => Snapshot.Volumes.FirstOrDefault(v => v.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                                       ?? Snapshot.Volumes.FirstOrDefault();

    public string DiskMainFormatted => MainVolume is { } v ? $"{v.UsedPercent:0}%" : Fmt.Missing;
    public string DiskMainDetail => MainVolume is { } v
        ? $"{v.Letter} • {v.FreeGb:0} GB livres de {v.TotalGb:0}" + (Snapshot.Volumes.Count > 1 ? $" • +{Snapshot.Volumes.Count - 1} volume(s)" : "")
        : "Nenhum volume";
    public string DiskActivityFormatted => Snapshot.Disks is { Count: > 0 } d
        ? $"Atividade {d.Max(x => x.ActivityPercent ?? 0):0}%"
        : "Atividade: abra o painel para medir";

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
        DiskHistory = Push(_diskHist, s.Disks is { Count: > 0 } d ? d.Max(x => x.ActivityPercent ?? 0) : null);
        OnPropertiesChanged(nameof(NetHistory), nameof(DiskHistory), nameof(NetDownFormatted), nameof(NetUpFormatted), nameof(NetAdapter),
            nameof(DiskMainFormatted), nameof(DiskMainDetail), nameof(DiskActivityFormatted));
    }
}
