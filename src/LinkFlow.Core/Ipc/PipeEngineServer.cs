using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace LinkFlow.Core.Ipc;

/// <summary>
/// Expõe o <see cref="LinkFlowEngine"/> do serviço para as interfaces via named pipe.
/// Acesso permitido apenas a usuários logados interativamente, administradores e SYSTEM.
/// </summary>
public sealed class PipeEngineServer : IAsyncDisposable
{
    private readonly LinkFlowEngine _engine;
    private readonly string _pipeName;
    private readonly List<Connection> _clients = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _disposed;
    private readonly Action<EngineSnapshot> _snapshotHandler;
    private readonly Action<EngineAlert> _alertHandler;
    private readonly Action<ControlConfig, object?> _configHandler;
    private readonly Action<CommandReceipt> _commandHandler;

    public PipeEngineServer(LinkFlowEngine engine, string? pipeName = null)
    {
        _engine = engine;
        _pipeName = pipeName ?? PipeProtocol.PipeName;
        _engine.SnapshotUpdated += _snapshotHandler = s => Broadcast(PipeProtocol.Encode(PipeProtocol.Snapshot, s), null, isSnapshot: true);
        _engine.AlertRaised += _alertHandler = a => Broadcast(PipeProtocol.Encode(PipeProtocol.Alert, a), null, isSnapshot: false);
        _engine.ConfigChanged += _configHandler = (c, origin) => Broadcast(PipeProtocol.Encode(PipeProtocol.Config, c), origin as Connection, isSnapshot: false);
        _engine.CommandUpdated += _commandHandler = r => Broadcast(PipeProtocol.Encode(PipeProtocol.Receipt, r), null, false);
    }

    public int ClientCount
    {
        get { lock (_lock) return _clients.Count; }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log.Info($"Servidor de pipe ouvindo em \\\\.\\pipe\\{_pipeName}");
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        // Quem cria o pipe (o próprio processo) também precisa de acesso total
        using var me = WindowsIdentity.GetCurrent();
        if (me.User != null)
            security.AddAccessRule(new PipeAccessRule(me.User, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var conn = new Connection(pipe);
                lock (_lock) _clients.Add(conn);
                _ = Task.Run(() => ServeAsync(conn, ct), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                Log.Warn($"Pipe: falha ao aceitar conexão: {ex.Message}");
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(Connection conn, CancellationToken ct)
    {
        Log.Info($"Interface conectada ({ClientCount} cliente(s)).");
        var writer = conn.RunWriterAsync(ct);
        try
        {
            conn.Enqueue(PipeProtocol.Encode(PipeProtocol.Hello, new PipeProtocol.HelloInfo
            {
                AppVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "?",
                Simulated = _engine.IsSimulated,
                ProcessId = Environment.ProcessId
            }));
            conn.Enqueue(PipeProtocol.Encode(PipeProtocol.Config, _engine.GetConfig()));
            conn.Enqueue(PipeProtocol.Encode(PipeProtocol.Snapshot, _engine.GetSnapshot()));

            var buffer = new byte[16 * 1024];
            using var pending = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                var line = await PipeProtocol.ReadLineAsync(conn.Pipe, buffer, pending, ct).ConfigureAwait(false);
                if (line == null) break;
                if (!PipeProtocol.TryDecode(line, out var type, out var payload))
                {
                    conn.Reject("linha que não é uma mensagem do protocolo");
                    continue;
                }

                // Uma mensagem com conteúdo errado é descartada sozinha: antes ela derrubava a conexão da interface
                try { Handle(conn, type, payload); }
                catch (Exception ex) when (ex is not (OperationCanceledException or IOException or ObjectDisposedException))
                {
                    conn.Reject($"'{type}' ({ex.GetType().Name}: {ex.Message})");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException)
        {
            Log.Debug($"Pipe: cliente desconectado ({ex.Message}).");
        }
        catch (Exception ex)
        {
            Log.Error("Pipe: erro atendendo cliente", ex);
        }
        finally
        {
            lock (_lock) _clients.Remove(conn);
            _engine.SetSensorDemand(conn, SensorDemand.None);
            _engine.RemoveSessionContext(conn);
            conn.Dispose();
            try { await writer.ConfigureAwait(false); } catch { }
            Log.Info($"Interface desconectada ({ClientCount} cliente(s)).");
        }
    }

    private void Handle(Connection conn, string type, System.Text.Json.Nodes.JsonNode? payload)
    {
        switch (type)
        {
            case PipeProtocol.Command:
                if (!PipeProtocol.TryRead<EngineCommandRequest>(payload, out var request) || request == null)
                { conn.Reject("solicitação ilegível"); return; }
                var receipt = _engine.ExecuteCommand(request, conn);
                conn.Enqueue(PipeProtocol.Encode(PipeProtocol.Receipt, receipt));
                if (receipt.State == CommandState.Conflict) conn.Enqueue(PipeProtocol.Encode(PipeProtocol.Config, _engine.GetConfig()));
                break;
            case PipeProtocol.ReadRequest:
                if (!PipeProtocol.TryRead<PipeProtocol.ReadRequestInfo>(payload, out var read) || read == null)
                { conn.Reject("consulta ilegível"); return; }
                HandleRead(conn, read);
                break;
            case PipeProtocol.SetConfig:
            case PipeProtocol.LcdFrame:
            case PipeProtocol.Resume:
            case PipeProtocol.RefreshRgb:
            case PipeProtocol.Rescan:
            case PipeProtocol.CurveTest:
            case PipeProtocol.CalibrationStart:
            case PipeProtocol.CalibrationCancel:
                conn.Enqueue(PipeProtocol.Encode(PipeProtocol.Receipt, new CommandReceipt
                {
                    Id = Guid.NewGuid().ToString("N"), Kind = type, State = CommandState.Rejected,
                    UpdatedAtUtc = DateTimeOffset.UtcNow, Message = "Interface antiga: comandos sem ID, revisão e prazo estão bloqueados. Atualize a interface."
                }));
                conn.Reject("comando de protocolo antigo bloqueado");
                break;
            case PipeProtocol.Demand:
                if (PipeProtocol.TryRead<int>(payload, out var d) && d >= 0 && (d & ~(int)SensorDemand.DiskIo) == 0)
                    _engine.SetSensorDemand(conn, (SensorDemand)d);
                else conn.Reject("pedido de sensores ilegível");
                break;
            default:
                // Tipo desconhecido (ex.: interface mais nova que o serviço): ignorado sem derrubar a conexão
                Log.Debug($"Pipe: mensagem de tipo desconhecido '{type}' ignorada.");
                break;
        }
    }

    private void HandleRead(Connection conn, PipeProtocol.ReadRequestInfo request)
    {
        var response = new PipeProtocol.ReadResponseInfo { Id = request.Id };
        try
        {
            if (request.Id is not { Length: > 0 and <= 80 }) throw new ArgumentException("Identificador de consulta inválido.");
            var query = request.History ?? new HistoryQuery();
            query.MaxPoints = Math.Clamp(query.MaxPoints, 1, 1200);
            var events = request.Events ?? new EventQuery();
            events.MaxItems = Math.Clamp(events.MaxItems, 1, 200);
            object data = request.Kind switch
            {
                "hubTelemetry" => _engine.GetHubTelemetryDiagnostics(),
                "history" => _engine.History.Query(query),
                "events" => _engine.History.QueryEvents(events),
                "sessions" => _engine.History.QuerySessions(),
                "compare" => _engine.History.CompareSessions(query, request.BaselineSession ?? string.Empty, request.CandidateSession ?? string.Empty),
                "csv" => _engine.History.ExportCsv(query),
                "json" => _engine.History.ExportJson(query, events),
                _ => throw new ArgumentException("Consulta não suportada.")
            };
            response.Data = JsonSerializer.SerializeToNode(data, data.GetType(), Json.Compact);
            if (Encoding.UTF8.GetByteCount(PipeProtocol.Encode(PipeProtocol.ReadResponse, response)) > PipeProtocol.MaxMessageBytes - 1024)
            { response.Data = null; response.Error = "Resultado grande demais. Reduza o período ou a quantidade de pontos."; }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        { response.Error = ex.Message; }
        conn.Enqueue(PipeProtocol.Encode(PipeProtocol.ReadResponse, response));
    }

    private void Broadcast(string message, Connection? except, bool isSnapshot)
    {
        Connection[] targets;
        lock (_lock) targets = _clients.ToArray();
        foreach (var c in targets)
        {
            if (!ReferenceEquals(c, except)) c.Enqueue(message, isSnapshot);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _engine.SnapshotUpdated -= _snapshotHandler;
        _engine.AlertRaised -= _alertHandler;
        _engine.ConfigChanged -= _configHandler;
        _engine.CommandUpdated -= _commandHandler;
        _cts?.Cancel();
        if (_acceptLoop != null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
        }
        Connection[] all;
        lock (_lock) all = _clients.ToArray();
        foreach (var c in all) c.Dispose();
        _cts?.Dispose();
    }

    /// <summary>Uma interface conectada. Escritas passam por uma fila; snapshots antigos são descartados se o cliente estiver lento.</summary>
    private sealed class Connection : IDisposable
    {
        private readonly Channel<string> _reliable = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly SemaphoreSlim _signal = new(0, 1);
        private string? _latestConfig, _latestSnapshot;

        public Connection(NamedPipeServerStream pipe)
        {
            Pipe = pipe;
        }

        internal int PendingSnapshots => Volatile.Read(ref _latestSnapshot) == null ? 0 : 1;
        public NamedPipeServerStream Pipe { get; }
        private int _rejected;

        /// <summary>Mensagem descartada. Só as primeiras vão como aviso: um processo insistindo não enche o log.</summary>
        public void Reject(string what)
        {
            int n = Interlocked.Increment(ref _rejected);
            if (n <= 3) Log.Warn($"Pipe: mensagem inválida ignorada: {what}.");
            else Log.Debug($"Pipe: mensagem inválida ignorada (nº {n}): {what}.");
        }

        public void Enqueue(string message, bool isSnapshot = false)
        {
            if (isSnapshot) Interlocked.Exchange(ref _latestSnapshot, message);
            else if (PipeProtocol.TryDecode(message, out var type, out _) && type == PipeProtocol.Config)
                Interlocked.Exchange(ref _latestConfig, message);
            else if (!_reliable.Writer.TryWrite(message))
            {
                // Recibos nunca são descartados em silêncio: a desconexão força ressincronização e replay idempotente.
                Dispose();
                return;
            }
            try { _signal.Release(); } catch (SemaphoreFullException) { }
        }

        public async Task RunWriterAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _signal.WaitAsync(ct).ConfigureAwait(false);
                    while (_reliable.Reader.TryRead(out var msg)) await WriteAsync(msg, ct).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _latestConfig, null) is { } config) await WriteAsync(config, ct).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _latestSnapshot, null) is { } snapshot) await WriteAsync(snapshot, ct).ConfigureAwait(false);
                }
            }
            catch { /* cliente saiu */ }
        }

        private async Task WriteAsync(string message, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(message + "\n");
            await Pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await Pipe.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _reliable.Writer.TryComplete();
            try { Pipe.Dispose(); } catch { }
        }
    }
}
