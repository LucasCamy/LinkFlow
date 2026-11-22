using System.IO;
using System.Text.Json;
using LinkFlow.Core;

namespace LinkFlow.Tests;

public sealed class OperationalHistoryEventQueryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LinkFlow-event-query-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
    private OperationalHistoryOptions Options(int maxEvents = 2000) => new()
    { DirectoryPath = _directory, MaxMemoryEvents = maxEvents, TimeProvider = new FixedClock(_now) };
    private OperationalEvent Event(string id, int seconds, string kind = "test", string source = "fixture", string? device = null, string detail = "original") => new()
    {
        Id = id, SessionId = "fixture-session", TimestampUtc = _now.AddSeconds(seconds), Kind = kind, Source = source,
        DeviceId = device, Message = "fixture event", Data = new() { ["detail"] = detail }
    };
    private EventQuery Query(int from = 1, int to = 100, int max = 100) => new()
    { FromUtc = _now.AddSeconds(from), ToUtc = _now.AddSeconds(to), Kind = "test", MaxItems = max };
    private string WriteClosed(params OperationalEvent[] events)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"lfh-{_now:yyyyMMdd}-{Guid.NewGuid():N}-000001.jsonl");
        File.WriteAllText(path, string.Concat(events.Select(entry => JsonSerializer.Serialize(new { type = "event", @event = entry }, Json.Compact) + "\n")));
        return path;
    }
    private static string Payload(IReadOnlyList<OperationalEvent> events) => JsonSerializer.Serialize(events, Json.Compact);
    private static void MemoryHit(OperationalHistory history)
    {
        var execution = history.Status.EventQueries.LastExecution;
        Assert.True(execution.MemoryCacheUsed);
        Assert.Equal(0, execution.DiskScans);
        Assert.Equal(0, execution.FilesRead);
        Assert.Equal(0, execution.BytesRead);
        Assert.Equal(0, execution.RecordsScanned);
    }
    private static void DiskRead(OperationalHistory history)
    {
        var execution = history.Status.EventQueries.LastExecution;
        Assert.False(execution.MemoryCacheUsed);
        Assert.Equal(1, execution.DiskScans);
        Assert.True(execution.FilesRead > 0);
        Assert.True(execution.BytesRead > 0);
        Assert.True(execution.RecordsScanned > 0);
    }

    [Fact]
    public async Task A_verified_recent_query_reads_no_payload_and_returns_independent_events()
    {
        await using var history = new OperationalHistory(Options());
        var incoming = Event("one", 1);
        history.RecordEvent(incoming);
        incoming.Data["detail"] = "caller changed";
        history.RecordEvent(Event("two", 2));
        await history.FlushAsync();

        var result = history.QueryEvents(Query());
        Assert.Equal(new[] { "two", "one" }, result.Select(e => e.Id));
        Assert.All(result, entry => Assert.Equal("original", entry.Data["detail"]));
        MemoryHit(history);
        result[0].Data["detail"] = "client changed";
        Assert.Equal("original", history.QueryEvents(Query())[0].Data["detail"]);
        Assert.Equal(2, history.Status.EventQueries.MemoryCacheHits);
        Assert.Equal(0, history.Status.Queries.Queries);
    }

    [Fact]
    public async Task Cold_restart_proves_events_from_previous_sessions_before_using_memory()
    {
        await using (var first = new OperationalHistory(Options()))
        {
            first.RecordEvent(Event("prior", 1));
            await first.FlushAsync();
        }
        await using var restarted = new OperationalHistory(Options());
        await restarted.FlushAsync();
        Assert.Equal("prior", Assert.Single(restarted.QueryEvents(Query())).Id);
        MemoryHit(restarted);
    }

    [Fact]
    public async Task Event_eviction_forces_disk_for_old_windows_and_allows_a_complete_recent_tail()
    {
        await using var history = new OperationalHistory(Options(4));
        for (int i = 1; i <= 10; i++) history.RecordEvent(Event("e" + i, i));
        await history.FlushAsync();

        Assert.Equal(10, history.QueryEvents(Query(1, 10)).Count);
        DiskRead(history);
        Assert.Equal(5, history.QueryEvents(Query(6, 10)).Count); // From equals the eviction fence: include that event from disk.
        DiskRead(history);
        Assert.Equal(new[] { "e10", "e9", "e8", "e7" }, history.QueryEvents(Query(7, 10)).Select(e => e.Id));
        MemoryHit(history);
        Assert.Equal(2, history.Status.EventQueries.DiskQueries);
    }

    [Theory]
    [InlineData(90, 80, 100)]
    [InlineData(86400, 86399, 86401)]
    public async Task Eviction_fence_covers_out_of_order_and_future_events(int lostAt, int from, int to)
    {
        await using var history = new OperationalHistory(Options(4));
        history.RecordEvent(Event("evicted-future", lostAt));
        for (int i = 10; i <= 13; i++) history.RecordEvent(Event("later-arrival-" + i, i));
        await history.FlushAsync();
        Assert.Equal("evicted-future", Assert.Single(history.QueryEvents(Query(from, to))).Id);
        DiskRead(history);
        Assert.Equal("evicted-events", history.Status.EventQueries.LastExecution.CacheDecision);
    }

    [Fact]
    public async Task Clock_regression_does_not_make_earlier_evicted_events_disappear()
    {
        await using var history = new OperationalHistory(Options(4));
        history.RecordEvent(Event("before-clock-regressed", 90));
        for (int i = 1; i <= 10; i++) history.RecordEvent(Event("after-regression-" + i, i));
        await history.FlushAsync();
        Assert.Equal(10, history.QueryEvents(Query(1, 10)).Count);
        DiskRead(history);
    }

    [Fact]
    public async Task Event_cache_is_independent_of_frame_eviction_and_acquisition_cadence()
    {
        var options = Options(); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        history.RecordEvent(Event("retained", 1));
        for (int i = 2; i < 20; i++) history.RecordFrame(new TelemetryFrame
        {
            TimestampUtc = _now.AddSeconds(i),
            Values = [new("engine.memory.private", "B", 123, _now.AddSeconds(i), ExpectedIntervalSeconds: 30)]
        });
        await history.FlushAsync();
        Assert.Equal("retained", Assert.Single(history.QueryEvents(Query(1, 2))).Id);
        MemoryHit(history);
    }

    [Fact]
    public async Task Cache_and_disk_preserve_memory_precedence_filters_clones_and_tie_boundary()
    {
        WriteClosed(Event("duplicate", 2, detail: "disk"));
        await using var history = new OperationalHistory(Options());
        history.RecordEvent(Event("duplicate", 2, detail: "memory"));
        history.RecordEvent(Event("b", 3, source: "wanted", device: "fan"));
        history.RecordEvent(Event("a", 3, source: "wanted", device: "fan"));
        history.RecordEvent(Event("c", 3, source: "wanted", device: "fan"));
        history.RecordEvent(Event("excluded-source", 3, source: "other", device: "fan"));
        history.RecordEvent(Event("excluded-device", 3, source: "wanted", device: "other"));
        history.RecordEvent(Event("excluded-kind", 3, kind: "other", source: "wanted", device: "fan"));
        await history.FlushAsync();
        var query = Query(max: 2); query.Source = "wanted"; query.DeviceId = "fan";
        string cached = Payload(history.QueryEvents(query));
        Assert.Equal(new[] { "a", "b" }, history.QueryEvents(query).Select(e => e.Id));
        MemoryHit(history);
        Assert.Equal("memory", Assert.Single(history.QueryEvents(Query(2, 2))).Data["detail"]);

        // A new, out-of-window segment invalidates only the optimization, forcing
        // the ordinary merge path without changing the requested evidence.
        WriteClosed(Event("external-outside-window", -1000));
        Assert.Equal(cached, Payload(history.QueryEvents(query)));
        DiskRead(history);
        Assert.Equal("memory", Assert.Single(history.QueryEvents(Query(2, 2))).Data["detail"]);
    }

    [Fact]
    public async Task Bounds_are_inclusive_and_offset_timestamps_compare_in_utc()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordEvent(Event("start", 1) with { TimestampUtc = _now.AddSeconds(1).ToOffset(TimeSpan.FromHours(5)) });
        history.RecordEvent(Event("end", 2));
        history.RecordEvent(Event("after", 3));
        await history.FlushAsync();
        var query = Query(1, 2); query.FromUtc = query.FromUtc.ToOffset(TimeSpan.FromHours(-4));
        Assert.Equal(new[] { "end", "start" }, history.QueryEvents(query).Select(e => e.Id));
        MemoryHit(history);
        query.MaxItems = 0;
        Assert.Equal("end", Assert.Single(history.QueryEvents(query)).Id);
    }

    [Fact]
    public async Task External_append_invalidates_the_proof_and_preserves_new_events()
    {
        string path = WriteClosed(Event("initial", 1));
        await using var history = new OperationalHistory(Options());
        await history.FlushAsync();
        Assert.Single(history.QueryEvents(Query())); MemoryHit(history);
        File.AppendAllText(path, JsonSerializer.Serialize(new { type = "event", @event = Event("external", 2) }, Json.Compact) + "\n");
        Assert.Equal(new[] { "external", "initial" }, history.QueryEvents(Query()).Select(e => e.Id));
        DiskRead(history);
        Assert.True(history.Status.EventQueries.CacheInvalidations > 0);
    }

    [Fact]
    public async Task Equal_length_rewrite_with_restored_last_write_time_is_detected_by_handle_change_time()
    {
        string path = WriteClosed(Event("external", 1, detail: "aaaa"));
        await using var history = new OperationalHistory(Options());
        await history.FlushAsync();
        Assert.Single(history.QueryEvents(Query())); MemoryHit(history);
        long length = new FileInfo(path).Length;
        DateTime modified = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, JsonSerializer.Serialize(new { type = "event", @event = Event("new-id!!", 2, detail: "bbbb") }, Json.Compact) + "\n");
        Assert.Equal(length, new FileInfo(path).Length);
        File.SetLastWriteTimeUtc(path, modified);

        // The old ID remains an in-memory event by the existing merge contract.
        Assert.Contains(history.QueryEvents(Query()), entry => entry.Id == "new-id!!" && entry.Data["detail"] == "bbbb");
        DiskRead(history);
        Assert.True(history.Status.EventQueries.CacheInvalidations > 0);
    }

    [Fact]
    public async Task External_removal_invalidates_cache_without_discarding_the_memory_tail()
    {
        string path = WriteClosed(Event("retained-in-memory", 1));
        await using var history = new OperationalHistory(Options());
        await history.FlushAsync();
        Assert.Single(history.QueryEvents(Query())); MemoryHit(history);
        File.Delete(path);
        Assert.Equal("retained-in-memory", Assert.Single(history.QueryEvents(Query())).Id);
        Assert.False(history.Status.EventQueries.LastExecution.MemoryCacheUsed);
        Assert.True(history.Status.EventQueries.CacheInvalidations > 0);
    }

    [Fact]
    public async Task Corrupt_initial_records_keep_good_events_and_disable_the_fast_path()
    {
        string path = WriteClosed(Event("good", 1));
        File.AppendAllText(path, "{broken-json\n");
        await using var history = new OperationalHistory(Options());
        await history.FlushAsync();
        Assert.Equal("good", Assert.Single(history.QueryEvents(Query())).Id);
        DiskRead(history);
        Assert.True(history.Status.CorruptRecords > 0);
    }

    [Fact]
    public async Task Failed_initial_io_is_not_mistaken_for_a_complete_empty_cache()
    {
        string path = WriteClosed(Event("previously-locked", 1));
        OperationalHistory history;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) history = new(Options());
        await using var lifetime = history;
        await history.FlushAsync();
        Assert.Equal("previously-locked", Assert.Single(history.QueryEvents(Query())).Id);
        DiskRead(history);
        Assert.NotNull(history.Status.EventQueries.CacheProofError);
    }

    [Fact]
    public async Task Query_interleaved_with_writer_rotation_and_pruning_preserves_the_fixed_historical_window()
    {
        string target = WriteClosed(Event("one", 1), Event("two", 2), Event("three", 3));
        File.SetLastWriteTimeUtc(target, _now.AddDays(-1).UtcDateTime);
        var options = Options(); options.MaxSegmentBytes = 4096; options.MaxDiskBytes = 16 * 1024;
        await using var history = new OperationalHistory(options);
        await history.FlushAsync();
        var query = Query(1, 3);
        string expected = Payload(history.QueryEvents(query)); MemoryHit(history);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var mayOpen = new ManualResetEventSlim(false);
        var enumerated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int suspended = 0;
        history.FileOperationProbe = (phase, path) =>
        {
            if (phase == "query-before-open" && path.Equals(target, StringComparison.OrdinalIgnoreCase)
                && Interlocked.CompareExchange(ref suspended, 1, 0) == 0)
            { enumerated.TrySetResult(); mayOpen.Wait(deadline.Token); }
        };
        Task<IReadOnlyList<OperationalEvent>>? pending = null;
        try
        {
            pending = Task.Run(() => history.QueryEvents(query));
            await enumerated.Task.WaitAsync(deadline.Token);
            for (int i = 20; i < 44; i++) history.RecordEvent(Event("newer-" + i, i, detail: new string('x', 2048)));
            await history.FlushAsync(deadline.Token);
            Assert.False(File.Exists(target));
            mayOpen.Set();
            Assert.Equal(expected, Payload(await pending.WaitAsync(deadline.Token)));
            Assert.False(history.Status.EventQueries.LastExecution.MemoryCacheUsed);
            Assert.True(history.Status.StorageAvailable);
            Assert.Null(history.Status.EventQueries.CacheProofError);
            Assert.Equal(0, history.Status.EventQueries.CacheInvalidations);
        }
        finally
        {
            mayOpen.Set(); history.FileOperationProbe = null;
            if (pending != null) await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(expected, Payload(history.QueryEvents(query))); MemoryHit(history);
    }

    [Fact]
    public async Task Retention_eviction_is_recorded_even_below_the_event_capacity()
    {
        var options = Options(); options.Retention = TimeSpan.FromMinutes(1);
        await using var history = new OperationalHistory(options);
        history.RecordEvent(Event("expired-from-memory", -120));
        history.RecordEvent(Event("recent", -10));
        await history.FlushAsync();
        Assert.Equal(2, history.QueryEvents(Query(-180, -1)).Count);
        DiskRead(history);
        Assert.Equal("recent", Assert.Single(history.QueryEvents(Query(-20, -1))).Id);
        MemoryHit(history);
    }

    [Fact]
    public async Task Invalid_ranges_are_counted_as_failed_event_queries_without_affecting_series_counters()
    {
        await using var history = new OperationalHistory(Options());
        Assert.Throws<ArgumentException>(() => history.QueryEvents(Query(2, 1)));
        Assert.Equal(1, history.Status.EventQueries.Queries);
        Assert.False(history.Status.EventQueries.LastExecution.Succeeded);
        Assert.Equal(0, history.Status.EventQueries.DiskQueries);
        Assert.Equal(0, history.Status.Queries.Queries);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
