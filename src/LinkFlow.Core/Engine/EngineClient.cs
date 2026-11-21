namespace LinkFlow.Core;

/// <summary>
/// Acesso da interface ao engine. Pode ser local (engine dentro do próprio app) ou remoto
/// (serviço do Windows via named pipe). A interface nunca fala com o hardware diretamente.
/// </summary>
public interface IEngineClient : IDisposable
{
    bool IsRemote { get; }
    bool IsConnected { get; }
    bool IsSimulated { get; }
    string ModeDescription { get; }

    ControlConfig Config { get; }
    EngineSnapshot? Snapshot { get; }
    IReadOnlyList<CommandReceipt> Operations => [];
    ConfigurationPersistence Persistence => new();
    AutomationState Automation => new();
    bool CanWrite => IsConnected;
    TimeSpan? SnapshotAge => Snapshot == null ? null : DateTime.Now - Snapshot.Timestamp;
    bool IsSnapshotFresh => SnapshotAge is { } age && age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(5);
    Version? ServiceVersion => null;
    int? NegotiatedProtocolVersion => null;
    event Action<CommandReceipt>? CommandUpdated { add { } remove { } }
    string SubmitCommand(EngineCommandRequest request) => throw new NotSupportedException();
    void SetSessionContext(AutomationSessionContext context) { }
    void SetAutomationOverride(DateTimeOffset? until) { }
    void ReportExternalOperation(WindowsOperationRecord record) { }
    Task<HistorySeries> QueryHistoryAsync(HistoryQuery query, CancellationToken ct = default) => Task.FromException<HistorySeries>(new NotSupportedException());
    Task<IReadOnlyList<OperationalEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default) => Task.FromException<IReadOnlyList<OperationalEvent>>(new NotSupportedException());
    Task<IReadOnlyList<HistorySession>> QuerySessionsAsync(CancellationToken ct = default) => Task.FromException<IReadOnlyList<HistorySession>>(new NotSupportedException());
    Task<SessionComparison> CompareSessionsAsync(HistoryQuery query, string baseline, string candidate, CancellationToken ct = default) => Task.FromException<SessionComparison>(new NotSupportedException());
    Task<string> ExportHistoryAsync(HistoryQuery query, bool csv, EventQuery? events = null, CancellationToken ct = default) => Task.FromException<string>(new NotSupportedException());

    /// <summary>Disparado em thread de fundo (~2x por segundo).</summary>
    event Action<EngineSnapshot>? SnapshotUpdated;

    /// <summary>Configuração alterada por outra origem (outro cliente/serviço). Não ecoa as próprias alterações.</summary>
    event Action<ControlConfig>? ConfigChanged;

    /// <summary>Qualquer alteração de configuração, inclusive as feitas por este cliente (thread qualquer).</summary>
    event Action<ControlConfig>? AnyConfigChanged;

    event Action<EngineAlert>? AlertRaised;
    event Action<bool>? ConnectionChanged;

    void Start();
    void UpdateConfig(Action<ControlConfig> mutate);
    void ReplaceConfig(ControlConfig config);
    void RequestLcdFrame();
    void NotifyResume();
    void RefreshRgb();

    /// <summary>Procura de novo ventoinhas, bomba, LCD e RGB.</summary>
    void RescanDevices();

    /// <summary>
    /// "Testar curva": as curvas usam esta temperatura em vez dos sensores por alguns segundos (renove enquanto
    /// o teste estiver aberto). null encerra o teste.
    /// </summary>
    void SetCurveTest(float? temperature);

    /// <summary>Inicia a calibração das ventoinhas (o progresso aparece em <see cref="EngineSnapshot.Calibration"/>).</summary>
    void StartCalibration();

    /// <summary>Interrompe a calibração em andamento; as ventoinhas voltam ao perfil.</summary>
    void CancelCalibration();

    /// <summary>Informa o que a interface está exibindo (ex.: discos), para o engine só ler o necessário.</summary>
    void SetSensorDemand(SensorDemand demand);

    /// <param name="restoreHardwareMode">Somente cliente local: devolve o hub ao modo hardware.</param>
    Task ShutdownAsync(bool restoreHardwareMode = true);
}

public sealed class LocalEngineClient : IEngineClient
{
    private readonly LinkFlowEngine _engine;
    private ControlConfig _config;
    private readonly object _lock = new();
    private long _lastSnapshotReceived;

    public LocalEngineClient(LinkFlowEngine engine)
    {
        _engine = engine;
        _config = engine.GetConfig();
        _engine.SnapshotUpdated += s =>
        {
            Snapshot = s;
            Volatile.Write(ref _lastSnapshotReceived, System.Diagnostics.Stopwatch.GetTimestamp());
            Notify(SnapshotUpdated, s, nameof(SnapshotUpdated));
        };
        _engine.AlertRaised += a => Notify(AlertRaised, a, nameof(AlertRaised));
        _engine.CommandUpdated += OnCommandUpdated;
        _engine.ConfigChanged += (c, origin) =>
        {
            lock (_lock) _config = c;
            if (!ReferenceEquals(origin, this)) Notify(ConfigChanged, c, nameof(ConfigChanged));
            Notify(AnyConfigChanged, c, nameof(AnyConfigChanged));
        };
    }

    public bool IsRemote => false;
    public bool IsConnected => true;
    public bool IsSimulated => _engine.IsSimulated;
    public string ModeDescription => _engine.IsSimulated ? "Simulação" : "Local";

    public ControlConfig Config
    {
        get { lock (_lock) return _config; }
    }

    public EngineSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<CommandReceipt> Operations => _engine.GetCommandReceipts();
    public ConfigurationPersistence Persistence => _engine.Persistence;
    public AutomationState Automation => _engine.GetAutomationState();
    public bool CanWrite => true;
    public TimeSpan? SnapshotAge => Volatile.Read(ref _lastSnapshotReceived) is var at && at != 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(at) : null;
    public bool IsSnapshotFresh => SnapshotAge is { } age && age <= TimeSpan.FromSeconds(5);
    public event Action<CommandReceipt>? CommandUpdated;
    private void OnCommandUpdated(CommandReceipt receipt) => Notify(CommandUpdated, receipt, nameof(CommandUpdated));

    public event Action<EngineSnapshot>? SnapshotUpdated;
    public event Action<ControlConfig>? ConfigChanged;
    public event Action<ControlConfig>? AnyConfigChanged;
    public event Action<EngineAlert>? AlertRaised;
#pragma warning disable CS0067 // sempre conectado
    public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067

    public void Start() => _engine.Start();
    public string SubmitCommand(EngineCommandRequest request)
    {
        var receipt = _engine.ExecuteCommand(request, this);
        // Um replay ou colisão de ID retorna um recibo existente sem emitir um novo evento no motor.
        Notify(CommandUpdated, receipt, nameof(CommandUpdated));
        return request.Id;
    }
    public void UpdateConfig(Action<ControlConfig> mutate)
    {
        var next = _engine.GetConfig();
        long revision = next.Revision;
        mutate(next);
        SubmitCommand(new() { Kind = Ipc.PipeProtocol.SetConfig, ExpectedRevision = revision, Config = next });
    }
    public void ReplaceConfig(ControlConfig config) => SubmitCommand(new() { Kind = Ipc.PipeProtocol.SetConfig, ExpectedRevision = Config.Revision, Config = config });
    private void Send(string kind, object? payload = null) => SubmitCommand(new() { Kind = kind, Payload = payload == null ? null : System.Text.Json.JsonSerializer.SerializeToNode(payload, Json.Compact) });
    public void RequestLcdFrame() => Send(Ipc.PipeProtocol.LcdFrame);
    public void NotifyResume() => Send(Ipc.PipeProtocol.Resume);
    public void RefreshRgb() => Send(Ipc.PipeProtocol.RefreshRgb);
    public void RescanDevices() => Send(Ipc.PipeProtocol.Rescan);
    public void SetCurveTest(float? temperature) => Send(Ipc.PipeProtocol.CurveTest, temperature);
    public void StartCalibration() => Send(Ipc.PipeProtocol.CalibrationStart);
    public void CancelCalibration() => Send(Ipc.PipeProtocol.CalibrationCancel);
    public void SetSessionContext(AutomationSessionContext context) => Send(Ipc.PipeProtocol.SessionContext, context);
    public void SetAutomationOverride(DateTimeOffset? until) => Send(Ipc.PipeProtocol.AutomationOverride, until);
    public void ReportExternalOperation(WindowsOperationRecord record) => Send(Ipc.PipeProtocol.ReportOperation, record);
    public Task<HistorySeries> QueryHistoryAsync(HistoryQuery query, CancellationToken ct = default) => Task.Run(() => _engine.History.Query(query), ct);
    public Task<IReadOnlyList<OperationalEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default) => Task.Run(() => _engine.History.QueryEvents(query), ct);
    public Task<IReadOnlyList<HistorySession>> QuerySessionsAsync(CancellationToken ct = default) => Task.Run(() => _engine.History.QuerySessions(), ct);
    public Task<SessionComparison> CompareSessionsAsync(HistoryQuery query, string baseline, string candidate, CancellationToken ct = default) => Task.Run(() => _engine.History.CompareSessions(query, baseline, candidate), ct);
    public Task<string> ExportHistoryAsync(HistoryQuery query, bool csv, EventQuery? events = null, CancellationToken ct = default) => Task.Run(() => csv ? _engine.History.ExportCsv(query) : _engine.History.ExportJson(query, events), ct);
    public void SetSensorDemand(SensorDemand demand) => _engine.SetSensorDemand(this, demand);
    public Task ShutdownAsync(bool restoreHardwareMode = true) => _engine.StopAsync(restoreHardwareMode);
    public void Dispose() { _engine.CommandUpdated -= OnCommandUpdated; _engine.RemoveSessionContext(this); _engine.Dispose(); }

    private static void Notify<T>(Action<T>? handlers, T value, string name)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception ex) { try { Log.Warn($"{name}: {ex.Message}"); } catch { } }
    }
}
