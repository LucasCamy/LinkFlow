using System.Diagnostics.Metrics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace LinkFlow.Tests;

public sealed class OperationalHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LinkFlow-history-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    private OperationalHistoryOptions Options(bool persistent = false) => new()
    {
        DirectoryPath = persistent ? _directory : null,
        TimeProvider = new FixedClock(_now),
        SampleInterval = TimeSpan.FromSeconds(1)
    };

    private TelemetryFrame Frame(int seconds, double? value, ReadingValidity validity = ReadingValidity.Fresh, string? session = null) => new()
    {
        TimestampUtc = _now.AddSeconds(seconds), SessionId = session ?? string.Empty,
        Values = [new("cpu.temp", "°C", value, value == null ? null : _now.AddSeconds(seconds), validity, "CPU package")]
    };

    private HistoryQuery Query(int seconds = 4) => new()
    {
        MetricKey = "cpu.temp", FromUtc = _now, ToUtc = _now.AddSeconds(seconds), BucketSeconds = 1, MaxPoints = 100
    };

    [Fact]
    public async Task Missing_samples_keep_their_time_positions_and_do_not_inflate_coverage()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(Frame(0, 50));
        history.RecordFrame(Frame(2, 70));

        var series = history.Query(Query());

        Assert.Equal(4, series.Points.Count);
        Assert.Equal(50, series.Points[0].Value);
        Assert.Null(series.Points[1].Value);
        Assert.Equal(_now.AddSeconds(1), series.Points[1].TimestampUtc);
        Assert.Equal(70, series.Points[2].Value);
        Assert.Null(series.Points[3].Value);
        Assert.Equal(50, series.Statistics.CoveragePercent);
        Assert.Equal(2, series.Statistics.ObservedSeconds);
        Assert.Equal(4, series.Statistics.ExpectedSamples);
    }

    [Fact]
    public async Task Stale_and_invalid_values_are_not_counted_as_fresh_measurements()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(Frame(0, 50));
        history.RecordFrame(Frame(1, 90, ReadingValidity.Stale));
        history.RecordFrame(Frame(2, double.PositiveInfinity));
        history.RecordFrame(Frame(3, null, ReadingValidity.Unavailable));

        var series = history.Query(Query());

        Assert.Equal(1, series.Statistics.ValidSamples);
        Assert.Equal(50, series.Statistics.Max);
        Assert.Equal(ReadingValidity.Stale, series.Points[1].Validity);
        Assert.Equal(ReadingValidity.Invalid, series.Points[2].Validity);
        Assert.Null(series.Points[1].Value);
        Assert.Null(series.Points[2].Value);
    }

    [Fact]
    public async Task Aggregates_keep_minimum_maximum_percentile_and_threshold_duration()
    {
        await using var history = new OperationalHistory(Options());
        for (int i = 0; i < 4; i++) history.RecordFrame(Frame(i, 40 + i * 10));
        var query = Query();
        query.BucketSeconds = 2;
        query.Threshold = 60;

        var series = history.Query(query);

        Assert.Equal(2, series.Points.Count);
        Assert.Equal(45, series.Points[0].Value);
        Assert.Equal(40, series.Points[0].Min);
        Assert.Equal(50, series.Points[0].Max);
        Assert.Equal(2, series.Points[0].ValidSamples);
        Assert.Equal(55, series.Statistics.Average);
        Assert.Equal(70, series.Statistics.P95);
        Assert.Equal(2, series.Statistics.SecondsAboveThreshold);
    }

    [Fact]
    public async Task Query_bounds_output_and_rejects_invalid_ranges()
    {
        await using var history = new OperationalHistory(Options());
        var query = Query(86400);
        query.MaxPoints = 10;
        var series = history.Query(query);
        Assert.InRange(series.Points.Count, 1, 10);
        query.ToUtc = query.FromUtc.AddSeconds(-1);
        Assert.Throws<ArgumentException>(() => history.Query(query));
    }

    [Fact]
    public async Task Recent_queries_use_verified_memory_without_reading_json_payloads()
    {
        await using var history = new OperationalHistory(Options(true));
        for (int i = 0; i < 10; i++) history.RecordFrame(Frame(i, 50 + i));
        await history.FlushAsync();

        var series = history.Query(Query(10));

        Assert.Equal(10, series.Statistics.ValidSamples);
        Assert.Equal(100, series.Statistics.CoveragePercent);
        Assert.True(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(0, series.QueryExecution.DiskScans);
        Assert.Equal(0, series.QueryExecution.BytesRead);
        Assert.Equal(0, series.QueryExecution.FramesDeserialized);
        Assert.True(history.Status.Queries.MemoryCacheHits > 0);
    }

    [Fact]
    public void Segment_metadata_interop_matches_the_documented_FILE_BASIC_INFO_layout()
    {
        var type = typeof(OperationalHistory).GetNestedType("FileBasicInfo", System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(40, System.Runtime.InteropServices.Marshal.SizeOf(type));
        Assert.Equal(0, System.Runtime.InteropServices.Marshal.OffsetOf(type, "CreationTime").ToInt32());
        Assert.Equal(8, System.Runtime.InteropServices.Marshal.OffsetOf(type, "LastAccessTime").ToInt32());
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.OffsetOf(type, "LastWriteTime").ToInt32());
        Assert.Equal(24, System.Runtime.InteropServices.Marshal.OffsetOf(type, "ChangeTime").ToInt32());
        Assert.Equal(32, System.Runtime.InteropServices.Marshal.OffsetOf(type, "FileAttributes").ToInt32());
    }

    [Fact]
    public async Task Duplicate_frame_ids_are_resolved_before_the_fast_path_filters_time()
    {
        await using var history = new OperationalHistory(Options());
        var first = Frame(0, 50);
        history.RecordFrame(first);
        history.RecordFrame(first with
        {
            TimestampUtc = _now.AddSeconds(10), Values = [new("cpu.temp", "°C", 90, _now.AddSeconds(10))]
        });

        var early = history.Query(Query());
        var laterQuery = Query(12); laterQuery.FromUtc = _now.AddSeconds(9);
        var later = history.Query(laterQuery);

        Assert.True(early.QueryExecution.MemoryCacheUsed);
        Assert.Equal(0, early.Statistics.TotalSamples);
        Assert.All(early.Points, p => Assert.Null(p.Value));
        Assert.Equal(90, later.Points[1].Value);
    }

    [Fact]
    public async Task Query_diagnostics_keep_counters_and_only_the_latest_execution()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(Frame(0, 50));
        history.Query(Query());
        var invalid = Query(); invalid.ToUtc = invalid.FromUtc.AddSeconds(-1);
        Assert.Throws<ArgumentException>(() => history.Query(invalid));

        var counters = history.Status.Queries;
        Assert.Equal(2, counters.Queries);
        Assert.Equal(1, counters.MemoryCacheHits);
        Assert.Equal(0, counters.DiskQueries);
        Assert.Equal(0, counters.BytesRead);
        Assert.False(counters.LastExecution.Succeeded);
        Assert.True(counters.LastExecution.DurationMilliseconds >= 0);
    }

    [Fact]
    public async Task A_cold_restart_can_prove_complete_cache_coverage_including_previous_context()
    {
        WriteClosedFrames(
            new() { TimestampUtc = _now.AddSeconds(-4), Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-5), ExpectedIntervalSeconds: 30)] },
            new() { TimestampUtc = _now.AddSeconds(26), Values = [new("engine.memory.private", "B", 1000, _now.AddSeconds(25), ExpectedIntervalSeconds: 30)] });
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        var query = Query(40); query.MetricKey = "engine.memory.private"; query.Threshold = 5000;

        var series = history.Query(query);

        Assert.True(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(0, series.QueryExecution.BytesRead);
        Assert.Equal(40, series.Statistics.ObservedSeconds);
        Assert.Equal(25, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(1, series.Statistics.TotalSamples);
        Assert.Equal(1000, series.Statistics.Average);
    }

    [Fact]
    public async Task An_evicted_window_uses_one_scan_and_materializes_only_the_requested_frames()
    {
        WriteClosedFrames(Enumerable.Range(0, 80).Select(i => Frame(i, 50 + i)).ToArray());
        var options = Options(true); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        await history.FlushAsync();
        var query = Query(23); query.FromUtc = _now.AddSeconds(20); query.Threshold = 71;

        var series = history.Query(query);

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.True(series.QueryExecution.BytesRead > 0);
        Assert.True(series.QueryExecution.RecordsScanned >= 80);
        Assert.Equal(3, series.QueryExecution.FramesDeserialized);
        Assert.Equal(new double?[] { 70, 71, 72 }, series.Points.Select(p => p.Value));
        Assert.Equal(100, series.Statistics.CoveragePercent);
        Assert.Equal(2, series.Statistics.SecondsAboveThreshold);
    }

    [Fact]
    public async Task The_single_pass_preserves_legacy_comment_and_trailing_comma_reading()
    {
        string path = WriteClosedFrames(Enumerable.Range(0, 20).Select(i => Frame(i, 50 + i)).ToArray());
        string[] lines = File.ReadAllLines(path);
        lines[0] = lines[0][..^1] + ",/* legacy journal */}";
        File.WriteAllLines(path, lines);
        var options = Options(true); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        await history.FlushAsync();

        var series = history.Query(Query(2));

        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(new double?[] { 50, 51 }, series.Points.Select(p => p.Value));
        Assert.Equal(0, history.Status.CorruptRecords);
    }

    [Fact]
    public async Task An_out_of_order_eviction_cannot_be_hidden_by_an_older_retained_frame()
    {
        var options = Options(true); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        history.RecordFrame(Frame(100, 90));
        for (int i = 0; i < 4; i++) history.RecordFrame(Frame(i, 50 + i));
        await history.FlushAsync();
        var query = Query(102); query.FromUtc = _now.AddSeconds(99);

        var series = history.Query(query);

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(90, series.Points[1].Value);
        Assert.Equal(1, series.Statistics.ValidSamples);
    }

    [Fact]
    public async Task A_long_cadence_context_evicted_from_memory_is_still_read_from_disk()
    {
        var options = Options(true); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(-19),
            Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-20), ExpectedIntervalSeconds: 30)]
        });
        for (int i = 0; i < 4; i++) history.RecordFrame(Frame(i, 50));
        await history.FlushAsync();
        var query = Query(10); query.MetricKey = "engine.memory.private";

        var series = history.Query(query);

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(30, series.ExpectedIntervalSeconds);
        Assert.Equal(10, series.Statistics.ObservedSeconds);
        Assert.Equal(0, series.Statistics.TotalSamples);
        Assert.Equal(100, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task Legacy_resource_context_without_explicit_cadence_cannot_be_hidden_by_the_eviction_fence()
    {
        string path = WriteClosedFrames(new TelemetryFrame
        {
            TimestampUtc = _now.AddSeconds(-20),
            Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-20))]
        });
        Assert.DoesNotContain("expectedIntervalSeconds", File.ReadAllText(path));
        var options = Options(true); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        for (int i = 0; i < 4; i++) history.RecordFrame(Frame(i, 50));
        await history.FlushAsync();
        var query = Query(10); query.MetricKey = "engine.memory.private";

        var series = history.Query(query);

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(30, series.ExpectedIntervalSeconds);
        Assert.Equal(10, series.Statistics.ObservedSeconds);
        Assert.Equal(100, series.Statistics.CoveragePercent);
        Assert.Equal(0, series.Statistics.TotalSamples);
        Assert.Equal(0, series.Statistics.ValidSamples);
        Assert.All(series.Points, p => Assert.Null(p.Value));
    }

    [Fact]
    public async Task Legacy_resource_samples_inside_the_window_keep_their_thirty_second_cadence()
    {
        WriteClosedFrames(
            new() { TimestampUtc = _now, Values = [new("engine.memory.private", "B", 1000, _now)] },
            new() { TimestampUtc = _now.AddSeconds(30), Values = [new("engine.memory.private", "B", 1100, _now.AddSeconds(30))] });
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        var query = Query(60); query.MetricKey = "engine.memory.private";

        var series = history.Query(query);

        Assert.Equal(30, series.ExpectedIntervalSeconds);
        Assert.Equal(30, series.BucketSeconds);
        Assert.Equal(2, series.Statistics.ExpectedSamples);
        Assert.Equal(2, series.Statistics.ValidSamples);
        Assert.Equal(60, series.Statistics.ObservedSeconds);
        Assert.Equal(100, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task A_newer_expired_read_ends_older_long_cadence_context_in_the_single_disk_pass()
    {
        WriteClosedFrames(
            new() { TimestampUtc = _now.AddSeconds(-19), Values = [new("cpu.temp", "°C", 90, _now.AddSeconds(-20), ExpectedIntervalSeconds: 30)] },
            new() { TimestampUtc = _now.AddSeconds(-1.5), Values = [new("cpu.temp", "°C", 50, _now.AddSeconds(-2), ExpectedIntervalSeconds: 1)] });
        var options = Options(true); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        // Evict both contextual records; their timestamp/cadence fence requires the disk path.
        for (int i = 0; i < 4; i++) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(i), Values = [new("other.metric", "%", 10, _now.AddSeconds(i))]
        });
        await history.FlushAsync();
        var query = Query(5); query.Threshold = 75;

        var series = history.Query(query);

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(0, series.Statistics.ObservedSeconds);
        Assert.Equal(0, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(0, series.Statistics.ValidSamples);
        Assert.Equal("°C", series.Unit);
    }

    [Fact]
    public async Task Adding_a_segment_invalidates_the_cache_proof_and_exposes_the_new_observation()
    {
        await using var history = new OperationalHistory(Options(true));
        history.RecordFrame(Frame(0, 50));
        await history.FlushAsync();
        Assert.True(history.Query(Query()).QueryExecution.MemoryCacheUsed);
        WriteClosedFrames(Frame(1, 90, session: "external-session"));

        var series = history.Query(Query());

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(90, series.Points[1].Value);
        Assert.Equal(1, history.Status.Queries.CacheInvalidations);
    }

    [Fact]
    public async Task Appending_to_a_closed_segment_invalidates_the_proof_without_losing_new_samples()
    {
        string path = WriteClosedFrames(Enumerable.Range(0, 10).Select(i => Frame(i, 50 + i)).ToArray());
        var options = Options(true); options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        await history.FlushAsync();
        var query = Query(11); query.FromUtc = _now.AddSeconds(7);
        Assert.True(history.Query(query).QueryExecution.MemoryCacheUsed);
        await File.AppendAllTextAsync(path, FrameLine(Frame(10, 120, session: "external-session")));

        var series = history.Query(query);

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(120, series.Points[3].Value);
        Assert.Equal(4, series.Statistics.ValidSamples);
    }

    [Fact]
    public async Task Deleting_a_segment_invalidates_the_proof_and_preserves_the_existing_memory_tail()
    {
        string path = WriteClosedFrames(Enumerable.Range(0, 4).Select(i => Frame(i, 50)).ToArray());
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        Assert.True(history.Query(Query()).QueryExecution.MemoryCacheUsed);
        File.Delete(path);

        var series = history.Query(Query());

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(4, series.Statistics.ValidSamples);
        Assert.Equal(100, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task A_same_length_rewrite_with_preserved_last_write_is_detected_by_change_metadata()
    {
        string path = WriteClosedFrames(Frame(0, 50));
        long length = new FileInfo(path).Length;
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        Assert.True(history.Query(Query()).QueryExecution.MemoryCacheUsed);
        File.WriteAllText(path, FrameLine(Frame(0, 90, session: "fixture-session")));
        File.SetLastWriteTimeUtc(path, lastWrite);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(path));

        var series = history.Query(Query());

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(90, series.Statistics.Max);
    }

    [Fact]
    public async Task Segment_io_failure_is_visible_even_when_memory_has_the_requested_samples()
    {
        string path = WriteClosedFrames(Frame(0, 55));
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        Assert.True(history.Query(Query()).QueryExecution.MemoryCacheUsed);
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var series = history.Query(Query());

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.False(history.Status.StorageAvailable);
        Assert.NotNull(history.Status.LastError);
        Assert.NotNull(history.Status.Queries.CacheProofError);
        Assert.Equal(55, series.Points[0].Value);
        Assert.Equal(0, history.Status.CorruptRecords);
    }

    [Fact]
    public async Task Own_rotation_and_disk_pruning_preserve_a_verified_recent_memory_snapshot()
    {
        var options = Options(true); options.MaxDiskBytes = 16 * 1024; options.MaxSegmentBytes = 4096;
        await using var history = new OperationalHistory(options);
        for (int i = 0; i < 80; i++) history.RecordFrame(Frame(i, 50 + i));
        await history.FlushAsync();
        var query = Query(80); query.FromUtc = _now.AddSeconds(77);

        var series = history.Query(query);

        Assert.True(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(0, series.QueryExecution.BytesRead);
        Assert.Equal(new double?[] { 127, 128, 129 }, series.Points.Select(p => p.Value));
        Assert.Equal(100, series.Statistics.CoveragePercent);
        Assert.InRange(history.Status.DiskBytes, 1, options.MaxDiskBytes);
    }

    [Fact]
    public async Task A_query_enumerated_before_pruning_can_open_during_delete_before_commit_without_false_io_failure()
    {
        var options = Options(true); options.MaxDiskBytes = 16 * 1024; options.MaxSegmentBytes = 4096;
        string target = WriteClosedFrames(Enumerable.Range(0, 10).Select(i => Frame(i, 50 + i)).ToArray());
        File.SetLastWriteTimeUtc(target, _now.AddMinutes(-1).UtcDateTime);
        await using var history = new OperationalHistory(options);
        await history.FlushAsync();
        var query = Query(10); query.Threshold = 55;
        Assert.True(history.Query(query).QueryExecution.MemoryCacheUsed);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var mayOpen = new ManualResetEventSlim();
        using var mayCommit = new ManualResetEventSlim();
        var enumerated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int queryBlocked = 0;
        history.FileOperationProbe = (phase, path) =>
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(path, target)) return;
            if (phase == "query-before-open" && Interlocked.CompareExchange(ref queryBlocked, 1, 0) == 0)
            {
                enumerated.TrySetResult();
                mayOpen.Wait(deadline.Token);
            }
            else if (phase == "prune-after-delete")
            {
                deleted.TrySetResult();
                mayCommit.Wait(deadline.Token);
            }
        };
        Task<HistorySeries>? pendingQuery = null;
        Task? pendingFlush = null;
        try
        {
            pendingQuery = Task.Run(() => history.Query(query));
            await enumerated.Task.WaitAsync(deadline.Token);
            // The query already owns an enumeration containing target. Starting it after Delete
            // would miss the original race because Directory.GetFiles would omit that path.
            for (int i = 10; i < 110; i++) history.RecordFrame(Frame(i, 50 + i));
            pendingFlush = history.FlushAsync(deadline.Token);
            await deleted.Task.WaitAsync(deadline.Token);
            Assert.False(File.Exists(target));
            mayOpen.Set(); // Open now fails with FileNotFound, while the prune is still pending.

            var series = await pendingQuery.WaitAsync(deadline.Token);

            Assert.False(series.QueryExecution.MemoryCacheUsed);
            Assert.Equal(1, series.QueryExecution.DiskScans);
            Assert.Equal(10, series.Statistics.ValidSamples);
            Assert.Equal(10, series.Statistics.ObservedSeconds);
            Assert.Equal(5, series.Statistics.SecondsAboveThreshold);
            Assert.Equal(100, series.Statistics.CoveragePercent);
            Assert.Equal(54.5, series.Statistics.Average);
            Assert.Equal(59, series.Statistics.P95);
            Assert.True(history.Status.StorageAvailable);
            Assert.Null(history.Status.LastError);
            Assert.Null(history.Status.Queries.CacheProofError);
            Assert.Equal(0, history.Status.Queries.CacheInvalidations);
        }
        finally
        {
            mayOpen.Set(); mayCommit.Set(); history.FileOperationProbe = null;
            if (pendingQuery != null) await pendingQuery.WaitAsync(TimeSpan.FromSeconds(10));
            if (pendingFlush != null) await pendingFlush.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(history.Query(query).QueryExecution.MemoryCacheUsed);
        Assert.Equal(0, history.Status.Queries.CacheInvalidations);
        Assert.InRange(history.Status.DiskBytes, 1, options.MaxDiskBytes);
    }

    [Fact]
    public async Task An_external_removal_between_prune_intention_and_delete_is_reported_and_does_not_evict_an_extra_segment()
    {
        var options = Options(true); options.MaxDiskBytes = 16 * 1024; options.MaxSegmentBytes = 4096;
        string target = WriteClosedSegment(new string('a', 32), _now.AddMinutes(-1));
        await using var history = new OperationalHistory(options);
        for (int i = 0; i < 4; i++) history.RecordFrame(Frame(i, 50 + i));
        await history.FlushAsync();
        string retained = WriteClosedSegment(new string('b', 32), _now.AddMinutes(1));
        int removed = 0;
        history.FileOperationProbe = (phase, path) =>
        {
            if (phase == "prune-before-delete" && StringComparer.OrdinalIgnoreCase.Equals(path, target) &&
                Interlocked.CompareExchange(ref removed, 1, 0) == 0) File.Delete(target);
        };
        try
        {
            history.RecordFrame(Frame(4, 90));
            await history.FlushAsync();
        }
        finally { history.FileOperationProbe = null; }

        Assert.Equal(1, removed);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(retained));
        Assert.False(history.Status.StorageAvailable);
        Assert.Contains("disappeared", history.Status.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(history.Status.Queries.CacheProofError);
        var series = history.Query(Query());
        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(4, series.Statistics.ValidSamples);
        Assert.Equal(100, series.Statistics.CoveragePercent);
        long actualBytes = Directory.GetFiles(_directory, "lfh-*.jsonl").Sum(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        });
        Assert.Equal(actualBytes, history.Status.DiskBytes);
    }

    [Fact]
    public async Task A_failed_prune_keeps_the_segment_and_exposes_the_access_failure()
    {
        var options = Options(true); options.MaxDiskBytes = 16 * 1024; options.MaxSegmentBytes = 4096;
        string target = WriteClosedSegment(new string('a', 32), _now.AddMinutes(-1));
        await using var history = new OperationalHistory(options);
        await history.FlushAsync();
        string other = WriteClosedSegment(new string('b', 32), _now.AddMinutes(1));
        File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
        try
        {
            history.RecordFrame(Frame(0, 55));
            await history.FlushAsync();

            Assert.True(File.Exists(target));
            Assert.False(File.Exists(other));
            Assert.False(history.Status.StorageAvailable);
            Assert.NotNull(history.Status.LastError);
            Assert.NotNull(history.Status.Queries.CacheProofError);
            var series = history.Query(Query());
            Assert.False(series.QueryExecution.MemoryCacheUsed);
            Assert.Equal(55, series.Points[0].Value);
            Assert.Equal(0, history.Status.CorruptRecords);
        }
        finally { if (File.Exists(target)) File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task External_corruption_forces_a_scan_and_remains_observable_without_hiding_good_samples()
    {
        string path = WriteClosedFrames(Frame(0, 55));
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        Assert.True(history.Query(Query()).QueryExecution.MemoryCacheUsed);
        await File.AppendAllTextAsync(path, "{broken\n");

        var series = history.Query(Query());

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(55, series.Points[0].Value);
        Assert.True(history.Status.CorruptRecords > 0);
        long corrupt = history.Status.CorruptRecords;
        history.Query(Query());
        Assert.Equal(corrupt, history.Status.CorruptRecords);
    }

    [Theory]
    [InlineData(1e308)]
    [InlineData(86401)]
    [InlineData(.01)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_persisted_cadence_is_corruption_and_does_not_break_query_or_exports(double cadence)
    {
        WriteClosedFrames(Frame(0, 50), new()
        {
            TimestampUtc = _now.AddSeconds(1),
            Values = [new("cpu.temp", "°C", 99, _now.AddSeconds(1), ExpectedIntervalSeconds: cadence)]
        }, Frame(2, 60));
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        var query = Query(); query.Threshold = 55;

        var series = history.Query(query);
        string csv = history.ExportCsv(query);
        using var json = JsonDocument.Parse(history.ExportJson(query));

        Assert.Equal(2, series.Statistics.ValidSamples);
        Assert.Equal(2, series.Statistics.ObservedSeconds);
        Assert.Equal(1, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(50, series.Statistics.CoveragePercent);
        Assert.Null(series.Points[1].Value);
        Assert.Equal(1, history.Status.CorruptRecords);
        Assert.DoesNotContain(",99,", csv);
        Assert.Equal(2, json.RootElement.GetProperty("series").GetProperty("statistics").GetProperty("validSamples").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("series").GetProperty("points")[1].GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData(.02)]
    [InlineData(86400)]
    public async Task Persisted_cadence_endpoints_remain_valid(double cadence)
    {
        WriteClosedFrames(new TelemetryFrame { TimestampUtc = _now,
            Values = [new("cpu.temp", "°C", 50, _now, ExpectedIntervalSeconds: cadence)] });
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();

        var series = history.Query(Query());

        Assert.Equal(1, series.Statistics.ValidSamples);
        Assert.Equal(Math.Min(4, cadence), series.Statistics.ObservedSeconds);
        Assert.Equal(0, history.Status.CorruptRecords);
    }

    [Fact]
    public async Task Producer_default_cadence_and_reader_bounds_stay_consistent()
    {
        var options = Options(true); options.SampleInterval = TimeSpan.FromDays(2);
        await using (var history = new OperationalHistory(options))
        {
            history.RecordFrame(Frame(0, 50));
            await history.FlushAsync();
            Assert.Equal(86400, history.Query(Query()).ExpectedIntervalSeconds);
        }

        await using var restored = new OperationalHistory(options);
        var series = restored.Query(Query());
        Assert.Equal(86400, series.ExpectedIntervalSeconds);
        Assert.Equal(1, series.Statistics.ValidSamples);
        Assert.Equal(0, restored.Status.CorruptRecords);
    }

    [Fact]
    public async Task A_persisted_acquisition_that_overflows_real_UTC_is_rejected_without_losing_good_lines()
    {
        WriteClosedFrames(Frame(0, 50), new()
        {
            TimestampUtc = _now.AddSeconds(1),
            Values = [new("cpu.temp", "°C", 99, DateTimeOffset.MaxValue.AddMilliseconds(-500), ExpectedIntervalSeconds: 1)]
        }, Frame(2, 60));
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();

        var series = history.Query(Query());
        using var exported = JsonDocument.Parse(history.ExportJson(Query()));

        Assert.Equal(2, series.Statistics.ValidSamples);
        Assert.Equal(2, series.Statistics.ObservedSeconds);
        Assert.Equal(1, history.Status.CorruptRecords);
        Assert.Null(series.Points[1].Value);
        Assert.Equal(2, exported.RootElement.GetProperty("series").GetProperty("statistics").GetProperty("validSamples").GetInt32());
    }

    [Fact]
    public async Task Offset_local_year_boundaries_are_normalized_before_adding_the_actual_interval()
    {
        var acquisition = new DateTimeOffset(9999, 12, 31, 23, 59, 58, TimeSpan.FromHours(14));
        var published = acquisition.ToUniversalTime();
        WriteClosedFrames(new TelemetryFrame { TimestampUtc = published, Values = [new("cpu.temp", "°C", 70, acquisition, ExpectedIntervalSeconds: 30)] });
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        var query = new HistoryQuery { MetricKey = "cpu.temp", FromUtc = published, ToUtc = published.AddMinutes(1), Threshold = 65 };

        var series = history.Query(query);
        using var exported = JsonDocument.Parse(history.ExportJson(query));

        Assert.Equal(1, series.Statistics.ValidSamples);
        Assert.Equal(30, series.Statistics.ObservedSeconds);
        Assert.Equal(30, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(50, series.Statistics.CoveragePercent);
        Assert.Equal(0, history.Status.CorruptRecords);
        Assert.Equal(30, exported.RootElement.GetProperty("series").GetProperty("statistics").GetProperty("observedSeconds").GetDouble());
    }

    [Fact]
    public async Task Invalid_typed_acquisition_intervals_are_rejected_before_they_enter_memory()
    {
        await using var history = new OperationalHistory(Options());
        Assert.Throws<ArgumentException>(() => history.RecordFrame(new()
        {
            TimestampUtc = _now,
            Values = [new("cpu.temp", "°C", 99, DateTimeOffset.MaxValue, ExpectedIntervalSeconds: 1)]
        }));
        history.RecordFrame(Frame(0, 50));

        Assert.Equal(1, history.Query(Query()).Statistics.ValidSamples);
        Assert.Equal(0, history.Status.CorruptRecords);
    }

    [Fact]
    public async Task A_fixed_historical_window_stays_correct_while_the_writer_appends_rotates_and_prunes()
    {
        var options = Options(true); options.MaxDiskBytes = 16 * 1024; options.MaxSegmentBytes = 4096;
        await using var history = new OperationalHistory(options);
        for (int i = 0; i < 10; i++) history.RecordFrame(Frame(i, 50 + i));
        await history.FlushAsync();
        var query = Query(10); query.Threshold = 55;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var rounds = System.Threading.Channels.Channel.CreateUnbounded<TaskCompletionSource>();
        var writer = Task.Run(async () =>
        {
            try
            {
                for (int chunk = 0; chunk < 12; chunk++)
                {
                    for (int item = 0; item < 12; item++)
                    {
                        int time = 10 + chunk * 12 + item;
                        history.RecordFrame(Frame(time, 50 + time));
                    }
                    var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task pendingFlush = history.FlushAsync(stop.Token);
                    await rounds.Writer.WriteAsync(observed, stop.Token);
                    await observed.Task.WaitAsync(stop.Token);
                    await pendingFlush;
                }
                rounds.Writer.TryComplete();
            }
            catch (Exception error) { rounds.Writer.TryComplete(error); throw; }
        });
        int reads = 0;
        try
        {
            await foreach (var observed in rounds.Reader.ReadAllAsync(stop.Token))
            {
                try
                {
                    // Query before joining the posted flush, while the backend can rotate/prune.
                    var series = history.Query(query);
                    Assert.Equal(10, series.Statistics.ValidSamples);
                    Assert.Equal(10, series.Statistics.TotalSamples);
                    Assert.Equal(10, series.Statistics.ObservedSeconds);
                    Assert.Equal(5, series.Statistics.SecondsAboveThreshold);
                    Assert.Equal(100, series.Statistics.CoveragePercent);
                    Assert.Equal(54.5, series.Statistics.Average);
                    Assert.Equal(59, series.Statistics.P95);
                    Assert.InRange(series.QueryExecution.DiskScans, 0, 1);
                    if (!series.QueryExecution.MemoryCacheUsed) Assert.Equal(1, series.QueryExecution.DiskScans);
                    reads++;
                }
                finally { observed.TrySetResult(); }
            }
        }
        finally
        {
            stop.Cancel();
            try { await writer; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        Assert.Equal(12, reads);
        Assert.InRange(history.Status.DiskBytes, 1, options.MaxDiskBytes);
        Assert.Equal(0, history.Status.CorruptRecords);
    }

    [Fact]
    public async Task An_incomplete_initial_load_cannot_be_treated_as_a_complete_cache()
    {
        string path = WriteClosedFrames(Frame(0, 55));
        await File.AppendAllTextAsync(path, "{broken\n");
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();

        var series = history.Query(Query());

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal("load-incomplete", series.QueryExecution.CacheDecision);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(55, series.Points[0].Value);
        Assert.True(history.Status.CorruptRecords > 0);
    }

    [Fact]
    public async Task Future_dated_segments_skipped_at_startup_cannot_be_hidden_by_a_cache_hit()
    {
        Directory.CreateDirectory(_directory);
        var future = Frame(0, 55, session: "future-session") with
        {
            TimestampUtc = _now.AddDays(2), Values = [new("cpu.temp", "°C", 55, _now.AddDays(2))]
        };
        File.WriteAllText(Path.Combine(_directory, $"lfh-{future.TimestampUtc:yyyyMMdd}-{Guid.NewGuid():N}-000001.jsonl"), FrameLine(future));
        await using var history = new OperationalHistory(Options(true));
        await history.FlushAsync();
        var query = Query(); query.FromUtc = _now.AddDays(2); query.ToUtc = query.FromUtc.AddSeconds(2);

        var series = history.Query(query);

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal(1, series.QueryExecution.DiskScans);
        Assert.Equal(55, series.Points[0].Value);
    }

    [Fact]
    public async Task A_future_acquisition_timestamp_requires_the_conservative_disk_path()
    {
        await using var history = new OperationalHistory(Options(true));
        history.RecordFrame(new() { TimestampUtc = _now, Values = [new("cpu.temp", "°C", 50, _now.AddSeconds(1))] });
        await history.FlushAsync();

        var series = history.Query(Query());

        Assert.False(series.QueryExecution.MemoryCacheUsed);
        Assert.Equal("acquisition-order-unknown", series.QueryExecution.CacheDecision);
        Assert.Equal(1, series.Statistics.ValidSamples);
    }

    private string WriteClosedFrames(params TelemetryFrame[] frames)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"lfh-{_now:yyyyMMdd}-{Guid.NewGuid():N}-000001.jsonl");
        File.WriteAllText(path, string.Concat(frames.Select(frame => FrameLine(frame with
        {
            SessionId = string.IsNullOrWhiteSpace(frame.SessionId) ? "fixture-session" : frame.SessionId
        }))));
        return path;
    }

    private static string FrameLine(TelemetryFrame frame) => JsonSerializer.Serialize(new { type = "frame", frame }, Json.Compact) + "\n";

    [Fact]
    public async Task Persisted_samples_and_events_survive_restart_without_duplicate_points()
    {
        await using (var history = new OperationalHistory(Options(true)))
        {
            history.RecordFrame(Frame(0, 51));
            history.RecordEvent(new() { TimestampUtc = _now, Kind = "test.event", Message = "persisted" });
            await history.FlushAsync();
            Assert.Equal(1, history.Query(Query()).Statistics.TotalSamples);
            Assert.True(history.Status.StorageAvailable);
            Assert.NotNull(history.Status.LastWriteUtc);
        }

        await using var restored = new OperationalHistory(Options(true));
        var series = restored.Query(Query());
        Assert.Equal(1, series.Statistics.TotalSamples);
        Assert.Equal(51, series.Points[0].Value);
        Assert.Contains(restored.QueryEvents(new() { FromUtc = _now.AddSeconds(-1), ToUtc = _now.AddSeconds(1) }),
            e => e.Kind == "test.event" && e.Message == "persisted");
    }

    [Fact]
    public async Task Damaged_tail_does_not_hide_previous_samples_or_prevent_new_writes()
    {
        await using (var history = new OperationalHistory(Options(true)))
        {
            history.RecordFrame(Frame(0, 52));
            await history.FlushAsync();
        }
        var file = Directory.GetFiles(_directory, "lfh-*.jsonl").First();
        await File.AppendAllTextAsync(file, "{\"type\":\"frame\",truncated\n");

        await using var recovered = new OperationalHistory(Options(true));
        Assert.True(recovered.Status.CorruptRecords > 0);
        recovered.RecordFrame(Frame(1, 53));
        await recovered.FlushAsync();
        Assert.Equal(2, recovered.Query(Query()).Statistics.ValidSamples);
        Assert.True(recovered.Status.StorageAvailable);
    }

    [Fact]
    public async Task Storage_failure_remains_visible_while_memory_history_continues()
    {
        Directory.CreateDirectory(_directory);
        var blockingFile = Path.Combine(_directory, "not-a-directory");
        File.WriteAllText(blockingFile, "occupied");
        var options = Options(true);
        options.DirectoryPath = blockingFile;
        await using var history = new OperationalHistory(options);
        history.RecordFrame(Frame(0, 54));
        await history.FlushAsync();

        Assert.False(history.Status.StorageAvailable);
        Assert.NotNull(history.Status.LastError);
        Assert.Equal(54, history.Query(Query()).Points[0].Value);
    }

    [Fact]
    public async Task Disk_and_memory_retention_have_enforced_bounds()
    {
        var options = Options(true);
        options.MaxDiskBytes = 16 * 1024;
        options.MaxSegmentBytes = 4096;
        options.MaxMemoryFrames = 4;
        await using var history = new OperationalHistory(options);
        for (int i = 0; i < 80; i++)
        {
            history.RecordFrame(Frame(i, 50 + i));
            await history.FlushAsync();
        }
        long bytes = Directory.GetFiles(_directory, "lfh-*.jsonl").Sum(p => new FileInfo(p).Length);
        Assert.InRange(bytes, 1, options.MaxDiskBytes);
        Assert.Equal(4, history.Status.StoredFrames);
        Assert.InRange(history.Status.DiskBytes, 1, options.MaxDiskBytes);
    }

    [Fact]
    public async Task Disk_budget_removes_the_oldest_segment_when_session_ids_sort_in_reverse_age_order()
    {
        var options = Options(true);
        options.MaxDiskBytes = 16 * 1024;
        options.MaxSegmentBytes = 4096;
        var day = new DateTimeOffset(_now.UtcDateTime.Date, TimeSpan.Zero);
        string oldest = WriteClosedSegment(new string('f', 32), day.AddHours(1));
        string newest = WriteClosedSegment(new string('0', 32), day.AddHours(2));
        Assert.True(new FileInfo(oldest).Length + new FileInfo(newest).Length > options.MaxDiskBytes);

        await using var history = new OperationalHistory(options);
        await history.FlushAsync();

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newest));
        long bytes = Directory.GetFiles(_directory, "lfh-*.jsonl").Sum(p => new FileInfo(p).Length);
        Assert.Equal(bytes, history.Status.DiskBytes);
        Assert.InRange(bytes, 1, options.MaxDiskBytes);
    }

    [Fact]
    public async Task Disk_budget_protects_the_active_segment_even_when_its_timestamp_is_oldest()
    {
        var options = Options(true);
        options.MaxDiskBytes = 16 * 1024;
        options.MaxSegmentBytes = 4096;
        await using var history = new OperationalHistory(options);
        await history.FlushAsync();
        string active = Assert.Single(Directory.GetFiles(_directory, "lfh-*.jsonl"));
        string olderClosed = WriteClosedSegment(new string('f', 32), _now.AddHours(1));
        string newerClosed = WriteClosedSegment(new string('0', 32), _now.AddHours(2));

        history.RecordFrame(Frame(0, 50));
        await history.FlushAsync();

        Assert.True(File.Exists(active));
        Assert.False(File.Exists(olderClosed));
        Assert.True(File.Exists(newerClosed));
        long bytes = Directory.GetFiles(_directory, "lfh-*.jsonl").Sum(p => new FileInfo(p).Length);
        Assert.Equal(bytes, history.Status.DiskBytes);
        Assert.InRange(bytes, 1, options.MaxDiskBytes);
    }

    private string WriteClosedSegment(string sessionId, DateTimeOffset lastWriteUtc)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"lfh-{lastWriteUtc:yyyyMMdd}-{sessionId}-000001.jsonl");
        var lines = Enumerable.Range(0, 12).Select(index => JsonSerializer.Serialize(new
        {
            type = "event",
            @event = new OperationalEvent
            {
                Id = $"{sessionId}-{index}", SessionId = sessionId, TimestampUtc = lastWriteUtc,
                Kind = "test.retention", Message = new string('x', 500)
            }
        }, Json.Compact));
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        File.SetLastWriteTimeUtc(path, lastWriteUtc.UtcDateTime);
        return path;
    }

    [Fact]
    public async Task Expired_files_are_removed_without_deleting_unrelated_files()
    {
        var options = Options(true);
        options.Retention = TimeSpan.FromDays(1);
        Directory.CreateDirectory(_directory);
        string old = Path.Combine(_directory, $"lfh-{_now.AddDays(-3):yyyyMMdd}-old-000001.jsonl");
        string unrelated = Path.Combine(_directory, "user-notes.txt");
        File.WriteAllText(old, "invalid\n");
        File.WriteAllText(unrelated, "keep");

        await using var history = new OperationalHistory(options);
        await history.FlushAsync();
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task Session_metadata_preserves_real_start_after_raw_eviction_and_restart()
    {
        var options = Options(true);
        options.MaxDiskBytes = 16 * 1024;
        options.MaxSegmentBytes = 4096;
        var started = _now.AddSeconds(-30);
        var stopped = _now.AddSeconds(-5);
        await using (var history = new OperationalHistory(options))
        {
            history.RecordEvent(new() { Kind = "engine.started", SessionId = "previous-session", TimestampUtc = started,
                Data = new() { ["version"] = "previous-version" } });
            history.RecordEvent(new() { Kind = "engine.stopped", SessionId = "previous-session", TimestampUtc = stopped });
            await history.FlushAsync();
            string firstSegment = Assert.Single(Directory.GetFiles(_directory, "lfh-*.jsonl"));
            for (int i = 0; i < 80; i++)
            {
                history.RecordFrame(Frame(i, 50 + i));
                await history.FlushAsync();
            }
            Assert.False(File.Exists(firstSegment));
        }
        foreach (string file in Directory.GetFiles(_directory, "lfh-*.jsonl")) File.Delete(file);

        await using var restored = new OperationalHistory(options);
        var session = Assert.Single(restored.QuerySessions(), s => s.Id == "previous-session");
        Assert.Equal(started, session.StartedUtc);
        Assert.Equal(stopped, session.StoppedUtc);
        Assert.Equal("previous-version", session.Version);
    }

    [Fact]
    public async Task Legacy_sessions_without_starts_are_omitted_and_counted_after_restart()
    {
        await using (var history = new OperationalHistory(Options(true)))
        {
            history.RecordEvent(new() { Kind = "engine.stopped", SessionId = "stop-only", TimestampUtc = _now.AddSeconds(-5) });
            history.RecordFrame(Frame(0, 50, session: "frame-only"));
            Assert.DoesNotContain(history.QuerySessions(), s => s.Id is "stop-only" or "frame-only");
            Assert.Equal(2, history.Status.SessionsWithUnknownStart);
            await history.FlushAsync();
        }
        foreach (string file in Directory.GetFiles(_directory, "lfh-*.jsonl")) File.Delete(file);

        await using var restored = new OperationalHistory(Options(true));
        Assert.DoesNotContain(restored.QuerySessions(), s => s.Id is "stop-only" or "frame-only");
        Assert.Equal(2, restored.Status.SessionsWithUnknownStart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Damaged_or_oversized_session_metadata_recovers_the_bounded_backup(bool oversized)
    {
        var started = _now.AddSeconds(-30);
        await using (var history = new OperationalHistory(Options(true)))
        {
            history.RecordEvent(new() { Kind = "engine.started", SessionId = "previous-session", TimestampUtc = started,
                Data = new() { ["version"] = "previous-version" } });
            await history.FlushAsync();
        }
        foreach (string file in Directory.GetFiles(_directory, "lfh-*.jsonl")) File.Delete(file);
        File.WriteAllText(Path.Combine(_directory, "sessions.json"), oversized ? new string('x', 64 * 1024 + 1) : "{broken");

        await using var restored = new OperationalHistory(Options(true));
        await restored.FlushAsync();

        Assert.Equal(started, Assert.Single(restored.QuerySessions(), s => s.Id == "previous-session").StartedUtc);
        Assert.True(restored.Status.CorruptRecords > 0);
        Assert.True(restored.Status.SessionMetadataAvailable);
        Assert.InRange(new FileInfo(Path.Combine(_directory, "sessions.json")).Length, 1, 64 * 1024);
        Assert.InRange(restored.Status.SessionMetadataBytes, 1, 128 * 1024);
    }

    [Fact]
    public async Task Session_metadata_has_independent_count_size_and_time_bounds()
    {
        var options = Options(true);
        options.Retention = TimeSpan.FromMinutes(1);
        await using var history = new OperationalHistory(options);
        history.RecordEvent(new() { Kind = "engine.started", SessionId = "expired-session", TimestampUtc = _now.AddMinutes(-2) });
        for (int i = 0; i < 120; i++) history.RecordEvent(new()
        {
            Kind = "engine.started", SessionId = $"session-{i}", TimestampUtc = _now.AddSeconds(-50 + i * .1),
            Data = new() { ["version"] = new string('v', 128) }
        });
        await history.FlushAsync();

        Assert.Equal(100, history.QuerySessions().Count);
        Assert.Contains(history.QuerySessions(), s => s.Id == history.SessionId);
        Assert.DoesNotContain(history.QuerySessions(), s => s.Id == "expired-session");
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(_directory, "sessions.json")));
        Assert.Equal(100, index.RootElement.GetProperty("sessions").GetArrayLength());
        Assert.InRange(history.Status.SessionMetadataBytes, 1, 128 * 1024);
    }

    [Fact]
    public async Task Session_metadata_failure_is_observable_while_in_memory_history_remains_available()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "sessions.json"));
        await using var history = new OperationalHistory(Options(true));
        history.RecordFrame(Frame(0, 55));
        await history.FlushAsync();

        Assert.False(history.Status.SessionMetadataAvailable);
        Assert.False(history.Status.StorageAvailable);
        Assert.NotNull(history.Status.SessionMetadataError);
        Assert.Equal(55, history.Query(Query()).Points[0].Value);
        Assert.Contains(history.QuerySessions(), s => s.Id == history.SessionId);
    }

    [Fact]
    public async Task Locked_session_metadata_reports_io_failure_without_claiming_corruption_or_overwriting_the_backup()
    {
        await using (var history = new OperationalHistory(Options(true)))
        {
            history.RecordEvent(new() { Kind = "engine.started", SessionId = "previous-session", TimestampUtc = _now.AddSeconds(-30) });
            await history.FlushAsync();
        }
        string path = Path.Combine(_directory, "sessions.json");
        string original = File.ReadAllText(path);
        string backup = File.ReadAllText(path + ".bak");
        using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var restored = new OperationalHistory(Options(true));
        await restored.FlushAsync();

        Assert.False(restored.Status.SessionMetadataAvailable);
        Assert.NotNull(restored.Status.SessionMetadataError);
        Assert.Equal(0, restored.Status.CorruptRecords);
        Assert.Equal(backup, File.ReadAllText(path + ".bak"));
        using (var reader = new StreamReader(held, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true))
            Assert.Equal(original, reader.ReadToEnd());
        Assert.Contains(restored.QuerySessions(), s => s.Id == "previous-session");

        held.Dispose();
        await restored.FlushAsync();
        Assert.True(restored.Status.SessionMetadataAvailable);
        Assert.Equal(0, restored.Status.CorruptRecords);
    }

    [Fact]
    public async Task An_oversized_raw_record_is_observable_and_does_not_terminate_the_writer()
    {
        var options = Options(true);
        options.MaxSegmentBytes = 4096;
        await using var history = new OperationalHistory(options);
        history.RecordFrame(new()
        {
            TimestampUtc = _now,
            Values = Enumerable.Range(0, 100).Select(i => new MetricObservation($"metric-{i}", "unit", i, _now,
                Source: new string('x', 160))).ToList()
        });
        await history.FlushAsync();
        Assert.False(history.Status.StorageAvailable);
        Assert.NotNull(history.Status.LastError);
        Assert.Equal(0, history.Status.CorruptRecords);

        history.RecordFrame(Frame(1, 56));
        await history.FlushAsync();
        Assert.True(history.Status.StorageAvailable);
        Assert.Equal(56, history.Query(Query()).Points[1].Value);
    }

    [Fact]
    public async Task Alert_escalation_and_resolution_are_individual_journal_events()
    {
        await using var history = new OperationalHistory(Options());
        var alert = new EngineAlert { Key = "cpu-temp", Since = _now.LocalDateTime, Severity = AlertSeverity.Warning, Message = "hot" };
        history.RecordAlert(alert);
        history.RecordAlert(alert);
        alert.Severity = AlertSeverity.Critical;
        history.RecordAlert(alert);
        history.RecordAlertResolution(alert.Key);
        var events = history.QueryEvents(new() { FromUtc = _now.AddSeconds(-1), ToUtc = _now.AddSeconds(1) });
        Assert.Single(events, e => e.Kind == "alert.opened");
        Assert.Single(events, e => e.Kind == "alert.escalated");
        Assert.Single(events, e => e.Kind == "alert.resolved");
    }

    [Fact]
    public async Task Configuration_event_captures_changed_fields_and_origin()
    {
        await using var history = new OperationalHistory(Options());
        var before = new ControlConfig();
        var after = before.Clone();
        after.GlobalPercent = 70;
        history.RecordConfigChange(before, after, "user");
        var change = Assert.Single(history.QueryEvents(new() { FromUtc = _now.AddSeconds(-1), ToUtc = _now.AddSeconds(1), Kind = "configuration.changed" }));
        Assert.Equal("user", change.Source);
        Assert.Contains("globalPercent", change.Data["fields"]);
        Assert.Equal("55", change.Data["before.globalPercent"]);
        Assert.Equal("70", change.Data["after.globalPercent"]);
    }

    [Fact]
    public async Task Exports_include_utc_unit_acquisition_time_and_explicit_gaps()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(Frame(0, 55));
        string csv = history.ExportCsv(Query(2));
        Assert.Contains("timestamp_utc,acquired_at_utc", csv);
        Assert.Contains("°C", csv);
        Assert.Contains("Unavailable", csv);
        using var json = JsonDocument.Parse(history.ExportJson(Query(2)));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("series").GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("series").GetProperty("points")[1].GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task Session_comparison_only_uses_the_selected_sessions()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(Frame(0, 40, session: "baseline"));
        history.RecordFrame(Frame(1, 60, session: "candidate"));
        var result = history.CompareSessions(Query(), "baseline", "candidate");
        Assert.Equal(40, result.Baseline.Average);
        Assert.Equal(60, result.Candidate.Average);
        Assert.Equal(20, result.AverageDifference);
    }

    [Fact]
    public async Task Snapshot_mapping_keeps_sensor_acquisition_time_and_unavailable_fan_rpm()
    {
        await using var history = new OperationalHistory(Options());
        var acquisition = _now.AddSeconds(-5);
        history.RecordSnapshot(new()
        {
            Timestamp = _now.LocalDateTime, CpuSensorAvailable = true, CpuTemp = 90,
            TelemetryHealth = [new() { Key = "cpu.temp", Source = "CPU package", Validity = ReadingValidity.Stale, LastSuccessUtc = acquisition }],
            Fans = [new() { Key = "pump-1", IsPump = true, Model = LinkDeviceModel.LiquidCoolerTitanSeries.ToString(),
                Rpm = 2500, RpmAvailable = false, RpmValidity = ReadingValidity.Stale, RpmLastSuccessUtc = acquisition }]
        });
        var cpu = history.Query(Query());
        Assert.Null(cpu.Points[0].Value);
        Assert.Equal(acquisition, cpu.Points[0].AcquiredAtUtc);
        var query = Query(); query.MetricKey = "pump.rpm"; query.DeviceId = "pump-1";
        Assert.Null(history.Query(query).Points[0].Value);
    }

    [Fact]
    public async Task Sparse_resource_metrics_use_their_actual_cadence_for_coverage_and_buckets()
    {
        await using var history = new OperationalHistory(Options());
        for (int i = 0; i < 2; i++) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(i * 30),
            Values = [new("engine.memory.private", "B", 1000 + i, _now.AddSeconds(i * 30), ExpectedIntervalSeconds: 30)]
        });
        var query = Query(60); query.MetricKey = "engine.memory.private";
        var series = history.Query(query);
        Assert.Equal(30, series.ExpectedIntervalSeconds);
        Assert.Equal(30, series.BucketSeconds);
        Assert.Equal(2, series.Statistics.ExpectedSamples);
        Assert.Equal(100, series.Statistics.CoveragePercent);
        Assert.Equal(2, series.Points.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Prior_acquisition_supports_the_window_without_becoming_an_in_window_sample(bool persistent)
    {
        await using (var recorded = new OperationalHistory(Options(persistent)))
        {
            recorded.RecordFrame(new()
            {
                TimestampUtc = _now.AddSeconds(-4),
                Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-5), ExpectedIntervalSeconds: 30)]
            });
            recorded.RecordFrame(new()
            {
                TimestampUtc = _now.AddSeconds(26),
                Values = [new("engine.memory.private", "B", 1000, _now.AddSeconds(25), ExpectedIntervalSeconds: 30)]
            });
            if (!persistent) AssertContext(recorded);
            await recorded.FlushAsync();
        }
        if (persistent)
        {
            await using var restored = new OperationalHistory(Options(true));
            AssertContext(restored);
        }

        void AssertContext(OperationalHistory history)
        {
            var query = Query(40); query.MetricKey = "engine.memory.private"; query.Threshold = 5000;
            var series = history.Query(query);
            Assert.Equal(40, series.Statistics.ObservedSeconds);
            Assert.Equal(100, series.Statistics.CoveragePercent);
            Assert.Equal(25, series.Statistics.SecondsAboveThreshold);
            Assert.Equal(1, series.Statistics.TotalSamples);
            Assert.Equal(1, series.Statistics.ValidSamples);
            Assert.Equal(1000, series.Statistics.Min);
            Assert.Equal(1000, series.Statistics.Max);
            Assert.Equal(1000, series.Statistics.Average);
            Assert.Equal(1000, series.Statistics.P95);
            Assert.Equal(1, series.Points.Sum(p => p.TotalSamples));
            Assert.Null(series.Points[1].Value);
        }
    }

    [Fact]
    public async Task Short_resource_window_can_have_contextual_coverage_without_new_measurements()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(-4),
            Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-5), ExpectedIntervalSeconds: 30)]
        });
        var query = Query(10); query.MetricKey = "engine.memory.private";

        var series = history.Query(query);

        Assert.Equal(30, series.ExpectedIntervalSeconds);
        Assert.Equal("B", series.Unit);
        Assert.Equal(10, series.Statistics.ObservedSeconds);
        Assert.Equal(100, series.Statistics.CoveragePercent);
        Assert.Equal(0, series.Statistics.TotalSamples);
        Assert.Equal(0, series.Statistics.ValidSamples);
        Assert.Null(series.Statistics.Average);
        Assert.Null(series.Statistics.P95);
        Assert.All(series.Points, p => { Assert.Null(p.Value); Assert.Equal(0, p.TotalSamples); });
    }

    [Fact]
    public async Task Expired_context_does_not_cover_a_short_resource_window()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(-29),
            Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-31), ExpectedIntervalSeconds: 30)]
        });
        var query = Query(10); query.MetricKey = "engine.memory.private";

        var series = history.Query(query);

        Assert.Equal(0, series.Statistics.ObservedSeconds);
        Assert.Equal(0, series.Statistics.CoveragePercent);
        Assert.Equal(0, series.Statistics.ValidSamples);
    }

    [Fact]
    public async Task A_failure_before_the_window_invalidates_contextual_support()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(-4),
            Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-5), ExpectedIntervalSeconds: 30)]
        });
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(-1),
            Values = [new("engine.memory.private", "B", null, _now.AddSeconds(-5), ReadingValidity.Unavailable, ExpectedIntervalSeconds: 30)]
        });
        var query = Query(10); query.MetricKey = "engine.memory.private";

        var series = history.Query(query);

        Assert.Equal(0, series.Statistics.ObservedSeconds);
        Assert.Equal(0, series.Statistics.CoveragePercent);
        Assert.Equal(0, series.Statistics.TotalSamples);
    }

    [Fact]
    public async Task Context_preserves_the_gap_before_the_next_acquisition()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(-19),
            Values = [new("engine.memory.private", "B", 9000, _now.AddSeconds(-20), ExpectedIntervalSeconds: 30)]
        });
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(20.5),
            Values = [new("engine.memory.private", "B", 1000, _now.AddSeconds(20), ExpectedIntervalSeconds: 30)]
        });
        var query = Query(40); query.MetricKey = "engine.memory.private";

        var series = history.Query(query);

        Assert.Equal(30, series.Statistics.ObservedSeconds);
        Assert.Equal(75, series.Statistics.CoveragePercent);
        Assert.Equal(1, series.Statistics.TotalSamples);
        Assert.Equal(1000, series.Statistics.Average);
    }

    [Fact]
    public async Task Repeated_publication_of_a_cached_acquisition_does_not_count_as_new_readings()
    {
        await using var history = new OperationalHistory(Options());
        for (int i = 0; i < 5; i++) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(i),
            Values = [new("volume.used", "%", 50, _now, ExpectedIntervalSeconds: 2)]
        });
        var query = Query(5); query.MetricKey = "volume.used";
        var series = history.Query(query);
        Assert.Equal(1, series.Statistics.ValidSamples);
        Assert.Equal(2, series.Statistics.ObservedSeconds);
        Assert.Equal(40, series.Statistics.CoveragePercent);
    }

    [Theory]
    [InlineData(1, .5, 4)]
    [InlineData(30, 10, 2)]
    public async Task Delayed_publication_preserves_the_full_acquisition_cadence(double cadence, double delay, int samples)
    {
        await using var history = new OperationalHistory(Options());
        for (int i = 0; i < samples; i++) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(i * cadence + delay),
            Values = [new("cpu.temp", "°C", 70 + i, _now.AddSeconds(i * cadence), ExpectedIntervalSeconds: cadence)]
        });
        var query = Query(); query.ToUtc = _now.AddSeconds(samples * cadence);

        var series = history.Query(query);

        Assert.Equal(samples, series.Statistics.ValidSamples);
        Assert.Equal(cadence, series.ExpectedIntervalSeconds);
        Assert.Equal(samples * cadence, series.Statistics.ObservedSeconds);
        Assert.Equal(100, series.Statistics.CoveragePercent);
        Assert.Equal(_now, series.Points[0].AcquiredAtUtc);
    }

    [Fact]
    public async Task Delayed_cached_publications_cannot_extend_an_expired_acquisition()
    {
        await using var history = new OperationalHistory(Options());
        for (int i = 0; i < 5; i++) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(i + .5),
            Values = [new("cpu.temp", "°C", 80, _now, ExpectedIntervalSeconds: 2)]
        });
        var query = Query(5); query.Threshold = 75;

        var series = history.Query(query);

        Assert.Equal(1, series.Statistics.ValidSamples);
        Assert.Equal(2, series.Statistics.ObservedSeconds);
        Assert.Equal(2, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(40, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task Acquisition_gaps_remain_uncovered_despite_publication_delay()
    {
        await using var history = new OperationalHistory(Options());
        foreach (int second in new[] { 0, 1, 4 }) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(second + .5),
            Values = [new("cpu.temp", "°C", second == 4 ? 80 : 50, _now.AddSeconds(second))]
        });
        var query = Query(6); query.Threshold = 75;

        var series = history.Query(query);

        Assert.Null(series.Points[2].Value);
        Assert.Null(series.Points[3].Value);
        Assert.Equal(3, series.Statistics.ObservedSeconds);
        Assert.Equal(1, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(50, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task A_failed_read_ends_coverage_and_a_cached_acquisition_cannot_resume_it()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(.5),
            Values = [new("cpu.temp", "°C", 80, _now, ExpectedIntervalSeconds: 2)]
        });
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(1.25),
            Values = [new("cpu.temp", "°C", null, _now, ReadingValidity.Unavailable, ExpectedIntervalSeconds: 2)]
        });
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(1.5),
            Values = [new("cpu.temp", "°C", 80, _now, ExpectedIntervalSeconds: 2)]
        });
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(2.5),
            Values = [new("cpu.temp", "°C", 50, _now.AddSeconds(2), ExpectedIntervalSeconds: 2)]
        });
        var query = Query(4); query.Threshold = 75;

        var series = history.Query(query);

        Assert.Equal(2, series.Statistics.ValidSamples);
        Assert.Equal(3.25, series.Statistics.ObservedSeconds);
        Assert.Equal(1.25, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(81.25, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task New_acquisitions_replace_previous_values_without_overlapping_threshold_duration()
    {
        await using var history = new OperationalHistory(Options());
        for (int i = 0; i < 3; i++) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(i * .75 + .4),
            Values = [new("cpu.temp", "°C", i == 1 ? 80 : 50, _now.AddSeconds(i * .75))]
        });
        var query = Query(3); query.Threshold = 75;

        var series = history.Query(query);

        Assert.Equal(2.5, series.Statistics.ObservedSeconds);
        Assert.Equal(.75, series.Statistics.SecondsAboveThreshold);
        Assert.InRange(series.Statistics.CoveragePercent, 83.33, 83.34);
    }

    [Fact]
    public async Task Coverage_clips_acquisition_intervals_to_short_query_windows()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(.25),
            Values = [new("cpu.temp", "°C", 80, _now.AddSeconds(-.25))]
        });
        var query = Query(); query.ToUtc = _now.AddSeconds(.5); query.Threshold = 75;

        var series = history.Query(query);

        Assert.Equal(.5, series.Statistics.ObservedSeconds);
        Assert.Equal(.5, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(100, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task Concurrent_devices_and_sessions_do_not_multiply_elapsed_coverage()
    {
        await using var history = new OperationalHistory(Options());
        foreach (string session in new[] { "first", "second" }) history.RecordFrame(new()
        {
            TimestampUtc = _now.AddSeconds(.5), SessionId = session,
            Values = [new("fan.rpm", "RPM", 1000, _now, DeviceId: "fan-1"),
                new("fan.rpm", "RPM", 1200, _now, DeviceId: "fan-2")]
        });
        var query = Query(2); query.MetricKey = "fan.rpm"; query.Threshold = 1100;

        var series = history.Query(query);

        Assert.Equal(4, series.Statistics.ValidSamples);
        Assert.Equal(1, series.Statistics.ObservedSeconds);
        Assert.Equal(1, series.Statistics.SecondsAboveThreshold);
        Assert.Equal(50, series.Statistics.CoveragePercent);
    }

    [Fact]
    public async Task Disk_aggregate_does_not_claim_complete_data_when_a_tracked_disk_is_missing()
    {
        await using var history = new OperationalHistory(Options());
        history.RecordSnapshot(new()
        {
            Timestamp = _now.LocalDateTime,
            Disks = [new() { Index = 0, ReadBytesPerSec = 100, WriteBytesPerSec = 50, ActivityPercent = 10, SampledAtUtc = _now }],
            TelemetryHealth = [new() { Key = "disk:0", Validity = ReadingValidity.Fresh, LastSuccessUtc = _now },
                new() { Key = "disk:1", Validity = ReadingValidity.Stale, LastSuccessUtc = _now.AddSeconds(-20) }]
        });
        var query = Query(); query.MetricKey = "disk.read";
        Assert.Null(history.Query(query).Points[0].Value);
        query.MetricKey = "disk.device.read"; query.DeviceId = "disk:0";
        Assert.Equal(100, history.Query(query).Points[0].Value);
    }

    [Fact]
    public async Task Metrics_publish_read_outcomes_and_operation_durations()
    {
        long readCount = 0;
        var durations = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "LinkFlow.Engine") current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "linkflow.sensor.reads") Interlocked.Add(ref readCount, measurement);
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "linkflow.engine.operation.duration") lock (durations) durations.Add(measurement);
        });
        listener.Start();
        await using var history = new OperationalHistory(Options());
        history.Metrics.Read("cpu.temp", true);
        history.Metrics.Duration("hub.read", TimeSpan.FromMilliseconds(20));
        Assert.True(readCount > 0);
        lock (durations) Assert.Contains(.02, durations);
    }

    [Fact]
    public async Task Reemitted_command_state_for_a_save_update_does_not_double_count_outcomes_or_latency()
    {
        long applied = 0;
        int durations = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "LinkFlow.Engine") current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name != "linkflow.control.commands") return;
            bool matches = false;
            foreach (var tag in tags) if (tag.Key == "kind" && Equals(tag.Value, "unique-metric-test")) matches = true;
            if (matches) Interlocked.Add(ref applied, measurement);
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name != "linkflow.engine.operation.duration") return;
            foreach (var tag in tags) if (tag.Key == "operation" && Equals(tag.Value, "command.unique-metric-test")) Interlocked.Increment(ref durations);
        });
        listener.Start();
        await using var history = new OperationalHistory(Options());
        OperationalEvent Event(string persistence) => new()
        {
            TimestampUtc = _now, Kind = "command.applied", Data = new()
            {
                ["commandId"] = "one-command", ["kind"] = "unique-metric-test", ["persistence"] = persistence,
                ["requestedAtUtc"] = _now.AddMilliseconds(-50).ToString("O", CultureInfo.InvariantCulture)
            }
        };
        history.RecordEvent(Event("Pending"));
        history.RecordEvent(Event("Saved"));
        Assert.Equal(1, applied);
        Assert.Equal(1, durations);
        Assert.Equal(2, history.QueryEvents(new() { FromUtc = _now.AddSeconds(-1), ToUtc = _now.AddSeconds(1), Kind = "command.applied" }).Count);
    }

    [Fact]
    public async Task Engine_collects_history_without_a_connected_or_visible_interface()
    {
        var factory = new SimulatedHardwareFactory();
        factory.Sensors.OpenDelay = TimeSpan.Zero;
        factory.Sensors.ForcedCpuTemp = 56;
        using var engine = new LinkFlowEngine(factory, new EngineOptions
        {
            ConfigPath = null,
            SnapshotInterval = TimeSpan.FromMilliseconds(30),
            TelemetryInterval = TimeSpan.FromMilliseconds(40),
            History = new() { DirectoryPath = _directory, SampleInterval = TimeSpan.FromMilliseconds(30) }
        });
        // Intentionally no SnapshotUpdated subscription: collection belongs to the engine.
        engine.Start();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        var query = new HistoryQuery { MetricKey = "cpu.temp", FromUtc = _now.AddSeconds(-1), ToUtc = _now.AddSeconds(10) };
        while (engine.History.Query(query).Statistics.ValidSamples < 2 && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(engine.History.Query(query).Statistics.ValidSamples >= 2);
        await engine.History.FlushAsync();
        Assert.True(Directory.GetFiles(_directory, "lfh-*.jsonl").Length > 0);
        await engine.StopAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
