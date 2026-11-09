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
    public void Color_square_sets_saturation_and_brightness_and_keeps_hue_through_black()
    {
        _vm.HexInput = "#00F0FF";
        double hue = _vm.Hue;

        _vm.ColorValue = 0;            // arrastou até o preto
        Assert.Equal("#000000", _vm.CurrentRgbHex);
        Assert.Equal(hue, _vm.Hue, 1);  // a matiz não se perde

        _vm.ColorValue = 1;
        _vm.ColorSaturation = 0.5;
        Assert.Equal("#80F8FF", _vm.CurrentRgbHex);

        _vm.RgbR = 255; _vm.RgbG = 0; _vm.RgbB = 0; // slider RGB atualiza o quadro
        Assert.Equal(0, _vm.Hue, 0);
        Assert.Equal(1, _vm.ColorSaturation, 2);
        WaitFor(() => _f.Hw.Hub.LastRgb?.R == 255 && _f.Hw.Hub.LastRgb?.B == 0, "cor no hub");
    }

    [Fact]
    public void Rename_edits_one_card_at_a_time_and_reset_restores_default_with_undo()
    {
        var a = Fan(1);
        var b = Fan(2);
        _vm.BeginRename(a);
        _vm.BeginRename(b);
        Assert.False(a.IsEditingName);
        Assert.True(b.IsEditingName);

        b.Name = "Topo traseira";
        Assert.True(b.HasCustomName);
        Assert.Equal("Topo traseira", _client.Config.GetFan(b.Key).Name);

        _vm.ResetName(b);
        Assert.False(b.HasCustomName);
        Assert.Equal(b.DefaultName, b.Name);
        Assert.Null(_client.Config.GetFan(b.Key).Name);

        _vm.UndoCommand.Execute(null);
        Assert.Equal("Topo traseira", b.Name);

        // Ctrl+R: só com exatamente um cartão selecionado
        _vm.SelectAllCommand.Execute(null);
        Assert.False(_vm.BeginRenameSelected());
        _vm.HandleFanClick(a, ctrl: false, shift: false);
        Assert.True(_vm.BeginRenameSelected());
        Assert.True(a.IsEditingName);
    }

    [Fact]
    public void Rescan_reconnects_hub_and_reports_found_devices()
    {
        int connects = _f.Hw.Hub.ConnectCalls;
        _vm.RescanDevicesCommand.Execute(null);
        Assert.True(_vm.IsRescanning);
        Assert.Equal("Procurando...", _vm.RescanLabel);
        WaitFor(() => !_vm.IsRescanning, "fim da busca");
        Assert.True(_f.Hw.Hub.ConnectCalls > connects, "hub reaberto para reler a lista de dispositivos");
        Assert.Contains("11 dispositivo(s) encontrado(s)", _vm.ToastText);
        Assert.Equal(11, _vm.Fans.Count);
    }

    [Fact]
    public void Dashboard_detail_shows_history_stats_and_rows()
    {
        WaitFor(() => _vm.Snapshot.SensorsReady && _vm.Snapshot.CpuTemp != null, "sensores");
        _vm.OpenDetail("cpu");
        Assert.True(_vm.IsDetailOpen);
        Assert.Equal("Processador", _vm.DetailTitle);
        Assert.Equal("cpu.temp", _vm.DetailSeriesKey);
        WaitFor(() => _vm.DetailHistory is { Length: >= 2 }, "histórico do gráfico");
        Assert.EndsWith("°C", _vm.DetailMax);
        Assert.Contains(_vm.DetailRows, r => r.Label == "Clock médio" && r.Value.Contains("GHz"));

        _vm.SetDetailSeriesCommand.Execute("cpu.power");
        Assert.EndsWith("W", _vm.DetailNow);

        _vm.OpenDetail("fans");
        Assert.Equal(10, _vm.DetailFans.Count);
        _vm.DetailActionCommand.Execute(null);
        Assert.False(_vm.IsDetailOpen);
        Assert.Equal(1, _vm.SelectedPage);

        _vm.OpenDetail("xyz");
        Assert.False(_vm.IsDetailOpen);
    }

    [Fact]
    public void Lcd_builder_limits_secondary_items_and_adding_media_switches_mode()
    {
        foreach (var m in new[] { LcdMetric.CpuLoad, LcdMetric.PumpRpm }) _vm.ToggleSecondaryCommand.Execute(m);
        Assert.Equal(4, _client.Config.Lcd.SecondaryMetrics.Count); // padrão GPU + líquido + 2
        _vm.ToggleSecondaryCommand.Execute(LcdMetric.Clock);
        Assert.Equal(4, _client.Config.Lcd.SecondaryMetrics.Count);
        Assert.Contains("até 4", _vm.ToastText);

        _vm.ToggleSecondaryCommand.Execute(LcdMetric.GpuTemp);
        Assert.Equal(new[] { LcdMetric.CoolantTemp, LcdMetric.CpuLoad, LcdMetric.PumpRpm }, _client.Config.Lcd.SecondaryMetrics);
        Assert.Equal(1, _vm.LcdSecondaryChoices.Single(c => c.Metric == LcdMetric.CoolantTemp).Order);

        _vm.LcdMainMetric = LcdMetric.GpuTemp;
        _vm.SetLcdRotationCommand.Execute("180");
        Assert.Equal(LcdMetric.GpuTemp, _client.Config.Lcd.MainMetric);
        Assert.Equal(180, _client.Config.Lcd.Rotation);

        _vm.AddLcdMedia(new[] { ("C:\\a.png", "a.png"), ("C:\\b.gif", "b.gif") });
        Assert.True(_vm.IsLcdMediaMode);
        Assert.Equal(2, _vm.LcdMedia.Count);
        Assert.True(_vm.LcdMedia[1].IsGif);

        _vm.MoveMediaUpCommand.Execute(_vm.LcdMedia[1]);
        Assert.Equal("C:\\b.gif", _client.Config.Lcd.Media[0].Path);
        _vm.LcdMedia[0].DurationSeconds = 30;
        Assert.Equal(30, _client.Config.Lcd.Media[0].DurationSeconds);

        _vm.RemoveMediaCommand.Execute(_vm.LcdMedia[0]);
        Assert.Single(_client.Config.Lcd.Media);
        _vm.UndoCommand.Execute(null);
        Assert.Equal(2, _client.Config.Lcd.Media.Count);
    }

    [Fact]
    public void Snapshot_reaches_interface_without_blocking()
    {
        WaitFor(() => _vm.Snapshot.HubConnected && _vm.CpuTempFormatted != Fmt.Missing, "telemetria na tela");
        Assert.Contains("RPM", _vm.AverageFanRpmFormatted);
        Assert.False(string.IsNullOrEmpty(_vm.RamDetail));
    }
}
