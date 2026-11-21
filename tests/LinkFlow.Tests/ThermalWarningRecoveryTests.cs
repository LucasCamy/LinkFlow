using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

namespace LinkFlow.Tests;

public sealed class ThermalWarningRecoveryTests
{
    // Chamadas isoladas mantêm as fronteiras exatas: o hub simulado adiciona ruído à temperatura do líquido.
    // Não inicia engine, filas, pipe ou hardware; a integração com as filas simuladas também é coberta abaixo.
    private static readonly MethodInfo TemperatureAlert = typeof(LinkFlowEngine).GetMethod("TempAlert", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo StateLock = typeof(LinkFlowEngine).GetField("_stateLock", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static LinkFlowEngine CreateUnstarted() => new(new SimulatedHardwareFactory(), new EngineOptions
    { ConfigPath = null, History = new OperationalHistoryOptions { DirectoryPath = null } }, new ControlConfig());

    private static void Read(LinkFlowEngine engine, string key, string label, float? value, float warning = 45, float critical = 55)
    {
        lock (StateLock.GetValue(engine)!) TemperatureAlert.Invoke(engine, [key, label, value, warning, critical]);
    }
    private static EngineAlert? Alert(LinkFlowEngine engine, string key = "coolant-temp") => engine.GetSnapshot().Alerts.SingleOrDefault(a => a.Key == key);

    [Theory]
    [InlineData("coolant-temp", EngineTexts.SensorCoolant, 45f, 55f)]
    [InlineData("cpu-temp", EngineTexts.SensorCpu, 90f, 95f)]
    [InlineData("gpu-temp", EngineTexts.SensorGpu, 83f, 88f)]
    public async Task Warning_updates_current_reading_through_the_exact_three_degree_band_without_realerting(
        string key, string label, float warning, float critical)
    {
        using var engine = CreateUnstarted();
        var delivered = new ConcurrentQueue<EngineAlert>();
        engine.AlertRaised += alert => { if (alert.Key == key) delivered.Enqueue(alert); };
        Read(engine, key, label, warning, warning, critical);
        var opened = Assert.IsType<EngineAlert>(Alert(engine, key));
        Assert.Equal(AlertSeverity.Warning, opened.Severity);
        Assert.Equal(EngineTexts.TempWarn, opened.MessageText!.Code);
        EngineFixture.WaitUntil(() => delivered.Count >= 1);

        Read(engine, key, label, warning - 1, warning, critical); // 45 -> 44 para o líquido.
        var recovering = Assert.IsType<EngineAlert>(Alert(engine, key));
        Assert.Equal(EngineTexts.TempWarnRecoveringTitle, recovering.TitleText!.Code);
        Assert.Equal(EngineTexts.TempWarnRecovering, recovering.MessageText!.Code);
        Assert.Equal(warning - 1, recovering.MessageText.Args[1].N!.Value);
        Assert.Equal(warning, recovering.MessageText.Args[2].N!.Value);
        Assert.Equal(warning - 3, recovering.MessageText.Args[3].N!.Value);
        Assert.Equal(opened.Since, recovering.Since);
        Assert.Equal(AlertSeverity.Warning, recovering.Severity);

        for (int sample = 0; sample < 20; sample++) Read(engine, key, label, warning - 2, warning, critical);
        Read(engine, key, label, warning - 3, warning, critical); // Exatamente 42 não limpa o aviso45.
        var atBoundary = Assert.IsType<EngineAlert>(Alert(engine, key));
        Assert.Equal(EngineTexts.TempWarnRecovering, atBoundary.MessageText!.Code);
        Assert.Equal(warning - 3, atBoundary.MessageText.Args[1].N!.Value);
        Assert.Equal(opened.Since, atBoundary.Since);
        Read(engine, key, label, warning - 3.1f, warning, critical); // 41,9 limpa.
        Assert.Null(Alert(engine, key));
        await Task.Delay(150); // AlertRaised é entregue de forma assíncrona pelo contrato do engine.
        Assert.Single(delivered);
    }

    [Theory]
    [InlineData(42f)]
    [InlineData(44f)]
    [InlineData(44.9f)]
    public void Starting_in_the_recovery_band_does_not_create_a_warning(float temperature)
    {
        using var engine = CreateUnstarted();
        for (int sample = 0; sample < 10; sample++) Read(engine, "coolant-temp", EngineTexts.SensorCoolant, temperature);
        Assert.Null(Alert(engine));
    }

    [Fact]
    public async Task Returning_above_warning_and_escalating_to_critical_keep_original_since_and_notification_rules()
    {
        using var engine = CreateUnstarted();
        var delivered = new ConcurrentQueue<EngineAlert>();
        engine.AlertRaised += alert => { if (alert.Key == "coolant-temp") delivered.Enqueue(alert); };
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 45);
        var opened = Assert.IsType<EngineAlert>(Alert(engine));
        EngineFixture.WaitUntil(() => delivered.Count >= 1);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 44);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 46);
        Assert.Equal(EngineTexts.TempWarn, Alert(engine)!.MessageText!.Code);
        Assert.Equal(opened.Since, Alert(engine)!.Since);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 55);
        Assert.Equal(AlertSeverity.Critical, Alert(engine)!.Severity);
        Assert.Equal(EngineTexts.TempCritical, Alert(engine)!.MessageText!.Code);
        Assert.Equal(opened.Since, Alert(engine)!.Since);
        EngineFixture.WaitUntil(() => delivered.Count >= 2);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 56);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 50);
        Assert.Equal(AlertSeverity.Warning, Alert(engine)!.Severity);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 44);
        Assert.Equal(EngineTexts.TempWarnRecovering, Alert(engine)!.MessageText!.Code);
        Assert.Equal(opened.Since, Alert(engine)!.Since);
        await Task.Delay(150);
        Assert.Equal(2, delivered.Count);
        Assert.Single(delivered, a => a.Severity == AlertSeverity.Warning);
        Assert.Single(delivered, a => a.Severity == AlertSeverity.Critical);
    }

    [Fact]
    public void A_critical_alert_that_drops_below_warning_still_clears_instead_of_entering_warning_recovery()
    {
        using var engine = CreateUnstarted();
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 55);
        Assert.Equal(AlertSeverity.Critical, Alert(engine)!.Severity);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 44);
        Assert.Null(Alert(engine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Missing_or_invalid_temperature_still_clears_a_recovering_warning(float? temperature)
    {
        using var engine = CreateUnstarted();
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 45);
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, 44);
        Assert.NotNull(Alert(engine));
        Read(engine, "coolant-temp", EngineTexts.SensorCoolant, temperature);
        Assert.Null(Alert(engine));
    }

    [Fact]
    public void Recovery_text_localizes_current_temperature_warning_and_strict_clear_limit_and_roundtrips()
    {
        var label = EngineText.Of(EngineTexts.SensorCoolant);
        var title = EngineText.Of(EngineTexts.TempWarnRecoveringTitle, label);
        var message = EngineText.Of(EngineTexts.TempWarnRecovering, label, 44.4f, 45f, 42f);
        var copy = JsonSerializer.Deserialize<EngineText>(JsonSerializer.Serialize(message, Json.Compact), Json.Compact)!;
        Assert.Equal("Líquido: aviso em recuperação", title.ToPortuguese());
        Assert.Equal("Coolant: warning recovery", EngineTexts.Format(title, EngineTexts.EnglishCulture));
        Assert.Equal("Líquido a 44,4 °C. Aviso em 45 °C; permanece ativo até ficar abaixo de 42 °C, para evitar oscilações.", copy.ToPortuguese());
        Assert.Equal("Coolant at 44.4 °C. Warning at 45 °C; remains active until below 42 °C to avoid repeated switching.", EngineTexts.Format(copy, EngineTexts.EnglishCulture));
    }

    [Fact]
    public void A_recovery_warning_keeps_normal_control_and_the_critical_threshold_still_protects_the_simulated_hardware()
    {
        using var fixture = new EngineFixture(new ControlConfig { GlobalPercent = 35 });
        EngineFixture.WaitUntil(() => fixture.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 35);
        fixture.Hw.Sensors.ForcedCpuTemp = 90;
        EngineFixture.WaitUntil(() => Alert(fixture.Engine, "cpu-temp")?.MessageText?.Code == EngineTexts.TempWarn);
        fixture.Hw.Sensors.ForcedCpuTemp = 88;
        EngineFixture.WaitUntil(() => Alert(fixture.Engine, "cpu-temp")?.MessageText?.Code == EngineTexts.TempWarnRecovering);
        Assert.False(fixture.Engine.GetSnapshot().FailSafeActive);
        Assert.Equal(35, fixture.Hw.Hub.LastSpeeds.GetValueOrDefault(1));
        fixture.Hw.Sensors.ForcedCpuTemp = 95;
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().FailSafeActive && fixture.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 100);
        Assert.Equal(AlertSeverity.Critical, Alert(fixture.Engine, "cpu-temp")!.Severity);
    }
}
