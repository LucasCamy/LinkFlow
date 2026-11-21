using System.Collections.Concurrent;

using System.IO;

namespace LinkFlow.Tests;

public class IsolatedGpuRgbTests
{
    [Fact]
    public void Poisoned_context_is_replaced_without_erasing_native_failure_evidence()
    {
        var first = new TestSession(1001);
        var second = new TestSession(1002);
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => ++created == 1 ? first : AssertReplacement(first, second));
        rgb.Initialize();
        Assert.True(rgb.SetColor(10, 20, 30, 100));
        var accepted = rgb.ControlStatus;

        first.Backend.PersistentColorStatus = -6;
        Assert.False(rgb.SetColor(40, 50, 60, 100));
        var failed = rgb.ControlStatus;
        Assert.False(failed.ControlAvailable);
        Assert.Equal(-6, failed.LastNativeFailure!.Status);
        Assert.Equal(EngineTexts.GpuAuraFailed, failed.DiagnosticText!.Code);
        Assert.Equal(0, first.DisposeCalls); // A frame never quietly starts another native controller.

        // This is the original failure: a re-probe in the same context succeeds but cannot cure its next write.
        Assert.True(first.Reprobe());
        Assert.False(first.DirectColor());
        Assert.Equal(1, first.Backend.NativeInitializationCalls);

        rgb.Initialize();
        var probed = rgb.ControlStatus;
        Assert.True(probed.ControlAvailable);
        Assert.Null(probed.LastOutputAccepted);
        Assert.Equal(failed.LastNativeFailure, probed.LastNativeFailure);
        Assert.Equal(accepted.LastOutputAcceptedUtc, probed.LastOutputAcceptedUtc);
        Assert.Equal(accepted.LastOutputAcceptedNativeSequence, probed.LastOutputAcceptedNativeSequence);
        Assert.Equal(2, probed.InitializationAttempts);
        Assert.Equal(2, probed.WorkerGeneration);
        Assert.Equal(1, probed.WorkerRestartCount);
        Assert.Equal(1002, probed.WorkerProcessId);
        Assert.Equal("output-rejected", probed.LastWorkerRestartReason);
        Assert.True(probed.LastNativeOperation!.Sequence > failed.LastNativeOperation!.Sequence);
        Assert.Equal(failed.TotalNativeFailures, probed.TotalNativeFailures); // Unobserved old-child calls are not invented.
        Assert.Equal(failed.TotalOutputFailures, probed.TotalOutputFailures);
        Assert.True(rgb.SetColor(40, 50, 60, 100));
        Assert.Equal(failed.LastNativeFailure, rgb.ControlStatus.LastNativeFailure);
        Assert.True(rgb.ControlStatus.LastOutputAcceptedNativeSequence > failed.LastNativeFailure.Sequence);
        Assert.Equal(0, rgb.ControlStatus.ConsecutiveOutputFailures);
        Assert.Equal(0, rgb.ControlStatus.WorkerTransportFailures);
        Assert.Equal(2, created);
    }

    [Fact]
    public void A_transient_native_rejection_recovers_in_the_same_child_without_hidden_retry()
    {
        var child = new TestSession(1010);
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => { created++; return child; });
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, 100));
        child.Backend.FailNext(0x8020, 0x00, -1);
        int requests = child.Requests.Count;
        Assert.False(rgb.SetColor(20, 30, 40, 100));
        Assert.Equal(requests + 1, child.Requests.Count);
        Assert.True(rgb.IsAvailable);
        Assert.Equal(0, child.DisposeCalls);
        var failure = rgb.ControlStatus.LastNativeFailure;
        Assert.True(rgb.SetColor(20, 30, 40, 100));
        var recovered = rgb.ControlStatus;
        rgb.Initialize(); // A healthy child is kept; a configuration refresh cannot reset the native context.
        Assert.Same(recovered, rgb.ControlStatus);
        Assert.Equal(1, created);
        Assert.Equal(1, rgb.ControlStatus.InitializationAttempts);
        Assert.Equal(1, rgb.ControlStatus.TotalNativeFailures);
        Assert.Equal(1, rgb.ControlStatus.TotalOutputFailures);
        Assert.Equal(failure, rgb.ControlStatus.LastNativeFailure);
        Assert.Equal(0, rgb.ControlStatus.WorkerRestartCount);
        Assert.All(child.Timeouts, timeout => Assert.True(timeout == TimeSpan.FromSeconds(5) || timeout == TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task All_getters_use_the_immutable_cache_while_an_exchange_holds_the_mutation_lock()
    {
        var child = new TestSession(1020) { BlockOperation = GpuRgbWorkerProtocol.Initialize };
        using var rgb = new IsolatedGpuRgbDevice(() => child);
        var initialize = Task.Run(rgb.Initialize);
        try
        {
            Assert.True(child.Entered.Wait(TimeSpan.FromSeconds(3)));
            var status = rgb.ControlStatus;
            int stateReads = child.StateReads;
            var reading = Task.Run(() =>
            {
                for (int i = 0; i < 10_000; i++)
                {
                    Assert.Same(status, rgb.ControlStatus);
                    Assert.False(rgb.IsAvailable);
                    Assert.True(rgb.ControlStatus.Initializing);
                    Assert.True(rgb.NeedsInitializationRetry);
                    Assert.Equal(string.Empty, rgb.Name);
                    Assert.Null(rgb.Diagnostic);
                    Assert.Null(rgb.DiagnosticText);
                }
            });
            await reading.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(stateReads, child.StateReads);
            Assert.False(initialize.IsCompleted);
        }
        finally { child.Continue.Set(); await initialize.WaitAsync(TimeSpan.FromSeconds(3)); }
        Assert.True(rgb.IsAvailable);
        Assert.False(rgb.ControlStatus.Initializing);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(child.Timeouts));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("request")]
    [InlineData("negative-counter")]
    [InlineData("regressed-sequence")]
    [InlineData("counter-jump")]
    [InlineData("enum")]
    [InlineData("led-count")]
    [InlineData("text")]
    [InlineData("diagnostic")]
    [InlineData("ack")]
    [InlineData("worker-identity")]
    [InlineData("stale-status")]
    public void Invalid_or_stale_reply_is_not_an_ack_and_is_retired_before_recovery(string fault)
    {
        var first = new TestSession(1030);
        var second = new TestSession(1031);
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => ++created == 1 ? first : AssertReplacement(first, second));
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, 100));
        var before = rgb.ControlStatus;
        var oldReply = first.LastReply!;
        first.Rewrite = reply => fault switch
        {
            "version" => reply with { Version = 99 },
            "generation" => reply with { Generation = reply.Generation - 1 },
            "request" => reply with { RequestId = reply.RequestId - 1 },
            "negative-counter" => reply with { Status = reply.Status with { TotalNativeFailures = -1 } },
            "regressed-sequence" => reply with { Status = reply.Status with { LastNativeOperation = reply.Status.LastNativeOperation! with { Sequence = 1 } } },
            "counter-jump" => reply with { Status = reply.Status with { LastNativeOperation = reply.Status.LastNativeOperation! with { Sequence = long.MaxValue } } },
            "enum" => reply with { Status = reply.Status with { AdapterObservation = (RgbAdapterObservation)9000 } },
            "led-count" => reply with { Status = reply.Status with { ControllerLedCount = 33 } },
            "text" => reply with { Status = reply.Status with { ControllerName = new string('A', 129) } },
            "diagnostic" => reply with { Status = reply.Status with { DiagnosticText = EngineText.Of(EngineTexts.GpuNvapiAccess, new string('x', 513)) } },
            "ack" => reply with { Accepted = false },
            "worker-identity" => reply with { Status = reply.Status with { WorkerProcessId = 88 } },
            "stale-status" => reply with { Status = oldReply.Status },
            _ => throw new InvalidOperationException()
        };
        int requests = first.Requests.Count;
        Assert.False(rgb.SetColor(100, 200, 250, 70));
        var failed = rgb.ControlStatus;
        Assert.False(failed.LastOutputAccepted);
        Assert.False(failed.ControlAvailable);
        Assert.True(failed.InitializationRetryNeeded);
        Assert.Null(failed.LastNativeFailure);
        Assert.Equal(0, failed.TotalNativeFailures);
        Assert.Equal(before.LastNativeOperation, failed.LastNativeOperation);
        Assert.Equal(before.LastOutputAcceptedUtc, failed.LastOutputAcceptedUtc);
        Assert.Equal(before.LastOutputAcceptedNativeSequence, failed.LastOutputAcceptedNativeSequence);
        Assert.Equal(1, failed.TotalOutputFailures);
        Assert.Equal(1, failed.WorkerTransportFailures);
        Assert.Equal(EngineTexts.GpuRgbWorkerFailed, failed.DiagnosticText!.Code);
        Assert.Contains("invalid-reply", failed.LastWorkerFailure);
        Assert.NotNull(failed.LastWorkerFailureUtc);
        Assert.Equal(requests + 1, first.Requests.Count);
        Assert.True(first.HasExited);
        Assert.Null(failed.WorkerProcessId);
        Assert.Equal(1, created);
        rgb.Initialize();
        Assert.True(rgb.SetColor(100, 200, 250, 70));
        Assert.Equal(2, created);
        Assert.Equal(failed.LastWorkerFailure, rgb.ControlStatus.LastWorkerFailure);
        Assert.Equal(failed.LastWorkerFailureUtc, rgb.ControlStatus.LastWorkerFailureUtc);
        Assert.Equal(1, rgb.ControlStatus.WorkerTransportFailures);
        Assert.Equal("invalid-reply", rgb.ControlStatus.LastWorkerRestartReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Exchange_timeout_has_an_explicit_transport_result_and_no_native_code_or_automatic_retry(bool duringInitialization)
    {
        var first = new TestSession(1040);
        var second = new TestSession(1041);
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => ++created == 1 ? first : AssertReplacement(first, second));
        if (!duringInitialization)
        {
            rgb.Initialize();
            Assert.True(rgb.SetColor(1, 2, 3, 100));
        }
        var old = rgb.ControlStatus;
        first.NextException = new TimeoutException("The fake exchange reached its deadline.");
        int requests = first.Requests.Count;
        if (duringInitialization) rgb.Initialize();
        else Assert.False(rgb.SetColor(4, 5, 6, 100));
        var timedOut = rgb.ControlStatus;
        Assert.Equal(old.LastNativeOperation, timedOut.LastNativeOperation);
        Assert.Null(timedOut.LastNativeFailure);
        Assert.Equal(0, timedOut.TotalNativeFailures);
        Assert.Equal(duringInitialization ? 0 : 1, timedOut.TotalOutputFailures);
        Assert.Equal(old.LastOutputAcceptedUtc, timedOut.LastOutputAcceptedUtc);
        Assert.Equal(1, timedOut.WorkerTransportFailures);
        Assert.Contains("exchange-timeout", timedOut.LastWorkerFailure);
        Assert.Equal(EngineTexts.GpuRgbWorkerFailed, timedOut.DiagnosticText!.Code);
        Assert.Contains("processo auxiliar", timedOut.Diagnostic);
        Assert.Contains("GPU RGB worker failed", EngineTexts.Format(timedOut.DiagnosticText!, EngineTexts.EnglishCulture));
        Assert.DoesNotContain("NVAPI", timedOut.Diagnostic);
        Assert.Equal(1, created);
        Assert.Equal(requests + 1, first.Requests.Count);
        Assert.Equal(duringInitialization ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2), first.Timeouts.Last());
        rgb.Initialize();
        Assert.True(rgb.SetColor(4, 5, 6, 100));
        Assert.Equal(2, created);
    }

    [Fact]
    public void A_failed_termination_blocks_replacement_until_exit_is_confirmed()
    {
        var first = new TestSession(1050) { DisposeFailuresRemaining = 1 };
        var second = new TestSession(1051);
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => ++created == 1 ? first : AssertReplacement(first, second));
        rgb.Initialize();
        first.Backend.PersistentColorStatus = -6;
        Assert.False(rgb.SetColor(1, 2, 3, 100));
        var failure = rgb.ControlStatus.LastNativeFailure;
        rgb.Initialize();
        Assert.Equal(1, created);
        Assert.False(first.HasExited);
        Assert.False(rgb.IsAvailable);
        Assert.True(rgb.NeedsInitializationRetry);
        Assert.Equal(1050, rgb.ControlStatus.WorkerProcessId);
        Assert.Contains("worker-termination-unconfirmed", rgb.ControlStatus.LastWorkerFailure);
        int exchanges = first.Requests.Count;
        Assert.False(rgb.SetColor(4, 5, 6, 100));
        Assert.Equal(exchanges, first.Requests.Count);
        rgb.Initialize();
        Assert.Equal(2, created);
        Assert.True(rgb.SetColor(4, 5, 6, 100));
        Assert.Equal(failure, rgb.ControlStatus.LastNativeFailure);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_child_cannot_erase_or_rewrite_a_previous_native_error(bool erase)
    {
        var child = new TestSession(1055);
        using var rgb = new IsolatedGpuRgbDevice(() => child);
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, 100));
        child.Backend.FailNext(0x8020, 0x00, -1);
        Assert.False(rgb.SetColor(4, 5, 6, 100));
        var failed = rgb.ControlStatus;
        child.Rewrite = reply => reply with
        {
            Status = erase ? reply.Status with { LastNativeFailure = null, TotalNativeFailures = 0 }
                : reply.Status with { LastNativeFailure = reply.Status.LastNativeFailure! with { AtUtc = DateTimeOffset.UnixEpoch } }
        };
        Assert.False(rgb.SetColor(4, 5, 6, 100));
        Assert.Equal(failed.LastNativeFailure, rgb.ControlStatus.LastNativeFailure);
        Assert.Equal(failed.TotalNativeFailures, rgb.ControlStatus.TotalNativeFailures);
        Assert.Equal(1, rgb.ControlStatus.WorkerTransportFailures);
        Assert.Contains("invalid-reply", rgb.ControlStatus.LastWorkerFailure);
    }

    [Fact]
    public void Brightness_preserves_the_existing_percent_clamp_before_private_protocol_validation()
    {
        var child = new TestSession(1056);
        using var rgb = new IsolatedGpuRgbDevice(() => child);
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, byte.MaxValue));
        Assert.Equal(100, child.Requests.Last().Brightness);
        Assert.Equal(0, rgb.ControlStatus.WorkerTransportFailures);
    }

    [Fact]
    public void Dispose_returning_without_confirmed_exit_is_not_permission_for_a_second_controller()
    {
        var first = new TestSession(1060) { ConfirmExitOnDispose = false };
        var second = new TestSession(1061);
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => ++created == 1 ? first : AssertReplacement(first, second));
        rgb.Initialize();
        first.Backend.PersistentColorStatus = -6;
        Assert.False(rgb.SetColor(1, 2, 3, 100));
        rgb.Initialize();
        Assert.Equal(1, created);
        Assert.False(first.HasExited);
        Assert.Contains("worker-termination-unconfirmed", rgb.ControlStatus.LastWorkerFailure);
        first.ConfirmExitOnDispose = true;
        rgb.Initialize();
        Assert.Equal(2, created);
        Assert.True(first.HasExited);
    }

    [Fact]
    public void Failed_dispose_remains_terminal_for_the_wrapper_even_if_exit_is_confirmed_later()
    {
        var first = new TestSession(1070) { DisposeFailuresRemaining = 1 };
        int created = 0;
        var rgb = new IsolatedGpuRgbDevice(() => { created++; return first; });
        rgb.Initialize();
        Assert.Throws<InvalidOperationException>(rgb.Dispose);
        Assert.False(rgb.IsAvailable);
        Assert.False(rgb.NeedsInitializationRetry);
        rgb.Initialize();
        Assert.False(rgb.SetColor(1, 2, 3, 100));
        Assert.Equal(1, created);
        rgb.Dispose(); // A caller may retry cleanup, but may never revive the disposed provider.
        Assert.True(first.HasExited);
        Assert.Null(rgb.ControlStatus.WorkerProcessId);
        rgb.Initialize();
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task Shutdown_waits_for_the_bounded_exchange_and_never_restarts_after_disposal()
    {
        var child = new TestSession(1080);
        int created = 0;
        var rgb = new IsolatedGpuRgbDevice(() => { created++; return child; });
        rgb.Initialize();
        child.BlockOperation = GpuRgbWorkerProtocol.Color;
        var output = Task.Run(() => rgb.SetColor(1, 2, 3, 100));
        Task? shutdown = null;
        try
        {
            Assert.True(child.Entered.Wait(TimeSpan.FromSeconds(3)));
            shutdown = Task.Run(rgb.Dispose);
            Assert.Equal(0, child.DisposeCalls);
            Assert.Equal(1080, rgb.ControlStatus.WorkerProcessId);
        }
        finally
        {
            child.Continue.Set();
            await output.WaitAsync(TimeSpan.FromSeconds(3));
            if (shutdown != null) await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
            else rgb.Dispose();
        }
        Assert.True(child.HasExited);
        Assert.False(rgb.IsAvailable);
        rgb.Initialize();
        Assert.False(rgb.SetColor(4, 5, 6, 100));
        Assert.Equal(1, created);
    }

    [Fact]
    public void A_crashed_healthy_child_is_detected_before_cache_can_ack_another_color()
    {
        var first = new TestSession(1090);
        var second = new TestSession(1091);
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => ++created == 1 ? first : AssertReplacement(first, second));
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, 100));
        int exchanges = first.Requests.Count;
        first.ExitExternally();
        Assert.False(rgb.SetColor(4, 5, 6, 100));
        Assert.Equal(exchanges, first.Requests.Count);
        Assert.False(rgb.ControlStatus.LastOutputAccepted);
        Assert.Contains("worker-exited", rgb.ControlStatus.LastWorkerFailure);
        Assert.Equal(EngineTexts.GpuRgbWorkerFailed, rgb.DiagnosticText!.Code);
        Assert.DoesNotContain("NVAPI", rgb.Diagnostic);
        rgb.Initialize();
        Assert.True(rgb.SetColor(4, 5, 6, 100));
        Assert.Equal(1, rgb.ControlStatus.WorkerRestartCount);
        Assert.Equal(1, rgb.ControlStatus.WorkerTransportFailures);
    }

    [Fact]
    public void Counter_and_request_order_are_monotonic_across_repeated_recovery_generations()
    {
        var children = new List<TestSession>();
        using var rgb = new IsolatedGpuRgbDevice(() =>
        {
            if (children.Count > 0) Assert.True(children[^1].HasExited);
            var child = new TestSession(1100 + children.Count);
            children.Add(child);
            return child;
        });
        long nativeSequence = 0, requestId = 0;
        RgbNativeOperationResult? retained = null;
        for (int generation = 1; generation <= 12; generation++)
        {
            rgb.Initialize();
            Assert.Equal(retained, rgb.ControlStatus.LastNativeFailure);
            Assert.True(rgb.SetColor(1, 2, 3, 100));
            var child = children[^1];
            child.Backend.PersistentColorStatus = -6;
            Assert.False(rgb.SetColor(4, 5, 6, 100));
            var status = rgb.ControlStatus;
            Assert.True(status.LastNativeOperation!.Sequence > nativeSequence);
            nativeSequence = status.LastNativeOperation.Sequence;
            retained = status.LastNativeFailure;
            Assert.Equal(generation, status.TotalNativeFailures);
            Assert.Equal(generation, status.TotalOutputFailures);
            Assert.Equal(generation, status.InitializationAttempts);
            Assert.Equal(generation, status.WorkerGeneration);
            Assert.Equal(generation - 1, status.WorkerRestartCount);
            Assert.Equal(0, status.WorkerTransportFailures);
            foreach (var request in child.Requests)
            {
                Assert.True(request.RequestId > requestId);
                requestId = request.RequestId;
                Assert.Equal(generation, request.Generation);
            }
            Assert.Equal(3, child.Requests.Count); // Initialize plus exactly two requested colors; no hidden queue/retry.
        }
    }

    private static TestSession AssertReplacement(TestSession old, TestSession replacement)
    {
        Assert.True(old.HasExited);
        return replacement;
    }

    [Theory]
    [InlineData("process-zero")]
    [InlineData("process-negative")]
    [InlineData("process-foreign")]
    [InlineData("thread-zero")]
    [InlineData("thread-negative")]
    [InlineData("handle-short")]
    [InlineData("handle-prefix")]
    [InlineData("handle-lowercase")]
    [InlineData("handle-nonhex")]
    [InlineData("duration-negative")]
    [InlineData("duration-nan")]
    [InlineData("duration-infinite")]
    [InlineData("version-too-long")]
    [InlineData("version-control")]
    public void Invalid_native_context_is_rejected_without_inventing_a_native_failure(string defect)
    {
        var child = new TestSession(1200);
        using var rgb = new IsolatedGpuRgbDevice(() => child);
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, 100));
        var accepted = rgb.ControlStatus;
        child.NativeContext = operation => defect switch
        {
            "process-zero" => operation with { ProcessId = 0 },
            "process-negative" => operation with { ProcessId = -1 },
            "process-foreign" => operation with { ProcessId = 1201 },
            "thread-zero" => operation with { ThreadId = 0 },
            "thread-negative" => operation with { ThreadId = -1 },
            "handle-short" => operation with { GpuHandle = "1234" },
            "handle-prefix" => operation with { GpuHandle = "0x0123456789ABCDEF" },
            "handle-lowercase" => operation with { GpuHandle = "0123456789abcdef" },
            "handle-nonhex" => operation with { GpuHandle = "GGGGGGGGGGGGGGGG" },
            "duration-negative" => operation with { TransferDurationMicroseconds = -0.1 },
            "duration-nan" => operation with { TransferDurationMicroseconds = double.NaN },
            "duration-infinite" => operation with { TransferDurationMicroseconds = double.PositiveInfinity },
            "version-too-long" => operation with { LibraryVersion = new string('v', 129) },
            "version-control" => operation with { LibraryVersion = "32.0\n9999" },
            _ => throw new InvalidOperationException()
        };
        Assert.False(rgb.SetColor(4, 5, 6, 100));
        var rejected = rgb.ControlStatus;
        Assert.Equal(accepted.LastNativeOperation, rejected.LastNativeOperation);
        Assert.Null(rejected.LastNativeFailure);
        Assert.Equal(0, rejected.TotalNativeFailures);
        Assert.Equal(1, rejected.WorkerTransportFailures);
        Assert.Equal(EngineTexts.GpuRgbWorkerFailed, rejected.DiagnosticText!.Code);
        Assert.Contains("invalid-reply", rejected.LastWorkerFailure);
        Assert.True(child.HasExited);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Optional_native_context_accepts_legacy_nulls_and_bounded_present_values(bool legacy)
    {
        var child = new TestSession(1210);
        child.NativeContext = operation => legacy ? operation with
        {
            ProcessId = null, ThreadId = null, GpuHandle = null, TransferDurationMicroseconds = null, LibraryVersion = null
        } : operation with
        {
            ProcessId = 1210, ThreadId = 7, GpuHandle = "0123456789ABCDEF", TransferDurationMicroseconds = 0,
            LibraryVersion = new string('v', 128)
        };
        using var rgb = new IsolatedGpuRgbDevice(() => child);
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, 100));
        var operation = rgb.ControlStatus.LastNativeOperation!;
        Assert.Equal(legacy ? null : (int?)1210, operation.ProcessId);
        Assert.Equal(legacy ? null : (int?)7, operation.ThreadId);
        Assert.Equal(legacy ? null : "0123456789ABCDEF", operation.GpuHandle);
        Assert.Equal(legacy ? null : (double?)0, operation.TransferDurationMicroseconds);
        Assert.Equal(legacy ? null : new string('v', 128), operation.LibraryVersion);
        Assert.Equal(0, rgb.ControlStatus.WorkerTransportFailures);
    }

    [Fact]
    public void Historical_native_context_is_retained_when_a_new_worker_maps_its_own_sequence()
    {
        var first = new TestSession(1220)
        {
            NativeContext = operation => operation with
            {
                ProcessId = 1220, ThreadId = 17, GpuHandle = "00000000000000AA", TransferDurationMicroseconds = 123.25,
                LibraryVersion = "32.0.15.9001"
            }
        };
        var second = new TestSession(1221)
        {
            NativeContext = operation => operation with
            {
                ProcessId = 1221, ThreadId = 19, GpuHandle = "00000000000000BB", TransferDurationMicroseconds = 51.5,
                LibraryVersion = "32.0.15.9002"
            }
        };
        int created = 0;
        using var rgb = new IsolatedGpuRgbDevice(() => ++created == 1 ? first : AssertReplacement(first, second));
        rgb.Initialize();
        Assert.True(rgb.SetColor(1, 2, 3, 100));
        first.Backend.PersistentColorStatus = -6;
        Assert.False(rgb.SetColor(4, 5, 6, 100));
        var error = rgb.ControlStatus.LastNativeFailure!;
        Assert.Equal(1220, error.ProcessId);
        Assert.Equal(EngineTexts.GpuAuraFailed, rgb.ControlStatus.DiagnosticText!.Code);
        rgb.Initialize();
        Assert.True(rgb.SetColor(4, 5, 6, 100));
        var renewed = rgb.ControlStatus;
        Assert.Equal(error, renewed.LastNativeFailure);
        Assert.Equal(17, renewed.LastNativeFailure!.ThreadId);
        Assert.Equal("00000000000000AA", renewed.LastNativeFailure.GpuHandle);
        Assert.Equal(123.25, renewed.LastNativeFailure.TransferDurationMicroseconds);
        Assert.Equal("32.0.15.9001", renewed.LastNativeFailure.LibraryVersion);
        Assert.Equal(1221, renewed.LastNativeOperation!.ProcessId);
        Assert.Equal("00000000000000BB", renewed.LastNativeOperation.GpuHandle);
        Assert.True(renewed.LastNativeOperation.Sequence > error.Sequence);
        Assert.Equal(0, renewed.WorkerTransportFailures);
    }

    [Fact]
    public void Native_access_diagnostic_keeps_its_native_code_and_translation_through_the_worker()
    {
        using var native = new AsusGpuRgb(new NativeAccessFailureBackend());
        native.Initialize();
        var child = new TestSession(1230) { Rewrite = reply => reply with { Accepted = false, Status = native.ControlStatus } };
        using var rgb = new IsolatedGpuRgbDevice(() => child);
        rgb.Initialize();
        Assert.False(rgb.IsAvailable);
        Assert.Equal(EngineTexts.GpuNvapiAccess, rgb.DiagnosticText!.Code);
        Assert.Contains("NVAPI", rgb.Diagnostic);
        Assert.Contains("NVAPI", EngineTexts.Format(rgb.DiagnosticText!, EngineTexts.EnglishCulture));
        Assert.Equal(0, rgb.ControlStatus.WorkerTransportFailures);
        Assert.Null(rgb.ControlStatus.LastWorkerFailure);
    }

    private sealed class NativeAccessFailureBackend : IAsusGpuRgbBackend
    {
        public int EnsureInitialized(out bool nativeInitializationCalled) => throw new EntryPointNotFoundException("Fake native entry point unavailable.");
        public int EnumeratePhysicalGpus(IntPtr[] handles, out int count) => throw new InvalidOperationException("No enumeration expected.");
        public int GetPciIdentifiers(IntPtr gpu, out uint device, out uint subsystem) => throw new InvalidOperationException("No PCI query expected.");
        public int Transfer(IntPtr gpu, bool read, byte command, byte[] data, int count) => throw new InvalidOperationException("No transfer expected.");
        public void Dispose() { }
    }

    private sealed class TestSession(int processId) : IGpuRgbWorkerSession
    {
        internal FakeAsusGpuRgbBackend Backend { get; } = new();
        private AsusGpuRgb? _device;
        private bool _exited, _deviceDisposed;
        private int _stateReads;
        public int ProcessId => processId;
        public bool HasExited { get { Interlocked.Increment(ref _stateReads); return _exited; } }
        internal int StateReads => Volatile.Read(ref _stateReads);
        internal int DisposeCalls { get; private set; }
        internal int DisposeFailuresRemaining { get; set; }
        internal bool ConfirmExitOnDispose { get; set; } = true;
        internal Exception? NextException { get; set; }
        internal string? BlockOperation { get; set; }
        internal ManualResetEventSlim Entered { get; } = new(false);
        internal ManualResetEventSlim Continue { get; } = new(false);
        internal ConcurrentQueue<GpuRgbWorkerRequest> Requests { get; } = new();
        internal ConcurrentQueue<TimeSpan> Timeouts { get; } = new();
        internal Func<GpuRgbWorkerReply, GpuRgbWorkerReply>? Rewrite { get; set; }
        internal Func<RgbNativeOperationResult, RgbNativeOperationResult>? NativeContext { get; set; }
        internal GpuRgbWorkerReply? LastReply { get; private set; }

        public GpuRgbWorkerReply Exchange(GpuRgbWorkerRequest request, TimeSpan timeout)
        {
            Requests.Enqueue(request);
            Timeouts.Enqueue(timeout);
            if (_exited) throw new EndOfStreamException("Fake worker exited.");
            if (NextException is { } error) { NextException = null; throw error; }
            if (BlockOperation == request.Operation)
            {
                Entered.Set();
                if (!Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fake exchange gate timed out.");
            }
            _device ??= new AsusGpuRgb(Backend);
            bool accepted;
            if (request.Operation == GpuRgbWorkerProtocol.Initialize) { _device.Initialize(); accepted = _device.IsAvailable; }
            else accepted = _device.SetColor(request.R, request.G, request.B, request.Brightness);
            // Fake native calls execute in the test runner; the session emulates the isolated child's identity.
            var status = _device.ControlStatus;
            status = status with { LastNativeOperation = Context(status.LastNativeOperation), LastNativeFailure = Context(status.LastNativeFailure) };
            var reply = new GpuRgbWorkerReply
            {
                Generation = request.Generation, RequestId = request.RequestId, Accepted = accepted, Status = status
            };
            LastReply = reply;
            return Rewrite?.Invoke(reply) ?? reply;
        }

        private RgbNativeOperationResult? Context(RgbNativeOperationResult? operation)
        {
            if (operation == null) return null;
            var childOperation = operation with { ProcessId = operation.ProcessId == null ? null : processId };
            return NativeContext?.Invoke(childOperation) ?? childOperation;
        }

        internal bool Reprobe() { _device!.Initialize(); return _device.IsAvailable; }
        internal bool DirectColor() => _device!.SetColor(50, 60, 70, 100);
        internal void ExitExternally() => _exited = true;

        public void Dispose()
        {
            DisposeCalls++;
            if (DisposeFailuresRemaining > 0) { DisposeFailuresRemaining--; throw new IOException("Fake termination was not confirmed."); }
            if (!ConfirmExitOnDispose) return;
            _exited = true;
            if (!_deviceDisposed)
            {
                _deviceDisposed = true;
                if (_device != null) _device.Dispose();
                else Backend.Dispose();
            }
        }
    }
}
