using System.Diagnostics;

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
public sealed class LinkFlowEngine : IDisposable
{
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
    private bool _lcdConnected;
    private bool _rgbReady;
    private bool? _rgbHubOk, _rgbGpuOk, _rgbMbOk, _rgbRamOk, _rgbPeripheralsOk, _rgbLcdOk;
    private readonly Dictionary<string, EngineAlert> _alerts = new();

    // Controle (escritos pela fila de sensores, lidos pela fila do hub)
    private volatile int _curveOutput = -1;
    private volatile bool _failSafe;
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

        _hub = factory.CreateHub();
        _sensors = factory.CreateSensors();
        _lcd = factory.CreateLcd();
        _gpuRgb = factory.CreateGpuRgb();
        _mbRgb = factory.CreateMotherboardRgb();
        _ramRgb = factory.CreateRamRgb();
        _peripheralsRgb = factory.CreatePeripheralsRgb();

        _saveTimer = new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

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
        Log.Info($"Engine iniciado ({(IsSimulated ? "simulação" : "hardware real")}).");
    }

    private static Task RunLane(string name, Func<Task> body) =>
        Task.Factory.StartNew(async () =>
        {
            try { await body().ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Error($"Fila '{name}' encerrou com erro", ex); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

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

    // ------------------------------------------------------------------ Fila do hub

    private async Task HubLoopAsync(CancellationToken ct)
    {
        var rgbClock = new RgbFrameClock();
        int rgbInterval = RgbFrameClock.IntervalMs(_opt.HubRgbMinInterval);
        long nextRead = 0, nextReconnect = 0, lastSpeedApply = 0, lastRgbApply = 0;
        long nextDeviceCheck = Stopwatch.GetTimestamp() + Ticks(_opt.SubDeviceCheckInterval);
        long appliedRgbVersion = -1;
        int failures = 0;
        Dictionary<int, byte>? lastSent = null;
        var zeroReads = new Dictionary<string, int>();

        while (!ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            try
            {
                if (Interlocked.Exchange(ref _forceReconnectHub, 0) == 1)
                {
                    if (_hub.IsConnected) _hub.Disconnect(restoreHardwareMode: false);
                    nextReconnect = 0;
                }

                if (Interlocked.Exchange(ref _usbChangedHub, 0) == 1 && !_hub.IsConnected) nextReconnect = 0;
                if (!_hub.IsConnected && now >= nextReconnect)
                {
                    bool ok = SafeCall(() => _hub.Connect());
                    nextReconnect = now + Ticks(_opt.ReconnectInterval);
                    if (ok)
                    {
                        failures = 0;
                        lastSent = null;
                        appliedRgbVersion = -1;
                        nextRead = 0;
                        OnHubConnected();
                    }
                    else
                    {
                        OnHubDisconnected();
                    }
                }

                if (_hub.IsConnected)
                {
                    ControlConfig cfg;
                    lock (_configLock) cfg = _config;

                    // 1. Telemetria do hub
                    if (now >= nextRead)
                    {
                        nextRead = now + Ticks(_opt.TelemetryInterval);
                        var speeds = _hub.ReadSpeeds();
                        var temps = _hub.ReadTemperatures();

                        if (speeds == null && temps == null) failures++;
                        else failures = 0;

                        if (failures >= _opt.HubFailuresBeforeReconnect || !_hub.IsConnected)
                        {
                            Log.Warn("Hub sem resposta: reconectando.");
                            _hub.Disconnect(restoreHardwareMode: false);
                            OnHubDisconnected();
                            nextReconnect = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(1));
                            continue;
                        }

                        UpdateHubTelemetry(cfg, speeds, temps, zeroReads);

                        // Ventoinha conectada/removida na corrente do hub (o USB do hub não muda)
                        if (now >= nextDeviceCheck)
                        {
                            nextDeviceCheck = now + Ticks(_opt.SubDeviceCheckInterval);
                            if (_hub.RefreshSubDevices())
                            {
                                Log.Info("Dispositivos do hub mudaram: atualizando a lista.");
                                OnHubConnected();
                                lastSent = null;
                            }
                        }
                    }

                    // 2. Velocidades (somente quando mudam, ou reafirmação periódica)
                    var desired = ComputeDesiredSpeeds(cfg);
                    bool reassert = Stopwatch.GetElapsedTime(lastSpeedApply) >= _opt.SpeedReassertInterval;
                    if (desired.Count > 0 && (lastSent == null || reassert || !SameSpeeds(desired, lastSent)))
                    {
                        if (_hub.SetSpeedsPercent(desired))
                        {
                            lastSent = desired;
                            lastSpeedApply = Stopwatch.GetTimestamp();
                            lock (_stateLock)
                            {
                                foreach (var f in _fans)
                                    if (desired.TryGetValue(f.Channel, out var p)) f.AppliedPercent = p;
                            }
                        }
                    }

                    // 3. RGB do hub (limitado a ~16 atualizações/s durante arrastes e efeitos)
                    long rgbVersion = Interlocked.Read(ref _rgbVersion);
                    bool hubAnimated = cfg.Rgb.SyncHub && cfg.Rgb.IsAnimated;
                    long frame = rgbClock.FrameTime(RgbEffects.Now, rgbInterval);
                    bool nextRgbFrame = hubAnimated && rgbClock.IsDue(frame, rgbInterval);
                    if ((appliedRgbVersion != rgbVersion || nextRgbFrame) && Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.HubRgbMinInterval)
                    {
                        bool changed = appliedRgbVersion != rgbVersion;
                        appliedRgbVersion = rgbVersion;
                        lastRgbApply = Stopwatch.GetTimestamp();
                        if (cfg.Rgb.SyncHub)
                        {
                            bool ok;
                            if (hubAnimated)
                            {
                                ok = ApplyHubFrame(cfg.Rgb, frame);
                                rgbClock.RecordFrame(frame, rgbInterval, ok ? Stopwatch.GetElapsedTime(lastRgbApply) : null);
                            }
                            else
                            {
                                var o = cfg.Rgb.OutputFor(RgbTargets.Hub);
                                ok = _hub.SetGlobalRgb(o.R, o.G, o.B, o.Brightness);
                            }
                            if (changed || _rgbHubOk != ok) lock (_stateLock) _rgbHubOk = ok;
                        }
                        else
                        {
                            lock (_stateLock) _rgbHubOk = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Erro na fila do hub", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            // Espera até a próxima leitura, ou acorda antes se a configuração mudar
            var wait = _hub.IsConnected
                ? Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextRead)
                : Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextReconnect);
            if (Interlocked.Read(ref _rgbVersion) != appliedRgbVersion && _hub.IsConnected)
                wait = Min(wait, _opt.HubRgbMinInterval);
            if (_hub.IsConnected && CurrentRgb is { SyncHub: true, IsAnimated: true })
                wait = Min(wait, RgbFrameWait(rgbClock, rgbInterval, lastRgbApply, _opt.HubRgbMinInterval));
            await WaitAsync(_hubSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    private void OnHubConnected()
    {
        var devices = _hub.SubDevices;
        lock (_stateLock)
        {
            _hubConnected = true;
            _hubEverConnected = true;
            _hubFirmware = _hub.FirmwareVersion;
            _hubStatus = $"Conectado • FW {_hub.FirmwareVersion} • {devices.Count} dispositivos";
            _fans = devices.Select(d => new FanState
            {
                Key = FanState.KeyFor(d),
                Channel = d.Channel,
                DeviceId = d.DeviceId,
                DefaultName = d.Name,
                Model = d.Model.ToString(),
                IsPump = d.IsPump,
                Rpm = d.CurrentRpm
            }).ToList();
            ClearAlert("hub-lost");
        }
        Log.Info($"Hub conectado: FW {_hub.FirmwareVersion}, {devices.Count} dispositivos.");
    }

    private void OnHubDisconnected()
    {
        lock (_stateLock)
        {
            bool wasConnected = _hubConnected;
            _hubConnected = false;
            _hubStatus = _hubEverConnected
                ? "Reconectando ao iCUE LINK Hub..."
                : "iCUE LINK Hub não encontrado (verifique o USB ou se o iCUE está aberto)";
            if (wasConnected || _hubEverConnected)
            {
                RaiseAlert("hub-lost", AlertSeverity.Warning, "Hub desconectado",
                    "O iCUE LINK Hub parou de responder. Tentando reconectar automaticamente.");
            }
        }
    }

    private void UpdateHubTelemetry(ControlConfig cfg, IReadOnlyDictionary<int, int>? speeds,
        IReadOnlyDictionary<int, float>? temps, Dictionary<string, int> zeroReads)
    {
        lock (_stateLock)
        {
            if (temps is { Count: > 0 })
            {
                var pump = _fans.FirstOrDefault(f => f.IsPump);
                float? coolant = null;
                if (pump != null && temps.TryGetValue(pump.Channel, out var pt) && pt is > 15 and < 80) coolant = pt;
                if (coolant == null)
                {
                    var plausible = temps.Values.Where(v => v is > 15 and < 80).ToList();
                    if (plausible.Count > 0) coolant = plausible.Max();
                }
                if (coolant != null) _coolant = coolant;

                foreach (var f in _fans)
                    if (temps.TryGetValue(f.Channel, out var t)) f.Temperature = t;
            }

            if (speeds == null) return;

            foreach (var f in _fans)
            {
                f.Rpm = speeds.TryGetValue(f.Channel, out var rpm) ? rpm : 0;

                if (!cfg.Safety.FanStopAlert)
                {
                    zeroReads.Remove(f.Key);
                    continue;
                }

                string name = cfg.GetFan(f.Key).Name ?? f.DefaultName;
                if (f.IsPump)
                {
                    bool low = f.Rpm < cfg.Safety.PumpMinRpm;
                    int n = low ? zeroReads.GetValueOrDefault(f.Key) + 1 : 0;
                    zeroReads[f.Key] = n;
                    if (n >= _opt.PumpLowReadsBeforeAlert)
                        RaiseAlert($"pump:{f.Key}", AlertSeverity.Critical, "Bomba com rotação baixa",
                            $"{name}: {f.Rpm} RPM (mínimo {cfg.Safety.PumpMinRpm}). Verifique a bomba!");
                    else if (n == 0) ClearAlert($"pump:{f.Key}");
                }
                else
                {
                    // Dispositivos de modelo desconhecido podem não ter tacômetro (ex.: módulos sem ventoinha)
                    bool stopped = f.Rpm == 0 && f.AppliedPercent >= FanLimits.MinPercent &&
                                   f.Model != nameof(LinkDeviceModel.Unknown);
                    int n = stopped ? zeroReads.GetValueOrDefault(f.Key) + 1 : 0;
                    zeroReads[f.Key] = n;
                    if (n >= _opt.FanStopReadsBeforeAlert)
                        RaiseAlert($"fan:{f.Key}", AlertSeverity.Warning, "Ventoinha parada",
                            $"{name} está em 0 RPM com {f.AppliedPercent}% de potência.");
                    else if (n == 0) ClearAlert($"fan:{f.Key}");
                }
            }
        }
    }

    private byte EffectiveGlobal(ControlConfig cfg)
    {
        if (_failSafe) return 100;
        if (cfg.Mode == FanControlMode.Curve && _curveOutput >= 0) return (byte)_curveOutput;
        return cfg.GlobalPercent;
    }

    /// <summary>Velocidade desejada por canal, considerando vínculo global, ajustes manuais, bomba e modo de segurança.</summary>
    public Dictionary<int, byte> ComputeDesiredSpeeds(ControlConfig cfg)
    {
        var result = new Dictionary<int, byte>();
        byte global = EffectiveGlobal(cfg);
        bool failSafe = _failSafe;
        var groupOutputs = _groupOutputs;
        foreach (var dev in _hub.SubDevices)
        {
            var ov = cfg.GetFan(FanState.KeyFor(dev));
            // Vinculada: segue a curva do seu grupo (se houver) ou a potência global/curva global
            byte linked = ov.Group != null && groupOutputs.TryGetValue(ov.Group, out var go) ? (byte)go : global;
            byte p = failSafe ? (byte)100 : ov.Linked ? linked : ov.Percent;
            if (dev.IsPump) p = Math.Max((byte)FanLimits.PumpMinPercent, p);
            result[dev.Channel] = Math.Clamp(p, (byte)FanLimits.MinPercent, (byte)100);
        }
        return result;
    }

    private static bool SameSpeeds(Dictionary<int, byte> a, Dictionary<int, byte> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);



    // ------------------------------------------------------------------ Conexão de dispositivos (USB)

    private int _usbChangedHub, _usbChangedAux;

    /// <summary>
    /// Avisado pelo Windows quando algo USB/HID é conectado: hub/LCD reconectam na hora
    /// em vez de esperar a próxima tentativa (que fica mais espaçada e consome menos).
    /// </summary>
    private void WatchUsb()
    {
        try
        {
            HidSharp.DeviceList.Local.Changed += (_, _) =>
            {
                Interlocked.Exchange(ref _usbChangedHub, 1);
                Interlocked.Exchange(ref _usbChangedAux, 1);
                Signal(_hubSignal);
                Signal(_auxSignal);
                Signal(_ramSignal);
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"Aviso de USB indisponível: {ex.Message}");
        }
    }
    // ------------------------------------------------------------------ Curvas por grupo

    private volatile Dictionary<string, int> _groupOutputs = new();

    /// <summary>Cada grupo com curva própria tem seu controlador (suavização/histerese independentes).</summary>
    private void EvaluateGroupCurves(ControlConfig cfg, Dictionary<string, (CurveController Controller, string Json)> controllers,
        float? cpu, float? gpu, float? coolant, bool newSample)
    {
        var outputs = new Dictionary<string, int>();
        foreach (var (group, curveCfg) in cfg.GroupCurves)
        {
            string json = System.Text.Json.JsonSerializer.Serialize(curveCfg, Json.Compact);
            if (!controllers.TryGetValue(group, out var entry) || entry.Json != json)
            {
                entry = (new CurveController(), json); // curva editada: aplica sem esperar a histerese
                controllers[group] = entry;
            }
            var temp = CurveController.SelectTemp(curveCfg.Source, cpu, gpu, coolant);
            if (temp is { } t && (newSample || entry.Controller.Current == null)) outputs[group] = entry.Controller.Evaluate(curveCfg, t);
            else if (entry.Controller.Current is { } cur) outputs[group] = cur;
        }
        foreach (var removed in controllers.Keys.Where(k => !cfg.GroupCurves.ContainsKey(k)).ToList()) controllers.Remove(removed);

        var prev = _groupOutputs;
        if (prev.Count != outputs.Count || outputs.Any(kv => !prev.TryGetValue(kv.Key, out var v) || v != kv.Value))
        {
            _groupOutputs = outputs;
            Signal(_hubSignal);
        }
    }
    // ------------------------------------------------------------------ Fila de sensores

    private async Task SensorLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        try { _sensors.Open(); }
        catch (Exception ex) { Log.Warn($"Sensores indisponíveis: {ex.Message}"); }
        lock (_stateLock) _sensorsReady = true;

        var curve = new CurveController();
        var groupControllers = new Dictionary<string, (CurveController Controller, string Json)>();
        float? lastCpu = null, lastGpu = null, lastCoolant = null;
        var lastMode = (FanControlMode)(-1);
        long appliedGpuVersion = -1;
        bool gpuFailSafeApplied = false;
        long nextRead = 0;
        float? lastTemp = null;
        string? lastCurve = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                ControlConfig cfg;
                lock (_configLock) cfg = _config;

                bool newSample = Stopwatch.GetTimestamp() >= nextRead;
                if (newSample)
                {
                    nextRead = Stopwatch.GetTimestamp() + Ticks(_opt.TelemetryInterval);
                    var t = _sensors.Read();
                    var network = _stats.ReadNetwork();
                    var volumes = _stats.ReadVolumes();
                    UpdateStorageDemand();
                    var disks = _sensors.ReadDisks();
                    float? coolant;
                    lock (_stateLock)
                    {
                        _telemetry = t;
                        _network = network;
                        _volumes = volumes;
                        _disks = disks;
                        coolant = _coolant;
                    }
                    lastCpu = t.CpuSensorAvailable ? t.CpuTemperature : null;
                    lastGpu = t.GpuTemperature;
                    lastCoolant = coolant;
                    lastTemp = CurveController.SelectTemp(cfg.Curve.Source, lastCpu, lastGpu, coolant);
                    EvaluateSafety(cfg, t, coolant);
                }

                // Curva automática
                int previousOutput = _curveOutput;
                string curveJson = System.Text.Json.JsonSerializer.Serialize(cfg.Curve, Json.Compact);
                bool curveEdited = lastCurve != null && curveJson != lastCurve;
                if (cfg.Mode != lastMode || curveEdited)
                {
                    // Mudou o modo ou a curva foi editada: aplica imediatamente, sem histerese pendente
                    float? smoothed = curve.SmoothedTemp;
                    curve.Reset();
                    if (smoothed != null && cfg.Mode == lastMode) lastTemp ??= smoothed;
                    lastMode = cfg.Mode;
                    lastCurve = curveJson;
                }

                if (cfg.Mode != FanControlMode.Curve) _curveOutput = -1;
                else if (lastTemp is { } temp && (newSample || curve.Current == null)) _curveOutput = curve.Evaluate(cfg.Curve, temp);
                lock (_stateLock) _curveInputTemp = curve.SmoothedTemp ?? lastTemp;
                if (previousOutput != _curveOutput) Signal(_hubSignal);
                EvaluateGroupCurves(cfg, groupControllers, lastCpu, lastGpu, lastCoolant, newSample);

                // Fan da GPU (LibreHardwareMonitor)
                long gpuVersion = Interlocked.Read(ref _gpuFanVersion);
                bool failSafe = _failSafe;
                if (failSafe && !gpuFailSafeApplied)
                {
                    _sensors.SetGpuFanSpeed(100);
                    gpuFailSafeApplied = true;
                }
                else if ((!failSafe && gpuFailSafeApplied) || (!failSafe && appliedGpuVersion != gpuVersion))
                {
                    gpuFailSafeApplied = false;
                    appliedGpuVersion = gpuVersion;
                    if (cfg.GpuFanAuto) _sensors.SetGpuFanAuto();
                    else _sensors.SetGpuFanSpeed(cfg.GpuFanPercent);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Erro na fila de sensores", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextRead);
            await WaitAsync(_sensorSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    private void EvaluateSafety(ControlConfig cfg, HardwareTelemetry t, float? coolant)
    {
        var s = cfg.Safety;
        float? cpu = t.CpuSensorAvailable ? t.CpuTemperature : null;
        float? gpu = t.GpuTemperature;

        lock (_stateLock)
        {
            TempAlert("cpu-temp", "CPU", cpu, s.CpuWarn, s.CpuCritical);
            TempAlert("gpu-temp", "GPU", gpu, s.GpuWarn, s.GpuCritical);
            TempAlert("coolant-temp", "Líquido", coolant, s.CoolantWarn, s.CoolantCritical);

            bool pumpCritical = _alerts.Keys.Any(k => k.StartsWith("pump:", StringComparison.Ordinal));
            bool critical = cpu >= s.CpuCritical || gpu >= s.GpuCritical || coolant >= s.CoolantCritical || pumpCritical;
            bool clear = !(cpu >= s.CpuCritical - 5) && !(gpu >= s.GpuCritical - 5) && !(coolant >= s.CoolantCritical - 3) && !pumpCritical;

            bool wasActive = _failSafe;
            if (!s.FailSafeEnabled) _failSafe = false;
            else if (critical) _failSafe = true;
            else if (clear) _failSafe = false;

            if (_failSafe && !wasActive)
            {
                RaiseAlert("failsafe", AlertSeverity.Critical, "Modo de segurança ativado",
                    "Temperatura crítica ou falha na bomba: todas as ventoinhas foram para 100%.");
                Log.Warn("Modo de segurança ATIVADO.");
            }
            else if (!_failSafe && wasActive)
            {
                ClearAlert("failsafe");
                Log.Info("Modo de segurança desativado.");
            }
        }

        if (_failSafe) Signal(_hubSignal);
    }

    private void TempAlert(string key, string label, float? value, float warn, float critical)
    {
        if (value is not { } v)
        {
            ClearAlert(key);
            return;
        }

        if (v >= critical)
        {
            RaiseAlert(key, AlertSeverity.Critical, $"{label} em temperatura crítica", $"{label} a {v:0}°C (limite {critical:0}°C).");
        }
        else if (v >= warn)
        {
            RaiseAlert(key, AlertSeverity.Warning, $"{label} muito quente", $"{label} a {v:0}°C (alerta em {warn:0}°C).");
        }
        else if (v < warn - 3)
        {
            ClearAlert(key);
        }
    }

    // ------------------------------------------------------------------ Fila de periféricos

    private async Task AuxLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        SafeCall(() => { _gpuRgb.Initialize(); return true; });
        SafeCall(() => { _mbRgb.Initialize(); return true; });
        SafeCall(() => { _ramRgb.Initialize(); return true; });
        SafeCall(() => { _peripheralsRgb.Initialize(); return true; });
        lock (_stateLock) _rgbReady = true;
        Log.Info($"RGB: GPU={(_gpuRgb.IsAvailable ? _gpuRgb.Name : "não detectada")} • Placa-mãe={(_mbRgb.IsAvailable ? _mbRgb.Name : "não detectada")} • RAM={(_ramRgb.IsAvailable ? _ramRgb.Name : "não detectada")} • Periféricos={(_peripheralsRgb.IsAvailable ? _peripheralsRgb.Name : "não detectados")}");
        if (_gpuRgb.Diagnostic is { } initialGpuDiagnostic) Log.Info($"RGB GPU: {initialGpuDiagnostic}");

        using var composer = new LcdComposer();
        var rgbClock = new RgbFrameClock();
        int rgbInterval = RgbFrameClock.IntervalMs(_opt.AuxRgbMinInterval);
        long appliedRgbVersion = -1, lastRgbApply = 0, nextFrame = 0, nextLcdReconnect = 0;
        long nextGpuRetry = Stopwatch.GetTimestamp() + Ticks(_opt.ReconnectInterval);
        long nextPeripheralRetry = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(30));
        LcdConfig? lastLcd = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                ControlConfig cfg;
                lock (_configLock) cfg = _config;
                long now = Stopwatch.GetTimestamp();

                if (Interlocked.Exchange(ref _forceReconnectAux, 0) == 1)
                {
                    _lcd.Disconnect();
                    nextLcdReconnect = 0;
                }

                // G HUB costuma abrir depois do LinkFlow no logon: tenta os periféricos de novo a cada 30 s
                if (!_peripheralsRgb.IsAvailable && now >= nextPeripheralRetry)
                {
                    nextPeripheralRetry = now + Ticks(TimeSpan.FromSeconds(30));
                    if (SafeCall(() => { _peripheralsRgb.Initialize(); return _peripheralsRgb.IsAvailable; }))
                    {
                        Log.Info($"Periféricos detectados: {_peripheralsRgb.Name}");
                        Interlocked.Increment(ref _rgbVersion);
                    }
                }

                bool rescanRgb = Interlocked.Exchange(ref _rescanRgb, 0) == 1;
                bool retryGpu = cfg.Rgb.SyncGpu && _gpuRgb.NeedsInitializationRetry && now >= nextGpuRetry;
                if (retryGpu || (rescanRgb && (cfg.Rgb.SyncGpu || !_gpuRgb.IsAvailable)))
                {
                    bool found = SafeCall(() => { _gpuRgb.Initialize(); return _gpuRgb.IsAvailable; });
                    nextGpuRetry = Stopwatch.GetTimestamp() + Ticks(_opt.ReconnectInterval);
                    if (retryGpu && found)
                    {
                        Log.Info($"RGB GPU: conexão recuperada com {_gpuRgb.Name}.");
                        appliedRgbVersion = -1;
                    }
                }

                if (rescanRgb)
                {
                    if (!_mbRgb.IsAvailable) SafeCall(() => { _mbRgb.Initialize(); return true; });
                    if (!_ramRgb.IsAvailable) SafeCall(() => { _ramRgb.Initialize(); return true; });
                    if (!_peripheralsRgb.IsAvailable) SafeCall(() => { _peripheralsRgb.Initialize(); return true; });
                    Interlocked.Increment(ref _rgbVersion);
                    Log.Info($"RGB redetectado: GPU={(_gpuRgb.IsAvailable ? _gpuRgb.Name : "não detectada")} • Placa-mãe={(_mbRgb.IsAvailable ? _mbRgb.Name : "não detectada")} • RAM={(_ramRgb.IsAvailable ? _ramRgb.Name : "não detectada")} • Periféricos={(_peripheralsRgb.IsAvailable ? _peripheralsRgb.Name : "não detectados")}");
                }

                // Configuração do LCD mudou: novo quadro imediatamente e libera imagens que saíram da lista
                if (!ReferenceEquals(lastLcd, cfg.Lcd))
                {
                    if (lastLcd != null) composer.Prune(cfg.Lcd);
                    lastLcd = cfg.Lcd;
                    nextFrame = 0;
                }

                // RGB da GPU, placa-mãe e periféricos (a RAM, lenta, tem fila própria)
                long rgbVersion = Interlocked.Read(ref _rgbVersion);
                var rgb = cfg.Rgb;
                bool animated = rgb.IsAnimated && HasAuxRgb(rgb);
                long frame = rgbClock.FrameTime(RgbEffects.Now, rgbInterval);
                if ((appliedRgbVersion != rgbVersion || (animated && rgbClock.IsDue(frame, rgbInterval))) &&
                    Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.AuxRgbMinInterval)
                {
                    bool changed = appliedRgbVersion != rgbVersion;
                    appliedRgbVersion = rgbVersion;
                    lastRgbApply = Stopwatch.GetTimestamp();
                    var previousGpuOk = _rgbGpuOk;
                    var previousGpuDiagnostic = _gpuRgb.Diagnostic;
                    bool? gpuOk = rgb.SyncGpu && _gpuRgb.IsAvailable ? SafeCall(() => Apply(_gpuRgb, rgb, RgbTargets.Gpu, frame)) : null;
                    bool? mbOk = rgb.SyncMotherboard && _mbRgb.IsAvailable ? SafeCall(() => Apply(_mbRgb, rgb, RgbTargets.Motherboard, frame)) : null;
                    bool? periphOk = rgb.SyncPeripherals && _peripheralsRgb.IsAvailable ? SafeCall(() => Apply(_peripheralsRgb, rgb, RgbTargets.Peripherals, frame)) : null;
                    if (animated)
                    {
                        var writeTime = gpuOk == true || mbOk == true || periphOk == true ? Stopwatch.GetElapsedTime(lastRgbApply) : (TimeSpan?)null;
                        rgbClock.RecordFrame(frame, rgbInterval, writeTime);
                    }
                    lock (_stateLock)
                    {
                        _rgbGpuOk = gpuOk;
                        _rgbMbOk = mbOk;
                        _rgbPeripheralsOk = periphOk;
                    }
                    if (gpuOk == false && (previousGpuOk != false || previousGpuDiagnostic != _gpuRgb.Diagnostic))
                        Log.Warn($"RGB GPU: {_gpuRgb.Diagnostic ?? "O controlador recusou o envio da cor."}");
                    else if (gpuOk == true && previousGpuOk != true)
                        Log.Info($"RGB GPU: cor aceita pelo controlador {_gpuRgb.Name}.");
                    // O anel do LCD segue a cor; durante efeitos ele acompanha no ritmo normal dos quadros
                    if (rgb.SyncLcd && changed) Interlocked.Exchange(ref _lcdRequested, 1);
                }

                // LCD
                if (Interlocked.Exchange(ref _usbChangedAux, 0) == 1) nextLcdReconnect = 0; // algo foi conectado: tenta já
                if (!_lcd.IsConnected && now >= nextLcdReconnect)
                {
                    SafeCall(() => _lcd.Connect());
                    nextLcdReconnect = now + Ticks(_opt.ReconnectInterval);
                }
                lock (_stateLock) _lcdConnected = _lcd.IsConnected;

                bool requested = Interlocked.Exchange(ref _lcdRequested, 0) == 1;
                if (_lcd.IsConnected && (requested || (cfg.Lcd.AutoStream && now >= nextFrame)))
                {
                    var (jpeg, next) = composer.ComposeJpeg(cfg.Lcd, GetSnapshot(), RgbForLcd(cfg.Rgb));
                    // Métricas: 1 quadro/s (opção do engine); GIFs: o atraso do próprio quadro
                    bool fast = composer.IsAnimating || (cfg.Lcd.Mode == LcdMode.Media && cfg.Lcd.Media.Count > 1);
                    var lcdInterval = fast ? next : _opt.LcdFrameInterval;
                    nextFrame = Stopwatch.GetTimestamp() + Ticks(lcdInterval);
                    bool ok = _lcd.SendJpegFrame(jpeg);
                    lock (_stateLock)
                    {
                        _lcdConnected = _lcd.IsConnected;
                        _lcdStatus = composer.Status;
                        _rgbLcdOk = cfg.Rgb.SyncLcd ? ok : null;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Erro na fila de periféricos", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            ControlConfig c2;
            lock (_configLock) c2 = _config;
            var wait = c2.Lcd.AutoStream && _lcd.IsConnected
                ? Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextFrame)
                : _opt.TelemetryInterval;
            if (Interlocked.Read(ref _rgbVersion) != appliedRgbVersion) wait = Min(wait, _opt.AuxRgbMinInterval);
            if (c2.Rgb.IsAnimated && HasAuxRgb(c2.Rgb))
                wait = Min(wait, RgbFrameWait(rgbClock, rgbInterval, lastRgbApply, _opt.AuxRgbMinInterval));
            if (c2.Rgb.SyncGpu && _gpuRgb.NeedsInitializationRetry)
                wait = Min(wait, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextGpuRetry));
            await WaitAsync(_auxSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A fila da memória usa a cadência do controlador sem atrasar os outros dispositivos.
    /// </summary>
    private async Task RamLoopAsync(CancellationToken ct)
    {
        var rgbClock = new RgbFrameClock();
        long applied = -1, lastApply = 0;
        while (!ct.IsCancellationRequested)
        {
            ControlConfig cfg;
            lock (_configLock) cfg = _config;
            bool ready;
            lock (_stateLock) ready = _rgbReady;
            var rgb = cfg.Rgb;
            long version = Interlocked.Read(ref _rgbVersion);
            bool animated = rgb.IsAnimated && rgb.SyncRam && _ramRgb.IsAvailable;
            var minInterval = animated && _ramRgb.MinAnimationInterval is { } streamingInterval
                ? Min(_opt.RamRgbMinInterval, streamingInterval)
                : _opt.RamRgbMinInterval;
            int interval = RgbFrameClock.IntervalMs(minInterval);
            long frame = rgbClock.FrameTime(RgbEffects.Now, interval);
            try
            {
                if (ready && (applied != version || (animated && rgbClock.IsDue(frame, interval))) &&
                    Stopwatch.GetElapsedTime(lastApply) >= minInterval)
                {
                    bool changed = applied != version;
                    applied = version;
                    lastApply = Stopwatch.GetTimestamp();
                    bool? ok = rgb.SyncRam && _ramRgb.IsAvailable ? SafeCall(() => Apply(_ramRgb, rgb, RgbTargets.Ram, frame)) : null;
                    if (animated) rgbClock.RecordFrame(frame, interval, ok == true ? Stopwatch.GetElapsedTime(lastApply) : null);
                    if (changed || _rgbRamOk != ok) lock (_stateLock) _rgbRamOk = ok;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"RGB da RAM: {ex.Message}");
            }

            var wait = !ready ? TimeSpan.FromMilliseconds(200)
                : applied != Interlocked.Read(ref _rgbVersion) ? minInterval - Stopwatch.GetElapsedTime(lastApply)
                : animated ? RgbFrameWait(rgbClock, interval, lastApply, minInterval)
                : TimeSpan.FromSeconds(5);
            try { await WaitAsync(_ramSignal, Clamp(wait), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private RgbConfig? CurrentRgb
    {
        get { lock (_configLock) return _config.Rgb; }
    }

    private bool HasAuxRgb(RgbConfig rgb) =>
        (rgb.SyncGpu && _gpuRgb.IsAvailable) || (rgb.SyncMotherboard && _mbRgb.IsAvailable) ||
        (rgb.SyncPeripherals && _peripheralsRgb.IsAvailable);

    private static TimeSpan RgbFrameWait(RgbFrameClock clock, int intervalMs, long lastSend, TimeSpan minInterval)
    {
        var frameWait = clock.UntilNextFrame(RgbEffects.Now, intervalMs);
        var rateWait = minInterval - Stopwatch.GetElapsedTime(lastSend);
        return frameWait > rateWait ? frameWait : rateWait;
    }

    private byte[] _hubFrame = Array.Empty<byte>();

    /// <summary>Um quadro do efeito nas ventoinhas: igual em todas, ou espalhado LED a LED pela corrente.</summary>
    private bool ApplyHubFrame(RgbConfig rgb, long frame)
    {
        if (!rgb.SpreadOnFans)
        {
            var o = rgb.OutputFor(RgbTargets.Hub, frame, 0);
            return _hub.SetGlobalRgb(o.R, o.G, o.B, o.Brightness);
        }

        int devices = Math.Max(_hub.SubDevices.Count, 1);
        int leds = Math.Max(devices * CorsairLinkHub.LedsPerDevice, 400);
        if (_hubFrame.Length != leds * 3) _hubFrame = new byte[leds * 3];
        var (_, _, _, brightness) = rgb.Finish(RgbTargets.Hub, 0, 0, 0);
        for (int i = 0; i < leds; i++)
        {
            double phase = RgbEffects.LedPhase(rgb.Effect, i, CorsairLinkHub.LedsPerDevice, devices);
            var (r, g, b, level) = RgbEffects.ColorAt(rgb, frame, phase);
            var (cr, cg, cb, _) = rgb.Finish(RgbTargets.Hub, r, g, b);
            _hubFrame[i * 3] = (byte)(cr * level);
            _hubFrame[i * 3 + 1] = (byte)(cg * level);
            _hubFrame[i * 3 + 2] = (byte)(cb * level);
        }
        return _hub.SetRgbLeds(_hubFrame, brightness);
    }

    /// <summary>O anel do LCD acompanha a cor do efeito no momento do quadro.</summary>
    private static RgbConfig RgbForLcd(RgbConfig rgb)
    {
        if (!rgb.IsAnimated) return rgb;
        var (r, g, b, _) = RgbEffects.ColorAt(rgb, RgbEffects.Now);
        return new RgbConfig { R = r, G = g, B = b, Brightness = rgb.Brightness, SyncLcd = rgb.SyncLcd };
    }

    private string BuildRgbStatus(RgbConfig rgb)
    {
        if (!_rgbReady) return "Detectando dispositivos RGB...";
        var ok = new List<string>();
        var fail = new List<string>();
        void Add(bool enabled, bool? r, string name)
        {
            if (!enabled) return;
            if (r == true) ok.Add(name);
            else if (r == false) fail.Add(name);
        }
        Add(rgb.SyncHub, _rgbHubOk, "Fans/Bomba");
        Add(rgb.SyncGpu, _rgbGpuOk, "GPU");
        Add(rgb.SyncMotherboard, _rgbMbOk, "Placa-mãe");
        Add(rgb.SyncRam, _rgbRamOk, "RAM DDR5");
        Add(rgb.SyncPeripherals, _rgbPeripheralsOk, "Periféricos");
        Add(rgb.SyncLcd, _rgbLcdOk, "LCD");
        if (ok.Count == 0 && fail.Count == 0) return "Nenhum dispositivo sincronizado";
        var s = ok.Count > 0 ? $"Sincronizado: {string.Join(", ", ok)}" : string.Empty;
        if (fail.Count > 0) s += (s.Length > 0 ? " • " : string.Empty) + $"Falhou: {string.Join(", ", fail)}";
        return s;
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
