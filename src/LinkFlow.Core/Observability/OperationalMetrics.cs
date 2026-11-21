using System.Diagnostics.Metrics;

namespace LinkFlow.Core;

/// <summary>In-process instruments; no network exporter or background metrics server is enabled.</summary>
public sealed class OperationalMetrics : IDisposable
{
    private readonly Meter _meter = new("LinkFlow.Engine", typeof(OperationalMetrics).Assembly.GetName().Version?.ToString());
    private readonly Counter<long> _reads, _commands, _recoveries, _storage, _dropped;
    private readonly Histogram<double> _duration, _recoveryDuration;
    private readonly Counter<long> _rgbControlRecoveries;
    private readonly Histogram<double> _rgbControlRecoveryDuration;
    private readonly Counter<long> _queryBytes, _queryRecords, _queryFrames;

    public OperationalMetrics()
    {
        _reads = _meter.CreateCounter<long>("linkflow.sensor.reads", "{read}", "Read attempts by sensor and outcome");
        _commands = _meter.CreateCounter<long>("linkflow.control.commands", "{command}", "Command outcomes without claiming physical readback");
        _recoveries = _meter.CreateCounter<long>("linkflow.device.recoveries", "{recovery}");
        _storage = _meter.CreateCounter<long>("linkflow.history.writes", "{write}");
        _dropped = _meter.CreateCounter<long>("linkflow.history.dropped", "{record}");
        _duration = _meter.CreateHistogram<double>("linkflow.engine.operation.duration", "s");
        _recoveryDuration = _meter.CreateHistogram<double>("linkflow.device.recovery.duration", "s");
        _rgbControlRecoveries = _meter.CreateCounter<long>("linkflow.rgb.control.recoveries", "{recovery}", "RGB channel recovery, not physical device reconnection");
        _rgbControlRecoveryDuration = _meter.CreateHistogram<double>("linkflow.rgb.control.recovery.duration", "s");
        _queryBytes = _meter.CreateCounter<long>("linkflow.history.query.bytes", "B", "Logical JSON bytes read by history queries");
        _queryRecords = _meter.CreateCounter<long>("linkflow.history.query.records", "{record}");
        _queryFrames = _meter.CreateCounter<long>("linkflow.history.query.frames", "{frame}", "Frames deserialized for history queries");
    }

    public void Read(string source, bool success) => _reads.Add(1, new("sensor", source), new("outcome", success ? "valid" : "failed"));
    public void Command(string kind, string outcome) => _commands.Add(1, new("kind", kind), new("outcome", outcome));
    public void Recovery(string device, double seconds)
    {
        _recoveries.Add(1, new KeyValuePair<string, object?>("device", device));
        _recoveryDuration.Record(Math.Max(0, seconds), new KeyValuePair<string, object?>("device", device));
    }
    internal void RgbControlRecovery(string channel, double seconds)
    {
        _rgbControlRecoveries.Add(1, new KeyValuePair<string, object?>("channel", channel));
        _rgbControlRecoveryDuration.Record(Math.Max(0, seconds), new KeyValuePair<string, object?>("channel", channel));
    }
    public void Duration(string lane, TimeSpan elapsed) => _duration.Record(Math.Max(0, elapsed.TotalSeconds), new KeyValuePair<string, object?>("operation", lane));
    internal void Storage(bool success) => _storage.Add(1, new KeyValuePair<string, object?>("outcome", success ? "success" : "failure"));
    internal void Dropped() => _dropped.Add(1);
    internal void Query(HistoryQueryExecution execution)
    {
        var path = new KeyValuePair<string, object?>("path", execution.MemoryCacheUsed ? "memory" : "disk");
        _queryBytes.Add(execution.BytesRead, path);
        _queryRecords.Add(execution.RecordsScanned, path);
        _queryFrames.Add(execution.FramesDeserialized, path);
        Duration(execution.Succeeded ? "history.query" : "history.query.failed", TimeSpan.FromMilliseconds(execution.DurationMilliseconds));
    }
    public void Dispose() => _meter.Dispose();
}
