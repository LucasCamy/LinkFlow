using System.IO;
using System.Diagnostics;

namespace LinkFlow.Tests;

public sealed class EngineFixture : IDisposable
{
    public SimulatedHardwareFactory Hw { get; } = new();
    public LinkFlowEngine Engine { get; }

    public EngineFixture(ControlConfig? config = null, Action<EngineOptions>? configure = null)
    {
        Hw.Sensors.OpenDelay = TimeSpan.Zero;
        var opt = new EngineOptions
        {
            TelemetryInterval = TimeSpan.FromMilliseconds(40),
            SnapshotInterval = TimeSpan.FromMilliseconds(30),
            ReconnectInterval = TimeSpan.FromMilliseconds(100),
            HubRgbMinInterval = TimeSpan.FromMilliseconds(20),
            AuxRgbMinInterval = TimeSpan.FromMilliseconds(20),
            LcdFrameInterval = TimeSpan.FromMilliseconds(50),
            FanStopReadsBeforeAlert = 3,
            PumpLowReadsBeforeAlert = 2,
            FailSafeRecoveryDelay = TimeSpan.FromMilliseconds(200),
            ConfigPath = null
        };
        configure?.Invoke(opt);
        Engine = new LinkFlowEngine(Hw, opt, config ?? new ControlConfig());
        Engine.Start();
    }

    public static void WaitUntil(Func<bool> condition, int timeoutMs = 3000, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Tempo esgotado: {because ?? "condição"}");
            Thread.Sleep(10);
        }
    }

    public void Dispose() => Engine.Dispose();
}

public class EngineTests
{
    private static byte Speed(EngineFixture f, int ch) => f.Hw.Hub.LastSpeeds.TryGetValue(ch, out var p) ? p : (byte)0;

    [Fact]
    public void Applies_global_speed_to_all_fans_and_keeps_pump_minimum()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 });
        EngineFixture.WaitUntil(() => Speed(f, 1) == 40, because: "fan 1 em 40%");
        Assert.All(Enumerable.Range(1, 10), ch => Assert.Equal(40, Speed(f, ch)));
        Assert.Equal(FanLimits.PumpMinPercent, Speed(f, 11));
    }

    [Fact]
    public void Unlinked_fan_keeps_manual_speed_when_global_changes()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 50 });
        EngineFixture.WaitUntil(() => Speed(f, 1) == 50);

        f.Engine.UpdateConfig(c => { var o = c.EditFan("SIM-RX-03"); o.Linked = false; o.Percent = 90; });
        f.Engine.UpdateConfig(c => c.GlobalPercent = 30);

        EngineFixture.WaitUntil(() => Speed(f, 1) == 30 && Speed(f, 3) == 90, because: "global 30 e fan 3 manual 90");
    }

    [Fact]
    public void Slider_drag_is_coalesced_into_few_hub_writes()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 25 });
        EngineFixture.WaitUntil(() => Speed(f, 1) == 25);
        int before = f.Hw.Hub.SetSpeedsCalls;

        for (byte p = 25; p <= 100; p++)
        {
            byte v = p;
            f.Engine.UpdateConfig(c => c.GlobalPercent = v);
        }

        EngineFixture.WaitUntil(() => Speed(f, 1) == 100, because: "valor final aplicado");
        int writes = f.Hw.Hub.SetSpeedsCalls - before;
        Assert.True(writes < 20, $"esperava poucas escritas, houve {writes}");
    }

    [Fact]
    public void Preset_relinks_fans_and_sets_mode()
    {
        var cfg = new ControlConfig();
        cfg.EditFan("SIM-RX-01").Linked = false;
        FanPresets.Apply(cfg, FanPresets.Max);
        Assert.True(cfg.GetFan("SIM-RX-01").Linked);
        Assert.Equal(100, cfg.GlobalPercent);
        Assert.False(cfg.GpuFanAuto);

        FanPresets.Apply(cfg, FanPresets.Auto);
        Assert.Equal(FanControlMode.Curve, cfg.Mode);
    }

    [Fact]
    public void Curve_mode_follows_temperature()
    {
        using var f = new EngineFixture(new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1f } });
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Hw.Sensors.ForcedGpuTemp = 40;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 30, because: "curva a 40°C = 30%");

        f.Hw.Sensors.ForcedCpuTemp = 80;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 85, because: "curva a 80°C = 85%");
    }

    [Fact]
    public void Critical_temperature_triggers_fail_safe()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 30 });
        var alerts = new List<EngineAlert>();
        f.Engine.AlertRaised += a => { lock (alerts) alerts.Add(a); };
        EngineFixture.WaitUntil(() => Speed(f, 1) == 30);

        f.Hw.Sensors.ForcedCpuTemp = 97;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 100 && f.Hw.Sensors.GpuFanManual == 100,
            because: "hub e GPU em 100% no modo de segurança");
        Assert.True(f.Engine.GetSnapshot().FailSafeActive);
        EngineFixture.WaitUntil(() => { lock (alerts) return alerts.Any(a => a.Key == "failsafe"); });

        f.Hw.Sensors.ForcedCpuTemp = 60;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 30 && f.Hw.Sensors.GpuFanManual == null,
            because: "hub em 30% e GPU em modo automático");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Fail_safe_restores_the_latest_profile_after_coolant_stays_below_critical(int sensorChannel)
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 35 },
            options => options.FailSafeRecoveryDelay = TimeSpan.FromMilliseconds(400));
        f.Hw.Hub.CoolantSensorChannel = sensorChannel;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 35);
        f.Hw.Hub.CoolantTemp = 51;
        EngineFixture.WaitUntil(() => Speed(f, 1) == 100);
        Assert.Contains(f.Engine.GetSnapshot().Alerts, a => a.Key == "failsafe" && a.Message.Contains("Líquido"));

        f.Engine.UpdateConfig(c =>
        {
            c.Mode = FanControlMode.Curve;
            c.Curve.Points = [new(0, 55), new(110, 55)];
            c.GpuFanAuto = false;
            c.GpuFanPercent = 60;
        });
        Assert.Equal(100, Speed(f, 1));
        f.Hw.Hub.CoolantTemp = 47.5f;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.Any(a =>
            a.Key == "failsafe" && a.Message.Contains("estabilização")));
        Assert.Equal(100, Speed(f, 1));
        EngineFixture.WaitUntil(() => Speed(f, 1) == 55 && f.Hw.Sensors.GpuFanManual == 60,
            because: "perfil selecionado retomado depois de a temperatura estabilizar");
        var snapshot = f.Engine.GetSnapshot();
        Assert.False(snapshot.FailSafeActive);
        Assert.Contains(snapshot.Alerts, a => a.Key == "coolant-temp" && a.Severity == AlertSeverity.Warning);
    }

    [Fact]
    public void Pump_failure_protects_hardware_even_when_fan_alerts_are_disabled()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40, Safety = { FanStopAlert = false } });
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().PumpRpm > 800);
        f.Hw.Hub.StoppedChannels.Add(11);
        EngineFixture.WaitUntil(() => Speed(f, 1) == 100 && f.Engine.GetSnapshot().FailSafeActive);
        f.Hw.Hub.StoppedChannels.Clear();
        EngineFixture.WaitUntil(() => Speed(f, 1) == 40 && !f.Engine.GetSnapshot().FailSafeActive);
    }

    [Fact]
    public void Stopped_fan_raises_alert()
    {
        using var f = new EngineFixture();
        f.Hw.Hub.StoppedChannels.Add(4);
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.Any(a => a.Key == "fan:SIM-RX-04"), because: "alerta de fan parada");
        f.Hw.Hub.StoppedChannels.Clear();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.All(a => a.Key != "fan:SIM-RX-04"), because: "alerta limpo");
    }

    [Fact]
    public void Hub_reconnects_after_consecutive_read_failures()
    {
        using var f = new EngineFixture();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().HubConnected);
        int connects = f.Hw.Hub.ConnectCalls;

        f.Hw.Hub.FailReads = true;
        EngineFixture.WaitUntil(() => !f.Engine.GetSnapshot().HubConnected, because: "detecta hub sem resposta");
        Assert.Contains(f.Engine.GetSnapshot().Alerts, a => a.Key == "hub-lost");

        f.Hw.Hub.FailReads = false;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().HubConnected && f.Hw.Hub.ConnectCalls > connects, because: "reconecta");
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.All(a => a.Key != "hub-lost"));
    }

    [Fact]
    public void Rgb_change_reaches_every_target()
    {
        using var f = new EngineFixture();
        f.Engine.UpdateConfig(c => { c.Rgb.R = 255; c.Rgb.G = 0; c.Rgb.B = 10; c.Rgb.Brightness = 80; c.Rgb.LedGamma = 1; });
        EngineFixture.WaitUntil(() =>
            f.Hw.Hub.LastRgb == (255, 0, 10, 80) &&
            f.Hw.GpuRgb.Last == (255, 0, 10, 80) &&
            f.Hw.MotherboardRgb.Last == (255, 0, 10, 80) &&
            f.Hw.RamRgb.Last == (255, 0, 10, 80) &&
            f.Hw.PeripheralsRgb.Last == (255, 0, 10, 80), because: "cor aplicada em hub, GPU, placa-mãe, RAM e periféricos");
        EngineFixture.WaitUntil(() => f.Hw.Lcd.Frames > 0, because: "LCD recebeu quadro");
    }

    [Fact]
    public void Rgb_target_disabled_is_not_written()
    {
        using var f = new EngineFixture(new ControlConfig { Rgb = new RgbConfig { SyncGpu = false, LedGamma = 1 } });
        f.Engine.UpdateConfig(c => c.Rgb.R = 1);
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastRgb?.R == 1);
        Thread.Sleep(100);
        Assert.Equal(0, f.Hw.GpuRgb.Calls);
    }

    [Fact]
    public async Task Stop_restores_hardware_mode()
    {
        var f = new EngineFixture();
        EngineFixture.WaitUntil(() => f.Hw.Hub.InSoftwareMode);
        await f.Engine.StopAsync(restoreHardwareMode: true);
        Assert.False(f.Hw.Hub.InSoftwareMode);
    }

    [Fact]
    public void Config_is_persisted_and_reloaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"linkflow-test-{Guid.NewGuid():N}.json");
        try
        {
            using (var f = new EngineFixture(configure: o => { o.ConfigPath = path; o.SaveDebounce = TimeSpan.FromMilliseconds(10); }))
            {
                f.Engine.UpdateConfig(c => { c.GlobalPercent = 77; c.EditFan("SIM-RX-02").Name = "Topo esquerda"; });
            }

            var store = new JsonFileStore<ControlConfig>(path);
            var loaded = store.Load();
            Assert.Equal(77, loaded.GlobalPercent);
            Assert.Equal("Topo esquerda", loaded.GetFan("SIM-RX-02").Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Config_changed_event_carries_origin()
    {
        using var f = new EngineFixture();
        object? seenOrigin = null;
        f.Engine.ConfigChanged += (_, o) => seenOrigin = o;
        var me = new object();
        f.Engine.UpdateConfig(c => c.GlobalPercent = 60, me);
        Assert.Same(me, seenOrigin);
    }

    [Fact]
    public void Profile_capture_and_apply_roundtrip()
    {
        var c = new ControlConfig { GlobalPercent = 44 };
        var o = c.EditFan("A");
        o.Linked = false;
        o.Percent = 88;
        var profile = FanProfile.Capture("Noite", c);

        FanPresets.Apply(c, FanPresets.Max);
        Assert.True(c.GetFan("A").Linked);

        profile.ApplyTo(c);
        Assert.Equal(44, c.GlobalPercent);
        Assert.False(c.GetFan("A").Linked);
        Assert.Equal(88, c.GetFan("A").Percent);
        Assert.Equal("Noite", c.ActivePreset);
    }
}
