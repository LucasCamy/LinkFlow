using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

/// <summary>Limites de segurança que o engine impõe a qualquer configuração recebida.</summary>
public class SafetyLimitTests
{
    [Fact]
    public void Absurd_thresholds_from_the_pipe_are_clamped_to_the_settings_ranges()
    {
        var cfg = new ControlConfig();
        cfg.Safety.CpuCritical = 500;
        cfg.Safety.CpuWarn = 400;
        cfg.Safety.GpuCritical = -20;
        cfg.Safety.CoolantCritical = 99;
        cfg.Safety.CoolantWarn = 80;
        cfg.Safety.PumpMinRpm = 0;

        cfg.Normalize();

        Assert.Equal(105, cfg.Safety.CpuCritical);
        Assert.Equal(100, cfg.Safety.CpuWarn);
        Assert.Equal(70, cfg.Safety.GpuCritical);
        Assert.True(cfg.Safety.GpuWarn <= cfg.Safety.GpuCritical, "alerta nunca acima do crítico");
        Assert.Equal(60, cfg.Safety.CoolantCritical);
        Assert.Equal(55, cfg.Safety.CoolantWarn);
        Assert.Equal(300, cfg.Safety.PumpMinRpm);
    }

    [Fact]
    public void Defaults_and_settings_page_values_are_kept()
    {
        var cfg = new ControlConfig();
        cfg.Safety.CpuWarn = 85;
        cfg.Safety.CpuCritical = 92;
        cfg.Normalize();
        Assert.Equal(85, cfg.Safety.CpuWarn);
        Assert.Equal(92, cfg.Safety.CpuCritical);
        Assert.Equal(new SafetyConfig().CoolantCritical, cfg.Safety.CoolantCritical);
        Assert.Equal(800, cfg.Safety.PumpMinRpm);
    }

    [Fact]
    public void Disabling_fail_safe_stays_possible()
    {
        var cfg = new ControlConfig();
        cfg.Safety.FailSafeEnabled = false;
        cfg.Normalize();
        Assert.False(cfg.Safety.FailSafeEnabled);
    }
}

public class PipeResilienceTests
{
    private static string UniquePipe() => $"LinkFlow.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task Slow_client_keeps_receiving_snapshots_after_its_queue_overflows()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 });
        var name = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, name);
        server.Start();

        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000);
        EngineFixture.WaitUntil(() => server.ClientCount == 1);

        // Cliente parado (interface travada) por mais de 1 s: o pipe enche e a fila descarta as mensagens mais
        // antigas, inclusive vários snapshots (o bug travava de vez a partir do terceiro descartado)
        var flood = Stopwatch.StartNew();
        for (int i = 0; flood.Elapsed < TimeSpan.FromSeconds(1.5); i++)
        {
            byte v = (byte)(20 + i % 80);
            f.Engine.UpdateConfig(c => c.GlobalPercent = v);
            await Task.Delay(3);
        }

        // Volta a ler: depois de esvaziar o atraso, snapshots novos precisam continuar chegando
        var received = new List<(long At, string Type)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = Stopwatch.GetTimestamp();
        var buffer = new byte[16 * 1024];
        using var pending = new MemoryStream();
        try
        {
            while (true)
            {
                var line = await PipeProtocol.ReadLineAsync(pipe, buffer, pending, stop.Token);
                if (line == null) break;
                if (PipeProtocol.TryDecode(line, out var type, out _)) received.Add((Stopwatch.GetTimestamp(), type));
            }
        }
        catch (OperationCanceledException) { }

        var late = received.Count(r => r.Type == PipeProtocol.Snapshot &&
                                        Stopwatch.GetElapsedTime(started, r.At) > TimeSpan.FromSeconds(1.5));
        Assert.True(late >= 5, $"snapshots depois do atraso: {late} (total recebido: {received.Count})");
    }

    [Fact]
    public async Task Interface_rejects_a_pipe_that_does_not_belong_to_the_service()
    {
        using var f = new EngineFixture();
        var name = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, name);
        server.Start();

        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut);
        await pipe.ConnectAsync(2000);

        Assert.Equal(Environment.ProcessId, PipeIdentity.ServerProcessId(pipe));
        Assert.False(PipeIdentity.IsLinkFlowService(pipe, out var reason), "o processo de teste não é o serviço LinkFlow");
        Assert.NotNull(reason);
    }
}

public class EngineP1Tests
{
    [Fact]
    public void Running_icue_raises_a_conflict_alert_until_it_closes()
    {
        var running = new List<string> { "Corsair iCUE" };
        using var f = new EngineFixture(configure: o =>
        {
            o.ConflictProbe = () => { lock (running) return running.ToList(); };
            o.ConflictCheckInterval = TimeSpan.FromMilliseconds(50);
        });

        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.Any(a => a.Key == "conflict" && a.Message.Contains("Corsair iCUE")),
            because: "alerta de conflito com o iCUE");

        lock (running) running.Clear();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.All(a => a.Key != "conflict"), because: "iCUE fechado");
    }

    [Fact]
    public void Conflict_names_are_joined_for_the_message()
    {
        Assert.Equal("Corsair iCUE", ConflictDetector.Describe(new[] { "Corsair iCUE" }));
        Assert.Equal("Corsair iCUE e SignalRGB", ConflictDetector.Describe(new[] { "Corsair iCUE", "SignalRGB" }));
        Assert.True(ConflictDetector.KnownControllers.ContainsKey("icue"), "nomes de processo sem diferenciar maiúsculas");
    }

    [Fact]
    public void Sensors_that_stop_reporting_are_reopened()
    {
        using var f = new EngineFixture(configure: o => o.SensorLossTimeout = TimeSpan.FromMilliseconds(150));
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().CpuTemp != null, because: "sensores funcionando");
        int opens = f.Hw.Sensors.OpenCalls;

        f.Hw.Sensors.ReopenRestoresTemperatures = true;
        f.Hw.Sensors.TemperaturesLost = true;

        EngineFixture.WaitUntil(() => f.Hw.Sensors.OpenCalls > opens, because: "LibreHardwareMonitor reaberto");
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().CpuTemp != null, because: "temperaturas de volta");
    }

    [Fact]
    public void Damaged_config_recovered_from_backup_is_reported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LinkFlow-cfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "config.json");
            var store = new JsonFileStore<ControlConfig>(path);
            store.Save(new ControlConfig { GlobalPercent = 66 });
            store.Save(new ControlConfig { GlobalPercent = 67 });
            File.WriteAllText(path, "{ \"globalPer");

            using var engine = new LinkFlowEngine(new SimulatedHardwareFactory(), new EngineOptions { ConfigPath = path });

            Assert.Equal(66, engine.GetConfig().GlobalPercent);
            Assert.Contains(engine.GetSnapshot().Alerts, a => a.Key == "config-recovered" && a.Title.Contains("cópia"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Healthy_config_raises_no_recovery_alert()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LinkFlow-cfg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "config.json");
            new JsonFileStore<ControlConfig>(path).Save(new ControlConfig { GlobalPercent = 52 });
            using var engine = new LinkFlowEngine(new SimulatedHardwareFactory(), new EngineOptions { ConfigPath = path });
            Assert.DoesNotContain(engine.GetSnapshot().Alerts, a => a.Key == "config-recovered");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
