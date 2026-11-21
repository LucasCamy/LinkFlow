using System.IO;
using System.IO.Compression;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

public sealed class OperationalPresentationTests
{
    [Fact]
    public void An_unavailable_tachometer_is_unknown_instead_of_a_stopped_fan()
    {
        using var fixture = new EngineFixture();
        using var client = new LocalEngineClient(fixture.Engine);
        using var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher);
        var fan = new FanViewModel(vm, new FanState { Model = nameof(LinkDeviceModel.FanRxRgbSeries), Rpm = 0, RpmAvailable = false });
        Assert.Equal("—", fan.RpmFormatted);
        Assert.False(fan.IsStopped);
        Assert.False(fan.ShowStoppedWarning);
        var lcd = new FanViewModel(vm, new FanState { Model = nameof(LinkDeviceModel.PumpLcd) });
        Assert.False(lcd.CanControlSpeed);
    }

    [Fact]
    public void Diagnostic_bundle_includes_sanitized_operational_evidence()
    {
        string path = Path.Combine(Path.GetTempPath(), "LinkFlow-diagnostic-" + Guid.NewGuid().ToString("N") + ".zip");
        DiagnosticBundle.Create(path, [], null, "hardware", operationalFiles: new Dictionary<string, string>
        { ["events.json"] = "{\"event\":\"sensor-lost\"}" });
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("operational/events.json")!.Open());
        Assert.Contains("sensor-lost", reader.ReadToEnd());
    }

    [Fact]
    public void Diagnostic_bundle_rejects_paths_in_external_evidence_names()
    {
        string path = Path.Combine(Path.GetTempPath(), "LinkFlow-diagnostic-" + Guid.NewGuid().ToString("N") + ".zip");
        Assert.Throws<ArgumentException>(() => DiagnosticBundle.Create(path, [], null, "hardware", operationalFiles:
            new Dictionary<string, string> { ["../outside.txt"] = "data" }));
        Assert.False(File.Exists(path));
    }
}
