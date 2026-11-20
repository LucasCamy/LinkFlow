using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

/// <summary>
/// Ida e volta de cada mensagem do pipe entre interface e serviço, reconexão e tolerância a mensagens inválidas
/// (de um lado e do outro). Cada teste usa um pipe com nome próprio.
/// </summary>
public class IpcRoundTripTests
{
    private static string UniquePipe() => $"LinkFlow.Test.{Guid.NewGuid():N}";

    // Mesmo prazo folgado de IpcTests: com a suíte em paralelo, aceitar a conexão pode levar segundos
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private static PipeEngineClient Connect(string pipe)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var remaining = ConnectTimeout - sw.Elapsed;
            var client = PipeEngineClient.TryConnect(remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), pipe);
            if (client != null) return client;
            if (sw.Elapsed >= ConnectTimeout) Assert.Fail($"Tempo esgotado: cliente não conectou ao pipe em {ConnectTimeout.TotalSeconds:0} s");
        }
    }

    private static byte Speed(EngineFixture f, int ch) => f.Hw.Hub.LastSpeeds.TryGetValue(ch, out var p) ? p : (byte)0;

    [Fact]
    public async Task Hello_config_and_snapshot_arrive_on_connect()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 47 });
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();

        using var client = Connect(pipe);
        // hello
        Assert.True(client.IsSimulated);
        Assert.Equal(Environment.ProcessId, client.ServiceProcessId);
        // config
        Assert.Equal(47, client.Config.GlobalPercent);
        // snapshot
        Assert.NotNull(client.Snapshot);
        Assert.True(client.Snapshot!.IsSimulated);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Engine_alert_reaches_the_client()
    {
        using var f = new EngineFixture();
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();
        using var client = Connect(pipe);
        var alerts = new List<EngineAlert>();
        client.AlertRaised += a => { lock (alerts) alerts.Add(a); };
        client.Start();

        f.Hw.Sensors.ForcedCpuTemp = 92; // acima do aviso (90 °C), abaixo do crítico
        EngineFixture.WaitUntil(() => { lock (alerts) return alerts.Any(a => a.Key == "cpu-temp"); }, because: "alerta chegou pelo pipe");
        EngineAlert alert;
        lock (alerts) alert = alerts.First(a => a.Key == "cpu-temp");
        Assert.Equal(AlertSeverity.Warning, alert.Severity);
        Assert.NotNull(alert.TitleText);
        EngineFixture.WaitUntil(() => client.Snapshot?.Alerts.Any(a => a.Key == "cpu-temp") == true,
            because: "snapshot seguinte também traz o alerta");
    }

    [Fact]
    public async Task Curve_test_starts_and_stops_through_the_pipe()
    {
        using var f = new EngineFixture(new ControlConfig { Mode = FanControlMode.Curve });
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();
        using var client = Connect(pipe);
        client.Start();

        client.SetCurveTest(72.5f);
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().CurveTestTemp == 72.5f, because: "serviço iniciou o teste de curva");
        EngineFixture.WaitUntil(() => client.Snapshot?.CurveTestTemp == 72.5f, because: "interface vê o teste no snapshot");

        client.SetCurveTest(null);
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().CurveTestTemp == null, because: "serviço encerrou o teste");
    }

    [Fact]
    public async Task Calibration_starts_and_cancels_through_the_pipe()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 55 });
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();
        using var client = Connect(pipe);
        client.Start();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Any(x => x.Rpm > 0) && Speed(f, 1) == 55);

        client.StartCalibration();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Calibration?.Running == true, because: "calibração iniciada pelo pipe");
        EngineFixture.WaitUntil(() => client.Snapshot?.Calibration?.Running == true, because: "progresso chega à interface");

        client.CancelCalibration();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Calibration?.Outcome == CalibrationOutcome.Cancelled,
            because: "calibração cancelada pelo pipe");
        EngineFixture.WaitUntil(() => Speed(f, 1) == 55, because: "perfil restaurado");
        Assert.All(f.Engine.GetConfig().Fans.Values, o => Assert.Null(o.Calibration));
    }

    [Fact]
    public async Task Resume_and_demand_reach_the_service()
    {
        using var f = new EngineFixture();
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();
        using var client = Connect(pipe);
        client.Start();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().HubConnected);

        int connects = f.Hw.Hub.ConnectCalls;
        client.NotifyResume();
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls > connects, because: "serviço reconectou o hub ao voltar da suspensão");

        client.SetSensorDemand(SensorDemand.DiskIo);
        EngineFixture.WaitUntil(() => f.Hw.Sensors.StorageEnabled, because: "serviço passou a ler os discos");
        EngineFixture.WaitUntil(() => client.Snapshot?.Disks is { Count: > 0 }, because: "discos chegam no snapshot");
    }

    [Fact]
    public async Task Change_made_while_service_is_down_survives_the_reconnection()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 50 });
        var pipe = UniquePipe();
        var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();

        using var client = Connect(pipe);
        var states = new List<bool>();
        var seenOnReconnect = new List<byte>();
        client.ConnectionChanged += s =>
        {
            lock (states)
            {
                states.Add(s);
                // A interface recarrega a configuração ao reconectar: ela já tem de ser a sincronizada
                if (s) seenOnReconnect.Add(client.Config.GlobalPercent);
            }
        };
        client.Start();

        await server.DisposeAsync();
        EngineFixture.WaitUntil(() => !client.IsConnected, because: "detectou queda do serviço");

        client.UpdateConfig(c => c.GlobalPercent = 66); // usuário mexe com o serviço fora do ar
        Assert.Equal(50, f.Engine.GetConfig().GlobalPercent);

        await using var server2 = new PipeEngineServer(f.Engine, pipe);
        server2.Start();
        EngineFixture.WaitUntil(() => { lock (states) return states.Count >= 2; }, timeoutMs: 20000, because: "reconectou");
        EngineFixture.WaitUntil(() => f.Engine.GetConfig().GlobalPercent == 66, because: "serviço recebeu a alteração feita offline");
        EngineFixture.WaitUntil(() => Speed(f, 1) == 66, because: "hub aplicou a alteração feita offline");

        // Antes a configuração antiga do serviço (montada antes de ler a fila) sobrescrevia a cópia local
        Thread.Sleep(300);
        Assert.Equal(66, client.Config.GlobalPercent);
        lock (states)
        {
            Assert.Equal(new[] { false, true }, states);
            Assert.Equal(new byte[] { 66 }, seenOnReconnect);
        }
    }

    [Fact]
    public async Task Reconnection_without_offline_changes_adopts_the_service_config()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 50 });
        var pipe = UniquePipe();
        var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();
        using var client = Connect(pipe);
        ControlConfig? changed = null;
        client.ConfigChanged += c => changed = c;
        client.Start();

        await server.DisposeAsync();
        EngineFixture.WaitUntil(() => !client.IsConnected, because: "detectou queda do serviço");
        f.Engine.UpdateConfig(c => c.GlobalPercent = 35); // mudou no serviço (ex.: outro cliente) enquanto estava fora

        await using var server2 = new PipeEngineServer(f.Engine, pipe);
        server2.Start();
        EngineFixture.WaitUntil(() => client.IsConnected && client.Config.GlobalPercent == 35, timeoutMs: 20000,
            because: "interface adotou a configuração do serviço");
        Assert.Equal((byte)35, changed?.GlobalPercent);
        Thread.Sleep(200);
        Assert.Equal(35, f.Engine.GetConfig().GlobalPercent);
    }

    [Fact]
    public async Task Server_ignores_invalid_and_unknown_messages_without_dropping_the_client()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 50 });
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();

        using var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync((int)ConnectTimeout.TotalMilliseconds);
        using var drain = new CancellationTokenSource();
        var reader = DrainAsync(raw, drain.Token); // o serviço não pode travar escrevendo para quem não lê
        var writer = new StreamWriter(raw, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        EngineFixture.WaitUntil(() => server.ClientCount == 1);

        string[] garbage =
        {
            "lixo{",
            "",
            "null",
            "[1,2,3]",
            "\"texto\"",
            "{}",
            "{\"t\":5}",
            "{\"t\":null}",
            "{\"t\":\"\"}",
            "{\"t\":\"setConfig\"}",
            "{\"t\":\"setConfig\",\"d\":\"abc\"}",
            "{\"t\":\"setConfig\",\"d\":[1]}",
            "{\"t\":\"setConfig\",\"d\":{\"globalPercent\":\"alto\"}}",
            "{\"t\":\"setConfig\",\"d\":{\"globalPercent\":70,\"safety\":null,\"profiles\":[null],\"fans\":{\"x\":null},\"groupCurves\":{\"g\":null}}}",
            "{\"t\":\"curveTest\",\"d\":\"quente\"}",
            "{\"t\":\"demand\",\"d\":{\"a\":1}}",
            "{\"t\":\"recursoDoFuturo\",\"d\":{\"x\":1}}",
            "{\"t\":\"snapshot\",\"d\":{}}"
        };
        foreach (var line in garbage) await writer.WriteLineAsync(line);

        // A configuração com campos nulos vale (com os padrões) e não deixa itens nulos para trás
        EngineFixture.WaitUntil(() => f.Engine.GetConfig().GlobalPercent == 70, because: "configuração parcial aceita");
        var cfg = f.Engine.GetConfig();
        Assert.NotNull(cfg.Safety);
        Assert.True(cfg.Safety.FailSafeEnabled);
        Assert.DoesNotContain(cfg.Profiles, p => p == null);
        Assert.Empty(cfg.Fans);
        Assert.Empty(cfg.GroupCurves);
        Assert.Null(f.Engine.GetSnapshot().CurveTestTemp);

        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.SetConfig, new ControlConfig { GlobalPercent = 77 }));
        EngineFixture.WaitUntil(() => f.Engine.GetConfig().GlobalPercent == 77, because: "mensagem válida depois do lixo funciona");
        EngineFixture.WaitUntil(() => Speed(f, 1) == 77, because: "hub aplicou");
        Assert.Equal(1, server.ClientCount);

        drain.Cancel();
        try { await reader; } catch { }
    }

    [Fact]
    public async Task Client_ignores_invalid_and_unknown_messages_from_the_service()
    {
        var pipe = UniquePipe();
        using var fake = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = fake.WaitForConnectionAsync();
        var connecting = Task.Run(() => PipeEngineClient.TryConnect(ConnectTimeout, pipe));
        await accept.WaitAsync(ConnectTimeout);
        using var drain = new CancellationTokenSource();
        var reader = DrainAsync(fake, drain.Token);
        var writer = new StreamWriter(fake, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        // Resposta inicial com lixo no meio: antes {"t":7} lançava exceção e a conexão era descartada
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Hello, new PipeProtocol.HelloInfo { Simulated = true, ProcessId = 4242 }));
        await writer.WriteLineAsync("{\"t\":7}");
        await writer.WriteLineAsync("[1,2]");
        await writer.WriteLineAsync("{\"t\":\"config\",\"d\":\"x\"}");
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Config, new ControlConfig { GlobalPercent = 33 }));
        await writer.WriteLineAsync("{\"t\":\"snapshot\",\"d\":\"x\"}");
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Snapshot, new EngineSnapshot { IsSimulated = true }));

        using var client = await connecting.WaitAsync(ConnectTimeout);
        Assert.NotNull(client);
        Assert.Equal(33, client!.Config.GlobalPercent);
        Assert.Equal(4242, client.ServiceProcessId);
        Assert.NotNull(client.Snapshot);

        var states = new List<bool>();
        ControlConfig? changed = null;
        int alerts = 0;
        client.ConnectionChanged += s => { lock (states) states.Add(s); };
        client.ConfigChanged += c => changed = c;
        client.AlertRaised += _ => Interlocked.Increment(ref alerts);
        client.Start();

        foreach (var line in new[]
                 {
                     "lixo", "{\"t\":\"config\",\"d\":[1]}", "{\"t\":\"alert\",\"d\":5}", "{\"t\":\"snapshot\",\"d\":true}",
                     "{\"t\":\"novidade\",\"d\":{}}", "{\"t\":\"hello\",\"d\":\"oi\"}"
                 })
            await writer.WriteLineAsync(line);
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Alert, new EngineAlert { Key = "teste", Severity = AlertSeverity.Warning }));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Config, new ControlConfig { GlobalPercent = 61 }));

        EngineFixture.WaitUntil(() => changed?.GlobalPercent == 61, because: "configuração válida depois do lixo chegou");
        EngineFixture.WaitUntil(() => Volatile.Read(ref alerts) == 1, because: "alerta válido chegou");
        Assert.True(client.IsConnected);
        Assert.Equal(61, client.Config.GlobalPercent);
        lock (states) Assert.Empty(states);

        drain.Cancel();
        try { await reader; } catch { }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!ct.IsCancellationRequested && await stream.ReadAsync(buffer, ct) > 0) { }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
}
