namespace LinkFlow.Tests;

public sealed class ConsumerIsolationTests
{
    [Fact]
    public void A_failing_snapshot_consumer_does_not_suppress_other_clients()
    {
        using var fixture = new EngineFixture();
        fixture.Engine.SnapshotUpdated += _ => throw new InvalidOperationException("simulated consumer failure");
        using var client = new LocalEngineClient(fixture.Engine);
        EngineFixture.WaitUntil(() => client.Snapshot?.CpuSensorAvailable == true);
    }

    [Fact]
    public void A_failing_configuration_consumer_does_not_suppress_the_following_observer()
    {
        using var fixture = new EngineFixture();
        fixture.Engine.ConfigChanged += (_, _) => throw new InvalidOperationException("simulated consumer failure");
        int observations = 0;
        fixture.Engine.ConfigChanged += (_, _) => Interlocked.Increment(ref observations);
        fixture.Engine.UpdateConfig(c => c.ActivePreset = "consumer test");
        Assert.Equal(1, observations);
    }
}
