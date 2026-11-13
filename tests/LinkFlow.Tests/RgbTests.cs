namespace LinkFlow.Tests;

/// <summary>Correção de cor para LEDs e brilho por dispositivo.</summary>
public class RgbTests
{
    [Fact]
    public void Gamma_makes_secondary_channels_dimmer_so_colors_stay_saturated()
    {
        var rgb = new RgbConfig { R = 128, G = 64, B = 255, Brightness = 100, LedGamma = 2.0f };
        var o = rgb.OutputFor(RgbTargets.Peripherals);
        Assert.Equal(255, o.B);          // canal principal intacto
        Assert.InRange(o.R, 60, 68);     // 128 → ~64 (0,5² × 255)
        Assert.InRange(o.G, 14, 18);     // 64 → ~16
        Assert.Equal(100, o.Brightness);

        rgb.LedGamma = 1f;               // correção desligada = cor crua
        Assert.Equal((128, 64, 255, 100), ((int)rgb.OutputFor("x").R, (int)rgb.OutputFor("x").G, (int)rgb.OutputFor("x").B, (int)rgb.OutputFor("x").Brightness));
    }

    [Fact]
    public void Device_brightness_multiplies_global_brightness()
    {
        var rgb = new RgbConfig { Brightness = 80, DeviceBrightness = { [RgbTargets.Gpu] = 50 } };
        Assert.Equal(40, rgb.OutputFor(RgbTargets.Gpu).Brightness);
        Assert.Equal(80, rgb.OutputFor(RgbTargets.Hub).Brightness);

        var other = Json.Clone(rgb);
        Assert.True(rgb.SameOutputAs(other));
        other.DeviceBrightness[RgbTargets.Gpu] = 60;
        Assert.False(rgb.SameOutputAs(other)); // mudar o brilho de um dispositivo reenvia a cor
    }

    [Fact]
    public void Engine_sends_corrected_color_to_hub()
    {
        using var f = new EngineFixture();
        f.Engine.UpdateConfig(c => { c.Rgb.R = 255; c.Rgb.G = 128; c.Rgb.B = 0; c.Rgb.LedGamma = 2f; });
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastRgb is { R: 255, B: 0 } l && l.G is >= 62 and <= 66, because: "laranja corrigido no hub");
    }

    [Fact]
    public void Engine_retries_gpu_detection_and_sends_static_color_after_startup_failure()
    {
        var hardware = new SimulatedHardwareFactory();
        hardware.GpuRgb.FailInitialization = true;
        var options = new EngineOptions
        {
            ConfigPath = null,
            ReconnectInterval = TimeSpan.FromMilliseconds(100),
            TelemetryInterval = TimeSpan.FromMilliseconds(40)
        };
        using var engine = new LinkFlowEngine(hardware, options, new ControlConfig());
        engine.Start();
        EngineFixture.WaitUntil(() => engine.GetSnapshot() is { HubConnected: true, GpuRgbRetrying: true, GpuRgbAvailable: false });
        int hubConnections = hardware.Hub.ConnectCalls;

        hardware.GpuRgb.FailInitialization = false;
        EngineFixture.WaitUntil(() => engine.GetSnapshot() is { GpuRgbAvailable: true, GpuRgbRetrying: false, GpuRgbApplied: true },
            because: "detecção recuperada e cor fixa reenviada");
        Assert.Equal(hubConnections, hardware.Hub.ConnectCalls);
    }
}
