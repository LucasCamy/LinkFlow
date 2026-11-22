using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LinkFlow.Tests;

public sealed class GpuRgbNativeFailureHistoryTests
{
    [Fact]
    public void A_single_output_keeps_all_three_actual_fake_backend_failures_in_order()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(1, 2, 3, 100));
        // A block fallback can succeed after one SDK rejection, before a later LED rejects the output.
        backend.FailNext(0x8100, 0x03, -1);
        backend.FailNext(0x8103, 0x03, -1);
        backend.FailNext(0x8103, 0x00, -1);
        Assert.False(gpu.SetColor(4, 5, 6, 100));
        var status = gpu.ControlStatus;
        var history = Assert.IsType<RgbNativeFailureHistory>(status.NativeFailureHistory);
        Assert.Equal(3, status.TotalNativeFailures);
        Assert.Equal(1, status.TotalOutputFailures);
        Assert.Equal(new long[] { 1, 2, 3 }, history.Records.Select(r => r.FailureOrdinal));
        Assert.Equal(new ushort?[] { 0x8100, 0x8103, 0x8103 }, history.Records.Select(r => r.Register));
        Assert.Equal(new byte?[] { 3, 3, 0 }, history.Records.Select(r => r.Command));
        Assert.True(history.Records.Zip(history.Records.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
        Assert.All(history.Records, r => { Assert.Equal(-1, r.Status); Assert.Equal(Environment.ProcessId, r.ProcessId); });
        Assert.Equal(history.Records[^1], status.LastNativeFailure);
        Assert.Equal(0, history.EvictedCount);
        Assert.Equal(0, history.MissingCount);
    }

    [Fact]
    public void The_driver_keeps_eight_contexts_and_reuses_the_history_through_healthy_frames_and_getters()
    {
        var backend = new FakeAsusGpuRgbBackend();
        using var gpu = new AsusGpuRgb(backend);
        gpu.Initialize();
        Assert.True(gpu.SetColor(1, 2, 3, 100));
        for (int i = 0; i < 20; i++)
        {
            backend.FailNext(0x8020, 0, -1);
            Assert.False(gpu.SetColor(1, 2, 3, 100));
        }
        var history = gpu.ControlStatus.NativeFailureHistory!;
        Assert.Equal(20, history.TotalFailures);
        Assert.Equal(12, history.EvictedCount);
        Assert.Equal(0, history.MissingCount);
        Assert.Equal(Enumerable.Range(13, 8).Select(i => (long)i), history.Records.Select(r => r.FailureOrdinal));
        for (int i = 0; i < 50; i++)
        {
            Assert.True(gpu.SetColor(4, 5, 6, 100));
            Assert.Same(history, gpu.ControlStatus.NativeFailureHistory);
        }
        int transfers = backend.TransferCalls;
        for (int i = 0; i < 1000; i++) Assert.Same(history, gpu.ControlStatus.NativeFailureHistory);
        Assert.Equal(transfers, backend.TransferCalls);
        AssertInvariant(history);
    }

    [Fact]
    public void History_and_batch_defensively_copy_and_expose_no_mutable_list()
    {
        var source = new[] { Failure(1) };
        var history = new RgbNativeFailureHistory { TotalFailures = 1, Records = source };
        var batch = new GpuRgbNativeFailureBatch { TotalFailures = 1, Records = source };
        source[0] = Failure(2);
        Assert.Equal(1, history.Records[0].FailureOrdinal);
        Assert.Equal(1, batch.Records[0].FailureOrdinal);
        Assert.True(Assert.IsAssignableFrom<IList>(history.Records).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => Assert.IsAssignableFrom<IList>(batch.Records)[0] = Failure(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbNativeFailureHistory { Records = new RgbNativeOperationResult[9] });
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuRgbNativeFailureBatch { Records = new RgbNativeOperationResult[9] });
        Assert.Equal(1, Json.Clone(history).Records[0].FailureOrdinal);
    }

    [Fact]
    public async Task Host_sends_a_baseline_and_changed_window_once_and_never_serializes_history_in_healthy_replies()
    {
        var device = new HistoryDevice { FailOnColor = 2, FailuresNextColor = 3 };
        var result = await Host(device, 5);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, device.Disposals);
        using var output = new MemoryStream(result.Bytes);
        var replies = new List<GpuRgbWorkerReply>();
        while (await Read(output) is { } reply) replies.Add(reply);
        Assert.Equal(6, replies.Count);
        Assert.NotNull(replies[0].NativeFailureBatch);
        Assert.Empty(replies[0].NativeFailureBatch!.Records);
        Assert.Equal(3, replies[2].NativeFailureBatch!.Records.Count);
        Assert.Null(replies[1].NativeFailureBatch);
        Assert.All(replies.Skip(3), reply => Assert.Null(reply.NativeFailureBatch));
        Assert.All(replies, reply => Assert.Null(reply.Status.NativeFailureHistory));
        var healthy = replies[^1];
        string wire = Encoding.UTF8.GetString(GpuRgbWorkerFraming.EncodeReply(healthy, 16384));
        Assert.DoesNotContain("nativeFailureBatch", wire);
        Assert.DoesNotContain("nativeFailureHistory", wire);
        Assert.Equal(3, healthy.Status.TotalNativeFailures);
        Assert.Equal(3, healthy.Status.LastNativeFailure!.FailureOrdinal);
    }

    [Fact]
    public async Task Byte_budget_trims_only_an_oldest_prefix_and_roundtrips_unicode_in_fragmented_frames()
    {
        var records = Enumerable.Range(1, 8).Select(i => Failure(i) with { LibraryVersion = new string('\u6C34', 128) }).ToArray();
        var reply = Reply(records, 8) with
        {
            Status = Status(records, 8) with
            {
                Source = new string('\u6E90', 128), ControllerName = new string('\u540D', 128),
                DiagnosticText = EngineText.Of(EngineTexts.GpuAuraFailed, new string('\u6DB2', 500), "00", "-1")
            }
        };
        byte[] encoded = GpuRgbWorkerFraming.EncodeReply(reply, 16384);
        Assert.InRange(encoded.Length, 1, 16384);
        var reduced = JsonSerializer.Deserialize<GpuRgbWorkerReply>(encoded, Json.Compact)!;
        Assert.True(reduced.NativeFailureBatch!.TransportOmittedCount > 0);
        Assert.Equal(8, reduced.NativeFailureBatch.TransportOmittedCount + reduced.NativeFailureBatch.Records.Count);
        Assert.Equal(8, reduced.NativeFailureBatch.Records[^1].FailureOrdinal);
        Assert.Equal(reply.Accepted, reduced.Accepted);
        Assert.Equal(reply.Status.LastNativeFailure, reduced.Status.LastNativeFailure);
        using var fragmented = new FragmentedStream();
        await GpuRgbWorkerFraming.WriteReplyAsync(fragmented, reply, CancellationToken.None);
        fragmented.Position = 0;
        var copy = await Read(fragmented);
        Assert.Equal(reduced.NativeFailureBatch.Records, copy!.NativeFailureBatch!.Records);
    }

    [Fact]
    public void Diagnostic_budget_can_omit_the_batch_without_changing_the_ack_but_cannot_hide_an_oversized_base()
    {
        var reply = Reply([Failure(1)], 1);
        int baseLength = GpuRgbWorkerFraming.EncodeReply(reply with { NativeFailureBatch = null }, int.MaxValue).Length;
        var copy = JsonSerializer.Deserialize<GpuRgbWorkerReply>(GpuRgbWorkerFraming.EncodeReply(reply, baseLength), Json.Compact)!;
        Assert.Null(copy.NativeFailureBatch);
        Assert.Equal(reply.Accepted, copy.Accepted);
        Assert.Equal(reply.Status.LastNativeFailure, copy.Status.LastNativeFailure);
        Assert.Throws<InvalidDataException>(() => GpuRgbWorkerFraming.EncodeReply(reply, baseLength - 1));
    }

    [Theory]
    [InlineData("{\"nativeFailureBatch\":{\"totalFailures\":1,\"totalFailures\":1}}")]
    [InlineData("{\"nativeFailureBatch\":{\"records\":[{\"failureOrdinal\":1,\"failureOrdinal\":2}]}}")]
    [InlineData("{\"nativeFailureBatch\":{\"unknown\":1}}")]
    public async Task Ambiguous_or_unknown_batch_json_is_rejected_before_acceptance(string json)
    {
        using var raw = RawFrame(Encoding.UTF8.GetBytes(json));
        var exception = await Record.ExceptionAsync(() => Read(raw));
        Assert.True(exception is InvalidDataException or JsonException);
    }

    [Fact]
    public void Parent_merges_a_burst_and_preserves_the_same_cached_history_when_a_healthy_batch_is_absent()
    {
        var session = new HistorySession(32001);
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 3;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(new long[] { 1, 2, 3 }, history.Records.Select(r => r.FailureOrdinal));
        Assert.Equal(3, device.ControlStatus.TotalNativeFailures);
        Assert.Equal(1, device.ControlStatus.TotalOutputFailures);
        Assert.Equal(0, history.MissingCount);
        Assert.True(device.SetColor(4, 5, 6, 100));
        Assert.Same(history, device.ControlStatus.NativeFailureHistory);
        Assert.Equal(history.Records[^1], device.ControlStatus.LastNativeFailure);
        AssertInvariant(history);
    }

    [Fact]
    public void Late_context_backfill_reduces_missing_without_recounting_received_or_evicted_records()
    {
        var session = new HistorySession(32002);
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 2;
        session.OmitPrefix = 1;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var incomplete = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(1, incomplete.MissingCount);
        Assert.Equal(2, Assert.Single(incomplete.Records).FailureOrdinal);
        Assert.True(device.SetColor(1, 2, 3, 100));
        Assert.Same(incomplete, device.ControlStatus.NativeFailureHistory); // Absence cannot fabricate a recovery.
        session.Device.FailuresNextColor = 1;
        session.OmitPrefix = 0;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var backfilled = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(0, backfilled.MissingCount);
        Assert.Equal(new long[] { 1, 2, 3 }, backfilled.Records.Select(r => r.FailureOrdinal));
        session.RepeatBatch = true;
        Assert.True(device.SetColor(1, 2, 3, 100));
        Assert.Same(backfilled, device.ControlStatus.NativeFailureHistory);
        AssertInvariant(backfilled);
    }

    [Fact]
    public void Source_ring_evictions_are_missing_at_the_parent_until_received_not_parent_evictions()
    {
        var session = new HistorySession(32003);
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 20;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(20, history.TotalFailures);
        Assert.Equal(12, history.MissingCount);
        Assert.Equal(0, history.EvictedCount);
        Assert.Equal(Enumerable.Range(13, 8).Select(i => (long)i), history.Records.Select(r => r.FailureOrdinal));
        session.Device.FailuresNextColor = 1;
        Assert.False(device.SetColor(1, 2, 3, 100));
        history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(21, history.TotalFailures);
        Assert.Equal(12, history.MissingCount);
        Assert.Equal(1, history.EvictedCount);
        AssertInvariant(history);
    }

    [Fact]
    public void An_omitted_batch_marks_only_new_counter_ordinals_missing_and_later_recovers_its_recent_tail()
    {
        var session = new HistorySession(32004);
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.OmitBatch = true;
        session.Device.FailuresNextColor = 20;
        Assert.False(device.SetColor(1, 2, 3, 100));
        Assert.Equal(19, device.ControlStatus.NativeFailureHistory!.MissingCount);
        Assert.Equal(20, Assert.Single(device.ControlStatus.NativeFailureHistory.Records).FailureOrdinal);
        session.OmitBatch = false;
        session.Device.FailuresNextColor = 1;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(13, history.MissingCount);
        Assert.Equal(0, history.EvictedCount);
        Assert.Equal(Enumerable.Range(14, 8).Select(i => (long)i), history.Records.Select(r => r.FailureOrdinal));
        AssertInvariant(history);
    }

    [Theory]
    [InlineData("total")]
    [InlineData("ring-evictions")]
    [InlineData("negative-omissions")]
    [InlineData("omission-count")]
    [InlineData("duplicate-ordinal")]
    [InlineData("noncontiguous")]
    [InlineData("stale-window")]
    [InlineData("sequence-order")]
    [InlineData("successful-record")]
    [InlineData("foreign-context")]
    [InlineData("last-failure")]
    [InlineData("new-failure-old-sequence")]
    [InlineData("last-operation-different-failure")]
    public void A_malformed_batch_cannot_advance_native_counters_or_replace_previously_accepted_evidence(string defect)
    {
        var session = new HistorySession(32005);
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var before = device.ControlStatus;
        session.Device.FailuresNextColor = 2;
        session.Rewrite = reply =>
        {
            var batch = reply.NativeFailureBatch!;
            var records = batch.Records.ToArray();
            batch = defect switch
            {
                "total" => batch with { TotalFailures = 3 },
                "ring-evictions" => batch with { RingEvictedCount = 1 },
                "negative-omissions" => batch with { TransportOmittedCount = -1 },
                "omission-count" => batch with { TransportOmittedCount = 1 },
                _ => batch with { Records = records.Select((r, i) => defect switch
                {
                    "duplicate-ordinal" when i == 1 => r with { FailureOrdinal = records[0].FailureOrdinal },
                    "noncontiguous" when i == 1 => r with { FailureOrdinal = 7 },
                    "stale-window" => r with { FailureOrdinal = r.FailureOrdinal + 10 },
                    "sequence-order" when i == 1 => r with { Sequence = records[0].Sequence },
                    "successful-record" when i == 1 => r with { Status = 0, FailureOrdinal = 0 },
                    "foreign-context" when i == 1 => r with { ProcessId = 9999 },
                    "last-failure" when i == records.Length - 1 => r with { Register = 0xFFFF },
                    "new-failure-old-sequence" when i == 2 => r with { Sequence = before.LastNativeOperation!.Sequence },
                    _ => r
                }).ToArray() }
            };
            return reply with
            {
                NativeFailureBatch = batch,
                Status = defect == "last-operation-different-failure" ? reply.Status with
                {
                    LastNativeOperation = reply.Status.LastNativeOperation! with { Status = -1, FailureOrdinal = reply.Status.TotalNativeFailures }
                } : reply.Status
            };
        };
        Assert.False(device.SetColor(4, 5, 6, 100));
        var after = device.ControlStatus;
        Assert.Equal(before.TotalNativeFailures, after.TotalNativeFailures);
        Assert.Equal(before.LastNativeOperation, after.LastNativeOperation);
        Assert.Equal(before.LastNativeFailure, after.LastNativeFailure);
        Assert.Same(before.NativeFailureHistory, after.NativeFailureHistory);
        Assert.Equal(1, after.WorkerTransportFailures);
        Assert.Contains("invalid-reply", after.LastWorkerFailure);
        Assert.True(session.HasExited);
    }

    [Fact]
    public void Changed_previously_received_context_is_rejected_even_when_the_latest_context_is_valid()
    {
        var session = new HistorySession(32006);
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 1;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var before = device.ControlStatus;
        session.Device.FailuresNextColor = 1;
        session.Rewrite = reply => reply with
        {
            NativeFailureBatch = reply.NativeFailureBatch! with
            {
                Records = reply.NativeFailureBatch.Records.Select((r, i) => i == 0 ? r with { Register = 0xFFFF } : r).ToArray()
            }
        };
        Assert.False(device.SetColor(4, 5, 6, 100));
        Assert.Equal(1, device.ControlStatus.TotalNativeFailures);
        Assert.Same(before.NativeFailureHistory, device.ControlStatus.NativeFailureHistory);
        Assert.Equal(1, device.ControlStatus.WorkerTransportFailures);
    }

    [Fact]
    public void An_old_window_cannot_reintroduce_a_context_the_parent_already_evicted()
    {
        var session = new HistorySession(32013);
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 8;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var oldWindow = device.ControlStatus.NativeFailureHistory!.Records.ToArray();
        session.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var before = device.ControlStatus;
        Assert.Equal(2, before.NativeFailureHistory!.EvictedCount);
        session.Device.FailuresNextColor = 1;
        session.Rewrite = reply => reply with
        { NativeFailureBatch = reply.NativeFailureBatch! with { Records = oldWindow } };
        Assert.False(device.SetColor(1, 2, 3, 100));
        Assert.Same(before.NativeFailureHistory, device.ControlStatus.NativeFailureHistory);
        Assert.Equal(before.TotalNativeFailures, device.ControlStatus.TotalNativeFailures);
        Assert.Equal(1, device.ControlStatus.WorkerTransportFailures);
    }

    [Fact]
    public void Worker_epochs_keep_global_unique_ordinals_and_original_context_and_a_new_parent_starts_its_own_scope()
    {
        var first = new HistorySession(32007);
        var second = new HistorySession(32008);
        var sessions = new Queue<IGpuRgbWorkerSession>([first, second]);
        using var device = new IsolatedGpuRgbDevice(() => sessions.Dequeue());
        device.Initialize();
        first.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var oldRecords = device.ControlStatus.NativeFailureHistory!.Records.ToArray();
        device.Initialize();
        Assert.True(first.HasExited);
        second.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(4, 5, 6, 100));
        var history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(new long[] { 1, 2, 3, 4 }, history.Records.Select(r => r.FailureOrdinal));
        Assert.Equal(oldRecords, history.Records.Take(2));
        Assert.All(history.Records.Take(2), r => Assert.Equal(32007, r.ProcessId));
        Assert.All(history.Records.Skip(2), r => Assert.Equal(32008, r.ProcessId));
        Assert.True(history.Records[2].Sequence > oldRecords[^1].Sequence);
        Assert.Equal(1, device.ControlStatus.WorkerRestartCount);
        AssertInvariant(history);
        using var restartedParent = new IsolatedGpuRgbDevice(() => new HistorySession(32009));
        restartedParent.Initialize();
        Assert.Equal(0, restartedParent.ControlStatus.NativeFailureHistory!.TotalFailures);
        Assert.Empty(restartedParent.ControlStatus.NativeFailureHistory.Records);
    }

    [Fact]
    public void Legacy_null_history_preserves_compatibility_and_legacy_unknowns_remain_explicit_on_upgrade()
    {
        var session = new HistorySession(32010) { Legacy = true };
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(1, 2, 3, 100));
        Assert.Null(device.ControlStatus.NativeFailureHistory);
        Assert.NotNull(device.ControlStatus.LastNativeFailure);
        session.Legacy = false;
        session.Device.FailuresNextColor = 1;
        Assert.False(device.SetColor(1, 2, 3, 100));
        Assert.Equal(0, device.ControlStatus.NativeFailureHistory!.MissingCount);
        Assert.Equal(3, device.ControlStatus.NativeFailureHistory.Records.Count);
        AssertInvariant(device.ControlStatus.NativeFailureHistory);
    }

    [Theory]
    [InlineData("register")]
    [InlineData("sequence")]
    [InlineData("ordinal")]
    public void A_capable_batch_cannot_rewrite_a_failure_already_delivered_with_legacy_ordinal_zero(string defect)
    {
        var session = new HistorySession(32015) { Legacy = true };
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var before = device.ControlStatus;
        Assert.Equal(0, before.LastNativeFailure!.FailureOrdinal);
        Assert.Null(before.NativeFailureHistory);
        session.Legacy = false;
        session.Device.FailuresNextColor = 1;
        session.Rewrite = reply => reply with
        {
            NativeFailureBatch = reply.NativeFailureBatch! with
            {
                Records = reply.NativeFailureBatch.Records.Select((r, i) => defect switch
                {
                    "register" when i == 1 => r with { Register = 0xFFFF },
                    "sequence" when i == 1 => r with { Sequence = before.LastNativeOperation!.Sequence },
                    "ordinal" when i == 0 => before.LastNativeFailure with { FailureOrdinal = 1 },
                    "ordinal" when i == 1 => r with { Sequence = before.LastNativeOperation!.Sequence },
                    _ => r
                }).ToArray()
            }
        };
        Assert.False(device.SetColor(4, 5, 6, 100));
        var after = device.ControlStatus;
        Assert.Equal(before.TotalNativeFailures, after.TotalNativeFailures);
        Assert.Equal(before.LastNativeFailure, after.LastNativeFailure);
        Assert.Equal(before.LastNativeOperation, after.LastNativeOperation);
        Assert.Null(after.NativeFailureHistory);
        Assert.Equal(1, after.WorkerTransportFailures);
    }

    [Fact]
    public void An_empty_budgeted_batch_still_receives_its_latest_context_from_the_compatibility_field()
    {
        var session = new HistorySession(32011) { OmitPrefix = 8 };
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 4;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(4, history.TotalFailures);
        Assert.Equal(3, history.MissingCount);
        Assert.Equal(device.ControlStatus.LastNativeFailure, Assert.Single(history.Records));
        AssertInvariant(history);
    }

    [Fact]
    public void A_previously_received_compatibility_context_can_leave_the_window_without_becoming_missing()
    {
        var session = new HistorySession(32012) { OmitBatch = true };
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(1, 2, 3, 100));
        Assert.Equal(2, Assert.Single(device.ControlStatus.NativeFailureHistory!.Records).FailureOrdinal);
        session.OmitBatch = false;
        session.Device.FailuresNextColor = 10;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(12, history.TotalFailures);
        Assert.Equal(1, history.EvictedCount); // Ordinal 2 was received before this window was supported.
        Assert.Equal(3, history.MissingCount); // Ordinals 1, 3 and 4 were never received.
        Assert.Equal(Enumerable.Range(5, 8).Select(i => (long)i), history.Records.Select(r => r.FailureOrdinal));
        AssertInvariant(history);
    }

    [Fact]
    public void The_latest_legacy_context_is_received_even_when_already_older_than_the_first_supported_window()
    {
        var session = new HistorySession(32016) { Legacy = true };
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        session.Device.FailuresNextColor = 2;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var legacy = device.ControlStatus.LastNativeFailure!;
        Assert.Equal(0, legacy.FailureOrdinal);
        Assert.Null(device.ControlStatus.NativeFailureHistory);
        session.Legacy = false;
        session.Device.FailuresNextColor = 10;
        Assert.False(device.SetColor(1, 2, 3, 100));
        var history = device.ControlStatus.NativeFailureHistory!;
        Assert.Equal(12, history.TotalFailures);
        Assert.Equal(3, history.MissingCount); // Ordinals 1, 3 and 4 were not received; ordinal 2 was.
        Assert.Equal(1, history.EvictedCount);
        Assert.Equal(Enumerable.Range(5, 8).Select(i => (long)i), history.Records.Select(r => r.FailureOrdinal));
        Assert.Equal(0, legacy.FailureOrdinal); // The prior immutable publication was never mutated.
        AssertInvariant(history);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void First_failed_initialization_without_a_batch_distinguishes_positive_ordinals_from_true_legacy(bool capable)
    {
        var session = new HistorySession(32014);
        session.Rewrite = reply =>
        {
            var fault = Failure(3) with { ProcessId = session.ProcessId, FailureOrdinal = capable ? 3 : 0 };
            var failed = reply with
            {
                Accepted = false, NativeFailureBatch = null,
                Status = reply.Status with
                {
                    ControlAvailable = false, TotalNativeFailures = 3, LastNativeOperation = fault, LastNativeFailure = fault
                }
            };
            // Same encoder omission path as a first reply whose base consumes the byte budget.
            int baseLength = GpuRgbWorkerFraming.EncodeReply(failed, int.MaxValue).Length;
            return JsonSerializer.Deserialize<GpuRgbWorkerReply>(GpuRgbWorkerFraming.EncodeReply(failed with
            {
                NativeFailureBatch = capable ? new() { TotalFailures = 3, TransportOmittedCount = 3 } : null
            }, baseLength), Json.Compact)!;
        };
        using var device = new IsolatedGpuRgbDevice(() => session);
        device.Initialize();
        Assert.Equal(0, device.ControlStatus.WorkerTransportFailures);
        Assert.Equal(3, device.ControlStatus.TotalNativeFailures);
        Assert.Equal(capable ? 3 : 0, device.ControlStatus.LastNativeFailure!.FailureOrdinal);
        if (capable)
        {
            var history = device.ControlStatus.NativeFailureHistory!;
            Assert.Equal(3, history.TotalFailures);
            Assert.Equal(2, history.MissingCount);
            Assert.Equal(0, history.EvictedCount);
            Assert.Equal(3, Assert.Single(history.Records).FailureOrdinal);
            AssertInvariant(history);
        }
        else Assert.Null(device.ControlStatus.NativeFailureHistory);
    }

    [Fact]
    public void Large_gaps_and_counter_saturation_never_wrap_and_do_not_fabricate_old_contexts()
    {
        var empty = new RgbNativeFailureHistory();
        var nearLimit = GpuRgbNativeFailureHistory.Merge(empty, 0, long.MaxValue - 1, null, 0, 0)!;
        Assert.Equal(long.MaxValue - 1, nearLimit.MissingCount);
        var saturated = GpuRgbNativeFailureHistory.Merge(nearLimit, long.MaxValue - 1, long.MaxValue, null, 0, 0)!;
        Assert.Equal(long.MaxValue, saturated.TotalFailures);
        Assert.Equal(long.MaxValue, saturated.MissingCount);
        Assert.Empty(saturated.Records);
        AssertInvariant(saturated);
        var mapped = GpuRgbNativeFailureHistory.Map(Failure(10), long.MaxValue - 1, long.MaxValue - 1);
        Assert.Equal(long.MaxValue, mapped.Sequence);
        Assert.Equal(long.MaxValue, mapped.FailureOrdinal);
    }

    private static void AssertInvariant(RgbNativeFailureHistory history)
    {
        Assert.Equal(8, history.Capacity);
        Assert.InRange(history.Records.Count, 0, 8);
        Assert.Equal(history.TotalFailures - history.EvictedCount - history.MissingCount, history.Records.Count);
        Assert.True(history.EvictedCount >= 0 && history.MissingCount >= 0);
    }

    private static RgbNativeOperationResult Failure(long ordinal) => new()
    {
        Sequence = ordinal, FailureOrdinal = ordinal, AtUtc = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero).AddTicks(ordinal),
        Operation = RgbNativeOperation.Write, Status = -1, Register = 0x8100, Command = 0,
        ProcessId = 32000, ThreadId = 1, GpuHandle = "0000000000000100", TransferDurationMicroseconds = 1
    };
    private static RgbControlStatus Status(RgbNativeOperationResult[] records, long total) => new()
    {
        TotalNativeFailures = total, LastNativeFailure = records.LastOrDefault(), LastNativeOperation = records.LastOrDefault()
    };
    private static GpuRgbWorkerReply Reply(RgbNativeOperationResult[] records, long total) => new()
    {
        Generation = 1, RequestId = 1, Accepted = false, Status = Status(records, total),
        NativeFailureBatch = new() { TotalFailures = total, RingEvictedCount = total - records.Length, Records = records }
    };
    private static Task<GpuRgbWorkerReply?> Read(Stream input) =>
        GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerReply>(input, 16384, CancellationToken.None);
    private static MemoryStream RawFrame(byte[] bytes)
    {
        var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        stream.Write(header); stream.Write(bytes); stream.Position = 0;
        return stream;
    }
    private static async Task<(int ExitCode, byte[] Bytes)> Host(HistoryDevice device, int colors)
    {
        using var input = new FragmentedStream();
        using var output = new FragmentedStream();
        for (int id = 1; id <= colors + 1; id++)
            await GpuRgbWorkerFraming.WriteAsync(input, new GpuRgbWorkerRequest
            {
                Generation = 1, RequestId = id, Brightness = 100,
                Operation = id == 1 ? GpuRgbWorkerProtocol.Initialize : GpuRgbWorkerProtocol.Color
            }, 1024, CancellationToken.None);
        input.Position = 0;
        int exit = await GpuRgbWorkerHost.RunTrustedAsync(input, output, () => device, CancellationToken.None);
        return (exit, output.ToArray());
    }
    private sealed class FragmentedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            for (int offset = 0; offset < buffer.Length; offset += 3)
                await base.WriteAsync(buffer.Slice(offset, Math.Min(3, buffer.Length - offset)), cancellationToken);
        }
    }

    private sealed class HistoryDevice(int processId = 32000) : IRgbDevice, IRgbControlStatusProvider
    {
        private long _sequence, _failures, _outputs, _consecutive;
        private int _colors;
        private readonly List<RgbNativeOperationResult> _recent = [];
        internal int FailuresNextColor, FailOnColor, Disposals;
        public string Name => "Managed fake RGB";
        public bool IsAvailable => ControlStatus.ControlAvailable;
        public RgbControlStatus ControlStatus { get; private set; } = new();
        public void Initialize() => ControlStatus = new()
        {
            ControllerName = Name, ControllerLedCount = 4, ControlAvailable = true, InitializationAttempts = 1,
            AdapterObservation = RgbAdapterObservation.Present,
            LastNativeOperation = Failure(++_sequence) with { FailureOrdinal = 0, Status = 0, ProcessId = processId },
            NativeFailureHistory = new()
        };
        public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
        {
            _colors++;
            int count = FailOnColor == 0 || FailOnColor == _colors ? FailuresNextColor : 0;
            if (count > 0) FailuresNextColor = 0;
            for (int i = 0; i < count; i++)
            {
                var failure = Failure(++_sequence) with { FailureOrdinal = ++_failures, ProcessId = processId };
                _recent.Add(failure);
                if (_recent.Count > 8) _recent.RemoveAt(0);
            }
            var last = Failure(++_sequence) with { FailureOrdinal = 0, Status = 0, ProcessId = processId };
            bool accepted = count == 0;
            if (!accepted) { _outputs++; _consecutive++; } else _consecutive = 0;
            ControlStatus = ControlStatus with
            {
                LastNativeOperation = last, LastNativeFailure = _recent.LastOrDefault(), TotalNativeFailures = _failures,
                TotalOutputFailures = _outputs, ConsecutiveOutputFailures = _consecutive,
                LastOutputAccepted = accepted, LastOutputAttemptUtc = last.AtUtc,
                LastOutputAcceptedUtc = accepted ? last.AtUtc : ControlStatus.LastOutputAcceptedUtc,
                LastOutputAcceptedNativeSequence = accepted ? last.Sequence : ControlStatus.LastOutputAcceptedNativeSequence,
                NativeFailureHistory = count == 0 ? ControlStatus.NativeFailureHistory : new()
                { TotalFailures = _failures, EvictedCount = _failures - _recent.Count, Records = _recent }
            };
            return accepted;
        }
        public void Dispose() => Disposals++;
    }

    private sealed class HistorySession(int processId) : IGpuRgbWorkerSession
    {
        internal HistoryDevice Device { get; } = new(processId);
        internal bool OmitBatch, RepeatBatch, Legacy;
        internal int OmitPrefix;
        internal Func<GpuRgbWorkerReply, GpuRgbWorkerReply>? Rewrite;
        private long? _sent;
        public int ProcessId => processId;
        public bool HasExited { get; private set; }
        public GpuRgbWorkerReply Exchange(GpuRgbWorkerRequest request, TimeSpan timeout)
        {
            bool accepted;
            if (request.Operation == GpuRgbWorkerProtocol.Initialize) { Device.Initialize(); accepted = Device.IsAvailable; }
            else accepted = Device.SetColor(request.R, request.G, request.B, request.Brightness);
            var raw = Device.ControlStatus;
            var history = raw.NativeFailureHistory!;
            var batch = !Legacy && !OmitBatch && (_sent != history.TotalFailures || RepeatBatch) ? new GpuRgbNativeFailureBatch
            {
                TotalFailures = history.TotalFailures, RingEvictedCount = history.EvictedCount,
                TransportOmittedCount = Math.Min(OmitPrefix, history.Records.Count), Records = history.Records.Skip(OmitPrefix).ToArray()
            } : null;
            _sent = history.TotalFailures;
            if (Legacy) raw = raw with
            {
                LastNativeOperation = raw.LastNativeOperation is { } operation ? operation with { FailureOrdinal = 0 } : null,
                LastNativeFailure = raw.LastNativeFailure is { } failure ? failure with { FailureOrdinal = 0 } : null
            };
            var reply = new GpuRgbWorkerReply
            {
                Generation = request.Generation, RequestId = request.RequestId, Accepted = accepted,
                Status = raw with { NativeFailureHistory = null }, NativeFailureBatch = batch
            };
            return Rewrite?.Invoke(reply) ?? reply;
        }
        public void Dispose() { HasExited = true; Device.Dispose(); }
    }
}
