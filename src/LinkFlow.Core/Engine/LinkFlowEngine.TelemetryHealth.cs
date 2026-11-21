using System.Diagnostics;

namespace LinkFlow.Core;

public sealed partial class LinkFlowEngine
{
    private readonly Dictionary<string, AcquisitionHealth> _acquisitions = new(StringComparer.Ordinal);
    private readonly Timer _safetySupervisor;
    private long _engineStartedAt;
    private long? _supervisedCurveMissingSince;
    private int _supervisingSafety;
    private long _lastPartialRpmReconnect;
    private CurveTemperatureSelection _curveSelection = new(null, CurveSource.MaxCpuGpu, null, false);

    private TimeSpan SafetyPeriod => _opt.TelemetryInterval < TimeSpan.FromMilliseconds(250)
        ? _opt.TelemetryInterval > TimeSpan.FromMilliseconds(10) ? _opt.TelemetryInterval : TimeSpan.FromMilliseconds(10)
        : TimeSpan.FromMilliseconds(250);

    private AcquisitionHealth Acquisition(string key, string source) =>
        _acquisitions.TryGetValue(key, out var health) ? health : _acquisitions[key] = new AcquisitionHealth(key, source);

    private void TrackValue(string key, string source, float? value, float min, float max, long now, DateTimeOffset utc)
    {
        bool valid = value is { } v && float.IsFinite(v) && v >= min && v <= max;
        Acquisition(key, source).Record(valid, value != null && !valid, value == null ? "not-reported" : "outside-valid-range", now, utc);
    }

    // Caller holds _stateLock. The individual values can disappear independently within one provider read.
    private void RecordSensorAcquisition(HardwareTelemetry t)
    {
        long now = Stopwatch.GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        Acquisition("sensors", "LibreHardwareMonitor").Record(true, false, null, now, utc);
        TrackValue("cpu.temp", "CPU", t.CpuSensorAvailable ? t.CpuTemperature : null, 0.1f, 150, now, utc);
        TrackValue("cpu.load", "CPU", t.CpuLoad, 0, 100, now, utc);
        TrackValue("cpu.power", "CPU", t.CpuPower, 0, 5000, now, utc);
        TrackValue("cpu.clock", "CPU", t.CpuClockMhz, 0, 20000, now, utc);
        TrackValue("gpu.temp", "GPU", t.GpuTemperature, 0.1f, 150, now, utc);
        TrackValue("gpu.hotspot", "GPU", t.GpuHotspotTemperature, 0.1f, 200, now, utc);
        TrackValue("gpu.load", "GPU", t.GpuLoad, 0, 100, now, utc);
        TrackValue("gpu.power", "GPU", t.GpuPower, 0, 5000, now, utc);
        TrackValue("gpu.clock", "GPU", t.GpuClockMhz, 0, 20000, now, utc);
        TrackValue("gpu.memory_clock", "GPU", t.GpuMemClockMhz, 0, 50000, now, utc);
        TrackValue("gpu.vram_used", "GPU", t.GpuVramUsedGb, 0, 4096, now, utc);
        TrackValue("gpu.vram_total", "GPU", t.GpuVramTotalGb, 0, 4096, now, utc);
        TrackValue("gpu.fan1", "GPU", t.GpuFanRpm1, 0, 50000, now, utc);
        TrackValue("gpu.fan2", "GPU", t.GpuFanRpm2, 0, 50000, now, utc);
        TrackValue("gpu.fan_percent", "GPU", t.GpuFanPercent, 0, 100, now, utc);
        TrackValue("ram.usage", "RAM", t.RamUsagePercent, 0, 100, now, utc);
        TrackValue("ram.used", "RAM", t.RamUsedGb, 0, 65536, now, utc);
        TrackValue("ram.total", "RAM", t.RamTotalGb, 0, 65536, now, utc);
    }

    private void RecordSensorFailure(Exception ex)
    {
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            var utc = DateTimeOffset.UtcNow;
            Acquisition("sensors", "LibreHardwareMonitor").Record(false, false, ex.GetType().Name, now, utc);
            foreach (var h in _acquisitions.Values.Where(h => h.Source is "CPU" or "GPU" or "RAM"))
                h.Record(false, false, "provider-read-failed", now, utc);
            // Preserve identities, never preserve values after a failed acquisition.
            _telemetry = new HardwareTelemetry { CpuName = _telemetry.CpuName, GpuName = _telemetry.GpuName };
        }
    }

    private TimeSpan FreshnessFor(string key) => key.StartsWith("vol:", StringComparison.Ordinal) ? TimeSpan.FromSeconds(30) : _opt.TelemetryFreshnessTimeout;
    private bool IsFresh(string key, long? now = null) => _acquisitions.TryGetValue(key, out var health) &&
        health.Validity(now ?? Stopwatch.GetTimestamp(), FreshnessFor(key)) == ReadingValidity.Fresh;

    private float? FreshValue(string key, float? value) => IsFresh(key) ? value : null;
    private int? FreshInt(string key, int? value) => IsFresh(key) ? value : null;

    private List<TelemetryHealth> SnapshotTelemetryHealth() => _acquisitions.Values
        .Select(health => health.Snapshot(Stopwatch.GetTimestamp(), FreshnessFor(health.Key))).ToList();

    private void RecordOptionalAcquisitions(NetworkState? network, IReadOnlyList<VolumeState> volumes, IReadOnlyList<DiskIoState>? disks)
    {
        long now = Stopwatch.GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        Acquisition("network", "Windows network").Record(network?.SampledAtUtc != null, false, "network-read-unavailable",
            network?.SampledAtTimestamp is > 0 and var nt ? nt : now, network?.SampledAtUtc ?? utc);
        foreach (var volume in volumes)
        {
            var health = Acquisition($"vol:{volume.Letter}", "Windows volumes");
            if (health.LastSuccessUtc == volume.SampledAtUtc && volume.SampledAtUtc != null) continue;
            health.Record(volume.SampledAtUtc != null && volume.TotalGb > 0 && volume.FreeGb >= 0, false, "volume-read-unavailable",
                volume.SampledAtTimestamp > 0 ? volume.SampledAtTimestamp : now, volume.SampledAtUtc ?? utc);
        }
        foreach (var disk in disks ?? [])
        {
            var health = Acquisition($"disk:{disk.Index}", "Storage sensors");
            if (health.LastSuccessUtc == disk.SampledAtUtc && disk.SampledAtUtc != null) continue;
            health.Record(disk.SampledAtUtc != null, false, "storage-read-unavailable",
                disk.SampledAtTimestamp > 0 ? disk.SampledAtTimestamp : now, disk.SampledAtUtc ?? utc);
        }
        foreach (var health in _acquisitions.Values.Where(h => h.Source == "Windows volumes" && !volumes.Any(v => $"vol:{v.Letter}" == h.Key)))
            health.Record(false, false, "volume-no-longer-reported", now, utc);
        foreach (var health in _acquisitions.Values.Where(h => h.Source == "Storage sensors" && !(disks?.Any(d => $"disk:{d.Index}" == h.Key) ?? false)))
            health.Record(false, false, _storageOn ? "storage-read-unavailable" : "not-requested", now, utc);
    }

    private void InvalidateAcquisitionsForResume()
    {
        lock (_stateLock)
        {
            long now = Stopwatch.GetTimestamp();
            var utc = DateTimeOffset.UtcNow;
            foreach (var health in _acquisitions.Values) health.Record(false, false, "resuming-awaiting-acquisition", now, utc);
            _curveInputTemp = null;
            _supervisedCurveMissingSince = null;
            _hasHubSpeeds = false;
        }
    }

    private void RecordHubAcquisition(IReadOnlyDictionary<int, int>? speeds, IReadOnlyDictionary<int, float>? temperatures)
    {
        long now = Stopwatch.GetTimestamp();
        var utc = DateTimeOffset.UtcNow;
        Acquisition("hub.rpm", "iCUE LINK").Record(speeds != null, false, "rpm-read-failed", now, utc);
        foreach (var fan in _fans.Where(f => f.HasTachometer))
        {
            // Available zero and unavailable channel are different observations.
            TrackValue($"fan:{fan.Key}", "iCUE LINK", speeds != null && speeds.TryGetValue(fan.Channel, out int rpm) ? rpm : null, 0, 50000, now, utc);
        }
        TrackValue("coolant.temp", "iCUE LINK", _coolant, 0.1f, 150, now, utc);
        foreach (var (channel, temperature) in temperatures ?? new Dictionary<int, float>())
            TrackValue($"hub.temp:{channel}", "iCUE LINK temperature", temperature, 0.1f, 150, now, utc);
        foreach (var health in _acquisitions.Values.Where(h => h.Source == "iCUE LINK temperature"))
        {
            if (temperatures?.Any(entry => $"hub.temp:{entry.Key}" == health.Key) == true) continue;
            health.Record(false, false, "temperature-channel-not-reported", now, utc);
        }
    }

    private void SuperviseSafety(object? _)
    {
        if (_cts.IsCancellationRequested || Volatile.Read(ref _started) == 0 || Interlocked.Exchange(ref _supervisingSafety, 1) != 0) return;
        try
        {
            ControlConfig cfg;
            lock (_configLock) cfg = _config;
            bool changed;
            lock (_stateLock)
            {
                long now = Stopwatch.GetTimestamp();
                float? cpu = FreshValue("cpu.temp", _telemetry.CpuTemperature);
                float? gpu = FreshValue("gpu.temp", _telemetry.GpuTemperature);
                float? coolant = FreshValue("coolant.temp", _coolant);
                _curveSelection = CurveController.SelectTemperature(cfg.Curve, cpu, gpu, coolant);
                bool missing = CurveInputMissing(cfg, cpu, gpu, coolant);
                if (missing) _supervisedCurveMissingSince ??= now;
                else _supervisedCurveMissingSince = null;
                var missingFor = _supervisedCurveMissingSince is { } since ? Stopwatch.GetElapsedTime(since, now) : (TimeSpan?)null;

                var sensorSource = _acquisitions.GetValueOrDefault("sensors");
                if (sensorSource?.MissingSince is { } sensorMissing && Stopwatch.GetElapsedTime(sensorMissing, now) >= _opt.SensorLossTimeout)
                    RaiseAlert("sensor-read-failed", AlertSeverity.Warning, EngineText.Of(EngineTexts.TelemetryLostTitle), EngineText.Of(EngineTexts.TelemetrySourceLost, "CPU/GPU/RAM"));
                else ClearAlert("sensor-read-failed");

                bool MissingRpm(FanState fan)
                {
                    string key = $"fan:{fan.Key}";
                    if (IsFresh(key, now)) return false;
                    var health = _acquisitions.GetValueOrDefault(key);
                    long since = health?.MissingSince ?? health?.LastSuccessTick ?? _engineStartedAt;
                    return Stopwatch.GetElapsedTime(since, now) >= _opt.SensorLossTimeout;
                }
                bool lostPumpRpm = _hubEverConnected && _fans.Any(f => f.IsPump && MissingRpm(f));
                if (lostPumpRpm)
                {
                    RaiseAlert("cooling-telemetry-lost", cfg.Safety.FailSafeEnabled ? AlertSeverity.Critical : AlertSeverity.Warning,
                        EngineText.Of(EngineTexts.TelemetryLostTitle), EngineText.Of(EngineTexts.TelemetrySourceLost, EngineText.Of(EngineTexts.SensorPump)));
                    // A partial packet failure must also recover, without reconnecting on every supervisor tick.
                    if (_hubConnected && (_lastPartialRpmReconnect == 0 || Stopwatch.GetElapsedTime(_lastPartialRpmReconnect, now) >=
                        (_opt.ReconnectInterval > TimeSpan.FromSeconds(1) ? _opt.ReconnectInterval : TimeSpan.FromSeconds(1))))
                    {
                        _lastPartialRpmReconnect = now;
                        Interlocked.Exchange(ref _forceReconnectHub, 1);
                        Signal(_hubSignal);
                    }
                }
                else ClearAlert("cooling-telemetry-lost");
                var missingFans = _fans.Where(f => !f.IsPump && f.HasTachometer && MissingRpm(f)).ToArray();
                if (_hubEverConnected && missingFans.Length > 0)
                    RaiseAlert("fan-telemetry-lost", AlertSeverity.Warning, EngineText.Of(EngineTexts.TelemetryLostTitle),
                        EngineText.Of(EngineTexts.TelemetrySourceLost, string.Join(", ", missingFans.Take(3).Select(f => cfg.GetFan(f.Key).Name ?? f.DefaultName))));
                else ClearAlert("fan-telemetry-lost");

                if (_curveSelection.IsFallback)
                    RaiseAlert("curve-source-fallback", AlertSeverity.Warning, EngineText.Of(EngineTexts.CurveFallbackTitle),
                        EngineText.Of(EngineTexts.CurveFallback, cfg.Curve.Source.ToString(), _curveSelection.EffectiveSource?.ToString() ?? "-"));
                else ClearAlert("curve-source-fallback");
                changed = EvaluateSafety(cfg.Safety, cpu, gpu, missingFor);
            }
            if (changed) { Signal(_hubSignal); Signal(_sensorSignal); }
        }
        catch (Exception ex) { Log.Error("Falha no supervisor de segurança", ex); }
        finally { Interlocked.Exchange(ref _supervisingSafety, 0); }
    }

    private FanState SnapshotFan(FanState fan, ControlConfig cfg)
    {
        var h = _acquisitions.GetValueOrDefault($"fan:{fan.Key}");
        var validity = !fan.HasTachometer ? ReadingValidity.NotSupported :
            h?.Validity(Stopwatch.GetTimestamp(), _opt.TelemetryFreshnessTimeout) ?? ReadingValidity.Unavailable;
        return new FanState
        {
            Key = fan.Key, Channel = fan.Channel, DeviceId = fan.DeviceId, DefaultName = fan.DefaultName,
            Model = fan.Model, IsPump = fan.IsPump, Rpm = validity == ReadingValidity.Fresh ? fan.Rpm : 0,
            RpmAvailable = validity == ReadingValidity.Fresh, RpmValidity = validity, RpmLastSuccessUtc = h?.LastSuccessUtc,
            AppliedPercent = fan.AppliedPercent, Temperature = IsFresh($"hub.temp:{fan.Channel}") ? fan.Temperature : null,
            RequestedPercent = fan.CanControlSpeed ? DesiredPercent(cfg, fan.Key, fan.IsPump, fan.Channel, protection: false) : null,
            ControlReason = !fan.CanControlSpeed ? "unsupported" : _failSafe ? "safety" : IsCalibrating(fan.Channel) ? "calibration" :
                !cfg.GetFan(fan.Key).Linked ? "manual" : cfg.GetFan(fan.Key).Group is { } group && _groupOutputs.ContainsKey(group)
                    ? $"group-curve:{group}" : cfg.Mode == FanControlMode.Curve ? "global-curve" : "global-fixed"
        };
    }
}
