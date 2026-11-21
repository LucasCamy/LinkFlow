using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class CommandTransactionTests
{
    private static string PipeName() => $"LinkFlow.Transaction.{Guid.NewGuid():N}";
    private static PipeEngineClient Connect(string name) => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name)
        ?? throw new InvalidOperationException("Test pipe did not connect.");
    private static EngineCommandRequest ConfigRequest(LinkFlowEngine engine, Action<ControlConfig> edit)
    {
        var cfg = engine.GetConfig();
        long revision = cfg.Revision;
        edit(cfg);
        return new() { Kind = PipeProtocol.SetConfig, Config = cfg, ExpectedRevision = revision };
    }

    [Fact]
    public void Stale_revision_is_rejected_without_overwriting_another_origin()
    {
        using var f = new EngineFixture();
        var first = ConfigRequest(f.Engine, c => c.GlobalPercent = 61);
        var stale = ConfigRequest(f.Engine, c => c.GlobalPercent = 83);
        var accepted = f.Engine.ExecuteCommand(first);
        var conflict = f.Engine.ExecuteCommand(stale);
        Assert.Equal(CommandState.Accepted, accepted.State);
        Assert.Equal(CommandState.Conflict, conflict.State);
        Assert.Equal(61, f.Engine.GetConfig().GlobalPercent);
        Assert.Equal(accepted.Revision, conflict.Revision);
    }

    [Fact]
    public void Duplicate_id_replays_the_receipt_and_never_reapplies_the_configuration()
    {
        using var f = new EngineFixture();
        var request = ConfigRequest(f.Engine, c => c.GlobalPercent = 67);
        var first = f.Engine.ExecuteCommand(request);
        var replay = f.Engine.ExecuteCommand(request);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.Revision, replay.Revision);
        Assert.Equal(first.Revision, f.Engine.GetConfig().Revision);
        Assert.Single(f.Engine.GetCommandReceipts());
        request.Config!.GlobalPercent = 80;
        Assert.Equal(CommandState.Rejected, f.Engine.ExecuteCommand(request).State);
        Assert.Equal(67, f.Engine.GetConfig().GlobalPercent);
    }

    [Fact]
    public void Expired_and_incompatible_commands_have_no_side_effects()
    {
        using var f = new EngineFixture();
        long revision = f.Engine.GetConfig().Revision;
        var expired = ConfigRequest(f.Engine, c => c.GlobalPercent = 90);
        expired.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(CommandState.Expired, f.Engine.ExecuteCommand(expired).State);
        var incompatible = ConfigRequest(f.Engine, c => c.GlobalPercent = 90);
        incompatible.Protocol = PipeProtocol.Version + 1;
        Assert.Equal(CommandState.Rejected, f.Engine.ExecuteCommand(incompatible).State);
        Assert.Equal(revision, f.Engine.GetConfig().Revision);
    }

    [Fact]
    public void Schema_migration_preserves_profiles_and_rejects_future_schemas()
    {
        var old = new ControlConfig { SchemaVersion = 1, GlobalPercent = 63 };
        old.Profiles.Add(FanProfile.Capture("Meu perfil", old));
        old.Normalize();
        Assert.Equal(ConfigurationSchema.CurrentVersion, old.SchemaVersion);
        Assert.Equal("Meu perfil", Assert.Single(old.Profiles).Name);
        Assert.Throws<InvalidDataException>(() => new ControlConfig { SchemaVersion = ConfigurationSchema.CurrentVersion + 1 }.Normalize());
        Assert.Throws<InvalidDataException>(() => new ControlConfig { Revision = -1 }.Normalize());
        using var f = new EngineFixture();
        var request = ConfigRequest(f.Engine, c => c.SchemaVersion = 500);
        Assert.Equal(CommandState.Rejected, f.Engine.ExecuteCommand(request).State);
    }

    [Fact]
    public void Newer_device_ack_never_confirms_the_older_superseded_request()
    {
        using var f = new EngineFixture();
        var first = f.Engine.ExecuteCommand(ConfigRequest(f.Engine, c => c.Rgb.R = 13));
        var second = f.Engine.ExecuteCommand(ConfigRequest(f.Engine, c => c.Rgb.R = 91));
        foreach (var target in second.Targets) f.Engine.ReportDeviceCommandResult(target.Target, second.Revision, true);
        Assert.Equal(CommandState.Superseded, f.Engine.GetCommandReceipts().Single(r => r.Id == first.Id).State);
        var confirmed = f.Engine.GetCommandReceipts().Single(r => r.Id == second.Id);
        Assert.Equal(CommandState.Applied, confirmed.State);
        Assert.Equal(CommandConfirmation.Controller, confirmed.Confirmation);
        Assert.NotEqual(CommandConfirmation.Physical, confirmed.Confirmation);
    }

    [Fact]
    public void Acknowledgement_for_different_revision_cannot_complete_a_pending_target()
    {
        using var f = new EngineFixture();
        var request = ConfigRequest(f.Engine, c => c.Rgb.R = 14);
        var receipt = f.Engine.ExecuteCommand(request);
        foreach (var target in receipt.Targets) f.Engine.ReportDeviceCommandResult(target.Target, receipt.Revision + 1, true);
        Assert.Equal(CommandState.Accepted, f.Engine.GetCommandReceipts().Single(r => r.Id == receipt.Id).State);
    }

    [Fact]
    public void Save_failure_is_visible_independently_of_runtime_application()
    {
        using var f = new EngineFixture();
        var receipt = f.Engine.ExecuteCommand(ConfigRequest(f.Engine, c => c.ActivePreset = "Manual salvo"));
        f.Engine.ReportConfigurationPersistence(receipt.Revision, false);
        Assert.Equal(PersistenceState.Failed, f.Engine.Persistence.State);
        Assert.Equal(CommandState.Applied, f.Engine.GetCommandReceipts().Single().State);
        Assert.Equal(PersistenceState.Failed, f.Engine.GetCommandReceipts().Single().Persistence);
        Assert.NotNull(f.Engine.Persistence.Error);
        f.Engine.ReportConfigurationPersistence(receipt.Revision, true);
        Assert.Equal(PersistenceState.Saved, f.Engine.Persistence.State);
        Assert.Equal(receipt.Revision, f.Engine.Persistence.SavedRevision);
    }

    [Fact]
    public void Merge_preserves_independent_changes_and_detects_same_field_conflicts()
    {
        var baseline = new ControlConfig(); baseline.Normalize();
        var user = baseline.Clone(); user.GlobalPercent = 77;
        var service = baseline.Clone(); service.Rgb.R = 71; service.Revision = 4;
        Assert.True(ConfigurationMerge.TryApply(baseline, user, service, out var merged));
        Assert.Equal(77, merged.GlobalPercent);
        Assert.Equal(71, merged.Rgb.R);
        Assert.Equal(4, merged.Revision);
        service.GlobalPercent = 45;
        Assert.False(ConfigurationMerge.TryApply(baseline, user, service, out _));
    }

    [Fact]
    public async Task Pipe_receipts_round_trip_with_controller_and_persistence_states()
    {
        using var f = new EngineFixture();
        string name = PipeName();
        await using var server = new PipeEngineServer(f.Engine, name); server.Start();
        using var client = Connect(name); client.Start();
        client.UpdateConfig(c => c.GlobalPercent = 69);
        EngineFixture.WaitUntil(() => client.Operations.Any(r => r.Kind == PipeProtocol.SetConfig && r.State is CommandState.Accepted or CommandState.Applied));
        EngineFixture.WaitUntil(() => f.Engine.GetConfig().GlobalPercent == 69);
        var operation = client.Operations.First(r => r.Kind == PipeProtocol.SetConfig);
        f.Engine.ReportDeviceCommandResult("fans", operation.Revision, true);
        EngineFixture.WaitUntil(() => client.Operations.Any(r => r.Id == operation.Id && r.State == CommandState.Applied));
        Assert.Equal(CommandConfirmation.Controller, client.Operations.First(r => r.Id == operation.Id).Confirmation);
    }

    [Fact]
    public async Task Two_pipe_clients_cannot_silently_overwrite_a_stale_full_config()
    {
        using var f = new EngineFixture();
        string name = PipeName();
        await using var server = new PipeEngineServer(f.Engine, name); server.Start();
        using var a = Connect(name); using var b = Connect(name);
        var stale = b.Config.Clone(); stale.GlobalPercent = 88;
        a.Start(); b.Start();
        a.UpdateConfig(c => c.GlobalPercent = 64);
        EngineFixture.WaitUntil(() => f.Engine.GetConfig().GlobalPercent == 64);
        string id = b.SubmitCommand(new() { Kind = PipeProtocol.SetConfig, ExpectedRevision = stale.Revision, Config = stale });
        EngineFixture.WaitUntil(() => b.Operations.Any(r => r.Id == id && r.State == CommandState.Conflict));
        Assert.Equal(64, f.Engine.GetConfig().GlobalPercent);
    }

    [Fact]
    public async Task Offline_same_field_conflict_adopts_the_service_and_reports_conflict()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 50 });
        string name = PipeName();
        var server = new PipeEngineServer(f.Engine, name); server.Start();
        using var client = Connect(name); client.Start();
        await server.DisposeAsync();
        EngineFixture.WaitUntil(() => !client.IsConnected);
        client.UpdateConfig(c => c.GlobalPercent = 66);
        f.Engine.UpdateConfig(c => c.GlobalPercent = 43);
        await using var replacement = new PipeEngineServer(f.Engine, name); replacement.Start();
        EngineFixture.WaitUntil(() => client.Operations.Any(r => r.State == CommandState.Conflict), timeoutMs: 20000);
        Assert.Equal(43, f.Engine.GetConfig().GlobalPercent);
        Assert.Equal(43, client.Config.GlobalPercent);
    }

    [Fact]
    public async Task History_and_event_queries_round_trip_over_the_pipe()
    {
        using var f = new EngineFixture();
        string name = PipeName();
        await using var server = new PipeEngineServer(f.Engine, name); server.Start();
        using var client = Connect(name); client.Start();
        client.UpdateConfig(c => c.ActivePreset = "Histórico");
        EngineFixture.WaitUntil(() => client.Operations.Any(r => r.State == CommandState.Applied));
        var query = new HistoryQuery { FromUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ToUtc = DateTimeOffset.UtcNow.AddSeconds(1), MaxPoints = 100 };
        Assert.Equal("cpu.temp", (await client.QueryHistoryAsync(query)).MetricKey);
        Assert.NotEmpty(await client.QueryEventsAsync(new EventQuery()));
        Assert.NotEmpty(await client.QuerySessionsAsync());
        Assert.Contains("cpu.temp", await client.ExportHistoryAsync(query, false));
    }

    [Fact]
    public async Task Old_protocol_is_read_only_and_never_mutates_hardware()
    {
        string name = PipeName();
        using var fake = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = fake.WaitForConnectionAsync();
        var connecting = Task.Run(() => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name));
        await accept;
        using var writer = new StreamWriter(fake, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Hello, new PipeProtocol.HelloInfo { Protocol = 1 }));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Config, new ControlConfig()));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Snapshot, new EngineSnapshot()));
        using var client = await connecting;
        Assert.NotNull(client);
        Assert.False(client!.CanWrite);
        client.UpdateConfig(c => c.GlobalPercent = 88);
        Assert.Equal(CommandState.Rejected, Assert.Single(client.Operations).State);
        Assert.Equal(55, client.Config.GlobalPercent);
    }

    [Fact]
    public async Task Open_pipe_without_fresh_snapshots_blocks_writes_then_reconnects()
    {
        string name = PipeName();
        using var fake = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = fake.WaitForConnectionAsync();
        var connecting = Task.Run(() => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name));
        await accept;
        using var writer = new StreamWriter(fake, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Hello, new PipeProtocol.HelloInfo()));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Config, new ControlConfig()));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Snapshot, new EngineSnapshot()));
        using var client = await connecting;
        Assert.NotNull(client); client!.Start();
        Assert.True(client.CanWrite);
        EngineFixture.WaitUntil(() => !client.IsSnapshotFresh, timeoutMs: 7000, because: "snapshot vencido mesmo com pipe aberto");
        Assert.False(client.CanWrite);
        client.UpdateConfig(c => c.GlobalPercent = 90);
        Assert.Equal(CommandState.Rejected, client.Operations.First().State);
        Assert.Equal(55, client.Config.GlobalPercent);
        EngineFixture.WaitUntil(() => !client.IsConnected, timeoutMs: 5000, because: "sem heartbeat inicia reconexão");
    }

    [Fact]
    public async Task Late_accepted_receipt_never_downgrades_a_controller_confirmation()
    {
        string name = PipeName();
        using var fake = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = fake.WaitForConnectionAsync();
        var connecting = Task.Run(() => PipeEngineClient.TryConnect(TimeSpan.FromSeconds(15), name));
        await accept;
        using var writer = new StreamWriter(fake, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Hello, new PipeProtocol.HelloInfo()));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Config, new ControlConfig()));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Snapshot, new EngineSnapshot()));
        using var client = await connecting;
        Assert.NotNull(client); client!.Start();
        string id = Guid.NewGuid().ToString("N");
        var accepted = new CommandReceipt { Id = id, Kind = PipeProtocol.RefreshRgb, State = CommandState.Accepted,
            UpdatedAtUtc = DateTimeOffset.UtcNow };
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Receipt, accepted with
        { State = CommandState.Applied, Confirmation = CommandConfirmation.Controller, UpdatedAtUtc = accepted.UpdatedAtUtc.AddMilliseconds(10) }));
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.Receipt, accepted));
        EngineFixture.WaitUntil(() => client.Operations.Any(r => r.Id == id));
        Assert.Equal(CommandState.Applied, client.Operations.Single(r => r.Id == id).State);
        Assert.Equal(CommandConfirmation.Controller, client.Operations.Single(r => r.Id == id).Confirmation);
    }
}
