namespace LinkFlow.Core;

/// <summary>The latest adapter enumeration through the RGB backend, not a physical connection verdict.</summary>
public enum RgbAdapterObservation { Unknown, Present, NotDetected, EnumerationUnavailable }

public enum RgbNativeOperation { Initialize, EnumerateAdapters, IdentifyAdapter, Read, Write }

/// <summary>A completed native call. Status is the unmodified SDK return code; zero means that call succeeded.</summary>
public sealed record RgbNativeOperationResult
{
    /// <summary>Monotonic per-provider ordering; zero means unavailable from an older provider.</summary>
    public long Sequence { get; init; }
    /// <summary>Ordered failure count in this provider; zero for success or a legacy provider. The isolated proxy maps it across generations.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long FailureOrdinal { get; init; }
    public DateTimeOffset AtUtc { get; init; }
    public RgbNativeOperation Operation { get; init; }
    public int Status { get; init; }
    public ushort? Register { get; init; }
    public byte? Command { get; init; }
    /// <summary>Process which completed this native call; retained when mapped across worker generations.</summary>
    public int? ProcessId { get; init; }
    /// <summary>Managed thread identity for correlation, not a native thread-affinity requirement.</summary>
    public int? ThreadId { get; init; }
    /// <summary>Opaque NVAPI handle, sixteen hexadecimal characters without0x. It is not an I2C address or durable GPU identity.</summary>
    public string? GpuHandle { get; init; }
    /// <summary>Elapsed time around the managed backend.Transfer call, including wrapper overhead; not pure driver or bus time.</summary>
    public double? TransferDurationMicroseconds { get; init; }
    /// <summary>Cached file-version metadata of the System32 NVAPI library; null when unavailable. No SDK query is performed.</summary>
    public string? LibraryVersion { get; init; }
}

/// <summary>
/// A bounded immutable window, not an unlimited journal. Evicted records were received before leaving this window;
/// MissingCount is a gauge of observed failure ordinals whose context is still not received, and can decrease on backfill.
/// A worker dying before reporting counters is unknown. Receiving a detail does not prove that a journal persisted it.
/// </summary>
public sealed record RgbNativeFailureHistory
{
    public const int MaximumRecords = 8;
    private IReadOnlyList<RgbNativeOperationResult> _records = Array.Empty<RgbNativeOperationResult>();
    public int Capacity { get; init; } = MaximumRecords;
    public long TotalFailures { get; init; }
    public long EvictedCount { get; init; }
    public long MissingCount { get; init; }
    public IReadOnlyList<RgbNativeOperationResult> Records
    {
        get => _records;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count > MaximumRecords) throw new ArgumentOutOfRangeException(nameof(value));
            _records = Array.AsReadOnly(value.ToArray());
        }
    }
}

/// <summary>
/// Immutable cached evidence about an RGB control channel. Reading it never calls the SDK or acquires its lock.
/// A successful probe/SDK output is not proof of emitted light, GPU telemetry health or a physical connection.
/// Counters saturate instead of wrapping. Source/name/diagnostics are bounded by the producer.
/// </summary>
public sealed record RgbControlStatus
{
    private EngineText? _diagnosticText;
    public string Source { get; init; } = "NVAPI/AuraI2CEx";
    public string ControllerName { get; init; } = string.Empty;
    public int ControllerLedCount { get; init; }
    public bool ControlAvailable { get; init; }
    public bool Initializing { get; init; }
    public bool InitializationRetryNeeded { get; init; }
    public RgbAdapterObservation AdapterObservation { get; init; }
    public DateTimeOffset? LastAdapterEnumerationUtc { get; init; }
    public DateTimeOffset? LastAdapterSeenUtc { get; init; }
    public bool? LastOutputAccepted { get; init; }
    public DateTimeOffset? LastOutputAttemptUtc { get; init; }
    public DateTimeOffset? LastOutputAcceptedUtc { get; init; }
    public long? LastOutputAcceptedNativeSequence { get; init; }
    public long ConsecutiveOutputFailures { get; init; }
    public long TotalOutputFailures { get; init; }
    public long TotalNativeFailures { get; init; }
    public long InitializationAttempts { get; init; }
    public RgbNativeOperationResult? LastNativeOperation { get; init; }
    /// <summary>Retained across subsequent successful calls and probes, so recovery never erases the last error.</summary>
    public RgbNativeOperationResult? LastNativeFailure { get; init; }
    /// <summary>Null for legacy/unavailable history. Cached immutable evidence; inspecting it performs no SDK or I/O call.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RgbNativeFailureHistory? NativeFailureHistory { get; init; }
    /// <summary>Identity of the currently owned isolated RGB child; null after confirmed exit.</summary>
    public int? WorkerProcessId { get; init; }
    /// <summary>Monotonic reserved generation attempt; a failed launch may not have created a child.</summary>
    public long WorkerGeneration { get; init; }
    /// <summary>Replacement sessions actually returned by the launcher, excluding the first owned session.</summary>
    public long WorkerRestartCount { get; init; }
    /// <summary>Process/protocol failures, separate from unmodified native SDK results.</summary>
    public long WorkerTransportFailures { get; init; }
    public string? LastWorkerRestartReason { get; init; }
    public DateTimeOffset? LastWorkerFailureUtc { get; init; }
    public string? LastWorkerFailure { get; init; }
    // EngineText is an existing mutable wire DTO. Defensive copies keep the cached status genuinely immutable.
    public EngineText? DiagnosticText { get => Copy(_diagnosticText); init => _diagnosticText = Copy(value); }
    public string? Diagnostic => _diagnosticText?.ToPortuguese();
    public bool RecoveryPending { get; init; }
    public int RecoveryAttemptCount { get; init; }
    public int RecoveryBackoffStep { get; init; }
    /// <summary>Presentation estimate only. The scheduler uses Stopwatch timestamps, never this UTC value.</summary>
    public DateTimeOffset? NextInitializationAttemptUtc { get; init; }

    private static EngineText? Copy(EngineText? value) => value == null ? null : new EngineText
    {
        Code = value.Code,
        Args = value.Args.Select(a => new EngineTextArg { S = a.S, N = a.N, T = Copy(a.T) }).ToList()
    };
}

public interface IRgbControlStatusProvider
{
    RgbControlStatus ControlStatus { get; }
}
