using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class PeripheralRgbDiagnosticTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LinkFlow-peripheral-diagnostics-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };

    public PeripheralRgbDiagnosticTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Export_advances_independently_while_initialization_is_blocked_and_never_reads_the_backend()
    {
        using var client = new FakeClient();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeBackend? backend = null;
        using var bridge = new PeripheralRgbBridge(client, token => backend = new(token)
        {
            InitializeAction = () => { entered.TrySetResult(); release.Wait(token); }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = bridge.DiagnosticSnapshot;
            Assert.Equal(PeripheralRgbOperationKind.Initialize, pending.InFlight!.Kind);
            Assert.Equal(1, pending.OperationRequests);
            Assert.Equal(0, pending.OperationCompletions);
            int reads = backend!.AvailabilityReads;
            string path = Path.Combine(_directory, "current.json");
            bridge.StartDiagnostics(path);
            await WaitForAsync(() => bridge.DiagnosticExportStatus.SuccessfulWrites >= 1);
            var first = Read(path);
            await WaitForAsync(() => bridge.DiagnosticExportStatus.SuccessfulWrites >= 2);
            var second = Read(path);
            Assert.Equal(reads, backend.AvailabilityReads);
            Assert.Equal(1, backend.InitializeCalls);
            Assert.Equal(PeripheralRgbOperationKind.Initialize, second.Snapshot.InFlight!.Kind);
            Assert.True(second.InFlightElapsedMilliseconds > first.InFlightElapsedMilliseconds);
            Assert.True(Stopwatch.GetElapsedTime(first.CapturedTimestamp, second.CapturedTimestamp) >= TimeSpan.FromSeconds(1));
            Assert.False(first.Snapshot.WorkerStopped);
            Assert.Equal(0, second.Snapshot.OperationCompletions);
            Assert.InRange(new FileInfo(path).Length, 1, PeripheralRgbDiagnosticExporter.MaximumBytes);
            Assert.Contains("unverified", second.Snapshot.Evidence);
        }
        finally
        {
            release.Set();
            bridge.Dispose();
            await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await bridge.DiagnosticWriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(bridge.DiagnosticSnapshot.WorkerStopped);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Blocked_color_call_keeps_exact_arguments_and_old_snapshots_immutable_until_completion()
    {
        using var client = new FakeClient();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bridge = new PeripheralRgbBridge(client, token => new FakeBackend(token)
        {
            SetColorAction = _ => { entered.TrySetResult(); release.Wait(token); return true; }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = bridge.DiagnosticSnapshot;
            var operation = pending.InFlight!;
            Assert.Equal(PeripheralRgbOperationKind.SetColor, operation.Kind);
            Assert.Equal(10, operation.ConfigurationRevision);
            Assert.Equal(new PeripheralRgbColor(10, 20, 30, 67), operation.LatestSample!.Color);
            Assert.Equal(operation.ConfigurationRevision, operation.LatestSample.ConfigurationRevision);
            Assert.Equal(1, pending.OperationCompletions); // Initialize only.
            Assert.Equal(0, pending.AcceptedColorRequests);
            Assert.True(operation.StartedTimestamp >= pending.BridgeStartedTimestamp);
            release.Set();
            await WaitForAsync(() => bridge.DiagnosticSnapshot.AcceptedColorRequests == 1);
            var finished = bridge.DiagnosticSnapshot;
            Assert.Null(finished.InFlight);
            Assert.True(finished.LastCompleted!.SdkAccepted);
            Assert.True(finished.LastCompleted.DurationMilliseconds >= 0);
            Assert.Equal(operation.LatestSample.Color, finished.LastAccepted!.Color);
            Assert.NotNull(pending.InFlight);
            Assert.Equal(0, pending.AcceptedColorRequests);
            Assert.True(finished.PublicationSequence > pending.PublicationSequence);
        }
        finally { release.Set(); bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Theory]
    [InlineData(PeripheralRgbOperationKind.Initialize)]
    [InlineData(PeripheralRgbOperationKind.SetColor)]
    [InlineData(PeripheralRgbOperationKind.Resend)]
    public async Task Backend_exception_finalizes_the_matching_span_and_retains_a_bounded_error(PeripheralRgbOperationKind kind)
    {
        using var client = new FakeClient();
        var failure = new InvalidOperationException(new string('e', 2048));
        using var bridge = new PeripheralRgbBridge(client, token => new FakeBackend(token)
        {
            InitializeAction = kind == PeripheralRgbOperationKind.Initialize ? () => throw failure : null,
            SetColorAction = kind == PeripheralRgbOperationKind.SetColor ? _ => throw failure : null,
            ResendAction = (_, _) => throw failure
        });
        try
        {
            if (kind == PeripheralRgbOperationKind.Resend)
            {
                await WaitForAsync(() => bridge.DiagnosticSnapshot.AcceptedColorRequests == 1);
                bridge.RequestResend();
            }
            await WaitForAsync(() => bridge.DiagnosticSnapshot.LoopFailures == 1);
            var snapshot = bridge.DiagnosticSnapshot;
            Assert.Null(snapshot.InFlight);
            Assert.Equal(kind, snapshot.LastCompleted!.Operation.Kind);
            Assert.False(snapshot.LastCompleted.Succeeded);
            Assert.Null(snapshot.LastCompleted.SdkAccepted); // An exception is not an explicit SDK refusal.
            Assert.InRange(snapshot.LastCompleted.Error!.Length, 1, 512);
            Assert.InRange(snapshot.LastLoopError!.Length, 1, 512);
            Assert.Equal(1, snapshot.OperationFailures);
            Assert.False(snapshot.WorkerStopped);
        }
        finally { bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Resend_preserves_initial_context_and_records_the_last_color_callback_across_revisions()
    {
        using var client = new FakeClient();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeBackend? backend = null;
        using var bridge = new PeripheralRgbBridge(client, token => backend = new(token));
        try
        {
            await WaitForAsync(() => bridge.DiagnosticSnapshot.AcceptedColorRequests == 1);
            backend!.ResendAction = (read, attempt) =>
            {
                attempt(1, 3);
                Assert.NotNull(read());
                var changed = client.Config.Clone();
                changed.Revision = 11; changed.Rgb.R = 90; changed.Rgb.G = 80; changed.Rgb.B = 70;
                client.Set(changed);
                attempt(2, 3);
                var color = read();
                entered.TrySetResult(); release.Wait();
                return color;
            };
            bridge.RequestResend();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = bridge.DiagnosticSnapshot;
            Assert.Equal(PeripheralRgbOperationKind.Resend, pending.InFlight!.Kind);
            Assert.Equal(10, pending.InFlight.ConfigurationRevision);
            Assert.Equal(11, pending.InFlight.LatestSample!.ConfigurationRevision);
            Assert.Equal(new PeripheralRgbColor(90, 80, 70, 67), pending.InFlight.LatestSample.Color);
            Assert.Equal(2, pending.InFlight.ResendAttempt);
            Assert.Equal(3, pending.InFlight.ResendAttemptLimit);
            release.Set();
            await WaitForAsync(() => bridge.DiagnosticSnapshot.AcceptedColorRequests == 2);
            var completed = bridge.DiagnosticSnapshot;
            Assert.Equal(PeripheralRgbOperationKind.Resend, completed.LastCompleted!.Operation.Kind);
            Assert.Equal(11, completed.LastAccepted!.ConfigurationRevision);
            Assert.Equal(completed.LastCompleted.ReturnedColor, completed.LastAccepted.Color);
            Assert.Equal(1, completed.ResendRequests);
            Assert.Equal(1, completed.InitializeRequests); // No second SDK session is added by diagnostics.
            Assert.Equal(1, backend.InitializeCalls);
        }
        finally { release.Set(); bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Faulty_status_observer_cannot_stop_other_observers_or_the_animated_worker()
    {
        using var client = new FakeClient();
        var config = client.Config.Clone();
        config.Rgb.Effect = RgbEffect.Palette; config.Rgb.EffectSpeed = 10;
        config.Rgb.Palette = ["#FF0000", "#00FF00"];
        client.Set(config);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var bridge = new PeripheralRgbBridge(client, token => new FakeBackend(token)
        {
            InitializeAction = () => { entered.TrySetResult(); release.Wait(token); }
        });
        int healthyNotifications = 0;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            bridge.StatusChanged += () => throw new InvalidOperationException("isolated observer failure");
            bridge.StatusChanged += () => Interlocked.Increment(ref healthyNotifications);
            release.Set();
            await WaitForAsync(() => bridge.DiagnosticSnapshot.DistinctAcceptedColors >= 2);
            var snapshot = bridge.DiagnosticSnapshot;
            Assert.True(snapshot.ObserverFailures >= 1);
            Assert.Contains("isolated observer", snapshot.LastObserverError!);
            Assert.True(Volatile.Read(ref healthyNotifications) >= 1);
            Assert.True(snapshot.AcceptedColorChanges >= 1);
            Assert.Equal(0, snapshot.OperationFailures);
            Assert.Equal(1, snapshot.InitializeRequests);
            Assert.False(snapshot.WorkerStopped);
            Assert.Empty(Directory.GetFiles(_directory)); // No exporter without opt-in, no fake logging.
        }
        finally { release.Set(); bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Sdk_refusal_is_recorded_without_counting_an_accepted_or_physically_verified_color()
    {
        using var client = new FakeClient();
        using var bridge = new PeripheralRgbBridge(client, token => new FakeBackend(token)
            { SetColorAction = _ => false, ReleaseOnRefusal = true });
        try
        {
            await WaitForAsync(() => bridge.DiagnosticSnapshot.HasIssue);
            var snapshot = bridge.DiagnosticSnapshot;
            Assert.False(snapshot.LastCompleted!.SdkAccepted);
            Assert.False(snapshot.BackendAvailable);
            Assert.Equal(1, snapshot.OperationFailures);
            Assert.Equal(0, snapshot.AcceptedColorRequests);
            Assert.Equal(0, snapshot.DistinctAcceptedColors);
            Assert.Null(snapshot.LastAccepted);
            Assert.False(snapshot.PhysicalOutputVerified);
            Assert.Contains("unverified", snapshot.Evidence);
        }
        finally { bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Disabling_sync_publishes_the_disconnected_backend_instead_of_its_previous_availability()
    {
        using var client = new FakeClient();
        FakeBackend? backend = null;
        using var bridge = new PeripheralRgbBridge(client, token => backend = new(token));
        try
        {
            await WaitForAsync(() => bridge.DiagnosticSnapshot.AcceptedColorRequests == 1);
            Assert.True(bridge.DiagnosticSnapshot.BackendAvailable);
            var changed = client.Config.Clone(); changed.Revision = 11; changed.Rgb.SyncPeripherals = false;
            client.Set(changed);
            await WaitForAsync(() => bridge.DiagnosticSnapshot is { SyncPeripherals: false, BackendAvailable: false });
            Assert.Equal(1, backend!.DisconnectCalls);
            Assert.Equal(1, bridge.DiagnosticSnapshot.SetColorRequests);
            Assert.Equal(11, bridge.DiagnosticSnapshot.ConfigurationRevision);
        }
        finally { bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Resend_aborted_by_sync_off_has_no_boolean_sdk_refusal_and_retains_the_last_callback_context()
    {
        using var client = new FakeClient();
        FakeBackend? backend = null;
        using var bridge = new PeripheralRgbBridge(client, token => backend = new(token));
        try
        {
            await WaitForAsync(() => bridge.DiagnosticSnapshot.AcceptedColorRequests == 1);
            backend!.ResendAction = (read, attempt) =>
            {
                attempt(1, 3);
                Assert.NotNull(read());
                var changed = client.Config.Clone(); changed.Revision = 11; changed.Rgb.SyncPeripherals = false;
                client.Set(changed);
                backend.Disconnect();
                return read();
            };
            bridge.RequestResend();
            await WaitForAsync(() => bridge.DiagnosticSnapshot.LastCompleted?.Operation.Kind == PeripheralRgbOperationKind.Resend);
            var snapshot = bridge.DiagnosticSnapshot;
            Assert.False(snapshot.LastCompleted!.Succeeded);
            Assert.Null(snapshot.LastCompleted.SdkAccepted);
            Assert.Null(snapshot.LastCompleted.ReturnedColor);
            Assert.False(snapshot.LastCompleted.Operation.LatestSample!.SyncPeripherals);
            Assert.Equal(11, snapshot.LastCompleted.Operation.LatestSample.ConfigurationRevision);
            Assert.False(snapshot.BackendAvailable);
            Assert.Equal(1, snapshot.AcceptedColorRequests);
            Assert.Equal(1, snapshot.SetColorRequests);
        }
        finally { bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Unexpected_availability_getter_failure_does_not_replace_the_original_backend_error()
    {
        using var client = new FakeClient();
        FakeBackend? backend = null;
        using var bridge = new PeripheralRgbBridge(client, token => backend = new(token)
        {
            SetColorAction = _ =>
            {
                backend!.AvailabilityFailure = new IOException("isolated availability failure");
                throw new InvalidOperationException("original backend failure");
            }
        });
        try
        {
            await WaitForAsync(() => bridge.DiagnosticSnapshot.LoopFailures == 1);
            var snapshot = bridge.DiagnosticSnapshot;
            Assert.Contains("original backend failure", snapshot.LastCompleted!.Error!);
            Assert.Contains("original backend failure", snapshot.LastLoopError!);
            Assert.Contains("isolated availability failure", snapshot.LastAvailabilityError!);
            Assert.Equal(1, snapshot.AvailabilityReadFailures);
            Assert.Null(snapshot.BackendAvailable);
            Assert.Null(snapshot.LastCompleted.SdkAccepted);
            Assert.Equal(1, snapshot.OperationFailures);
        }
        finally { bridge.Dispose(); await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public void Color_cardinality_is_bounded_and_reports_saturation_while_transitions_and_ranges_continue()
    {
        var recorder = new PeripheralRgbDiagnosticRecorder();
        var config = FakeClient.InitialConfig();
        for (int i = 0; i < 600; i++)
        {
            var color = ((byte)(i / 256), (byte)(i % 256), (byte)40, (byte)100);
            recorder.Calculated(config, i * 50, color);
            var operation = recorder.Begin(PeripheralRgbOperationKind.SetColor, config);
            recorder.Complete(operation, true, color);
        }
        var snapshot = recorder.Snapshot;
        Assert.Equal(512, snapshot.DistinctAcceptedColors);
        Assert.True(snapshot.DistinctAcceptedColorsSaturated);
        Assert.Equal(600, snapshot.AcceptedColorRequests);
        Assert.Equal(599, snapshot.AcceptedColorChanges);
        Assert.Equal(new PeripheralRgbColor(0, 0, 40, 100), snapshot.AcceptedChannelMin);
        Assert.Equal(new PeripheralRgbColor(2, 255, 40, 100), snapshot.AcceptedChannelMax);
        Assert.Equal(600, snapshot.OperationCompletions);
    }

    [Fact]
    public async Task Export_failures_are_bounded_and_reported_without_touching_an_existing_destination()
    {
        string parent = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(parent, "keep");
        using var exporter = new PeripheralRgbDiagnosticExporter(Path.Combine(parent, "current.json"),
            () => new(), TimeSpan.FromMilliseconds(10), maximumFailures: 2);
        await exporter.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exporter.Status.StoppedAfterFailures);
        Assert.True(exporter.Status.Stopped);
        Assert.Equal(2, exporter.Status.FailedWrites);
        Assert.Equal(0, exporter.Status.SuccessfulWrites);
        Assert.InRange(exporter.Status.LastError!.Length, 1, 512);
        Assert.Equal("keep", await File.ReadAllTextAsync(parent));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Repeated_opt_in_keeps_one_file_and_disposal_stops_the_independent_writer()
    {
        using var client = new FakeClient();
        using var bridge = new PeripheralRgbBridge(client, token => new FakeBackend(token));
        string path = Path.Combine(_directory, "current.json"), ignored = Path.Combine(_directory, "second.json");
        try
        {
            bridge.StartDiagnostics(path);
            bridge.StartDiagnostics(ignored);
            await WaitForAsync(() => bridge.DiagnosticExportStatus.SuccessfulWrites >= 1);
        }
        finally
        {
            bridge.Dispose();
            await bridge.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            await bridge.DiagnosticWriterCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(bridge.DiagnosticExportStatus.Stopped);
        Assert.False(File.Exists(ignored));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_directory)));
        Assert.Equal(1, Read(path).Schema);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_does_not_overwrite_or_delete_a_preexisting_unowned_temporary_file(bool failBeforeWrite)
    {
        string path = Path.Combine(_directory, "current.json"), unrelated = path + ".tmp";
        await File.WriteAllTextAsync(unrelated, "keep");
        using var exporter = new PeripheralRgbDiagnosticExporter(path,
            () => failBeforeWrite ? throw new InvalidOperationException("isolated snapshot read failure") : new PeripheralRgbDiagnosticSnapshot(),
            TimeSpan.FromMilliseconds(10), maximumFailures: 1);
        try
        {
            if (failBeforeWrite) await exporter.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            else await WaitForAsync(() => exporter.Status.SuccessfulWrites >= 1);
        }
        finally { exporter.Dispose(); await exporter.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        Assert.Equal(!failBeforeWrite, File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory, ".current.json.*.tmp"));
    }

    [Fact]
    public async Task Export_replaces_current_file_while_a_shared_delete_reader_remains_open()
    {
        string path = Path.Combine(_directory, "current.json");
        await File.WriteAllTextAsync(path, "old-content");
        using var heldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var exporter = new PeripheralRgbDiagnosticExporter(path, () => new PeripheralRgbDiagnosticSnapshot(), TimeSpan.FromMilliseconds(10));
        try
        {
            await WaitForAsync(() => exporter.Status.SuccessfulWrites >= 3);
            Assert.Equal(0, exporter.Status.FailedWrites);
            Assert.Equal(1, Read(path).Schema);
            using var oldContent = new StreamReader(heldReader, leaveOpen: true);
            Assert.Equal("old-content", await oldContent.ReadToEndAsync());
        }
        finally { exporter.Dispose(); await exporter.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Empty(Directory.GetFiles(_directory, ".current.json.*.tmp"));
    }

    [Fact]
    public async Task Export_retains_the_last_failure_detail_after_the_file_recovers()
    {
        string path = Path.Combine(_directory, "current.json");
        await File.WriteAllTextAsync(path, "old-content");
        using var heldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var exporter = new PeripheralRgbDiagnosticExporter(path, () => new PeripheralRgbDiagnosticSnapshot(), TimeSpan.FromMilliseconds(20));
        try
        {
            await WaitForAsync(() => exporter.Status.FailedWrites >= 1);
            var failed = exporter.Status;
            Assert.NotNull(failed.LastFailureUtc);
            Assert.NotNull(failed.LastFailure);
            Assert.NotNull(failed.LastFailureHResult);
            Assert.Equal("replace-current", failed.LastFailureStage);
            heldReader.Dispose();
            await WaitForAsync(() => exporter.Status.SuccessfulWrites >= 2);
            var recovered = exporter.Status;
            Assert.Equal(0, recovered.ConsecutiveFailures);
            Assert.Null(recovered.LastError);
            Assert.True(recovered.LastFailureUtc >= failed.LastFailureUtc);
            Assert.True(recovered.FailedWrites >= failed.FailedWrites);
            await WaitForAsync(() => exporter.Status.SuccessfulWrites >= recovered.SuccessfulWrites + 2);
            Assert.Equal(recovered.LastFailureUtc, exporter.Status.LastFailureUtc);
            Assert.Equal(recovered.LastFailure, exporter.Status.LastFailure);
            var exported = Read(path).ExporterStatusAtCapture;
            Assert.Equal(recovered.LastFailure, exported.LastFailure);
            Assert.Equal(recovered.LastFailureHResult, exported.LastFailureHResult);
        }
        finally { exporter.Dispose(); await exporter.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Empty(Directory.GetFiles(_directory, ".current.json.*.tmp"));
    }

    private static PeripheralRgbDiagnosticExport Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<PeripheralRgbDiagnosticExport>(stream, Options)!;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        long started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(5)) throw new TimeoutException("Isolated peripheral diagnostic did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private sealed class FakeBackend(CancellationToken token) : IPeripheralRgbBackend
    {
        private int _availabilityReads, _initializeCalls, _disconnectCalls;
        private bool _available;
        public int AvailabilityReads => Volatile.Read(ref _availabilityReads);
        public int InitializeCalls => Volatile.Read(ref _initializeCalls);
        public int DisconnectCalls => Volatile.Read(ref _disconnectCalls);
        public Exception? AvailabilityFailure { get; set; }
        public bool IsAvailable
        {
            get
            {
                Interlocked.Increment(ref _availabilityReads);
                if (AvailabilityFailure is { } error) throw error;
                return _available;
            }
        }
        public EngineText? DiagnosticText => null;
        public string? Diagnostic => null;
        public Action? InitializeAction { get; init; }
        public Func<PeripheralRgbColor, bool>? SetColorAction { get; init; }
        public bool ReleaseOnRefusal { get; init; }
        public Func<Func<(byte R, byte G, byte B, byte Brightness)?>, Action<int, int>, (byte R, byte G, byte B, byte Brightness)?>? ResendAction { get; set; }
        public void Initialize() { Interlocked.Increment(ref _initializeCalls); InitializeAction?.Invoke(); token.ThrowIfCancellationRequested(); _available = true; }
        public bool SetColor(byte r, byte g, byte b, byte brightness)
        {
            bool accepted = SetColorAction?.Invoke(new(r, g, b, brightness)) ?? true;
            if (!accepted && ReleaseOnRefusal) _available = false;
            return accepted;
        }
        public (byte R, byte G, byte B, byte Brightness)? Resend(Func<(byte R, byte G, byte B, byte Brightness)?> readColor, Action<int, int> onAttempt) =>
            ResendAction is { } action ? action(readColor, onAttempt) : readColor();
        public void Disconnect() { Interlocked.Increment(ref _disconnectCalls); _available = false; }
        public void Dispose() => _available = false;
    }

    private sealed class FakeClient : IEngineClient
    {
        private ControlConfig _config = InitialConfig();
        internal static ControlConfig InitialConfig() => new() { Revision = 10,
            Rgb = new() { R = 10, G = 20, B = 30, Brightness = 67, LedGamma = 1, Effect = RgbEffect.Static, SyncPeripherals = true } };
        public bool IsRemote => true;
        public bool IsConnected => true;
        public bool IsSimulated => true;
        public string ModeDescription => "isolated fake";
        public ControlConfig Config => Volatile.Read(ref _config);
        public EngineSnapshot? Snapshot => null;
#pragma warning disable CS0067
        public event Action<EngineSnapshot>? SnapshotUpdated;
        public event Action<ControlConfig>? ConfigChanged;
        public event Action<ControlConfig>? AnyConfigChanged;
        public event Action<EngineAlert>? AlertRaised;
        public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
        public void Set(ControlConfig config) { Volatile.Write(ref _config, config); AnyConfigChanged?.Invoke(config); }
        public void Start() { }
        public void UpdateConfig(Action<ControlConfig> mutate) { var next = Config.Clone(); mutate(next); Set(next); }
        public void ReplaceConfig(ControlConfig config) => Set(config);
        public void RequestLcdFrame() { }
        public void NotifyResume() { }
        public void RefreshRgb() { }
        public void RescanDevices() { }
        public void SetCurveTest(float? temperature) { }
        public void StartCalibration() { }
        public void CancelCalibration() { }
        public void SetSensorDemand(SensorDemand demand) { }
        public Task ShutdownAsync(bool restoreHardwareMode = true) => Task.CompletedTask;
        public void Dispose() { }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
