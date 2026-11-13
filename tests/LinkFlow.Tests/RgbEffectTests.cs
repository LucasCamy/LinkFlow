namespace LinkFlow.Tests;

/// <summary>Efeitos de iluminação: cálculo por tempo, espalhamento e envio animado pelo engine.</summary>
public class RgbEffectTests
{
    [Fact]
    public void Static_returns_picked_color()
    {
        var rgb = new RgbConfig { R = 10, G = 20, B = 30 };
        Assert.Equal(((byte)10, (byte)20, (byte)30, 1.0), RgbEffects.ColorAt(rgb, 123456));
        Assert.False(rgb.IsAnimated);
    }

    [Fact]
    public void Breathing_keeps_color_and_varies_level()
    {
        var rgb = new RgbConfig { R = 255, G = 0, B = 128, Effect = RgbEffect.Breathing, EffectSpeed = 5 };
        var levels = Enumerable.Range(0, 200).Select(i => RgbEffects.ColorAt(rgb, i * 50L)).ToList();
        Assert.All(levels, l => Assert.Equal((255, 0, 128), ((int)l.R, (int)l.G, (int)l.B)));
        Assert.True(levels.Min(l => l.Level) < 0.15);
        Assert.True(levels.Max(l => l.Level) > 0.95);
        // O nível vira brilho na saída do dispositivo
        Assert.True(Enumerable.Range(0, 200).Select(i => rgb.OutputFor("x", i * 50L, 0).Brightness).Distinct().Count() > 10);
    }

    [Fact]
    public void Rainbow_goes_through_all_hues_and_phase_shifts_it()
    {
        var rgb = new RgbConfig { Effect = RgbEffect.Rainbow, EffectSpeed = 10 }; // ciclo de 2,5 s
        var colors = Enumerable.Range(0, 50).Select(i => RgbEffects.ColorAt(rgb, i * 50L)).ToList();
        Assert.Contains(colors, c => c.R > 200 && c.G < 60 && c.B < 60);
        Assert.Contains(colors, c => c.G > 200 && c.R < 60 && c.B < 60);
        Assert.Contains(colors, c => c.B > 200 && c.R < 60 && c.G < 60);
        Assert.NotEqual(RgbEffects.ColorAt(rgb, 1000, 0), RgbEffects.ColorAt(rgb, 1000, 0.5));
    }

    [Fact]
    public void Palette_holds_each_color_and_fades_between_them()
    {
        var rgb = new RgbConfig { Effect = RgbEffect.Palette, EffectSpeed = 1, Palette = { } };
        rgb.Palette = new() { "#FF0000", "#0000FF" };
        // Velocidade 1: 8 s por cor; começo do ciclo segura a primeira cor
        Assert.Equal(((byte)255, (byte)0, (byte)0, 1.0), RgbEffects.ColorAt(rgb, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0, 1.0), RgbEffects.ColorAt(rgb, 2000));
        var mid = RgbEffects.ColorAt(rgb, 6000); // no meio da transição
        Assert.InRange(mid.R, 40, 215);
        Assert.InRange(mid.B, 40, 215);
        Assert.Equal(((byte)0, (byte)0, (byte)255, 1.0), RgbEffects.ColorAt(rgb, 8000));
    }

    [Fact]
    public void Random_is_deterministic_across_processes_and_never_repeats_the_previous_color()
    {
        var rgb = new RgbConfig { Effect = RgbEffect.Random, EffectSpeed = 10 };
        var copy = Json.Clone(rgb);
        for (long t = 0; t < 20000; t += 333)
            Assert.Equal(RgbEffects.ColorAt(rgb, t, 0.25), RgbEffects.ColorAt(copy, t, 0.25)); // serviço e interface calculam igual

        // Amostra o meio de cada intervalo (cor "parada") e compara consecutivas
        double period = 800;
        var held = Enumerable.Range(0, 40).Select(i => RgbEffects.ColorAt(rgb, (long)(i * period + period * 0.3))).ToList();
        for (int i = 1; i < held.Count; i++)
            Assert.NotEqual(held[i - 1], held[i]);
    }

    [Fact]
    public void Led_phase_spreads_chain_and_random_uses_whole_fans()
    {
        Assert.Equal(0, RgbEffects.LedPhase(RgbEffect.Rainbow, 0, 34, 4));
        Assert.InRange(RgbEffects.LedPhase(RgbEffect.Rainbow, 68, 34, 4), 0.49, 0.51);
        Assert.Equal(RgbEffects.LedPhase(RgbEffect.Random, 34, 34, 4), RgbEffects.LedPhase(RgbEffect.Random, 60, 34, 4));
        Assert.InRange(RgbEffects.LedPhase(RgbEffect.Rainbow, 10_000, 34, 4), 0, 0.9999); // LEDs extras não estouram
    }

    [Fact]
    public void Normalize_cleans_invalid_palette_and_speed()
    {
        var rgb = new RgbConfig { R = 1, G = 2, B = 3, EffectSpeed = 50, Palette = new() { "nope", "#abc", "#112233" }, Effect = (RgbEffect)99 };
        rgb.Normalize();
        Assert.Equal(10, rgb.EffectSpeed);
        Assert.Equal(new[] { "#abc", "#112233" }, rgb.Palette);
        Assert.Equal(RgbEffect.Static, rgb.Effect);

        rgb.Palette = new() { "x" };
        rgb.Normalize();
        Assert.Equal(new[] { "#010203" }, rgb.Palette);
    }

    [Fact]
    public void Effect_settings_count_as_output_changes()
    {
        var a = new RgbConfig();
        var b = Json.Clone(a);
        Assert.True(a.SameOutputAs(b));
        b.Effect = RgbEffect.Rainbow;
        Assert.False(a.SameOutputAs(b));
        b = Json.Clone(a);
        b.Palette.Add("#FFFFFF");
        Assert.False(a.SameOutputAs(b));
    }

    [Fact]
    public void Engine_animates_hub_with_per_led_frames_when_spread()
    {
        using var f = new EngineFixture();
        f.Engine.UpdateConfig(c => { c.Rgb.Effect = RgbEffect.Rainbow; c.Rgb.EffectSpeed = 10; c.Rgb.SpreadOnFans = true; c.Rgb.SyncHub = true; });
        EngineFixture.WaitUntil(() => f.Hw.Hub.SetLedsCalls >= 5, because: "quadros contínuos do efeito no hub");
        var leds = f.Hw.Hub.LastLeds!;
        // Corrente espalhada: primeira e meio da corrente em pontos diferentes do arco-íris
        int mid = leds.Length / 3 / 2 * 3;
        Assert.NotEqual((leds[0], leds[1], leds[2]), (leds[mid], leds[mid + 1], leds[mid + 2]));

        // Volta para cor fixa: para de enviar quadros
        f.Engine.UpdateConfig(c => c.Rgb.Effect = RgbEffect.Static);
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastRgb is { } l && l.R == f.Engine.GetConfig().Rgb.OutputFor(RgbTargets.Hub).R, because: "cor fixa reaplicada");
        Thread.Sleep(150);
        int calls = f.Hw.Hub.SetLedsCalls;
        Thread.Sleep(400);
        Assert.Equal(calls, f.Hw.Hub.SetLedsCalls);
    }

    [Fact]
    public void Engine_animates_aux_devices_and_ram_on_its_own_lane()
    {
        using var f = new EngineFixture();
        int gpu0 = f.Hw.GpuRgb.Calls, ram0 = f.Hw.RamRgb.Calls;
        f.Engine.UpdateConfig(c => { c.Rgb.Effect = RgbEffect.Breathing; c.Rgb.EffectSpeed = 10; });
        EngineFixture.WaitUntil(() => f.Hw.GpuRgb.Calls >= gpu0 + 5 && f.Hw.RamRgb.Calls >= ram0 + 3, because: "GPU e RAM acompanham o efeito");
    }

    [Fact]
    public void Rgb_devices_share_phase_with_independent_frame_rates()
    {
        var rgb = new RgbConfig { Effect = RgbEffect.Random, EffectSpeed = 10, SpreadOnFans = false };
        const long sharedFrame = 4500;
        var expected = rgb.OutputFor(RgbTargets.Hub, sharedFrame, 0);
        int hubInterval = RgbFrameClock.IntervalMs(new EngineOptions().HubRgbMinInterval);
        int fallbackRamInterval = RgbFrameClock.IntervalMs(new EngineOptions().RamRgbMinInterval);

        Assert.True(hubInterval < fallbackRamInterval);
        Assert.True(RgbFrameClock.PeripheralIntervalMs < hubInterval);
        foreach (var (target, interval, writeMs) in new[]
        {
            (RgbTargets.Ram, fallbackRamInterval, 80),
            (RgbTargets.Hub, hubInterval, 12),
            (RgbTargets.Peripherals, RgbFrameClock.PeripheralIntervalMs, 2)
        })
        {
            var clock = new RgbFrameClock();
            clock.RecordFrame(sharedFrame - interval, interval, TimeSpan.FromMilliseconds(writeMs));
            long start = sharedFrame - writeMs;

            Assert.Equal(TimeSpan.FromMilliseconds(1), clock.UntilNextFrame(start - 1, interval));
            Assert.False(clock.IsDue(clock.FrameTime(start - 1, interval), interval));
            long frame = clock.FrameTime(start, interval);
            Assert.True(clock.IsDue(frame, interval));
            Assert.Equal(sharedFrame, frame);
            Assert.Equal(expected, rgb.OutputFor(target, frame, 0));
            Assert.Equal(sharedFrame + interval * 2L, clock.FrameTime(start + interval * 2 + 10, interval));
        }
    }
}
