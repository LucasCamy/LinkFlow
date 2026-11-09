using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public class IpcTests
{
    private static string UniquePipe() => $"LinkFlow.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task Client_receives_state_and_controls_engine()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 45 });
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();

        using var client = PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2), pipe);
        Assert.NotNull(client);
        Assert.True(client!.IsRemote);
        Assert.True(client.IsSimulated);
        Assert.Equal(45, client.Config.GlobalPercent);
        Assert.NotNull(client.Snapshot);

        var snapshots = 0;
        client.SnapshotUpdated += _ => Interlocked.Increment(ref snapshots);
        client.Start();

        client.UpdateConfig(c => c.GlobalPercent = 85);
        EngineFixture.WaitUntil(() => f.Engine.GetConfig().GlobalPercent == 85, because: "engine recebeu a configuração");
        EngineFixture.WaitUntil(() => f.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 85, because: "hub aplicou 85%");
        EngineFixture.WaitUntil(() => snapshots >= 2, because: "snapshots chegando pelo pipe");

        client.RequestLcdFrame();
        int frames = f.Hw.Lcd.Frames;
        EngineFixture.WaitUntil(() => f.Hw.Lcd.Frames > frames, because: "quadro de LCD solicitado pelo cliente");

        // "Atualizar" na interface: o serviço reabre o hub e relê os dispositivos
        int connects = f.Hw.Hub.ConnectCalls;
        client.RescanDevices();
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls > connects, because: "serviço reatualizou os dispositivos");
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Fans.Count == 11, because: "dispositivos de volta");
    }

    [Fact]
    public async Task Config_change_reaches_other_clients_but_not_sender()
    {
        using var f = new EngineFixture();
        var pipe = UniquePipe();
        await using var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();

        using var a = PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2), pipe)!;
        using var b = PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2), pipe)!;
        int aEcho = 0;
        ControlConfig? bSeen = null;
        a.ConfigChanged += _ => Interlocked.Increment(ref aEcho);
        b.ConfigChanged += c => bSeen = c;
        a.Start();
        b.Start();
        EngineFixture.WaitUntil(() => server.ClientCount == 2);

        a.UpdateConfig(c => c.Rgb.R = 123);
        EngineFixture.WaitUntil(() => bSeen?.Rgb.R == 123, because: "segundo cliente recebeu a mudança");
        Thread.Sleep(200);
        Assert.Equal(0, aEcho);
        Assert.Equal(123, b.Config.Rgb.R);
    }

    [Fact]
    public async Task Client_reconnects_when_service_restarts()
    {
        using var f = new EngineFixture();
        var pipe = UniquePipe();
        var server = new PipeEngineServer(f.Engine, pipe);
        server.Start();

        using var client = PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2), pipe)!;
        var states = new List<bool>();
        client.ConnectionChanged += s => { lock (states) states.Add(s); };
        client.Start();

        await server.DisposeAsync();
        EngineFixture.WaitUntil(() => !client.IsConnected, because: "detectou queda do serviço");

        await using var server2 = new PipeEngineServer(f.Engine, pipe);
        server2.Start();
        EngineFixture.WaitUntil(() => client.IsConnected, timeoutMs: 6000, because: "reconectou");
        lock (states) Assert.Equal(new[] { false, true }, states);

        client.UpdateConfig(c => c.GlobalPercent = 66);
        EngineFixture.WaitUntil(() => f.Engine.GetConfig().GlobalPercent == 66, because: "comandos voltam a funcionar");
    }

    [Fact]
    public void TryConnect_returns_null_without_service()
    {
        Assert.Null(PipeEngineClient.TryConnect(TimeSpan.FromMilliseconds(200), UniquePipe()));
    }

    [Fact]
    public void Protocol_roundtrip()
    {
        var line = PipeProtocol.Encode(PipeProtocol.SetConfig, new ControlConfig { GlobalPercent = 77 });
        Assert.True(PipeProtocol.TryDecode(line, out var type, out var payload));
        Assert.Equal(PipeProtocol.SetConfig, type);
        Assert.Equal(77, PipeProtocol.Read<ControlConfig>(payload)!.GlobalPercent);
        Assert.False(PipeProtocol.TryDecode("lixo{", out _, out _));
    }
}
