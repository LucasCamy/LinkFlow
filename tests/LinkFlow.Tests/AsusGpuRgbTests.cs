using System.Diagnostics;
using System.Text;

namespace LinkFlow.Tests;

public class AsusGpuRgbTests
{
    [Theory]
    [InlineData(-6)]
    [InlineData(-8)]
    [InlineData(-10)]
    public void Native_access_failure_invalidates_control_without_claiming_adapter_removal(int status)
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(100, 50, 25, 80));
        var oldHandle = backend.LastTransferHandle;
        var before = gpu.ControlStatus;

        backend.FailNext(0x8020, 0x00, status);
        Assert.False(gpu.SetColor(200, 20, 10, 100));
        var failed = gpu.ControlStatus;
        Assert.False(failed.ControlAvailable);
        Assert.True(failed.InitializationRetryNeeded);
        Assert.False(failed.LastOutputAccepted);
        Assert.Equal(RgbAdapterObservation.Present, failed.AdapterObservation);
        Assert.Equal(before.LastAdapterSeenUtc, failed.LastAdapterSeenUtc);
        Assert.Equal(1, failed.TotalNativeFailures);
        Assert.Equal(1, failed.TotalOutputFailures);
        Assert.Equal(1, failed.ConsecutiveOutputFailures);
        Assert.Equal(status, failed.LastNativeFailure!.Status);
        Assert.Equal(RgbNativeOperation.Write, failed.LastNativeFailure.Operation);
        Assert.Equal((ushort)0x8020, failed.LastNativeFailure.Register);
        Assert.Equal((byte)0x00, failed.LastNativeFailure.Command);
        Assert.True(failed.LastNativeFailure.Sequence > before.LastOutputAcceptedNativeSequence);
        Assert.Contains("NVAPI " + status, failed.Diagnostic);

        gpu.Initialize();
        var probed = gpu.ControlStatus;
        Assert.True(probed.ControlAvailable);
        Assert.Null(probed.LastOutputAccepted); // A successful probe is not an accepted color.
        Assert.Equal(failed.LastNativeFailure, probed.LastNativeFailure);
        Assert.Equal(0, probed.LastNativeOperation!.Status);
        Assert.True(gpu.SetColor(200, 20, 10, 100));
        Assert.NotEqual(oldHandle, backend.LastTransferHandle);
        Assert.True(gpu.ControlStatus.LastOutputAccepted);
        Assert.True(gpu.ControlStatus.LastOutputAcceptedNativeSequence > failed.LastNativeFailure.Sequence);
        Assert.Equal(0, gpu.ControlStatus.ConsecutiveOutputFailures);
        Assert.Equal(1, gpu.ControlStatus.TotalOutputFailures);
        Assert.Equal(1, backend.NativeInitializationCalls); // Reference remains balanced across re-probes.
    }

    [Fact]
    public void Enumeration_failure_keeps_the_last_seen_adapter_and_exact_sdk_error_separate()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        var seen = gpu.ControlStatus.LastAdapterSeenUtc;
        int transfers = backend.TransferCalls;
        backend.EnumerationStatus = -6;
        gpu.Initialize();
        var status = gpu.ControlStatus;
        Assert.Equal(RgbAdapterObservation.EnumerationUnavailable, status.AdapterObservation);
        Assert.Equal(seen, status.LastAdapterSeenUtc);
        Assert.False(status.ControlAvailable);
        Assert.True(status.InitializationRetryNeeded);
        Assert.Equal(RgbNativeOperation.EnumerateAdapters, status.LastNativeFailure!.Operation);
        Assert.Equal(-6, status.LastNativeFailure.Status);
        Assert.Null(status.LastNativeFailure.Register);
        Assert.Null(status.LastNativeFailure.Command);
        Assert.Equal(transfers, backend.TransferCalls);
    }

    [Fact]
    public void Probe_success_followed_by_output_failure_does_not_confirm_the_color()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.IsAvailable);
        Assert.Null(gpu.ControlStatus.LastOutputAccepted);
        backend.FailNext(0x8020, 0x00, -6);
        Assert.False(gpu.SetColor(255, 0, 0, 100));
        Assert.False(gpu.ControlStatus.LastOutputAccepted);
        Assert.Null(gpu.ControlStatus.LastOutputAcceptedUtc);
        Assert.NotNull(gpu.ControlStatus.LastOutputAttemptUtc);
    }

    [Fact]
    public void Unknown_firmware_is_not_written_and_does_not_enter_an_automatic_retry_storm()
    {
        var backend = new FakeAsusGpuRgbBackend("XUMA0-E6K5-1110");
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        int transfers = backend.TransferCalls;
        Assert.Equal(RgbAdapterObservation.Present, gpu.ControlStatus.AdapterObservation);
        Assert.False(gpu.IsAvailable);
        Assert.False(gpu.NeedsInitializationRetry);
        Assert.False(gpu.SetColor(255, 0, 0, 100));
        Assert.Equal(transfers, backend.TransferCalls);
        Assert.Equal(0, backend.OutputWriteCalls);
        Assert.Equal(0, gpu.ControlStatus.TotalNativeFailures);
        Assert.Equal(EngineTexts.GpuFirmwareUnknown, gpu.DiagnosticText!.Code);
    }

    [Fact]
    public void Generic_transport_rejection_keeps_control_detected_but_output_unconfirmed()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(10, 20, 30, 100));
        backend.FailNext(0x8020, 0x00, -1);
        Assert.False(gpu.SetColor(40, 50, 60, 100));
        Assert.True(gpu.IsAvailable);
        Assert.False(gpu.NeedsInitializationRetry);
        Assert.False(gpu.ControlStatus.LastOutputAccepted);
        var failed = gpu.ControlStatus.LastNativeFailure;
        Assert.True(gpu.SetColor(40, 50, 60, 100));
        Assert.Equal(failed, gpu.ControlStatus.LastNativeFailure);
        Assert.True(gpu.ControlStatus.LastOutputAccepted);
    }

    [Fact]
    public void Successful_sdk_calls_with_mismatched_registers_reject_output_without_inventing_a_native_error()
    {
        var backend = new FakeAsusGpuRgbBackend { MismatchColorReadback = true };
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.False(gpu.SetColor(20, 40, 60, 100));
        Assert.True(gpu.IsAvailable);
        Assert.False(gpu.ControlStatus.LastOutputAccepted);
        Assert.Null(gpu.ControlStatus.LastNativeFailure);
        Assert.Equal(0, gpu.ControlStatus.TotalNativeFailures);
        Assert.Equal(0, gpu.ControlStatus.LastNativeOperation!.Status);
        Assert.Equal(EngineTexts.GpuColorMismatch, gpu.DiagnosticText!.Code);
    }

    [Fact]
    public async Task Reading_status_during_reprobe_is_nonblocking_and_never_publishes_the_private_reset()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(10, 20, 30, 100));
        backend.BlockEnumeration = true;
        var init = Task.Run(gpu.Initialize);
        try
        {
            Assert.True(backend.EnumerationEntered.Wait(TimeSpan.FromSeconds(3)));
            var reading = Task.Run(() =>
            {
                var status = gpu.ControlStatus;
                for (int i = 0; i < 10_000; i++)
                {
                    Assert.Same(status, gpu.ControlStatus);
                    Assert.True(gpu.IsAvailable);
                    Assert.Equal("AUMA0-E6K5-1110", gpu.Name);
                    Assert.Null(gpu.Diagnostic);
                }
                return status;
            });
            var observed = await reading.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(observed.Initializing);
            Assert.True(observed.ControlAvailable);
            Assert.True(observed.LastOutputAccepted);
            Assert.False(init.IsCompleted);
        }
        finally
        {
            backend.EnumerationContinue.Set();
            await init.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.False(gpu.ControlStatus.Initializing);
        Assert.True(gpu.IsAvailable);
        Assert.Null(gpu.ControlStatus.LastOutputAccepted);
    }

    [Fact]
    public async Task Initial_detection_does_not_fabricate_availability_while_native_probe_is_pending()
    {
        var backend = new FakeAsusGpuRgbBackend { BlockEnumeration = true };
        using var gpu = new AsusGpuRgb(backend);
        var init = Task.Run(gpu.Initialize);
        try
        {
            Assert.True(backend.EnumerationEntered.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(gpu.ControlStatus.Initializing);
            Assert.False(gpu.IsAvailable);
            Assert.Null(gpu.ControlStatus.LastOutputAccepted);
        }
        finally
        {
            backend.EnumerationContinue.Set();
            await init.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.True(gpu.IsAvailable);
    }

    [Fact]
    public void Status_getters_and_mutating_a_diagnostic_copy_have_no_sdk_side_effects()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        backend.FailNext(0x8020, 0x00, -6);
        Assert.False(gpu.SetColor(10, 20, 30, 100));
        var cached = gpu.ControlStatus;
        int calls = backend.TransferCalls, enums = backend.EnumerationCalls;
        var diagnostic = cached.DiagnosticText!;
        diagnostic.Code = "changed";
        diagnostic.Args.Clear();
        for (int i = 0; i < 1000; i++)
        {
            Assert.Same(cached, gpu.ControlStatus);
            Assert.Contains("NVAPI -6", gpu.Diagnostic);
            Assert.False(gpu.IsAvailable);
            Assert.True(gpu.NeedsInitializationRetry);
        }
        Assert.Equal(calls, backend.TransferCalls);
        Assert.Equal(enums, backend.EnumerationCalls);
    }

    [Fact]
    public void Wire_roundtrip_preserves_sdk_failure_identity_and_accepted_output_without_physical_claims()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(100, 50, 25, 80));
        Assert.Equal(new byte[] { 80, 20, 40 }, backend.ColorAt(0x8100)); // Existing R/B/G layout and brightness.
        backend.FailNext(0x8020, 0x00, -6);
        Assert.False(gpu.SetColor(255, 0, 0, 100));
        var copy = Json.Clone(gpu.ControlStatus);
        Assert.Equal(gpu.ControlStatus.LastNativeFailure, copy.LastNativeFailure);
        Assert.Equal(gpu.ControlStatus.LastOutputAcceptedUtc, copy.LastOutputAcceptedUtc);
        Assert.False(copy.LastOutputAccepted);
        Assert.Contains("NVAPI -6", copy.Diagnostic);
        Assert.Equal(1, copy.TotalNativeFailures);
    }
}

internal sealed class FakeAsusGpuRgbBackend : IAsusGpuRgbBackend
{
    private readonly Dictionary<ushort, byte> _registers = new();
    private readonly Queue<(ushort Register, byte Command, int Status)> _faults = new();
    private readonly object _gate = new();
    private ushort _selected;
    private bool _initialized;
    private int _transfers, _enumerations, _nativeInitializations, _outputWrites;
    private int _persistentColorStatus;
    private readonly List<long> _enumeratedAt = [];

    internal FakeAsusGpuRgbBackend(string firmware = "AUMA0-E6K5-1110")
    {
        for (int i = 0; i < 15; i++) _registers[(ushort)(0x1000 + i)] = Encoding.ASCII.GetBytes(firmware)[i];
        _registers[0x1C03] = 4;
        _registers[0x8020] = 0;
        _registers[0x8021] = 1;
    }

    internal int EnumerationStatus { get; set; }
    internal bool BlockEnumeration { get; set; }
    internal bool MismatchColorReadback { get; set; }
    internal ManualResetEventSlim EnumerationEntered { get; } = new(false);
    internal ManualResetEventSlim EnumerationContinue { get; } = new(false);
    internal int TransferCalls => Volatile.Read(ref _transfers);
    internal int EnumerationCalls => Volatile.Read(ref _enumerations);
    internal int NativeInitializationCalls => Volatile.Read(ref _nativeInitializations);
    internal int OutputWriteCalls => Volatile.Read(ref _outputWrites);
    internal int PersistentColorStatus { get => Volatile.Read(ref _persistentColorStatus); set => Volatile.Write(ref _persistentColorStatus, value); }
    internal long[] EnumerationTimestamps { get { lock (_gate) return _enumeratedAt.ToArray(); } }
    internal IntPtr LastTransferHandle { get; private set; }

    internal void FailNext(ushort register, byte command, int status)
    { lock (_gate) _faults.Enqueue((register, command, status)); }

    internal byte[] ColorAt(ushort register) => Enumerable.Range(0, 3).Select(i => _registers[(ushort)(register + i)]).ToArray();

    public int EnsureInitialized(out bool nativeInitializationCalled)
    {
        nativeInitializationCalled = !_initialized;
        if (!_initialized) { _initialized = true; Interlocked.Increment(ref _nativeInitializations); }
        return 0;
    }

    public int EnumeratePhysicalGpus(IntPtr[] handles, out int count)
    {
        int generation = Interlocked.Increment(ref _enumerations);
        lock (_gate) _enumeratedAt.Add(Stopwatch.GetTimestamp());
        if (BlockEnumeration)
        {
            EnumerationEntered.Set();
            if (!EnumerationContinue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Isolated fake enumeration gate timed out.");
        }
        handles[0] = new IntPtr(generation);
        count = EnumerationStatus == 0 ? 1 : 0;
        return EnumerationStatus;
    }

    public int GetPciIdentifiers(IntPtr gpu, out uint device, out uint subsystem)
    { device = 0x270410DE; subsystem = 0x88981043; return 0; }

    public int Transfer(IntPtr gpu, bool read, byte command, byte[] data, int count)
    {
        Interlocked.Increment(ref _transfers);
        LastTransferHandle = gpu;
        ushort register = !read && command == 0x00 ? (ushort)((data[0] << 8) | data[1]) : _selected;
        if (register == 0x8020 && command == 0x00 && PersistentColorStatus is var persistent && persistent != 0)
            return persistent;
        lock (_gate)
        {
            if (_faults.TryPeek(out var failure) && failure.Register == register && failure.Command == command)
            { _faults.Dequeue(); return failure.Status; }
        }
        if (read && command is >= 0xA0 and <= 0xAF) data[0] = (byte)(command - 0xA0);
        else if (read && command == 0x81)
        {
            data[0] = _registers.GetValueOrDefault(_selected);
            if (MismatchColorReadback && _selected >= 0x8100) data[0] ^= 1;
        }
        else if (!read && command == 0x00) _selected = register;
        else if (!read && command == 0x01)
        { _registers[_selected] = data[0]; if (_selected >= 0x8100) Interlocked.Increment(ref _outputWrites); }
        else if (!read && command == 0x03)
        {
            for (int i = 0; i < data[0]; i++) _registers[(ushort)(_selected + i)] = data[i + 1];
            Interlocked.Increment(ref _outputWrites);
        }
        else throw new InvalidOperationException("Unexpected isolated Aura protocol call.");
        return 0;
    }

    public void Dispose() { EnumerationEntered.Dispose(); EnumerationContinue.Dispose(); }
}
