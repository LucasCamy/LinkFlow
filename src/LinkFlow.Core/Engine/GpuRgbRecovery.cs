using System.Diagnostics;

namespace LinkFlow.Core;

/// <summary>
/// Monotonic scheduling for unavailable GPU RGB access. A successful probe or a few accepted frames do not reset
/// its backoff; only a continuously successful output interval does. No SDK calls or event suppression occur here.
/// Owned by the auxiliary lane. Explicit user resends remain separately bounded by their existing attempt budget.
/// </summary>
internal sealed class GpuRgbRecovery
{
    private readonly long _first, _maximum, _stable;
    private long? _acceptedSince, _lastAccepted;

    public GpuRgbRecovery(TimeSpan first, TimeSpan maximum, TimeSpan stable)
    {
        _first = Ticks(Clamp(first, TimeSpan.FromMilliseconds(10), TimeSpan.FromDays(1)));
        _maximum = Math.Max(_first, Ticks(Clamp(maximum, TimeSpan.FromMilliseconds(10), TimeSpan.FromDays(1))));
        _stable = Ticks(Clamp(stable, TimeSpan.FromMilliseconds(10), TimeSpan.FromDays(1)));
    }

    public bool Pending { get; private set; }
    public int Attempts { get; private set; }
    public int BackoffStep { get; private set; }
    public long? RetryAt { get; private set; }

    public void Unavailable(long now)
    {
        _acceptedSince = _lastAccepted = null;
        if (Pending) return;
        Pending = true;
        RetryAt = Add(now, _first);
    }

    public bool Due(long now) => Pending && RetryAt is { } retry && now >= retry;

    public void InitializationAttempted(long now)
    {
        Unavailable(now);
        if (Attempts < int.MaxValue) Attempts++;
        BackoffStep = Math.Min(Attempts, 30);
        long delay = _first;
        for (int i = 0; i < BackoffStep && delay < _maximum; i++)
            delay = Math.Min(_maximum, delay * 2);
        RetryAt = Add(now, delay);
    }

    public void OutputAccepted(long now)
    {
        if (!Pending) return;
        _acceptedSince ??= now;
        _lastAccepted = now;
    }

    public void OutputRejected() => _acceptedSince = _lastAccepted = null;

    public void Poll(long now)
    {
        // Require an accepted output at/after the far end too; mere silence after one probe is not stability evidence.
        if (Pending && _acceptedSince is { } accepted && _lastAccepted is { } last && now >= last &&
            last >= accepted && last - accepted >= _stable)
            Reset();
    }

    public void Reset()
    {
        Pending = false;
        Attempts = BackoffStep = 0;
        RetryAt = _acceptedSince = _lastAccepted = null;
    }

    private static long Add(long now, long delay) => now > long.MaxValue - delay ? long.MaxValue : now + delay;
    private static long Ticks(TimeSpan duration) => Math.Max(1, (long)(duration.TotalSeconds * Stopwatch.Frequency));
    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) => value < minimum ? minimum : value > maximum ? maximum : value;
}
