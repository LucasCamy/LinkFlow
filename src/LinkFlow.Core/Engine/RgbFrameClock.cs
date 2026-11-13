namespace LinkFlow.Core;

/// <summary>Agenda quadros RGB no relógio comum e antecipa o envio pelo tempo de escrita observado.</summary>
public sealed class RgbFrameClock
{
    private long _lastFrame;
    private int _lastInterval;
    private double? _writeMs;

    public const int PeripheralIntervalMs = 50;

    // Cadências distintas compartilham a mesma origem e coincidem nos múltiplos de 25 ms.
    public static int IntervalMs(TimeSpan minInterval) =>
        (int)Math.Max(1, Math.Ceiling((minInterval.TotalMilliseconds + 10) / 25)) * 25;

    public long FrameTime(long now, int intervalMs)
    {
        long frame = (now + LeadMs(intervalMs)) / intervalMs * intervalMs;
        return intervalMs == _lastInterval ? Math.Max(frame, _lastFrame) : frame;
    }

    public bool IsDue(long frameTime, int intervalMs) => intervalMs != _lastInterval || frameTime > _lastFrame;

    public void RecordFrame(long frameTime, int intervalMs, TimeSpan? writeTime = null)
    {
        _lastFrame = frameTime;
        _lastInterval = intervalMs;
        if (writeTime is { } duration)
        {
            double sample = Math.Clamp(duration.TotalMilliseconds, 0, intervalMs * 0.75);
            _writeMs = _writeMs is { } previous ? previous + (sample - previous) * 0.25 : sample;
        }
    }

    public TimeSpan UntilNextFrame(long now, int intervalMs) => intervalMs != _lastInterval
        ? TimeSpan.Zero
        : TimeSpan.FromMilliseconds(Math.Max(0, _lastFrame + intervalMs - LeadMs(intervalMs) - now));

    private int LeadMs(int intervalMs) => Math.Min((int)Math.Ceiling(_writeMs ?? 0), intervalMs * 3 / 4);
}
