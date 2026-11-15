using System.IO;
using System.IO.Compression;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

/// <summary>Pacote de diagnóstico, faixa do modo de segurança e "Testar curva".</summary>
public sealed class DiagnosticBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "linkflow-diag-" + Guid.NewGuid().ToString("N"));

    public DiagnosticBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Logs(string name) => Directory.CreateDirectory(Path.Combine(_dir, name)).FullName;

    private static Dictionary<string, string> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void Bundle_has_logs_config_and_hardware_summary()
    {
        var service = Logs("svc");
        File.WriteAllText(Path.Combine(service, "linkflow-service-2026-11-14.log"), "12:00:00 [Info ] Hub conectado\n");
        File.WriteAllText(Path.Combine(service, "notas.txt"), "não é log");
        var old = Path.Combine(service, "linkflow-service-2026-10-01.log");
        File.WriteAllText(old, "antigo");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

        var snapshot = new EngineSnapshot
        {
            HubConnected = true, HubFirmware = "2.6.201",
            Fans = { new FanState { Channel = 13, DefaultName = "TITAN Pump", Model = "Titan", IsPump = true, Rpm = 2400, Temperature = 31.5f } }
        };
        string summary = DiagnosticBundle.DescribeHardware(snapshot, "2.3.1", "Serviço do Windows", [("Processador", "Ryzen 7", "8 núcleos")]);
        var zipPath = Path.Combine(_dir, DiagnosticBundle.SuggestedFileName(new DateTime(2026, 11, 14, 9, 5, 0)));

        var result = DiagnosticBundle.Create(zipPath, [new("compartilhado", service)], new ControlConfig(), summary);

        Assert.Equal("LinkFlow-Diagnostic-20261114-0905.zip", Path.GetFileName(result.Path));
        Assert.Equal(1, result.LogFiles);
        var entries = ReadZip(zipPath);
        Assert.Contains("logs/compartilhado/linkflow-service-2026-11-14.log", entries.Keys);
        Assert.DoesNotContain(entries.Keys, k => k.Contains("2026-10-01") || k.Contains("notas"));
        Assert.Contains("\"safety\"", entries["config.json"]);
        Assert.Contains("Firmware: 2.6.201", entries["hardware-summary.txt"]);
        Assert.Contains("Canal 13: TITAN Pump", entries["hardware-summary.txt"]);
        Assert.Contains("Processador: Ryzen 7", entries["hardware-summary.txt"]);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Bundle_reads_logs_still_open_for_writing_and_keeps_no_handles()
    {
        var logs = Logs("app");
        var path = Path.Combine(logs, "linkflow-app-2026-11-14.log");
        // Igual ao Log: o arquivo fica aberto para escrita enquanto o pacote é gerado
        using (var writer = new StreamWriter(new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true })
        {
            writer.Write("linha 1\n");
            var zip = Path.Combine(_dir, "pacote.zip");
            DiagnosticBundle.Create(zip, [new("app", logs)], null, "resumo");
            writer.Write("linha 2\n");   // o log continua gravável

            Assert.Contains("linha 1", ReadZip(zip)["logs/app/linkflow-app-2026-11-14.log"]);
            // Nenhum handle do pacote ficou aberto: o .zip pode ser apagado e o log, renomeado
            File.Delete(zip);
        }
        File.Move(path, path + ".old");
        Assert.True(File.Exists(path + ".old"));
    }

    [Fact]
    public void Same_folder_listed_twice_is_read_once()
    {
        var logs = Logs("dup");
        File.WriteAllText(Path.Combine(logs, "linkflow-app-2026-11-14.log"), "x");
        var result = DiagnosticBundle.Create(Path.Combine(_dir, "d.zip"), [new("a", logs), new("b", logs + Path.DirectorySeparatorChar)], null, "r");
        Assert.Equal(1, result.LogFiles);
    }

    [Fact]
    public void Sanitize_hides_profile_user_and_machine()
    {
        string text = """
            Mídia: C:\Users\lucas\Pictures\fundo.gif
            JSON: "C:\\Users\\lucas\\Pictures\\a.png"
            Usuário lucas no PC DESKTOP-AB12; lucasfilm e Placas não mudam.
            """;
        string clean = DiagnosticBundle.Sanitize(text, @"C:\Users\lucas", "lucas", "DESKTOP-AB12");

        Assert.Contains(@"<perfil>\Pictures\fundo.gif", clean);
        Assert.Contains(@"""<perfil>\\Pictures\\a.png""", clean);
        Assert.Contains("Usuário <usuario> no PC <computador>;", clean);
        Assert.Contains("lucasfilm", clean);
        Assert.DoesNotContain(@"Users\lucas", clean);
    }
}

public sealed class FailSafeBannerTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0);

    [Fact]
    public void Hidden_while_protection_is_idle()
    {
        var b = new FailSafeBannerViewModel();
        b.Update(new EngineSnapshot { Alerts = { new EngineAlert { Key = "cpu-temp", Severity = AlertSeverity.Warning } } }, Now);
        Assert.False(b.IsVisible);
    }

    [Fact]
    public void Shows_the_reading_that_triggered_it_and_the_forced_speed()
    {
        var b = new FailSafeBannerViewModel();
        b.Update(new EngineSnapshot
        {
            FailSafeActive = true,
            Alerts =
            {
                new EngineAlert { Key = "failsafe", Severity = AlertSeverity.Critical, Message = "CPU a 98 °C (limite 95 °C). Ventoinhas em 100%." },
                new EngineAlert { Key = "cpu-temp", Severity = AlertSeverity.Critical, Message = "CPU a 98 °C (limite 95 °C).", Since = Now }
            }
        }, Now);

        Assert.True(b.IsVisible);
        Assert.False(b.IsRecovering);
        Assert.Equal("CPU a 98 °C (limite 95 °C).", b.Reason);
        Assert.Contains("100%", b.Status);
    }

    [Fact]
    public void Pump_reason_says_for_how_long()
    {
        var b = new FailSafeBannerViewModel();
        b.Update(new EngineSnapshot
        {
            FailSafeActive = true,
            Alerts = { new EngineAlert { Key = "pump:p1", Severity = AlertSeverity.Critical, Title = "Bomba com rotação baixa",
                                         Message = "TITAN Pump: 0 RPM (mínimo 600).", Since = Now.AddSeconds(-12) } }
        }, Now);
        Assert.Equal("Bomba com rotação baixa há 12 s · TITAN Pump: 0 RPM (mínimo 600).", b.Reason);
    }

    [Fact]
    public void Recovering_keeps_the_original_reason_and_then_hides()
    {
        var b = new FailSafeBannerViewModel();
        b.Update(new EngineSnapshot { FailSafeActive = true, FailSafeRecovering = true, FailSafeReason = "GPU a 92 °C (limite 90 °C)." }, Now);
        Assert.True(b.IsVisible && b.IsRecovering);
        Assert.Equal("GPU a 92 °C (limite 90 °C).", b.Reason);
        Assert.Contains("Retomando", b.Status);

        b.Update(new EngineSnapshot(), Now);
        Assert.False(b.IsVisible);
    }

    [Fact]
    public void Watchdog_stall_is_shown_even_without_fail_safe()
    {
        var b = new FailSafeBannerViewModel();
        b.Update(new EngineSnapshot { Alerts = { new EngineAlert { Key = "engine-stalled", Severity = AlertSeverity.Critical, Message = "A fila do hub parou." } } }, Now);
        Assert.True(b.IsVisible);
        Assert.Contains("travado", b.Title);
        Assert.Contains("modo hardware", b.Status);
    }
}

public sealed class CurveTestTests
{
    private static readonly IReadOnlyList<CurvePoint> Points = FanCurveConfig.DefaultPoints();

    [Fact]
    public void Starts_from_the_current_temperature_and_follows_the_slider()
    {
        var sent = new List<float?>();
        var t = new CurveTestViewModel(sent.Add, () => Points, () => 47.4);

        t.IsActive = true;
        Assert.Equal(47, t.Temperature);
        t.Temperature = 80;
        t.Temperature = 150;   // limitado a 100 °C
        t.Renew();
        t.Stop();
        t.Renew();             // parado: não renova

        Assert.Equal(new float?[] { 47, 80, 100, 100, null }, sent);
    }

    [Fact]
    public void Preview_matches_the_curve_and_slider_alone_sends_nothing()
    {
        var sent = new List<float?>();
        var t = new CurveTestViewModel(sent.Add, () => Points);
        t.Temperature = 80;
        Assert.Equal(new FanCurveConfig().Evaluate(80), t.PreviewPercent);
        Assert.Equal("80 °C → 85%", t.Summary);
        Assert.Empty(sent);
    }

    [Fact]
    public void Engine_applies_the_simulated_temperature_and_returns_to_real_readings()
    {
        using var f = new EngineFixture(new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1f } });
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Hw.Sensors.ForcedGpuTemp = 40;
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 30, because: "curva a 40 °C = 30%");

        f.Engine.SetCurveTest(80);
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 85, because: "teste a 80 °C = 85%");
        Assert.Equal(80, f.Engine.GetSnapshot().CurveTestTemp);
        Assert.False(f.Engine.GetSnapshot().FailSafeActive);

        f.Engine.SetCurveTest(null);
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 30, because: "fim do teste volta à leitura real (sem esperar a histerese)");
        Assert.Null(f.Engine.GetSnapshot().CurveTestTemp);
    }

    [Fact]
    public void Engine_ends_an_abandoned_test_by_itself()
    {
        using var f = new EngineFixture(new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1f } },
            o => o.CurveTestTimeout = TimeSpan.FromMilliseconds(300));
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Hw.Sensors.ForcedGpuTemp = 40;
        f.Engine.SetCurveTest(85);
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 93);
        // A interface "travou": ninguém renova o pedido
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 30 && f.Engine.GetSnapshot().CurveTestTemp == null,
            because: "teste expirado");
    }

    [Fact]
    public void Real_critical_temperature_overrides_the_test()
    {
        using var f = new EngineFixture(new ControlConfig { Mode = FanControlMode.Curve, Curve = new FanCurveConfig { Smoothing = 1f } });
        f.Hw.Sensors.ForcedCpuTemp = 40;
        f.Engine.SetCurveTest(20);   // teste "frio"...
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 30);
        f.Hw.Sensors.ForcedCpuTemp = 97;   // ...mas a CPU real esquentou
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 100 && f.Engine.GetSnapshot().FailSafeActive,
            because: "o modo de segurança usa a temperatura real");
    }
}
