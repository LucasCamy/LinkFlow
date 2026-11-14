using System.Diagnostics;
using System.Globalization;

namespace LinkFlow.Core;

public sealed class EngineOptions
{
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan SubDeviceCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SpeedReassertInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HubRgbMinInterval { get; set; } = TimeSpan.FromMilliseconds(60);
    public TimeSpan AuxRgbMinInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan RamRgbMinInterval { get; set; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan LcdFrameInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SaveDebounce { get; set; } = TimeSpan.FromMilliseconds(800);
    public int HubFailuresBeforeReconnect { get; set; } = 3;
    public int FanStopReadsBeforeAlert { get; set; } = 5;
    public int PumpLowReadsBeforeAlert { get; set; } = 3;
    public TimeSpan FailSafeRecoveryDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Curva sem nenhuma temperatura (CPU, GPU e líquido) por este tempo → modo de segurança.</summary>
    public TimeSpan SensorLossTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Fila do hub ou dos sensores sem completar um ciclo por este tempo → considerada travada.</summary>
    public TimeSpan LaneStallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Chamado (uma vez) quando uma fila crítica trava ou termina sozinha, depois de devolver o hub ao modo
    /// hardware. O serviço encerra o processo para o Windows reiniciá-lo.
    /// </summary>
    public Action<string>? LaneStalled { get; set; }

    /// <summary>
    /// True no serviço (SYSTEM): o LCD só abre imagens de %ProgramData%\LinkFlow\media, porque a configuração
    /// pode vir de qualquer processo da sessão.
    /// </summary>
    public bool MediaLibraryOnly { get; set; }

    /// <summary>Programas que disputam o hub (iCUE, SignalRGB...). Null = processos reais, exceto na simulação.</summary>
    public Func<IReadOnlyList<string>>? ConflictProbe { get; set; }
    public TimeSpan ConflictCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Arquivo onde a configuração é persistida. Null = não persiste (testes).</summary>
    public string? ConfigPath { get; set; } = AppPaths.ControlConfigFile;
}

/// <summary>
/// Núcleo do LinkFlow: dono exclusivo do hardware. Roda três filas de trabalho independentes,
/// cada uma em sua própria thread de fundo, para que nada bloqueie a interface:
/// <list type="bullet">
/// <item><b>Hub</b>: leitura de RPM/temperaturas do iCUE LINK, aplicação de velocidades e RGB do hub.</item>
/// <item><b>Sensores</b>: LibreHardwareMonitor (CPU/GPU/RAM), curva automática, modo de segurança e fan da GPU.</item>
/// <item><b>Periféricos</b>: RGB da GPU e da placa-mãe, quadros do LCD.</item>
/// </list>
/// A configuração é tratada como "último valor vence": arrastar um slider gera muitas atualizações,
/// mas cada fila aplica apenas o estado mais recente quando acorda.
/// </summary>
public sealed partial class LinkFlowEngine : IDisposable
{
    private const int GpuRgbResendAttempts = 3;
    private static readonly TimeSpan GpuRgbResendDelay = TimeSpan.FromMilliseconds(350);
    private readonly IHardwareFactory _factory;
    private readonly EngineOptions _opt;
    private readonly JsonFileStore<ControlConfig>? _store;

    private readonly IFanHub _hub;
    private readonly ISensorSource _sensors;
    private readonly ILcdDisplay _lcd;
    private readonly IRgbDevice _gpuRgb;
    private readonly IRgbDevice _mbRgb;
    private readonly IRgbDevice _ramRgb;
    private readonly IRgbDevice _peripheralsRgb;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _hubSignal = new(0, 1);
    private readonly SemaphoreSlim _sensorSignal = new(0, 1);
    private readonly SemaphoreSlim _auxSignal = new(0, 1);
    private readonly SemaphoreSlim _ramSignal = new(0, 1);
    private readonly List<Task> _lanes = new();
    private readonly Timer _saveTimer;

    // Configuração (imutável por convenção; substituída a cada alteração)
    private readonly object _configLock = new();
    private ControlConfig _config;
    private long _configVersion;
    private long _rgbVersion;
    private long _gpuFanVersion;
    private long _gpuRgbResendVersion;

    // Estado compartilhado (protegido por _stateLock)
    private readonly object _stateLock = new();
    private HardwareTelemetry _telemetry = new();
    private bool _sensorsReady;
    private bool _hubConnected;
    private bool _hubEverConnected;
    private string _hubFirmware = string.Empty;
    private string _hubStatus = "Procurando iCUE LINK Hub...";
    private List<FanState> _fans = new();
    private float? _coolant;
    private int? _coolantChannel;
    private IReadOnlyDictionary<int, float>? _hubTemperatures;
    private bool _hasHubSpeeds;
    private bool _lcdConnected;
    private bool _rgbReady;
    private bool? _rgbHubOk, _rgbGpuOk, _rgbMbOk, _rgbRamOk, _rgbPeripheralsOk, _rgbLcdOk;
    private readonly Dictionary<string, EngineAlert> _alerts = new();

    // Controle (escritos pela fila de sensores, lidos pela fila do hub)
    private volatile int _curveOutput = -1;
    private volatile bool _failSafe;
    private long? _failSafeRecoveryStarted;
    private readonly HashSet<string> _failSafeCauses = new();
    private readonly HashSet<int> _failSafeCoolantChannels = new();
    private float? _curveInputTemp;
    private int _lcdRequested;
    private int _forceReconnectHub;
    private int _forceReconnectAux;
    private int _rescanRgb;
    private string? _lcdStatus;

    // Rede/discos (fila de sensores) e quem está pedindo leitura dos discos
    private readonly SystemStats _stats = new();
    private NetworkState? _network;
    private List<VolumeState> _volumes = new();
    private List<DiskIoState>? _disks;
    private readonly Dictionary<object, SensorDemand> _demands = new();
    private long _lastDiskDemand;
    private bool _storageOn;

    /// <summary>Um cliente informa o que está exibindo. Discos só são lidos enquanto alguém pede.</summary>
    public void SetSensorDemand(object client, SensorDemand demand)
    {
        lock (_demands)
        {
            if (demand == SensorDemand.None) _demands.Remove(client);
            else _demands[client] = demand;
        }
        Signal(_sensorSignal);
    }

    private void UpdateStorageDemand()
    {
        bool wanted;
        lock (_demands) wanted = _demands.Values.Any(d => d.HasFlag(SensorDemand.DiskIo));
        long now = Stopwatch.GetTimestamp();
        if (wanted) _lastDiskDemand = now;
        // Desliga só 60 s depois do último pedido (evita liga/desliga ao trocar de tela)
        bool on = wanted || (_storageOn && Stopwatch.GetElapsedTime(_lastDiskDemand, now) < TimeSpan.FromSeconds(60));
        if (on == _storageOn) return;
        _storageOn = on;
        SafeCall(() => { _sensors.SetStorageEnabled(on); return true; });
        if (!on) lock (_stateLock) _disks = null;
    }
    private int _started;
    private int _stopped;

    public event Action<EngineSnapshot>? SnapshotUpdated;
    public event Action<ControlConfig, object?>? ConfigChanged;
    public event Action<EngineAlert>? AlertRaised;

    public bool IsSimulated => _factory.IsSimulated;

    public LinkFlowEngine(IHardwareFactory factory, EngineOptions? options = null, ControlConfig? initialConfig = null)
    {
        _factory = factory;
        _opt = options ?? new EngineOptions();
        if (_opt.ConfigPath != null) _store = new JsonFileStore<ControlConfig>(_opt.ConfigPath);

        _config = initialConfig ?? _store?.Load() ?? new ControlConfig();
        _config.Normalize();
        if (initialConfig == null && _store is { LoadedFromFallback: true })
        {
            // Antes o arquivo inválido virava padrão em silêncio (perfis e curvas sumiam sem explicação)
            _alerts["config-recovered"] = new EngineAlert
            {
                Key = "config-recovered",
                Severity = AlertSeverity.Warning,
                Title = _store.RecoveredFromBackup ? "Configuração recuperada da cópia" : "Configuração redefinida",
                Message = _store.RecoveredFromBackup
                    ? "O arquivo de configuração estava danificado. O LinkFlow usou a última cópia salva; confira perfis e curvas."
                    : "O arquivo de configuração estava danificado e não havia cópia. O LinkFlow voltou ao padrão; o arquivo antigo foi guardado como config.json.corrompido.",
                Since = DateTime.Now
            };
        }

        _hub = factory.CreateHub();
        _sensors = factory.CreateSensors();
        _lcd = factory.CreateLcd();
        _gpuRgb = factory.CreateGpuRgb();
        _mbRgb = factory.CreateMotherboardRgb();
        _ramRgb = factory.CreateRamRgb();
        _peripheralsRgb = factory.CreatePeripheralsRgb();

        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _watchdog = new Timer(CheckLanes, null, Timeout.Infinite, Timeout.Infinite);
    }

    private readonly Timer _watchdog;

    // ------------------------------------------------------------------ API pública

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var ct = _cts.Token;
        _lanes.Add(RunLane("hub", () => HubLoopAsync(ct)));
        _lanes.Add(RunLane("sensores", () => SensorLoopAsync(ct)));
        _lanes.Add(RunLane("periféricos", () => AuxLoopAsync(ct)));
        _lanes.Add(RunLane("memórias", () => RamLoopAsync(ct)));
        _lanes.Add(RunLane("snapshot", () => PublishLoopAsync(ct)));
        if (!IsSimulated) WatchUsb();
        var period = Min(TimeSpan.FromSeconds(5), _opt.LaneStallTimeout / 4);
        _watchdog.Change(period, period);
        Log.Info($"Engine iniciado ({(IsSimulated ? "simulação" : "hardware real")}).");
    }

    private static Task RunLane(string name, Func<Task> body) =>
        Task.Factory.StartNew(async () =>
        {
            try { await body().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error($"Fila '{name}' encerrou com erro", ex); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    public bool FailSafeEnabled
    {
        get { lock (_configLock) return _config.Safety.FailSafeEnabled; }
    }

    public ControlConfig GetConfig()
    {
        lock (_configLock) return Json.Clone(_config);
    }

    /// <summary>Aplica uma alteração sobre uma cópia da configuração atual (último valor vence).</summary>
    public void UpdateConfig(Action<ControlConfig> mutate, object? origin = null)
    {
        ControlConfig updated;
        lock (_configLock)
        {
            var next = Json.Clone(_config);
            mutate(next);
            next.Normalize();
            updated = Commit(next);
        }
        AfterConfigChange(updated, origin);
    }

    public void ReplaceConfig(ControlConfig config, object? origin = null)
    {
        ControlConfig updated;
        lock (_configLock)
        {
            var next = Json.Clone(config);
            next.Normalize();
            updated = Commit(next);
        }
        AfterConfigChange(updated, origin);
    }

    private ControlConfig Commit(ControlConfig next)
    {
        var prev = _config;
        if (!prev.Rgb.SameOutputAs(next.Rgb)) Interlocked.Increment(ref _rgbVersion);
        if (!prev.Rgb.SyncGpu && next.Rgb.SyncGpu) _gpuRgbResendVersion++;
        if (prev.GpuFanAuto != next.GpuFanAuto || prev.GpuFanPercent != next.GpuFanPercent) Interlocked.Increment(ref _gpuFanVersion);
        _config = next;
        Interlocked.Increment(ref _configVersion);
        return next;
    }

    private void AfterConfigChange(ControlConfig updated, object? origin)
    {
        Signal(_hubSignal);
        Signal(_sensorSignal);
        Signal(_auxSignal);
        Signal(_ramSignal);
        if (_store != null) _saveTimer.Change(_opt.SaveDebounce, Timeout.InfiniteTimeSpan);

        var handler = ConfigChanged;
        if (handler != null)
        {
            var copy = Json.Clone(updated);
            try { handler(copy, origin); } catch (Exception ex) { Log.Warn($"ConfigChanged handler: {ex.Message}"); }
        }
    }

    public void RequestLcdFrame()
    {
        Interlocked.Exchange(ref _lcdRequested, 1);
        Signal(_auxSignal);
        Signal(_ramSignal);
    }

    /// <summary>Força reconexão de todos os dispositivos (ex.: ao voltar da suspensão).</summary>
    public void NotifyResume()
    {
        Log.Info("Retorno da suspensão: reconectando dispositivos.");
        lock (_stateLock) _failSafeRecoveryStarted = null;
        Interlocked.Exchange(ref _forceReconnectHub, 1);
        Interlocked.Exchange(ref _forceReconnectAux, 1);
        Interlocked.Increment(ref _rgbVersion);
        Interlocked.Increment(ref _gpuFanVersion);
        Signal(_hubSignal);
        Signal(_sensorSignal);
        Signal(_auxSignal);
        Signal(_ramSignal);
    }

    /// <summary>Revê os controladores RGB e reenvia a cor atual, mantendo as conexões do hub e do LCD.</summary>
    public void RefreshRgb()
    {
        Interlocked.Exchange(ref _rescanRgb, 1);
        Interlocked.Increment(ref _rgbVersion);
        Signal(_hubSignal);
        Signal(_auxSignal);
        Signal(_ramSignal);
    }

    /// <summary>
    /// Procura de novo todos os dispositivos: reabre o hub (relê a lista de ventoinhas/bomba),
    /// reconecta o LCD e tenta detectar o RGB da GPU/placa-mãe que não foi encontrado.
    /// </summary>
    public void RescanDevices()
    {
        Log.Info("Reatualizando dispositivos a pedido do usuário.");
        lock (_stateLock) _hubStatus = "Procurando dispositivos...";
        Interlocked.Exchange(ref _forceReconnectHub, 1);
        Interlocked.Exchange(ref _forceReconnectAux, 1);
        Interlocked.Exchange(ref _rescanRgb, 1);
        Interlocked.Increment(ref _gpuFanVersion);
        Signal(_hubSignal);
        Signal(_sensorSignal);
        Signal(_auxSignal);
        Signal(_ramSignal);
    }

    public EngineSnapshot GetSnapshot()
    {
        ControlConfig cfg;
        lock (_configLock) cfg = _config;

        lock (_stateLock)
        {
            var t = _telemetry;
            return new EngineSnapshot
            {
                Timestamp = DateTime.Now,
                IsSimulated = IsSimulated,
                HubConnected = _hubConnected,
                HubFirmware = _hubFirmware,
                HubStatus = _hubStatus,
                SensorsReady = _sensorsReady,
                CpuSensorAvailable = t.CpuSensorAvailable,
                CpuName = t.CpuName,
                GpuName = t.GpuName,
                CpuTemp = t.CpuTemperature,
                CpuLoad = t.CpuLoad,
                GpuTemp = t.GpuTemperature,
                GpuHotspot = t.GpuHotspotTemperature,
                GpuLoad = t.GpuLoad,
                RamUsage = t.RamUsagePercent,
                RamUsedGb = t.RamUsedGb,
                RamTotalGb = t.RamTotalGb,
                CoolantTemp = _coolant,
                GpuFanRpm1 = t.GpuFanRpm1,
                GpuFanRpm2 = t.GpuFanRpm2,
                GpuFanPercent = t.GpuFanPercent,
                CpuPower = t.CpuPower,
                CpuClockMhz = t.CpuClockMhz,
                CpuCores = t.CpuCores,
                GpuPower = t.GpuPower,
                GpuClockMhz = t.GpuClockMhz,
                GpuMemClockMhz = t.GpuMemClockMhz,
                GpuVramUsedGb = t.GpuVramUsedGb,
                GpuVramTotalGb = t.GpuVramTotalGb,
                CpuCoreLoads = t.CpuCoreLoads,
                Network = _network,
                Volumes = _volumes,
                Disks = _disks,
                LcdStatus = _lcdStatus,
                EffectiveGlobalPercent = EffectiveGlobal(cfg),
                GroupOutputs = new Dictionary<string, int>(_groupOutputs),
                CurveInputTemp = _curveInputTemp,
                FailSafeActive = _failSafe,
                Fans = _fans.Select(f => new FanState
                {
                    Key = f.Key, Channel = f.Channel, DeviceId = f.DeviceId, DefaultName = f.DefaultName,
                    Model = f.Model, IsPump = f.IsPump, Rpm = f.Rpm, AppliedPercent = f.AppliedPercent, Temperature = f.Temperature
                }).ToList(),
                LcdConnected = _lcdConnected,
                GpuRgbAvailable = _gpuRgb.IsAvailable,
                GpuRgbRetrying = cfg.Rgb.SyncGpu && _gpuRgb.NeedsInitializationRetry,
                GpuRgbName = _gpuRgb.Name,
                GpuRgbApplied = cfg.Rgb.SyncGpu ? _rgbGpuOk : null,
                GpuRgbDiagnostic = _gpuRgb.Diagnostic,
                MotherboardRgbAvailable = _mbRgb.IsAvailable,
                MotherboardRgbName = _mbRgb.Name,
                RamRgbAvailable = _ramRgb.IsAvailable,
                RamRgbName = _ramRgb.Name,
                PeripheralsRgbAvailable = _peripheralsRgb.IsAvailable,
                PeripheralsRgbName = _peripheralsRgb.Name,
                PeripheralsRgbApplied = cfg.Rgb.SyncPeripherals ? _rgbPeripheralsOk : null,
                PeripheralsRgbDiagnostic = _peripheralsRgb.Diagnostic,
                RgbDevicesReady = _rgbReady,
                RgbStatus = BuildRgbStatus(cfg.Rgb),
                Alerts = _alerts.Values.OrderByDescending(a => a.Severity).ThenBy(a => a.Since).Select(a => new EngineAlert
                {
                    Key = a.Key, Severity = a.Severity, Title = a.Title, Message = a.Message, Since = a.Since
                }).ToList()
            };
        }
    }

    // ------------------------------------------------------------------ Snapshot e alertas

    private async Task PublishLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_opt.SnapshotInterval, ct).ConfigureAwait(false);
            var handler = SnapshotUpdated;
            if (handler == null) continue;
            try { handler(GetSnapshot()); }
            catch (Exception ex) { Log.Warn($"SnapshotUpdated handler: {ex.Message}"); }
        }
    }

    /// <summary>Chamar com _stateLock adquirido.</summary>
    private void RaiseAlert(string key, AlertSeverity severity, string title, string message)
    {
        if (_alerts.TryGetValue(key, out var existing))
        {
            bool escalated = severity > existing.Severity;
            existing.Severity = severity;
            existing.Title = title;
            existing.Message = message;
            if (!escalated) return;
        }
        else
        {
            existing = new EngineAlert { Key = key, Severity = severity, Title = title, Message = message, Since = DateTime.Now };
            _alerts[key] = existing;
        }

        Log.Warn($"ALERTA [{severity}] {title}: {message}");
        var copy = new EngineAlert { Key = key, Severity = severity, Title = title, Message = message, Since = existing.Since };
        var handler = AlertRaised;
        if (handler != null) ThreadPool.QueueUserWorkItem(_ => { try { handler(copy); } catch { } });
    }

    /// <summary>Chamar com _stateLock adquirido.</summary>
    private void ClearAlert(string key) => _alerts.Remove(key);

    // ------------------------------------------------------------------ Encerramento e utilidades

    private void SaveNow()
    {
        if (_store == null) return;
        ControlConfig cfg;
        lock (_configLock) cfg = _config;
        _store.Save(cfg);
    }

    /// <param name="restoreHardwareMode">Devolve o hub ao modo hardware (curva interna) e a GPU ao vBIOS.</param>
    public async Task StopAsync(bool restoreHardwareMode = true)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        _cts.Cancel();
        try { await Task.WhenAll(_lanes).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warn($"Filas não encerraram a tempo: {ex.Message}"); }

        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveNow();

        if (restoreHardwareMode)
        {
            SafeCall(() => _sensors.SetGpuFanAuto());
        }
        SafeCall(() => { _hub.Disconnect(restoreHardwareMode); return true; });
        foreach (var d in new IDisposable[] { _lcd, _gpuRgb, _mbRgb, _ramRgb, _peripheralsRgb, _sensors })
        {
            try { d.Dispose(); } catch { }
        }
        Log.Info($"Engine parado (hub {(restoreHardwareMode ? "devolvido ao modo hardware" : "mantido")}).");
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _watchdog.Dispose();
        _saveTimer.Dispose();
        _cts.Dispose();
    }

    private static bool Apply(IRgbDevice device, RgbConfig rgb, string target, long frame)
    {
        var o = rgb.OutputFor(target, frame, 0);
        return rgb.IsAnimated
            ? device.SetAnimationColor(o.R, o.G, o.B, o.Brightness)
            : device.SetColor(o.R, o.G, o.B, o.Brightness);
    }

    private static bool SafeCall(Func<bool> f)
    {
        try { return f(); }
        catch (Exception ex)
        {
            Log.Warn($"Falha de dispositivo: {ex.Message}");
            return false;
        }
    }

    private static void Signal(SemaphoreSlim s)
    {
        if (s.CurrentCount > 0) return;
        try { s.Release(); } catch (SemaphoreFullException) { }
    }

    private static async Task WaitAsync(SemaphoreSlim s, TimeSpan timeout, CancellationToken ct)
    {
        await s.WaitAsync(timeout, ct).ConfigureAwait(false);
    }

    private static long Ticks(TimeSpan ts) => (long)(ts.TotalSeconds * Stopwatch.Frequency);
    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.FromMilliseconds(5) ? TimeSpan.FromMilliseconds(5) : t > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : t;
}
