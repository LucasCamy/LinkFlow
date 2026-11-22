namespace LinkFlow.Core;

/// <summary>Bounded local history. A null directory keeps history in memory (isolated tests and previews).</summary>
public sealed class OperationalHistoryOptions
{
    public string? DirectoryPath { get; set; }
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);
    public long MaxDiskBytes { get; set; } = 64L * 1024 * 1024;
    public long MaxSegmentBytes { get; set; } = 2L * 1024 * 1024;
    public int QueueCapacity { get; set; } = 512;
    public int MaxMemoryFrames { get; set; } = 7200;
    public int MaxMemoryEvents { get; set; } = 2000;
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    internal OperationalHistoryOptions Validated() => new()
    {
        DirectoryPath = string.IsNullOrWhiteSpace(DirectoryPath) ? null : Path.GetFullPath(DirectoryPath),
        SampleInterval = SampleInterval < TimeSpan.FromMilliseconds(20) ? TimeSpan.FromMilliseconds(20)
            : SampleInterval > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : SampleInterval,
        Retention = Retention < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : Retention > TimeSpan.FromDays(365) ? TimeSpan.FromDays(365) : Retention,
        MaxDiskBytes = Math.Clamp(MaxDiskBytes, 16 * 1024, 1024L * 1024 * 1024),
        MaxSegmentBytes = Math.Clamp(MaxSegmentBytes, 4096, Math.Max(4096, Math.Clamp(MaxDiskBytes, 16 * 1024, 1024L * 1024 * 1024) / 4)),
        QueueCapacity = Math.Clamp(QueueCapacity, 8, 8192),
        MaxMemoryFrames = Math.Clamp(MaxMemoryFrames, 4, 86400),
        MaxMemoryEvents = Math.Clamp(MaxMemoryEvents, 4, 10000),
        TimeProvider = TimeProvider ?? TimeProvider.System
    };
}

public sealed class HistoryQuery
{
    public string MetricKey { get; set; } = "cpu.temp";
    public string? DeviceId { get; set; }
    public DateTimeOffset FromUtc { get; set; } = DateTimeOffset.UtcNow.AddHours(-1);
    public DateTimeOffset ToUtc { get; set; } = DateTimeOffset.UtcNow;
    public int BucketSeconds { get; set; } = 1;
    public int MaxPoints { get; set; } = 1200;
    public string? SessionId { get; set; }
    public double? Threshold { get; set; }
}

public sealed class EventQuery
{
    public DateTimeOffset FromUtc { get; set; } = DateTimeOffset.UtcNow.AddDays(-1);
    public DateTimeOffset ToUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Kind { get; set; }
    public string? Source { get; set; }
    public string? DeviceId { get; set; }
    /// <summary>Newest UTC timestamp first; ordinal Id breaks timestamp ties deterministically, not chronologically.</summary>
    public int MaxItems { get; set; } = 200;
}

public sealed record HistoryPoint
{
    public DateTimeOffset TimestampUtc { get; init; }
    public DateTimeOffset? AcquiredAtUtc { get; init; }
    public double? Value { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public int ValidSamples { get; init; }
    public int TotalSamples { get; init; }
    public ReadingValidity Validity { get; init; } = ReadingValidity.Unavailable;
    public string? SessionId { get; init; }
    public string Source { get; init; } = string.Empty;
}

public sealed record HistoryStatistics
{
    public int TotalSamples { get; init; }
    public int ValidSamples { get; init; }
    public int ExpectedSamples { get; init; }
    public double CoveragePercent { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Average { get; init; }
    public double? P95 { get; init; }
    public double ObservedSeconds { get; init; }
    public double SecondsAboveThreshold { get; init; }
    public double? Threshold { get; init; }
}

public sealed record HistorySeries
{
    public string MetricKey { get; init; } = string.Empty;
    public string Unit { get; init; } = string.Empty;
    public string? DeviceId { get; init; }
    public DateTimeOffset FromUtc { get; init; }
    public DateTimeOffset ToUtc { get; init; }
    public int BucketSeconds { get; init; }
    public double ExpectedIntervalSeconds { get; init; } = 1;
    public List<HistoryPoint> Points { get; init; } = [];
    public HistoryStatistics Statistics { get; init; } = new();
    public HistoryQueryExecution QueryExecution { get; init; } = new();
}

/// <summary>Payload reads, not filesystem metadata checks or physical disk I/O.</summary>
public sealed record HistoryQueryExecution
{
    public bool MemoryCacheUsed { get; init; }
    public string CacheDecision { get; init; } = string.Empty;
    public int DiskScans { get; init; }
    public int FilesRead { get; init; }
    public long BytesRead { get; init; }
    public long RecordsScanned { get; init; }
    /// <summary>Full frame models hydrated; context uses validated metric projections.</summary>
    public long FramesDeserialized { get; init; }
    public double DurationMilliseconds { get; init; }
    public bool Succeeded { get; init; }
}

/// <summary>Bounded counters for one query category; series/events use separate instances.</summary>
public sealed record HistoryQueryStatistics
{
    public long Queries { get; init; }
    public long MemoryCacheHits { get; init; }
    public long DiskQueries { get; init; }
    public long BytesRead { get; init; }
    public long RecordsScanned { get; init; }
    public long FramesDeserialized { get; init; }
    public long CacheInvalidations { get; init; }
    public string? CacheProofError { get; init; }
    public HistoryQueryExecution LastExecution { get; init; } = new();
}

public sealed record OperationalEvent
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string SessionId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public AlertSeverity Severity { get; init; }
    public string Source { get; init; } = "engine";
    public string? DeviceId { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, string> Data { get; init; } = new();
}

public sealed record HistorySession(string Id, DateTimeOffset StartedUtc, DateTimeOffset? StoppedUtc, string Version);
public sealed record SessionComparison(string BaselineSessionId, string CandidateSessionId,
    HistoryStatistics Baseline, HistoryStatistics Candidate, double? AverageDifference, double? P95Difference);

public sealed record HistoryStorageStatus
{
    public bool PersistenceEnabled { get; init; }
    public bool StorageAvailable { get; init; } = true;
    public DateTimeOffset? LastWriteUtc { get; init; }
    public string? LastError { get; init; }
    public long DroppedWrites { get; init; }
    public long CorruptRecords { get; init; }
    public long DiskBytes { get; init; }
    public int StoredFrames { get; init; }
    public int StoredEvents { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public bool SessionMetadataAvailable { get; init; } = true;
    public string? SessionMetadataError { get; init; }
    public long SessionMetadataBytes { get; init; }
    public int SessionsWithUnknownStart { get; init; }
    public HistoryQueryStatistics Queries { get; init; } = new();
    public HistoryQueryStatistics EventQueries { get; init; } = new();
}

/// <summary>A single value with its acquisition time. Missing values remain explicit observations.</summary>
public sealed record MetricObservation(string Key, string Unit, double? Value, DateTimeOffset? AcquiredAtUtc,
    ReadingValidity Validity = ReadingValidity.Fresh, string Source = "engine", string? DeviceId = null,
    double? ExpectedIntervalSeconds = null);

public sealed record TelemetryFrame
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; }
    public List<MetricObservation> Values { get; init; } = [];
}
