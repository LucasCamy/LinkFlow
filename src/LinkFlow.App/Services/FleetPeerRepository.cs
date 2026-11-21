using System.IO;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

public sealed class FleetPeerRepository
{
    private readonly JsonFileStore<List<FleetPeerSettings>> _store;
    public FleetPeerRepository(string? path = null) => _store = new(path ?? Path.Combine(AppPaths.UserDir, "fleet-peers.json"));
    public IReadOnlyList<FleetPeerSettings> Load() => _store.Load().Where(p => p != null && p.Host is { Length: > 0 and <= 253 } &&
        p.Port is > 0 and <= 65535 && p.CertificateSha256 is { Length: 64 } && p.CertificateSha256.All(Uri.IsHexDigit) &&
        p.ProtectedToken is { Length: > 0 and <= 8192 }).Take(32).ToArray();
    public void Save(IEnumerable<FleetPeerSettings> peers)
    {
        var next = peers.Take(33).ToList();
        if (next.Count > 32) throw new InvalidOperationException("O painel acompanha até 32 pares.");
        if (next.Any(p => string.IsNullOrEmpty(p.ProtectedToken))) throw new InvalidDataException("O token do par deve estar protegido pelo Windows.");
        if (!_store.Save(next)) throw new IOException("Não foi possível salvar os pares.");
    }
    public static FleetPeerSettings Pair(string name, string host, int port, string pin, string token)
    {
        var peer = new FleetPeerSettings { Name = name.Trim(), Host = host.Trim(), Port = port,
            CertificateSha256 = pin.Replace(":", string.Empty).Replace(" ", string.Empty).ToUpperInvariant() };
        if (peer.Name.Length is < 1 or > 100) throw new ArgumentException("Informe um nome de até 100 caracteres.");
        _ = new FleetClient(peer, token);
        peer.ProtectedToken = FleetSecretStore.ProtectToken(token);
        return peer;
    }
}
