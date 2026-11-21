using System.IO;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

public sealed class FleetPresentationTests
{
    [Fact]
    public void Invalid_saved_peers_do_not_break_the_panel()
    {
        string path = Path.Combine(Path.GetTempPath(), "LinkFlow-peers-" + Guid.NewGuid().ToString("N"), "peers.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[{\"host\":null,\"certificateSha256\":null},{\"host\":\"localhost\",\"port\":-1,\"certificateSha256\":\"bad\"}]");
        Assert.Empty(new FleetPeerRepository(path).Load());
    }

    [Theory]
    [InlineData(20.0)]
    [InlineData(null)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_fresh_response_with_old_or_unknown_origin_age_is_not_live(double? age)
    {
        var peer = new FleetPeerViewModel(new());
        peer.Apply(new() { SnapshotAgeSeconds = age, Permissions = FleetPermission.Observe | FleetPermission.Control });
        Assert.False(peer.Current);
        Assert.False(peer.CanControl);
        Assert.Equal("—", peer.Readings);
    }

    [Fact]
    public void Persisted_peer_uses_dpapi_instead_of_plaintext_credentials()
    {
        string token = new('a', 64);
        string path = Path.Combine(Path.GetTempPath(), "LinkFlow-peers-" + Guid.NewGuid().ToString("N"), "peers.json");
        var repository = new FleetPeerRepository(path);
        var peer = FleetPeerRepository.Pair("PC", "localhost", 47931, new string('b', 64), token);
        repository.Save([peer]);
        Assert.DoesNotContain(token, File.ReadAllText(path));
        Assert.Equal(token, FleetSecretStore.UnprotectToken(Assert.Single(repository.Load()).ProtectedToken));
    }

    [Fact]
    public void Offline_peer_does_not_display_old_values_as_current_or_offer_control()
    {
        var peer = new FleetPeerViewModel(new());
        peer.Apply(new() { SnapshotAgeSeconds = 0, Permissions = FleetPermission.Observe | FleetPermission.Control, Snapshot = new() { CpuTemp = 42 } });
        Assert.True(peer.CanControl);
        peer.Fail();
        Assert.False(peer.CanControl);
        Assert.Equal("—", peer.Readings);
    }

    [Fact]
    public void Peer_repository_limits_count_and_rejects_unprotected_empty_tokens()
    {
        var repository = new FleetPeerRepository(Path.Combine(Path.GetTempPath(), "LinkFlow-peers-" + Guid.NewGuid().ToString("N"), "peers.json"));
        Assert.Throws<InvalidOperationException>(() => repository.Save(Enumerable.Range(0, 33).Select(_ => new FleetPeerSettings { ProtectedToken = "encrypted" })));
        Assert.Throws<InvalidDataException>(() => repository.Save([new()]));
    }
}
