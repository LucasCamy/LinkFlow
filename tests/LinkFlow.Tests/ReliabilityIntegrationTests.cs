using System.Collections;
using System.IO;
using System.Reflection;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

/// <summary>Cross-module fault and recovery checks. All actuators are simulated; pipes and persistence are real.</summary>
public sealed class ReliabilityIntegrationTests
{
    private static byte Speed(EngineFixture fixture) => fixture.Hw.Hub.LastSpeeds.GetValueOrDefault(1);
    private static EngineCommandRequest Request(LinkFlowEngine engine, Action<ControlConfig> mutate)
    {
        var config = engine.GetConfig();
        long revision = config.Revision;
        mutate(config);
        return new() { Kind = PipeProtocol.SetConfig, ExpectedRevision = revision, Config = config };
    }

    [Fact]
    public async Task Bounded_soak_preserves_safety_command_truth_and_history_across_repeated_failures()
    {
        using var directory = new TestDirectory();
        var config = new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1, Points = [new(0, 45), new(110, 45)] } };
        using var fixture = new EngineFixture(config, options =>
        {
            options.SensorLossTimeout = TimeSpan.FromMilliseconds(160);
            options.TelemetryFreshnessTimeout = TimeSpan.FromMilliseconds(150);
            options.FailSafeRecoveryDelay = TimeSpan.FromMilliseconds(160);
            options.History = new()
            {
                DirectoryPath = directory.Path, SampleInterval = TimeSpan.FromMilliseconds(20),
                MaxMemoryFrames = 12, MaxMemoryEvents = 32, QueueCapacity = 8, MaxDiskBytes = 256 * 1024, MaxSegmentBytes = 64 * 1024
            };
        });
        fixture.Hw.Sensors.ForcedCpuTemp = 40;
        fixture.Hw.Sensors.ForcedGpuTemp = 40;
        EngineFixture.WaitUntil(() => Speed(fixture) == 45 && fixture.Engine.History.Status.StoredFrames >= 4);

        for (int cycle = 0; cycle < 6; cycle++)
        {
            bool sourceFailure = cycle % 2 == 0;
            fixture.Hw.Sensors.ThrowOnRead = sourceFailure;
            fixture.Hw.Hub.FailSpeedReads = !sourceFailure;
            EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().FailSafeActive && Speed(fixture) == 100, because: $"protection in failure cycle {cycle}");
            byte requested = (byte)(50 + cycle);
            var receipt = fixture.Engine.ExecuteCommand(Request(fixture.Engine, next => next.Curve.Points = [new(0, requested), new(110, requested)]));
            EngineFixture.WaitUntil(() => fixture.Engine.GetCommandReceipts().Single(operation => operation.Id == receipt.Id).Targets.Any(target => target.Detail == "deferred-by-safety"));
            EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Fans.Where(fan => fan.CanControlSpeed && !fan.IsPump).All(fan => fan.RequestedPercent == requested));
            Assert.NotEqual(CommandState.Applied, fixture.Engine.GetCommandReceipts().Single(operation => operation.Id == receipt.Id).State);
            Assert.All(fixture.Engine.GetSnapshot().Fans.Where(fan => fan.CanControlSpeed && !fan.IsPump), fan => Assert.Equal(requested, fan.RequestedPercent));
            Assert.Equal(100, Speed(fixture));

            fixture.Hw.Sensors.ThrowOnRead = false;
            fixture.Hw.Hub.FailSpeedReads = false;
            EngineFixture.WaitUntil(() => !fixture.Engine.GetSnapshot().FailSafeActive && Speed(fixture) == requested,
                because: $"latest profile recovered in cycle {cycle}");
            EngineFixture.WaitUntil(() => fixture.Engine.GetCommandReceipts().Single(operation => operation.Id == receipt.Id).State == CommandState.Applied);
            Assert.InRange(fixture.Engine.History.Status.StoredFrames, 1, 12);
            Assert.InRange(fixture.Engine.History.Status.StoredEvents, 1, 32);
        }
        await fixture.Engine.History.FlushAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var status = fixture.Engine.History.Status;
        Assert.True(status.StorageAvailable);
        Assert.InRange(status.DiskBytes, 0, 256 * 1024 + 64 * 1024);
        Assert.All(fixture.Engine.GetSnapshot().TelemetryHealth.Where(health => health.Key is "cpu.temp" or "gpu.temp" or "hub.rpm"),
            health => Assert.Equal(ReadingValidity.Fresh, health.Validity));
        Assert.NotEmpty(fixture.Engine.History.QueryEvents(new() { Kind = "alert.opened", MaxItems = 200 }));
        Assert.NotEmpty(fixture.Engine.History.QueryEvents(new() { Kind = "alert.resolved", MaxItems = 200 }));
    }

    [Fact]
    public void Cached_cooled_coolant_cannot_release_protection_when_acquisition_stops()
    {
        using var fixture = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, options =>
        {
            options.TelemetryFreshnessTimeout = TimeSpan.FromMilliseconds(150);
            options.FailSafeRecoveryDelay = TimeSpan.FromMilliseconds(700);
            options.SensorLossTimeout = TimeSpan.FromSeconds(10);
            options.LaneStallTimeout = TimeSpan.FromMinutes(1);
        });
        fixture.Hw.Hub.CoolantTemp = 51;
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().FailSafeActive && Speed(fixture) == 100);
        fixture.Hw.Hub.CoolantTemp = 44;
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().FailSafeRecovering);
        fixture.Hw.Hub.ReadGate.Reset();
        try
        {
            EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().CoolantTemp == null);
            Thread.Sleep(850);
            var snapshot = fixture.Engine.GetSnapshot();
            Assert.True(snapshot.FailSafeActive);
            Assert.False(snapshot.FailSafeRecovering);
            Assert.Equal(100, Speed(fixture));
            Assert.Contains(snapshot.TelemetryHealth, health => health.Key == "hub.temp:11" && health.Validity == ReadingValidity.Stale);
        }
        finally { fixture.Hw.Hub.ReadGate.Set(); }
        EngineFixture.WaitUntil(() => !fixture.Engine.GetSnapshot().FailSafeActive && Speed(fixture) == 40);
    }

    [Fact]
    public void A_stalled_optional_disk_provider_cannot_hide_an_already_acquired_critical_temperature()
    {
        using var fixture = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, options => options.LaneStallTimeout = TimeSpan.FromMinutes(1));
        fixture.Engine.SetSensorDemand(this, SensorDemand.DiskIo);
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Disks is { Count: 2 } && Speed(fixture) == 40);
        fixture.Hw.Sensors.ReadGate.Reset();
        try
        {
            EngineFixture.WaitUntil(() => fixture.Hw.Sensors.IsReadWaiting);
            fixture.Hw.Sensors.ForcedCpuTemp = 97;
            fixture.Hw.Sensors.DiskReadGate.Reset();
            fixture.Hw.Sensors.ReadGate.Set();
            EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().FailSafeActive && Speed(fixture) == 100,
                because: "temperature acquisition is visible to the independent supervisor before optional disk read completes");
            Assert.DoesNotContain(fixture.Engine.GetSnapshot().Alerts, alert => alert.Key == "engine-stalled");
        }
        finally { fixture.Hw.Sensors.ReadGate.Set(); fixture.Hw.Sensors.DiskReadGate.Set(); }
    }

    [Fact]
    public void Quiet_schedule_and_history_operate_without_any_interface_subscriber()
    {
        using var directory = new TestDirectory();
        var config = new ControlConfig();
        config.Automation.Quiet = new() { Enabled = true, StartHour = 0, EndHour = 0, Profile = FanPresets.Silent };
        using var fixture = new EngineFixture(config, options => options.History = new()
        {
            DirectoryPath = directory.Path, SampleInterval = TimeSpan.FromMilliseconds(20), MaxMemoryFrames = 20
        });
        EngineFixture.WaitUntil(() => fixture.Engine.GetConfig().ActivePreset == FanPresets.Silent && fixture.Engine.History.Status.StoredFrames >= 5);
        Assert.Equal("quiet", fixture.Engine.GetAutomationState().Reason);
        Assert.Equal(40, Speed(fixture));
        var series = fixture.Engine.History.Query(new() { MetricKey = "cpu.temp", FromUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ToUtc = DateTimeOffset.UtcNow.AddSeconds(1) });
        Assert.True(series.Statistics.ValidSamples > 0);
        Assert.Contains(fixture.Engine.History.QueryEvents(new()), entry => entry.Kind == "configuration.changed");
    }

    [Fact]
    public void Persistence_failure_is_visible_while_control_continues_and_recovers_after_storage_returns()
    {
        using var directory = new TestDirectory();
        string blocker = System.IO.Path.Combine(directory.Path, "blocked");
        File.WriteAllText(blocker, "test fixture blocks the configuration directory");
        string configPath = System.IO.Path.Combine(blocker, "config.json");
        using var fixture = new EngineFixture(configure: options =>
        {
            options.ConfigPath = configPath;
            options.SaveDebounce = TimeSpan.FromMilliseconds(20);
            options.History = new() { DirectoryPath = System.IO.Path.Combine(directory.Path, "history") };
        });
        var receipt = fixture.Engine.ExecuteCommand(Request(fixture.Engine, config => config.GlobalPercent = 63));
        EngineFixture.WaitUntil(() => Speed(fixture) == 63 && fixture.Engine.Persistence.State == PersistenceState.Failed);
        Assert.Equal(PersistenceState.Failed, fixture.Engine.GetCommandReceipts().Single(operation => operation.Id == receipt.Id).Persistence);
        Assert.NotNull(fixture.Engine.Persistence.Error);

        File.Delete(blocker);
        Directory.CreateDirectory(blocker);
        var recovered = fixture.Engine.ExecuteCommand(Request(fixture.Engine, config => config.GlobalPercent = 64));
        EngineFixture.WaitUntil(() => fixture.Engine.Persistence.State == PersistenceState.Saved && Speed(fixture) == 64);
        Assert.Equal(PersistenceState.Saved, fixture.Engine.GetCommandReceipts().Single(operation => operation.Id == recovered.Id).Persistence);
        Assert.Equal(64, new JsonFileStore<ControlConfig>(configPath).Load().GlobalPercent);
    }

    [Fact]
    public async Task Concurrent_commands_commit_one_revision_and_explicitly_reject_the_other()
    {
        using var fixture = new EngineFixture();
        for (int cycle = 0; cycle < 8; cycle++)
        {
            var a = Request(fixture.Engine, config => config.GlobalPercent = 60);
            var b = Request(fixture.Engine, config => config.GlobalPercent = 80);
            long before = fixture.Engine.GetConfig().Revision;
            var receipts = await Task.WhenAll(Task.Run(() => fixture.Engine.ExecuteCommand(a)), Task.Run(() => fixture.Engine.ExecuteCommand(b)));
            Assert.Single(receipts, receipt => receipt.State is CommandState.Accepted or CommandState.Applied);
            Assert.Single(receipts, receipt => receipt.State == CommandState.Conflict);
            Assert.Equal(before + 1, fixture.Engine.GetConfig().Revision);
        }
        Assert.Contains(fixture.Engine.History.QueryEvents(new() { MaxItems = 200 }), entry => entry.Kind == "command.conflict");
    }

    [Fact]
    public async Task Offline_client_flood_is_bounded_and_reports_rejections_without_hardware_side_effects()
    {
        using var fixture = new EngineFixture();
        string name = "LinkFlow.QueueSoak." + Guid.NewGuid().ToString("N");
        await using var server = new PipeEngineServer(fixture.Engine, name);
        server.Start();
        using var client = PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name) ?? throw new InvalidOperationException("Test pipe unavailable.");
        // Deliberately do not Start the writer: this is a connected UI whose sender cannot make progress.
        long revision = fixture.Engine.GetConfig().Revision;
        for (int index = 0; index < 1000; index++) client.SubmitCommand(new() { Kind = PipeProtocol.CalibrationStart });
        Assert.InRange(client.Operations.Count, 1, 128);
        Assert.Contains(client.Operations, receipt => receipt.State == CommandState.Rejected && receipt.Message.Contains("Fila"));
        Assert.Equal(revision, fixture.Engine.GetConfig().Revision);
        Assert.Null(fixture.Engine.GetSnapshot().Calibration);
    }

    [Fact]
    public void Replay_cache_retains_fixed_size_digests_and_rejects_overload()
    {
        using var fixture = new EngineFixture(configure: options => options.History = new() { MaxMemoryEvents = 8 });
        int overload = 0;
        for (int index = 0; index < 4200; index++)
        {
            var receipt = fixture.Engine.ExecuteCommand(new() { Kind = "unsupported-soak-command" });
            if (receipt.Message.StartsWith("Limite", StringComparison.Ordinal)) overload++;
        }
        var replay = (IDictionary)typeof(LinkFlowEngine).GetField("_commandReplay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Engine)!;
        Assert.InRange(replay.Count, 1, 4096);
        Assert.True(overload > 0);
        foreach (var value in replay.Values)
            Assert.Equal(64, ((string)value!.GetType().GetField("Item1")!.GetValue(value)!).Length);
        Assert.InRange(fixture.Engine.GetCommandReceipts().Count, 1, 128);
        Assert.InRange(fixture.Engine.History.Status.StoredEvents, 1, 8);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("LinkFlow-integration-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
