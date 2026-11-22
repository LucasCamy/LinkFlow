using System.Globalization;

namespace LinkFlow.Tests;

/// <summary>Pure in-memory observer tests: no Engine, SDK, process-resource sampling, filesystem or IPC.</summary>
public sealed class RgbNativeFailureJournalTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 13, 0, 0, TimeSpan.Zero);
    private static OperationalHistory History() => new(new() { DirectoryPath = null, TimeProvider = new Clock() });
    private static RgbNativeOperationResult Failure(long ordinal) => new()
    {
        FailureOrdinal = ordinal, Sequence = ordinal, AtUtc = Start,
        Operation = RgbNativeOperation.Write, Status = -1, Register = 0x8020, Command = 0,
        ProcessId = 4242, ThreadId = 7, GpuHandle = "0000000000000100",
        TransferDurationMicroseconds = 123.5, LibraryVersion = "32.0.16.2124"
    };

    private static RgbControlStatus Window(long total, long missing, params RgbNativeOperationResult[] records) => new()
    {
        ControlAvailable = true, AdapterObservation = RgbAdapterObservation.Present,
        LastOutputAccepted = total == 0, TotalNativeFailures = total, TotalOutputFailures = total == 0 ? 0 : 1,
        LastNativeFailure = records.MaxBy(f => f.FailureOrdinal), WorkerGeneration = 1, WorkerProcessId = 4242,
        NativeFailureHistory = new()
        {
            TotalFailures = total, MissingCount = missing, EvictedCount = total - missing - records.Length, Records = records
        }
    };

    private static void Observe(OperationalHistory history, int seconds, RgbControlStatus status) =>
        history.ObserveGpuRgbControl(new() { GpuRgbControl = status }, Start.AddSeconds(seconds));

    private static OperationalEvent[] Events(OperationalHistory history, string kind) => history.QueryEvents(new()
    {
        DeviceId = "rgb.gpu", FromUtc = Start.AddDays(-2), ToUtc = Start.AddHours(1), MaxItems = 1000
    }).Where(e => e.Kind == kind).OrderBy(e => e.TimestampUtc)
        .ThenBy(e => e.Data.TryGetValue("nativeFailureOrdinal", out string? value) ? long.Parse(value, CultureInfo.InvariantCulture) : 0)
        .ToArray();

    private static long[] Ordinals(OperationalHistory history) => Events(history, "rgb.native.failure")
        .Select(e => long.Parse(e.Data["nativeFailureOrdinal"], CultureInfo.InvariantCulture)).ToArray();

    [Fact]
    public async Task One_output_with_three_native_failures_preserves_all_contexts_with_one_episode_warning()
    {
        await using var history = History();
        Observe(history, 0, Window(0, 0));
        var one = Failure(1) with { Operation = RgbNativeOperation.Read, Register = 0x8020 };
        var two = Failure(2) with { Operation = RgbNativeOperation.Read, Register = 0x8021, Status = -6 };
        var three = Failure(3) with { Register = 0x8115 };
        Observe(history, 1, Window(3, 0, one, two, three));

        Assert.Equal([1L, 2L, 3L], Ordinals(history));
        var details = Events(history, "rgb.native.failure");
        Assert.All(details, e => Assert.Equal(AlertSeverity.Info, e.Severity));
        Assert.Equal(["0x8020", "0x8021", "0x8115"], details.Select(e => e.Data["nativeRegister"]));
        Assert.Equal(["-1", "-6", "-1"], details.Select(e => e.Data["nativeStatus"]));
        Assert.Single(Events(history, "rgb.transport.failed"));
        Assert.Single(Events(history, "rgb.output.rejected"));
        var change = Events(history, "rgb.native.history")[^1];
        Assert.Equal("3", change.Data["nativeRecordEventsOffered"]);
        Assert.Equal("3", change.Data["totalNativeFailures"]);
        Assert.Equal("0", change.Data["nativeHistoryMissing"]);
    }

    [Fact]
    public async Task Healthy_reack_and_retained_window_do_not_replay_details_or_history_changes()
    {
        await using var history = History();
        var failed = Window(3, 0, Failure(1), Failure(2), Failure(3));
        Observe(history, 1, failed);
        for (int i = 2; i <= 200; i++)
            Observe(history, i, failed with { LastOutputAccepted = true, LastOutputAcceptedNativeSequence = 4,
                LastOutputAttemptUtc = Start.AddSeconds(i), LastOutputAcceptedUtc = Start.AddSeconds(i) });

        Assert.Equal([1L, 2L, 3L], Ordinals(history));
        Assert.Single(Events(history, "rgb.native.history"));
        Assert.Single(Events(history, "rgb.transport.failed"));
    }

    [Fact]
    public async Task Overlapping_windows_offer_only_new_ordinals()
    {
        await using var history = History();
        Observe(history, 1, Window(8, 0, Enumerable.Range(1, 8).Select(i => Failure(i)).ToArray()));
        Observe(history, 2, Window(12, 0, Enumerable.Range(5, 8).Select(i => Failure(i)).ToArray()));
        Assert.Equal(Enumerable.Range(1, 12).Select(i => (long)i), Ordinals(history));
        Assert.Equal("4", Events(history, "rgb.native.history")[^1].Data["nativeRecordEventsOffered"]);
    }

    [Fact]
    public async Task Late_lower_ordinals_are_recorded_and_missing_is_a_decreasing_gauge()
    {
        await using var history = History();
        Observe(history, 1, Window(3, 2, Failure(3)));
        Observe(history, 2, Window(3, 1, Failure(2), Failure(3)));
        Observe(history, 3, Window(3, 0, Failure(1), Failure(2), Failure(3)));
        Observe(history, 4, Window(3, 0, Failure(1), Failure(2), Failure(3)));

        Assert.Equal([3L, 2L, 1L], Ordinals(history));
        var changes = Events(history, "rgb.native.history");
        Assert.Equal(3, changes.Length);
        Assert.Equal("true", changes[0].Data["firstObservation"]);
        Assert.False(changes[0].Data.ContainsKey("missingChange")); // The first gauge is a baseline.
        Assert.All(changes.Skip(1), e => Assert.Equal("-1", e.Data["missingChange"]));
        Assert.Equal("0", changes[^1].Data["nativeHistoryMissing"]);
        Assert.All(changes, e => Assert.Contains("not-persistence-confirmation", e.Data["journalDelivery"]));
    }

    [Fact]
    public async Task Evictions_are_not_reported_as_missing_or_automatic_journal_loss()
    {
        await using var history = History();
        Observe(history, 1, Window(8, 0, Enumerable.Range(1, 8).Select(i => Failure(i)).ToArray()));
        Observe(history, 2, Window(16, 0, Enumerable.Range(9, 8).Select(i => Failure(i)).ToArray()));
        var change = Events(history, "rgb.native.history")[^1];
        Assert.Equal("8", change.Data["evictedChange"]);
        Assert.Equal("0", change.Data["missingChange"]);
        Assert.Equal("0", change.Data["nativeHistoryMissing"]);
        Assert.Contains("not-automatic-loss", change.Data["evictionMeaning"]);
        Assert.Equal(AlertSeverity.Info, change.Severity);
        Assert.Equal(16, Ordinals(history).Length);
    }

    [Fact]
    public async Task Context_received_by_parent_but_already_outside_window_is_not_invented_as_journaled()
    {
        await using var history = History();
        var retained = Enumerable.Range(3, 8).Select(i => Failure(i)).ToArray();
        Observe(history, 1, Window(10, 2, retained));
        Observe(history, 2, Window(10, 0, retained)); // Missing1/2 arrived in the parent, then immediately left its recent window.
        Assert.Equal(Enumerable.Range(3, 8).Select(i => (long)i), Ordinals(history));
        var change = Events(history, "rgb.native.history")[^1];
        Assert.Equal("-2", change.Data["missingChange"]);
        Assert.Equal("2", change.Data["evictedChange"]);
        Assert.Equal("0", change.Data["nativeRecordEventsOffered"]);
        Assert.Contains("not-persistence-confirmation", change.Data["journalDelivery"]);
    }

    [Fact]
    public async Task Overload_exposes_only_eight_received_contexts_and_preserves_missing_gauge()
    {
        await using var history = History();
        Observe(history, 1, Window(20, 12, Enumerable.Range(13, 8).Select(i => Failure(i)).ToArray()));
        Assert.Equal(Enumerable.Range(13, 8).Select(i => (long)i), Ordinals(history));
        var change = Assert.Single(Events(history, "rgb.native.history"));
        Assert.Equal("8", change.Data["nativeRecordEventsOffered"]);
        Assert.Equal("12", change.Data["nativeHistoryMissing"]);
        Assert.Equal("0", change.Data["nativeHistoryEvicted"]);
    }

    [Fact]
    public async Task Metadata_budget_keeps_ordinal_and_all_native_fields_ahead_of_extra_status()
    {
        await using var history = History();
        var fault = Failure(7) with { Sequence = 22, Register = 0x8115, Command = 3, ThreadId = 67 };
        Observe(history, 1, Window(7, 6, fault) with
        {
            Source = new string('s', 8000), ControllerName = new string('c', 8000),
            DiagnosticText = EngineText.Of(EngineTexts.GpuNvapiAccess, new string('d', 8000)),
            WorkerRestartCount = 15, WorkerTransportFailures = 8, LastWorkerRestartReason = new string('r', 8000),
            LastWorkerFailure = new string('w', 8000), LastWorkerFailureUtc = Start,
            LastOutputAttemptUtc = Start, LastOutputAcceptedUtc = Start.AddSeconds(-1), LastOutputAcceptedNativeSequence = 20
        });
        var entry = Assert.Single(Events(history, "rgb.native.failure"));
        Assert.InRange(entry.Data.Count, 1, 24);
        Assert.Equal("7", entry.Data["nativeFailureOrdinal"]);
        Assert.Equal("22", entry.Data["nativeFailureSequence"]);
        Assert.Equal("Write", entry.Data["nativeOperation"]);
        Assert.Equal("-1", entry.Data["nativeStatus"]);
        Assert.Equal("0x8115", entry.Data["nativeRegister"]);
        Assert.Equal("0x03", entry.Data["nativeCommand"]);
        Assert.Equal("4242", entry.Data["nativeProcessId"]);
        Assert.Equal("67", entry.Data["nativeThreadId"]);
        Assert.Equal("managed", entry.Data["nativeThreadIdKind"]);
        Assert.Equal("0000000000000100", entry.Data["nativeGpuHandle"]);
        Assert.Equal("123.5", entry.Data["nativeTransferDurationMicroseconds"]);
        Assert.Equal("backend-call-including-wrapper", entry.Data["nativeTransferTimingScope"]);
        Assert.Equal("32.0.16.2124", entry.Data["nativeLibraryVersion"]);
        Assert.Equal(Start.ToString("O", CultureInfo.InvariantCulture), entry.Data["nativeFailureUtc"]);
        Assert.Equal(Start.AddSeconds(1).ToString("O", CultureInfo.InvariantCulture), entry.Data["observedAtUtc"]);
        Assert.Equal("unverified", entry.Data["physicalOutput"]);
        Assert.True(int.Parse(entry.Data["omittedDataFields"], CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task Legacy_context_migrating_to_an_ordinal_is_not_replayed()
    {
        await using var history = History();
        var legacy = Failure(0) with { Sequence = 77 };
        Observe(history, 1, Window(0, 0) with { NativeFailureHistory = null, LastOutputAccepted = false, LastNativeFailure = legacy });
        var supported = Window(1, 0, legacy with { FailureOrdinal = 1 });
        Observe(history, 2, supported);
        Observe(history, 3, supported);
        Assert.Empty(Ordinals(history));
        Assert.Single(Events(history, "rgb.transport.failed"));
        Assert.Equal("1", Assert.Single(Events(history, "rgb.native.history")).Data["legacyContextEventsAlreadyOffered"]);
    }

    [Fact]
    public async Task Legacy_coalesced_latest_observation_is_not_falsely_treated_as_a_recorded_context()
    {
        await using var history = History();
        var first = Failure(0) with { Sequence = 10 };
        var coalesced = first with { Sequence = 11 };
        Observe(history, 1, Window(0, 0) with { NativeFailureHistory = null, LastOutputAccepted = false, LastNativeFailure = first });
        Observe(history, 2, Window(0, 0) with { NativeFailureHistory = null, LastOutputAccepted = false, LastNativeFailure = coalesced });
        Observe(history, 3, Window(2, 0, first with { FailureOrdinal = 1 }, coalesced with { FailureOrdinal = 2 }));
        Assert.Equal([2L], Ordinals(history));
        var legacy = Assert.Single(Events(history, "rgb.transport.failed"));
        Assert.Equal("10", legacy.Data["nativeFailureSequence"]);
    }

    [Fact]
    public async Task Several_legacy_signature_events_are_all_recognized_during_migration()
    {
        await using var history = History();
        var first = Failure(0) with { Sequence = 10, Register = 0x8020 };
        var second = first with { Sequence = 11, Register = 0x8121 };
        Observe(history, 1, Window(0, 0) with { NativeFailureHistory = null, LastOutputAccepted = false, LastNativeFailure = first });
        Observe(history, 2, Window(0, 0) with { NativeFailureHistory = null, LastOutputAccepted = false, LastNativeFailure = second });
        Observe(history, 3, Window(2, 0, first with { FailureOrdinal = 1 }, second with { FailureOrdinal = 2 }));
        Assert.Equal(2, Events(history, "rgb.transport.failed").Length);
        Assert.Empty(Ordinals(history));
        Assert.Equal("2", Assert.Single(Events(history, "rgb.native.history")).Data["legacyContextEventsAlreadyOffered"]);
    }

    [Fact]
    public async Task Legacy_event_context_rotation_covers_the_eight_records_that_can_migrate()
    {
        await using var history = History();
        RgbNativeOperationResult Legacy(int sequence) => Failure(0) with { Sequence = sequence, Register = (ushort)(0x8020 + sequence) };
        for (int sequence = 1; sequence <= 12; sequence++)
            Observe(history, sequence, Window(0, 0) with { NativeFailureHistory = null, LastOutputAccepted = false,
                LastNativeFailure = Legacy(sequence) });
        var recent = Enumerable.Range(5, 8).Select(i => Legacy(i) with { FailureOrdinal = i }).ToArray();
        Observe(history, 13, Window(12, 0, recent));
        Assert.Equal(12, Events(history, "rgb.transport.failed").Length);
        Assert.Empty(Ordinals(history));
        Assert.Equal("8", Assert.Single(Events(history, "rgb.native.history")).Data["legacyContextEventsAlreadyOffered"]);
        Observe(history, 14, Window(13, 0, Enumerable.Range(6, 8).Select(i => Legacy(i) with { FailureOrdinal = i }).ToArray()));
        Assert.Equal([13L], Ordinals(history));
    }

    [Fact]
    public async Task Providers_without_history_keep_legacy_episode_behavior()
    {
        await using var history = History();
        for (int i = 1; i <= 60; i++)
            Observe(history, i, Window(0, 0) with { NativeFailureHistory = null, LastOutputAccepted = false,
                LastNativeFailure = Failure(0) with { Sequence = i } });
        Assert.Single(Events(history, "rgb.transport.failed"));
        Assert.Empty(Events(history, "rgb.native.failure"));
        Assert.Empty(Events(history, "rgb.native.history"));
    }

    [Fact]
    public async Task Temporary_legacy_snapshot_does_not_clear_the_supported_dedup_window()
    {
        await using var history = History();
        var supported = Window(1, 0, Failure(1));
        Observe(history, 1, supported);
        Observe(history, 2, supported with { NativeFailureHistory = null });
        Observe(history, 3, supported);
        Assert.Equal([1L], Ordinals(history));
        Assert.Single(Events(history, "rgb.native.history"));
    }

    [Fact]
    public async Task Initializing_status_can_preserve_context_without_publishing_a_completed_outage()
    {
        await using var history = History();
        Observe(history, 0, Window(0, 0));
        var status = Window(1, 0, Failure(1));
        Observe(history, 1, status with { Initializing = true, ControlAvailable = false });
        Assert.Equal([1L], Ordinals(history));
        Assert.Empty(Events(history, "rgb.control.unavailable"));
        Assert.Empty(Events(history, "rgb.output.rejected"));
        Assert.Empty(Events(history, "rgb.transport.failed"));
        Observe(history, 2, status);
        Assert.Equal([1L], Ordinals(history));
        Assert.Single(Events(history, "rgb.transport.failed"));
    }

    [Fact]
    public async Task Late_native_utc_is_metadata_while_temporal_queries_use_journal_observation_time()
    {
        await using var history = History();
        var fault = Failure(1) with { AtUtc = Start.AddDays(-1) };
        Observe(history, 20, Window(1, 0, fault));
        var entry = Assert.Single(history.QueryEvents(new() { Kind = "rgb.native.failure", DeviceId = "rgb.gpu",
            FromUtc = Start.AddSeconds(19), ToUtc = Start.AddSeconds(21), MaxItems = 10 }));
        Assert.Equal(Start.AddSeconds(20), entry.TimestampUtc);
        Assert.Equal(fault.AtUtc.ToString("O", CultureInfo.InvariantCulture), entry.Data["nativeFailureUtc"]);
        Assert.Equal(Start.AddSeconds(20).ToString("O", CultureInfo.InvariantCulture), entry.Data["observedAtUtc"]);
        Assert.Empty(history.QueryEvents(new() { Kind = "rgb.native.failure", DeviceId = "rgb.gpu",
            FromUtc = fault.AtUtc.AddSeconds(-1), ToUtc = fault.AtUtc.AddSeconds(1), MaxItems = 10 }));
        Observe(history, 25, Window(1, 0, fault));
        Assert.Single(Events(history, "rgb.native.failure"));
    }

    [Fact]
    public async Task Many_rotating_windows_remain_duplicate_free_and_ignore_delayed_older_window()
    {
        await using var history = History();
        for (int total = 1; total <= 100; total++)
        {
            var records = Enumerable.Range(Math.Max(1, total - 7), Math.Min(8, total)).Select(i => Failure(i)).ToArray();
            Observe(history, total, Window(total, 0, records));
        }
        Observe(history, 101, Window(8, 0, Enumerable.Range(1, 8).Select(i => Failure(i)).ToArray()));
        var last = Window(100, 0, Enumerable.Range(93, 8).Select(i => Failure(i)).ToArray());
        for (int second = 102; second <= 150; second++) Observe(history, second, last);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), Ordinals(history));
    }

    [Fact]
    public async Task Long_counter_gauges_and_negative_backfill_change_do_not_overflow()
    {
        await using var history = History();
        Observe(history, 1, Window(long.MaxValue, long.MaxValue - 1, Failure(long.MaxValue)));
        Observe(history, 2, Window(long.MaxValue, long.MaxValue - 2, Failure(long.MaxValue - 1), Failure(long.MaxValue)));
        Assert.Equal([long.MaxValue, long.MaxValue - 1], Ordinals(history));
        var change = Events(history, "rgb.native.history")[^1];
        Assert.Equal(long.MaxValue.ToString(CultureInfo.InvariantCulture), change.Data["totalNativeFailures"]);
        Assert.Equal("-1", change.Data["missingChange"]);
        Assert.Equal("0", change.Data["nativeFailureTotalChange"]);
    }

    [Fact]
    public async Task Concurrent_same_window_is_deduplicated_before_event_enqueue()
    {
        await using var history = History();
        var status = Window(8, 0, Enumerable.Range(1, 8).Select(i => Failure(i)).ToArray());
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => Observe(history, 1, status))));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), Ordinals(history));
        Assert.Single(Events(history, "rgb.native.history"));
        Assert.Single(Events(history, "rgb.transport.failed"));
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Start;
    }
}
