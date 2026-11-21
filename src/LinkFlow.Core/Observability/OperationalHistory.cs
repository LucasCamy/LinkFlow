using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace LinkFlow.Core;

/// <summary>
/// Service-owned history. Producers only copy bounded records and TryWrite to a bounded queue;
/// filesystem work runs on a separate worker. Queries merge persisted segments with the current memory tail.
/// </summary>
public sealed partial class OperationalHistory : IAsyncDisposable
{
    private sealed record DiskRecord(string Type, TelemetryFrame? Frame = null, OperationalEvent? Event = null);
    private sealed record WriteItem(DiskRecord? Record, TaskCompletionSource? Barrier = null);
    private const int MaxRecordChars = 256 * 1024;
    private readonly OperationalHistoryOptions _options;
    private readonly object _gate = new();
    private readonly List<TelemetryFrame> _frames = [];
    private readonly List<OperationalEvent> _events = [];
    private readonly Dictionary<string, EngineAlert> _activeAlerts = new();
    private readonly HashSet<string> _seenAlertEvents = new();
    private readonly Dictionary<string, DateTimeOffset> _lastReadAttempts = new();
    private readonly HashSet<string> _seenCorruptLines = new();
    private readonly HashSet<string> _seenCommandMetrics = new();
    private readonly Queue<string> _commandMetricOrder = new();
    private readonly Dictionary<string, bool> _devices = new();
    private readonly Dictionary<string, DateTimeOffset> _disconnectedAt = new();
    private readonly Channel<WriteItem> _writes;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _dispose = new();
    private StreamWriter? _writer;
    private string? _writerPath;
    private DateTimeOffset _lastFrameUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastResourceUtc = DateTimeOffset.MinValue;
    private TimeSpan _previousCpu;
    private DateTimeOffset _previousCpuAt;
    private int _segment;
    private long _droppedWrites, _corruptRecords, _diskBytes;
    private DateTimeOffset? _lastWriteUtc;
    private string? _lastError;
    private bool _storageAvailable = true;
    private int _disposed;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public OperationalMetrics Metrics { get; } = new();
    public event Action<HistoryStorageStatus>? StorageChanged;
    private DateTimeOffset Now => _options.TimeProvider.GetUtcNow();

    public OperationalHistory(OperationalHistoryOptions? options = null)
    {
        _options = (options ?? new()).Validated();
        _writes = Channel.CreateBounded<WriteItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        if (_options.DirectoryPath != null)
        {
            try
            {
                EnsureSafeDirectory();
                LoadSessionMetadata();
                LoadInitialQueryCache();
                PruneFiles();
            }
            catch (Exception ex) when (IsStorageError(ex)) { StorageFailure(ex); }
        }
        _writerTask = Task.Run(WriteLoopAsync);
        RecordEvent(new OperationalEvent
        {
            Kind = "engine.started", Message = "Engine session started", TimestampUtc = Now,
            Data = new() { ["version"] = typeof(OperationalHistory).Assembly.GetName().Version?.ToString() ?? "unknown" }
        });
    }

    public HistoryStorageStatus Status
    {
        get
        {
            lock (_gate) return new()
            {
                PersistenceEnabled = _options.DirectoryPath != null,
                StorageAvailable = _storageAvailable && _sessionMetadataAvailable,
                LastWriteUtc = _lastWriteUtc,
                LastError = _lastError ?? _sessionMetadataError,
                DroppedWrites = Interlocked.Read(ref _droppedWrites),
                CorruptRecords = Interlocked.Read(ref _corruptRecords),
                DiskBytes = Interlocked.Read(ref _diskBytes),
                StoredFrames = _frames.Count,
                StoredEvents = _events.Count,
                SessionId = SessionId,
                SessionMetadataAvailable = _sessionMetadataAvailable,
                SessionMetadataError = _sessionMetadataError,
                SessionMetadataBytes = _sessionMetadataBytes,
                SessionsWithUnknownStart = _sessions.Values.Count(s => s.StartedUtc == null),
                Queries = QueryStatistics
            };
        }
    }

    public void RecordSnapshot(EngineSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var when = ToUtc(snapshot.Timestamp);
        ObserveSnapshotEvents(snapshot, when);
        lock (_gate)
        {
            if (when < _lastFrameUtc || when - _lastFrameUtc < _options.SampleInterval) return;
            _lastFrameUtc = when;
        }
        var values = SnapshotMetrics(snapshot, when);
        RecordFrame(new TelemetryFrame { TimestampUtc = when, Values = values });
    }

    public void RecordFrame(TelemetryFrame frame)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var values = frame.Values.Where(v => !string.IsNullOrWhiteSpace(v.Key)).Take(256).Select(v => v with
        {
            Key = Limit(v.Key, 160), Unit = Limit(v.Unit, 32), Source = Limit(v.Source, 160),
            DeviceId = v.DeviceId == null ? null : Limit(v.DeviceId, 160),
            AcquiredAtUtc = v.AcquiredAtUtc?.ToUniversalTime(),
            ExpectedIntervalSeconds = v.ExpectedIntervalSeconds is { } interval && double.IsFinite(interval) ? Math.Clamp(interval, .02, 86400) : DefaultMetricCadence(v.Key),
            Value = v.Value is { } number && double.IsFinite(number) ? number : null,
            Validity = v.Value is { } n && !double.IsFinite(n) || !Enum.IsDefined(v.Validity) ? ReadingValidity.Invalid
                : v.Value == null && v.Validity == ReadingValidity.Fresh ? ReadingValidity.Unavailable : v.Validity
        }).ToList();
        var copy = frame with
        {
            Id = string.IsNullOrWhiteSpace(frame.Id) ? Guid.NewGuid().ToString("N") : Limit(frame.Id, 80),
            SessionId = string.IsNullOrWhiteSpace(frame.SessionId) ? SessionId : Limit(frame.SessionId, 80),
            TimestampUtc = frame.TimestampUtc.ToUniversalTime(), Values = values
        };
        if (!ValidFrame(copy)) throw new ArgumentException("History frame has an invalid acquisition interval.", nameof(frame));
        lock (_gate) { _frames.Add(copy); ObserveFrameCadence(copy); ObserveSessionFrame(copy); TrimMemory(); }
        Enqueue(new("frame", copy));
    }

    public void RecordEvent(OperationalEvent entry)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var copy = entry with
        {
            Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : Limit(entry.Id, 80),
            TimestampUtc = entry.TimestampUtc.ToUniversalTime(),
            SessionId = string.IsNullOrWhiteSpace(entry.SessionId) ? SessionId : Limit(entry.SessionId, 80),
            Kind = Limit(entry.Kind, 100), Source = Limit(entry.Source, 160),
            DeviceId = entry.DeviceId == null ? null : Limit(entry.DeviceId, 160),
            Message = Limit(entry.Message, 2048),
            Data = entry.Data.Take(24).Select(kv => new KeyValuePair<string, string>(Limit(kv.Key, 100), Limit(kv.Value, 2048)))
                .DistinctBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value)
        };
        lock (_gate) { _events.Add(copy); ObserveSessionEvent(copy); TrimMemory(); }
        if (copy.Kind.StartsWith("command.", StringComparison.Ordinal))
        {
            string state = copy.Kind[8..], command = copy.Data.GetValueOrDefault("commandId", copy.Id);
            bool first;
            lock (_gate)
            {
                string key = command + "|" + state;
                first = _seenCommandMetrics.Add(key);
                if (first)
                {
                    _commandMetricOrder.Enqueue(key);
                    while (_commandMetricOrder.Count > 2048) _seenCommandMetrics.Remove(_commandMetricOrder.Dequeue());
                }
            }
            if (first)
            {
                string kind = copy.Data.GetValueOrDefault("kind", "control");
                Metrics.Command(kind, state);
                if (Enum.TryParse<CommandState>(state, true, out var status) && status is not (CommandState.Pending or CommandState.Accepted) &&
                    DateTimeOffset.TryParse(copy.Data.GetValueOrDefault("requestedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var requested))
                    Metrics.Duration("command." + kind, copy.TimestampUtc - requested);
            }
        }
        Enqueue(new("event", Event: copy));
    }

    public void RecordConfigChange(ControlConfig before, ControlConfig after, string origin)
    {
        var a = JsonSerializer.SerializeToNode(before, Json.Compact)?.AsObject();
        var b = JsonSerializer.SerializeToNode(after, Json.Compact)?.AsObject();
        if (a == null || b == null) return;
        var changed = b.Where(kv => !JsonNode.DeepEquals(kv.Value, a[kv.Key])).Select(kv => kv.Key).Take(24).ToArray();
        if (changed.Length == 0) return;
        var data = new Dictionary<string, string> { ["fields"] = string.Join(", ", changed) };
        foreach (var key in changed.Take(10))
        {
            data[$"before.{key}"] = Limit(a[key]?.ToJsonString(Json.Compact) ?? "null", 512);
            data[$"after.{key}"] = Limit(b[key]?.ToJsonString(Json.Compact) ?? "null", 512);
        }
        RecordEvent(new() { Kind = "configuration.changed", TimestampUtc = Now, Source = origin, Message = "Configuration changed", Data = data });
    }

    public void RecordAlert(EngineAlert alert)
    {
        string occurrence = $"{alert.Key}|{alert.Since.Ticks}|{alert.Severity}";
        bool first, escalated;
        lock (_gate)
        {
            if (!_seenAlertEvents.Add(occurrence)) return;
            first = !_activeAlerts.TryGetValue(alert.Key, out var previous);
            escalated = previous != null && alert.Severity > previous.Severity;
            _activeAlerts[alert.Key] = new EngineAlert { Key = alert.Key, Since = alert.Since, Severity = alert.Severity, Message = alert.Message };
            if (_seenAlertEvents.Count > 4000) _seenAlertEvents.Clear();
        }
        RecordEvent(new()
        {
            Kind = escalated ? "alert.escalated" : first ? "alert.opened" : "alert.changed",
            TimestampUtc = Now, Severity = alert.Severity, Message = alert.Message,
            Data = new() { ["key"] = alert.Key, ["sinceUtc"] = ToUtc(alert.Since).ToString("O") }
        });
    }

    public void RecordAlertResolution(string key)
    {
        EngineAlert? previous;
        lock (_gate)
        {
            if (!_activeAlerts.Remove(key, out previous)) return;
        }
        RecordEvent(new()
        {
            Kind = "alert.resolved", TimestampUtc = Now, Severity = AlertSeverity.Info,
            Message = previous.Message,
            Data = new() { ["key"] = key, ["durationSeconds"] = Math.Max(0, (Now - ToUtc(previous.Since)).TotalSeconds).ToString("F3", CultureInfo.InvariantCulture) }
        });
    }

    private HistorySeries QueryCore(HistoryQuery query, QueryWork work)
    {
        ArgumentNullException.ThrowIfNull(query);
        var from = query.FromUtc.ToUniversalTime();
        var to = query.ToUtc.ToUniversalTime();
        if (to < from || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("History range must be between zero and 366 days.", nameof(query));
        if (string.IsNullOrWhiteSpace(query.MetricKey) || query.MetricKey.Length > 160) throw new ArgumentException("Invalid metric key.", nameof(query));
        IEnumerable<(TelemetryFrame Frame, MetricObservation Value)> SelectObservations(IEnumerable<TelemetryFrame> frames) =>
            frames.Where(f => query.SessionId == null || f.SessionId == query.SessionId)
                .SelectMany(f => f.Values.Where(v => v.Key == query.MetricKey && (query.DeviceId == null || v.DeviceId == query.DeviceId))
                    .Select(v => (Frame: f, Value: v)));
        var frames = ReadQueryFrames(query, from, to, work);
        var allObservations = SelectObservations(frames).ToList();
        var observations = allObservations.Where(x => x.Frame.TimestampUtc >= from).ToList();
        double cadence = observations.Select(x => ObservationCadence(x.Value)).DefaultIfEmpty(DefaultMetricCadence(query.MetricKey)).Max();
        // The last acquisition before From can still support the beginning of the window. Keep
        // that bounded context only for elapsed coverage; it is not a new sample in this query.
        var coverageObservations = allObservations;
        int max = Math.Clamp(query.MaxPoints, 1, 4000);
        int bucket = Math.Max(Math.Max((int)Math.Ceiling(cadence), Math.Clamp(query.BucketSeconds, 1, 86400)),
            (int)Math.Ceiling(Math.Max(1, (to - from).TotalSeconds) / max));
        int count = Math.Min(max, Math.Max(1, (int)Math.Ceiling((to - from).TotalSeconds / bucket)));
        var groups = observations.GroupBy(x => Math.Clamp((int)((x.Frame.TimestampUtc - from).TotalSeconds / bucket), 0, count - 1))
            .ToDictionary(g => g.Key, g => g.ToList());
        var points = new List<HistoryPoint>(count);
        for (int i = 0; i < count; i++)
        {
            var time = from.AddSeconds((long)i * bucket);
            if (!groups.TryGetValue(i, out var group)) { points.Add(new() { TimestampUtc = time }); continue; }
            var good = group.Where(x => IsValid(x.Value)).DistinctBy(x => (x.Frame.SessionId, x.Value.DeviceId, x.Value.AcquiredAtUtc ?? x.Frame.TimestampUtc)).ToList();
            var last = group[^1];
            points.Add(new()
            {
                TimestampUtc = time,
                AcquiredAtUtc = good.Count > 0 ? good[^1].Value.AcquiredAtUtc : last.Value.AcquiredAtUtc,
                Value = good.Count > 0 ? good.Average(x => x.Value.Value!.Value) : null,
                Min = good.Count > 0 ? good.Min(x => x.Value.Value!.Value) : null,
                Max = good.Count > 0 ? good.Max(x => x.Value.Value!.Value) : null,
                ValidSamples = good.Count, TotalSamples = group.Count,
                Validity = good.Count > 0 ? ReadingValidity.Fresh : last.Value.Validity,
                Source = string.Join("; ", group.Select(x => x.Value.Source).Distinct().Take(4)),
                SessionId = group.Select(x => x.Frame.SessionId).Distinct().Take(2).Count() == 1 ? last.Frame.SessionId : null
            });
        }
        var valid = observations.Where(x => IsValid(x.Value)).DistinctBy(x => (x.Frame.SessionId, x.Value.DeviceId, x.Value.AcquiredAtUtc ?? x.Frame.TimestampUtc)).ToList();
        var sorted = valid.Select(x => x.Value.Value!.Value).Order().ToArray();
        var (observedSeconds, hotSeconds) = MeasureCoverage(coverageObservations, from, to, query.Threshold);
        double rangeSeconds = (to - from).TotalSeconds;
        int expected = Math.Max(1, (int)Math.Ceiling((to - from).TotalSeconds / cadence));
        return new()
        {
            MetricKey = query.MetricKey, DeviceId = query.DeviceId, Unit = observations.FirstOrDefault().Value?.Unit ?? coverageObservations.FirstOrDefault().Value?.Unit ?? string.Empty,
            FromUtc = from, ToUtc = to, BucketSeconds = bucket, ExpectedIntervalSeconds = cadence, Points = points,
            Statistics = new()
            {
                TotalSamples = observations.Count, ValidSamples = valid.Count, ExpectedSamples = expected,
                CoveragePercent = rangeSeconds > 0 ? Math.Clamp(observedSeconds / rangeSeconds * 100, 0, 100) : 0,
                Min = sorted.Length > 0 ? sorted[0] : null, Max = sorted.Length > 0 ? sorted[^1] : null,
                Average = sorted.Length > 0 ? sorted.Average() : null,
                P95 = sorted.Length > 0 ? sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * .95) - 1, 0, sorted.Length - 1)] : null,
                ObservedSeconds = observedSeconds, SecondsAboveThreshold = hotSeconds, Threshold = query.Threshold
            }
        };
    }

    private (double ObservedSeconds, double HotSeconds) MeasureCoverage(
        IReadOnlyList<(TelemetryFrame Frame, MetricObservation Value)> observations,
        DateTimeOffset from, DateTimeOffset to, double? threshold)
    {
        var intervals = new List<(DateTimeOffset Start, DateTimeOffset End, bool Hot)>();
        foreach (var stream in observations.GroupBy(x => (x.Frame.SessionId, x.Value.DeviceId)))
        {
            var acquisitions = new HashSet<DateTimeOffset>();
            var validAfter = from;
            int active = -1;
            foreach (var observation in stream)
            {
                if (!IsValid(observation.Value))
                {
                    // An explicit failed/stale observation ends the previous acquisition's support.
                    EndActiveAt(observation.Frame.TimestampUtc);
                    if (observation.Frame.TimestampUtc > validAfter) validAfter = observation.Frame.TimestampUtc;
                    active = -1;
                    continue;
                }

                var acquired = observation.Value.AcquiredAtUtc ?? observation.Frame.TimestampUtc;
                if (!acquisitions.Add(acquired)) continue;
                EndActiveAt(acquired);
                active = -1;
                var start = acquired > validAfter ? acquired : validAfter;
                // Invalid persisted intervals are rejected before materialization. This non-saturating
                // UTC addition also keeps offset-local year boundaries from fabricating duration.
                var end = acquired.ToUniversalTime().AddSeconds(ObservationCadence(observation.Value));
                if (end > to) end = to;
                if (end <= start) continue;
                intervals.Add((start, end, threshold is { } limit && observation.Value.Value >= limit));
                active = intervals.Count - 1;
            }

            void EndActiveAt(DateTimeOffset boundary)
            {
                if (active < 0 || intervals[active].End <= boundary) return;
                var previous = intervals[active];
                intervals[active] = (previous.Start, boundary, previous.Hot);
            }
        }

        // Coverage follows acquisition time, not the delayed publication time. Union intervals so
        // cached publications and overlapping devices/sessions cannot count the same time twice.
        return (UnionSeconds(intervals), UnionSeconds(intervals.Where(x => x.Hot)));
    }

    private static double UnionSeconds(IEnumerable<(DateTimeOffset Start, DateTimeOffset End, bool Hot)> intervals)
    {
        DateTimeOffset? coveredUntil = null;
        double seconds = 0;
        foreach (var interval in intervals.OrderBy(x => x.Start))
        {
            var start = coveredUntil is { } previous && previous > interval.Start ? previous : interval.Start;
            if (interval.End <= start) continue;
            seconds += (interval.End - start).TotalSeconds;
            coveredUntil = interval.End;
        }
        return seconds;
    }

    public HistoryStatistics Summarize(HistoryQuery query, double? threshold = null) => Query(CopyQuery(query, query.SessionId, threshold ?? query.Threshold)).Statistics;

    public IReadOnlyList<OperationalEvent> QueryEvents(EventQuery query)
    {
        if (query.ToUtc < query.FromUtc || query.ToUtc - query.FromUtc > TimeSpan.FromDays(366)) throw new ArgumentException("Invalid event range.", nameof(query));
        var all = new Dictionary<string, OperationalEvent>(StringComparer.Ordinal);
        foreach (var record in ReadDisk(query.FromUtc, query.ToUtc, includeFrames: false))
            if (record.Event is { } e) all[e.Id] = e;
        lock (_gate)
            foreach (var e in _events) all[e.Id] = e;
        return all.Values.Where(e => e.TimestampUtc >= query.FromUtc && e.TimestampUtc <= query.ToUtc)
            .Where(e => query.Kind == null || e.Kind == query.Kind)
            .Where(e => query.Source == null || e.Source == query.Source)
            .Where(e => query.DeviceId == null || e.DeviceId == query.DeviceId)
            .OrderByDescending(e => e.TimestampUtc).Take(Math.Clamp(query.MaxItems, 1, 2000)).Select(CloneEvent).ToArray();
    }

    public IReadOnlyList<HistorySession> QuerySessions()
    {
        lock (_gate)
        {
            TrimSessions();
            // Older clients require a non-null StartedUtc. Omit unknown boundaries rather than
            // inventing a start from a surviving stop event; Status reports the omitted count.
            return _sessions.Values.Where(s => s.StartedUtc != null).OrderByDescending(s => s.StartedUtc)
                .Select(s => new HistorySession(s.Id, s.StartedUtc!.Value, s.StoppedUtc, s.Version)).ToArray();
        }
    }

    public SessionComparison CompareSessions(HistoryQuery query, string baselineId, string candidateId)
    {
        var baseline = Query(CopyQuery(query, baselineId, query.Threshold)).Statistics;
        var candidate = Query(CopyQuery(query, candidateId, query.Threshold)).Statistics;
        return new(baselineId, candidateId, baseline, candidate,
            baseline.Average != null && candidate.Average != null ? candidate.Average - baseline.Average : null,
            baseline.P95 != null && candidate.P95 != null ? candidate.P95 - baseline.P95 : null);
    }

    public string ExportCsv(HistoryQuery query)
    {
        var series = Query(query);
        var b = new StringBuilder("timestamp_utc,acquired_at_utc,metric,device_id,unit,value,min,max,validity,valid_samples,total_samples,session_id,source,expected_interval_seconds\r\n");
        foreach (var p in series.Points)
        {
            b.AppendJoin(',', Csv(p.TimestampUtc.ToString("O")), Csv(p.AcquiredAtUtc?.ToString("O")), Csv(series.MetricKey), Csv(series.DeviceId), Csv(series.Unit),
                Number(p.Value), Number(p.Min), Number(p.Max), Csv(p.Validity.ToString()), p.ValidSamples.ToString(CultureInfo.InvariantCulture),
                p.TotalSamples.ToString(CultureInfo.InvariantCulture), Csv(p.SessionId), Csv(p.Source), Number(series.ExpectedIntervalSeconds)).Append("\r\n");
        }
        return b.ToString();
    }

    public string ExportJson(HistoryQuery query, EventQuery? events = null) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, exportedAtUtc = Now, series = Query(query),
        events = events == null ? [] : QueryEvents(events), status = Status
    }, new JsonSerializerOptions(Json.Compact) { DefaultIgnoreCondition = JsonIgnoreCondition.Never });

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_options.DirectoryPath == null || _writerTask.IsCompleted) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writes.Writer.WriteAsync(new(null, completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        RecordEvent(new() { Kind = "engine.stopped", Message = "Engine session stopped", TimestampUtc = Now });
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writes.Writer.TryComplete();
        try { await _writerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { _dispose.Cancel(); }
        Metrics.Dispose();
        _dispose.Dispose();
    }

    private void Enqueue(DiskRecord record)
    {
        if (_options.DirectoryPath == null) return;
        if (_writes.Writer.TryWrite(new(record))) return;
        Interlocked.Increment(ref _droppedWrites);
        Metrics.Dropped();
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var item in _writes.Reader.ReadAllAsync(_dispose.Token).ConfigureAwait(false))
            {
                if (item.Barrier != null)
                {
                    Exception? error = null;
                    BeginDiskMutation();
                    try
                    {
                        if (_writer != null) await _writer.FlushAsync(_dispose.Token).ConfigureAwait(false);
                        RememberOpenSegment();
                        await PersistSessionMetadataAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsStorageError(ex)) { error = ex; StorageFailure(ex); CloseWriter(); }
                    finally { EndDiskMutation(); }
                    if (error != null) item.Barrier.TrySetException(error); else item.Barrier.TrySetResult();
                    continue;
                }
                if (item.Record == null) continue;
                BeginDiskMutation();
                try
                {
                    var json = JsonSerializer.Serialize(item.Record, Json.Compact);
                    if (json.Length > MaxRecordChars) throw new InvalidDataException("History record exceeds the size limit.");
                    int bytes = Encoding.UTF8.GetByteCount(json) + 1;
                    if (bytes > _options.MaxSegmentBytes) throw new InvalidDataException("History record exceeds the configured segment budget.");
                    var when = item.Record.Frame?.TimestampUtc ?? item.Record.Event!.TimestampUtc;
                    await EnsureWriterAsync(when, bytes).ConfigureAwait(false);
                    await _writer!.WriteLineAsync(json.AsMemory(), _dispose.Token).ConfigureAwait(false);
                    await _writer.FlushAsync(_dispose.Token).ConfigureAwait(false);
                    lock (_gate) { _lastWriteUtc = Now; _storageAvailable = true; _lastError = null; }
                    Metrics.Storage(true);
                    RememberOpenSegment();
                    await PersistSessionMetadataAsync().ConfigureAwait(false);
                    PruneFiles();
                }
                catch (Exception ex) when (IsStorageError(ex)) { StorageFailure(ex); CloseWriter(); }
                finally { EndDiskMutation(); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            BeginDiskMutation();
            try { CloseWriter(); } finally { EndDiskMutation(); }
            while (_writes.Reader.TryRead(out var item)) item.Barrier?.TrySetCanceled();
        }
    }

    private async Task EnsureWriterAsync(DateTimeOffset when, int bytes)
    {
        string prefix = $"lfh-{when:yyyyMMdd}-";
        if (_writer != null && _writerPath != null && Path.GetFileName(_writerPath).StartsWith(prefix, StringComparison.Ordinal) &&
            _writer.BaseStream.Length + bytes <= _options.MaxSegmentBytes) return;
        if (_writer != null) await _writer.FlushAsync(_dispose.Token).ConfigureAwait(false);
        CloseWriter();
        EnsureSafeDirectory();
        var name = $"{prefix}{SessionId}-{Interlocked.Increment(ref _segment):D6}.jsonl";
        var path = Path.Combine(_options.DirectoryPath!, name);
        // Read attributes from this same handle for the cache proof, rather than trusting a path
        // which could have been replaced. Other processes still cannot write this open segment.
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 16384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        _writerPath = path;
    }

    private void EnsureSafeDirectory()
    {
        var path = _options.DirectoryPath!;
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("History directory is redirected.");
        Directory.CreateDirectory(path);
        if (SharedDataSecurity.IsReparsePoint(path)) throw new IOException("History directory is redirected.");
    }

    private void CloseWriter()
    {
        try { RememberOpenSegment(); }
        catch (Exception ex) when (IsStorageError(ex) || ex is ObjectDisposedException) { InvalidateQueryCache(); }
        try { _writer?.Dispose(); } catch (IOException) { }
        _writer = null;
        _writerPath = null;
    }

    private void LoadRecent(DateTimeOffset from, DateTimeOffset to)
    {
        foreach (var record in ReadDisk(from, to))
        {
            if (record.Frame != null) { _frames.Add(record.Frame); ObserveFrameCadence(record.Frame); ObserveSessionFrame(record.Frame); }
            if (record.Event != null) { _events.Add(record.Event); ObserveSessionEvent(record.Event); }
            if ((_frames.Count + _events.Count) % 128 == 0) TrimMemory();
        }
        lock (_gate) TrimMemory();
    }

    private IEnumerable<DiskRecord> ReadDisk(DateTimeOffset from, DateTimeOffset to, bool includeFrames = true)
    {
        long scanMutation = Volatile.Read(ref _diskMutationVersion);
        foreach (var path in DataFiles())
        {
            var date = FileDate(path);
            if (date != null && (date.Value > to.UtcDateTime.Date || date.Value < from.UtcDateTime.Date)) continue;
            List<DiskRecord> records = [];
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > _options.MaxDiskBytes) continue;
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                string? line;
                int index = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    index++;
                    try
                    {
                        if (line.Length == 0) continue;
                        if (!includeFrames && line.StartsWith("{\"type\":\"frame\"", StringComparison.Ordinal)) continue;
                        if (line.Length > MaxRecordChars) throw new JsonException("History record too large.");
                        var record = JsonSerializer.Deserialize<DiskRecord>(line, Json.Compact);
                        if (record is { Type: "frame", Frame: { } frame } && ValidFrame(frame)) records.Add(record);
                        else if (record is { Type: "event", Event: { } entry } && entry.Id is { Length: > 0 and <= 80 } &&
                            entry.Kind is { Length: > 0 and <= 100 } && entry.Source != null && entry.Message != null && entry.Data != null) records.Add(record);
                        else throw new JsonException("Invalid history record.");
                    }
                    catch (Exception ex) when (ex is JsonException or NotSupportedException or NullReferenceException)
                    {
                        Interlocked.Increment(ref _diskReadFailures);
                        if (path == _writerPath && reader.EndOfStream) continue; // concurrent partial append, memory tail covers it
                        lock (_gate)
                        {
                            if (_seenCorruptLines.Count < 4096 && _seenCorruptLines.Add($"{path}:{index}")) Interlocked.Increment(ref _corruptRecords);
                        }
                    }
                }
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                if (ex is not FileNotFoundException || !PrunedDuring(path, scanMutation))
                { Interlocked.Increment(ref _diskReadFailures); StorageFailure(ex); }
            }
            foreach (var record in records) yield return record;
        }
    }

    private string[] DataFiles(bool throwOnError = false)
    {
        if (_options.DirectoryPath == null || !Directory.Exists(_options.DirectoryPath)) return [];
        try
        {
            if (SharedDataSecurity.IsReparsePoint(_options.DirectoryPath)) throw new IOException("History directory is redirected.");
            long scanMutation = Volatile.Read(ref _diskMutationVersion);
            return Directory.GetFiles(_options.DirectoryPath, "lfh-*.jsonl")
                .Where(path =>
                {
                    try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0; }
                    catch (FileNotFoundException) when (PrunedDuring(path, scanMutation)) { return false; }
                })
                .Order(StringComparer.Ordinal).Take(20000).ToArray();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            if (throwOnError) throw;
            StorageFailure(ex); return [];
        }
    }

    private void PruneFiles()
    {
        var cutoff = (Now - _options.Retention).UtcDateTime.Date;
        // Session IDs are random, so filename order does not describe age across sessions.
        // Refresh filesystem metadata on every pass; deleted segments leave no cached entries.
        var files = DataFiles().Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal).ToList();
        long total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (file.FullName == _writerPath) continue;
            bool expired = FileDate(file.Name) is { } date && date < cutoff;
            if (!expired && total <= _options.MaxDiskBytes) continue;
            long attempt = 0, length = 0;
            bool removed = false;
            try
            {
                length = file.Length;
                attempt = BeginSegmentPrune(file.FullName);
                FileOperationProbe?.Invoke("prune-before-delete", file.FullName);
                DeleteSegment(file.FullName);
                removed = true;
                FileOperationProbe?.Invoke("prune-after-delete", file.FullName);
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                // Missing externally is a real storage change, not a successful deletion of ours.
                // Its bytes are nevertheless gone; do not evict another recent segment for them.
                if (!removed && (ex is FileNotFoundException or DirectoryNotFoundException)) total -= length;
                InvalidateQueryCache(ex);
                StorageFailure(ex);
            }
            finally
            {
                if (attempt != 0) CompleteSegmentPrune(file.FullName, attempt, removed);
                if (removed) total -= length;
            }
        }
        Interlocked.Exchange(ref _diskBytes, Math.Max(0, total));
    }

    private void TrimMemory()
    {
        var cutoff = Now - _options.Retention;
        _frames.RemoveAll(f => { if (f.TimestampUtc >= cutoff) return false; ObserveEvictedFrame(f); return true; });
        _events.RemoveAll(e => e.TimestampUtc < cutoff);
        if (_frames.Count > _options.MaxMemoryFrames)
        {
            int remove = _frames.Count - _options.MaxMemoryFrames;
            for (int i = 0; i < remove; i++) ObserveEvictedFrame(_frames[i]);
            _frames.RemoveRange(0, remove);
        }
        if (_events.Count > _options.MaxMemoryEvents) _events.RemoveRange(0, _events.Count - _options.MaxMemoryEvents);
    }

    private void StorageFailure(Exception ex)
    {
        lock (_gate) { _storageAvailable = false; _lastError = Limit(ex.Message, 512); }
        Metrics.Storage(false);
        try { StorageChanged?.Invoke(Status); } catch { }
    }

    private void ObserveSnapshotEvents(EngineSnapshot s, DateTimeOffset when)
    {
        foreach (var alert in s.Alerts) RecordAlert(alert);
        string[] resolved;
        lock (_gate) resolved = _activeAlerts.Keys.Except(s.Alerts.Select(a => a.Key), StringComparer.Ordinal).ToArray();
        foreach (var key in resolved) RecordAlertResolution(key);
        ObserveDevice("hub", s.HubConnected, when);
        ObserveDevice("lcd", s.LcdConnected, when);
        ObserveGpuRgbControl(s, when);
        ObserveDevice("rgb.motherboard", s.MotherboardRgbAvailable, when);
        ObserveDevice("rgb.ram", s.RamRgbAvailable, when);
        ObserveDevice("rgb.peripherals", s.PeripheralsRgbAvailable, when);
        foreach (var health in s.TelemetryHealth)
        {
            if (health.LastAttemptUtc is not { } attempt) continue;
            lock (_gate)
            {
                if (_lastReadAttempts.GetValueOrDefault(health.Key) == attempt) continue;
                _lastReadAttempts[health.Key] = attempt;
            }
            Metrics.Read(health.Key, health.Validity == ReadingValidity.Fresh);
        }
    }

    private void ObserveDevice(string key, bool connected, DateTimeOffset when)
    {
        bool? old;
        double? recovered = null;
        lock (_gate)
        {
            old = _devices.TryGetValue(key, out bool before) ? before : null;
            if (old == connected) return;
            _devices[key] = connected;
            // Only a loss after observed availability opens a recovery episode.
            if (!connected && old == true) _disconnectedAt.TryAdd(key, when);
            else if (connected && _disconnectedAt.Remove(key, out var lost)) recovered = Math.Max(0, (when - lost).TotalSeconds);
        }
        // Unsupported devices at startup are not disconnect incidents.
        if (old == null && !connected) return;
        var data = new Dictionary<string, string>();
        if (recovered is { } seconds) { data["recoverySeconds"] = seconds.ToString("F3", CultureInfo.InvariantCulture); Metrics.Recovery(key, seconds); }
        RecordEvent(new() { TimestampUtc = when, DeviceId = key, Kind = connected ? recovered == null ? "device.discovered" : "device.recovered" : "device.lost",
            Severity = connected ? AlertSeverity.Info : AlertSeverity.Warning, Message = connected ? "Device available" : "Device unavailable", Data = data });
    }

    private List<MetricObservation> SnapshotMetrics(EngineSnapshot s, DateTimeOffset when)
    {
        var health = s.TelemetryHealth.GroupBy(h => h.Key).ToDictionary(g => g.Key, g => g.Last());
        var values = new List<MetricObservation>();
        void Add(string key, string unit, double? value, string? healthKey = null, string? device = null, double? expectedInterval = null)
        {
            var h = health.GetValueOrDefault(healthKey ?? key);
            var validity = h?.Validity ?? (value != null ? ReadingValidity.Fresh : ReadingValidity.Unavailable);
            values.Add(new(key, unit, validity == ReadingValidity.Fresh ? value : null, h?.LastSuccessUtc ?? (value != null ? when : null),
                validity, h?.Source ?? "engine", device, expectedInterval ?? _options.SampleInterval.TotalSeconds));
        }
        Add("cpu.temp", "°C", s.CpuSensorAvailable ? s.CpuTemp : null);
        Add("cpu.load", "%", s.CpuLoad); Add("cpu.power", "W", s.CpuPower); Add("cpu.clock", "MHz", s.CpuClockMhz);
        Add("gpu.temp", "°C", s.GpuTemp); Add("gpu.hotspot", "°C", s.GpuHotspot); Add("gpu.load", "%", s.GpuLoad);
        Add("gpu.power", "W", s.GpuPower); Add("gpu.clock", "MHz", s.GpuClockMhz); Add("gpu.fan", "RPM", s.GpuFanRpm1, "gpu.fan1");
        Add("gpu.vram_used", "GiB", s.GpuVramUsedGb); Add("gpu.vram_total", "GiB", s.GpuVramTotalGb);
        Add("coolant.temp", "°C", s.CoolantTemp); Add("ram.usage", "%", s.RamUsage); Add("ram.used", "GiB", s.RamUsedGb);
        Add("ram.total", "GiB", s.RamTotalGb); Add("fans.power", "%", s.EffectiveGlobalPercent);
        Add("net.down", "B/s", s.Network?.DownBytesPerSec, "network"); Add("net.up", "B/s", s.Network?.UpBytesPerSec, "network");
        var reportedDisks = s.Disks ?? [];
        var trackedDiskHealth = health.Values.Where(h => h.Key.StartsWith("disk:", StringComparison.Ordinal)).ToArray();
        bool allDisksFresh = reportedDisks.Count > 0 && trackedDiskHealth.Length > 0 && trackedDiskHealth.All(h => h.Validity == ReadingValidity.Fresh) &&
            reportedDisks.All(d => health.TryGetValue($"disk:{d.Index}", out var disk) && disk.Validity == ReadingValidity.Fresh);
        var oldestDiskAcquisition = trackedDiskHealth.Select(h => h.LastSuccessUtc).Min();
        var diskValidity = allDisksFresh ? ReadingValidity.Fresh : ReadingValidity.Unavailable;
        void DiskAggregate(string key, string unit, double? value) => values.Add(new(key, unit, allDisksFresh ? value : null,
            oldestDiskAcquisition, diskValidity, "LibreHardwareMonitor storage aggregate", ExpectedIntervalSeconds: 2));
        DiskAggregate("disk.activity", "%", reportedDisks.Count > 0 && reportedDisks.All(d => d.ActivityPercent != null) ? reportedDisks.Max(d => d.ActivityPercent) : null);
        DiskAggregate("disk.read", "B/s", reportedDisks.Count > 0 && reportedDisks.All(d => d.ReadBytesPerSec != null) ? reportedDisks.Sum(d => (double?)d.ReadBytesPerSec) : null);
        DiskAggregate("disk.write", "B/s", reportedDisks.Count > 0 && reportedDisks.All(d => d.WriteBytesPerSec != null) ? reportedDisks.Sum(d => (double?)d.WriteBytesPerSec) : null);
        foreach (var disk in s.Disks ?? [])
        {
            string device = $"disk:{disk.Index}";
            Add("disk.device.activity", "%", disk.ActivityPercent, device, device, 2);
            Add("disk.device.read", "B/s", disk.ReadBytesPerSec, device, device, 2);
            Add("disk.device.write", "B/s", disk.WriteBytesPerSec, device, device, 2);
            Add("disk.device.temp", "°C", disk.Temperature, device, device, 2);
        }
        foreach (var volume in s.Volumes)
        {
            string device = $"vol:{volume.Letter}";
            Add("volume.used", "%", volume.UsedPercent, device, device, 15);
            Add("volume.free", "GiB", volume.FreeGb, device, device, 15);
        }
        foreach (var fan in s.Fans.Where(f => f.HasTachometer))
        {
            values.Add(new("fan.rpm", "RPM", fan.RpmAvailable ? fan.Rpm : null, fan.RpmLastSuccessUtc,
                fan.RpmValidity, "hub", fan.Key));
            values.Add(new("fan.power", "%", fan.AppliedPercent, when, ReadingValidity.Fresh, "hub", fan.Key));
            if (fan.IsPump) values.Add(new("pump.rpm", "RPM", fan.RpmAvailable ? fan.Rpm : null, fan.RpmLastSuccessUtc, fan.RpmValidity, "hub", fan.Key));
        }
        var fans = s.Fans.Where(f => !f.IsPump && f.HasTachometer && f.RpmAvailable).ToArray();
        Add("fans.rpm", "RPM", fans.Length > 0 ? fans.Average(f => f.Rpm) : null);
        if (when - _lastResourceUtc >= TimeSpan.FromSeconds(30))
        {
            _lastResourceUtc = when;
            try
            {
                using var process = Process.GetCurrentProcess();
                Add("engine.memory.private", "B", process.PrivateMemorySize64, expectedInterval: 30);
                Add("engine.memory.working_set", "B", process.WorkingSet64, expectedInterval: 30);
                var cpu = process.TotalProcessorTime;
                if (_previousCpuAt != default && when > _previousCpuAt)
                    Add("engine.cpu", "%", Math.Clamp((cpu - _previousCpu).TotalSeconds / (when - _previousCpuAt).TotalSeconds / Environment.ProcessorCount * 100, 0, 100), expectedInterval: 30);
                _previousCpu = cpu; _previousCpuAt = when;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return values;
    }

    private static bool IsValid(MetricObservation v) => v.Validity == ReadingValidity.Fresh && v.Value is { } n && double.IsFinite(n);
    private bool ValidFrame(TelemetryFrame f) => f.Id is { Length: > 0 and <= 80 } && f.Values is { Count: <= 256 } &&
        f.Values.All(v => v != null && v.Key is { Length: > 0 and <= 160 } && ValidObservation(v, f.TimestampUtc));
    private static DateTimeOffset ToUtc(DateTime value) => new DateTimeOffset(value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Local) : value).ToUniversalTime();
    private static DateTime? FileDate(string path) => DateTime.TryParseExact(Path.GetFileName(path).AsSpan(4, Math.Min(8, Math.Max(0, Path.GetFileName(path).Length - 4))),
        "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    private static bool IsStorageError(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException;
    private static string Limit(string? value, int limit) => value == null ? string.Empty : value.Length <= limit ? value : value[..limit];
    private static string Number(double? n) => n?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Csv(string? value)
    {
        value ??= string.Empty;
        // Do not turn exported device names into spreadsheet formulas.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    private static OperationalEvent CloneEvent(OperationalEvent e) => e with { Data = new(e.Data) };
    private static HistoryQuery CopyQuery(HistoryQuery q, string? session, double? threshold) => new()
    {
        MetricKey = q.MetricKey, DeviceId = q.DeviceId, FromUtc = q.FromUtc, ToUtc = q.ToUtc, BucketSeconds = q.BucketSeconds,
        MaxPoints = q.MaxPoints, SessionId = session, Threshold = threshold
    };
}
