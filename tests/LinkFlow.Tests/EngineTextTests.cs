using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.Tests;

/// <summary>Textos que o engine manda por código: catálogo, tradução e ida e volta pelo pipe.</summary>
public sealed class EngineTextTests
{
    public static readonly CultureInfo En = EngineTexts.EnglishCulture;
    private static readonly CultureInfo Pt = EngineTexts.PortugueseCulture;
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static string CoreResources =>
        Path.Combine(Path.GetDirectoryName(StringResourceTests.AppSourceDir)!, "LinkFlow.Core", "Resources");

    private static Dictionary<string, string> Resx(string file) =>
        XDocument.Load(Path.Combine(CoreResources, file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static List<string> Codes() =>
        typeof(EngineTexts).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();

    [Fact]
    public void Every_code_has_a_text_in_both_languages_with_the_same_placeholders()
    {
        var pt = Resx("EngineStrings.resx");
        var en = Resx("EngineStrings.en.resx");
        Assert.Equal(Codes().Order(), pt.Keys.Order());
        Assert.Equal(pt.Keys.Order(), en.Keys.Order());
        foreach (var code in Codes())
        {
            Assert.Equal(code, typeof(EngineTexts).GetField(code)!.GetRawConstantValue());
            Assert.False(string.IsNullOrWhiteSpace(pt[code]), code);
            Assert.False(string.IsNullOrWhiteSpace(en[code]), code);
            Assert.True(Placeholders(pt[code]).SetEquals(Placeholders(en[code])), $"{code}: os {{n}} não batem");
        }
    }

    private static HashSet<string> Placeholders(string text) => Placeholder.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void English_comes_from_the_core_satellite_assembly()
    {
        Assert.Equal("Hub disconnected", EngineTexts.Template(EngineTexts.HubLostTitle, En));
        Assert.Equal("Hub desconectado", EngineTexts.Template(EngineTexts.HubLostTitle, Pt));
        Assert.Equal(En, EngineTexts.CultureFor("en"));
        Assert.Equal(Pt, EngineTexts.CultureFor("pt-BR"));
        Assert.Equal(Pt, EngineTexts.CultureFor(null));
    }

    [Fact]
    public void Of_rejects_unknown_codes_and_arguments()
    {
        Assert.Throws<ArgumentException>(() => EngineText.Of("NaoExiste"));
        Assert.Throws<ArgumentException>(() => EngineText.Of(EngineTexts.Conflict, DateTime.Now));
    }

    [Fact]
    public void Nested_texts_and_numbers_follow_the_language()
    {
        var cpu = EngineText.Of(EngineTexts.SensorCpu);
        var critical = EngineText.Of(EngineTexts.TempCritical, cpu, 98.3f, 95f);
        var failSafe = EngineText.Of(EngineTexts.FailSafeOn, critical);

        Assert.Equal("CPU a 98,3 °C (limite 95 °C). Ventoinhas em 100%.", failSafe.ToPortuguese());
        Assert.Equal("CPU at 98.3 °C (limit 95 °C). Fans at 100%.", EngineTexts.Format(failSafe, En));

        var coolant = EngineText.Of(EngineTexts.TempWarnTitle, EngineText.Of(EngineTexts.SensorCoolant));
        Assert.Equal("Líquido muito quente", coolant.ToPortuguese());
        Assert.Equal("Coolant running hot", EngineTexts.Format(coolant, En));
    }

    [Fact]
    public void Literal_arguments_are_kept_as_they_are()
    {
        var text = EngineText.Of(EngineTexts.PumpLow, "{0} Pump", 412, 600);
        Assert.Equal("{0} Pump: 412 RPM (mínimo 600). Verifique a bomba!", text.ToPortuguese());
        Assert.Equal("{0} Pump: 412 RPM (minimum 600). Check the pump!", EngineTexts.Format(text, En));
    }

    [Fact]
    public void Unknown_or_broken_text_falls_back_to_the_portuguese_received()
    {
        var newer = new EngineText { Code = "DeOutraVersao" };
        Assert.Null(EngineTexts.TryFormat(newer, En));
        Assert.Equal("reserva", EngineTextUi.Localize(newer, "reserva"));
        Assert.Equal("reserva", EngineTextUi.Localize(null, "reserva"));

        // Parâmetro a menos do que o texto pede (ex.: serviço de outra versão): não estoura, usa a reserva
        var missingArg = new EngineText { Code = EngineTexts.FanStopped, Args = [new EngineTextArg { S = "Fan 1" }] };
        Assert.Null(EngineTexts.TryFormat(missingArg, En));
        Assert.Equal("reserva", EngineTextUi.Localize(missingArg, "reserva"));
    }

    [Fact]
    public void Alert_texts_survive_the_pipe_and_are_translated()
    {
        var title = EngineText.Of(EngineTexts.TempCriticalTitle, EngineText.Of(EngineTexts.SensorGpu));
        var message = EngineText.Of(EngineTexts.TempCritical, EngineText.Of(EngineTexts.SensorGpu), 91.5, 90);
        var alert = new EngineAlert
        {
            Key = "gpu-temp", Severity = AlertSeverity.Critical, Title = title.ToPortuguese(), Message = message.ToPortuguese(),
            TitleText = title, MessageText = message
        };

        var copy = JsonSerializer.Deserialize<EngineAlert>(JsonSerializer.Serialize(alert, Json.Compact), Json.Compact)!;

        Assert.Equal("GPU em temperatura crítica", copy.LocalizedTitle());
        Assert.Equal("GPU a 91,5 °C (limite 90 °C).", copy.LocalizedMessage());
        Assert.Equal("GPU at 91.5 °C (limit 90 °C).", EngineTexts.Format(copy.MessageText!, En));
    }

    [Fact]
    public void Rgb_status_lists_the_synced_devices_in_each_language()
    {
        using var f = new EngineFixture();
        f.Engine.UpdateConfig(c =>
        {
            c.Rgb.SyncHub = true;
            c.Rgb.SyncGpu = true;
            c.Rgb.SyncMotherboard = true;
            c.Rgb.SyncRam = false;
            c.Rgb.SyncPeripherals = false;
            c.Rgb.SyncLcd = false;
        });
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().RgbStatus == "Sincronizado: Fans/Bomba, GPU, Placa-mãe",
            because: "três dispositivos sincronizados");

        var text = f.Engine.GetSnapshot().RgbStatusText!;
        Assert.Equal("Synced: Fans/Pump, GPU, Motherboard", EngineTexts.Format(text, En));
    }

    [Fact]
    public void Lcd_metric_labels_and_numbers_follow_the_language()
    {
        var snap = new EngineSnapshot { CpuLoad = 37.6f, CoolantTemp = 31.26f, Fans = { new FanState { IsPump = true, Rpm = 2104 } } };

        var pt = LcdComposer.Metric(LcdMetric.PumpRpm, snap, Pt);
        var en = LcdComposer.Metric(LcdMetric.PumpRpm, snap, En);
        Assert.Equal(("2.104", "BOMBA RPM"), (pt.Value, pt.Label));
        Assert.Equal(("2,104", "PUMP RPM"), (en.Value, en.Label));
        Assert.Equal("USO CPU", LcdComposer.Metric(LcdMetric.CpuLoad, snap, Pt).Label);
        var coolant = LcdComposer.Metric(LcdMetric.CoolantTemp, snap, En);
        Assert.Equal(("31.3°", "COOLANT"), (coolant.Value, coolant.Label));
        Assert.Equal("12 MB/s", SensorCatalog.Rate(12.3e6, En));
        Assert.Equal("1.5 MB/s", SensorCatalog.Rate(1.5e6, En));
        Assert.Equal("1,5 MB/s", SensorCatalog.Rate(1.5e6));
    }

    [Fact]
    public void Lcd_frames_use_the_language_saved_by_the_interface()
    {
        using var composer = new LcdComposer();
        composer.Compose(new LcdConfig { Mode = LcdMode.Metrics, Language = "en" }, new EngineSnapshot(), new RgbConfig());
        Assert.Equal(EngineTexts.LcdShowingMetrics, composer.Status.Code);
        Assert.Equal("Metrics", EngineTexts.Format(composer.Status, En));

        Assert.Equal("en", Json.Clone(new ControlConfig { Lcd = { Language = "en" } }).Lcd.Language);
        Assert.Equal("en", new ControlConfig { Lcd = { Language = "en" } }.Clone().Lcd.Language);
    }

    [Fact]
    public void Old_snapshot_without_codes_still_shows_the_portuguese_text()
    {
        var snapshot = JsonSerializer.Deserialize<EngineSnapshot>("""{ "hubStatus": "Conectado • FW 1 • 3 dispositivos" }""", Json.Compact)!;
        Assert.Null(snapshot.HubStatusText);
        Assert.Equal("Conectado • FW 1 • 3 dispositivos", EngineTextUi.Localize(snapshot.HubStatusText, snapshot.HubStatus));
    }
}
