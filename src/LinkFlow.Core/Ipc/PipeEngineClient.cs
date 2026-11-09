using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;

namespace LinkFlow.Core.Ipc;

/// <summary>
/// Interface conectada ao serviço LinkFlow. Reconecta sozinha se o serviço reiniciar.
/// Alterações são aplicadas na cópia local (resposta imediata na UI) e enviadas ao serviço.
/// </summary>
public sealed class PipeEngineClient : IEngineClient
{
    private readonly string _pipeName;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private ControlConfig _config = new();
    private NamedPipeClientStream? _pipe;
    private Task? _loop;
    private volatile bool _connected;

    private PipeEngineClient(string pipeName) => _pipeName = pipeName;

    public bool IsRemote => true;
    public bool IsConnected => _connected;
    public bool IsSimulated { get; private set; }
    public string ModeDescription => "Serviço do Windows";
    public int? ServiceProcessId { get; private set; }

    public ControlConfig Config
    {
        get { lock (_lock) return _config; }
    }

    public EngineSnapshot? Snapshot { get; private set; }

    public event Action<EngineSnapshot>? SnapshotUpdated;
    public event Action<ControlConfig>? ConfigChanged;
    public event Action<EngineAlert>? AlertRaised;
    public event Action<bool>? ConnectionChanged;

    /// <summary>Tenta conectar ao serviço. Retorna null se ele não estiver rodando.</summary>
    public static PipeEngineClient? TryConnect(TimeSpan timeout, string? pipeName = null)
    {
        var client = new PipeEngineClient(pipeName ?? PipeProtocol.PipeName);
        if (!client.ConnectOnce(timeout))
        {
            client.Dispose();
            return null;
        }

        // Espera a configuração inicial (hello + config chegam logo após conectar)
        var buffer = new byte[16 * 1024];
        using var pending = new MemoryStream();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
                var line = PipeProtocol.ReadLineAsync(client._pipe!, buffer, pending, cts.Token).GetAwaiter().GetResult();
                if (line == null) break;
                client.Handle(line, initial: true);
                if (client._gotConfig && client.Snapshot != null) break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Serviço respondeu de forma inesperada: {ex.Message}");
        }

        if (!client._gotConfig)
        {
            client.Dispose();
            return null;
        }

        client._initialPending = pending.ToArray();
        return client;
    }

    private bool _gotConfig;
    private byte[]? _initialPending;

    private bool ConnectOnce(TimeSpan timeout)
    {
        try
        {
            var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect((int)timeout.TotalMilliseconds);
            _pipe = pipe;
            _connected = true;
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!first)
            {
                if (!ConnectOnce(TimeSpan.FromSeconds(1)))
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                    continue;
                }
                Log.Info("Reconectado ao serviço LinkFlow.");
                ConnectionChanged?.Invoke(true);
            }

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
                    var line = await PipeProtocol.ReadLineAsync(pipe, buffer, pending, ct).ConfigureAwait(false);
                    if (line == null) break;
                    Handle(line, initial: false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Debug($"Conexão com o serviço caiu: {ex.Message}");
            }
            finally
            {
                linked.Cancel();
                try { await writer.ConfigureAwait(false); } catch { }
                try { pipe.Dispose(); } catch { }
            }

            if (ct.IsCancellationRequested) break;
            _connected = false;
            Log.Warn("Serviço LinkFlow desconectado; tentando reconectar...");
            ConnectionChanged?.Invoke(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteLoopAsync(Stream pipe, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                // Coalescência: durante arrastes envia só a configuração mais recente
                var latest = msg;
                while (latest.StartsWith("{\"t\":\"setConfig\"", StringComparison.Ordinal) && _outgoing.Reader.TryPeek(out var next) &&
                       next.StartsWith("{\"t\":\"setConfig\"", StringComparison.Ordinal) && _outgoing.Reader.TryRead(out next))
                {
                    latest = next;
                }
                var bytes = Encoding.UTF8.GetBytes(latest + "\n");
                await pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch { /* desconectado: o laço principal reconecta */ }
    }

    private void Handle(string line, bool initial)
    {
        if (!PipeProtocol.TryDecode(line, out var type, out var payload)) return;
        switch (type)
        {
            case PipeProtocol.Hello:
                var hello = PipeProtocol.Read<PipeProtocol.HelloInfo>(payload);
                if (hello != null)
                {
                    IsSimulated = hello.Simulated;
                    ServiceProcessId = hello.ProcessId;
                    if (hello.Protocol != PipeProtocol.Version)
                        Log.Warn($"Versão do protocolo do serviço ({hello.Protocol}) difere da interface ({PipeProtocol.Version}).");
                }
                break;
            case PipeProtocol.Config:
                if (PipeProtocol.Read<ControlConfig>(payload) is { } cfg)
                {
                    lock (_lock) _config = cfg;
                    _gotConfig = true;
                    if (!initial) ConfigChanged?.Invoke(cfg);
                }
                break;
            case PipeProtocol.Snapshot:
                if (PipeProtocol.Read<EngineSnapshot>(payload) is { } snap)
                {
                    Snapshot = snap;
                    if (!initial) SnapshotUpdated?.Invoke(snap);
                }
                break;
            case PipeProtocol.Alert:
                if (!initial && PipeProtocol.Read<EngineAlert>(payload) is { } alert) AlertRaised?.Invoke(alert);
                break;
        }
    }

    public void UpdateConfig(Action<ControlConfig> mutate)
    {
        ControlConfig next;
        lock (_lock)
        {
            next = Json.Clone(_config);
            mutate(next);
            next.Normalize();
            _config = next;
        }
        _outgoing.Writer.TryWrite(PipeProtocol.Encode(PipeProtocol.SetConfig, next));
    }

    public void ReplaceConfig(ControlConfig config)
    {
        var next = Json.Clone(config);
        next.Normalize();
        lock (_lock) _config = next;
        _outgoing.Writer.TryWrite(PipeProtocol.Encode(PipeProtocol.SetConfig, next));
    }

    public void RequestLcdFrame() => _outgoing.Writer.TryWrite(PipeProtocol.Encode(PipeProtocol.LcdFrame, null));
    public void NotifyResume() => _outgoing.Writer.TryWrite(PipeProtocol.Encode(PipeProtocol.Resume, null));
    public void RescanDevices() => _outgoing.Writer.TryWrite(PipeProtocol.Encode(PipeProtocol.Rescan, null));

    /// <summary>Fecha só a conexão: o serviço continua controlando o hardware.</summary>
    public Task ShutdownAsync(bool restoreHardwareMode = true)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        try { _pipe?.Dispose(); } catch { }
        _connected = false;
    }
}
