using System.Windows.Threading;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

/// <summary>
/// Testes da camada de interface (ViewModel) com o engine simulado: perfis, desfazer, seleção múltipla,
/// ações em lote, grupos, cor e regressão da "desvinculação em cascata".
/// Os comandos testados são os mesmos acionados pelos atalhos de teclado (F1–F5, Ctrl+Z, Ctrl+↑/↓, Ctrl+A).
/// </summary>
public sealed class ViewModelTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private readonly LocalEngineClient _client;
    private readonly MainViewModel _vm;

    public ViewModelTests()
    {
        _client = new LocalEngineClient(_f.Engine);
        _vm = new MainViewModel(_client, Dispatcher.CurrentDispatcher);
        WaitFor(() => _vm.Fans.Count == 11, "ventoinhas carregadas na interface");
    }

    public void Dispose() => _f.Dispose();

    /// <summary>Processa a fila do Dispatcher até a condição ser verdadeira.</summary>
    private static void WaitFor(Func<bool> condition, string because)
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

    private FanViewModel Fan(int channel) => _vm.Fans.First(f => f.Channel == channel);

    [Fact]
    public void F4_applies_max_preset_and_ctrl_z_undoes()
    {
        _vm.ApplyPresetCommand.Execute("4");
        Assert.Equal(FanPresets.Max, _client.Config.ActivePreset);
        Assert.Equal(100, _client.Config.GlobalPercent);
        Assert.True(_vm.ToastVisible && _vm.ToastCanUndo);

        _vm.UndoCommand.Execute(null);
        Assert.Equal(FanPresets.Balanced, _client.Config.ActivePreset);
        Assert.Equal(55, _client.Config.GlobalPercent);
        Assert.Equal(55, _vm.GlobalSpeedPercent);
    }

    [Fact]
    public void Moving_global_slider_keeps_fans_linked()
    {
        _vm.GlobalSpeedPercent = 80;
        _vm.GlobalSpeedPercent = 40;
        WaitFor(() => _f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 40, "hub recebeu 40%");

        Assert.All(_vm.Fans, f => Assert.True(f.IsLinked));
        Assert.All(_client.Config.Fans.Values, o => Assert.True(o.Linked));
        Assert.Equal(40, Fan(1).SliderValue);
        Assert.Equal(FanLimits.PumpMinPercent, Fan(11).SliderValue);
    }

    [Fact]
    public void Dragging_one_fan_unlinks_only_that_fan()
    {
        Fan(3).SliderValue = 90;
        Assert.False(Fan(3).IsLinked);
        Assert.True(Fan(1).IsLinked);
        Assert.False(_client.Config.GetFan(Fan(3).Key).Linked);
        WaitFor(() => _f.Hw.Hub.LastSpeeds.GetValueOrDefault(3) == 90, "fan 3 em 90%");

        _vm.ToggleLinkCommand.Execute(Fan(3));
        Assert.True(Fan(3).IsLinked);
        WaitFor(() => _f.Hw.Hub.LastSpeeds.GetValueOrDefault(3) == 55, "fan 3 de volta ao global");
    }

    [Fact]
    public void Global_step_shortcut_changes_power_and_leaves_curve()
    {
        _vm.ApplyPresetCommand.Execute("5"); // curva
        Assert.True(_vm.IsCurveMode);

        _vm.GlobalStepCommand.Execute("-5");
        Assert.True(_vm.IsFixedMode);
        Assert.Equal(50, _client.Config.GlobalPercent);
    }

    [Fact]
    public void Click_selection_supports_ctrl_and_shift()
    {
        _vm.HandleFanClick(Fan(2), ctrl: false, shift: false);
        Assert.Equal(1, _vm.SelectedCount);

        _vm.HandleFanClick(Fan(5), ctrl: false, shift: true);
        Assert.Equal(new[] { 2, 3, 4, 5 }, _vm.Fans.Where(f => f.IsSelected).Select(f => f.Channel));

        _vm.HandleFanClick(Fan(9), ctrl: true, shift: false);
        Assert.Equal(5, _vm.SelectedCount);

        _vm.InvertSelectionCommand.Execute(null);
        Assert.Equal(6, _vm.SelectedCount);

        _vm.ClearSelectionCommand.Execute(null);
        Assert.False(_vm.HasSelection);
    }

    [Fact]
    public void Batch_apply_group_and_undo()
    {
        _vm.SelectFansCommand.Execute(null);
        Assert.Equal(10, _vm.SelectedCount);

        _vm.ClearSelectionCommand.Execute(null);
        Fan(1).IsSelected = true;
        Fan(2).IsSelected = true;
        _vm.BatchPercent = 70;
        _vm.ApplyBatchCommand.Execute(null);

        Assert.Equal(70, _client.Config.GetFan(Fan(1).Key).Percent);
        Assert.False(_client.Config.GetFan(Fan(2).Key).Linked);
        WaitFor(() => _f.Hw.Hub.LastSpeeds.GetValueOrDefault(2) == 70, "lote aplicado no hub");

        _vm.GroupNameInput = "Frontal";
        _vm.SetGroupCommand.Execute(null);
        Assert.Contains("Frontal", _vm.AvailableGroups);

        _vm.ClearSelectionCommand.Execute(null);
        _vm.SelectGroupCommand.Execute("Frontal");
        Assert.Equal(new[] { 1, 2 }, _vm.Fans.Where(f => f.IsSelected).Select(f => f.Channel));

        _vm.UndoCommand.Execute(null); // desfaz o agrupamento
        Assert.Null(_client.Config.GetFan(Fan(1).Key).Group);
    }

    [Fact]
    public void Profiles_can_be_saved_applied_and_deleted()
    {
        _vm.GlobalSpeedPercent = 35;
        _vm.NewProfileName = "Noite";
        _vm.SaveProfileCommand.Execute(null);
        Assert.Contains(_vm.Profiles, p => p.Name == "Noite");

        _vm.ApplyPresetCommand.Execute(FanPresets.Max);
        _vm.ApplyProfileCommand.Execute("Noite");
        Assert.Equal(35, _client.Config.GlobalPercent);
        Assert.Equal("Noite", _vm.ActivePresetLabel);

        _vm.DeleteProfileCommand.Execute("Noite");
        Assert.Empty(_vm.Profiles);
    }

    [Fact]
    public void Hex_input_and_hue_update_color()
    {
        _vm.HexInput = "#f00";
        Assert.Equal((byte)255, _vm.RgbR);
        Assert.Equal((byte)0, _vm.RgbG);
        Assert.Equal(0, _vm.Hue, 0);
        WaitFor(() => _f.Hw.Hub.LastRgb?.R == 255 && _f.Hw.Hub.LastRgb?.G == 0, "cor no hub");

        _vm.Hue = 120;
        Assert.Equal("#00FF00", _vm.CurrentRgbHex);

        _vm.HexInput = "xyz";
        Assert.True(_vm.HexInvalid);
        Assert.Equal("#00FF00", _vm.CurrentRgbHex);

        _vm.ApplyColorCommand.Execute("#A855F7");
        Assert.Equal("#A855F7", _vm.RecentColors.First());
    }

    [Fact]
    public void Snapshot_reaches_interface_without_blocking()
    {
        WaitFor(() => _vm.Snapshot.HubConnected && _vm.CpuTempFormatted != Fmt.Missing, "telemetria na tela");
        Assert.Contains("RPM", _vm.AverageFanRpmFormatted);
        Assert.False(string.IsNullOrEmpty(_vm.RamDetail));
    }
}
