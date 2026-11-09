using System.Diagnostics;
using System.Net.NetworkInformation;

namespace LinkFlow.Core;

/// <summary>O que algum cliente está exibindo e precisa ser lido (o resto não é coletado).</summary>
[Flags]
public enum SensorDemand
{
    None = 0,
    /// <summary>Leitura/escrita, atividade e temperatura dos discos (LibreHardwareMonitor, mais caro).</summary>
    DiskIo = 1,
}

public sealed class NetworkState
{
    public string Adapter { get; set; } = string.Empty;
    public double DownBytesPerSec { get; set; }
    public double UpBytesPerSec { get; set; }
    /// <summary>Velocidade do link (bits/s), para exibir em %.</summary>
    public long LinkBitsPerSec { get; set; }
    public long SessionDownBytes { get; set; }
    public long SessionUpBytes { get; set; }
}

public sealed class VolumeState
{
    public string Letter { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double UsedPercent => TotalGb > 0 ? (TotalGb - FreeGb) / TotalGb * 100 : 0;
}

public sealed class DiskIoState
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public float? Temperature { get; set; }
    public float? ActivityPercent { get; set; }
    public float? ReadBytesPerSec { get; set; }
    public float? WriteBytesPerSec { get; set; }
}

/// <summary>
/// Rede e espaço em disco por APIs nativas do .NET (microssegundos por leitura, sem driver).
/// Rede a cada leitura; volumes a cada 15 s.
/// </summary>
public sealed class SystemStats
{
    private readonly Dictionary<string, (long Rx, long Tx)> _last = new();
    private long _lastTimestamp;
    private long _sessionRx, _sessionTx;
    private List<VolumeState> _volumes = new();
    private long _nextVolumes;

    public NetworkState ReadNetwork()
    {
        var state = new NetworkState();
        long now = Stopwatch.GetTimestamp();
        double seconds = _lastTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        _lastTimestamp = now;

        double bestRate = -1;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                long rx = stats.BytesReceived, tx = stats.BytesSent;
                if (_last.TryGetValue(nic.Id, out var prev) && seconds > 0.05)
                {
                    long dRx = Math.Max(0, rx - prev.Rx), dTx = Math.Max(0, tx - prev.Tx);
                    state.DownBytesPerSec += dRx / seconds;
                    state.UpBytesPerSec += dTx / seconds;
                    _sessionRx += dRx;
                    _sessionTx += dTx;
                    double rate = (dRx + dTx) / seconds;
                    // Adaptador principal: o com mais tráfego (ou com gateway, se ninguém trafega)
                    bool hasGateway = nic.GetIPProperties().GatewayAddresses.Count > 0;
                    double score = rate + (hasGateway ? 1 : 0);
                    if (score > bestRate)
                    {
                        bestRate = score;
                        state.Adapter = nic.Name;
                        state.LinkBitsPerSec = nic.Speed;
                    }
                }
                _last[nic.Id] = (rx, tx);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Rede: {ex.Message}");
        }

        state.SessionDownBytes = _sessionRx;
        state.SessionUpBytes = _sessionTx;
        return state;
    }

    public List<VolumeState> ReadVolumes()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextVolumes) return _volumes;
        _nextVolumes = now + 15 * Stopwatch.Frequency;
        var list = new List<VolumeState>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new VolumeState
                {
                    Letter = d.Name.TrimEnd('\\'),
                    Label = d.VolumeLabel,
                    TotalGb = d.TotalSize / 1e9,
                    FreeGb = d.AvailableFreeSpace / 1e9
                });
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Volumes: {ex.Message}");
        }
        return _volumes = list;
    }
}
