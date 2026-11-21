using System.Diagnostics.Metrics;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class RgbControlObservabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 6, 47, 0, TimeSpan.Zero);
    private static OperationalHistory CreateHistory() => new(new() { DirectoryPath = null, TimeProvider = new FixedClock() });
    private static RgbControlStatus Ready(int seconds = 0) => new()
    {
        ControlAvailable = true, AdapterObservation = RgbAdapterObservation.Present, ControllerName = "AUMA0-E6K5-1110",
        ControllerLedCount = 12, LastOutputAccepted = true, LastOutputAttemptUtc = Start.AddSeconds(seconds),
        LastOutputAcceptedUtc = Start.AddSeconds(seconds)
    };
    private static RgbNativeOperationResult Fault(int seconds, int status = -6, ushort register = 0x8020) => new()
    { AtUtc = Start.AddSeconds(seconds), Operation = RgbNativeOperation.Read, Status = status, Register = register, Command = 0 };
    private static RgbControlStatus Failed(int seconds, long failures = 1) => new()
    {
        ControlAvailable = false, InitializationRetryNeeded = true, AdapterObservation = RgbAdapterObservation.Present,
        ControllerName = "AUMA0-E6K5-1110", LastOutputAccepted = false, LastOutputAttemptUtc = Start.AddSeconds(seconds),
        LastNativeFailure = Fault(seconds), LastNativeOperation = Fault(seconds),
        TotalNativeFailures = failures, TotalOutputFailures = failures, ConsecutiveOutputFailures = failures
    };
    private static void Observe(OperationalHistory history, int seconds, RgbControlStatus? status, bool legacyAvailable = false, bool? legacyApplied = null) =>
        history.RecordSnapshot(new() { Timestamp = Start.AddSeconds(seconds).LocalDateTime, GpuRgbControl = status,
            GpuRgbAvailable = legacyAvailable, GpuRgbApplied = legacyApplied });
    private static List<OperationalEvent> Events(OperationalHistory history, string? device = "rgb.gpu") => history.QueryEvents(new()
    { FromUtc = Start.AddSeconds(-1), ToUtc = Start.AddMinutes(5), DeviceId = device, MaxItems = 1000 }).OrderBy(e => e.TimestampUtc).ToList();

    [Theory]
    [InlineData("hub")]
    [InlineData("lcd")]
    [InlineData("rgb.motherboard")]
    public async Task First_availability_is_discovery_and_only_a_subsequent_loss_has_recovery_duration(string key)
    {
        await using var history = CreateHistory();
        void Sample(int second, bool available) => history.RecordSnapshot(new()
        {
            Timestamp = Start.AddSeconds(second).LocalDateTime,
            HubConnected = key == "hub" && available, LcdConnected = key == "lcd" && available,
            MotherboardRgbAvailable = key == "rgb.motherboard" && available
        });
        Sample(0, false);
        Sample(10, false);
        Sample(20, true);
        var first = Assert.Single(Events(history, key));
        Assert.Equal("device.discovered", first.Kind);
        Assert.False(first.Data.ContainsKey("recoverySeconds"));
        Sample(25, false);
        Sample(40, true);
        var events = Events(history, key);
        Assert.Equal(["device.discovered", "device.lost", "device.recovered"], events.Select(e => e.Kind));
        Assert.Equal("15.000", events[^1].Data["recoverySeconds"]);
    }

    [Fact]
    public async Task First_gpu_control_availability_never_claims_physical_recovery()
    {
        await using var history = CreateHistory();
        Observe(history, 0, new() { AdapterObservation = RgbAdapterObservation.Unknown });
        Observe(history, 10, Ready(10));
        var events = Events(history);
        var detected = Assert.Single(events, e => e.Kind.StartsWith("rgb.control.", StringComparison.Ordinal));
        Assert.Equal("rgb.control.detected", detected.Kind);
        Assert.False(detected.Data.ContainsKey("recoverySeconds"));
        Assert.All(events, e => Assert.DoesNotContain("device.", e.Kind));
        Assert.Equal("unverified", detected.Data["physicalConnection"]);
    }

    [Fact]
    public async Task Native_failure_is_a_control_outage_even_with_last_enumerated_adapter_present()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        Observe(history, 2, Failed(2));
        var events = Events(history);
        var outage = Assert.Single(events, e => e.Kind == "rgb.control.unavailable");
        Assert.Equal("Present", outage.Data["adapterObservation"]);
        Assert.Equal("-6", outage.Data["nativeStatus"]);
        Assert.Equal("0x8020", outage.Data["nativeRegister"]);
        Assert.Equal("0x00", outage.Data["nativeCommand"]);
        Assert.Equal("false", outage.Data["outputAccepted"]);
        Assert.Equal("unverified", outage.Data["physicalConnection"]);
        Assert.Single(events, e => e.Kind == "rgb.transport.failed");
        Assert.Single(events, e => e.Kind == "rgb.output.rejected");
        Assert.DoesNotContain(events, e => e.Kind.StartsWith("device.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Repeated_faults_and_retry_counters_do_not_emit_one_event_per_attempt()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        for (int i = 1; i <= 60; i++)
            Observe(history, i, Failed(i, i) with { InitializationAttempts = i, RecoveryAttemptCount = i,
                RecoveryPending = true, NextInitializationAttemptUtc = Start.AddSeconds(i + 10) });
        var events = Events(history);
        Assert.Single(events, e => e.Kind == "rgb.control.unavailable");
        Assert.Single(events, e => e.Kind == "rgb.transport.failed");
        Assert.Single(events, e => e.Kind == "rgb.output.rejected");
        Assert.Equal(6, events.Count);
    }

    [Fact]
    public async Task Initialization_attempts_do_not_publish_a_completed_loss_or_adapter_change()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        Observe(history, 1, Ready() with { Initializing = true, ControlAvailable = false,
            AdapterObservation = RgbAdapterObservation.Unknown, LastOutputAccepted = null });
        Observe(history, 2, Ready(2));
        Assert.DoesNotContain(Events(history), e => e.Kind is "rgb.control.unavailable" or "rgb.control.recovered" or "rgb.output.rejected");
        Assert.Single(Events(history), e => e.Kind == "rgb.adapter.observation");
    }

    [Fact]
    public async Task Repeated_probes_without_completed_output_do_not_rearm_an_existing_rejection()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        for (int i = 1; i < 10; i += 2)
        {
            Observe(history, i, Failed(i) with { LastOutputAccepted = null });
            Observe(history, i + 1, Failed(i + 1));
        }
        Assert.Single(Events(history), e => e.Kind == "rgb.output.rejected");
        Assert.Single(Events(history), e => e.Kind == "rgb.transport.failed");
    }

    [Fact]
    public async Task Probe_recovery_is_separate_from_output_acceptance_and_retains_the_fault()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        Observe(history, 2, Failed(2));
        Observe(history, 10, Ready(10) with { LastOutputAccepted = null, LastOutputAcceptedUtc = Start,
            LastNativeFailure = Fault(2) });
        var events = Events(history);
        var recovered = Assert.Single(events, e => e.Kind == "rgb.control.recovered");
        Assert.Equal("8.000", recovered.Data["recoverySeconds"]);
        Assert.Equal("unknown", recovered.Data["outputAccepted"]);
        Assert.Equal("-6", recovered.Data["nativeStatus"]);
        Assert.Single(events, e => e.Kind == "rgb.output.accepted"); // Original output, not the new probe.
        Observe(history, 11, Ready(11) with { LastNativeFailure = Fault(2) });
        Observe(history, 12, Ready(11) with { LastNativeFailure = Fault(2) });
        Assert.Equal(2, Events(history).Count(e => e.Kind == "rgb.output.accepted"));
        Assert.Single(Events(history), e => e.Kind == "rgb.transport.failed");
    }

    [Fact]
    public async Task Identical_native_error_after_recovered_output_opens_a_new_episode_without_replaying_retained_error()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        Observe(history, 2, Failed(2));
        Observe(history, 4, Ready(4) with { LastNativeFailure = Fault(2) });
        Observe(history, 5, Ready(4) with { LastNativeFailure = Fault(2) });
        Observe(history, 6, Failed(6));
        Observe(history, 7, Failed(7));
        var events = Events(history);
        Assert.Equal(2, events.Count(e => e.Kind == "rgb.transport.failed"));
        Assert.Equal(2, events.Count(e => e.Kind == "rgb.control.unavailable"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_sequence_orders_recovery_and_new_identical_fault_when_utc_repeats_or_regresses(bool regressedClock)
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready() with { LastOutputAcceptedNativeSequence = 1 });
        Observe(history, 1, Failed(2) with { LastNativeFailure = Fault(2) with { Sequence = 2 } });
        Observe(history, 2, Failed(2) with { LastNativeFailure = Fault(2) with { Sequence = 3 } });
        var restored = Ready(2) with { LastNativeFailure = Fault(2) with { Sequence = 3 }, LastOutputAcceptedNativeSequence = 4,
            LastOutputAcceptedUtc = Start.AddSeconds(regressedClock ? -120 : 2) };
        Observe(history, 3, restored);
        Observe(history, 4, restored); // Retained error cannot be replayed after recovery.
        Assert.Single(Events(history), e => e.Kind == "rgb.transport.failed");
        Observe(history, 5, Failed(2) with { LastNativeFailure = Fault(2) with { Sequence = 5 } });
        Observe(history, 6, Failed(2) with { LastNativeFailure = Fault(2) with { Sequence = 6 } });
        var faults = Events(history).Where(e => e.Kind == "rgb.transport.failed").ToArray();
        Assert.Equal(2, faults.Length);
        Assert.Equal("2", faults[0].Data["nativeFailureSequence"]);
        Assert.Equal("5", faults[1].Data["nativeFailureSequence"]);
        Assert.Equal(faults[0].Data["nativeFailureUtc"], faults[1].Data["nativeFailureUtc"]);
    }

    [Fact]
    public async Task A_changed_native_failure_signature_and_enumeration_failure_remain_visible()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        Observe(history, 2, Failed(2));
        Observe(history, 3, Failed(3) with { AdapterObservation = RgbAdapterObservation.EnumerationUnavailable,
            LastNativeFailure = Fault(3) with { Operation = RgbNativeOperation.EnumerateAdapters, Register = null, Command = null } });
        var events = Events(history);
        Assert.Equal(2, events.Count(e => e.Kind == "rgb.transport.failed"));
        var enumeration = Assert.Single(events, e => e.Kind == "rgb.adapter.observation" && e.Severity == AlertSeverity.Warning);
        Assert.Equal("EnumerationUnavailable", enumeration.Data["adapterObservation"]);
        Assert.Equal("unverified", enumeration.Data["physicalConnection"]);
    }

    [Fact]
    public async Task Native_event_details_are_bounded_while_raw_status_register_and_command_remain_exact()
    {
        await using var history = CreateHistory();
        Observe(history, 0, Ready());
        Observe(history, 1, Failed(1) with { Source = new('s', 8000), ControllerName = new('c', 8000),
            DiagnosticText = EngineText.Of(EngineTexts.GpuNvapiAccess, new string('d', 8000)) });
        var fault = Assert.Single(Events(history), e => e.Kind == "rgb.transport.failed");
        Assert.Equal(160, fault.Data["backend"].Length);
        Assert.Equal(160, fault.Data["controller"].Length);
        Assert.Equal(512, fault.Data["diagnostic"].Length);
        Assert.InRange(fault.Data.Count, 1, 24);
        Assert.Equal("-6", fault.Data["nativeStatus"]);
        Assert.Equal("0x8020", fault.Data["nativeRegister"]);
        Assert.Equal("0x00", fault.Data["nativeCommand"]);
    }

    [Fact]
    public async Task Legacy_rgb_driver_keeps_control_events_without_inventing_native_evidence()
    {
        await using var history = CreateHistory();
        Observe(history, 0, null);
        Observe(history, 10, null, legacyAvailable: true, legacyApplied: true);
        Observe(history, 20, null, legacyAvailable: false, legacyApplied: false);
        Observe(history, 30, null, legacyAvailable: true, legacyApplied: false);
        var events = Events(history);
        Assert.Equal(["rgb.control.detected", "rgb.control.unavailable", "rgb.control.recovered"],
            events.Where(e => e.Kind.StartsWith("rgb.control.", StringComparison.Ordinal)).Select(e => e.Kind));
        Assert.All(events, e => Assert.Equal("legacy-rgb-availability", e.Data["evidence"]));
        Assert.DoesNotContain(events, e => e.Kind == "rgb.transport.failed" || e.Data.ContainsKey("nativeStatus"));
    }

    [Theory]
    [InlineData("rgb.control.detected")]
    [InlineData("rgb.control.unavailable")]
    [InlineData("rgb.control.recovered")]
    [InlineData("rgb.adapter.observation")]
    [InlineData("rgb.output.accepted")]
    [InlineData("rgb.output.rejected")]
    [InlineData("rgb.transport.failed")]
    public void Rgb_timeline_presents_localized_labels_and_raw_sdk_evidence_without_physical_claims(string kind)
    {
        Assert.NotEqual(kind, OperationalText.Event(kind));
        string detail = OperationalText.RgbEventDetail(new() { Kind = kind, Data = new()
            { ["adapterObservation"] = "EnumerationUnavailable", ["nativeStatus"] = "-6", ["nativeRegister"] = "0x8020", ["nativeCommand"] = "0x00" } });
        Assert.Contains("SDK -6", detail);
        Assert.Contains("0x8020", detail);
        Assert.Contains(OperationalText.Pick("não são verificadas", "does not verify"), detail);
    }

    [Fact]
    public async Task Gpu_channel_recovery_uses_its_own_instrument_and_does_not_increment_device_reconnections()
    {
        long channelRecoveries = 0, physicalRecoveries = 0;
        using var ownCalls = new ThreadLocal<bool>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "LinkFlow.Engine" && instrument.Name is "linkflow.rgb.control.recoveries" or "linkflow.device.recoveries")
                current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (!ownCalls.Value) return; // Other parallel fixtures have independent meters with the same public instrument names.
            if (instrument.Name == "linkflow.rgb.control.recoveries") Interlocked.Add(ref channelRecoveries, value);
            if (instrument.Name == "linkflow.device.recoveries") Interlocked.Add(ref physicalRecoveries, value);
        });
        listener.Start();
        await using var history = CreateHistory();
        ownCalls.Value = true;
        Observe(history, 0, new());
        Observe(history, 1, Ready(1));
        Assert.Equal(0, Interlocked.Read(ref channelRecoveries));
        Observe(history, 2, Failed(2));
        Observe(history, 5, Ready(5));
        ownCalls.Value = false;
        Assert.Equal(1, Interlocked.Read(ref channelRecoveries));
        Assert.Equal(0, Interlocked.Read(ref physicalRecoveries));
    }

    [Fact]
    public void Snapshot_reads_the_cached_provider_once_and_never_reads_its_legacy_properties()
    {
        var provider = new CachedRgb { Cached = Failed(2) with { TotalNativeFailures = 400, InitializationAttempts = 20 } };
        using var engine = new LinkFlowEngine(new ProviderFactory(provider), new() { ConfigPath = null }, new());
        var snapshot = engine.GetSnapshot();
        Assert.Equal(1, provider.Reads);
        Assert.NotNull(snapshot.GpuRgbControl);
        Assert.False(snapshot.GpuRgbAvailable);
        Assert.False(snapshot.GpuRgbApplied);
        Assert.Equal(400, snapshot.GpuRgbControl.TotalNativeFailures);
        Assert.Equal(-6, snapshot.GpuRgbControl.LastNativeFailure!.Status);
        var restored = Json.Clone(snapshot);
        Assert.Equal(snapshot.GpuRgbControl, restored.GpuRgbControl);
        provider.Cached = Ready(5) with { LastNativeFailure = Fault(2), LastOutputAccepted = null };
        var probe = engine.GetSnapshot();
        Assert.Equal(2, provider.Reads);
        Assert.True(probe.GpuRgbAvailable);
        Assert.Null(probe.GpuRgbApplied);
        Assert.Equal(-6, probe.GpuRgbControl!.LastNativeFailure!.Status);
        provider.Cached = Ready(7) with { LastNativeFailure = Fault(2) };
        engine.UpdateConfig(config => config.Rgb.SyncGpu = false);
        var disabled = engine.GetSnapshot();
        Assert.Equal(3, provider.Reads);
        Assert.Null(disabled.GpuRgbApplied);
        Assert.True(disabled.GpuRgbControl!.LastOutputAccepted); // Historical ACK stays available, without claiming a currently requested output.
        Assert.Equal(400, snapshot.GpuRgbControl.TotalNativeFailures);
    }

    [Fact]
    public void Engine_snapshot_keeps_legacy_driver_compatibility_when_provider_is_absent()
    {
        var simulated = new SimulatedHardwareFactory();
        using var engine = new LinkFlowEngine(simulated, new() { ConfigPath = null }, new());
        var snapshot = engine.GetSnapshot();
        Assert.Null(snapshot.GpuRgbControl);
        Assert.Equal(simulated.GpuRgb.IsAvailable, snapshot.GpuRgbAvailable);
        Assert.Equal(simulated.GpuRgb.Name, snapshot.GpuRgbName);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Start;
    }
    private sealed class CachedRgb : IRgbDevice, IRgbControlStatusProvider
    {
        public RgbControlStatus Cached { get; set; } = new();
        public int Reads { get; private set; }
        public RgbControlStatus ControlStatus { get { Reads++; return Cached; } }
        public bool IsAvailable => throw new InvalidOperationException("Snapshot must use the captured provider state.");
        public string Name => throw new InvalidOperationException("Snapshot must use the captured provider state.");
        public bool NeedsInitializationRetry => throw new InvalidOperationException("Snapshot must use the captured provider state.");
        public string? Diagnostic => throw new InvalidOperationException("Snapshot must use the captured provider state.");
        public EngineText? DiagnosticText => throw new InvalidOperationException("Snapshot must use the captured provider state.");
        public void Initialize() => throw new InvalidOperationException("This snapshot test must not initialize any backend.");
        public bool SetColor(byte r, byte g, byte b, byte brightnessPercent) => throw new InvalidOperationException("This snapshot test must not control any device.");
        public void Dispose() { }
    }
    private sealed class ProviderFactory(CachedRgb gpu) : IHardwareFactory
    {
        private readonly SimulatedHardwareFactory _simulated = new();
        public bool IsSimulated => true;
        public IFanHub CreateHub() => _simulated.CreateHub();
        public ISensorSource CreateSensors() => _simulated.CreateSensors();
        public ILcdDisplay CreateLcd() => _simulated.CreateLcd();
        public IRgbDevice CreateGpuRgb() => gpu;
        public IRgbDevice CreateMotherboardRgb() => _simulated.CreateMotherboardRgb();
        public IRgbDevice CreateRamRgb() => _simulated.CreateRamRgb();
        public IRgbDevice CreatePeripheralsRgb() => _simulated.CreatePeripheralsRgb();
    }
}
