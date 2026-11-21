using LinkFlow.App.Services;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class WaitingServiceClientTests
{
    [Fact]
    public void Waiting_interface_rejects_changes_without_assuming_hardware()
    {
        using var client = new WaitingServiceClient(() => null);
        bool observed = false;
        client.CommandUpdated += _ => throw new InvalidOperationException("disposed observer");
        client.CommandUpdated += _ => observed = true;
        client.UpdateConfig(c => c.GlobalPercent = 99);
        Assert.False(client.IsConnected);
        Assert.False(client.CanWrite);
        Assert.True(client.IsRemote);
        Assert.Equal(55, client.Config.GlobalPercent);
        Assert.Equal(CommandState.Rejected, Assert.Single(client.Operations).State);
        Assert.True(observed);
    }

    [Fact]
    public async Task Waiting_interface_connects_to_a_later_service_and_preserves_protocol_metadata()
    {
        using var fixture = new EngineFixture();
        string pipe = "LinkFlow.Waiting.Test." + Guid.NewGuid().ToString("N");
        await using var server = new PipeEngineServer(fixture.Engine, pipe);
        server.Start();
        using var client = new WaitingServiceClient(() => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2), pipe));
        int configurations = 0;
        client.ConfigChanged += _ => throw new InvalidOperationException("disposed observer");
        client.ConfigChanged += _ => Interlocked.Increment(ref configurations);
        client.Start();
        EngineFixture.WaitUntil(() => client.CanWrite && configurations > 0);
        Assert.Equal(PipeProtocol.Version, client.NegotiatedProtocolVersion);
        Assert.NotNull(client.SnapshotAge);
        Assert.True(client.IsSnapshotFresh);
        client.UpdateConfig(c => c.GlobalPercent = 85);
        EngineFixture.WaitUntil(() => fixture.Engine.GetConfig().GlobalPercent == 85);
    }
}
