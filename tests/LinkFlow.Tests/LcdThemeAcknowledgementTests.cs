using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class LcdThemeAcknowledgementTests
{
    private static LinkFlowEngine Unstarted(ControlConfig? config = null) => new(new SimulatedHardwareFactory(),
        new EngineOptions { ConfigPath = null }, config ?? new ControlConfig());
    private static CommandReceipt Change(LinkFlowEngine engine, Action<ControlConfig> edit)
    {
        var config = engine.GetConfig(); edit(config);
        return engine.ExecuteCommand(new EngineCommandRequest { Kind = PipeProtocol.SetConfig, Config = config, ExpectedRevision = config.Revision });
    }

    [Theory]
    [InlineData(RgbTargets.Hub)]
    [InlineData(RgbTargets.Gpu)]
    [InlineData(RgbTargets.Motherboard)]
    [InlineData(RgbTargets.Ram)]
    [InlineData(RgbTargets.Peripherals)]
    public void Compensating_general_and_device_brightness_preserves_that_destinations_entire_output(string target)
    {
        var original = new RgbConfig { Brightness = 100, Effect = RgbEffect.Breathing };
        original.DeviceBrightness[target] = 50;
        var next = original.Clone(); next.Brightness = 50; next.DeviceBrightness[target] = 100;
        Assert.True(original.SameOutputFor(next, target));
        Assert.True(original.SameLcdThemeAs(next));
        foreach (long time in new long[] { 0, 1000, 2000, 5432 })
            Assert.Equal(original.OutputFor(target, time, 0), next.OutputFor(target, time, 0));
    }

    [Fact]
    public void Compensated_hub_brightness_does_not_require_hub_or_lcd_but_other_leds_still_change()
    {
        using var engine = Unstarted(new ControlConfig { Rgb = { Brightness = 100, DeviceBrightness = { [RgbTargets.Hub] = 50 } } });
        var receipt = Change(engine, c => { c.Rgb.Brightness = 50; c.Rgb.DeviceBrightness[RgbTargets.Hub] = 100; });
        Assert.Equal(new[] { "rgb.gpu", "rgb.motherboard", "rgb.peripherals", "rgb.ram" }, receipt.Targets.Select(t => t.Target).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Lcd_sync_changes_require_lcd_confirmation_without_unrelated_led_targets(bool initiallySynced)
    {
        using var engine = Unstarted(new ControlConfig { Rgb = { SyncLcd = initiallySynced, R = 255, G = 20, B = 10 } });
        var receipt = Change(engine, c => c.Rgb.SyncLcd = !initiallySynced);
        Assert.Equal("lcd", Assert.Single(receipt.Targets).Target);
        engine.ReportDeviceCommandResult("lcd", receipt.Revision, true);
        Assert.Equal(CommandState.Applied, engine.GetCommandReceipts().Single(r => r.Id == receipt.Id).State);
    }

    [Fact]
    public void Unsynced_lcd_ignores_global_color_while_synced_lcd_requires_its_own_confirmation()
    {
        using var synced = Unstarted();
        Assert.Contains(Change(synced, c => c.Rgb.R = 120).Targets, t => t.Target == "lcd");
        using var unsynced = Unstarted(new ControlConfig { Rgb = { SyncLcd = false } });
        Assert.DoesNotContain(Change(unsynced, c => c.Rgb.R = 120).Targets, t => t.Target == "lcd");
    }

    [Fact]
    public void Metadata_brightness_gamma_and_hub_spreading_do_not_change_the_lcd_theme()
    {
        using var engine = Unstarted();
        var receipt = Change(engine, c =>
        {
            c.Rgb.RecentColors.Add("#ABCDEF"); c.Rgb.Brightness = 73; c.Rgb.LedGamma = 1.3f;
            c.Rgb.DeviceBrightness[RgbTargets.Hub] = 30; c.Rgb.SpreadOnFans = !c.Rgb.SpreadOnFans;
        });
        Assert.DoesNotContain(receipt.Targets, t => t.Target == "lcd");
    }

    [Fact]
    public void Lcd_configuration_change_still_requires_lcd_confirmation()
    {
        using var engine = Unstarted();
        Assert.Equal("lcd", Assert.Single(Change(engine, c => c.Lcd.Title = "Novo título").Targets).Target);
    }

    [Fact]
    public void Animated_lcd_theme_compares_the_program_but_ignores_breathing_brightness()
    {
        var rainbow = new RgbConfig { Effect = RgbEffect.Rainbow, EffectSpeed = 2 };
        var faster = rainbow.Clone(); faster.EffectSpeed = 9;
        Assert.False(rainbow.SameLcdThemeAs(faster));
        var breathing = new RgbConfig { Effect = RgbEffect.Breathing, EffectSpeed = 2 };
        var other = breathing.Clone(); other.EffectSpeed = 9; other.Brightness = 20;
        Assert.True(breathing.SameLcdThemeAs(other));
        other.Effect = RgbEffect.Static;
        Assert.True(breathing.SameLcdThemeAs(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changing_lcd_sync_redraws_and_confirms_even_with_auto_stream_disabled(bool initiallySynced)
    {
        using var fixture = new EngineFixture(new ControlConfig
        { Lcd = { AutoStream = false }, Rgb = { SyncLcd = initiallySynced, R = 255, G = 10, B = 10 } });
        EngineFixture.WaitUntil(() => fixture.Hw.Lcd.Frames > 0);
        int frames = fixture.Hw.Lcd.Frames;
        byte[] before = fixture.Hw.Lcd.LastFrame!.ToArray();
        var receipt = Change(fixture.Engine, c => c.Rgb.SyncLcd = !initiallySynced);
        EngineFixture.WaitUntil(() => fixture.Hw.Lcd.Frames > frames && fixture.Engine.GetCommandReceipts().Any(r => r.Id == receipt.Id && r.State == CommandState.Applied));
        Assert.Equal("lcd", Assert.Single(receipt.Targets).Target);
        Assert.False(before.SequenceEqual(fixture.Hw.Lcd.LastFrame!));
    }

    [Fact]
    public void Changing_lcd_configuration_redraws_and_confirms_without_auto_stream()
    {
        using var fixture = new EngineFixture(new ControlConfig { Lcd = { AutoStream = false } });
        EngineFixture.WaitUntil(() => fixture.Hw.Lcd.Frames > 0);
        int frames = fixture.Hw.Lcd.Frames;
        var receipt = Change(fixture.Engine, c => c.Lcd.Title = "Configuração enviada");
        EngineFixture.WaitUntil(() => fixture.Hw.Lcd.Frames > frames && fixture.Engine.GetCommandReceipts().Any(r => r.Id == receipt.Id && r.State == CommandState.Applied));
    }

    [Fact]
    public void A_global_theme_change_redraws_and_confirms_lcd_without_auto_stream()
    {
        using var fixture = new EngineFixture(new ControlConfig { Lcd = { AutoStream = false } });
        EngineFixture.WaitUntil(() => fixture.Hw.Lcd.Frames > 0);
        int frames = fixture.Hw.Lcd.Frames;
        var receipt = Change(fixture.Engine, c => { c.Rgb.R = 255; c.Rgb.G = 10; c.Rgb.B = 10; });
        Assert.Contains(receipt.Targets, t => t.Target == "lcd");
        EngineFixture.WaitUntil(() => fixture.Hw.Lcd.Frames > frames && fixture.Engine.GetCommandReceipts().Any(r =>
            r.Id == receipt.Id && r.Targets.Any(t => t.Target == "lcd" && t.Accepted == true)));
    }

    [Fact]
    public async Task A_hub_only_brightness_or_metadata_change_does_not_redraw_the_manual_lcd()
    {
        using var fixture = new EngineFixture(new ControlConfig { Lcd = { AutoStream = false } });
        EngineFixture.WaitUntil(() => fixture.Hw.Lcd.Frames > 0);
        int frames = fixture.Hw.Lcd.Frames;
        var receipt = Change(fixture.Engine, c => { c.Rgb.DeviceBrightness[RgbTargets.Hub] = 0; c.Rgb.RecentColors.Add("#ABCDEF"); });
        Assert.Equal("rgb.hub", Assert.Single(receipt.Targets).Target);
        EngineFixture.WaitUntil(() => fixture.Engine.GetCommandReceipts().Any(r => r.Id == receipt.Id && r.State == CommandState.Applied));
        await Task.Delay(150);
        Assert.Equal(frames, fixture.Hw.Lcd.Frames);
    }
}
