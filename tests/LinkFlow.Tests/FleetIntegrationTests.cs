using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class FleetIntegrationTests
{
    private sealed class FleetFixture : IAsyncDisposable
    {
        public EngineFixture Engine { get; } = new();
        public LocalEngineClient Local { get; }
        public FleetAgentServer Server { get; }
        public X509Certificate2 Certificate { get; }
        public string Observer { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string Controller { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public FleetPeerSettings Peer => new() { Host = "127.0.0.1", Port = Server.BoundPort, CertificateSha256 = Server.CertificateSha256 };
        public FleetFixture(bool allowControl = true)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            Certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
            Local = new(Engine.Engine);
            Server = new(Local, new FleetAgentOptions
            {
                Enabled = true, Port = 0, AllowControl = allowControl,
                AccessKeys = [new() { TokenSha256 = FleetClient.TokenHash(Observer) },
                    new() { TokenSha256 = FleetClient.TokenHash(Controller), Permissions = FleetPermission.Observe | FleetPermission.Control }]
            }, Certificate);
        }
        public async Task StartAsync()
        {
            await Server.StartAsync();
            EngineFixture.WaitUntil(() => Local.Snapshot?.HubConnected == true);
        }
        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); Certificate.Dispose(); Engine.Dispose(); }
    }

    [Fact]
    public async Task Pinned_tls_observer_reads_live_state_and_history()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var client = new FleetClient(f.Peer, f.Observer);
        var snapshot = await client.GetSnapshotAsync();
        Assert.True(snapshot.Snapshot.HubConnected);
        Assert.True(snapshot.Config.Safety.FailSafeEnabled);
        Assert.Equal(FleetPermission.Observe, snapshot.Permissions);
        Assert.Equal("cpu.temp", (await client.QueryHistoryAsync(new())).MetricKey);
        Assert.NotEmpty(await client.QueryEventsAsync(new()));
    }

    [Fact]
    public async Task Wrong_certificate_pin_fails_tls_before_sending_the_token()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var peer = f.Peer; peer.CertificateSha256 = new string('0', 64);
        var client = new FleetClient(peer, f.Observer);
        await Assert.ThrowsAsync<AuthenticationException>(() => client.GetSnapshotAsync());
    }

    [Fact]
    public async Task Wrong_token_is_rejected_without_leaking_details()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var client = new FleetClient(f.Peer, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        var error = await Assert.ThrowsAsync<FleetRequestException>(() => client.GetSnapshotAsync());
        Assert.Equal("unauthorized", error.Code);
        Assert.DoesNotContain(f.Observer, error.Message);
    }

    [Fact]
    public async Task Observer_cannot_submit_even_a_valid_revisioned_change()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var client = new FleetClient(f.Peer, f.Observer);
        var current = await client.GetSnapshotAsync();
        current.Config.GlobalPercent = 82;
        var error = await Assert.ThrowsAsync<FleetRequestException>(() => client.SubmitCommandAsync(new()
        { Kind = PipeProtocol.SetConfig, ExpectedRevision = current.Config.Revision, Config = current.Config }));
        Assert.Equal("forbidden", error.Code);
        Assert.Equal(55, f.Engine.Engine.GetConfig().GlobalPercent);
    }

    [Fact]
    public async Task Agent_control_toggle_overrides_a_control_scoped_token()
    {
        await using var f = new FleetFixture(allowControl: false); await f.StartAsync();
        var client = new FleetClient(f.Peer, f.Controller);
        Assert.Equal(FleetPermission.Observe, (await client.GetSnapshotAsync()).Permissions);
        var error = await Assert.ThrowsAsync<FleetRequestException>(() => client.SubmitCommandAsync(new() { Kind = PipeProtocol.RefreshRgb }));
        Assert.Equal("forbidden", error.Code);
    }

    [Fact]
    public async Task Controller_can_change_fan_output_with_ack_and_idempotent_replay()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var client = new FleetClient(f.Peer, f.Controller);
        var current = await client.GetSnapshotAsync();
        current.Config.GlobalPercent = 72;
        var request = new EngineCommandRequest { Kind = PipeProtocol.SetConfig, ExpectedRevision = current.Config.Revision, Config = current.Config };
        var accepted = await client.SubmitCommandAsync(request);
        Assert.True(accepted.State is CommandState.Accepted or CommandState.Applied);
        var replay = await client.SubmitCommandAsync(request);
        Assert.Equal(accepted.Revision, replay.Revision);
        Assert.Equal(accepted.Revision, f.Engine.Engine.GetConfig().Revision);
        EngineFixture.WaitUntil(() => f.Engine.Hw.Hub.LastSpeeds.GetValueOrDefault(1) == 72);
        request.Config.GlobalPercent = 93;
        Assert.Equal(CommandState.Rejected, (await client.SubmitCommandAsync(request)).State);
        Assert.Equal(72, f.Engine.Engine.GetConfig().GlobalPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_commands_cannot_disable_or_weaken_local_protection(bool changeThreshold)
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var client = new FleetClient(f.Peer, f.Controller);
        var current = await client.GetSnapshotAsync();
        if (changeThreshold) current.Config.Safety.CpuCritical = 105;
        else current.Config.Safety.FailSafeEnabled = false;
        var error = await Assert.ThrowsAsync<FleetRequestException>(() => client.SubmitCommandAsync(new()
        { Kind = PipeProtocol.SetConfig, ExpectedRevision = current.Config.Revision, Config = current.Config }));
        Assert.Equal("protection", error.Code);
        Assert.True(f.Engine.Engine.GetConfig().Safety.FailSafeEnabled);
        Assert.Equal(95, f.Engine.Engine.GetConfig().Safety.CpuCritical);
    }

    [Fact]
    public async Task Remote_stale_revision_is_reported_as_conflict()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var client = new FleetClient(f.Peer, f.Controller);
        var current = await client.GetSnapshotAsync();
        f.Engine.Engine.UpdateConfig(c => c.GlobalPercent = 64);
        current.Config.GlobalPercent = 86;
        Assert.Equal(CommandState.Conflict, (await client.SubmitCommandAsync(new()
        { Kind = PipeProtocol.SetConfig, ExpectedRevision = current.Config.Revision, Config = current.Config })).State);
        Assert.Equal(64, f.Engine.Engine.GetConfig().GlobalPercent);
    }

    [Fact]
    public async Task Remote_cannot_spoof_resume_or_simulate_curve_temperature()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        var client = new FleetClient(f.Peer, f.Controller);
        foreach (string kind in new[] { PipeProtocol.Resume, PipeProtocol.CurveTest, PipeProtocol.CalibrationStart })
        {
            var error = await Assert.ThrowsAsync<FleetRequestException>(() => client.SubmitCommandAsync(new() { Kind = kind }));
            Assert.Equal("forbidden", error.Code);
        }
        Assert.Null(f.Engine.Engine.GetSnapshot().CurveTestTemp);
    }

    [Fact]
    public async Task Local_safety_continues_after_remote_monitor_disappears()
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        await new FleetClient(f.Peer, f.Observer).GetSnapshotAsync();
        f.Engine.Hw.Sensors.ForcedCpuTemp = 100;
        EngineFixture.WaitUntil(() => f.Engine.Engine.GetSnapshot().FailSafeActive);
        EngineFixture.WaitUntil(() => f.Engine.Hw.Hub.LastSpeeds.Values.All(p => p == 100));
        Assert.True(f.Engine.Engine.GetConfig().Safety.FailSafeEnabled);
    }

    [Fact]
    public async Task Disabled_agent_and_implicit_network_binding_are_rejected()
    {
        await using var f = new FleetFixture();
        await using var disabled = new FleetAgentServer(f.Local, new FleetAgentOptions
        { AccessKeys = [new() { TokenSha256 = FleetClient.TokenHash(f.Observer) }] }, f.Certificate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.StartAsync());
        Assert.Throws<ArgumentException>(() => new FleetAgentServer(f.Local, new FleetAgentOptions
        { Enabled = true, BindAddress = "0.0.0.0", AccessKeys = [new() { TokenSha256 = FleetClient.TokenHash(f.Observer) }] }, f.Certificate));
    }

    [Fact]
    public void Peer_token_storage_uses_user_dpapi_and_detects_tampering()
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string protectedToken = FleetSecretStore.ProtectToken(token);
        Assert.NotEqual(token, protectedToken);
        Assert.DoesNotContain(token, protectedToken);
        Assert.Equal(token, FleetSecretStore.UnprotectToken(protectedToken));
        var bytes = Convert.FromBase64String(protectedToken); bytes[^1] ^= 1;
        Assert.ThrowsAny<Exception>(() => FleetSecretStore.UnprotectToken(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public async Task Explicit_provisioning_stores_only_hashes_and_machine_protected_certificate_password()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LinkFlow-Fleet-Provision-" + Guid.NewGuid().ToString("N"));
        var pairing = FleetProvisioning.Create(directory, "Teste isolado", allowControl: true);
        string config = File.ReadAllText(Path.Combine(directory, "agent.json"));
        Assert.DoesNotContain(pairing.ObserverToken, config);
        Assert.DoesNotContain(pairing.ControlToken!, config);
        Assert.Throws<IOException>(() => FleetProvisioning.Create(directory, "Duplicado"));
        var loaded = FleetProvisioning.Load(Path.Combine(directory, "agent.json"));
        Assert.NotNull(loaded);
        using var certificate = loaded.Value.Certificate;
        Assert.Equal(pairing.CertificateSha256, FleetClient.CertificatePin(certificate));
        Assert.Equal(2, loaded.Value.Options.AccessKeys.Count);
        Assert.All(loaded.Value.Options.AccessKeys, key => Assert.Equal(64, key.TokenSha256.Length));
        using var engine = new EngineFixture();
        var local = new LocalEngineClient(engine.Engine);
        loaded.Value.Options.Port = 0;
        await using var server = new FleetAgentServer(local, loaded.Value.Options, certificate);
        await server.StartAsync();
        EngineFixture.WaitUntil(() => local.Snapshot != null);
        var client = new FleetClient(new FleetPeerSettings { Host = "127.0.0.1", Port = server.BoundPort,
            CertificateSha256 = pairing.CertificateSha256 }, pairing.ObserverToken);
        Assert.Equal("Teste isolado", (await client.GetSnapshotAsync()).Name);
    }

    [Fact]
    public void Shared_hardening_keeps_fleet_private_when_running_as_administrator()
    {
        if (!SharedDataSecurity.IsPrivileged) return;
        string root = Path.Combine(Path.GetTempPath(), "LinkFlow-Fleet-Acl-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "fleet");
        FleetProvisioning.Create(directory, "ACL isolada");
        SharedDataSecurity.Harden(root);
        var security = new DirectoryInfo(directory).GetAccessControl();
        var users = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
        Assert.True(security.AreAccessRulesProtected);
        Assert.DoesNotContain(security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>(), rule => rule.IdentityReference == users &&
                rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow);
    }

    [Theory]
    [InlineData("fallback-policy")]
    [InlineData("fallback-floor")]
    [InlineData("group-policy")]
    [InlineData("stored-profiles")]
    [InlineData("calibration")]
    public async Task Remote_control_cannot_weaken_other_local_protection_settings(string change)
    {
        await using var f = new FleetFixture(); await f.StartAsync();
        if (change == "group-policy") f.Engine.Engine.UpdateConfig(c => c.GroupCurves["radiador"] = new());
        var client = new FleetClient(f.Peer, f.Controller);
        var current = await client.GetSnapshotAsync();
        switch (change)
        {
            case "fallback-policy": current.Config.Curve.FallbackPolicy = CurveFallbackPolicy.AllowAnyAvailable; break;
            case "fallback-floor": current.Config.Curve.FallbackMinPercent = 20; break;
            case "group-policy": current.Config.GroupCurves["radiador"].FallbackPolicy = CurveFallbackPolicy.AllowAnyAvailable; break;
            case "stored-profiles": current.Config.Profiles.Add(FanProfile.Capture("remoto", current.Config)); break;
            case "calibration": current.Config.EditFan("SIM-RX-01").Calibration = new FanCalibration { MinPercent = 20 }; break;
        }
        var error = await Assert.ThrowsAsync<FleetRequestException>(() => client.SubmitCommandAsync(new()
        { Kind = PipeProtocol.SetConfig, ExpectedRevision = current.Config.Revision, Config = current.Config }));
        Assert.Equal("protection", error.Code);
        Assert.Equal(CurveFallbackPolicy.SafeSpeed, f.Engine.Engine.GetConfig().Curve.FallbackPolicy);
        Assert.Equal(70, f.Engine.Engine.GetConfig().Curve.FallbackMinPercent);
    }

    [Fact]
    public async Task Network_responses_do_not_make_a_frozen_local_snapshot_look_current()
    {
        EngineOptions? options = null;
        using var engine = new EngineFixture(configure: opt => options = opt);
        var local = new LocalEngineClient(engine.Engine);
        EngineFixture.WaitUntil(() => local.IsSnapshotFresh);
        options!.SnapshotInterval = TimeSpan.FromHours(1);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await using var server = new FleetAgentServer(local, new FleetAgentOptions
        { Enabled = true, Port = 0, AccessKeys = [new() { TokenSha256 = FleetClient.TokenHash(token) }] }, certificate);
        await server.StartAsync();
        var client = new FleetClient(new() { Host = "127.0.0.1", Port = server.BoundPort, CertificateSha256 = server.CertificateSha256 }, token);
        var initial = await client.GetSnapshotAsync();
        Assert.True(initial.SnapshotAgeSeconds < 5);
        EngineFixture.WaitUntil(() => !local.IsSnapshotFresh, timeoutMs: 7000, because: "publicador local parou de produzir snapshots");
        var error = await Assert.ThrowsAsync<FleetRequestException>(() => client.GetSnapshotAsync());
        Assert.Equal("unavailable", error.Code);
        engine.Hw.Sensors.ForcedCpuTemp = 100;
        EngineFixture.WaitUntil(() => engine.Engine.GetSnapshot().FailSafeActive, because: "proteção local segue independente da publicação e rede");
    }
}
