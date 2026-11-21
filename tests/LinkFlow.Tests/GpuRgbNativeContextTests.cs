using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace LinkFlow.Tests;

public sealed class GpuRgbNativeContextTests
{
    [Fact]
    public void Transfer_context_measures_the_backend_wrapper_and_status_getters_remain_cached()
    {
        var backend = new ContextBackend { Version = "32.0.15.8108" };
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(10, 20, 30, 100));
        var status = gpu.ControlStatus;
        var operation = status.LastNativeOperation!;
        Assert.Equal(Environment.ProcessId, operation.ProcessId);
        Assert.Equal(Environment.CurrentManagedThreadId, operation.ThreadId);
        Assert.Equal(unchecked((ulong)backend.Inner.LastTransferHandle.ToInt64()).ToString("X16", CultureInfo.InvariantCulture), operation.GpuHandle);
        Assert.Equal("32.0.15.8108", operation.LibraryVersion);
        Assert.True(operation.TransferDurationMicroseconds is { } duration && double.IsFinite(duration) &&
            duration >= backend.LastMeasuredMicroseconds && duration >= 0);
        int transfers = backend.Inner.TransferCalls, metadataReads = backend.MetadataReads;
        for (int i = 0; i < 100; i++) Assert.Same(status, gpu.ControlStatus);
        Assert.Equal(transfers, backend.Inner.TransferCalls);
        Assert.Equal(metadataReads, backend.MetadataReads);
    }

    [Fact]
    public void Failure_context_survives_reprobe_success_and_wire_roundtrip()
    {
        var backend = new ContextBackend { Version = "32.0.15.8108" };
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        backend.Inner.FailNext(0x8020, 0x00, -6);
        Assert.False(gpu.SetColor(10, 20, 30, 100));
        var fault = gpu.ControlStatus.LastNativeFailure!;
        Assert.Equal(-6, fault.Status);
        Assert.NotNull(fault.GpuHandle);
        Assert.NotNull(fault.TransferDurationMicroseconds);
        gpu.Initialize();
        Assert.True(gpu.SetColor(10, 20, 30, 100));
        Assert.Equal(fault, gpu.ControlStatus.LastNativeFailure);
        Assert.NotEqual(fault.GpuHandle, gpu.ControlStatus.LastNativeOperation!.GpuHandle);
        var wire = Json.Clone(gpu.ControlStatus);
        Assert.Equal(fault, wire.LastNativeFailure);
        Assert.Equal(gpu.ControlStatus.LastNativeOperation, wire.LastNativeOperation);
    }

    [Fact]
    public void Retained_native_failure_keeps_the_original_worker_context_after_replacement()
    {
        using var first = new ContextSession(31001, "first-library");
        using var second = new ContextSession(31002, "second-library");
        var sessions = new Queue<IGpuRgbWorkerSession>([first, second]);
        using var device = new IsolatedGpuRgbDevice(() => sessions.Dequeue());
        device.Initialize();
        Assert.True(device.SetColor(10, 20, 30, 100));
        first.Backend.Inner.FailNext(0x8020, 0x00, -6);
        Assert.False(device.SetColor(40, 50, 60, 100));
        var fault = device.ControlStatus.LastNativeFailure!;
        Assert.Equal(31001, fault.ProcessId);
        device.Initialize();
        Assert.True(device.SetColor(40, 50, 60, 100));
        Assert.True(first.HasExited);
        Assert.Equal(2, device.ControlStatus.WorkerGeneration);
        Assert.Equal(fault, device.ControlStatus.LastNativeFailure);
        Assert.Equal("first-library", device.ControlStatus.LastNativeFailure!.LibraryVersion);
        Assert.Equal(31002, device.ControlStatus.LastNativeOperation!.ProcessId);
        Assert.Equal("second-library", device.ControlStatus.LastNativeOperation.LibraryVersion);
        Assert.True(device.ControlStatus.LastNativeOperation.Sequence > fault.Sequence);
        Assert.Equal(fault, Json.Clone(device.ControlStatus).LastNativeFailure);
    }

    [Fact]
    public void Optional_metadata_failure_neither_masks_acceptance_nor_relabels_a_native_rejection()
    {
        var backend = new ContextBackend { ThrowMetadata = true };
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(1, 2, 3, 100));
        Assert.Null(gpu.ControlStatus.LastNativeOperation!.LibraryVersion);
        Assert.Equal(0, gpu.ControlStatus.TotalNativeFailures);
        backend.Inner.FailNext(0x8020, 0x00, -6);
        Assert.False(gpu.SetColor(4, 5, 6, 100));
        Assert.Equal(-6, gpu.ControlStatus.LastNativeFailure!.Status);
        Assert.Null(gpu.ControlStatus.LastNativeFailure.LibraryVersion);
        Assert.Equal(1, gpu.ControlStatus.TotalNativeFailures);
        Assert.Equal(1, gpu.ControlStatus.TotalOutputFailures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\nversion")]
    public void Unknown_or_control_character_metadata_is_omitted_without_changing_output(string? version)
    {
        var backend = new ContextBackend { Version = version };
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(10, 20, 30, 100));
        Assert.Null(gpu.ControlStatus.LastNativeOperation!.LibraryVersion);
        Assert.Equal(0, gpu.ControlStatus.TotalNativeFailures);
    }

    [Fact]
    public void Oversized_optional_version_is_bounded_and_legacy_context_is_still_readable()
    {
        var backend = new ContextBackend { Version = new string('v', 200) };
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(10, 20, 30, 100));
        Assert.Equal(128, gpu.ControlStatus.LastNativeOperation!.LibraryVersion!.Length);
        var legacy = JsonSerializer.Deserialize<RgbNativeOperationResult>("{\"sequence\":1,\"status\":-6}", Json.Compact)!;
        Assert.Equal(-6, legacy.Status);
        Assert.Null(legacy.ProcessId);
        Assert.Null(legacy.ThreadId);
        Assert.Null(legacy.GpuHandle);
        Assert.Null(legacy.TransferDurationMicroseconds);
        Assert.Null(legacy.LibraryVersion);
    }

    [Fact]
    public void Default_backend_metadata_has_no_version_requirement_or_sdk_read()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(1, 2, 3, 100));
        Assert.Null(gpu.ControlStatus.LastNativeOperation!.LibraryVersion);
        Assert.Equal(1, backend.NativeInitializationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Journal_keeps_fault_context_invariant_and_bounded_even_with_all_worker_metadata(bool fullWorkerMetadata)
    {
        var start = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        await using var history = new OperationalHistory(new() { DirectoryPath = null, TimeProvider = new ContextClock(start) });
        var fault = new RgbNativeOperationResult
        {
            Sequence = 2, AtUtc = start, Operation = RgbNativeOperation.Write, Status = -6,
            Register = 0x8020, Command = 0, ProcessId = 31001, ThreadId = 7,
            GpuHandle = "0000000000000001", TransferDurationMicroseconds = 2.75, LibraryVersion = "first-library"
        };
        void Record(int second, RgbControlStatus status) => history.RecordSnapshot(new()
        { Timestamp = start.AddSeconds(second).UtcDateTime, GpuRgbControl = status });
        var ready = new RgbControlStatus { ControlAvailable = true, LastOutputAccepted = true };
        if (fullWorkerMetadata) ready = ready with { WorkerGeneration = 3, WorkerProcessId = 31001, WorkerRestartCount = 2 };
        Record(0, ready);
        var failed = ready with { ControlAvailable = false, LastOutputAccepted = false, LastNativeFailure = fault };
        if (fullWorkerMetadata) failed = failed with
        {
            Source = "NVAPI/AuraI2CEx", ControllerName = "AUMA0-E6K5-1110", AdapterObservation = RgbAdapterObservation.Present,
            InitializationRetryNeeded = true, TotalNativeFailures = 7, TotalOutputFailures = 6,
            ConsecutiveOutputFailures = 3, InitializationAttempts = 3, WorkerTransportFailures = 2,
            LastWorkerRestartReason = "earlier-output-rejected", LastWorkerFailure = "Earlier worker response deadline exceeded",
            LastWorkerFailureUtc = start, LastOutputAttemptUtc = start.AddSeconds(1), LastOutputAcceptedUtc = start,
            LastOutputAcceptedNativeSequence = 1,
            DiagnosticText = EngineText.Of(EngineTexts.GpuAuraFailed, "8020", "00", "-6")
        };
        Record(1, failed);
        Record(2, failed with { LastNativeFailure = fault with { Sequence = 3, ThreadId = 8,
            GpuHandle = "0000000000000002", TransferDurationMicroseconds = 9.5, LibraryVersion = "second-library" } });
        var events = history.QueryEvents(new() { FromUtc = start.AddSeconds(-1), ToUtc = start.AddSeconds(5), DeviceId = "rgb.gpu" });
        var entry = Assert.Single(events, e => e.Kind == "rgb.transport.failed");
        Assert.Equal("31001", entry.Data["nativeProcessId"]);
        Assert.Equal("7", entry.Data["nativeThreadId"]);
        Assert.Equal("managed", entry.Data["nativeThreadIdKind"]);
        Assert.Equal("0000000000000001", entry.Data["nativeGpuHandle"]);
        Assert.Equal("2.75", entry.Data["nativeTransferDurationMicroseconds"]);
        Assert.Equal("backend-call-including-wrapper", entry.Data["nativeTransferTimingScope"]);
        Assert.Equal("first-library", entry.Data["nativeLibraryVersion"]);
        Assert.Equal("Write", entry.Data["nativeOperation"]);
        Assert.Equal("-6", entry.Data["nativeStatus"]);
        Assert.Equal("2", entry.Data["nativeFailureSequence"]);
        Assert.Equal("0x8020", entry.Data["nativeRegister"]);
        Assert.Equal("0x00", entry.Data["nativeCommand"]);
        Assert.Equal(start.ToString("O", CultureInfo.InvariantCulture), entry.Data["nativeFailureUtc"]);
        Assert.Equal("unverified", entry.Data["physicalOutput"]);
        Assert.Equal(24, entry.Data.Count);
        Assert.True(int.Parse(entry.Data["omittedDataFields"], CultureInfo.InvariantCulture) > 0);
        if (fullWorkerMetadata)
        {
            Assert.Equal("3", entry.Data["workerGeneration"]);
            Assert.Equal("31001", entry.Data["workerProcessId"]);
            Assert.Equal("7", entry.Data["totalNativeFailures"]);
            Assert.Equal("6", entry.Data["totalOutputFailures"]);
            var worker = Assert.Single(events, e => e.Kind == "rgb.worker.transport.failed");
            Assert.Equal("Earlier worker response deadline exceeded", worker.Data["workerLastFailure"]);
            Assert.Equal("2", worker.Data["workerTransportFailures"]);
            Assert.InRange(worker.Data.Count, 1, 24);
        }
    }

    private sealed class ContextClock(DateTimeOffset start) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => start.AddSeconds(10);
    }

    private sealed class ContextBackend : IAsusGpuRgbBackend
    {
        internal FakeAsusGpuRgbBackend Inner { get; } = new();
        internal string? Version { get; init; }
        internal bool ThrowMetadata { get; init; }
        internal int MetadataReads { get; private set; }
        internal double LastMeasuredMicroseconds { get; private set; }
        public string? LibraryVersion
        {
            get
            {
                MetadataReads++;
                if (ThrowMetadata) throw new InvalidOperationException("Optional fake metadata unavailable.");
                return Version;
            }
        }
        public int EnsureInitialized(out bool called) => Inner.EnsureInitialized(out called);
        public int EnumeratePhysicalGpus(IntPtr[] handles, out int count) => Inner.EnumeratePhysicalGpus(handles, out count);
        public int GetPciIdentifiers(IntPtr gpu, out uint device, out uint subsystem) => Inner.GetPciIdentifiers(gpu, out device, out subsystem);
        public int Transfer(IntPtr gpu, bool read, byte command, byte[] data, int count)
        {
            long started = Stopwatch.GetTimestamp();
            try { Thread.SpinWait(1000); return Inner.Transfer(gpu, read, command, data, count); }
            finally { LastMeasuredMicroseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds; }
        }
        public void Dispose() => Inner.Dispose();
    }

    private sealed class ContextSession(int processId, string version) : IGpuRgbWorkerSession
    {
        internal ContextBackend Backend { get; } = new() { Version = version };
        private AsusGpuRgb? _gpu;
        public int ProcessId => processId;
        public bool HasExited { get; private set; }
        public GpuRgbWorkerReply Exchange(GpuRgbWorkerRequest request, TimeSpan timeout)
        {
            _gpu ??= new AsusGpuRgb(Backend);
            bool accepted;
            if (request.Operation == GpuRgbWorkerProtocol.Initialize) { _gpu.Initialize(); accepted = _gpu.IsAvailable; }
            else accepted = _gpu.SetColor(request.R, request.G, request.B, request.Brightness);
            var raw = _gpu.ControlStatus;
            return new()
            {
                Generation = request.Generation, RequestId = request.RequestId, Accepted = accepted,
                Status = raw with
                {
                    LastNativeOperation = raw.LastNativeOperation is { } operation ? operation with { ProcessId = processId } : null,
                    LastNativeFailure = raw.LastNativeFailure is { } failure ? failure with { ProcessId = processId } : null
                }
            };
        }
        public void Dispose()
        {
            if (HasExited) return;
            HasExited = true;
            if (_gpu == null) Backend.Dispose(); else _gpu.Dispose();
        }
    }
}
