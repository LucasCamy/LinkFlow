using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.Core;

public sealed partial class OperationalHistory
{
    private sealed class QueryWork
    {
        public bool MemoryCacheUsed;
        public string CacheDecision = string.Empty;
        public int DiskScans, FilesRead;
        public long BytesRead, RecordsScanned, FramesDeserialized;
    }

    private readonly record struct SegmentStamp(long Length, DateTime CreationUtc, DateTime LastWriteUtc, long ChangeTime);
    // https://learn.microsoft.com/windows/win32/api/winbase/ns-winbase-file_basic_info
    [StructLayout(LayoutKind.Sequential, Pack = 8, Size = 40)]
    private struct FileBasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileBasicInfo(SafeFileHandle handle, int informationClass, out FileBasicInfo information, uint size);
    // Unlike File.Delete, this reports a path already removed externally as ERROR_FILE_NOT_FOUND.
    // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-deletefilew
    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteHistoryFile(string path);
    private static readonly string[] ObservationTextProperties = ["unit", "source", "deviceId"];
    private static readonly string[] ObservationNumberProperties = ["value", "expectedIntervalSeconds"];
    private static readonly JsonDocumentOptions QueryDocumentOptions = new()
    { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private Dictionary<string, SegmentStamp> _knownSegments = new(StringComparer.OrdinalIgnoreCase);
    private readonly record struct PrunedSegment(long Version, long Attempt, bool Confirmed);
    private Dictionary<string, PrunedSegment> _recentPrunedSegments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(string Path, long Attempt)> _prunedSegmentOrder = new();
    private long _pruneAttempt;
    private Action<string, string>? _fileOperationProbe;
    // A narrow internal seam lets tests suspend actual file operations at the race boundaries.
    // It never crosses options/IPC and is null in production.
    internal Action<string, string>? FileOperationProbe
    {
        get => Volatile.Read(ref _fileOperationProbe);
        set => Volatile.Write(ref _fileOperationProbe, value);
    }
    private long _diskMutationVersion, _diskReadFailures;
    private bool _initialCacheLoadComplete, _futureAcquisitionObserved;
    private int _queryCacheInvalidated;
    private string? _queryCacheProofError;
    private DateTimeOffset? _evictedFrameFence;
    private double _largestCadenceSeconds;
    private long _queryCount, _memoryQueryHits, _diskQueryCount, _queryBytes, _queryRecords, _queryFrames, _queryInvalidations;
    private HistoryQueryExecution _lastQueryExecution = new();

    private HistoryQueryStatistics QueryStatistics => new()
    {
        Queries = Interlocked.Read(ref _queryCount), MemoryCacheHits = Interlocked.Read(ref _memoryQueryHits),
        DiskQueries = Interlocked.Read(ref _diskQueryCount), BytesRead = Interlocked.Read(ref _queryBytes),
        RecordsScanned = Interlocked.Read(ref _queryRecords), FramesDeserialized = Interlocked.Read(ref _queryFrames),
        CacheInvalidations = Interlocked.Read(ref _queryInvalidations), CacheProofError = Volatile.Read(ref _queryCacheProofError),
        LastExecution = Volatile.Read(ref _lastQueryExecution)
    };

    public HistorySeries Query(HistoryQuery query)
    {
        long started = Stopwatch.GetTimestamp();
        var work = new QueryWork();
        try
        {
            var series = QueryCore(query, work);
            return series with { QueryExecution = CompleteQuery(work, started, true) };
        }
        catch
        {
            CompleteQuery(work, started, false);
            throw;
        }
    }

    private HistoryQueryExecution CompleteQuery(QueryWork work, long started, bool succeeded)
    {
        var execution = new HistoryQueryExecution
        {
            MemoryCacheUsed = work.MemoryCacheUsed, CacheDecision = work.CacheDecision,
            DiskScans = work.DiskScans, FilesRead = work.FilesRead, BytesRead = work.BytesRead,
            RecordsScanned = work.RecordsScanned, FramesDeserialized = work.FramesDeserialized,
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds, Succeeded = succeeded
        };
        Interlocked.Increment(ref _queryCount);
        if (work.MemoryCacheUsed) Interlocked.Increment(ref _memoryQueryHits);
        if (work.DiskScans > 0) Interlocked.Increment(ref _diskQueryCount);
        Interlocked.Add(ref _queryBytes, work.BytesRead);
        Interlocked.Add(ref _queryRecords, work.RecordsScanned);
        Interlocked.Add(ref _queryFrames, work.FramesDeserialized);
        Volatile.Write(ref _lastQueryExecution, execution);
        Metrics.Query(execution);
        return execution;
    }

    // Callers hold _gate, or are loading before the writer/publication starts.
    private void ObserveFrameCadence(TelemetryFrame frame)
    {
        foreach (var value in frame.Values)
        {
            double cadence = ObservationCadence(value);
            if (!ValidCadence(cadence)) { _futureAcquisitionObserved = true; continue; }
            _largestCadenceSeconds = Math.Max(_largestCadenceSeconds, cadence);
            if (value.AcquiredAtUtc > frame.TimestampUtc) _futureAcquisitionObserved = true;
        }
    }

    // Legacy resource JSON predates per-observation cadence. Its 30-second acquisition interval
    // must be identical in the eviction proof, context selection, duration and query statistics.
    private double DefaultMetricCadence(string key) => key.StartsWith("engine.", StringComparison.Ordinal)
        ? Math.Max(30, _options.SampleInterval.TotalSeconds) : _options.SampleInterval.TotalSeconds;
    private double ObservationCadence(MetricObservation value) => value.ExpectedIntervalSeconds ?? DefaultMetricCadence(value.Key);
    private static bool ValidCadence(double cadence) => double.IsFinite(cadence) && cadence is >= .02 and <= 86400;

    private static bool TryExpiry(DateTimeOffset acquired, double cadence, out DateTimeOffset expiry)
    {
        expiry = default;
        if (!ValidCadence(cadence)) return false;
        try
        {
            // DateTimeOffset.AddSeconds preserves the input offset. Normalize first: a clock near
            // local year9999 can still have hours of valid UTC range, and must not become a day-long TTL.
            expiry = acquired.ToUniversalTime().AddSeconds(cadence);
            return true;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private bool ValidObservation(MetricObservation value, DateTimeOffset published) =>
        (value.ExpectedIntervalSeconds == null || ValidCadence(value.ExpectedIntervalSeconds.Value)) &&
        TryExpiry(value.AcquiredAtUtc ?? published, ObservationCadence(value), out _);

    private void ObserveEvictedFrame(TelemetryFrame frame)
    {
        if (_evictedFrameFence == null || frame.TimestampUtc > _evictedFrameFence) _evictedFrameFence = frame.TimestampUtc;
    }

    // Saturation is conservative only for the cache rejection fence, never for measured coverage.
    private static DateTimeOffset FenceExpiry(DateTimeOffset acquired, double cadence) =>
        TryExpiry(acquired, cadence, out var expiry) ? expiry : DateTimeOffset.MaxValue;

    private void BeginDiskMutation() => Interlocked.Increment(ref _diskMutationVersion);
    private void EndDiskMutation() => Interlocked.Increment(ref _diskMutationVersion);
    private bool PrunedDuring(string path, long version) => OperatingSystem.IsWindows() &&
        Volatile.Read(ref _recentPrunedSegments).TryGetValue(path, out var prune) &&
        (prune.Version > version || prune.Version == version && (version & 1) != 0) &&
        (prune.Confirmed || prune.Version == Volatile.Read(ref _diskMutationVersion) && (prune.Version & 1) != 0);

    private static void DeleteSegment(string path)
    {
        if (!OperatingSystem.IsWindows()) { File.Delete(path); return; }
        string nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
            : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        if (DeleteHistoryFile(nativePath)) return;
        int error = Marshal.GetLastWin32Error();
        var cause = new Win32Exception(error);
        string message = $"History segment could not be deleted: {cause.Message}";
        Exception failure = error switch
        {
            2 => new FileNotFoundException("History segment disappeared before pruning.", path, cause),
            3 => new DirectoryNotFoundException("History directory disappeared before pruning.", cause),
            5 => new UnauthorizedAccessException(message, cause),
            _ => new IOException(message, cause)
        };
        throw failure;
    }

    private static SegmentStamp ReadStamp(FileStream stream)
    {
        if (!OperatingSystem.IsWindows()) return new(stream.Length, File.GetCreationTimeUtc(stream.SafeFileHandle),
            File.GetLastWriteTimeUtc(stream.SafeFileHandle), 0);
        if (!GetFileBasicInfo(stream.SafeFileHandle, 0, out var information, (uint)Marshal.SizeOf<FileBasicInfo>()))
            throw new IOException("Cannot read history segment identity metadata.", new Win32Exception(Marshal.GetLastWin32Error()));
        // NTFS ChangeTime also detects rewrites whose length and LastWriteTime are preserved.
        return new(stream.Length, DateTime.FromFileTimeUtc(information.CreationTime),
            DateTime.FromFileTimeUtc(information.LastWriteTime), information.ChangeTime);
    }

    private Dictionary<string, SegmentStamp> CaptureSegmentManifest()
    {
        var result = new Dictionary<string, SegmentStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in DataFiles(throwOnError: true))
        {
            FileOperationProbe?.Invoke("query-before-open", path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            result[path] = ReadStamp(stream); // Handle metadata avoids stale NTFS directory lengths for an open writer.
        }
        return result;
    }

    private static bool SameSegments(Dictionary<string, SegmentStamp> first, Dictionary<string, SegmentStamp> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var stamp) && stamp == pair.Value);

    private void LoadInitialQueryCache()
    {
        Dictionary<string, SegmentStamp>? before = null;
        try { before = CaptureSegmentManifest(); }
        catch (Exception ex) when (IsStorageError(ex)) { InvalidateQueryCache(ex); StorageFailure(ex); }
        long failures = _diskReadFailures, corruption = _corruptRecords;
        var from = Now - _options.Retention;
        var to = Now.AddMinutes(1);
        LoadRecent(from, to); // Optional identity metadata must never prevent loading ordinary history.
        if (before == null) return;
        try
        {
            var after = CaptureSegmentManifest();
            _knownSegments = after;
            _initialCacheLoadComplete = _storageAvailable && failures == _diskReadFailures &&
                corruption == _corruptRecords && SameSegments(before, after) && after.All(pair =>
                    pair.Value.Length <= _options.MaxDiskBytes && (FileDate(pair.Key) is not { } date ||
                        date >= from.UtcDateTime.Date && date <= to.UtcDateTime.Date));
        }
        catch (Exception ex) when (IsStorageError(ex)) { InvalidateQueryCache(ex); StorageFailure(ex); }
    }

    private void RememberOpenSegment()
    {
        if (!_initialCacheLoadComplete || Volatile.Read(ref _queryCacheInvalidated) != 0 || _writerPath == null ||
            _writer?.BaseStream is not FileStream stream) return;
        try
        {
            var updated = new Dictionary<string, SegmentStamp>(Volatile.Read(ref _knownSegments), StringComparer.OrdinalIgnoreCase)
            { [_writerPath] = ReadStamp(stream) };
            Volatile.Write(ref _knownSegments, updated);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // Invalidate the optimization and surface the I/O failure, without ending the raw writer.
            InvalidateQueryCache(ex); StorageFailure(ex);
        }
    }

    private long BeginSegmentPrune(string path)
    {
        long attempt = ++_pruneAttempt;
        var pruned = new Dictionary<string, PrunedSegment>(Volatile.Read(ref _recentPrunedSegments), StringComparer.OrdinalIgnoreCase)
        { [path] = new(Volatile.Read(ref _diskMutationVersion), attempt, false) };
        _prunedSegmentOrder.Enqueue((path, attempt));
        while (_prunedSegmentOrder.Count > 256)
        {
            var old = _prunedSegmentOrder.Dequeue();
            if (pruned.TryGetValue(old.Path, out var previous) && previous.Attempt == old.Attempt) pruned.Remove(old.Path);
        }
        // Publish BEFORE Delete: readers may have enumerated the path and not opened it yet.
        Volatile.Write(ref _recentPrunedSegments, pruned);
        return attempt;
    }

    private void CompleteSegmentPrune(string path, long attempt, bool removed)
    {
        var pruned = new Dictionary<string, PrunedSegment>(Volatile.Read(ref _recentPrunedSegments), StringComparer.OrdinalIgnoreCase);
        if (pruned.TryGetValue(path, out var pending) && pending.Attempt == attempt)
        {
            if (removed) pruned[path] = pending with { Confirmed = true };
            else pruned.Remove(path); // A failed delete must never become evidence for ignoring external I/O.
            Volatile.Write(ref _recentPrunedSegments, pruned);
        }
        if (!removed) return;
        var updated = new Dictionary<string, SegmentStamp>(Volatile.Read(ref _knownSegments), StringComparer.OrdinalIgnoreCase);
        updated.Remove(path);
        Volatile.Write(ref _knownSegments, updated);
    }

    private void InvalidateQueryCache(Exception? exception = null)
    {
        if (exception != null) Volatile.Write(ref _queryCacheProofError, Limit(exception.Message, 400));
        if (Interlocked.Exchange(ref _queryCacheInvalidated, 1) == 0) Interlocked.Increment(ref _queryInvalidations);
    }

    private List<TelemetryFrame> ReadQueryFrames(HistoryQuery query, DateTimeOffset from, DateTimeOffset to, QueryWork work)
    {
        long mutation = Volatile.Read(ref _diskMutationVersion);
        var known = Volatile.Read(ref _knownSegments);
        TelemetryFrame[] memory;
        DateTimeOffset? evicted;
        double cadence;
        bool future;
        lock (_gate)
        {
            memory = _frames.ToArray();
            evicted = _evictedFrameFence;
            cadence = _largestCadenceSeconds;
            future = _futureAcquisitionObserved;
        }
        var memoryById = new Dictionary<string, TelemetryFrame>(StringComparer.Ordinal);
        foreach (var frame in memory) memoryById[frame.Id] = frame;

        bool useMemory = _options.DirectoryPath == null;
        work.CacheDecision = useMemory ? "memory-only" : "load-incomplete";
        if (!useMemory && _initialCacheLoadComplete && Volatile.Read(ref _queryCacheInvalidated) == 0)
        {
            work.CacheDecision = "evicted-context";
            // Every discarded publication, including an out-of-order one, advances this fence.
            // The largest observed cadence also bounds context from discarded failures/cached reads.
            if (!OperatingSystem.IsWindows() || known.Values.Any(stamp => stamp.ChangeTime <= 0))
                work.CacheDecision = "filesystem-proof-unavailable";
            else if (!future && (evicted == null || from > FenceExpiry(evicted.Value, cadence)))
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
                            {
                                useMemory = true;
                                work.CacheDecision = "verified-memory";
                            }
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
            else if (future) work.CacheDecision = "acquisition-order-unknown";
        }
        else if (!useMemory && Volatile.Read(ref _queryCacheInvalidated) != 0)
            work.CacheDecision = Volatile.Read(ref _queryCacheProofError) == null ? "segments-changed" : "segment-io-failed";

        if (useMemory)
        {
            work.MemoryCacheUsed = true;
            return SelectQueryFrames(memoryById.Values, query, from, to);
        }

        work.DiskScans = 1;
        var merged = ReadQueryDisk(query, from, to, work);
        // Preserve the existing memory-tail precedence, including unpersisted observations.
        foreach (var pair in memoryById) merged[pair.Key] = pair.Value;
        return SelectQueryFrames(merged.Values, query, from, to);
    }

    private IEnumerable<MetricObservation> QueryValues(TelemetryFrame frame, HistoryQuery query) => frame.Values
        .Where(v => v.Key == query.MetricKey && (query.DeviceId == null || v.DeviceId == query.DeviceId));

    private List<TelemetryFrame> SelectQueryFrames(IEnumerable<TelemetryFrame> source, HistoryQuery query,
        DateTimeOffset from, DateTimeOffset to)
    {
        var frames = new Dictionary<string, TelemetryFrame>(StringComparer.Ordinal);
        var invalid = new Dictionary<(string Session, string? Device), TelemetryFrame>();
        var latest = new Dictionary<(string Session, string? Device), TelemetryFrame>();
        foreach (var frame in source)
        {
            if (frame.TimestampUtc >= to || query.SessionId != null && frame.SessionId != query.SessionId) continue;
            if (frame.TimestampUtc >= from) { frames[frame.Id] = frame; continue; }
            foreach (var value in QueryValues(frame, query))
            {
                var stream = (frame.SessionId, value.DeviceId);
                if (!latest.TryGetValue(stream, out var last) || frame.TimestampUtc > last.TimestampUtc) latest[stream] = frame;
                if (IsValid(value))
                {
                    if (TryExpiry(value.AcquiredAtUtc ?? frame.TimestampUtc, ObservationCadence(value), out var expiry) && expiry > from)
                        frames[frame.Id] = frame;
                }
                else
                {
                    var key = (frame.SessionId, value.DeviceId);
                    if (!invalid.TryGetValue(key, out var previous) || frame.TimestampUtc > previous.TimestampUtc) invalid[key] = frame;
                }
            }
        }
        foreach (var frame in latest.Values) frames[frame.Id] = frame;
        foreach (var frame in invalid.Values) frames[frame.Id] = frame;
        return frames.Values.OrderBy(f => f.TimestampUtc).ToList();
    }

    private Dictionary<string, TelemetryFrame> ReadQueryDisk(HistoryQuery query, DateTimeOffset from, DateTimeOffset to, QueryWork work)
    {
        var frames = new Dictionary<string, TelemetryFrame>(StringComparer.Ordinal);
        long scanMutation = Volatile.Read(ref _diskMutationVersion);
        foreach (string path in DataFiles())
        {
            // Context may cross a UTC day. Its own acquisition/cadence, not an assumed metric name,
            // decides relevance below. The existing disk/time budgets still bound this scan.
            if (FileDate(path) is { } date && date > to.UtcDateTime.Date) continue;
            FileStream? stream = null;
            try
            {
                FileOperationProbe?.Invoke("query-before-open", path);
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > _options.MaxDiskBytes) continue;
                work.FilesRead++;
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true, 4096, leaveOpen: true);
                int lineNumber = 0;
                while (reader.ReadLine() is { } line)
                {
                    lineNumber++;
                    if (line.Length == 0) continue;
                    work.RecordsScanned++;
                    try
                    {
                        if (line.Length > MaxRecordChars) throw new JsonException("History record too large.");
                        using var document = JsonDocument.Parse(line, QueryDocumentOptions);
                        var root = document.RootElement;
                        if (!root.TryGetProperty("type", out var type)) throw new JsonException("Invalid history record.");
                        if (type.GetString() == "event")
                        {
                            var record = root.Deserialize<DiskRecord>(Json.Compact);
                            if (record?.Event is not { } entry || entry.Id is not { Length: > 0 and <= 80 } ||
                                entry.Kind is not { Length: > 0 and <= 100 } || entry.Source == null || entry.Message == null || entry.Data == null)
                                throw new JsonException("Invalid history event.");
                            continue;
                        }
                        if (type.GetString() != "frame" || !root.TryGetProperty("frame", out var element) ||
                            !element.TryGetProperty("id", out var idElement) || idElement.GetString() is not { Length: > 0 and <= 80 } id ||
                            !element.TryGetProperty("timestampUtc", out var timestamp) || !timestamp.TryGetDateTimeOffset(out var when))
                            throw new JsonException("Invalid history frame.");
                        var selected = ValidateAndSelectValues(element, query, when < to, when);
                        if (when >= to || selected.Count == 0) { frames.Remove(id); continue; }
                        if (when < from)
                        {
                            // Keep lightweight projections until ID precedence is resolved. The final
                            // selector retains support, the last read, and the last failure per stream.
                            // An expired newer read can end an older long-cadence value's support.
                            string? session = element.TryGetProperty("sessionId", out var sessionElement) ? sessionElement.GetString() : string.Empty;
                            frames[id] = new() { Id = id, SessionId = session!, TimestampUtc = when, Values = selected };
                            continue;
                        }
                        // Full typed frames are materialized only inside this window. Context uses
                        // validated projections, without hydrating every metric of every old frame.
                        var frame = element.Deserialize<TelemetryFrame>(Json.Compact);
                        work.FramesDeserialized++;
                        if (frame == null || !ValidFrame(frame)) throw new JsonException("Invalid history frame.");
                        frames[id] = frame with { Values = QueryValues(frame, query).ToList() };
                    }
                    catch (Exception ex) when (ex is JsonException or NotSupportedException or NullReferenceException or InvalidOperationException)
                    {
                        if (path == _writerPath && reader.EndOfStream) continue;
                        Interlocked.Increment(ref _diskReadFailures);
                        lock (_gate)
                            if (_seenCorruptLines.Count < 4096 && _seenCorruptLines.Add($"{path}:{lineNumber}")) Interlocked.Increment(ref _corruptRecords);
                    }
                }
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                // A closed segment can be pruned after enumeration while later frames are saved.
                // Access/other I/O errors remain visible; a missing file during our mutation is retried
                // by the next query, while this query keeps the immutable memory snapshot.
                if (ex is not FileNotFoundException || !PrunedDuring(path, scanMutation))
                { Interlocked.Increment(ref _diskReadFailures); StorageFailure(ex); }
            }
            finally
            {
                if (stream != null)
                {
                    try { work.BytesRead += stream.Position; } catch (IOException) { }
                    stream.Dispose();
                }
            }
        }
        return frames;
    }

    private List<MetricObservation> ValidateAndSelectValues(JsonElement frame, HistoryQuery query, bool select, DateTimeOffset published)
    {
        if (frame.TryGetProperty("sessionId", out var session) && session.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new JsonException("Invalid history session.");
        if (!frame.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 256)
            throw new JsonException("Invalid history observations.");
        var selected = new List<MetricObservation>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("key", out var key) ||
                key.ValueKind != JsonValueKind.String || key.GetString() is not { Length: > 0 and <= 160 } name)
                throw new JsonException("Invalid history observation key.");
            foreach (string property in ObservationTextProperties)
                if (value.TryGetProperty(property, out var text) && text.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    throw new JsonException("Invalid history observation text.");
            foreach (string property in ObservationNumberProperties)
                if (value.TryGetProperty(property, out var number) && number.ValueKind != JsonValueKind.Null &&
                    (number.ValueKind != JsonValueKind.Number || !number.TryGetDouble(out _)))
                    throw new JsonException("Invalid history observation number.");
            double cadence = DefaultMetricCadence(name);
            if (value.TryGetProperty("expectedIntervalSeconds", out var declared) && declared.ValueKind != JsonValueKind.Null)
            {
                cadence = declared.GetDouble();
                if (!ValidCadence(cadence)) throw new JsonException("Invalid history observation cadence.");
            }
            DateTimeOffset acquisition = published;
            if (value.TryGetProperty("acquiredAtUtc", out var acquired) && acquired.ValueKind != JsonValueKind.Null &&
                (acquired.ValueKind != JsonValueKind.String || !acquired.TryGetDateTimeOffset(out acquisition)))
                throw new JsonException("Invalid history acquisition timestamp.");
            if (!TryExpiry(acquisition, cadence, out _)) throw new JsonException("History acquisition interval exceeds UTC bounds.");
            if (value.TryGetProperty("validity", out var validity)) validity.Deserialize<ReadingValidity>(Json.Compact);
            if (!select || name != query.MetricKey) continue;
            var observation = value.Deserialize<MetricObservation>(Json.Compact) ?? throw new JsonException("Invalid history observation.");
            if (query.DeviceId == null || observation.DeviceId == query.DeviceId) selected.Add(observation);
        }
        return selected;
    }
}
