using System.Diagnostics;
using System.Globalization;

namespace LinkFlow.Core;

public sealed class EngineOptions
{
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Depois de voltar da suspensão o USB do hub leva alguns segundos para reaparecer: nesse período o hub é
    /// procurado a cada <see cref="ResumeReconnectInterval"/> (e não a cada <see cref="ReconnectInterval"/>), sem o
    /// alerta de hub perdido.
    /// </summary>
    public TimeSpan ResumeGracePeriod { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan ResumeReconnectInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Avisos de retorno da suspensão dentro deste intervalo contam como um só. O Windows manda dois ao serviço
    /// (ResumeAutomatic e ResumeSuspend) e a interface repassa o dela: eram três reconexões seguidas do hub.
    /// </summary>
    public TimeSpan ResumeCoalesceWindow { get; set; } = TimeSpan.FromSeconds(3);
    public TimeSpan SubDeviceCheckInterval { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SpeedReassertInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Coalesce normal speed changes; safety transitions bypass this limit.</summary>
    public TimeSpan SpeedApplyMinInterval { get; set; } = TimeSpan.FromMilliseconds(50);
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

    /// <summary>After this age a successfully acquired value is no longer published as current.</summary>
    public TimeSpan TelemetryFreshnessTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>"Testar curva" termina sozinho se a interface parar de renovar o pedido (ex.: fechou ou travou).</summary>
    public TimeSpan CurveTestTimeout { get; set; } = TimeSpan.FromSeconds(6);

    // O firmware do hub muda a rotação em rampa linear de ~45 RPM/s (medido com RX RGB, FW 4.1.656):
    // de 50% a 100% leva ~23 s, e de 100% a 40% ~30 s. Os tempos abaixo partem disso.

    /// <summary>Calibração: espera mínima em cada potência antes de ler o RPM (a rampa começar).</summary>
    public TimeSpan CalibrationSettle { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Calibração: estável = RPM igual (com folga) ao de pelo menos este tempo atrás. Na rampa, 2 s dão ~90 RPM.</summary>
    public TimeSpan CalibrationStableWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Calibração: espera máxima pela rotação estabilizar (depois, usa a última leitura) ou pelo hub aplicar a potência.</summary>
    public TimeSpan CalibrationSettleMax { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Calibração: sem nenhuma leitura nova de RPM por este tempo, o hub parou de responder e ela é interrompida.</summary>
    public TimeSpan CalibrationReadTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Desgaste: leituras seguidas abaixo do esperado pela calibração antes do alerta (~1 por segundo).</summary>
    public int FanWearReadsBeforeAlert { get; set; } = 60;

    /// <summary>Desgaste: só compara depois que a potência fica este tempo sem mudar (a rampa do hub leva até ~40 s).</summary>
    public TimeSpan FanWearSteadyTime { get; set; } = TimeSpan.FromSeconds(40);

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

    /// <summary>Retenção e coleta contínua, independentes da interface. Null usa os padrões do serviço.</summary>
    public OperationalHistoryOptions? History { get; set; }
    public Func<InventoryObservation>? InventoryProvider { get; set; }
    public TimeSpan InventoryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Somente testes da assembly amiga; nunca vem de configuração, IPC ou argumentos do produto.</summary>
    internal string? HardwareLeaseNameForTests { get; set; }

    /// <summary>Programas que disputam o hub (iCUE, SignalRGB...). Null = processos reais, exceto na simulação.</summary>
    public Func<IReadOnlyList<string>>? ConflictProbe { get; set; }
    public TimeSpan ConflictCheckInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Supervisor de sincronismo do RGB: novas tentativas, reinicialização por dispositivo e ressincronização.</summary>
    public RgbSyncPolicy RgbSync { get; set; } = new();

    /// <summary>Arquivo onde a configuração é persistida. Null = não persiste (testes).</summary>
    public string? ConfigPath { get; set; } = AppPaths.ControlConfigFile;
}

/// <summary>
/// Núcleo do LinkFlow: dono exclusivo do hardware. Roda filas de trabalho independentes,
/// cada uma em sua própria thread de fundo, para que nada bloqueie a interface:
/// <list type="bullet">
/// <item><b>Hub</b>: leitura de RPM/temperaturas do iCUE LINK, aplicação de velocidades e RGB do hub.</item>
/// <item><b>Sensores</b>: LibreHardwareMonitor (CPU/GPU/RAM), curva automática, modo de segurança e fan da GPU.</item>
/// <item><b>Periféricos</b>: RGB da GPU, da placa-mãe e dos periféricos, quadros do LCD.</item>
/// <item><b>Memórias</b>: RGB dos pentes de memória, na cadência do controlador sem atrasar os outros dispositivos.</item>
/// <item><b>Snapshot</b>: publica o estado para as interfaces.</item>
/// </list>
/// A configuração é tratada como "último valor vence": arrastar um slider gera muitas atualizações,
/// mas cada fila aplica apenas o estado mais recente quando acorda.
/// </summary>
public sealed partial class LinkFlowEngine : IDisposable
{
    private const int GpuRgbResendAttempts = 3;
    private static readonly TimeSpan GpuRgbResendDelay = TimeSpan.FromMilliseconds(350);

    /// <summary>Barramento da GPU ocupado por mais que isto (as rajadas normais duram até ~4 s): vira aviso no log.</summary>
    private static readonly TimeSpan GpuRgbBusyWarnAfter = TimeSpan.FromSeconds(10);
    private readonly IHardwareFactory _factory;
    private HardwareControlLease? _hardwareOwnership;
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
    private readonly object _configurationSaveGate = new();
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
    private EngineText _hubStatus = EngineText.Of(EngineTexts.HubSearching);
    private List<FanState> _fans = new();
    private float? _coolant;
    private int? _coolantChannel;
    private IReadOnlyDictionary<int, float>? _hubTemperatures;
    private bool _hasHubSpeeds;
    private bool _lcdConnected;
    private bool _rgbReady;
    private bool? _rgbHubOk, _rgbGpuOk, _rgbMbOk, _rgbRamOk, _rgbPeripheralsOk, _rgbLcdOk;
    private bool _rgbGpuRecovering, _rgbMbRecovering, _rgbRamRecovering, _rgbPeripheralsRecovering;
    private readonly Dictionary<string, EngineAlert> _alerts = new();

    // Controle (escritos pela fila de sensores, lidos pela fila do hub)
    private volatile int _curveOutput = -1;
    private volatile bool _failSafe;
    private long? _failSafeRecoveryStarted;
    private EngineText? _failSafeReason;
    private float? _curveTestTemp;
    private long _curveTestUntil;
    private readonly HashSet<string> _failSafeCauses = new();
    private readonly HashSet<int> _failSafeCoolantChannels = new();
    private float? _curveInputTemp;
    private int _lcdRequested;
    private int _forceReconnectHub;
    private int _resumeHub;
    private int _forceReconnectAux;
    private int _rescanRgb;
    private long _rgbResyncGeneration;
    private long _lastRgbResyncRequest;
    private EngineText? _lcdStatus;

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
            var title = EngineText.Of(_store.RecoveredFromBackup ? EngineTexts.ConfigRecoveredTitle : EngineTexts.ConfigResetTitle);
            var message = EngineText.Of(_store.RecoveredFromBackup ? EngineTexts.ConfigRecovered : EngineTexts.ConfigReset);
            _alerts["config-recovered"] = new EngineAlert
            {
                Key = "config-recovered",
                Severity = AlertSeverity.Warning,
                Title = title.ToPortuguese(),
                Message = message.ToPortuguese(),
                TitleText = title,
                MessageText = message,
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
        _safetySupervisor = new Timer(SuperviseSafety, null, Timeout.Infinite, Timeout.Infinite);
        InitializeObservability();
        MarkConfigurationPending(_config.Revision);
        if (initialConfig == null && _store is { LoadedFromFallback: false } && File.Exists(_store.FilePath))
            ReportConfigurationPersistence(_config.Revision, true);
    }

    private readonly Timer _watchdog;

    // ------------------------------------------------------------------ API pública

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) == 1, this);
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            if (!IsSimulated) _hardwareOwnership = _opt.HardwareLeaseNameForTests is { } testName
                ? HardwareControlLease.AcquireForTests(testName)
                : HardwareControlLease.Acquire();
        }
        catch { Interlocked.Exchange(ref _started, 0); throw; }
        if (Volatile.Read(ref _stopped) == 1)
        {
            _hardwareOwnership?.Dispose();
            _hardwareOwnership = null;
            throw new ObjectDisposedException(nameof(LinkFlowEngine));
        }
        _engineStartedAt = Stopwatch.GetTimestamp();
        InitializeLaneBeats(_engineStartedAt);
        var ct = _cts.Token;
        _lanes.Add(RunLane("hub", () => HubLoopAsync(ct)));
        _lanes.Add(RunLane("sensores", () => SensorLoopAsync(ct)));
        _lanes.Add(RunLane("periféricos", () => AuxLoopAsync(ct)));
        _lanes.Add(RunLane("memórias", () => RamLoopAsync(ct)));
        _lanes.Add(RunLane("snapshot", () => PublishLoopAsync(ct)));
        // The internal Local/GUID ownership fixture uses fake adapters and must not initialize HidSharp either.
        if (!IsSimulated && _opt.HardwareLeaseNameForTests == null) WatchUsb();
        var period = WatchdogPeriod;
        Interlocked.Exchange(ref _lastLaneCheck, Stopwatch.GetTimestamp()); // suspensão antes da primeira verificação
        _watchdog.Change(period, period);
        _safetySupervisor.Change(SafetyPeriod, SafetyPeriod);
        StartAutomation();
        StartInventory();
        if (_store != null && Persistence.State != PersistenceState.Saved)
            _saveTimer.Change(_opt.SaveDebounce, Timeout.InfiniteTimeSpan);
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
        lock (_configLock) return _config.Clone();
    }

    /// <summary>Aplica uma alteração sobre uma cópia da configuração atual (último valor vence).</summary>
    public void UpdateConfig(Action<ControlConfig> mutate, object? origin = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) == 1, this);
        ControlConfig updated;
        lock (_configLock)
        {
            var next = _config.Clone();
            mutate(next);
            next.Normalize();
            updated = Commit(next);
        }
        AfterConfigChange(updated, origin);
    }

    public void ReplaceConfig(ControlConfig config, object? origin = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) == 1, this);
        ControlConfig updated;
        lock (_configLock)
        {
            var next = config.Clone();
            next.Normalize();
            updated = Commit(next);
        }
        AfterConfigChange(updated, origin);
    }

    private ControlConfig Commit(ControlConfig next)
    {
        var prev = _config;
        if (prev.Revision == long.MaxValue) throw new InvalidOperationException("Limite de revisões atingido.");
        next.Revision = prev.Revision + 1;
        if (!prev.Rgb.SameOutputAs(next.Rgb)) Interlocked.Increment(ref _rgbVersion);
        if (!prev.Rgb.SyncGpu && next.Rgb.SyncGpu) _gpuRgbResendVersion++;
        if (prev.GpuFanAuto != next.GpuFanAuto || prev.GpuFanPercent != next.GpuFanPercent) Interlocked.Increment(ref _gpuFanVersion);
        _config = next;
        Interlocked.Increment(ref _configVersion);
        return next;
    }

    private void AfterConfigChange(ControlConfig updated, object? origin)
    {
        MarkConfigurationPending(updated.Revision);
        Signal(_hubSignal);
        Signal(_sensorSignal);
        Signal(_auxSignal);
        Signal(_ramSignal);
        if (_store != null) _saveTimer.Change(_opt.SaveDebounce, Timeout.InfiniteTimeSpan);

        var handler = ConfigChanged;
        if (handler != null)
        {
            var copy = updated.Clone();
            foreach (Action<ControlConfig, object?> observer in handler.GetInvocationList())
                try { observer(copy, origin); } catch (Exception ex) { Log.Warn($"ConfigChanged handler: {ex.Message}"); }
        }
    }

    public void RequestLcdFrame()
    {
        Interlocked.Exchange(ref _lcdRequested, 1);
        Signal(_auxSignal);
        Signal(_ramSignal);
    }

    private long _lastResume;

    /// <summary>Força reconexão de todos os dispositivos (ex.: ao voltar da suspensão). Avisos repetidos contam uma vez.</summary>
    public void NotifyResume()
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastResume);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < _opt.ResumeCoalesceWindow)
        {
            Log.Debug("Aviso repetido de retorno da suspensão ignorado.");
            return;
        }
        if (Interlocked.CompareExchange(ref _lastResume, now, last) != last) return; // outro aviso simultâneo já tratou

        Log.Info("Retorno da suspensão: reconectando dispositivos.");
        // O processo ficou parado durante a suspensão: a última batida das filas é de antes dela
        ResetLaneBeats(now);
        lock (_stateLock) _failSafeRecoveryStarted = null;
        InvalidateAcquisitionsForResume();
        Interlocked.Exchange(ref _resumeHub, 1);
        Interlocked.Exchange(ref _forceReconnectHub, 1);
        Interlocked.Exchange(ref _forceReconnectAux, 1);
        Interlocked.Increment(ref _rgbVersion);
        Interlocked.Increment(ref _gpuFanVersion);
        RequestRgbResync("retorno da suspensão");
        Signal(_hubSignal);
        Signal(_sensorSignal);
        Signal(_auxSignal);
        Signal(_ramSignal);
    }

    /// <summary>
    /// Pede ao supervisor uma ressincronização de todos os dispositivos RGB (além do reenvio imediato): um envio da cor
    /// atual alguns segundos depois, quando os controladores já acordaram. Pedidos próximos viram uma linha só no log.
    /// </summary>
    private void RequestRgbResync(string reason)
    {
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Exchange(ref _lastRgbResyncRequest, now);
        if (last == 0 || Stopwatch.GetElapsedTime(last, now) > TimeSpan.FromSeconds(10))
            Log.Info($"RGB: ressincronizando todos os dispositivos ({reason}).");
        Interlocked.Increment(ref _rgbResyncGeneration);
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
    /// "Testar curva": as curvas passam a usar <paramref name="temperature"/> no lugar da leitura real, sem histerese
    /// nem suavização, para ouvir o ruído de cada ponto. Vale por <see cref="EngineOptions.CurveTestTimeout"/>;
    /// a interface renova enquanto o teste está aberto. null encerra. O modo de segurança continua usando as
    /// temperaturas reais e tem prioridade.
    /// </summary>
    public void SetCurveTest(float? temperature)
    {
        lock (_stateLock)
        {
            if (temperature is { } t && float.IsFinite(t))
            {
                if (_curveTestTemp == null) Log.Info("Teste de curva iniciado pela interface.");
                _curveTestTemp = Math.Clamp(t, 0f, 110f);
                _curveTestUntil = Stopwatch.GetTimestamp() + (long)(_opt.CurveTestTimeout.TotalSeconds * Stopwatch.Frequency);
            }
            else
            {
                if (_curveTestTemp != null) Log.Info("Teste de curva encerrado.");
                _curveTestTemp = null;
            }
        }
        Signal(_sensorSignal);
    }

    /// <summary>Temperatura do teste de curva em andamento (null = sem teste ou expirado).</summary>
    private float? ActiveCurveTest()
    {
        lock (_stateLock)
        {
            if (_curveTestTemp != null && Stopwatch.GetTimestamp() > _curveTestUntil)
            {
                Log.Info("Teste de curva expirado: voltando às temperaturas reais.");
                _curveTestTemp = null;
            }
            return _curveTestTemp;
        }
    }

    /// <summary>
    /// Procura de novo todos os dispositivos: reabre o hub (relê a lista de ventoinhas/bomba),
    /// reconecta o LCD e tenta detectar o RGB da GPU/placa-mãe que não foi encontrado.
    /// </summary>
    public void RescanDevices()
    {
        Log.Info("Reatualizando dispositivos a pedido do usuário.");
        lock (_stateLock) _hubStatus = EngineText.Of(EngineTexts.HubSearchingDevices);
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
        var automation = GetAutomationState();
        var persistence = Persistence;
        var operations = GetCommandReceipts();
        var historyStorage = History.Status;
        var inventory = Volatile.Read(ref _inventorySnapshot);
        // One immutable cached read, outside engine locks; the provider never calls the native backend here.
        var gpuControl = CaptureGpuRgbControlStatus();

        lock (_stateLock)
        {
            var t = _telemetry;
            var rgbStatus = BuildRgbStatus(cfg.Rgb, gpuControl);
            return new EngineSnapshot
            {
                Timestamp = DateTime.Now,
                ConfigRevision = cfg.Revision,
                Persistence = persistence,
                Operations = operations.ToList(),
                Automation = automation,
                HistoryStorage = historyStorage,
                Inventory = inventory,
                IsSimulated = IsSimulated,
                TelemetryHealth = SnapshotTelemetryHealth(),
                CurveRequestedSource = cfg.Curve.Source,
                CurveEffectiveSource = _curveSelection.EffectiveSource,
                CurveUsingFallback = _curveSelection.IsFallback,
                HubConnected = _hubConnected,
                HubFirmware = _hubFirmware,
                HubStatus = _hubStatus.ToPortuguese(),
                HubStatusText = _hubStatus,
                SensorsReady = _sensorsReady,
                CpuSensorAvailable = t.CpuSensorAvailable && IsFresh("cpu.temp"),
                CpuName = t.CpuName,
                GpuName = t.GpuName,
                CpuTemp = FreshValue("cpu.temp", t.CpuTemperature),
                CpuLoad = FreshValue("cpu.load", t.CpuLoad),
                GpuTemp = FreshValue("gpu.temp", t.GpuTemperature),
                GpuHotspot = FreshValue("gpu.hotspot", t.GpuHotspotTemperature),
                GpuLoad = FreshValue("gpu.load", t.GpuLoad),
                RamUsage = FreshValue("ram.usage", t.RamUsagePercent),
                RamUsedGb = FreshValue("ram.used", t.RamUsedGb),
                RamTotalGb = FreshValue("ram.total", t.RamTotalGb),
                CoolantTemp = FreshValue("coolant.temp", _coolant),
                GpuFanRpm1 = FreshInt("gpu.fan1", t.GpuFanRpm1),
                GpuFanRpm2 = FreshInt("gpu.fan2", t.GpuFanRpm2),
                GpuFanPercent = FreshValue("gpu.fan_percent", t.GpuFanPercent),
                CpuPower = FreshValue("cpu.power", t.CpuPower),
                CpuClockMhz = FreshValue("cpu.clock", t.CpuClockMhz),
                CpuCores = t.CpuCores,
                GpuPower = FreshValue("gpu.power", t.GpuPower),
                GpuClockMhz = FreshValue("gpu.clock", t.GpuClockMhz),
                GpuMemClockMhz = FreshValue("gpu.memory_clock", t.GpuMemClockMhz),
                GpuVramUsedGb = FreshValue("gpu.vram_used", t.GpuVramUsedGb),
                GpuVramTotalGb = FreshValue("gpu.vram_total", t.GpuVramTotalGb),
                CpuCoreLoads = IsFresh("cpu.load") ? t.CpuCoreLoads : null,
                Network = IsFresh("network") ? _network : null,
                Volumes = _volumes.Where(volume => IsFresh($"vol:{volume.Letter}")).ToList(),
                Disks = _disks?.Where(disk => IsFresh($"disk:{disk.Index}")).ToList(),
                LcdStatus = _lcdStatus?.ToPortuguese(),
                LcdStatusText = _lcdStatus,
                EffectiveGlobalPercent = EffectiveGlobal(cfg),
                GroupOutputs = new Dictionary<string, int>(_groupOutputs),
                CurveInputTemp = _curveTestTemp != null || _curveSelection.Temperature != null ? _curveInputTemp : null,
                FailSafeActive = _failSafe,
                FailSafeReason = _failSafe ? _failSafeReason?.ToPortuguese() : null,
                FailSafeReasonText = _failSafe ? _failSafeReason : null,
                FailSafeRecovering = _failSafe && _failSafeRecoveryStarted != null,
                CurveTestTemp = _curveTestTemp,
                Calibration = _calibrationStatus,
                Fans = _fans.Select(f => SnapshotFan(f, cfg)).ToList(),
                LcdConnected = _lcdConnected,
                GpuRgbControl = gpuControl,
                GpuRgbAvailable = gpuControl?.ControlAvailable ?? _gpuRgb.IsAvailable,
                GpuRgbRetrying = cfg.Rgb.SyncGpu && (gpuControl?.InitializationRetryNeeded ?? _gpuRgb.NeedsInitializationRetry),
                GpuRgbName = gpuControl?.ControllerName ?? _gpuRgb.Name,
                GpuRgbApplied = !cfg.Rgb.SyncGpu ? null : gpuControl == null ? _rgbGpuOk
                    : gpuControl.ControlAvailable ? gpuControl.LastOutputAccepted : false,
                GpuRgbDiagnostic = gpuControl == null ? _gpuRgb.Diagnostic : gpuControl.Diagnostic,
                GpuRgbDiagnosticText = gpuControl == null ? _gpuRgb.DiagnosticText : gpuControl.DiagnosticText,
                MotherboardRgbAvailable = _mbRgb.IsAvailable,
                MotherboardRgbName = _mbRgb.Name,
                RamRgbAvailable = _ramRgb.IsAvailable,
                RamRgbName = _ramRgb.Name,
                PeripheralsRgbAvailable = _peripheralsRgb.IsAvailable,
                PeripheralsRgbName = _peripheralsRgb.Name,
                PeripheralsRgbApplied = cfg.Rgb.SyncPeripherals ? _rgbPeripheralsOk : null,
                PeripheralsRgbDiagnostic = _peripheralsRgb.Diagnostic,
                PeripheralsRgbDiagnosticText = _peripheralsRgb.DiagnosticText,
                RgbDevicesReady = _rgbReady,
                RgbStatusText = rgbStatus,
                RgbStatus = rgbStatus.ToPortuguese(),
                Alerts = _alerts.Values.OrderByDescending(a => a.Severity).ThenBy(a => a.Since).Select(a => new EngineAlert
                {
                    Key = a.Key, Severity = a.Severity, Title = a.Title, Message = a.Message, Since = a.Since,
                    TitleText = a.TitleText, MessageText = a.MessageText
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
            var snapshot = GetSnapshot();
            PublishObservability(snapshot);
            var handler = SnapshotUpdated;
            if (handler == null) continue;
            foreach (Action<EngineSnapshot> observer in handler.GetInvocationList())
                try { observer(snapshot); }
                catch (Exception ex) { Log.Warn($"SnapshotUpdated handler: {ex.Message}"); }
        }
    }

    /// <summary>Chamar com _stateLock adquirido.</summary>
    private void RaiseAlert(string key, AlertSeverity severity, EngineText titleText, EngineText messageText)
    {
        string title = titleText.ToPortuguese(), message = messageText.ToPortuguese();
        if (_alerts.TryGetValue(key, out var existing))
        {
            bool escalated = severity > existing.Severity;
            existing.Severity = severity;
            existing.Title = title;
            existing.Message = message;
            existing.TitleText = titleText;
            existing.MessageText = messageText;
            if (!escalated) return;
        }
        else
        {
            existing = new EngineAlert
            {
                Key = key, Severity = severity, Title = title, Message = message, TitleText = titleText, MessageText = messageText,
                Since = DateTime.Now
            };
            _alerts[key] = existing;
        }

        Log.Warn($"ALERTA [{severity}] {title}: {message}");
        var copy = new EngineAlert
        {
            Key = key, Severity = severity, Title = title, Message = message, TitleText = titleText, MessageText = messageText,
            Since = existing.Since
        };
        var handler = AlertRaised;
        if (handler != null) ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Action<EngineAlert> observer in handler.GetInvocationList())
                try { observer(copy); } catch (Exception ex) { Log.Debug($"AlertRaised handler: {ex.Message}"); }
        });
    }

    /// <summary>Chamar com _stateLock adquirido.</summary>
    private void ClearAlert(string key)
    {
        if (_alerts.Remove(key)) RecordAlertResolution(key);
    }

    // ------------------------------------------------------------------ Encerramento e utilidades

    private void SaveNow()
    {
        if (_store == null) return;
        if (!IsSimulated && _hardwareOwnership == null) return;
        // A cópia deve ser capturada dentro do mesmo gate da gravação: callbacks sobrepostos
        // não podem gravar uma revisão antiga depois da revisão mais recente.
        lock (_configurationSaveGate)
        {
            ControlConfig cfg;
            lock (_configLock) cfg = _config;
            ReportConfigurationPersistence(cfg.Revision, _store.Save(cfg));
        }
    }

    /// <param name="restoreHardwareMode">Devolve o hub ao modo hardware (curva interna) e a GPU ao vBIOS.</param>
    public async Task StopAsync(bool restoreHardwareMode = true)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
        _safetySupervisor.Change(Timeout.Infinite, Timeout.Infinite);
        _cts.Cancel();
        if (_automationTimer != null)
        {
            try { await _automationTimer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException) { Log.Warn("Automação ainda finalizando durante o encerramento."); }
        }
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        // Sem adquirir a posse, este engine nunca deve chamar nenhum controle/restauração real.
        bool ownsHardware = IsSimulated || _hardwareOwnership != null;
        var cleanup = new List<Task>();
        if (ownsHardware)
        {
            cleanup.Add(Task.Run(() => SafeCall(() => { _hub.Disconnect(restoreHardwareMode); return true; })));
            cleanup.Add(Task.Run(() =>
            {
                if (restoreHardwareMode) SafeCall(() => _sensors.SetGpuFanAuto());
                try { _sensors.Dispose(); } catch (Exception ex) { Log.Debug($"Encerrando sensores: {ex.Message}"); }
            }));
            foreach (var device in new IDisposable[] { _lcd, _gpuRgb, _mbRgb, _ramRgb, _peripheralsRgb })
                cleanup.Add(Task.Run(() =>
                {
                    try { device.Dispose(); }
                    catch (Exception ex)
                    {
                        Log.Debug($"Encerrando dispositivo: {ex.Message}");
                        // A failed termination does not confirm that this controller relinquished hardware.
                        // Fault the cleanup task so ownership stays held until the process exits.
                        throw;
                    }
                }));
        }
        bool cleanupFinished = false;
        try
        {
            await Task.WhenAll(_lanes.Concat(cleanup)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            cleanupFinished = true;
        }
        catch (Exception ex) { Log.Warn($"Filas/dispositivos não encerraram a tempo; posse mantida até o processo terminar: {ex.Message}"); }
        bool saveFinished = false;
        try { await Task.Run(SaveNow).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); saveFinished = true; }
        catch (TimeoutException) { Log.Warn("A gravação final de configuração não confirmou o término no prazo."); }
        if (cleanupFinished && saveFinished)
        {
            _hardwareOwnership?.Dispose();
            _hardwareOwnership = null;
        }
        try { await StopObservabilityAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { Log.Warn("O histórico ainda está finalizando suas gravações."); }
        Log.Info($"Engine encerrado (restauração de hardware {(restoreHardwareMode ? "solicitada" : "dispensada")}; filas e limpeza {(cleanupFinished ? "concluídas" : "pendentes")}).");
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        _watchdog.Dispose();
        _safetySupervisor.Dispose();
        _saveTimer.Dispose();
        _cts.Dispose();
    }

    private static bool Write(IRgbDevice device, (byte R, byte G, byte B, byte Brightness) o, bool animated) => animated
        ? device.SetAnimationColor(o.R, o.G, o.B, o.Brightness)
        : device.SetColor(o.R, o.G, o.B, o.Brightness);

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
