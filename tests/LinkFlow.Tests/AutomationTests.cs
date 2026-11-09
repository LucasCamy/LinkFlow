using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

/// <summary>Curvas por grupo, ventoinha conectada com o PC ligado, horário silencioso e perfil por app.</summary>
public sealed class AutomationTests : IDisposable
{
    private readonly List<IDisposable> _dispose = new();
    public void Dispose() { foreach (var d in _dispose) d.Dispose(); }

    private static void Pump(Func<bool> condition, string because)
    {
        var deadline = DateTime.Now.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.Now > deadline) Assert.Fail($"Tempo esgotado: {because}");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
            Thread.Sleep(15);
        }
    }

    [Fact]
    public void Group_with_own_curve_gets_its_own_speed()
    {
        var cfg = new ControlConfig { GlobalPercent = 40 };
        cfg.EditFan("SIM-RX-01").Group = "Radiador"; // canal 1
        cfg.GroupCurves["Radiador"] = new FanCurveConfig
        {
            Source = CurveSource.Coolant,
            Points = new() { new(20, 90), new(60, 90) },
            Smoothing = 1
        };
        using var f = new EngineFixture(cfg);
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 90 && f.Hw.Hub.LastSpeeds.GetValueOrDefault(2) == 40,
            because: "grupo segue a própria curva; os outros seguem o global");
        Assert.Equal(90, f.Engine.GetSnapshot().GroupOutputs["Radiador"]);

        f.Engine.UpdateConfig(c => c.GroupCurves.Remove("Radiador"));
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 40, because: "sem curva própria volta ao global");
    }

    [Fact]
    public void Fan_plugged_into_the_hub_chain_appears_without_restart()
    {
        using var f = new EngineFixture(configure: o => o.SubDeviceCheckInterval = TimeSpan.FromMilliseconds(100));
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Count == 11);
        f.Hw.Hub.PlugFan(12);
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Count == 12, because: "nova ventoinha detectada");
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.ContainsKey(12), because: "velocidade aplicada na nova ventoinha");
    }

    [Theory]
    [InlineData(23, 7, 23, true)]
    [InlineData(23, 7, 3, true)]
    [InlineData(23, 7, 7, false)]
    [InlineData(23, 7, 12, false)]
    [InlineData(13, 15, 14, true)]
    public void Quiet_hours_window_handles_midnight(int start, int end, int hour, bool expected)
    {
        var q = new QuietHours { Enabled = true, StartHour = start, EndHour = end };
        Assert.Equal(expected, q.Contains(new DateTime(2026, 1, 1, hour, 30, 0)));
    }

    private (MainViewModel Vm, LocalEngineClient Client, EngineFixture F) CreateVm()
    {
        var f = new EngineFixture();
        _dispose.Add(f);
        var client = new LocalEngineClient(f.Engine);
        var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher);
        Pump(() => vm.Fans.Count == 11, "ventoinhas");
        return (vm, client, f);
    }

    [Fact]
    public void App_rule_applies_profile_and_restores_previous_one()
    {
        var (vm, client, _) = CreateVm();
        vm.NewRuleProcess = "jogo-que-nao-existe.exe";
        vm.AddRuleCommand.Execute(null);
        Assert.Equal("jogo-que-nao-existe", client.Config.Automation.Rules.Single().Process);
        vm.AppRules[0].Profile = FanPresets.Max;

        using var auto = new AutomationService(vm);
        auto.SimulateForeground("jogo-que-nao-existe");
        Assert.Equal(FanPresets.Max, client.Config.ActivePreset);
        Assert.Contains("Máximo", auto.Status);

        // O processo não está rodando de verdade: ao trocar de janela, a regra termina e o perfil volta
        auto.SimulateForeground("explorer");
        Assert.Equal(FanPresets.Balanced, client.Config.ActivePreset);
        Assert.Equal(string.Empty, auto.Status);
    }

    [Fact]
    public void Manual_change_pauses_automation_until_reason_changes()
    {
        var (vm, client, _) = CreateVm();
        vm.NewRuleProcess = "editor";
        vm.AddRuleCommand.Execute(null);
        using var auto = new AutomationService(vm);
        auto.SimulateForeground("editor");
        Assert.Equal(FanPresets.Performance, client.Config.ActivePreset);

        vm.ApplyPreset(FanPresets.Silent); // usuário escolheu outro
        auto.SimulateForeground("editor");
        Assert.Equal(FanPresets.Silent, client.Config.ActivePreset);
        Assert.Contains("Pausada", auto.Status);
    }

    [Fact]
    public void Curve_editor_can_edit_a_group_curve_without_touching_global()
    {
        var (vm, client, _) = CreateVm();
        vm.HandleFanClick(vm.Fans[0], false, false);
        vm.GroupNameInput = "Topo";
        vm.SetGroupCommand.Execute(null);
        Pump(() => vm.GroupCurves.Count == 1, "grupo listado");

        vm.EditCurveCommand.Execute("Topo");
        Assert.True(vm.IsEditingGroupCurve);
        Assert.True(client.Config.GroupCurves.ContainsKey("Topo"));
        var globalBefore = client.Config.Curve.Points.Select(p => p.Percent).ToList();

        vm.OnCurveEdited(new() { new(30, 50), new(80, 95) }, final: true);
        Assert.Equal(new byte[] { 50, 95 }, client.Config.GroupCurves["Topo"].Points.Select(p => p.Percent));
        Assert.Equal(globalBefore, client.Config.Curve.Points.Select(p => p.Percent));

        vm.GroupCurves[0].Source = CurveSource.Gpu;
        Assert.Equal(CurveSource.Gpu, client.Config.GroupCurves["Topo"].Source);

        vm.GroupCurves[0].HasOwnCurve = false;
        Assert.False(client.Config.GroupCurves.ContainsKey("Topo"));
        Assert.False(vm.IsEditingGroupCurve);
    }
}
