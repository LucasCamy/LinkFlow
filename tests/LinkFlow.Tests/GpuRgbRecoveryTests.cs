using System.Diagnostics;

namespace LinkFlow.Tests;

public class GpuRgbRecoveryTests
{
    private static long S(double seconds) => 1_000_000 + (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void Unavailable_access_waits_before_first_probe_then_backs_off_to_a_bound()
    {
        var recovery = new GpuRgbRecovery(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(80), TimeSpan.FromSeconds(30));
        recovery.Unavailable(S(0));
        Assert.Equal(S(10), recovery.RetryAt);
        Assert.False(recovery.Due(S(9.99)));
        Assert.True(recovery.Due(S(10)));
        recovery.InitializationAttempted(S(10));
        Assert.Equal(S(30), recovery.RetryAt);
        recovery.InitializationAttempted(S(30));
        Assert.Equal(S(70), recovery.RetryAt);
        recovery.InitializationAttempted(S(70));
        Assert.Equal(S(150), recovery.RetryAt);
        recovery.InitializationAttempted(S(150));
        Assert.Equal(S(230), recovery.RetryAt);
        recovery.Unavailable(S(151));
        Assert.Equal(S(230), recovery.RetryAt); // Re-observing failure cannot create immediate probes.
        Assert.Equal(4, recovery.Attempts);
    }

    [Fact]
    public void Brief_successes_do_not_reset_a_flapping_episode_or_its_probe_budget()
    {
        var recovery = new GpuRgbRecovery(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30));
        recovery.Unavailable(S(0));
        recovery.InitializationAttempted(S(1));
        recovery.OutputAccepted(S(1));
        recovery.OutputAccepted(S(2));
        recovery.Poll(S(2));
        recovery.Unavailable(S(2.1));
        Assert.Equal(S(3), recovery.RetryAt);
        recovery.InitializationAttempted(S(3));
        recovery.OutputAccepted(S(3));
        recovery.OutputAccepted(S(5));
        recovery.Unavailable(S(5.1));
        recovery.Poll(S(100));
        Assert.True(recovery.Pending);
        Assert.Equal(2, recovery.Attempts);
        Assert.Equal(2, recovery.BackoffStep);
        Assert.Equal(S(7), recovery.RetryAt);
    }

    [Fact]
    public void Silence_after_one_accepted_frame_is_not_evidence_of_stability()
    {
        var recovery = new GpuRgbRecovery(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30));
        recovery.Unavailable(S(0));
        recovery.InitializationAttempted(S(1));
        recovery.OutputAccepted(S(1));
        recovery.Poll(S(1000));
        Assert.True(recovery.Pending);
        recovery.OutputAccepted(S(1001));
        recovery.Poll(S(1001));
        Assert.False(recovery.Pending);
    }

    [Fact]
    public void Observed_success_across_the_stable_window_resets_the_next_episode()
    {
        var recovery = new GpuRgbRecovery(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30));
        recovery.Unavailable(S(0));
        recovery.InitializationAttempted(S(1));
        recovery.InitializationAttempted(S(3));
        recovery.OutputAccepted(S(4));
        recovery.OutputAccepted(S(33.99));
        recovery.Poll(S(34));
        Assert.True(recovery.Pending);
        recovery.OutputAccepted(S(34));
        recovery.Poll(S(34));
        Assert.False(recovery.Pending);
        Assert.Equal(0, recovery.Attempts);
        Assert.Null(recovery.RetryAt);
        recovery.Unavailable(S(35));
        Assert.Equal(S(36), recovery.RetryAt);
        Assert.Equal(0, recovery.BackoffStep);
    }

    [Fact]
    public void A_rejected_output_restarts_stability_even_when_adapter_enumeration_still_succeeds()
    {
        var recovery = new GpuRgbRecovery(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30));
        recovery.Unavailable(S(0));
        recovery.InitializationAttempted(S(1));
        recovery.OutputAccepted(S(2));
        recovery.OutputRejected();
        recovery.OutputAccepted(S(20));
        recovery.OutputAccepted(S(49));
        recovery.Poll(S(49));
        Assert.True(recovery.Pending);
        recovery.OutputAccepted(S(50));
        recovery.Poll(S(50));
        Assert.False(recovery.Pending);
    }

    [Fact]
    public void Extreme_options_and_timestamp_arithmetic_remain_bounded()
    {
        var recovery = new GpuRgbRecovery(TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.MinValue);
        recovery.Unavailable(long.MaxValue - 1);
        Assert.Equal(long.MaxValue, recovery.RetryAt);
        for (int i = 0; i < 100; i++) recovery.InitializationAttempted(long.MaxValue - 1);
        Assert.Equal(long.MaxValue, recovery.RetryAt);
        Assert.Equal(30, recovery.BackoffStep);
        Assert.Equal(100, recovery.Attempts);
        recovery.Reset();
        Assert.False(recovery.Pending);
        Assert.Null(recovery.RetryAt);
    }

    [Fact]
    public void Engine_reprobes_only_the_failed_rgb_channel_and_requires_stable_outputs_before_resetting_backoff()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        var hardware = new FakeGpuHardwareFactory(gpu);
        hardware.Inner.Sensors.OpenDelay = TimeSpan.Zero;
        var options = new EngineOptions
        {
            ConfigPath = null, History = new OperationalHistoryOptions(),
            TelemetryInterval = TimeSpan.FromMilliseconds(20), SnapshotInterval = TimeSpan.FromMilliseconds(20),
            AuxRgbMinInterval = TimeSpan.FromMilliseconds(20), ReconnectInterval = TimeSpan.FromMilliseconds(80),
            RgbSync = new RgbSyncPolicy
            {
                ReinitMax = TimeSpan.FromMilliseconds(250), StableAfter = TimeSpan.FromMilliseconds(100),
                LogAfter = TimeSpan.FromHours(1), SleepGap = TimeSpan.FromHours(1), StaticRefresh = null
            }
        };
        var config = new ControlConfig();
        config.Rgb.Effect = RgbEffect.Palette;
        using var engine = new LinkFlowEngine(hardware, options, config);
        engine.Start();
        EngineFixture.WaitUntil(() => engine.CaptureGpuRgbControlStatus()?.LastOutputAccepted == true);
        int hubConnections = hardware.Inner.Hub.ConnectCalls;
        int motherboardInitializations = hardware.Inner.MotherboardRgb.InitializationCalls;
        backend.PersistentColorStatus = -6;
        EngineFixture.WaitUntil(() => engine.CaptureGpuRgbControlStatus() is
            { ControlAvailable: false, RecoveryPending: true, RecoveryAttemptCount: >= 3, LastOutputAccepted: false },
            because: "SDK-6 reproduzido com re-probes crescentes e nenhuma confirmação de saída");
        var failed = engine.CaptureGpuRgbControlStatus()!;
        Assert.Equal(-6, failed.LastNativeFailure!.Status);
        Assert.NotNull(failed.NextInitializationAttemptUtc);
        Assert.True(failed.TotalNativeFailures >= 4);
        Assert.Equal(1, backend.NativeInitializationCalls);
        var attempts = backend.EnumerationTimestamps;
        Assert.True(Stopwatch.GetElapsedTime(attempts[1], attempts[2]) >= TimeSpan.FromMilliseconds(140));
        Assert.True(Stopwatch.GetElapsedTime(attempts[2], attempts[3]) >= TimeSpan.FromMilliseconds(230));
        Assert.Equal(hubConnections, hardware.Inner.Hub.ConnectCalls);
        Assert.Equal(motherboardInitializations, hardware.Inner.MotherboardRgb.InitializationCalls);
        Assert.False(engine.GetSnapshot().FailSafeActive);

        backend.PersistentColorStatus = 0;
        EngineFixture.WaitUntil(() => engine.CaptureGpuRgbControlStatus() is
            { ControlAvailable: true, LastOutputAccepted: true, RecoveryPending: false, RecoveryAttemptCount: 0 },
            because: "envios aceitos durante janela estável encerram recuperação sem apagar última falha");
        Assert.Equal(-6, engine.CaptureGpuRgbControlStatus()!.LastNativeFailure!.Status);
        Assert.Equal(hubConnections, hardware.Inner.Hub.ConnectCalls);
    }

    private sealed class FakeGpuHardwareFactory(AsusGpuRgb gpu) : IHardwareFactory
    {
        internal SimulatedHardwareFactory Inner { get; } = new();
        public bool IsSimulated => true;
        public IFanHub CreateHub() => Inner.Hub;
        public ISensorSource CreateSensors() => Inner.Sensors;
        public ILcdDisplay CreateLcd() => Inner.Lcd;
        public IRgbDevice CreateGpuRgb() => gpu;
        public IRgbDevice CreateMotherboardRgb() => Inner.MotherboardRgb;
        public IRgbDevice CreateRamRgb() => Inner.RamRgb;
        public IRgbDevice CreatePeripheralsRgb() => Inner.PeripheralsRgb;
    }
}
