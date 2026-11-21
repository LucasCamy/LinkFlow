using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using LinkFlow.App.Services;
using LinkFlow.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.Tests;

public sealed class UpdateReliabilityTests
{
    private const string Pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";

    [Theory]
    [InlineData("Other Publisher", Pin)]
    [InlineData("LinkFlow Publisher", "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void Official_policy_rejects_a_trusted_signature_from_another_publisher_or_certificate(string publisher, string pin)
    {
        var verifier = new FakeVerifier(new(publisher, pin));
        using var handle = new SafeFileHandle(0, ownsHandle: false);
        Assert.Throws<InvalidDataException>(() => new UpdateTrustPolicy(DistributionChannel.Official, "LinkFlow Publisher", Pin).Verify("installer.exe", handle, verifier));
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public void Official_policy_requires_windows_trust_and_an_explicit_pin()
    {
        using var handle = new SafeFileHandle(0, ownsHandle: false);
        var untrusted = new FakeVerifier(null);
        Assert.Throws<InvalidDataException>(() => new UpdateTrustPolicy(DistributionChannel.Official, "LinkFlow Publisher", Pin).Verify("installer.exe", handle, untrusted));
        Assert.Throws<InvalidDataException>(() => new UpdateTrustPolicy(DistributionChannel.Official).Verify("installer.exe", handle, untrusted));
        Assert.Equal(1, untrusted.Calls);
    }

    [Fact]
    public void Official_policy_accepts_only_the_pinned_verified_identity()
    {
        using var handle = new SafeFileHandle(0, ownsHandle: false);
        var result = new UpdateTrustPolicy(DistributionChannel.Official, "LinkFlow Publisher", Pin)
            .Verify("installer.exe", handle, new FakeVerifier(new("LinkFlow Publisher", Pin.ToLowerInvariant())));
        Assert.True(result.PublisherVerified);
        Assert.Equal(DistributionChannel.Official, result.Channel);
        Assert.Equal("LinkFlow Publisher", result.Publisher);
    }

    [Fact]
    public void Explicit_development_policy_never_claims_publisher_verification()
    {
        using var handle = new SafeFileHandle(0, ownsHandle: false);
        var verifier = new FakeVerifier(null);
        var result = UpdateTrustPolicy.Development.Verify("unsigned.exe", handle, verifier);
        Assert.False(result.PublisherVerified);
        Assert.Equal(DistributionChannel.Development, result.Channel);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public void Windows_trust_provider_returns_the_verified_signer_and_a_sha256_certificate_pin()
    {
        // The Windows PowerShell binary can be catalog-signed, which intentionally does not satisfy the
        // embedded Authenticode policy used for downloaded installers. The installed .NET host has its own
        // embedded Microsoft signature. Locate the host from this test's runtime so custom SDK roots work.
        string executable = Path.GetFullPath(Path.Combine(
            System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        Assert.True(File.Exists(executable), $"The native trust integration test requires the signed .NET SDK host: {executable}");
        using var file = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var signer = WindowsAuthenticode.TrustedSigner(executable, file.SafeFileHandle);
        Assert.False(string.IsNullOrWhiteSpace(signer.Publisher));
        Assert.Equal(64, signer.CertificateSha256.Length);
        Assert.All(signer.CertificateSha256, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Downloader_enforces_signature_policy_after_hash_verification_and_cleans_rejected_files(bool trusted)
    {
        byte[] bytes = [1, 2, 3, 4];
        const string name = "LinkFlow-Setup-3.2.1.exe";
        var asset = new UpdateAsset(name, new Uri($"{AppUpdateClient.ReleasesPage}/download/v3.2.1/{name}"), bytes.Length, SHA256.HashData(bytes));
        var verifier = new FakeVerifier(trusted ? new("LinkFlow Publisher", Pin) : null);
        using var http = new HttpClient(new FixtureDownload(bytes));
        var client = new AppUpdateClient(http, new(DistributionChannel.Official, "LinkFlow Publisher", Pin), verifier);
        using var cache = new RecoveryDirectory();
        if (trusted)
        {
            using var downloaded = await client.DownloadAsync(asset, cache.Path, new Progress<int>(), default);
            Assert.True(downloaded.Trust.PublisherVerified);
            Assert.Throws<IOException>(() => File.OpenWrite(downloaded.Path));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(asset, cache.Path, new Progress<int>(), default));
            Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
        }
        Assert.Equal(1, verifier.Calls);
    }

    [Theory]
    [InlineData("../LinkFlow-Setup-3.2.1.exe", "https://github.com/LucasCamy/LinkFlow/releases/download/v3.2.1/LinkFlow-Setup-3.2.1.exe")]
    [InlineData("LinkFlow-Setup-3.2.1.exe", "relative.exe")]
    [InlineData("LinkFlow-Setup-3.2.1.exe", "https://github.com/LucasCamy/LinkFlow/releases/download/v3.2.0/LinkFlow-Setup-3.2.1.exe")]
    public async Task Downloader_rejects_traversal_relative_urls_and_mismatched_release_paths_before_download(string name, string url)
    {
        using var http = new HttpClient(new FixtureDownload([1]));
        using var cache = new RecoveryDirectory();
        var asset = new UpdateAsset(name, new Uri(url, UriKind.RelativeOrAbsolute), 1, SHA256.HashData(new byte[] { 1 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new AppUpdateClient(http).DownloadAsync(asset, cache.Path, new Progress<int>(), default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
    }

    [Fact]
    public void Preparing_update_preserves_independent_configuration_and_records_expected_monitoring()
    {
        using var cache = new RecoveryDirectory();
        var store = new UpdateRecoveryStore(cache.Path);
        var client = new RecoveryClient { IsRemote = true, Config = new ControlConfig { GlobalPercent = 47 }, Snapshot = HealthySnapshot() };
        var record = store.Prepare(new(3, 1, 0), new(3, 2, 0), client);
        client.Config.GlobalPercent = 88;
        var loaded = store.Load()!;
        Assert.Equal(UpdateRecoveryState.Pending, loaded.State);
        Assert.Equal("3.2.0", loaded.TargetVersion);
        Assert.Equal(47, JsonSerializer.Deserialize<ControlConfig>(loaded.ConfigurationJson, Json.Compact)!.GlobalPercent);
        Assert.True(record.ServiceRequired);
        Assert.True(record.HubRequired);
        Assert.True(record.CpuSensorRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Preparing_update_requires_live_writable_control_and_does_not_replace_recovery_on_failure(bool failSafe)
    {
        using var cache = new RecoveryDirectory();
        var store = new UpdateRecoveryStore(cache.Path);
        var client = new RecoveryClient { Connected = failSafe, Snapshot = HealthySnapshot() };
        client.Snapshot!.FailSafeActive = failSafe;
        Assert.Throws<InvalidOperationException>(() => store.Prepare(new(3, 1, 0), new(3, 2, 0), client));
        Assert.Null(store.Load());
    }

    [Theory]
    [InlineData("version", "installed-version-mismatch")]
    [InlineData("service", "service-offline")]
    [InlineData("service-version", "service-version-mismatch")]
    [InlineData("protocol", "protocol-mismatch")]
    [InlineData("old-snapshot", "snapshot-overdue")]
    [InlineData("hub", "hub-missing")]
    [InlineData("cpu", "cpu-sensor-missing")]
    [InlineData("failsafe", "failsafe-active")]
    public void Post_update_health_rejects_version_communication_or_monitoring_regressions(string failure, string reason)
    {
        var snapshot = HealthySnapshot();
        var probe = new UpdateHealthProbe(new(3, 2, 0), true, true, new(3, 2, 0), PipeProtocol.Version, snapshot);
        probe = failure switch
        {
            "version" => probe with { InstalledVersion = new(3, 1, 0) },
            "service" => probe with { Connected = false },
            "service-version" => probe with { ServiceVersion = new(3, 1, 0) },
            "protocol" => probe with { Protocol = PipeProtocol.Version - 1 },
            _ => probe
        };
        if (failure == "old-snapshot") snapshot.Timestamp = DateTime.Now.AddMinutes(-1);
        if (failure == "hub") snapshot.HubConnected = false;
        if (failure == "cpu") snapshot.CpuSensorAvailable = false;
        if (failure == "failsafe") snapshot.FailSafeActive = true;
        var record = new UpdateRecoveryRecord { TargetVersion = "3.2.0", ServiceRequired = true, HubRequired = true, CpuSensorRequired = true };
        Assert.Equal(reason, UpdateRecoveryStore.HealthFailure(record, probe, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Healthy_post_update_handshake_completes_journal_without_replacing_configuration()
    {
        using var cache = new RecoveryDirectory();
        var store = new UpdateRecoveryStore(cache.Path);
        var client = new RecoveryClient { IsRemote = true, Snapshot = HealthySnapshot() };
        store.Prepare(new(3, 1, 0), new(3, 2, 0), client);
        var result = await store.ValidatePendingAsync(() => new(new(3, 2, 0), true, true, new(3, 2, 0), PipeProtocol.Version, HealthySnapshot()), TimeSpan.Zero, default);
        Assert.Equal(UpdateRecoveryState.Healthy, result!.State);
        Assert.NotNull(result.ValidatedAtUtc);
        Assert.Empty(client.Operations);
        Assert.Equal(UpdateRecoveryState.Healthy, store.Load()!.State);
    }

    [Fact]
    public async Task Failed_health_retains_backup_and_restoration_requires_current_revision_confirmation()
    {
        using var cache = new RecoveryDirectory();
        var store = new UpdateRecoveryStore(cache.Path);
        var client = new RecoveryClient { Config = new ControlConfig { GlobalPercent = 47, Revision = 3 }, Snapshot = HealthySnapshot() };
        store.Prepare(new(3, 1, 0), new(3, 2, 0), client);
        await store.ValidatePendingAsync(() => new(new(3, 1, 0), false, true, null, null, client.Snapshot), TimeSpan.Zero, default);
        client.Config = new ControlConfig { GlobalPercent = 80, Revision = 9 };
        var result = await store.RestoreConfigurationAsync(client, default);
        Assert.Equal(9, client.LastExpectedRevision);
        Assert.Equal(CommandState.Applied, result.State);
        Assert.Equal(47, client.Config.GlobalPercent);
        Assert.Equal(10, client.Config.Revision);
        Assert.Equal(UpdateRecoveryState.ConfigurationRestored, store.Load()!.State);
    }

    [Fact]
    public async Task Restore_conflict_does_not_claim_rollback_or_overwrite_a_new_configuration()
    {
        using var cache = new RecoveryDirectory();
        var store = new UpdateRecoveryStore(cache.Path);
        var client = new RecoveryClient { Config = new ControlConfig { GlobalPercent = 47 }, Snapshot = HealthySnapshot(), RejectWithConflict = true };
        store.Prepare(new(3, 1, 0), new(3, 2, 0), client);
        store.MarkFailed("service-offline");
        client.Config = new ControlConfig { GlobalPercent = 80, Revision = 9 };
        var result = await store.RestoreConfigurationAsync(client, default);
        Assert.Equal(CommandState.Conflict, result.State);
        Assert.Equal(80, client.Config.GlobalPercent);
        Assert.Equal(UpdateRecoveryState.Failed, store.Load()!.State);
    }

    [Fact]
    public void Corrupted_backup_is_rejected_without_mutating_active_control()
    {
        using var cache = new RecoveryDirectory();
        var store = new UpdateRecoveryStore(cache.Path);
        var client = new RecoveryClient { Snapshot = HealthySnapshot() };
        store.Prepare(new(3, 1, 0), new(3, 2, 0), client);
        string path = Path.Combine(cache.Path, "update-recovery.json");
        var record = JsonSerializer.Deserialize<UpdateRecoveryRecord>(File.ReadAllText(path), Json.Options)!;
        record.ConfigurationJson += " ";
        File.WriteAllText(path, JsonSerializer.Serialize(record, Json.Options));
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Empty(client.Operations);
    }

    private static EngineSnapshot HealthySnapshot() => new() { Timestamp = DateTime.Now, SensorsReady = true, HubConnected = true, CpuSensorAvailable = true, CpuTemp = 50 };

    private sealed class FakeVerifier(TrustedSignerIdentity? identity) : IUpdateSignatureVerifier
    {
        public int Calls { get; private set; }
        public TrustedSignerIdentity Verify(string path, SafeFileHandle handle)
        {
            Calls++;
            return identity ?? throw new InvalidOperationException("Untrusted signature");
        }
    }

    private sealed class FixtureDownload(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private sealed class RecoveryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("LinkFlow-recovery-test-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class RecoveryClient : IEngineClient
    {
        public bool IsRemote { get; set; }
        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected;
        public bool CanWrite => Connected;
        public bool IsSimulated => true;
        public string ModeDescription => "test";
        public ControlConfig Config { get; set; } = new();
        public EngineSnapshot? Snapshot { get; set; }
        public bool RejectWithConflict { get; set; }
        public long? LastExpectedRevision { get; private set; }
        private readonly List<CommandReceipt> _operations = [];
        public IReadOnlyList<CommandReceipt> Operations => _operations;
#pragma warning disable CS0067
        public event Action<EngineSnapshot>? SnapshotUpdated;
        public event Action<ControlConfig>? ConfigChanged;
        public event Action<ControlConfig>? AnyConfigChanged;
        public event Action<EngineAlert>? AlertRaised;
        public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
        public string SubmitCommand(EngineCommandRequest request)
        {
            LastExpectedRevision = request.ExpectedRevision;
            if (!RejectWithConflict) { Config = request.Config!.Clone(); Config.Revision = request.ExpectedRevision!.Value + 1; }
            _operations.Add(new() { Id = request.Id, State = RejectWithConflict ? CommandState.Conflict : CommandState.Applied, Persistence = PersistenceState.NotRequired });
            return request.Id;
        }
        public void Start() { }
        public void UpdateConfig(Action<ControlConfig> mutate) => mutate(Config);
        public void ReplaceConfig(ControlConfig config) => Config = config;
        public void RequestLcdFrame() { }
        public void NotifyResume() { }
        public void RefreshRgb() { }
        public void RescanDevices() { }
        public void SetCurveTest(float? temperature) { }
        public void StartCalibration() { }
        public void CancelCalibration() { }
        public void SetSensorDemand(SensorDemand demand) { }
        public Task ShutdownAsync(bool restoreHardwareMode = true) => Task.CompletedTask;
        public void Dispose() { }
    }
}
