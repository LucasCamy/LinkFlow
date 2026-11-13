using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

/// <summary>Widget: itens do catálogo, % ou valor, ordem, aparência salva e leitura de discos sob demanda.</summary>
public sealed class WidgetTests : IDisposable
{
    private readonly EngineFixture _f = new();
    private readonly LocalEngineClient _client;
    private readonly MainViewModel _vm;
    private readonly WidgetSettings _settings = new() { Items = WidgetSettings.DefaultItems() };
    private int _saves;
    private readonly WidgetViewModel _w;

    public WidgetTests()
    {
        _client = new LocalEngineClient(_f.Engine);
        _vm = new MainViewModel(_client, Dispatcher.CurrentDispatcher);
        WaitFor(() => _vm.Snapshot.HubConnected && _vm.Snapshot.SensorsReady, "telemetria");
        _w = new WidgetViewModel(_settings, () => _saves++, _vm);
    }

    public void Dispose() => _f.Dispose();

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

    [Fact]
    public void Default_items_show_live_values()
    {
        Assert.Equal(new[] { "cpu.temp", "gpu.temp", "coolant.temp", "fans.avg" }, _w.Items.Select(i => i.Id));
        WaitFor(() => _vm.Snapshot.CpuTemp.HasValue && _vm.Snapshot.Fans.Any(f => !f.IsPump && f.Rpm > 0),
            "amostra de temperatura da CPU e rotação das ventoinhas");
        _w.Update(_vm.Snapshot);
        Assert.EndsWith("°C", _w.Items[0].Text);
        Assert.EndsWith("RPM", _w.Items[3].Text);
        Assert.True(_w.Items[0].Ratio > 0);
    }

    [Fact]
    public void Add_toggle_percent_move_and_remove_are_saved()
    {
        WaitFor(() => _vm.Snapshot.RamUsedGb.HasValue && _vm.Snapshot.RamTotalGb.HasValue, "amostra de memória");
        _w.AddCommand.Execute("ram.usage");
        var ram = _w.Items.Last();
        Assert.Equal("ram.usage", _settings.Items.Last().Id);
        Assert.EndsWith("GB", ram.Text);
        Assert.True(ram.CanToggleDisplay);

        _w.ToggleDisplayCommand.Execute(ram);
        Assert.Equal(SensorDisplay.Percent, _settings.Items.Last().Display);
        Assert.EndsWith("%", ram.Text);

        _w.MoveUpCommand.Execute(ram);
        Assert.Equal("ram.usage", _settings.Items[^2].Id);
        Assert.Equal("ram.usage", _w.Items[^2].Id);

        _w.AddCommand.Execute("ram.usage"); // clicar de novo no chip tira
        Assert.DoesNotContain(_settings.Items, i => i.Id == "ram.usage");
        Assert.True(_saves >= 4);
    }

    [Fact]
    public void Appearance_changes_persist_and_picker_marks_added_items()
    {
        _w.Scale = 1.5;
        _w.Opacity = 0.6;
        _w.SetLayoutCommand.Execute("Grid");
        _w.SetAccentCommand.Execute("#FF0000");
        Assert.Equal(1.5, _settings.Scale);
        Assert.Equal(WidgetLayout.Grid, _settings.Layout);
        Assert.True(_w.IsGrid);
        Assert.False(_settings.UseSensorColors);
        Assert.Equal("#FF0000", _w.Items[0].Color);

        var cpu = _w.Picker.SelectMany(g => g.Items).Single(i => i.Id == "cpu.temp");
        Assert.True(cpu.IsAdded);
        Assert.Contains(_w.Picker, g => g.Name == SensorCatalog.Network);

        _w.ResetCommand.Execute(null);
        Assert.Equal(1.0, _settings.Scale);
        Assert.True(_settings.UseSensorColors);
    }

    [Fact]
    public void Disk_items_request_disk_reading_only_while_widget_is_visible()
    {
        var visible = false;
        _vm.SetWidgetDemandSource(() => visible ? _w.Demand : SensorDemand.None);
        _vm.IsViewActive = false; // janela na bandeja
        _vm.UpdateSensorDemand();
        Thread.Sleep(150);
        Assert.Equal(SensorDemand.None, _vm.CurrentSensorDemand); // bandeja + widget sem disco: nada é pedido

        _settings.Items.Add(new WidgetItemSetting { Id = "disk:0:activity" });
        visible = true;
        _vm.UpdateSensorDemand();
        Assert.Equal(SensorDemand.DiskIo, _vm.CurrentSensorDemand);
        WaitFor(() => _f.Hw.Sensors.StorageEnabled, "discos ligados pelo widget");
    }

    [Fact]
    public void Old_widget_options_are_migrated()
    {
        var ui = new UiSettings { MiniShowCpu = true, MiniShowGpu = false, MiniShowCoolant = false, MiniShowFans = false, MiniShowRam = true, MiniOpacity = 0.7 };
        var w = WidgetSettings.MigrateFrom(ui);
        Assert.Equal(new[] { "cpu.temp", "ram.usage" }, w.Items.Select(i => i.Id));
        Assert.Equal(SensorDisplay.Percent, w.Items[1].Display);
        Assert.Equal(0.7, w.Opacity);
    }
}
