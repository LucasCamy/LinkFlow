using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using LinkFlow.Core;
using LinkFlow.Core.Ipc;

namespace LinkFlow.App.Services;

internal enum UpdateRecoveryState { None, Pending, Validating, Healthy, Failed, ConfigurationRestored }

internal sealed class UpdateRecoveryRecord
{
    public string Id { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public DateTimeOffset PreparedAtUtc { get; set; }
    public DateTimeOffset? ValidatedAtUtc { get; set; }
    public UpdateRecoveryState State { get; set; }
    public string? Reason { get; set; }
    public bool ServiceRequired { get; set; }
    public bool HubRequired { get; set; }
    public bool CpuSensorRequired { get; set; }
    // Keep the exact pre-update JSON: adding a default property in a newer assembly must not invalidate its hash.
    public string ConfigurationJson { get; set; } = string.Empty;
    public string ConfigurationSha256 { get; set; } = string.Empty;
}

internal sealed record UpdateHealthProbe(Version InstalledVersion, bool IsServiceMode, bool Connected,
    Version? ServiceVersion, int? Protocol, EngineSnapshot? Snapshot);

/// <summary>Update journal keeps the last configuration until the new app and service have demonstrated health.</summary>
internal sealed class UpdateRecoveryStore(string directory)
{
    private const long MaxJournalBytes = 2 * 1024 * 1024;
    private readonly JsonFileStore<UpdateRecoveryRecord> _store = new(Path.Combine(directory, "update-recovery.json"));

    private static string Hash(string json) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
    private static Version Triplet(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    public UpdateRecoveryRecord? Load()
    {
        if (!File.Exists(_store.FilePath)) return null;
        if (new FileInfo(_store.FilePath).Length > MaxJournalBytes) throw new InvalidDataException("O registro de recuperação da atualização ultrapassou o limite permitido.");
        var record = _store.Load();
        if (!Guid.TryParseExact(record.Id, "N", out _) || !Version.TryParse(record.FromVersion, out _) ||
            !Version.TryParse(record.TargetVersion, out _) || string.IsNullOrWhiteSpace(record.ConfigurationJson) ||
            !string.Equals(Hash(record.ConfigurationJson), record.ConfigurationSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A cópia de configuração anterior à atualização está inválida. A configuração atual foi preservada.");
        return record;
    }

    private UpdateRecoveryRecord Save(UpdateRecoveryRecord record)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(record, Json.Options).LongLength > MaxJournalBytes)
            throw new InvalidDataException("A configuração é grande demais para o registro de recuperação da atualização.");
        if (!_store.Save(record)) throw new IOException("Não foi possível salvar o registro de recuperação da atualização.");
        return record;
    }

    public UpdateRecoveryRecord Prepare(Version current, Version target, IEngineClient engine)
    {
        if (Triplet(target) <= Triplet(current)) throw new InvalidOperationException("A atualização deve ser mais recente que a versão instalada.");
        if (!engine.IsConnected || !engine.CanWrite) throw new InvalidOperationException("Reconecte ao controle antes de preparar a atualização.");
        if (engine.Snapshot?.FailSafeActive == true) throw new InvalidOperationException("A proteção está ativa. Aguarde a estabilização antes de atualizar o controle.");
        var config = engine.Config.Clone();
        config.Normalize();
        string configJson = JsonSerializer.Serialize(config, Json.Compact);
        return Save(new()
        {
            Id = Guid.NewGuid().ToString("N"), FromVersion = Triplet(current).ToString(3), TargetVersion = Triplet(target).ToString(3),
            PreparedAtUtc = DateTimeOffset.UtcNow, State = UpdateRecoveryState.Pending,
            ServiceRequired = engine.IsRemote, HubRequired = engine.Snapshot?.HubConnected == true,
            CpuSensorRequired = engine.Snapshot?.CpuSensorAvailable == true,
            ConfigurationJson = configJson, ConfigurationSha256 = Hash(configJson)
        });
    }

    public UpdateRecoveryRecord MarkFailed(string reason)
    {
        var record = Load() ?? throw new InvalidOperationException("Nenhuma atualização está registrada.");
        record.State = UpdateRecoveryState.Failed;
        record.Reason = reason;
        record.ValidatedAtUtc = DateTimeOffset.UtcNow;
        return Save(record);
    }

    public static string? HealthFailure(UpdateRecoveryRecord record, UpdateHealthProbe probe, DateTimeOffset now)
    {
        if (Triplet(probe.InstalledVersion) != Version.Parse(record.TargetVersion)) return "installed-version-mismatch";
        if (record.ServiceRequired)
        {
            if (!probe.IsServiceMode || !probe.Connected) return "service-offline";
            if (probe.ServiceVersion == null || Triplet(probe.ServiceVersion) != Version.Parse(record.TargetVersion)) return "service-version-mismatch";
            if (probe.Protocol != PipeProtocol.Version) return "protocol-mismatch";
        }
        if (probe.Snapshot is not { } snapshot) return "snapshot-missing";
        var age = now - new DateTimeOffset(snapshot.Timestamp).ToUniversalTime();
        if (age < TimeSpan.FromSeconds(-5) || age > TimeSpan.FromSeconds(5)) return "snapshot-overdue";
        if (!snapshot.SensorsReady) return "sensors-initializing";
        if (record.HubRequired && !snapshot.HubConnected) return "hub-missing";
        if (record.CpuSensorRequired && !snapshot.CpuSensorAvailable) return "cpu-sensor-missing";
        if (snapshot.FailSafeActive) return "failsafe-active";
        return null;
    }

    public async Task<UpdateRecoveryRecord?> ValidatePendingAsync(Func<UpdateHealthProbe> readProbe, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var record = Load();
        if (record == null || record.State is not (UpdateRecoveryState.Pending or UpdateRecoveryState.Validating)) return record;
        record.State = UpdateRecoveryState.Validating;
        Save(record);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        string? failure;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            failure = HealthFailure(record, readProbe(), DateTimeOffset.UtcNow);
            if (failure == null)
            {
                record.State = UpdateRecoveryState.Healthy;
                record.Reason = null;
                record.ValidatedAtUtc = DateTimeOffset.UtcNow;
                return Save(record);
            }
            if (elapsed.Elapsed >= timeout) break;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        } while (true);
        return MarkFailed(failure!);
    }

    public async Task<CommandReceipt> RestoreConfigurationAsync(IEngineClient engine, CancellationToken cancellationToken)
    {
        var record = Load() ?? throw new InvalidOperationException("Nenhuma configuração anterior está disponível.");
        if (record.State != UpdateRecoveryState.Failed) throw new InvalidOperationException("A restauração só fica disponível após uma atualização sem validação de saúde.");
        if (!engine.IsConnected || !engine.CanWrite) throw new InvalidOperationException("Reconecte ao controle antes de restaurar a configuração.");
        var command = new EngineCommandRequest
        {
            Kind = PipeProtocol.SetConfig, ExpectedRevision = engine.Config.Revision,
            Config = JsonSerializer.Deserialize<ControlConfig>(record.ConfigurationJson, Json.Compact) ?? throw new InvalidDataException("A configuração anterior não pôde ser lida."),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(15)
        };
        engine.SubmitCommand(command);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(15))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.Operations.FirstOrDefault(operation => operation.Id == command.Id) is { } receipt)
            {
                if (receipt.State is CommandState.Rejected or CommandState.Expired or CommandState.Conflict or CommandState.Failed or CommandState.Unconfirmed)
                    return receipt;
                if ((receipt.State is CommandState.Applied or CommandState.NotVerifiable) && (receipt.Persistence is PersistenceState.Saved or PersistenceState.NotRequired))
                {
                    record.State = UpdateRecoveryState.ConfigurationRestored;
                    record.Reason = "configuration-restored-binaries-unchanged";
                    record.ValidatedAtUtc = DateTimeOffset.UtcNow;
                    Save(record);
                    return receipt;
                }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("A restauração foi solicitada, mas o controle ainda não confirmou o resultado. Confira a central de operações.");
    }
}
