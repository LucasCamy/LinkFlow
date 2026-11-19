using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.Tests;

/// <summary>Textos que o engine manda por código: catálogo, tradução na interface e ida e volta pelo pipe.</summary>
public sealed class EngineTextTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private static Dictionary<string, string> Resx(string file) =>
        XDocument.Load(Path.Combine(StringResourceTests.AppSourceDir, "Resources", file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => ((string?)d.Element("value") ?? "").Replace("\r\n", "\n"));

    internal static string? English(string code) => Resx("Strings.en.resx").GetValueOrDefault("Engine_" + code);

    [Fact]
    public void Every_code_has_a_portuguese_text()
    {
        var codes = typeof(EngineTexts).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(codes.Order(), EngineTexts.Portuguese.Keys.Order());
        Assert.All(codes, code => Assert.Equal(code, typeof(EngineTexts).GetField(code)!.GetRawConstantValue()));
    }

    [Fact]
    public void Interface_has_every_code_with_the_same_portuguese_as_the_engine()
    {
        var pt = Resx("Strings.resx").Where(e => e.Key.StartsWith("Engine_", StringComparison.Ordinal))
            .ToDictionary(e => e.Key["Engine_".Length..], e => e.Value);
        Assert.Equal(EngineTexts.Portuguese.Keys.Order(), pt.Keys.Order());
        foreach (var (code, text) in EngineTexts.Portuguese) Assert.Equal(text, pt[code]);
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
        Assert.Equal("CPU at 98.3 °C (limit 95 °C). Fans at 100%.", EngineTexts.Format(failSafe, English, En));

        var coolant = EngineText.Of(EngineTexts.TempWarnTitle, EngineText.Of(EngineTexts.SensorCoolant));
        Assert.Equal("Líquido muito quente", coolant.ToPortuguese());
        Assert.Equal("Coolant running hot", EngineTexts.Format(coolant, English, En));
    }

    [Fact]
    public void Literal_arguments_are_kept_as_they_are()
    {
        var text = EngineText.Of(EngineTexts.PumpLow, "{0} Pump", 412, 600);
        Assert.Equal("{0} Pump: 412 RPM (mínimo 600). Verifique a bomba!", text.ToPortuguese());
        Assert.Equal("{0} Pump: 412 RPM (minimum 600). Check the pump!", EngineTexts.Format(text, English, En));
    }

    [Fact]
    public void Broken_or_missing_translation_falls_back_to_portuguese()
    {
        var text = EngineText.Of(EngineTexts.FanStopped, "Fan 1", 40);
        Assert.Null(EngineTexts.TryFormat(text, _ => "{0} {1} {2}", En));
        Assert.Equal("Fan 1 está em 0 RPM com 40% de potência.", EngineTexts.Format(text, _ => null, Pt));
        Assert.Null(EngineTexts.TryFormat(new EngineText { Code = "DeOutraVersao" }, _ => null, En));
        Assert.Equal("reserva", EngineTextUi.Localize(new EngineText { Code = "DeOutraVersao" }, "reserva"));
        Assert.Equal("reserva", EngineTextUi.Localize(null, "reserva"));
    }

    [Fact]
    public void Alert_texts_survive_the_pipe_and_are_translated_by_the_interface()
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
        Assert.Equal("GPU at 91.5 °C (limit 90 °C).", EngineTexts.Format(copy.MessageText!, English, En));
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
        Assert.Equal("Synced: Fans/Pump, GPU, Motherboard", EngineTexts.Format(text, English, En));
    }

    [Fact]
    public void Old_snapshot_without_codes_still_shows_the_portuguese_text()
    {
        var snapshot = JsonSerializer.Deserialize<EngineSnapshot>("""{ "hubStatus": "Conectado • FW 1 • 3 dispositivos" }""", Json.Compact)!;
        Assert.Null(snapshot.HubStatusText);
        Assert.Equal("Conectado • FW 1 • 3 dispositivos", EngineTextUi.Localize(snapshot.HubStatusText, snapshot.HubStatus));
    }
}
