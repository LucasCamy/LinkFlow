using System.Diagnostics;

namespace LinkFlow.Tests;

public sealed class TelemetryReliabilityTests
{
    private static byte Speed(EngineFixture f, int channel = 1) => f.Hw.Hub.LastSpeeds.GetValueOrDefault(channel);

    [Fact]
    public void Repeated_sensor_exceptions_invalidate_values_and_protect_before_lane_watchdog()
    {
        var config = new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1 } };
        using var f = new EngineFixture(config, options =>
        {
            options.SensorLossTimeout = TimeSpan.FromMilliseconds(200);
            options.TelemetryFreshnessTimeout = TimeSpan.FromMilliseconds(150);
            options.LaneStallTimeout = TimeSpan.FromMinutes(1);
        });
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Hw.Sensors.ForcedGpuTemp = 40;
        EngineFixture.WaitUntil(() => Speed(f) == 30 && f.Engine.GetSnapshot().CpuSensorAvailable);
        DateTimeOffset? lastSuccess;
        // Stop at an acquisition boundary so a successful in-flight read cannot change the baseline.
        f.Hw.Sensors.ReadGate.Reset();
        try
        {
            EngineFixture.WaitUntil(() => f.Hw.Sensors.IsReadWaiting);
            lastSuccess = f.Engine.GetSnapshot().TelemetryHealth.Single(h => h.Key == "cpu.temp").LastSuccessUtc;
            f.Hw.Sensors.ThrowOnRead = true;
        }
        finally { f.Hw.Sensors.ReadGate.Set(); }

        // Curve freshness loss and repeated provider failures have independent clocks. Protection may fire
        // first; wait for both guarantees instead of requiring their alerts in the first protected snapshot.
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot() is { CpuTemp: null, CpuSensorAvailable: false, FailSafeActive: true } current &&
            Speed(f) == 100 && current.Alerts.Any(alert => alert.Key == "sensor-read-failed") &&
            current.TelemetryHealth.Any(health => health.Key == "sensors" && health.ConsecutiveFailures >= 2));
        var snapshot = f.Engine.GetSnapshot();
        var cpu = snapshot.TelemetryHealth.Single(h => h.Key == "cpu.temp");
        Assert.Equal(ReadingValidity.Stale, cpu.Validity);
        Assert.Equal(lastSuccess, cpu.LastSuccessUtc);
        Assert.True(cpu.ConsecutiveFailures > 0);
        Assert.Contains(snapshot.Alerts, a => a.Key == "sensor-read-failed");
        Assert.DoesNotContain(snapshot.Alerts, a => a.Key == "engine-stalled");

        f.Hw.Sensors.ThrowOnRead = false;
        EngineFixture.WaitUntil(() => Speed(f) == 30 && !f.Engine.GetSnapshot().FailSafeActive);
        Assert.DoesNotContain(f.Engine.GetSnapshot().Alerts, a => a.Key is "sensor-lost" or "sensor-read-failed" or "failsafe");
    }

    [Fact]
    public void Losing_only_rpm_packets_is_unknown_and_enters_protection_until_new_rpm_arrives()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 45 }, o => o.SensorLossTimeout = TimeSpan.FromMilliseconds(200));
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().PumpRpm > 800 && Speed(f) == 45);

        f.Hw.Hub.FailSpeedReads = true;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().PumpRpm == null);
        EngineFixture.WaitUntil(() => Speed(f) == 100 && f.Engine.GetSnapshot().FailSafeActive);
        var snapshot = f.Engine.GetSnapshot();
        Assert.NotNull(snapshot.CoolantTemp);
        Assert.All(snapshot.Fans, fan => Assert.False(fan.RpmAvailable));
        Assert.Contains(snapshot.Alerts, a => a.Key == "cooling-telemetry-lost" && a.Severity == AlertSeverity.Critical);
        Assert.Equal(ReadingValidity.Stale, snapshot.TelemetryHealth.Single(h => h.Key == "hub.rpm").Validity);
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls > 1, because: "partial RPM failure also starts bounded recovery attempts");

        f.Hw.Hub.FailSpeedReads = false;
        EngineFixture.WaitUntil(() => Speed(f) == 45 && !f.Engine.GetSnapshot().FailSafeActive && f.Engine.GetSnapshot().PumpRpm > 800);
    }

    [Fact]
    public void Rpm_loss_warns_without_claiming_a_stopped_pump_when_protection_is_disabled()
    {
        var config = new ControlConfig { GlobalPercent = 45, Safety = { FailSafeEnabled = false } };
        using var f = new EngineFixture(config, o => o.SensorLossTimeout = TimeSpan.FromMilliseconds(120));
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().PumpRpm > 800);
        f.Hw.Hub.FailSpeedReads = true;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.Any(a => a.Key == "cooling-telemetry-lost"));
        Assert.False(f.Engine.GetSnapshot().FailSafeActive);
        Assert.Equal(45, Speed(f));
        Assert.DoesNotContain(f.Engine.GetSnapshot().Alerts, a => a.Key.StartsWith("pump:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pump_channel_unavailable_and_measured_zero_have_distinct_validity_and_alerts(bool unavailable)
    {
        using var fixture = new EngineFixture(new ControlConfig { GlobalPercent = 45 }, options => options.SensorLossTimeout = TimeSpan.FromMilliseconds(160));
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().PumpRpm > 800);
        if (unavailable) fixture.Hw.Hub.UnavailableRpmChannels.Add(11);
        else fixture.Hw.Hub.StoppedChannels.Add(11);
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().FailSafeActive);
        var snapshot = fixture.Engine.GetSnapshot();
        var pump = snapshot.Fans.Single(fan => fan.IsPump);
        if (unavailable)
        {
            Assert.Null(snapshot.PumpRpm);
            Assert.False(pump.RpmAvailable);
            Assert.Contains(snapshot.Alerts, alert => alert.Key == "cooling-telemetry-lost");
            Assert.DoesNotContain(snapshot.Alerts, alert => alert.Key.StartsWith("pump:", StringComparison.Ordinal));
        }
        else
        {
            Assert.Equal(0, snapshot.PumpRpm);
            Assert.True(pump.RpmAvailable);
            Assert.Contains(snapshot.Alerts, alert => alert.Key.StartsWith("pump:", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot.Alerts, alert => alert.Key == "cooling-telemetry-lost");
        }
        fixture.Hw.Hub.UnavailableRpmChannels.Clear();
        fixture.Hw.Hub.StoppedChannels.Clear();
        EngineFixture.WaitUntil(() => !fixture.Engine.GetSnapshot().FailSafeActive && Speed(fixture) == 45);
    }

    [Fact]
    public void Missing_fan_channel_reports_lost_monitoring_instead_of_a_false_stopped_fan()
    {
        using var fixture = new EngineFixture(configure: options => options.SensorLossTimeout = TimeSpan.FromMilliseconds(120));
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Fans.All(fan => fan.RpmAvailable));
        fixture.Hw.Hub.UnavailableRpmChannels.Add(4);
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Alerts.Any(alert => alert.Key == "fan-telemetry-lost"));
        var snapshot = fixture.Engine.GetSnapshot();
        Assert.False(snapshot.Fans.Single(fan => fan.Channel == 4).RpmAvailable);
        Assert.NotNull(snapshot.PumpRpm);
        Assert.False(snapshot.FailSafeActive);
        Assert.DoesNotContain(snapshot.Alerts, alert => alert.Key == "fan:SIM-RX-04");
        fixture.Hw.Hub.UnavailableRpmChannels.Clear();
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Alerts.All(alert => alert.Key != "fan-telemetry-lost"));
    }

    [Fact]
    public async Task Watchdog_covers_sensor_open_before_the_first_successful_iteration()
    {
        var hardware = new SimulatedHardwareFactory();
        hardware.Sensors.OpenGate.Reset();
        var stalled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new LinkFlowEngine(hardware, new EngineOptions
        {
            ConfigPath = null,
            LaneStallTimeout = TimeSpan.FromMilliseconds(1500),
            LaneStalled = reason => stalled.TrySetResult(reason)
        });
        try
        {
            engine.Start();
            var reason = await stalled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("sensores", reason);
            Assert.False(engine.GetSnapshot().SensorsReady);
            Assert.Contains(engine.GetSnapshot().Alerts, a => a.Key == "engine-stalled");
            Assert.False(hardware.Hub.InSoftwareMode);
        }
        finally { hardware.Sensors.OpenGate.Set(); }
    }

    [Fact]
    public void Snapshot_time_cannot_refresh_a_blocked_acquisition()
    {
        using var f = new EngineFixture(configure: o =>
        {
            o.TelemetryFreshnessTimeout = TimeSpan.FromMilliseconds(150);
            o.LaneStallTimeout = TimeSpan.FromMinutes(1);
        });
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().PumpRpm > 800);
        f.Hw.Hub.ReadGate.Reset();
        try
        {
            EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().PumpRpm == null);
            var snapshot = f.Engine.GetSnapshot();
            Assert.NotEqual(default, snapshot.Timestamp);
            Assert.Equal(ReadingValidity.Stale, snapshot.TelemetryHealth.Single(h => h.Key == "hub.rpm").Validity);
            Assert.All(snapshot.Fans, fan => Assert.False(fan.RpmAvailable));
        }
        finally { f.Hw.Hub.ReadGate.Set(); }
    }

    [Fact]
    public void Missing_selected_cpu_does_not_silently_use_gpu_with_the_cpu_curve()
    {
        var config = new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Source = CurveSource.Cpu, Smoothing = 1 } };
        using var f = new EngineFixture(config, o => o.SensorLossTimeout = TimeSpan.FromMilliseconds(150));
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Hw.Sensors.ForcedGpuTemp = 35;
        EngineFixture.WaitUntil(() => Speed(f) == 30);
        f.Hw.Sensors.CpuAvailable = false;
        EngineFixture.WaitUntil(() => Speed(f) == 100 && f.Engine.GetSnapshot().FailSafeActive);
        Assert.False(f.Engine.GetSnapshot().CurveUsingFallback);
        Assert.Null(f.Engine.GetSnapshot().CurveEffectiveSource);
        Assert.NotNull(f.Engine.GetSnapshot().GpuTemp);
    }

    [Fact]
    public void Authorized_fallback_is_visible_and_obeys_a_conservative_floor()
    {
        var config = new ControlConfig
        {
            Mode = FanControlMode.Curve,
            Curve = new FanCurveConfig { Source = CurveSource.Cpu, Smoothing = 1, FallbackPolicy = CurveFallbackPolicy.AllowOtherCpuGpu, FallbackMinPercent = 75 }
        };
        using var f = new EngineFixture(config);
        f.Hw.Sensors.CpuAvailable = false;
        f.Hw.Sensors.ForcedGpuTemp = 35;
        EngineFixture.WaitUntil(() => Speed(f) == 75 && f.Engine.GetSnapshot().CurveUsingFallback);
        var snapshot = f.Engine.GetSnapshot();
        Assert.Equal(CurveSource.Gpu, snapshot.CurveEffectiveSource);
        Assert.Contains(snapshot.Alerts, a => a.Key == "curve-source-fallback");
        Assert.False(snapshot.FailSafeActive);
    }

    [Fact]
    public void Lcd_has_no_tachometer_or_speed_control_and_stopped_fans_remain_in_the_average()
    {
        using var f = new EngineFixture();
        f.Hw.Hub.PlugDevice(new ConnectedSubDevice { Channel = 12, DeviceId = "LCD", Model = LinkDeviceModel.PumpLcd, Name = "Pump LCD" });
        f.Engine.RescanDevices();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(fan => fan.Key == "LCD"));
        var lcd = f.Engine.GetSnapshot().Fans.Single(fan => fan.Key == "LCD");
        Assert.False(lcd.HasTachometer);
        Assert.False(lcd.CanControlSpeed);
        Assert.False(lcd.RpmAvailable);
        Assert.Equal(ReadingValidity.NotSupported, lcd.RpmValidity);
        Assert.Null(lcd.RequestedPercent);
        Assert.DoesNotContain(12, f.Engine.ComputeDesiredSpeeds(f.Engine.GetConfig()).Keys);
        Assert.DoesNotContain(SensorCatalog.All(f.Engine.GetSnapshot()), sensor => sensor.Id == "fan:LCD");

        var aggregate = new EngineSnapshot { Fans = { new FanState { Rpm = 1000 }, new FanState { Rpm = 0 }, lcd } };
        Assert.Equal(500, aggregate.AverageFanRpm);
    }

    [Fact]
    public void Invalid_values_are_not_published_or_selected_for_a_curve()
    {
        using var f = new EngineFixture();
        f.Hw.Sensors.ForcedCpuTemp = float.NaN;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().TelemetryHealth.Any(h => h.Key == "cpu.temp" && h.Validity == ReadingValidity.Invalid));
        Assert.Null(f.Engine.GetSnapshot().CpuTemp);
        Assert.False(f.Engine.GetSnapshot().CpuSensorAvailable);
        Assert.Null(CurveController.SelectTemperature(new FanCurveConfig { Source = CurveSource.Cpu }, float.NaN, 40, 30).Temperature);
    }

    [Fact]
    public void Curve_fallback_policy_survives_cloning_and_edits_reset_comparison()
    {
        var original = new FanCurveConfig { FallbackPolicy = CurveFallbackPolicy.AllowAnyAvailable, FallbackMinPercent = 85 };
        var copy = original.Clone();
        Assert.True(copy.SameAs(original));
        copy.FallbackPolicy = CurveFallbackPolicy.SafeSpeed;
        Assert.False(copy.SameAs(original));
        copy = original.Clone(); copy.FallbackMinPercent = 90;
        Assert.False(copy.SameAs(original));
        original.FallbackPolicy = (CurveFallbackPolicy)999;
        original.FallbackMinPercent = 255;
        original.Normalize();
        Assert.Equal(CurveFallbackPolicy.SafeSpeed, original.FallbackPolicy);
        Assert.Equal(100, original.FallbackMinPercent);
    }

    [Fact]
    public void Cached_volume_read_keeps_the_original_acquisition_timestamp()
    {
        using var f = new EngineFixture();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Volumes.Count > 0);
        var first = f.Engine.GetSnapshot().Volumes[0];
        Assert.NotNull(first.SampledAtUtc);
        var acquisition = f.Engine.GetSnapshot().TelemetryHealth.Single(h => h.Key == $"vol:{first.Letter}");
        Thread.Sleep(200);
        var next = f.Engine.GetSnapshot();
        Assert.Equal(first.SampledAtUtc, next.Volumes.Single(v => v.Letter == first.Letter).SampledAtUtc);
        Assert.Equal(acquisition.LastSuccessUtc, next.TelemetryHealth.Single(h => h.Key == acquisition.Key).LastSuccessUtc);
    }

    [Fact]
    public void Failed_static_hub_rgb_retries_with_backoff_without_a_new_configuration_or_hub_reconnect()
    {
        using var fixture = new EngineFixture(new ControlConfig { GlobalPercent = 45, Rgb = { LedGamma = 1 } }, options =>
        {
            options.RgbSync.RetryFirst = TimeSpan.FromMilliseconds(150);
            options.RgbSync.RetryMax = TimeSpan.FromMilliseconds(300);
        });
        EngineFixture.WaitUntil(() => fixture.Hw.Hub.LastRgb != null && fixture.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 45);
        int connections = fixture.Hw.Hub.ConnectCalls;
        int writes = fixture.Hw.Hub.SetRgbCalls;
        fixture.Hw.Hub.RgbFailuresRemaining = 1;
        fixture.Engine.UpdateConfig(config => config.Rgb.R = 27);
        EngineFixture.WaitUntil(() => fixture.Hw.Hub.LastRgb?.R == 27);
        Assert.Equal(writes + 2, fixture.Hw.Hub.SetRgbCalls);
        Assert.True(Stopwatch.GetElapsedTime(fixture.Hw.Hub.PreviousRgbAttemptTimestamp, fixture.Hw.Hub.LastRgbAttemptTimestamp) >= TimeSpan.FromMilliseconds(100));
        Assert.Equal(connections, fixture.Hw.Hub.ConnectCalls);
        Assert.Equal(45, fixture.Hw.Hub.LastSpeeds.GetValueOrDefault(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fan_temperature_keeps_its_own_source_and_freshness_and_is_never_labeled_coolant(bool pumpPresent)
    {
        using var fixture = new EngineFixture(configure: options =>
        {
            options.TelemetryFreshnessTimeout = TimeSpan.FromMilliseconds(150);
            options.LaneStallTimeout = TimeSpan.FromMinutes(1);
        });
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().CoolantTemp != null);
        if (!pumpPresent)
        {
            fixture.Hw.Hub.UnplugDevice(11);
            fixture.Engine.RescanDevices();
            EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Fans.All(fan => !fan.IsPump));
        }
        fixture.Hw.Hub.CoolantSensorChannel = 1;
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot() is { CoolantTemp: null } snapshot &&
            snapshot.Fans.Any(fan => fan.Channel == 1 && fan.Temperature != null));
        var current = fixture.Engine.GetSnapshot();
        Assert.Equal(ReadingValidity.Fresh, current.TelemetryHealth.Single(health => health.Key == "hub.temp:1").Validity);
        Assert.NotEqual(ReadingValidity.Fresh, current.TelemetryHealth.Single(health => health.Key == "coolant.temp").Validity);
        Assert.DoesNotContain(current.Alerts, alert => alert.Key == "coolant-temp");

        fixture.Hw.Hub.ReadGate.Reset();
        try
        {
            EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Fans.Single(fan => fan.Channel == 1).Temperature == null);
            Assert.Equal(ReadingValidity.Stale, fixture.Engine.GetSnapshot().TelemetryHealth.Single(health => health.Key == "hub.temp:1").Validity);
        }
        finally { fixture.Hw.Hub.ReadGate.Set(); }
    }
}
