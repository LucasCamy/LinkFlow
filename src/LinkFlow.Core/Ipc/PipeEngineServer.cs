using System.IO.Pipes;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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

    public PipeEngineServer(LinkFlowEngine engine, string? pipeName = null)
    {
        _engine = engine;
        _pipeName = pipeName ?? PipeProtocol.PipeName;
        _engine.SnapshotUpdated += s => Broadcast(PipeProtocol.Encode(PipeProtocol.Snapshot, s), null, isSnapshot: true);
        _engine.AlertRaised += a => Broadcast(PipeProtocol.Encode(PipeProtocol.Alert, a), null, isSnapshot: false);
        _engine.ConfigChanged += (c, origin) => Broadcast(PipeProtocol.Encode(PipeProtocol.Config, c), origin as Connection, isSnapshot: false);
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
                if (!PipeProtocol.TryDecode(line, out var type, out var payload)) continue;

                switch (type)
                {
                    case PipeProtocol.SetConfig:
                        if (PipeProtocol.Read<ControlConfig>(payload) is { } cfg) _engine.ReplaceConfig(cfg, conn);
                        break;
                    case PipeProtocol.LcdFrame:
                        _engine.RequestLcdFrame();
                        break;
                    case PipeProtocol.Resume:
                        _engine.NotifyResume();
                        break;
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
            conn.Dispose();
            try { await writer.ConfigureAwait(false); } catch { }
            Log.Info($"Interface desconectada ({ClientCount} cliente(s)).");
        }
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
        private readonly Channel<(string Msg, bool Snapshot)> _queue =
            Channel.CreateBounded<(string, bool)>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private int _pendingSnapshots;

        public Connection(NamedPipeServerStream pipe) => Pipe = pipe;
        public NamedPipeServerStream Pipe { get; }

        public void Enqueue(string message, bool isSnapshot = false)
        {
            if (isSnapshot && Volatile.Read(ref _pendingSnapshots) > 2) return; // cliente lento: pula quadros
            if (isSnapshot) Interlocked.Increment(ref _pendingSnapshots);
            _queue.Writer.TryWrite((message, isSnapshot));
        }

        public async Task RunWriterAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var (msg, snap) in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (snap) Interlocked.Decrement(ref _pendingSnapshots);
                    var bytes = Encoding.UTF8.GetBytes(msg + "\n");
                    await Pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await Pipe.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch { /* cliente saiu */ }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            try { Pipe.Dispose(); } catch { }
        }
    }
}
