using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class CommandAcknowledgementReliabilityTests
{
    private static LinkFlowEngine Unstarted(ControlConfig? config = null) => new(new SimulatedHardwareFactory(),
        new EngineOptions { ConfigPath = null }, config ?? new ControlConfig());

    private static EngineCommandRequest Change(LinkFlowEngine engine, Action<ControlConfig> change)
    {
        var config = engine.GetConfig(); change(config);
        return new() { Kind = PipeProtocol.SetConfig, ExpectedRevision = config.Revision, Config = config };
    }
    private static CommandReceipt Current(LinkFlowEngine engine, string id) => engine.GetCommandReceipts().Single(r => r.Id == id);

    [Fact]
    public void Repeated_animation_acknowledgements_keep_first_timestamp_and_do_not_flood_accepted_receipts()
    {
        using var engine = Unstarted();
        var events = new List<CommandReceipt>(); engine.CommandUpdated += events.Add;
        var receipt = engine.ExecuteCommand(Change(engine, c => c.Rgb.R = 91));
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, true, "controller-accepted");
        var first = Current(engine, receipt.Id);
        var firstTarget = first.Targets.Single(t => t.Target == "rgb.hub");
        int count = events.Count;
        for (int frame = 0; frame < 1000; frame++) engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, true, "controller-accepted");

        var latest = Current(engine, receipt.Id);
        Assert.Equal(CommandState.Accepted, latest.State); // Outros destinos não confirmaram.
        Assert.Equal(count, events.Count);
        Assert.Equal(first.UpdatedAtUtc, latest.UpdatedAtUtc);
        Assert.Equal(firstTarget.ConfirmedAtUtc, latest.Targets.Single(t => t.Target == "rgb.hub").ConfirmedAtUtc);
        Assert.Equal(2, events.Count); // Aceitação inicial + primeira confirmação parcial.
    }

    [Fact]
    public void Real_target_result_transitions_still_publish_and_an_old_revision_cannot_replace_them()
    {
        using var engine = Unstarted();
        var events = new List<CommandReceipt>(); engine.CommandUpdated += events.Add;
        var receipt = engine.ExecuteCommand(Change(engine, c => c.Rgb.R = 92));
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, false, "busy");
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, false, "rejected");
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, true, "controller-accepted");
        var confirmed = Current(engine, receipt.Id).Targets.Single(t => t.Target == "rgb.hub");
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision - 1, false, "obsolete");
        Assert.Equal(4, events.Count);
        Assert.True(confirmed.Accepted);
        Assert.Equal("controller-accepted", confirmed.Detail);
        Assert.Equal(confirmed, Current(engine, receipt.Id).Targets.Single(t => t.Target == "rgb.hub"));
    }

    [Fact]
    public async Task Duplicate_result_still_checks_expiry_instead_of_returning_early()
    {
        using var engine = Unstarted();
        var request = Change(engine, c => c.Rgb.R = 93);
        request.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        var receipt = engine.ExecuteCommand(request);
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, true);
        var firstAt = Current(engine, receipt.Id).Targets.Single(t => t.Target == "rgb.hub").ConfirmedAtUtc;
        await Task.Delay(2100);
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, true);
        var expired = Current(engine, receipt.Id);
        Assert.Equal(CommandState.NotVerifiable, expired.State);
        Assert.Equal(firstAt, expired.Targets.Single(t => t.Target == "rgb.hub").ConfirmedAtUtc);
        Assert.Equal(CommandConfirmation.None, expired.Confirmation);
    }

    [Fact]
    public void Snapshot_ticks_expire_a_partial_receipt_without_a_new_device_result()
    {
        var hardware = new SimulatedHardwareFactory();
        hardware.PeripheralsRgb.FailInitialization = true;
        hardware.Sensors.OpenDelay = TimeSpan.Zero;
        using var engine = new LinkFlowEngine(hardware, new EngineOptions
        { ConfigPath = null, TelemetryInterval = TimeSpan.FromMilliseconds(40), SnapshotInterval = TimeSpan.FromMilliseconds(30) }, new ControlConfig());
        engine.Start();
        EngineFixture.WaitUntil(() => engine.GetSnapshot().HubConnected);
        var request = Change(engine, c => c.Rgb.R = 94);
        request.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        var receipt = engine.ExecuteCommand(request);
        EngineFixture.WaitUntil(() => Current(engine, receipt.Id).State == CommandState.NotVerifiable);
        Assert.Contains(Current(engine, receipt.Id).Targets, t => t.Target == "rgb.peripherals" && t.Accepted != true);
    }

    [Fact]
    public void Persistence_callbacks_publish_only_real_state_changes()
    {
        using var engine = Unstarted();
        var events = new List<CommandReceipt>(); engine.CommandUpdated += events.Add;
        var receipt = engine.ExecuteCommand(Change(engine, c => c.ActivePreset = "Persistência deduplicada"));
        engine.ReportConfigurationPersistence(receipt.Revision, true);
        var firstSave = Current(engine, receipt.Id);
        for (int repeat = 0; repeat < 100; repeat++) engine.ReportConfigurationPersistence(receipt.Revision, true);
        Assert.Equal(2, events.Count);
        Assert.Equal(firstSave.UpdatedAtUtc, Current(engine, receipt.Id).UpdatedAtUtc);
        engine.ReportConfigurationPersistence(receipt.Revision, false);
        for (int repeat = 0; repeat < 100; repeat++) engine.ReportConfigurationPersistence(receipt.Revision, false);
        Assert.Equal(3, events.Count);
        engine.ReportConfigurationPersistence(receipt.Revision, true);
        Assert.Equal(4, events.Count);
        Assert.Equal(PersistenceState.Saved, Current(engine, receipt.Id).Persistence);
    }

    [Theory]
    [InlineData(RgbTargets.Hub, "rgb.hub")]
    [InlineData(RgbTargets.Gpu, "rgb.gpu")]
    [InlineData(RgbTargets.Motherboard, "rgb.motherboard")]
    [InlineData(RgbTargets.Ram, "rgb.ram")]
    [InlineData(RgbTargets.Peripherals, "rgb.peripherals")]
    public void Per_device_brightness_requires_only_that_devices_confirmation(string device, string target)
    {
        using var engine = Unstarted();
        var receipt = engine.ExecuteCommand(Change(engine, c => c.Rgb.DeviceBrightness[device] = 0));
        Assert.Equal(target, Assert.Single(receipt.Targets).Target);
        engine.ReportDeviceCommandResult(target, receipt.Revision, true);
        Assert.Equal(CommandState.Applied, Current(engine, receipt.Id).State);
        Assert.Equal(CommandConfirmation.Controller, Current(engine, receipt.Id).Confirmation);
        Assert.NotEqual(CommandConfirmation.Physical, Current(engine, receipt.Id).Confirmation);
    }

    [Fact]
    public void Global_color_changes_still_require_all_enabled_destinations()
    {
        using var engine = Unstarted();
        var receipt = engine.ExecuteCommand(Change(engine, c => c.Rgb.R = 95));
        Assert.Equal(new[] { "lcd", "rgb.gpu", "rgb.hub", "rgb.motherboard", "rgb.peripherals", "rgb.ram" }, receipt.Targets.Select(t => t.Target).Order());
    }

    [Fact]
    public void Enabling_one_sync_requires_only_that_target_and_disabling_it_remains_a_software_change()
    {
        using var engine = Unstarted(new ControlConfig { Rgb = { SyncGpu = false } });
        var enabled = engine.ExecuteCommand(Change(engine, c => c.Rgb.SyncGpu = true));
        Assert.Equal("rgb.gpu", Assert.Single(enabled.Targets).Target);
        var disabled = engine.ExecuteCommand(Change(engine, c => c.Rgb.SyncGpu = false));
        Assert.Empty(disabled.Targets);
        Assert.Equal(CommandState.Applied, disabled.State);
        Assert.Equal(CommandConfirmation.Software, disabled.Confirmation);
        Assert.Equal(CommandState.Superseded, Current(engine, enabled.Id).State);
    }

    [Fact]
    public void Recent_colors_and_default_brightness_representation_do_not_require_hardware_acknowledgements()
    {
        using var engine = Unstarted();
        var colors = engine.ExecuteCommand(Change(engine, c => c.Rgb.RecentColors.Add("#123456")));
        Assert.Empty(colors.Targets);
        var explicitDefault = engine.ExecuteCommand(Change(engine, c => c.Rgb.DeviceBrightness[RgbTargets.Hub] = 100));
        Assert.Empty(explicitDefault.Targets);
        var implicitDefault = engine.ExecuteCommand(Change(engine, c => c.Rgb.DeviceBrightness.Remove(RgbTargets.Hub)));
        Assert.Empty(implicitDefault.Targets);
        Assert.Equal(CommandConfirmation.Software, implicitDefault.Confirmation);
    }

    [Fact]
    public void Hub_spreading_and_lcd_sync_require_only_their_respective_controller()
    {
        using var engine = Unstarted();
        var spread = engine.ExecuteCommand(Change(engine, c => c.Rgb.SpreadOnFans = !c.Rgb.SpreadOnFans));
        Assert.Equal("rgb.hub", Assert.Single(spread.Targets).Target);
        var lcd = engine.ExecuteCommand(Change(engine, c => c.Rgb.SyncLcd = !c.Rgb.SyncLcd));
        Assert.Equal("lcd", Assert.Single(lcd.Targets).Target);
    }

    [Fact]
    public void Animated_program_changes_are_detected_even_when_the_frame_at_time_zero_is_identical()
    {
        var config = new ControlConfig { Rgb = { Effect = RgbEffect.Rainbow, EffectSpeed = 2 } };
        using var engine = Unstarted(config);
        var changed = engine.GetConfig(); changed.Rgb.EffectSpeed = 9;
        Assert.Equal(config.Rgb.OutputFor(RgbTargets.Hub, 0, 0), changed.Rgb.OutputFor(RgbTargets.Hub, 0, 0));
        var receipt = engine.ExecuteCommand(new() { Kind = PipeProtocol.SetConfig, ExpectedRevision = changed.Revision, Config = changed });
        Assert.Equal(6, receipt.Targets.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_ack_never_becomes_applied_and_is_retained_even_if_expiry_was_already_published(bool expiryPublishedFirst)
    {
        using var engine = Unstarted();
        var request = Change(engine, c => c.Rgb.DeviceBrightness[RgbTargets.Hub] = 0);
        request.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(2);
        var receipt = engine.ExecuteCommand(request);
        await Task.Delay(2100);
        if (expiryPublishedFirst) engine.ReportDeviceCommandResult("unrelated", receipt.Revision, true);
        engine.ReportDeviceCommandResult("rgb.hub", receipt.Revision, true);
        var late = Current(engine, receipt.Id);
        Assert.Equal(CommandState.NotVerifiable, late.State);
        Assert.Equal(CommandConfirmation.None, late.Confirmation);
        var target = Assert.Single(late.Targets);
        Assert.True(target.Accepted);
        Assert.True(target.ConfirmedAtUtc > receipt.ExpiresAtUtc);
        Assert.Contains("após o prazo", late.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_new_configuration_revision_supersedes_pending_global_revisions_even_for_disjoint_or_metadata_changes(bool metadataOnly)
    {
        using var engine = Unstarted();
        var first = engine.ExecuteCommand(Change(engine, c => c.Rgb.DeviceBrightness[RgbTargets.Hub] = 0));
        var second = engine.ExecuteCommand(Change(engine, c =>
        {
            if (metadataOnly) c.ActivePreset = "Só metadados";
            else c.Rgb.DeviceBrightness[RgbTargets.Gpu] = 25;
        }));
        Assert.Equal(CommandState.Superseded, Current(engine, first.Id).State);
        engine.ReportDeviceCommandResult("rgb.hub", second.Revision, true);
        Assert.Equal(CommandState.Superseded, Current(engine, first.Id).State);
        Assert.Contains("revisão", Current(engine, first.Id).Message);
    }

    [Fact]
    public void A_new_configuration_does_not_change_a_previous_applied_receipt()
    {
        using var engine = Unstarted();
        var first = engine.ExecuteCommand(Change(engine, c => c.Rgb.DeviceBrightness[RgbTargets.Hub] = 0));
        engine.ReportDeviceCommandResult("rgb.hub", first.Revision, true);
        Assert.Equal(CommandState.Applied, Current(engine, first.Id).State);
        engine.ExecuteCommand(Change(engine, c => c.ActivePreset = "Outra revisão"));
        Assert.Equal(CommandState.Applied, Current(engine, first.Id).State);
    }
}
