namespace LinkFlow.Tests;

/// <summary>Calibração das ventoinhas: análise, rotina no engine, piso calibrado e alerta de desgaste.</summary>
public class CalibrationTests
{
    private static readonly DateTime Date = new(2026, 11, 15, 10, 0, 0);

    private static List<CalibrationPoint> Sweep(params (byte Percent, int Rpm)[] points) =>
        points.Select(p => new CalibrationPoint(p.Percent, p.Rpm)).ToList();

    [Fact]
    public void Fan_that_never_stalls_keeps_the_linkflow_minimum()
    {
        var cal = FanCalibration.Analyze(Sweep((100, 2150), (60, 1390), (20, 630)), null, Date);
        Assert.Equal(FanLimits.MinPercent, cal.MinPercent);
        Assert.Equal(2150, cal.MaxRpm);
        Assert.Null(cal.StartPercent);
        Assert.Equal(new byte[] { 20, 60, 100 }, cal.Points.Select(p => p.Percent));
    }

    [Fact]
    public void Stall_sets_the_floor_above_the_stall_and_the_start_power()
    {
        // Girou até 30%, parou em 25% e só voltou a girar em 40%: piso 40% + margem
        var cal = FanCalibration.Analyze(Sweep((100, 2150), (30, 820), (25, 0), (20, 0)), startPercent: 40, Date);
        Assert.Equal(45, cal.MinPercent);
        Assert.Equal((byte)40, cal.StartPercent);

        // Voltou a girar logo acima da parada: piso pela menor potência estável
        cal = FanCalibration.Analyze(Sweep((100, 2150), (30, 820), (25, 0)), startPercent: 25, Date);
        Assert.Equal(35, cal.MinPercent);
    }

    [Fact]
    public void Expected_rpm_interpolates_between_measurements()
    {
        var cal = FanCalibration.Analyze(Sweep((100, 2000), (50, 1000), (30, 0)), 35, Date);
        Assert.Equal(1500, cal.ExpectedRpm(75));
        Assert.Equal(2000, cal.ExpectedRpm(100));
        Assert.Null(cal.ExpectedRpm(40));   // abaixo da menor medição que girou: sem referência
    }

    // ------------------------------------------------------------------ Engine

    private static EngineFixture Fixture(ControlConfig? config = null) => new(config ?? new ControlConfig { GlobalPercent = 55 }, o =>
    {
        o.CalibrationSettle = TimeSpan.FromMilliseconds(60);
        o.CalibrationSettleMax = TimeSpan.FromSeconds(2);
        o.FanWearReadsBeforeAlert = 3;
        o.FanWearSteadyTime = TimeSpan.FromMilliseconds(150);
    });

    private static CalibrationStatus WaitForOutcome(EngineFixture f, int timeoutMs = 20000)
    {
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Calibration is { Running: false, Outcome: not CalibrationOutcome.None },
            timeoutMs, "fim da calibração");
        return f.Engine.GetSnapshot().Calibration!;
    }

    private static byte Speed(EngineFixture f, int ch) => f.Hw.Hub.LastSpeeds.GetValueOrDefault(ch);

    [Fact]
    public void Calibration_measures_every_fan_finds_stalls_and_restores_the_profile()
    {
        using var f = Fixture();
        f.Hw.Hub.StallBelowPercent[3] = 30;   // para abaixo de 30%...
        f.Hw.Hub.StartPercent[3] = 40;        // ...e só volta a girar em 40%
        f.Hw.Hub.RpmScale[5] = 0.6;           // gasta: 40% abaixo das outras
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(x => x.Rpm > 0) && Speed(f, 1) == 55);

        Assert.True(f.Engine.StartCalibration());
        Assert.False(f.Engine.StartCalibration());   // uma por vez
        var status = WaitForOutcome(f);

        Assert.Equal(CalibrationOutcome.Completed, status.Outcome);
        var cfg = f.Engine.GetConfig();
        var normal = cfg.Fans["SIM-RX-01"].Calibration!;
        Assert.Equal(FanLimits.MinPercent, normal.MinPercent);
        Assert.InRange(normal.MaxRpm, 2050, 2250);
        Assert.Equal(CalibrationSweepLength, normal.Points.Count);

        var stalled = cfg.Fans["SIM-RX-03"].Calibration!;
        Assert.Equal((byte)40, stalled.StartPercent);
        Assert.Equal(45, stalled.MinPercent);
        Assert.Contains(status.Results, r => r.Key == "SIM-RX-03" && r.Note!.Contains("40%") && !r.Warning);
        Assert.Contains(status.Results, r => r.Key == "SIM-RX-05" && r.Warning);
        Assert.False(cfg.Fans.TryGetValue("SIM-TITAN", out var pump) && pump.Calibration != null, "a bomba fica de fora");

        // De volta ao perfil (55%), sem alertas de parada pelo que a calibração fez
        EngineFixture.WaitUntil(() => Speed(f, 1) == 55 && Speed(f, 3) == 55, because: "perfil restaurado");
        Assert.DoesNotContain(f.Engine.GetSnapshot().Alerts, a => a.Key.StartsWith("fan:"));
    }

    private static readonly int CalibrationSweepLength = 10;

    [Fact]
    public void Calibrated_floor_keeps_a_stalling_fan_spinning()
    {
        var config = new ControlConfig { GlobalPercent = 25 };
        config.EditFan("SIM-RX-02").Calibration = FanCalibration.Analyze(Sweep((100, 2150), (40, 1010), (30, 0)), 40, Date);
        using var f = Fixture(config);
        EngineFixture.WaitUntil(() => Speed(f, 1) == 25 && Speed(f, 2) == 45, because: "piso de 45% só na ventoinha calibrada");
    }

    [Fact]
    public void Cancel_restores_the_fans_and_changes_nothing()
    {
        using var f = Fixture();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(x => x.Rpm > 0) && Speed(f, 1) == 55);
        Assert.True(f.Engine.StartCalibration());
        EngineFixture.WaitUntil(() => Speed(f, 1) == 100, because: "primeiro degrau da varredura");

        f.Engine.CancelCalibration();
        var status = WaitForOutcome(f);
        Assert.Equal(CalibrationOutcome.Cancelled, status.Outcome);
        Assert.All(f.Engine.GetConfig().Fans.Values, o => Assert.Null(o.Calibration));
        EngineFixture.WaitUntil(() => Speed(f, 1) == 55, because: "perfil restaurado");
    }

    [Fact]
    public void Heat_during_calibration_aborts_it()
    {
        using var f = Fixture();
        f.Hw.Sensors.ForcedCpuTemp = 50;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(x => x.Rpm > 0) && Speed(f, 1) == 55);
        Assert.True(f.Engine.StartCalibration());
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Calibration?.Percent is <= 80, because: "varredura em andamento");

        f.Hw.Sensors.ForcedCpuTemp = 91;   // acima do alerta (90 °C), abaixo do crítico
        var status = WaitForOutcome(f);
        Assert.Equal(CalibrationOutcome.Aborted, status.Outcome);
        Assert.Contains("CPU", status.Message);
        Assert.All(f.Engine.GetConfig().Fans.Values, o => Assert.Null(o.Calibration));
        EngineFixture.WaitUntil(() => Speed(f, 1) == 55, because: "perfil restaurado");
    }

    [Fact]
    public void Calibration_is_refused_during_fail_safe()
    {
        using var f = Fixture();
        f.Hw.Sensors.ForcedCpuTemp = 97;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().FailSafeActive);
        Assert.False(f.Engine.StartCalibration());
        Assert.Contains("segurança", f.Engine.GetSnapshot().Calibration!.Message);
    }

    [Fact]
    public void Fan_spinning_well_below_its_calibration_raises_a_wear_warning()
    {
        var config = new ControlConfig { GlobalPercent = 60 };
        // Referência de uma ventoinha saudável do simulador (250 + 19 RPM por %)
        config.EditFan("SIM-RX-04").Calibration = FanCalibration.Analyze(
            Sweep((100, 2150), (60, 1390), (20, 630)), null, Date);
        using var f = Fixture(config);
        EngineFixture.WaitUntil(() => Speed(f, 4) == 60);
        Thread.Sleep(400);
        Assert.DoesNotContain(f.Engine.GetSnapshot().Alerts, a => a.Key == "fan-wear:SIM-RX-04");

        f.Hw.Hub.RpmScale[4] = 0.6;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.Any(a => a.Key == "fan-wear:SIM-RX-04"), because: "alerta de desgaste");
        Assert.Contains("esperado cerca de 1390", f.Engine.GetSnapshot().Alerts.First(a => a.Key == "fan-wear:SIM-RX-04").Message);

        f.Hw.Hub.RpmScale.Remove(4);   // limpou a poeira
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.All(a => a.Key != "fan-wear:SIM-RX-04"), because: "alerta some");
    }
}

/// <summary>Assistente de calibração (view model) sobre o engine simulado.</summary>
public class CalibrationViewModelTests
{
    private static EngineFixture Fixture() => new(new ControlConfig { GlobalPercent = 55 }, o =>
    {
        o.CalibrationSettle = TimeSpan.FromMilliseconds(60);
        o.CalibrationSettleMax = TimeSpan.FromSeconds(2);
    });

    private static void Follow(LinkFlow.App.ViewModels.CalibrationViewModel vm, EngineFixture f, Func<bool> until, string because)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!until())
        {
            if (sw.ElapsedMilliseconds > 20000) Assert.Fail($"Tempo esgotado: {because}");
            vm.Update(f.Engine.GetSnapshot());
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void Wizard_goes_from_intro_to_results()
    {
        using var f = Fixture();
        f.Hw.Hub.StallBelowPercent[2] = 30;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(x => x.Rpm > 0));
        var client = new LocalEngineClient(f.Engine);
        var vm = new LinkFlow.App.ViewModels.CalibrationViewModel(client, f.Engine.GetSnapshot());

        Assert.True(vm.IsIntro);
        Assert.Equal(10, vm.FanCount);
        Assert.Contains("10 ventoinhas", vm.FanCountText);
        Assert.Null(vm.LastCalibration);

        vm.Start();
        Assert.True(vm.IsRunning);
        Follow(vm, f, () => vm.IsDone, "resultado da calibração");

        Assert.True(vm.Succeeded);
        Assert.Equal(10, vm.Results.Count);
        var stalled = vm.Results.Single(r => r.Name == "RX Fan #2");
        Assert.Contains("mínimo 35%", stalled.Detail);
        Assert.NotNull(stalled.Note);
        Assert.StartsWith("Última calibração:", vm.LastCalibration);
    }

    [Fact]
    public void Old_result_is_not_shown_as_the_new_one()
    {
        using var f = Fixture();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(x => x.Rpm > 0));
        var client = new LocalEngineClient(f.Engine);
        var vm = new LinkFlow.App.ViewModels.CalibrationViewModel(client, f.Engine.GetSnapshot());

        // Outra janela calibra e cancela enquanto este assistente está na introdução
        Assert.True(f.Engine.StartCalibration());
        f.Engine.CancelCalibration();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Calibration is { Outcome: CalibrationOutcome.Cancelled });
        vm.Update(f.Engine.GetSnapshot());
        Assert.True(vm.IsIntro);

        vm.Start();
        vm.Update(f.Engine.GetSnapshot());   // antes do engine processar: ainda não terminou
        Assert.True(vm.IsRunning);
        vm.Cancel();
        Follow(vm, f, () => vm.IsDone, "cancelamento");
        Assert.Equal(CalibrationOutcome.Cancelled, vm.Outcome);
        Assert.False(vm.HasResults);
    }

    [Fact]
    public void Refusal_is_reported()
    {
        using var f = Fixture();
        f.Hw.Sensors.ForcedCpuTemp = 97;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().FailSafeActive);
        var vm = new LinkFlow.App.ViewModels.CalibrationViewModel(new LocalEngineClient(f.Engine), f.Engine.GetSnapshot());
        vm.Start();
        vm.Update(f.Engine.GetSnapshot());
        Assert.True(vm.IsDone);
        Assert.Equal(CalibrationOutcome.Aborted, vm.Outcome);
        Assert.Contains("segurança", vm.Message);
    }
}

/// <summary>Tela LCD da bomba e calibração com o PC já quente.</summary>
public class PumpLcdAndHotStartTests
{
    private static readonly ConnectedSubDevice PumpLcd = new()
    {
        Channel = 12, DeviceId = "SIM-PUMP-LCD", Model = LinkDeviceModel.PumpLcd, Name = "Pump LCD #12"
    };

    private static EngineFixture Fixture() => new(new ControlConfig { GlobalPercent = 55 }, o =>
    {
        o.SubDeviceCheckInterval = TimeSpan.FromMilliseconds(50);
        o.CalibrationSettle = TimeSpan.FromMilliseconds(60);
        o.CalibrationSettleMax = TimeSpan.FromSeconds(2);
    });

    [Fact]
    public void Pump_lcd_is_not_a_stopped_fan_and_is_not_calibrated()
    {
        using var f = Fixture();
        f.Hw.Hub.PlugDevice(PumpLcd);
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(x => x.Key == "SIM-PUMP-LCD"), because: "LCD na lista");
        Thread.Sleep(600);   // várias leituras com 0 RPM

        var snapshot = f.Engine.GetSnapshot();
        Assert.Equal(nameof(LinkDeviceModel.PumpLcd), snapshot.Fans.Single(x => x.Key == "SIM-PUMP-LCD").Model);
        Assert.DoesNotContain(snapshot.Alerts, a => a.Key == "fan:SIM-PUMP-LCD");

        Assert.True(f.Engine.StartCalibration());
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Calibration is { Outcome: CalibrationOutcome.Completed }, 20000, "calibração");
        Assert.DoesNotContain(f.Engine.GetSnapshot().Calibration!.Results, r => r.Key == "SIM-PUMP-LCD");
        Assert.False(f.Engine.GetConfig().Fans.TryGetValue("SIM-PUMP-LCD", out var lcd) && lcd.Calibration != null);
    }

    [Fact]
    public void Calibration_is_refused_upfront_when_the_coolant_is_already_hot()
    {
        using var f = Fixture();
        f.Hw.Hub.CoolantTemp = 46.5f;   // acima do alerta (45 °C), abaixo do crítico (50 °C)
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().CoolantTemp >= 46, because: "líquido quente");

        Assert.False(f.Engine.StartCalibration());
        var status = f.Engine.GetSnapshot().Calibration!;
        Assert.Equal(CalibrationOutcome.Aborted, status.Outcome);
        Assert.Matches(@"O líquido está a 46,\d °C, acima do alerta \(45 °C\)\.", status.Message);   // o simulador soma até 0,2 °C
        Assert.Contains("PC ocioso", status.Message);
        Assert.Equal(55, f.Hw.Hub.LastSpeeds.GetValueOrDefault(1));   // nenhuma ventoinha mexida
    }
}
