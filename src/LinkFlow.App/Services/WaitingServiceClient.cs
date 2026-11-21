using System.IO;
using LinkFlow.Core;
using LinkFlow.Core.Ipc;

namespace LinkFlow.App.Services;

/// <summary>Uma interface desconectada continua acessível, sem assumir o hardware reservado ao serviço.</summary>
public sealed class WaitingServiceClient : IEngineClient
{
    private readonly Func<IEngineClient?> _connect;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly List<CommandReceipt> _rejected = [];
    private volatile IEngineClient? _client;
    private Task? _connecting;
    private SensorDemand _demand;
    private bool _disposed;
    public WaitingServiceClient(Func<IEngineClient?>? connect = null) => _connect = connect ?? (() => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(1)));
    public bool IsRemote => true;
    public bool IsConnected => _client?.IsConnected == true;
    public bool IsSimulated => _client?.IsSimulated == true;
    public bool CanWrite => _client?.CanWrite == true;
    public TimeSpan? SnapshotAge => _client?.SnapshotAge;
    public bool IsSnapshotFresh => _client?.IsSnapshotFresh == true;
    public Version? ServiceVersion => _client?.ServiceVersion;
    public int? NegotiatedProtocolVersion => _client?.NegotiatedProtocolVersion;
    public string ModeDescription => _client?.ModeDescription ?? "Aguardando o serviço do Windows";
    public ControlConfig Config => _client?.Config ?? new();
    public EngineSnapshot? Snapshot => _client?.Snapshot;
    public IReadOnlyList<CommandReceipt> Operations { get { lock (_gate) return _client?.Operations ?? _rejected.ToArray(); } }
    public ConfigurationPersistence Persistence => _client?.Persistence ?? new();
    public AutomationState Automation => _client?.Automation ?? new();
    public event Action<EngineSnapshot>? SnapshotUpdated;
    public event Action<ControlConfig>? ConfigChanged;
    public event Action<ControlConfig>? AnyConfigChanged;
    public event Action<EngineAlert>? AlertRaised;
    public event Action<bool>? ConnectionChanged;
    public event Action<CommandReceipt>? CommandUpdated;

    public void Start() => _connecting ??= Task.Run(async () =>
    {
        while (!_lifetime.IsCancellationRequested)
        {
            IEngineClient? connected = null;
            try { connected = _connect(); }
            catch (Exception ex) { Log.Debug($"Aguardando o serviço: {ex.Message}"); }
            if (connected != null)
            {
                lock (_gate)
                {
                    if (_disposed) { connected.Dispose(); return; }
                    _client = connected;
                    connected.SnapshotUpdated += s => Notify(SnapshotUpdated, s);
                    connected.ConfigChanged += c => Notify(ConfigChanged, c);
                    connected.AnyConfigChanged += c => Notify(AnyConfigChanged, c);
                    connected.AlertRaised += a => Notify(AlertRaised, a);
                    connected.ConnectionChanged += state => Notify(ConnectionChanged, state);
                    connected.CommandUpdated += operation => Notify(CommandUpdated, operation);
                    connected.SetSensorDemand(_demand);
                    connected.Start();
                }
                lock (_gate)
                {
                    if (_disposed) return;
                    Notify(ConfigChanged, connected.Config);
                    if (connected.Snapshot is { } snapshot) Notify(SnapshotUpdated, snapshot);
                    Notify(ConnectionChanged, connected.IsConnected);
                }
                return;
            }
            try { await Task.Delay(1000, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    });

    public string SubmitCommand(EngineCommandRequest request)
    {
        if (_client != null) return _client.SubmitCommand(request);
        var receipt = new CommandReceipt { Id = request.Id, Kind = request.Kind, RequestedAtUtc = request.RequestedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = request.ExpiresAtUtc, State = CommandState.Rejected,
            Message = "O serviço está indisponível. Nenhuma alteração foi aplicada." };
        lock (_gate) { _rejected.Insert(0, receipt); if (_rejected.Count > 128) _rejected.RemoveAt(128); }
        Notify(CommandUpdated, receipt);
        return receipt.Id;
    }
    public void UpdateConfig(Action<ControlConfig> mutate)
    {
        if (_client != null) _client.UpdateConfig(mutate);
        else SubmitCommand(new() { Kind = PipeProtocol.SetConfig });
    }
    public void ReplaceConfig(ControlConfig config)
    {
        if (_client != null) _client.ReplaceConfig(config);
        else SubmitCommand(new() { Kind = PipeProtocol.SetConfig });
    }
    public void RequestLcdFrame() { if (_client != null) _client.RequestLcdFrame(); else SubmitCommand(new() { Kind = PipeProtocol.LcdFrame }); }
    public void NotifyResume() { _client?.NotifyResume(); }
    public void RefreshRgb() { if (_client != null) _client.RefreshRgb(); else SubmitCommand(new() { Kind = PipeProtocol.RefreshRgb }); }
    public void RescanDevices() { if (_client != null) _client.RescanDevices(); else SubmitCommand(new() { Kind = PipeProtocol.Rescan }); }
    public void SetCurveTest(float? temperature) { _client?.SetCurveTest(temperature); }
    public void StartCalibration() { if (_client != null) _client.StartCalibration(); else SubmitCommand(new() { Kind = PipeProtocol.CalibrationStart }); }
    public void CancelCalibration() { _client?.CancelCalibration(); }
    public void SetSessionContext(AutomationSessionContext context) { _client?.SetSessionContext(context); }
    public void SetAutomationOverride(DateTimeOffset? until) { _client?.SetAutomationOverride(until); }
    public void ReportExternalOperation(WindowsOperationRecord operation) { _client?.ReportExternalOperation(operation); }
    public void SetSensorDemand(SensorDemand demand) { _demand = demand; _client?.SetSensorDemand(demand); }
    private static Task<T> Unavailable<T>() => Task.FromException<T>(new IOException("O serviço ainda está indisponível."));
    public Task<HistorySeries> QueryHistoryAsync(HistoryQuery query, CancellationToken ct = default) => _client?.QueryHistoryAsync(query, ct) ?? Unavailable<HistorySeries>();
    public Task<IReadOnlyList<OperationalEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default) => _client?.QueryEventsAsync(query, ct) ?? Unavailable<IReadOnlyList<OperationalEvent>>();
    public Task<IReadOnlyList<HistorySession>> QuerySessionsAsync(CancellationToken ct = default) => _client?.QuerySessionsAsync(ct) ?? Unavailable<IReadOnlyList<HistorySession>>();
    public Task<SessionComparison> CompareSessionsAsync(HistoryQuery query, string baseline, string candidate, CancellationToken ct = default) => _client?.CompareSessionsAsync(query, baseline, candidate, ct) ?? Unavailable<SessionComparison>();
    public Task<string> ExportHistoryAsync(HistoryQuery query, bool csv, EventQuery? events = null, CancellationToken ct = default) => _client?.ExportHistoryAsync(query, csv, events, ct) ?? Unavailable<string>();
    public Task ShutdownAsync(bool restoreHardwareMode = true) { Dispose(); return Task.CompletedTask; }
    private static void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception ex) { try { Log.Warn($"Interface aguardando serviço: {ex.Message}"); } catch { } }
    }
    public void Dispose()
    {
        IEngineClient? connected;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            connected = _client;
        }
        connected?.Dispose();
    }
}
