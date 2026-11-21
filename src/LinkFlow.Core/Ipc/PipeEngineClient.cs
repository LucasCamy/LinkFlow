using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace LinkFlow.Core.Ipc;

/// <summary>Cliente com fila limitada, revisões otimistas e reconexão sem sobrescrever alterações concorrentes.</summary>
public sealed class PipeEngineClient : IEngineClient
{
    private const int QueueCapacity = 128;
    private readonly string _pipeName;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<OutgoingMessage> _outgoing = Channel.CreateBounded<OutgoingMessage>(new BoundedChannelOptions(QueueCapacity)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Dictionary<string, CommandReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _receiptOrder = new();
    private readonly Dictionary<string, TaskCompletionSource<CommandReceipt>> _acks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<PipeProtocol.ReadResponseInfo>> _reads = new(StringComparer.Ordinal);
    private ControlConfig _config = new(), _serviceConfig = new();
    private NamedPipeClientStream? _pipe;
    private Task? _loop;
    private volatile bool _connected, _compatible;
    private bool _gotConfig, _gotHello, _resyncPending, _reportedConnected = true, _configNotificationPending;
    private int _queuedConfigs, _generation;
    private long _lastSnapshotReceived, _connectionStarted;
    private byte[]? _initialPending;
    private string? _lastRejection;
    private readonly bool _verifyServer;
    private OutgoingMessage? _retry;
    private TaskCompletionSource _ready = NewReady();
    private SensorDemand _demand;

    private sealed record OutgoingMessage(EngineCommandRequest? Request, ControlConfig? BaseConfig = null, bool Coalesce = false,
        string? Raw = null, int Generation = 0, bool Ephemeral = false);
    private static TaskCompletionSource NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PipeEngineClient(string pipeName)
    {
        _pipeName = pipeName;
        _verifyServer = pipeName == PipeProtocol.DefaultPipeName && Environment.GetEnvironmentVariable("LINKFLOW_PIPE") == null;
    }

    public bool IsRemote => true;
    public bool IsConnected => _connected;
    public bool CanWrite => _connected && _compatible && IsSnapshotFresh;
    public TimeSpan? SnapshotAge => Volatile.Read(ref _lastSnapshotReceived) is var at && at != 0 ? Stopwatch.GetElapsedTime(at) : null;
    public bool IsSnapshotFresh => SnapshotAge is { } age && age <= TimeSpan.FromSeconds(5);
    public bool IsSimulated { get; private set; }
    public string ModeDescription => _compatible ? "Serviço do Windows" : "Serviço do Windows (somente leitura: protocolo incompatível)";
    public int? ServiceProcessId { get; private set; }
    public Version? ServiceVersion { get; private set; }
    public int? NegotiatedProtocolVersion { get; private set; }
    public ControlConfig Config { get { lock (_lock) return _config; } }
    public EngineSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<CommandReceipt> Operations { get { lock (_lock) return _receiptOrder.Reverse().Select(id => _receipts[id]).ToArray(); } }
    public ConfigurationPersistence Persistence => Snapshot?.Persistence ?? new();
    public AutomationState Automation => Snapshot?.Automation ?? new();
    public event Action<EngineSnapshot>? SnapshotUpdated;
    public event Action<ControlConfig>? ConfigChanged;
    public event Action<ControlConfig>? AnyConfigChanged;
    public event Action<EngineAlert>? AlertRaised;
    public event Action<bool>? ConnectionChanged;
    public event Action<CommandReceipt>? CommandUpdated;

    public static PipeEngineClient? TryConnect(TimeSpan timeout, string? pipeName = null)
    {
        var client = new PipeEngineClient(pipeName ?? PipeProtocol.PipeName);
        if (!client.ConnectOnce(timeout)) { client.Dispose(); return null; }
        var buffer = new byte[16 * 1024];
        using var pending = new MemoryStream();
        using var initialTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (!initialTimeout.IsCancellationRequested)
            {
                var line = PipeProtocol.ReadLineAsync(client._pipe!, buffer, pending, initialTimeout.Token).GetAwaiter().GetResult();
                if (line == null) break;
                client.Handle(line, true);
                if (client._gotHello && client._gotConfig && client.Snapshot != null) break;
            }
        }
        catch (Exception ex) { Log.Warn($"Serviço respondeu de forma inesperada: {ex.Message}"); }
        if (!client._gotHello || !client._gotConfig || !client.IsConnected) { client.Dispose(); return null; }
        client._initialPending = pending.ToArray();
        return client;
    }

    private bool ConnectOnce(TimeSpan timeout)
    {
        NamedPipeClientStream? pipe = null;
        try
        {
            pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue));
            if (_verifyServer && !PipeIdentity.IsLinkFlowService(pipe, out var reason))
            {
                pipe.Dispose();
                if (reason != _lastRejection) Log.Warn($"Pipe do LinkFlow recusado: {reason}.");
                _lastRejection = reason;
                return false;
            }
            _lastRejection = null;
            _pipe = pipe;
            Volatile.Write(ref _connectionStarted, Stopwatch.GetTimestamp());
            Volatile.Write(ref _lastSnapshotReceived, 0);
            Interlocked.Increment(ref _generation);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        { pipe?.Dispose(); return false; }
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));
    private async Task RunAsync(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!first && !ConnectOnce(TimeSpan.FromSeconds(1))) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
            var pipe = _pipe!;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writer = WriteLoopAsync(pipe, linked.Token);
            try
            {
                var buffer = new byte[16 * 1024];
                using var pending = new MemoryStream();
                if (first && _initialPending is { Length: > 0 } init) pending.Write(init);
                first = false;
                while (!ct.IsCancellationRequested)
                {
                    var line = await PipeProtocol.ReadLineAsync(pipe, buffer, pending, ct).WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
                    if (line == null) break;
                    Handle(line, false);
                    if ((SnapshotAge ?? Stopwatch.GetElapsedTime(Volatile.Read(ref _connectionStarted))) > TimeSpan.FromSeconds(8))
                        throw new TimeoutException("O serviço deixou de publicar snapshots; reconectando.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Debug($"Conexão com o serviço caiu: {ex.Message}"); }
            finally
            {
                linked.Cancel();
                try { await writer.ConfigureAwait(false); } catch { }
                try { pipe.Dispose(); } catch { }
            }
            if (ct.IsCancellationRequested) break;
            lock (_lock)
            {
                _resyncPending = true;
                _connected = false;
                _gotHello = false;
                _gotConfig = false;
                ServiceVersion = null;
                NegotiatedProtocolVersion = null;
                _ready = NewReady();
                foreach (var read in _reads.Values) read.TrySetException(new IOException("O serviço desconectou durante a consulta."));
                _reads.Clear();
            }
            if (_reportedConnected) { _reportedConnected = false; Notify(ConnectionChanged, false, nameof(ConnectionChanged)); }
            await Task.Delay(700, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync(Stream pipe, CancellationToken ct)
    {
        await _ready.Task.WaitAsync(ct).ConfigureAwait(false);
        OutgoingMessage? active = null;
        try
        {
            if (_demand != SensorDemand.None) await WriteLineAsync(pipe, PipeProtocol.Encode(PipeProtocol.Demand, (int)_demand), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                lock (_lock) { active = _retry; _retry = null; }
                if (active == null)
                {
                    if (!await _outgoing.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) break;
                    if (!_outgoing.Reader.TryRead(out active)) continue;
                }
                if (active.Ephemeral && active.Generation != Volatile.Read(ref _generation))
                { RejectQueued(active, CommandState.Expired, "A sessão mudou; o contexto antigo não foi reenviado."); active = null; continue; }
                if (active.Request == null)
                {
                    if (active.Raw != null) await WriteLineAsync(pipe, active.Raw, ct).ConfigureAwait(false);
                    active = null;
                    continue;
                }
                while (active.Coalesce && _outgoing.Reader.TryPeek(out var next) && next.Coalesce && _outgoing.Reader.TryRead(out next))
                {
                    var originalBase = active.BaseConfig;
                    RejectQueued(active, CommandState.Superseded, "Substituído pelo ajuste mais recente antes do envio.");
                    active = next with { BaseConfig = originalBase };
                }
                var request = active.Request!;
                if (request.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                { RejectQueued(active, CommandState.Expired, "Solicitação vencida antes do envio."); active = null; continue; }
                if (!_compatible)
                { RejectQueued(active, CommandState.Rejected, "Protocolo incompatível; o serviço está em modo somente leitura."); active = null; continue; }
                if (!IsSnapshotFresh && request.Kind is not (PipeProtocol.Resume or PipeProtocol.CalibrationCancel))
                { RejectQueued(active, CommandState.Rejected, "Leituras do serviço vencidas; comando não enviado."); active = null; continue; }
                if (active.BaseConfig != null && request.Config != null)
                {
                    ControlConfig authoritative;
                    lock (_lock) authoritative = _serviceConfig.Clone();
                    if (!ConfigurationMerge.TryApply(active.BaseConfig, request.Config, authoritative, out var merged))
                    { RejectQueued(active, CommandState.Conflict, "Outra origem alterou o mesmo ajuste. Releia e revise a alteração."); active = null; continue; }
                    request.Config = merged;
                    request.ExpectedRevision = authoritative.Revision;
                    active = active with { BaseConfig = null };
                }
                TaskCompletionSource<CommandReceipt> ack;
                lock (_lock)
                    if (!_acks.TryGetValue(request.Id, out ack!)) _acks[request.Id] = ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await WriteLineAsync(pipe, PipeProtocol.Encode(PipeProtocol.Command, request), ct).ConfigureAwait(false);
                var remaining = request.ExpiresAtUtc - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) remaining = TimeSpan.FromMilliseconds(1);
                CommandReceipt receipt;
                try { receipt = await ack.Task.WaitAsync(remaining, ct).ConfigureAwait(false); }
                catch (TimeoutException)
                { RejectQueued(active, CommandState.Unconfirmed, "O serviço não confirmou o recebimento dentro do prazo. Releia o estado antes de repetir."); active = null; continue; }
                bool notifyConfig = false;
                lock (_lock)
                {
                    _acks.Remove(request.Id);
                    if (request.Kind == PipeProtocol.SetConfig)
                    {
                        _queuedConfigs = Math.Max(0, _queuedConfigs - 1);
                        if (request.Config != null && receipt.State is CommandState.Accepted or CommandState.Applied or CommandState.NotVerifiable)
                        {
                            var accepted = request.Config.Clone();
                            accepted.Revision = receipt.Revision;
                            if (accepted.Revision >= _serviceConfig.Revision) _serviceConfig = accepted;
                        }
                        if (_queuedConfigs == 0)
                        {
                            _config = _serviceConfig.Clone();
                            notifyConfig = _configNotificationPending;
                            _configNotificationPending = false;
                        }
                    }
                }
                bool rejected = receipt.State is CommandState.Rejected or CommandState.Conflict or CommandState.Expired or CommandState.Failed;
                if (notifyConfig || rejected) Notify(ConfigChanged, Config, nameof(ConfigChanged));
                if (!rejected && request.Kind == PipeProtocol.SetConfig) Notify(AnyConfigChanged, Config, nameof(AnyConfigChanged));
                active = null;
            }
        }
        catch
        {
            if (active?.Request != null)
            {
                if (!active.Ephemeral) lock (_lock) _retry = active;
                else RejectQueued(active, CommandState.Unconfirmed, "Conexão encerrada antes da confirmação.");
            }
            try { pipe.Dispose(); } catch { }
            throw;
        }
    }

    private static async Task WriteLineAsync(Stream pipe, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        if (bytes.Length > PipeProtocol.MaxMessageBytes) throw new InvalidDataException("Solicitação maior que o limite do protocolo.");
        await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
        await pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    private void Handle(string line, bool initial)
    {
        if (!PipeProtocol.TryDecode(line, out var type, out var payload)) return;
        switch (type)
        {
            case PipeProtocol.Hello:
                if (!PipeProtocol.TryRead<PipeProtocol.HelloInfo>(payload, out var hello) || hello == null) break;
                IsSimulated = hello.Simulated;
                ServiceProcessId = hello.ProcessId;
                ServiceVersion = Version.TryParse(hello.AppVersion, out var serviceVersion) ? serviceVersion : null;
                NegotiatedProtocolVersion = hello.Protocol;
                _gotHello = true;
                _compatible = hello.Protocol == PipeProtocol.Version;
                if (!_compatible) Log.Warn("Interface e serviço usam protocolos diferentes: alterações bloqueadas.");
                CompleteConnectionIfReady();
                break;
            case PipeProtocol.Config:
                if (!PipeProtocol.TryRead<ControlConfig>(payload, out var cfg) || cfg == null) break;
                try { ConfigurationSchema.ValidateAndMigrate(cfg); cfg.Normalize(); }
                catch (Exception ex) { Log.Warn($"Configuração do serviço recusada: {ex.Message}"); break; }
                ApplyServiceConfig(cfg, initial);
                break;
            case PipeProtocol.Snapshot:
                if (!PipeProtocol.TryRead<EngineSnapshot>(payload, out var snapshot) || snapshot == null) break;
                Snapshot = snapshot;
                Volatile.Write(ref _lastSnapshotReceived, Stopwatch.GetTimestamp());
                CompleteConnectionIfReady();
                if (!initial) Notify(SnapshotUpdated, snapshot, nameof(SnapshotUpdated));
                break;
            case PipeProtocol.Alert:
                if (!initial && PipeProtocol.TryRead<EngineAlert>(payload, out var alert) && alert != null) Notify(AlertRaised, alert, nameof(AlertRaised));
                break;
            case PipeProtocol.Receipt:
                if (PipeProtocol.TryRead<CommandReceipt>(payload, out var receipt) && receipt != null)
                {
                    RememberReceipt(receipt);
                    lock (_lock) if (_acks.TryGetValue(receipt.Id, out var ack) && receipt.State != CommandState.Pending) ack.TrySetResult(receipt);
                }
                break;
            case PipeProtocol.ReadResponse:
                if (PipeProtocol.TryRead<PipeProtocol.ReadResponseInfo>(payload, out var response) && response != null)
                    lock (_lock) if (_reads.Remove(response.Id, out var read)) read.TrySetResult(response);
                break;
        }
    }

    private void ApplyServiceConfig(ControlConfig config, bool initial)
    {
        bool notify;
        lock (_lock)
        {
            _serviceConfig = config;
            _gotConfig = true;
            notify = _queuedConfigs == 0;
            if (notify) _config = config.Clone();
            // Uma origem externa pode chegar antes do ACK do nosso comando. O evento só pode
            // publicar a configuração autoritativa depois de drenar os ajustes locais pendentes.
            if (!initial && !notify) _configNotificationPending = true;
            if (notify) _configNotificationPending = false;
        }
        if (!initial && notify) { Notify(ConfigChanged, config, nameof(ConfigChanged)); Notify(AnyConfigChanged, config, nameof(AnyConfigChanged)); }
        CompleteConnectionIfReady();
    }

    private void CompleteConnectionIfReady()
    {
        bool announce = false;
        lock (_lock)
        {
            if (!_gotHello || !_gotConfig || !IsSnapshotFresh) return;
            _connected = true;
            _ready.TrySetResult();
            if (_resyncPending && !_reportedConnected)
            {
                _reportedConnected = true;
                announce = true;
            }
            _resyncPending = false;
        }
        // "Conectado" garante que o observador pode consultar/alterar usando o estado completo desta conexão.
        if (announce) Notify(ConnectionChanged, true, nameof(ConnectionChanged));
    }

    public void UpdateConfig(Action<ControlConfig> mutate)
    {
        ControlConfig next, baseline;
        lock (_lock)
        {
            baseline = _config.Clone();
            next = baseline.Clone();
            mutate(next);
            next.Normalize();
            QueueConfig(next, baseline);
        }
        Notify(AnyConfigChanged, next, nameof(AnyConfigChanged));
    }
    public void ReplaceConfig(ControlConfig config)
    {
        var next = config.Clone(); next.Normalize();
        lock (_lock) QueueConfig(next, _config.Clone());
        Notify(AnyConfigChanged, next, nameof(AnyConfigChanged));
    }
    private void QueueConfig(ControlConfig next, ControlConfig baseline)
    {
        var request = new EngineCommandRequest { Kind = PipeProtocol.SetConfig, ExpectedRevision = baseline.Revision, Config = next.Clone() };
        bool coalesce = next.Rgb.SyncGpu == baseline.Rgb.SyncGpu;
        _queuedConfigs++;
        if (Enqueue(new(request, baseline, coalesce))) _config = next;
    }
    public string SubmitCommand(EngineCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var copy = JsonSerializer.Deserialize<EngineCommandRequest>(JsonSerializer.Serialize(request, Json.Compact), Json.Compact)!;
        if (copy.Kind == PipeProtocol.SetConfig) lock (_lock) _queuedConfigs++;
        Enqueue(new(copy, Generation: Volatile.Read(ref _generation), Ephemeral: copy.Kind == PipeProtocol.SessionContext));
        return copy.Id;
    }
    private bool Enqueue(OutgoingMessage message)
    {
        if (message.Request is { } request)
        {
            if (message.Ephemeral && !_connected) { RejectQueued(message, CommandState.Rejected, "Sessão desconectada; contexto não enviado."); return false; }
            if (_gotHello && !_compatible) { RejectQueued(message, CommandState.Rejected, "Protocolo incompatível; alterações bloqueadas."); return false; }
            if (_connected && !IsSnapshotFresh && request.Kind is not (PipeProtocol.Resume or PipeProtocol.CalibrationCancel))
            { RejectQueued(message, CommandState.Rejected, "Leituras do serviço vencidas; aguarde a reconexão antes de alterar controles."); return false; }
            RememberReceipt(new CommandReceipt { Id = request.Id, Kind = request.Kind, State = CommandState.Pending,
                RequestedAtUtc = request.RequestedAtUtc, ExpiresAtUtc = request.ExpiresAtUtc, UpdatedAtUtc = DateTimeOffset.UtcNow, Message = "Aguardando envio ao serviço." });
        }
        if (_outgoing.Writer.TryWrite(message)) return true;
        RejectQueued(message, CommandState.Rejected, "Fila de solicitações cheia ou encerrada; nenhuma ação foi enfileirada.");
        return false;
    }
    private void RejectQueued(OutgoingMessage message, CommandState state, string text)
    {
        if (message.Request is not { } request) return;
        lock (_lock)
        {
            if (request.Kind == PipeProtocol.SetConfig) _queuedConfigs = Math.Max(0, _queuedConfigs - 1);
            _acks.Remove(request.Id);
            if (_queuedConfigs == 0)
            {
                _config = _serviceConfig.Clone();
                _configNotificationPending = false;
            }
        }
        RememberReceipt(new CommandReceipt { Id = request.Id, Kind = request.Kind, State = state,
            RequestedAtUtc = request.RequestedAtUtc, ExpiresAtUtc = request.ExpiresAtUtc, UpdatedAtUtc = DateTimeOffset.UtcNow,
            Revision = _serviceConfig.Revision, Message = text });
        if (state != CommandState.Superseded) Notify(ConfigChanged, Config, nameof(ConfigChanged));
    }
    private void RememberReceipt(CommandReceipt receipt)
    {
        lock (_lock)
        {
            if (_receipts.TryGetValue(receipt.Id, out var previous) &&
                (previous == receipt || receipt.UpdatedAtUtc < previous.UpdatedAtUtc || previous.IsTerminal && !receipt.IsTerminal)) return;
            if (!_receipts.ContainsKey(receipt.Id)) _receiptOrder.Enqueue(receipt.Id);
            _receipts[receipt.Id] = receipt;
            while (_receiptOrder.Count > QueueCapacity) _receipts.Remove(_receiptOrder.Dequeue());
        }
        Notify(CommandUpdated, receipt, nameof(CommandUpdated));
    }
    private void Send(string kind, object? payload = null, int seconds = 30) => SubmitCommand(new EngineCommandRequest
    { Kind = kind, Payload = payload == null ? null : JsonSerializer.SerializeToNode(payload, Json.Compact), ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds) });
    public void RequestLcdFrame() => Send(PipeProtocol.LcdFrame);
    public void NotifyResume() => Send(PipeProtocol.Resume);
    public void RefreshRgb() => Send(PipeProtocol.RefreshRgb);
    public void RescanDevices() => Send(PipeProtocol.Rescan);
    public void SetCurveTest(float? temperature) => Send(PipeProtocol.CurveTest, temperature, 5);
    public void StartCalibration() => Send(PipeProtocol.CalibrationStart);
    public void CancelCalibration() => Send(PipeProtocol.CalibrationCancel);
    public void SetSessionContext(AutomationSessionContext context) => Send(PipeProtocol.SessionContext, context, 5);
    public void SetAutomationOverride(DateTimeOffset? until) => Send(PipeProtocol.AutomationOverride, until);
    public void ReportExternalOperation(WindowsOperationRecord record) => Send(PipeProtocol.ReportOperation, record);
    public void SetSensorDemand(SensorDemand demand)
    {
        _demand = demand;
        Enqueue(new(null, Raw: PipeProtocol.Encode(PipeProtocol.Demand, (int)demand), Generation: Volatile.Read(ref _generation), Ephemeral: true));
    }

    private async Task<T> ReadAsync<T>(PipeProtocol.ReadRequestInfo request, CancellationToken ct)
    {
        if (!_connected || !_compatible) throw new IOException("Serviço desconectado ou protocolo incompatível.");
        TaskCompletionSource<PipeProtocol.ReadResponseInfo> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_reads.Count >= 16) throw new InvalidOperationException("Consultas demais em andamento.");
            _reads[request.Id] = completion;
        }
        try
        {
            if (!Enqueue(new(null, Raw: PipeProtocol.Encode(PipeProtocol.ReadRequest, request), Generation: Volatile.Read(ref _generation), Ephemeral: true)))
                throw new IOException("Não foi possível enfileirar a consulta.");
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            if (response.Error != null) throw new InvalidOperationException(response.Error);
            return PipeProtocol.Read<T>(response.Data) ?? throw new InvalidDataException("Resposta de consulta vazia.");
        }
        finally { lock (_lock) _reads.Remove(request.Id); }
    }
    public Task<HistorySeries> QueryHistoryAsync(HistoryQuery query, CancellationToken ct = default) => ReadAsync<HistorySeries>(new() { Kind = "history", History = query }, ct);
    public async Task<IReadOnlyList<OperationalEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default) => await ReadAsync<List<OperationalEvent>>(new() { Kind = "events", Events = query }, ct).ConfigureAwait(false);
    public async Task<IReadOnlyList<HistorySession>> QuerySessionsAsync(CancellationToken ct = default) => await ReadAsync<List<HistorySession>>(new() { Kind = "sessions" }, ct).ConfigureAwait(false);
    public Task<SessionComparison> CompareSessionsAsync(HistoryQuery query, string baseline, string candidate, CancellationToken ct = default) => ReadAsync<SessionComparison>(new() { Kind = "compare", History = query, BaselineSession = baseline, CandidateSession = candidate }, ct);
    public Task<string> ExportHistoryAsync(HistoryQuery query, bool csv, EventQuery? events = null, CancellationToken ct = default) => ReadAsync<string>(new() { Kind = csv ? "csv" : "json", History = query, Events = events }, ct);
    public Task ShutdownAsync(bool restoreHardwareMode = true) { Dispose(); return Task.CompletedTask; }
    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel(); _outgoing.Writer.TryComplete();
        try { _pipe?.Dispose(); } catch { }
        _connected = false;
        lock (_lock)
        {
            foreach (var read in _reads.Values) read.TrySetCanceled();
            _reads.Clear();
            foreach (var ack in _acks.Values) ack.TrySetCanceled();
            _acks.Clear();
        }
    }

    private static void Notify<T>(Action<T>? handlers, T value, string name)
    {
        if (handlers == null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception ex) { try { Log.Warn($"{name}: {ex.Message}"); } catch { } }
    }
}

/// <summary>Rebase de alterações independentes; conflito no mesmo campo é explícito.</summary>
internal static class ConfigurationMerge
{
    public static bool TryApply(ControlConfig baseline, ControlConfig requested, ControlConfig current, out ControlConfig result)
    {
        try
        {
            var merged = Merge(JsonSerializer.SerializeToNode(baseline, Json.Compact), JsonSerializer.SerializeToNode(requested, Json.Compact), JsonSerializer.SerializeToNode(current, Json.Compact), true);
            result = merged!.Deserialize<ControlConfig>(Json.Compact)!;
            result.Revision = current.Revision;
            result.Normalize();
            return true;
        }
        catch (ConfigurationConflictException) { result = current.Clone(); return false; }
    }
    private static JsonNode? Merge(JsonNode? before, JsonNode? after, JsonNode? current, bool root = false)
    {
        if (JsonNode.DeepEquals(before, after)) return current?.DeepClone();
        if (before is JsonObject oldObject && after is JsonObject newObject && current is JsonObject currentObject)
        {
            var merged = (JsonObject)currentObject.DeepClone();
            foreach (string key in oldObject.Select(k => k.Key).Union(newObject.Select(k => k.Key)))
            {
                if (root && key is "revision" or "schemaVersion") continue;
                bool existed = oldObject.TryGetPropertyValue(key, out var oldValue);
                bool exists = newObject.TryGetPropertyValue(key, out var newValue);
                currentObject.TryGetPropertyValue(key, out var currentValue);
                if (existed && !exists)
                {
                    if (!JsonNode.DeepEquals(currentValue, oldValue) && currentValue != null) throw new ConfigurationConflictException();
                    merged.Remove(key);
                }
                else if (exists) merged[key] = Merge(oldValue, newValue, currentValue);
            }
            return merged;
        }
        if (JsonNode.DeepEquals(current, before) || JsonNode.DeepEquals(current, after)) return after?.DeepClone();
        throw new ConfigurationConflictException();
    }
    private sealed class ConfigurationConflictException : Exception;
}
