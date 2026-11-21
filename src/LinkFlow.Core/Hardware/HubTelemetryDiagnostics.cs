namespace LinkFlow.Core;

/// <summary>Optional read-only access to packets already acquired by the hub's normal telemetry lane.</summary>
public interface IHubTelemetryDiagnostics
{
    /// <summary>Returns cached immutable data; must never open, read, write, or wait on the hardware.</summary>
    HubTelemetryDiagnostics GetTelemetryDiagnostics();
}

public sealed record HubTelemetryDiagnostics
{
    public bool Supported { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string? Reason { get; init; }
    public HubTelemetryDiagnosticPacket? Rpm { get; init; }
    public HubTelemetryDiagnosticPacket? Temperature { get; init; }
}

/// <summary>A received HID report, not a new measurement or proof that its readings are valid.</summary>
public sealed record HubTelemetryDiagnosticPacket
{
    public string DataType { get; init; } = string.Empty;
    public string Firmware { get; init; } = string.Empty;
    public int ReceivedBytes { get; init; }
    public int? DeclaredRecordCount { get; init; }
    public DateTimeOffset AcquiredAtUtc { get; init; }
    public long AcquiredAtTimestamp { get; init; }
    public long TimestampFrequency { get; init; }
    public double AgeSeconds { get; init; }
    public string? ParseRejection { get; init; }
    public string Hex { get; init; } = string.Empty;
}

/// <summary>Exactly two immutable slots; snapshots neither perform I/O nor take the native driver lock.</summary>
internal sealed class HubTelemetryDiagnosticsCache(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private HubTelemetryDiagnosticPacket? _rpm, _temperature;

    internal bool Capture(ReadOnlySpan<byte> response, ReadOnlySpan<byte> type, string firmware)
    {
        bool rpm = type.SequenceEqual(LinkHubProtocol.DataTypeSpeeds);
        bool temperature = type.SequenceEqual(LinkHubProtocol.DataTypeTemperatures);
        if ((!rpm && !temperature) || response.Length > LinkHubProtocol.PacketSizeOut || !LinkHubProtocol.IsResponse(response, type))
            return false;
        // Keep even a parser-rejected response for diagnosis, but never pad a short native read.
        LinkHubProtocol.TelemetryRecordCount(response, type, out var rejection);
        var packet = new HubTelemetryDiagnosticPacket
        {
            DataType = Convert.ToHexString(type), Firmware = firmware.Length > 64 ? firmware[..64] : firmware,
            ReceivedBytes = response.Length, DeclaredRecordCount = response.Length > 7 ? response[7] : null,
            AcquiredAtUtc = _time.GetUtcNow(), AcquiredAtTimestamp = _time.GetTimestamp(),
            TimestampFrequency = _time.TimestampFrequency, ParseRejection = rejection,
            Hex = Convert.ToHexString(response)
        };
        if (rpm) Volatile.Write(ref _rpm, packet);
        else Volatile.Write(ref _temperature, packet);
        return true;
    }

    internal HubTelemetryDiagnostics Snapshot()
    {
        var rpm = Volatile.Read(ref _rpm);
        var temperature = Volatile.Read(ref _temperature);
        long now = _time.GetTimestamp();
        HubTelemetryDiagnosticPacket? Age(HubTelemetryDiagnosticPacket? packet) => packet == null ? null : packet with
        {
            AgeSeconds = Math.Max(0, _time.GetElapsedTime(packet.AcquiredAtTimestamp, now).TotalSeconds)
        };
        return new()
        {
            Supported = true, Provider = nameof(CorsairLinkHub),
            Reason = rpm == null && temperature == null ? "no-packets-received" : null,
            Rpm = Age(rpm), Temperature = Age(temperature)
        };
    }
}
