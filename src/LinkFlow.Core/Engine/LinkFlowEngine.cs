using System.Diagnostics;

namespace LinkFlow.Core;

public sealed class EngineOptions
{
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan SpeedReassertInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan HubRgbMinInterval { get; set; } = TimeSpan.FromMilliseconds(60);
    public TimeSpan AuxRgbMinInterval { get; set; } = TimeSpan.FromMilliseconds(100);
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

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _hubSignal = new(0, 1);
    private readonly SemaphoreSlim _sensorSignal = new(0, 1);
    private readonly SemaphoreSlim _auxSignal = new(0, 1);
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
    private bool? _rgbHubOk, _rgbGpuOk, _rgbMbOk, _rgbLcdOk;
    private readonly Dictionary<string, EngineAlert> _alerts = new();

    // Controle (escritos pela fila de sensores, lidos pela fila do hub)
    private volatile int _curveOutput = -1;
    private volatile bool _failSafe;
    private float? _curveInputTemp;
    private int _lcdRequested;
    private int _forceReconnectHub;
    private int _forceReconnectAux;
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
        _lanes.Add(RunLane("snapshot", () => PublishLoopAsync(ct)));
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
                EffectiveGlobalPercent = EffectiveGlobal(cfg),
                CurveInputTemp = _curveInputTemp,
                FailSafeActive = _failSafe,
                Fans = _fans.Select(f => new FanState
                {
                    Key = f.Key, Channel = f.Channel, DeviceId = f.DeviceId, DefaultName = f.DefaultName,
                    Model = f.Model, IsPump = f.IsPump, Rpm = f.Rpm, AppliedPercent = f.AppliedPercent, Temperature = f.Temperature
                }).ToList(),
                LcdConnected = _lcdConnected,
                GpuRgbAvailable = _gpuRgb.IsAvailable,
                GpuRgbName = _gpuRgb.Name,
                MotherboardRgbAvailable = _mbRgb.IsAvailable,
                MotherboardRgbName = _mbRgb.Name,
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
        long nextRead = 0, nextReconnect = 0, lastSpeedApply = 0, lastRgbApply = 0;
        long appliedRgbVersion = -1;
        int failures = 0;
        Dictionary<int, byte>? lastSent = null;
        var zeroReads = new Dictionary<string, int>();

        while (!ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            try
            {
                if (Interlocked.Exchange(ref _forceReconnectHub, 0) == 1 && _hub.IsConnected)
                {
                    _hub.Disconnect(restoreHardwareMode: false);
                    nextReconnect = 0;
                }

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

                    // 3. RGB do hub (limitado a ~16 atualizações/s durante arrastes)
                    long rgbVersion = Interlocked.Read(ref _rgbVersion);
                    if (appliedRgbVersion != rgbVersion && Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.HubRgbMinInterval)
                    {
                        appliedRgbVersion = rgbVersion;
                        lastRgbApply = Stopwatch.GetTimestamp();
                        if (cfg.Rgb.SyncHub)
                        {
                            bool ok = _hub.SetGlobalRgb(cfg.Rgb.R, cfg.Rgb.G, cfg.Rgb.B, cfg.Rgb.Brightness);
                            lock (_stateLock) _rgbHubOk = ok;
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
        foreach (var dev in _hub.SubDevices)
        {
            var ov = cfg.GetFan(FanState.KeyFor(dev));
            byte p = failSafe ? (byte)100 : ov.Linked ? global : ov.Percent;
            if (dev.IsPump) p = Math.Max((byte)FanLimits.PumpMinPercent, p);
            result[dev.Channel] = Math.Clamp(p, (byte)FanLimits.MinPercent, (byte)100);
        }
        return result;
    }

    private static bool SameSpeeds(Dictionary<int, byte> a, Dictionary<int, byte> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    // ------------------------------------------------------------------ Fila de sensores

    private async Task SensorLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        try { _sensors.Open(); }
        catch (Exception ex) { Log.Warn($"Sensores indisponíveis: {ex.Message}"); }
        lock (_stateLock) _sensorsReady = true;

        var curve = new CurveController();
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
                    float? coolant;
                    lock (_stateLock)
                    {
                        _telemetry = t;
                        coolant = _coolant;
                    }
                    lastTemp = CurveController.SelectTemp(cfg.Curve.Source,
                        t.CpuSensorAvailable ? t.CpuTemperature : null, t.GpuTemperature, coolant);
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
        lock (_stateLock) _rgbReady = true;
        Log.Info($"RGB: GPU={(_gpuRgb.IsAvailable ? _gpuRgb.Name : "não detectada")} • Placa-mãe={(_mbRgb.IsAvailable ? _mbRgb.Name : "não detectada")}");

        using var renderer = new LcdRenderer();
        long appliedRgbVersion = -1, lastRgbApply = 0, nextFrame = 0, nextLcdReconnect = 0;

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

                // RGB da GPU e placa-mãe
                long rgbVersion = Interlocked.Read(ref _rgbVersion);
                if (appliedRgbVersion != rgbVersion && Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.AuxRgbMinInterval)
                {
                    appliedRgbVersion = rgbVersion;
                    lastRgbApply = Stopwatch.GetTimestamp();
                    var rgb = cfg.Rgb;
                    bool? gpuOk = rgb.SyncGpu && _gpuRgb.IsAvailable ? SafeCall(() => _gpuRgb.SetColor(rgb.R, rgb.G, rgb.B, rgb.Brightness)) : null;
                    bool? mbOk = rgb.SyncMotherboard && _mbRgb.IsAvailable ? SafeCall(() => _mbRgb.SetColor(rgb.R, rgb.G, rgb.B, rgb.Brightness)) : null;
                    lock (_stateLock)
                    {
                        _rgbGpuOk = gpuOk;
                        _rgbMbOk = mbOk;
                    }
                    if (rgb.SyncLcd) Interlocked.Exchange(ref _lcdRequested, 1);
                }

                // LCD
                if (!_lcd.IsConnected && now >= nextLcdReconnect)
                {
                    SafeCall(() => _lcd.Connect());
                    nextLcdReconnect = now + Ticks(_opt.ReconnectInterval);
                }
                lock (_stateLock) _lcdConnected = _lcd.IsConnected;

                bool requested = Interlocked.Exchange(ref _lcdRequested, 0) == 1;
                if (_lcd.IsConnected && (requested || (cfg.Lcd.AutoStream && now >= nextFrame)))
                {
                    nextFrame = now + Ticks(_opt.LcdFrameInterval);
                    var jpeg = renderer.Render(BuildLcdFrame(cfg));
                    bool ok = _lcd.SendJpegFrame(jpeg);
                    lock (_stateLock)
                    {
                        _lcdConnected = _lcd.IsConnected;
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
            await WaitAsync(_auxSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    private LcdFrameData BuildLcdFrame(ControlConfig cfg)
    {
        lock (_stateLock)
        {
            var t = _telemetry;
            var critical = _alerts.Values.FirstOrDefault(a => a.Severity == AlertSeverity.Critical);
            string status = _failSafe ? "MODO SEGURANÇA" : critical != null ? "ALERTA" : "OPERACIONAL";
            var (r, g, b) = cfg.Rgb.SyncLcd ? (cfg.Rgb.R, cfg.Rgb.G, cfg.Rgb.B) : ((byte)0, (byte)240, (byte)255);
            return new LcdFrameData(
                t.CpuSensorAvailable ? t.CpuTemperature : null,
                t.GpuTemperature,
                _coolant,
                _fans.FirstOrDefault(f => f.IsPump)?.Rpm,
                r, g, b, status, _failSafe || critical != null);
        }
    }

    private string BuildRgbStatus(RgbConfig rgb)
    {
        if (!_rgbReady) return "Detectando dispositivos RGB...";
        var ok = new List<string>();
        var fail = new List<string>();
        void Add(bool? r, string name) { if (r == true) ok.Add(name); else if (r == false) fail.Add(name); }
        Add(_rgbHubOk, "Fans/Bomba");
        Add(_rgbGpuOk, "GPU");
        Add(_rgbMbOk, "Placa-mãe");
        Add(_rgbLcdOk, "LCD");
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
        foreach (var d in new IDisposable[] { _lcd, _gpuRgb, _mbRgb, _sensors })
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
