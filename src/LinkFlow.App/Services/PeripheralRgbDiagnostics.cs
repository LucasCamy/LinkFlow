using System.Diagnostics;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

public enum PeripheralRgbOperationKind { Initialize, SetColor, Resend }

/// <summary>Arguments passed to the existing backend, before its SDK percentage conversion.</summary>
public sealed record PeripheralRgbColor(byte R, byte G, byte B, byte Brightness);

public sealed record PeripheralRgbFrameSample
{
    public long ConfigurationRevision { get; init; }
    public RgbEffect Effect { get; init; }
    public byte EffectSpeed { get; init; }
    public bool SyncPeripherals { get; init; }
    public long FrameTimeMilliseconds { get; init; }
    public DateTimeOffset CalculatedAtUtc { get; init; }
    public long CalculatedTimestamp { get; init; }
    public PeripheralRgbColor? Color { get; init; }
}

/// <summary>A backend span can include SDK settling, shutdown or release; it is not a native stack trace.</summary>
public sealed record PeripheralRgbBackendOperation
{
    public long RequestId { get; init; }
    public PeripheralRgbOperationKind Kind { get; init; }
    public long ConfigurationRevision { get; init; }
    public RgbEffect Effect { get; init; }
    public bool SyncPeripherals { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public long StartedTimestamp { get; init; }
    public PeripheralRgbFrameSample? LatestSample { get; init; }
    public int? ResendAttempt { get; init; }
    public int? ResendAttemptLimit { get; init; }
}

public sealed record PeripheralRgbBackendCompletion
{
    public PeripheralRgbBackendOperation Operation { get; init; } = new();
    public DateTimeOffset CompletedAtUtc { get; init; }
    public long CompletedTimestamp { get; init; }
    public double DurationMilliseconds { get; init; }
    public bool Succeeded { get; init; }
    public bool? SdkAccepted { get; init; }
    public PeripheralRgbColor? ReturnedColor { get; init; }
    public string? Error { get; init; }
}

/// <summary>Published by the RGB worker. Readers only load this immutable reference; they never query the SDK.</summary>
public sealed record PeripheralRgbDiagnosticSnapshot
{
    public int Schema { get; init; } = 1;
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset BridgeStartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public long BridgeStartedTimestamp { get; init; } = Stopwatch.GetTimestamp();
    public long TimestampFrequency { get; init; } = Stopwatch.Frequency;
    public long PublicationSequence { get; init; }
    public DateTimeOffset PublishedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public long PublishedTimestamp { get; init; } = Stopwatch.GetTimestamp();
    public bool WorkerStopped { get; init; }
    public string? StatusText { get; init; }
    public bool HasIssue { get; init; }
    public bool? BackendAvailable { get; init; }
    public long AvailabilityReadFailures { get; init; }
    public string? LastAvailabilityError { get; init; }
    public long ConfigurationRevision { get; init; }
    public RgbEffect Effect { get; init; }
    public bool SyncPeripherals { get; init; }
    public long CalculatedFrames { get; init; }
    public long OperationRequests { get; init; }
    public long OperationCompletions { get; init; }
    public long OperationFailures { get; init; }
    public long InitializeRequests { get; init; }
    public long SetColorRequests { get; init; }
    public long ResendRequests { get; init; }
    public long AcceptedColorRequests { get; init; }
    public long AcceptedColorChanges { get; init; }
    public int DistinctAcceptedColors { get; init; }
    public int DistinctAcceptedColorsLimit { get; init; } = PeripheralRgbDiagnosticRecorder.ColorLimit;
    public bool DistinctAcceptedColorsSaturated { get; init; }
    public PeripheralRgbColor? AcceptedChannelMin { get; init; }
    public PeripheralRgbColor? AcceptedChannelMax { get; init; }
    public PeripheralRgbFrameSample? LastCalculated { get; init; }
    public PeripheralRgbFrameSample? LastAccepted { get; init; }
    public PeripheralRgbColor? LastAcceptedColor { get; init; }
    public DateTimeOffset? LastAcceptedAtUtc { get; init; }
    public long? LastAcceptedTimestamp { get; init; }
    public DateTimeOffset? LastColorChangeAcceptedAtUtc { get; init; }
    public long? LastColorChangeAcceptedTimestamp { get; init; }
    public PeripheralRgbBackendOperation? InFlight { get; init; }
    public PeripheralRgbBackendCompletion? LastCompleted { get; init; }
    public long LoopFailures { get; init; }
    public string? LastLoopError { get; init; }
    public long ObserverFailures { get; init; }
    public string? LastObserverError { get; init; }
    public bool PhysicalOutputVerified => false;
    public string SustainedOwnership => "Unknown";
    public string Evidence { get; init; } = "Backend/SDK acceptance only; sustained ownership and physical light output are unverified.";
}

/// <summary>Single writer on the existing bridge thread; bounded state, no I/O and no backend reads.</summary>
internal sealed class PeripheralRgbDiagnosticRecorder
{
    internal const int ColorLimit = 512;
    private PeripheralRgbDiagnosticSnapshot _snapshot = new();
    private readonly HashSet<uint> _acceptedColors = new(ColorLimit);
    internal PeripheralRgbDiagnosticSnapshot Snapshot => Volatile.Read(ref _snapshot);
    private static long Next(long value) => value == long.MaxValue ? value : value + 1;
    internal static string ErrorText(Exception error)
    {
        string text = error.GetType().Name + ": " + error.Message;
        return text.Length <= 512 ? text : text[..512];
    }

    private void Publish(PeripheralRgbDiagnosticSnapshot value) => Volatile.Write(ref _snapshot, value with
    {
        PublicationSequence = Next(_snapshot.PublicationSequence),
        PublishedAtUtc = DateTimeOffset.UtcNow,
        PublishedTimestamp = Stopwatch.GetTimestamp()
    });

    internal void ObserveConfiguration(ControlConfig config)
    {
        var s = Snapshot;
        if (s.ConfigurationRevision == config.Revision && s.Effect == config.Rgb.Effect &&
            s.SyncPeripherals == config.Rgb.SyncPeripherals) return;
        Publish(s with { ConfigurationRevision = config.Revision, Effect = config.Rgb.Effect,
            SyncPeripherals = config.Rgb.SyncPeripherals });
    }

    internal void Calculated(ControlConfig config, long frame, (byte R, byte G, byte B, byte Brightness)? color)
    {
        var s = Snapshot;
        var sample = new PeripheralRgbFrameSample { ConfigurationRevision = config.Revision, Effect = config.Rgb.Effect,
            EffectSpeed = config.Rgb.EffectSpeed, SyncPeripherals = config.Rgb.SyncPeripherals,
            FrameTimeMilliseconds = frame, CalculatedAtUtc = DateTimeOffset.UtcNow, CalculatedTimestamp = Stopwatch.GetTimestamp(),
            Color = color is { } c ? new(c.R, c.G, c.B, c.Brightness) : null };
        Publish(s with { ConfigurationRevision = config.Revision, Effect = config.Rgb.Effect,
            SyncPeripherals = config.Rgb.SyncPeripherals, CalculatedFrames = Next(s.CalculatedFrames), LastCalculated = sample,
            InFlight = s.InFlight is { Kind: PeripheralRgbOperationKind.Resend } pending ? pending with { LatestSample = sample } : s.InFlight });
    }

    internal PeripheralRgbBackendOperation Begin(PeripheralRgbOperationKind kind, ControlConfig config)
    {
        var s = Snapshot;
        var operation = new PeripheralRgbBackendOperation { RequestId = Next(s.OperationRequests), Kind = kind,
            ConfigurationRevision = config.Revision, Effect = config.Rgb.Effect, SyncPeripherals = config.Rgb.SyncPeripherals,
            StartedAtUtc = DateTimeOffset.UtcNow, StartedTimestamp = Stopwatch.GetTimestamp(),
            LatestSample = kind == PeripheralRgbOperationKind.SetColor ? s.LastCalculated : null };
        Publish(s with { ConfigurationRevision = config.Revision, Effect = config.Rgb.Effect,
            SyncPeripherals = config.Rgb.SyncPeripherals, OperationRequests = operation.RequestId, InFlight = operation,
            InitializeRequests = kind == PeripheralRgbOperationKind.Initialize ? Next(s.InitializeRequests) : s.InitializeRequests,
            SetColorRequests = kind == PeripheralRgbOperationKind.SetColor ? Next(s.SetColorRequests) : s.SetColorRequests,
            ResendRequests = kind == PeripheralRgbOperationKind.Resend ? Next(s.ResendRequests) : s.ResendRequests });
        return operation;
    }

    internal void ResendAttempt(int attempt, int total)
    {
        var s = Snapshot;
        if (s.InFlight is { Kind: PeripheralRgbOperationKind.Resend } operation)
            Publish(s with { InFlight = operation with { ResendAttempt = attempt, ResendAttemptLimit = total } });
    }

    internal void Complete(PeripheralRgbBackendOperation operation, bool success,
        (byte R, byte G, byte B, byte Brightness)? returnedColor = null, Exception? error = null,
        bool? backendAvailable = null, Exception? availabilityError = null)
    {
        var s = Snapshot;
        operation = s.InFlight is { } pending && pending.RequestId == operation.RequestId ? pending : operation;
        long completed = Stopwatch.GetTimestamp();
        var color = returnedColor is { } c ? new PeripheralRgbColor(c.R, c.G, c.B, c.Brightness) : null;
        var completion = new PeripheralRgbBackendCompletion { Operation = operation, CompletedAtUtc = DateTimeOffset.UtcNow,
            CompletedTimestamp = completed, DurationMilliseconds = Stopwatch.GetElapsedTime(operation.StartedTimestamp, completed).TotalMilliseconds,
            Succeeded = success, SdkAccepted = error != null || operation.Kind == PeripheralRgbOperationKind.Initialize ? null :
                operation.Kind == PeripheralRgbOperationKind.Resend ? success ? true : null : success,
            ReturnedColor = color, Error = error == null ? null : ErrorText(error) };
        s = s with { InFlight = null, LastCompleted = completion, OperationCompletions = Next(s.OperationCompletions),
            OperationFailures = !success ? Next(s.OperationFailures) : s.OperationFailures,
            BackendAvailable = backendAvailable,
            AvailabilityReadFailures = availabilityError == null ? s.AvailabilityReadFailures : Next(s.AvailabilityReadFailures),
            LastAvailabilityError = availabilityError == null ? null : ErrorText(availabilityError) };
        if (success && color != null)
        {
            uint key = ((uint)color.R << 24) | ((uint)color.G << 16) | ((uint)color.B << 8) | color.Brightness;
            bool saturated = s.DistinctAcceptedColorsSaturated;
            if (!_acceptedColors.Contains(key))
            {
                if (_acceptedColors.Count < ColorLimit) _acceptedColors.Add(key);
                else saturated = true;
            }
            bool changed = s.LastAcceptedColor is { } previousColor && previousColor != color;
            // An opaque backend may return a color without using readColor. Keep that result but do
            // not invent a frame/revision from an earlier calculation when no callback was observed.
            var sample = operation.LatestSample is { } observed ? observed with { Color = color } : null;
            static PeripheralRgbColor Min(PeripheralRgbColor? previous, PeripheralRgbColor current) => previous == null ? current : new(
                Math.Min(previous.R, current.R), Math.Min(previous.G, current.G), Math.Min(previous.B, current.B), Math.Min(previous.Brightness, current.Brightness));
            static PeripheralRgbColor Max(PeripheralRgbColor? previous, PeripheralRgbColor current) => previous == null ? current : new(
                Math.Max(previous.R, current.R), Math.Max(previous.G, current.G), Math.Max(previous.B, current.B), Math.Max(previous.Brightness, current.Brightness));
            s = s with { AcceptedColorRequests = Next(s.AcceptedColorRequests),
                AcceptedColorChanges = changed ? Next(s.AcceptedColorChanges) : s.AcceptedColorChanges,
                DistinctAcceptedColors = _acceptedColors.Count, DistinctAcceptedColorsSaturated = saturated,
                AcceptedChannelMin = Min(s.AcceptedChannelMin, color), AcceptedChannelMax = Max(s.AcceptedChannelMax, color),
                LastAccepted = sample, LastAcceptedColor = color,
                LastAcceptedAtUtc = completion.CompletedAtUtc, LastAcceptedTimestamp = completed,
                LastColorChangeAcceptedAtUtc = changed ? completion.CompletedAtUtc : s.LastColorChangeAcceptedAtUtc,
                LastColorChangeAcceptedTimestamp = changed ? completed : s.LastColorChangeAcceptedTimestamp };
        }
        Publish(s);
    }

    internal void ObserveAvailability(bool? available, Exception? error = null)
    {
        var s = Snapshot;
        if (s.BackendAvailable != available || error != null || s.LastAvailabilityError != null)
            Publish(s with { BackendAvailable = available,
                AvailabilityReadFailures = error == null ? s.AvailabilityReadFailures : Next(s.AvailabilityReadFailures),
                LastAvailabilityError = error == null ? null : ErrorText(error) });
    }

    internal void StatusChanged(string text, bool hasIssue) => Publish(Snapshot with
        { StatusText = text.Length <= 512 ? text : text[..512], HasIssue = hasIssue });

    internal void LoopFailed(Exception error) => Publish(Snapshot with
        { LoopFailures = Next(Snapshot.LoopFailures), LastLoopError = ErrorText(error) });
    internal void ObserverFailed(Exception error) => Publish(Snapshot with
        { ObserverFailures = Next(Snapshot.ObserverFailures), LastObserverError = ErrorText(error) });
    internal void Stopped() => Publish(Snapshot with { WorkerStopped = true });
}
