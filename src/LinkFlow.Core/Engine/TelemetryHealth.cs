using System.Diagnostics;

namespace LinkFlow.Core;

public enum ReadingValidity { Fresh, Stale, Unavailable, Invalid, NotSupported }

/// <summary>Quality belongs to an acquisition, never to the time a snapshot was published.</summary>
public sealed class TelemetryHealth
{
    public string Key { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public ReadingValidity Validity { get; set; } = ReadingValidity.Unavailable;
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public double? AgeSeconds { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? Reason { get; set; }
}

/// <summary>A missing selected source defaults to protection instead of reusing another sensor's curve.</summary>
public enum CurveFallbackPolicy { SafeSpeed, AllowOtherCpuGpu, AllowAnyAvailable }

public sealed partial class FanCurveConfig
{
    public CurveFallbackPolicy FallbackPolicy { get; set; } = CurveFallbackPolicy.SafeSpeed;
    public byte FallbackMinPercent { get; set; } = 70;
}

public sealed record CurveTemperatureSelection(float? Temperature, CurveSource RequestedSource,
    CurveSource? EffectiveSource, bool IsFallback);

internal sealed class AcquisitionHealth(string key, string source)
{
    public string Key { get; } = key;
    public string Source { get; } = source;
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public long LastSuccessTick { get; private set; }
    public long? MissingSince { get; private set; }
    public ReadingValidity State { get; private set; } = ReadingValidity.Unavailable;
    public int Failures { get; private set; }
    public string? Reason { get; private set; }

    public void Record(bool success, bool invalid, string? reason, long now, DateTimeOffset utc)
    {
        LastAttemptUtc = utc;
        if (success)
        {
            LastSuccessTick = now;
            LastSuccessUtc = utc;
            MissingSince = null;
            State = ReadingValidity.Fresh;
            Failures = 0;
            Reason = null;
        }
        else
        {
            MissingSince ??= now;
            State = invalid ? ReadingValidity.Invalid : LastSuccessUtc != null ? ReadingValidity.Stale : ReadingValidity.Unavailable;
            Failures++;
            Reason = reason ?? "reading-unavailable";
        }
    }

    public ReadingValidity Validity(long now, TimeSpan maxAge) => State == ReadingValidity.Fresh &&
        Stopwatch.GetElapsedTime(LastSuccessTick, now) > maxAge ? ReadingValidity.Stale : State;

    public TelemetryHealth Snapshot(long now, TimeSpan maxAge) => new()
    {
        Key = Key,
        Source = Source,
        Validity = Validity(now, maxAge),
        LastAttemptUtc = LastAttemptUtc,
        LastSuccessUtc = LastSuccessUtc,
        AgeSeconds = LastSuccessTick == 0 ? null : Math.Max(0, Stopwatch.GetElapsedTime(LastSuccessTick, now).TotalSeconds),
        ConsecutiveFailures = Failures,
        Reason = Validity(now, maxAge) == ReadingValidity.Stale && Reason == null ? "acquisition-overdue" : Reason
    };
}
