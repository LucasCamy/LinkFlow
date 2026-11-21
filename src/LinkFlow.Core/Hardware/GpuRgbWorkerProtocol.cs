namespace LinkFlow.Core;

/// <summary>Private, bounded request/reply protocol for the service-owned GPU RGB child.</summary>
internal static class GpuRgbWorkerProtocol
{
    public const int Version = 1;
    public const int MaximumRequestBytes = 1024;
    public const int MaximumReplyBytes = 16 * 1024;
    public const string Initialize = "initialize";
    public const string Color = "color";
}

internal sealed record GpuRgbWorkerRequest
{
    public int Version { get; init; } = GpuRgbWorkerProtocol.Version;
    public long Generation { get; init; }
    public long RequestId { get; init; }
    public string Operation { get; init; } = string.Empty;
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    public byte Brightness { get; init; }
}

internal sealed record GpuRgbWorkerReply
{
    public int Version { get; init; } = GpuRgbWorkerProtocol.Version;
    public long Generation { get; init; }
    public long RequestId { get; init; }
    public bool Accepted { get; init; }
    public RgbControlStatus Status { get; init; } = new();
}

/// <summary>
/// One owned child, one exchange at a time. Dispose must confirm child termination before a replacement is allowed;
/// inability to terminate is an error, never permission to start a second physical controller.
/// </summary>
internal interface IGpuRgbWorkerSession : IDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    GpuRgbWorkerReply Exchange(GpuRgbWorkerRequest request, TimeSpan timeout);
}
