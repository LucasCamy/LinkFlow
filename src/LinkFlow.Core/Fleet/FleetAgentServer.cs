using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Core;

/// <summary>Agente TLS opcional. O motor e suas proteções permanecem locais, inclusive sem nenhum cliente de rede.</summary>
public sealed class FleetAgentServer : IAsyncDisposable
{
    private readonly IEngineClient _engine;
    private readonly FleetAgentOptions _options;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _rates = new(StringComparer.Ordinal);
    private readonly List<(byte[] Hash, FleetPermission Permissions)> _keys;
    private TcpListener? _listener;
    private Task? _accept;
    private long _connectionId;
    private int _disposed;
    public int BoundPort => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public string CertificateSha256 => FleetClient.CertificatePin(_certificate);

    public FleetAgentServer(IEngineClient engine, FleetAgentOptions options, X509Certificate2 certificate)
    {
        _engine = engine;
        _options = Json.Clone(options);
        _certificate = certificate;
        if (!IPAddress.TryParse(_options.BindAddress, out var address)) throw new ArgumentException("O endereço de escuta deve ser um IP explícito.");
        if (!IPAddress.IsLoopback(address) && !_options.AllowNetwork) throw new ArgumentException("Acesso pela rede exige AllowNetwork explícito.");
        if (_options.Port is < 0 or > 65535) throw new ArgumentException("Porta inválida.");
        if (_options.Name.Length is < 1 or > 100 || _options.AgentId.Length is < 1 or > 80) throw new ArgumentException("Identificação do agente inválida.");
        if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
            throw new ArgumentException("Certificado TLS inválido ou sem chave privada.");
        if (_options.AccessKeys is not { Count: > 0 and <= 16 }) throw new ArgumentException("Configure entre uma e dezesseis credenciais.");
        _keys = _options.AccessKeys.Select(k =>
        {
            if (k.TokenSha256.Length != 64 || k.TokenSha256.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Hash de credencial inválido.");
            if ((k.Permissions & FleetPermission.Observe) == 0 || (k.Permissions & ~(FleetPermission.Observe | FleetPermission.Control)) != 0)
                throw new ArgumentException("Escopo de credencial inválido.");
            return (Convert.FromHexString(k.TokenSha256), k.Permissions);
        }).ToList();
        _slots = new(Math.Clamp(_options.MaxConnections, 1, 16));
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) throw new InvalidOperationException("O agente de rede está desativado.");
        if (_accept != null) throw new InvalidOperationException("Agente já iniciado.");
        ct.ThrowIfCancellationRequested();
        _listener = new(IPAddress.Parse(_options.BindAddress), _options.Port);
        _listener.Start(Math.Clamp(_options.MaxConnections, 1, 16));
        _accept = AcceptAsync(_stop.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (!_slots.Wait(0)) { tcp.Dispose(); continue; }
                long id = Interlocked.Increment(ref _connectionId);
                var task = ServeAsync(tcp, ct);
                _clients[id] = task;
                _ = task.ContinueWith(_ => { _clients.TryRemove(id, out var removed); _slots.Release(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        using (tcp)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 2, 30)));
            using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate, ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token).ConfigureAwait(false);
                var buffer = new byte[8192];
                using var pending = new MemoryStream();
                string? line = await PipeProtocol.ReadLineAsync(tls, buffer, pending, timeout.Token).ConfigureAwait(false);
                if (line == null) return;
                FleetResponse response;
                FleetRequest? request = null;
                try { request = JsonSerializer.Deserialize<FleetRequest>(line, Json.Compact); }
                catch (JsonException) { }
                if (request == null) response = Error(string.Empty, "invalid", "Solicitação inválida.");
                else response = await HandleAsync(request, timeout.Token).ConfigureAwait(false);
                string encoded = JsonSerializer.Serialize(response, Json.Compact);
                if (Encoding.UTF8.GetByteCount(encoded) > PipeProtocol.MaxMessageBytes - 1024)
                    encoded = JsonSerializer.Serialize(Error(response.Id, "size", "Resposta grande demais; reduza a consulta."), Json.Compact);
                await tls.WriteAsync(Encoding.UTF8.GetBytes(encoded + "\n"), timeout.Token).ConfigureAwait(false);
                await tls.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or SocketException) { }
            // Credenciais, conteúdo das solicitações e exceções TLS nunca são enviados ao log.
        }
    }

    private async Task<FleetResponse> HandleAsync(FleetRequest request, CancellationToken ct)
    {
        if (request.Id is not { Length: > 0 and <= 80 } || request.Protocol != 1) return Error(request.Id, "protocol", "Protocolo ou identificador incompatível.");
        if (!Authenticate(request.Token, out var permission, out var identity)) return Error(request.Id, "unauthorized", "Credencial recusada.");
        lock (_rates)
        {
            if (!_rates.TryGetValue(identity, out var queue)) _rates[identity] = queue = new();
            var now = DateTimeOffset.UtcNow;
            while (queue.TryPeek(out var at) && at < now.AddMinutes(-1)) queue.Dequeue();
            if (queue.Count >= Math.Clamp(_options.RequestsPerMinute, 10, 600)) return Error(request.Id, "rate", "Limite de consultas atingido.");
            queue.Enqueue(now);
        }
        try
        {
            object result;
            switch (request.Kind)
            {
                case "snapshot":
                    if (_engine.Snapshot == null || !_engine.IsSnapshotFresh) return Error(request.Id, "unavailable", "O motor não publicou leituras recentes.");
                    result = new FleetSnapshot
                    {
                        AgentId = _options.AgentId, Name = _options.Name,
                        Version = _engine.ServiceVersion?.ToString() ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? string.Empty,
                        Permissions = _options.AllowControl ? permission : permission & ~FleetPermission.Control,
                        CollectedAtUtc = DateTimeOffset.UtcNow, SnapshotAgeSeconds = _engine.SnapshotAge?.TotalSeconds,
                        Config = _engine.Config.Clone(), Snapshot = _engine.Snapshot
                    };
                    break;
                case "history":
                    var query = request.History ?? new(); query.MaxPoints = Math.Clamp(query.MaxPoints, 1, 1200);
                    result = await _engine.QueryHistoryAsync(query, ct).ConfigureAwait(false);
                    break;
                case "events":
                    var events = request.Events ?? new(); events.MaxItems = Math.Clamp(events.MaxItems, 1, 200);
                    result = await _engine.QueryEventsAsync(events, ct).ConfigureAwait(false);
                    break;
                case "command":
                    if (!_options.AllowControl || !permission.HasFlag(FleetPermission.Control)) return Error(request.Id, "forbidden", "Esta credencial permite apenas observação.");
                    if (request.Command == null) return Error(request.Id, "invalid", "Comando ausente.");
                    if (!_engine.IsSnapshotFresh && request.Command.Kind != PipeProtocol.CalibrationCancel)
                        return Error(request.Id, "unavailable", "Leituras locais vencidas; controle remoto aguardando recuperação.");
                    if (request.Command.Kind is not (PipeProtocol.SetConfig or PipeProtocol.RefreshRgb or PipeProtocol.Rescan or PipeProtocol.CalibrationCancel))
                        return Error(request.Id, "forbidden", "Esta operação exige a sessão local.");
                    if (request.Command.Kind == PipeProtocol.SetConfig)
                    {
                        var current = _engine.Config;
                        if (request.Command.ExpectedRevision != current.Revision && !_engine.Operations.Any(r => r.Id == request.Command.Id))
                            return new() { Id = request.Id, Data = JsonSerializer.SerializeToNode(new CommandReceipt
                            { Id = request.Command.Id, Kind = request.Command.Kind, State = CommandState.Conflict, Revision = current.Revision,
                              RequestedAtUtc = request.Command.RequestedAtUtc, ExpiresAtUtc = request.Command.ExpiresAtUtc,
                              UpdatedAtUtc = DateTimeOffset.UtcNow, Message = "A configuração mudou. Releia o estado antes de alterar." }, Json.Compact) };
                        if (request.Command.Config == null || !current.Safety.FailSafeEnabled ||
                            !PreservesLocalProtection(current, request.Command.Config))
                            return Error(request.Id, "protection", "As proteções e seus limites só podem ser alterados localmente.");
                    }
                    result = await ForwardCommandAsync(request.Command, ct).ConfigureAwait(false);
                    break;
                default: return Error(request.Id, "invalid", "Operação não suportada.");
            }
            return new() { Id = request.Id, Data = JsonSerializer.SerializeToNode(result, result.GetType(), Json.Compact) };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or TimeoutException)
        { return Error(request.Id, "unavailable", "A solicitação não pôde ser concluída. Consulte o estado local do agente."); }
    }

    private async Task<CommandReceipt> ForwardCommandAsync(EngineCommandRequest request, CancellationToken ct)
    {
        TaskCompletionSource<CommandReceipt> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Received(CommandReceipt receipt) { if (receipt.Id == request.Id && receipt.State != CommandState.Pending) completion.TrySetResult(receipt); }
        _engine.CommandUpdated += Received;
        try
        {
            _engine.SubmitCommand(request);
            if (completion.Task.IsCompletedSuccessfully) return completion.Task.Result;
            var existing = _engine.Operations.FirstOrDefault(r => r.Id == request.Id && r.State != CommandState.Pending);
            if (existing != null) return existing;
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        finally { _engine.CommandUpdated -= Received; }
    }

    private static bool PreservesLocalProtection(ControlConfig current, ControlConfig proposed)
    {
        if (JsonSerializer.Serialize(proposed.Safety, Json.Compact) != JsonSerializer.Serialize(current.Safety, Json.Compact)) return false;
        static bool SamePolicy(FanCurveConfig? before, FanCurveConfig? after) => before != null && after != null &&
            before.FallbackPolicy == after.FallbackPolicy && before.FallbackMinPercent == after.FallbackMinPercent;
        if (!SamePolicy(current.Curve, proposed.Curve)) return false;
        // A remoção de uma curva/grupo também pode remover a política protetiva daquele conjunto de fans.
        if (proposed.GroupCurves == null || current.GroupCurves.Count != proposed.GroupCurves.Count || current.GroupCurves.Any(kv =>
            !proposed.GroupCurves.TryGetValue(kv.Key, out var after) || !SamePolicy(kv.Value, after))) return false;
        // Perfis armazenados são aprovados localmente; o cliente remoto escolhe, mas não os reescreve.
        if (JsonSerializer.Serialize(current.Profiles, Json.Compact) != JsonSerializer.Serialize(proposed.Profiles, Json.Compact)) return false;
        if (proposed.Fans == null || current.Fans.Keys.Union(proposed.Fans.Keys).Any(key =>
            JsonSerializer.Serialize(current.GetFan(key).Calibration, Json.Compact) != JsonSerializer.Serialize(proposed.GetFan(key).Calibration, Json.Compact))) return false;
        return true;
    }

    private bool Authenticate(string? token, out FleetPermission permissions, out string identity)
    {
        permissions = 0; identity = string.Empty;
        if (token is not { Length: >= 32 and <= 256 }) return false;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var key in _keys)
            if (CryptographicOperations.FixedTimeEquals(hash, key.Hash)) permissions |= key.Permissions;
        if (permissions == 0) return false;
        identity = Convert.ToHexString(hash);
        return true;
    }
    private static FleetResponse Error(string id, string code, string message) => new() { Id = id, ErrorCode = code, Error = message };
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); _listener?.Stop();
        if (_accept != null) try { await _accept.ConfigureAwait(false); } catch { }
        try { await Task.WhenAll(_clients.Values).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }
}
