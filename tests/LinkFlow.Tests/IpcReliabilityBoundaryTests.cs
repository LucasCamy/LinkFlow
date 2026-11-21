using System.IO.Pipes;
using System.IO;
using System.Text;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class IpcReliabilityBoundaryTests
{
    [Fact]
    public async Task Reconnection_event_waits_for_fresh_snapshot_before_accepting_immediate_control_changes()
    {
        string name = "LinkFlow.Handshake.Test." + Guid.NewGuid().ToString("N");
        var first = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepting = first.WaitForConnectionAsync();
        var connecting = Task.Run(() => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name));
        await accepting;
        await SendAsync(first, PipeProtocol.Hello, new PipeProtocol.HelloInfo());
        await SendAsync(first, PipeProtocol.Config, new ControlConfig());
        await SendAsync(first, PipeProtocol.Snapshot, new EngineSnapshot());
        using var client = await connecting;
        Assert.NotNull(client);
        var states = new List<bool>();
        client!.ConnectionChanged += state =>
        {
            lock (states) states.Add(state);
            if (state) client.UpdateConfig(c => c.GlobalPercent = 66);
        };
        client.Start();
        first.Dispose();
        EngineFixture.WaitUntil(() => !client.IsConnected);
        using var replacement = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await replacement.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await SendAsync(replacement, PipeProtocol.Hello, new PipeProtocol.HelloInfo());
        await SendAsync(replacement, PipeProtocol.Config, new ControlConfig { Revision = 11, GlobalPercent = 77 });
        EngineFixture.WaitUntil(() => client.Config.Revision == 11);
        Assert.False(client.IsConnected);
        Assert.False(client.CanWrite);
        lock (states) Assert.Equal(new[] { false }, states);
        await SendAsync(replacement, PipeProtocol.Snapshot, new EngineSnapshot());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var pending = new MemoryStream();
        string? line = await PipeProtocol.ReadLineAsync(replacement, new byte[8192], pending, timeout.Token);
        Assert.True(PipeProtocol.TryDecode(line!, out var type, out var payload));
        Assert.Equal(PipeProtocol.Command, type);
        var request = PipeProtocol.Read<EngineCommandRequest>(payload)!;
        Assert.Equal(66, request.Config!.GlobalPercent);
        Assert.Equal(11, request.ExpectedRevision);
        Assert.True(client.CanWrite);
        lock (states) Assert.Equal(new[] { false, true }, states);
        await SendAsync(replacement, PipeProtocol.Receipt, new CommandReceipt
        { Id = request.Id, Kind = request.Kind, State = CommandState.Accepted, Revision = 12, UpdatedAtUtc = DateTimeOffset.UtcNow });
    }

    private static async Task SendAsync(Stream stream, string kind, object payload)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(PipeProtocol.Encode(kind, payload) + "\n"));
        await stream.FlushAsync();
    }

    [Fact]
    public async Task Throwing_engine_and_interface_observers_do_not_break_receipts_or_pipe_progress()
    {
        using var fixture = new EngineFixture();
        fixture.Engine.CommandUpdated += _ => throw new InvalidOperationException("observador de comando com falha");
        string name = "LinkFlow.Observer.Test." + Guid.NewGuid().ToString("N");
        await using var server = new PipeEngineServer(fixture.Engine, name); server.Start();
        using var client = PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name);
        Assert.NotNull(client);
        int receipts = 0, snapshots = 0, externalConfigs = 0;
        client!.CommandUpdated += _ => throw new InvalidOperationException("janela de comandos fechada");
        client.CommandUpdated += _ => Interlocked.Increment(ref receipts);
        client.SnapshotUpdated += _ => throw new InvalidOperationException("janela de métricas fechada");
        client.SnapshotUpdated += _ => Interlocked.Increment(ref snapshots);
        client.ConfigChanged += _ => throw new InvalidOperationException("janela de configurações fechada");
        client.ConfigChanged += _ => Interlocked.Increment(ref externalConfigs);
        client.Start();
        client.UpdateConfig(c => c.GlobalPercent = 68);
        EngineFixture.WaitUntil(() => fixture.Engine.GetConfig().GlobalPercent == 68 && receipts > 0 && snapshots > 1);
        fixture.Engine.UpdateConfig(c => c.ActivePreset = "origem externa");
        EngineFixture.WaitUntil(() => externalConfigs > 0 && client.Config.ActivePreset == "origem externa");
        Assert.True(client.IsConnected);
        Assert.True(client.CanWrite);
        Assert.DoesNotContain(client.Operations, r => r.State == CommandState.Unconfirmed);
    }

    [Fact]
    public async Task External_configuration_before_our_ack_is_notified_after_pending_edits_drain()
    {
        string name = "LinkFlow.ExternalBeforeAck.Test." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accepting = server.WaitForConnectionAsync();
        var connecting = Task.Run(() => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name));
        await accepting;
        await SendAsync(server, PipeProtocol.Hello, new PipeProtocol.HelloInfo());
        await SendAsync(server, PipeProtocol.Config, new ControlConfig { Revision = 1 });
        await SendAsync(server, PipeProtocol.Snapshot, new EngineSnapshot { ConfigRevision = 1 });
        using var client = await connecting;
        Assert.NotNull(client);
        var externalRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notified = new TaskCompletionSource<ControlConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
        client!.SnapshotUpdated += s => { if (s.ConfigRevision == 3) externalRead.TrySetResult(); };
        client.ConfigChanged += c => { if (c.ActivePreset == "origem externa") notified.TrySetResult(c); };
        client.Start();
        client.UpdateConfig(c => c.GlobalPercent = 68);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pending = new MemoryStream();
        string? line = await PipeProtocol.ReadLineAsync(server, new byte[8192], pending, timeout.Token);
        Assert.True(PipeProtocol.TryDecode(line!, out var type, out var payload));
        Assert.Equal(PipeProtocol.Command, type);
        var request = PipeProtocol.Read<EngineCommandRequest>(payload)!;
        // Outra origem já aplicou a revisão 3 enquanto a confirmação da revisão 2 ainda viaja.
        await SendAsync(server, PipeProtocol.Config, new ControlConfig { Revision = 3, GlobalPercent = 68, ActivePreset = "origem externa" });
        await SendAsync(server, PipeProtocol.Snapshot, new EngineSnapshot { ConfigRevision = 3 });
        await externalRead.Task.WaitAsync(timeout.Token);
        await SendAsync(server, PipeProtocol.Receipt, new CommandReceipt
        { Id = request.Id, Kind = request.Kind, State = CommandState.Accepted, Revision = 2, UpdatedAtUtc = DateTimeOffset.UtcNow });

        var observed = await notified.Task.WaitAsync(timeout.Token);
        Assert.Equal(3, observed.Revision);
        Assert.Equal(68, observed.GlobalPercent);
        Assert.Equal("origem externa", client.Config.ActivePreset);
        Assert.True(client.CanWrite);
    }

    [Fact]
    public void Local_command_observers_are_isolated_from_one_another()
    {
        using var fixture = new EngineFixture();
        using var client = new LocalEngineClient(fixture.Engine);
        client.CommandUpdated += _ => throw new InvalidOperationException("observador local com falha");
        int received = 0;
        client.CommandUpdated += _ => Interlocked.Increment(ref received);
        client.UpdateConfig(c => c.GlobalPercent = 73);
        Assert.True(received > 0);
        Assert.Equal(73, fixture.Engine.GetConfig().GlobalPercent);
    }

    [Fact]
    public async Task Slow_client_is_disconnected_when_reliable_receipts_exceed_the_bounded_queue()
    {
        using var fixture = new EngineFixture();
        string name = "LinkFlow.SlowReceipt.Test." + Guid.NewGuid().ToString("N");
        await using var server = new PipeEngineServer(fixture.Engine, name); server.Start();
        using var unread = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await unread.ConnectAsync(15000);
        EngineFixture.WaitUntil(() => server.ClientCount == 1);
        // Nenhuma leitura: o kernel e a fila limitada enchem; não se pode perder um ACK e fingir conexão saudável.
        for (int index = 0; index < 400; index++)
        {
            var config = fixture.Engine.GetConfig();
            long revision = config.Revision;
            config.ActivePreset = "flood-" + index;
            fixture.Engine.ExecuteCommand(new() { Kind = PipeProtocol.SetConfig, ExpectedRevision = revision, Config = config });
        }
        EngineFixture.WaitUntil(() => server.ClientCount == 0, because: "cliente lento foi desconectado em vez de perder recibos silenciosamente");
    }
}
