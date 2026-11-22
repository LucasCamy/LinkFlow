using System.Diagnostics;

namespace LinkFlow.Core;

public sealed partial class OperationalHistory
{
    // Events have no acquisition TTL/context. Their independent fence records every
    // eviction, including out-of-order timestamps, rather than guessing from frames.
    private DateTimeOffset? _evictedEventFence;
    private long _eventQueryCount, _eventMemoryHits, _eventDiskQueries, _eventQueryBytes, _eventQueryRecords, _eventQueryFrames;
    private HistoryQueryExecution _lastEventQueryExecution = new();

    private HistoryQueryStatistics EventQueryStatistics => new()
    {
        Queries = Interlocked.Read(ref _eventQueryCount), MemoryCacheHits = Interlocked.Read(ref _eventMemoryHits),
        DiskQueries = Interlocked.Read(ref _eventDiskQueries), BytesRead = Interlocked.Read(ref _eventQueryBytes),
        RecordsScanned = Interlocked.Read(ref _eventQueryRecords), FramesDeserialized = Interlocked.Read(ref _eventQueryFrames),
        CacheInvalidations = Interlocked.Read(ref _queryInvalidations), CacheProofError = Volatile.Read(ref _queryCacheProofError),
        LastExecution = Volatile.Read(ref _lastEventQueryExecution)
    };

    // Called under _gate, or while the initial cache is loaded before publication.
    private void ObserveEvictedEvent(OperationalEvent entry)
    {
        var at = entry.TimestampUtc.ToUniversalTime();
        if (_evictedEventFence == null || at > _evictedEventFence) _evictedEventFence = at;
    }

    public IReadOnlyList<OperationalEvent> QueryEvents(EventQuery query)
    {
        long started = Stopwatch.GetTimestamp();
        var work = new QueryWork();
        bool succeeded = false;
        try
        {
            var result = QueryEventsCore(query, work);
            succeeded = true;
            return result;
        }
        finally
        {
            var execution = new HistoryQueryExecution
            {
                MemoryCacheUsed = work.MemoryCacheUsed, CacheDecision = work.CacheDecision,
                DiskScans = work.DiskScans, FilesRead = work.FilesRead, BytesRead = work.BytesRead,
                RecordsScanned = work.RecordsScanned, FramesDeserialized = work.FramesDeserialized,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Succeeded = succeeded
            };
            Interlocked.Increment(ref _eventQueryCount);
            if (work.MemoryCacheUsed) Interlocked.Increment(ref _eventMemoryHits);
            if (work.DiskScans > 0) Interlocked.Increment(ref _eventDiskQueries);
            Interlocked.Add(ref _eventQueryBytes, work.BytesRead);
            Interlocked.Add(ref _eventQueryRecords, work.RecordsScanned);
            Interlocked.Add(ref _eventQueryFrames, work.FramesDeserialized);
            Volatile.Write(ref _lastEventQueryExecution, execution);
            Metrics.Duration(succeeded ? "history.events.query" : "history.events.query.failed", TimeSpan.FromMilliseconds(execution.DurationMilliseconds));
        }
    }

    private IReadOnlyList<OperationalEvent> QueryEventsCore(EventQuery query, QueryWork work)
    {
        ArgumentNullException.ThrowIfNull(query);
        var from = query.FromUtc.ToUniversalTime();
        var to = query.ToUtc.ToUniversalTime();
        if (to < from || to - from > TimeSpan.FromDays(366)) throw new ArgumentException("Invalid event range.", nameof(query));
        string? kind = query.Kind, source = query.Source, device = query.DeviceId;
        int max = Math.Clamp(query.MaxItems, 1, 2000);

        long mutation = Volatile.Read(ref _diskMutationVersion);
        var known = Volatile.Read(ref _knownSegments);
        OperationalEvent[] memory;
        DateTimeOffset? evicted;
        lock (_gate) { memory = _events.ToArray(); evicted = _evictedEventFence; }

        bool useMemory = _options.DirectoryPath == null;
        work.CacheDecision = useMemory ? "memory-only" : "load-incomplete";
        if (!useMemory && _initialCacheLoadComplete && Volatile.Read(ref _queryCacheInvalidated) == 0)
        {
            work.CacheDecision = "evicted-events";
            if (!OperatingSystem.IsWindows() || known.Values.Any(stamp => stamp.ChangeTime <= 0))
                work.CacheDecision = "filesystem-proof-unavailable";
            else if (evicted == null || from > evicted.Value)
            {
                work.CacheDecision = "writer-active";
                if ((mutation & 1) == 0)
                {
                    try
                    {
                        var actual = CaptureSegmentManifest();
                        if (mutation == Volatile.Read(ref _diskMutationVersion))
                        {
                            if (SameSegments(known, actual) && _storageAvailable)
                            { useMemory = true; work.CacheDecision = "verified-memory"; }
                            else { InvalidateQueryCache(); work.CacheDecision = "segments-changed"; }
                        }
                    }
                    catch (Exception ex) when (IsStorageError(ex))
                    {
                        if (ex is FileNotFoundException { FileName: { } removed } && PrunedDuring(removed, mutation)) work.CacheDecision = "writer-active";
                        else { InvalidateQueryCache(ex); StorageFailure(ex); work.CacheDecision = "segment-io-failed"; }
                    }
                }
            }
        }
        else if (!useMemory && Volatile.Read(ref _queryCacheInvalidated) != 0)
            work.CacheDecision = Volatile.Read(ref _queryCacheProofError) == null ? "segments-changed" : "segment-io-failed";

        var all = new Dictionary<string, OperationalEvent>(StringComparer.Ordinal);
        if (useMemory) work.MemoryCacheUsed = true;
        else
        {
            work.DiskScans = 1;
            foreach (var record in ReadDisk(from, to, includeFrames: false, work: work))
                if (record.Event is { } entry) all[entry.Id] = entry;
        }
        // The snapshot contains immutable internal records; return independent Data dictionaries.
        // Memory precedence is identical to the persisted path, including unflushed events.
        foreach (var entry in memory) all[entry.Id] = entry;
        return all.Values.Where(entry => entry.TimestampUtc >= from && entry.TimestampUtc <= to)
            .Where(entry => kind == null || entry.Kind == kind)
            .Where(entry => source == null || entry.Source == source)
            .Where(entry => device == null || entry.DeviceId == device)
            // A shared timestamp provides no evidence of chronology between events.
            .OrderByDescending(entry => entry.TimestampUtc).ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .Take(max).Select(CloneEvent).ToArray();
    }
}
