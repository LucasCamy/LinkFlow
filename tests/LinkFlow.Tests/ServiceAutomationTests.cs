namespace LinkFlow.Tests;

public sealed class ServiceAutomationTests
{
    [Fact]
    public void Quiet_hours_are_decided_without_an_interface()
    {
        var cfg = new ControlConfig();
        cfg.Automation.Quiet = new QuietHours { Enabled = true, StartHour = 0, EndHour = 0, Profile = FanPresets.Silent };
        using var fixture = new EngineFixture(cfg);
        EngineFixture.WaitUntil(() => fixture.Engine.GetConfig().ActivePreset == FanPresets.Silent);
        Assert.Equal("quiet", fixture.Engine.GetAutomationState().Reason);
        Assert.Equal(FanPresets.Balanced, fixture.Engine.GetAutomationState().BaseProfile);
    }

    [Fact]
    public void Rule_priority_and_manual_override_are_explicit()
    {
        var cfg = new ControlConfig();
        cfg.Automation.Rules = [new() { Process = "game", Profile = FanPresets.Performance, Priority = 10 }, new() { Process = "game", Profile = FanPresets.Max, Priority = 20 }];
        using var fixture = new EngineFixture(cfg);
        fixture.Engine.SetSessionContext(this, new() { ForegroundProcess = "game", RunningProcesses = ["game"] });
        Assert.Equal(FanPresets.Max, fixture.Engine.GetConfig().ActivePreset);
        fixture.Engine.UpdateConfig(c => FanPresets.Apply(c, FanPresets.Silent));
        fixture.Engine.EvaluateAutomationNow();
        Assert.Equal(FanPresets.Silent, fixture.Engine.GetConfig().ActivePreset);
        Assert.True(fixture.Engine.GetAutomationState().Suppressed);
        fixture.Engine.SetSessionContext(this, new() { ForegroundProcess = "explorer" });
        Assert.Null(fixture.Engine.GetAutomationState().Reason);
    }

    [Fact]
    public void Context_rejects_stale_observations_and_executable_paths()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new AutomationSessionContext { ObservedAtUtc = now.AddMinutes(-3) }.Normalize(now));
        Assert.Throws<ArgumentException>(() => new AutomationSessionContext { ForegroundProcess = @"C:\game.exe" }.Normalize(now));
    }

    [Fact]
    public void Timed_manual_override_restores_base_and_expires()
    {
        var cfg = new ControlConfig();
        cfg.Automation.Quiet = new QuietHours { Enabled = true, StartHour = 0, EndHour = 0 };
        using var fixture = new EngineFixture(cfg);
        fixture.Engine.SetAutomationOverride(DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(FanPresets.Balanced, fixture.Engine.GetConfig().ActivePreset);
        Assert.True(fixture.Engine.GetAutomationState().Suppressed);
        fixture.Engine.SetAutomationOverride(null);
        Assert.Equal(FanPresets.Silent, fixture.Engine.GetConfig().ActivePreset);
    }
}
