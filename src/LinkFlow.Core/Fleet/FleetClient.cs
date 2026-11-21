using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Core;

/// <summary>Conecta apenas ao certificado pareado explicitamente, inclusive quando ele é autoassinado.</summary>
public sealed class FleetClient
{
    private readonly FleetPeerSettings _peer;
    private readonly string _token;
    private readonly byte[] _pin;
    public FleetClient(FleetPeerSettings peer, string token)
    {
        _peer = Json.Clone(peer);
        _token = token;
        if (string.IsNullOrWhiteSpace(peer.Host) || peer.Host.Length > 253 || peer.Host.Any(char.IsControl) || peer.Port is < 1 or > 65535)
            throw new ArgumentException("Endereço do agente inválido.");
        if (peer.CertificateSha256.Length != 64 || peer.CertificateSha256.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("Impressão do certificado inválida.");
        if (token.Length is < 32 or > 256) throw new ArgumentException("Credencial inválida.");
        _pin = Convert.FromHexString(peer.CertificateSha256);
    }
    public static string CertificatePin(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public Task<FleetSnapshot> GetSnapshotAsync(CancellationToken ct = default) => RequestAsync<FleetSnapshot>(new() { Kind = "snapshot" }, ct);
    public Task<CommandReceipt> SubmitCommandAsync(EngineCommandRequest request, CancellationToken ct = default) => RequestAsync<CommandReceipt>(new() { Kind = "command", Command = request }, ct);
    public Task<HistorySeries> QueryHistoryAsync(HistoryQuery query, CancellationToken ct = default) => RequestAsync<HistorySeries>(new() { Kind = "history", History = query }, ct);
    public async Task<IReadOnlyList<OperationalEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default) => await RequestAsync<List<OperationalEvent>>(new() { Kind = "events", Events = query }, ct).ConfigureAwait(false);
    private async Task<T> RequestAsync<T>(FleetRequest request, CancellationToken ct)
    {
        request.Token = _token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(_peer.Host, _peer.Port, timeout.Token).ConfigureAwait(false);
        using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate != null &&
            CryptographicOperations.FixedTimeEquals(_pin, SHA256.HashData(certificate.GetRawCertData())) &&
            DateTime.TryParse(certificate.GetExpirationDateString(), out var expires) && expires.ToUniversalTime() > DateTime.UtcNow &&
            DateTime.TryParse(certificate.GetEffectiveDateString(), out var starts) && starts.ToUniversalTime() <= DateTime.UtcNow);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        { TargetHost = _peer.Host, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, timeout.Token).ConfigureAwait(false);
        var encoded = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json.Compact) + "\n");
        if (encoded.Length > PipeProtocol.MaxMessageBytes) throw new InvalidDataException("Solicitação grande demais.");
        await tls.WriteAsync(encoded, timeout.Token).ConfigureAwait(false);
        await tls.FlushAsync(timeout.Token).ConfigureAwait(false);
        using var pending = new MemoryStream();
        string? line = await PipeProtocol.ReadLineAsync(tls, new byte[8192], pending, timeout.Token).ConfigureAwait(false);
        var response = line == null ? null : JsonSerializer.Deserialize<FleetResponse>(line, Json.Compact);
        if (response == null || response.Id != request.Id) throw new InvalidDataException("Resposta inválida do agente.");
        if (response.ErrorCode != null) throw new FleetRequestException(response.ErrorCode, response.Error ?? "Solicitação recusada.");
        return PipeProtocol.Read<T>(response.Data) ?? throw new InvalidDataException("Resposta vazia do agente.");
    }
}
