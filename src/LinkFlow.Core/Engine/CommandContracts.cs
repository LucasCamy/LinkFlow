using System.Text.Json.Nodes;

namespace LinkFlow.Core;

/// <summary>Estado da operação. Aceitar a configuração não comprova o resultado físico.</summary>
public enum CommandState { Pending, Accepted, Applied, NotVerifiable, Rejected, Expired, Conflict, Failed, Superseded, Unconfirmed }
public enum CommandConfirmation { None, Software, Controller, Physical }
public enum PersistenceState { NotRequired, Pending, Saved, Failed }

public sealed record CommandTargetResult
{
    public string Target { get; init; } = string.Empty;
    public bool? Accepted { get; init; }
    public string? Detail { get; init; }
    public DateTimeOffset? ConfirmedAtUtc { get; init; }
}

public sealed record CommandReceipt
{
    public string Id { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public CommandState State { get; init; }
    public CommandConfirmation Confirmation { get; init; }
    public long Revision { get; init; }
    public PersistenceState Persistence { get; init; }
    public DateTimeOffset RequestedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<CommandTargetResult> Targets { get; init; } = [];
    public bool IsTerminal => State is not (CommandState.Pending or CommandState.Accepted);
}

public sealed class EngineCommandRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Protocol { get; set; } = 2;
    public string Kind { get; set; } = string.Empty;
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow.AddSeconds(30);
    public long? ExpectedRevision { get; set; }
    public ControlConfig? Config { get; set; }
    public JsonNode? Payload { get; set; }
}

public sealed record ConfigurationPersistence
{
    public long ActiveRevision { get; init; }
    public long SavedRevision { get; init; }
    public PersistenceState State { get; init; }
    public DateTimeOffset? LastAttemptUtc { get; init; }
    public DateTimeOffset? LastSuccessUtc { get; init; }
    public string? Error { get; init; }
}

public sealed partial class ControlConfig
{
    /// <summary>Revisão do serviço: clientes não podem sobrescrever uma revisão que não leram.</summary>
    public long Revision { get; set; }
}

public static class ConfigurationSchema
{
    public const int CurrentVersion = 2;

    public static void ValidateAndMigrate(ControlConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.SchemaVersion < 0 || config.SchemaVersion > CurrentVersion)
            throw new InvalidDataException($"Versão de configuração não suportada: {config.SchemaVersion}. Suportada: {CurrentVersion}.");
        if (config.Revision < 0) throw new InvalidDataException("A revisão da configuração não pode ser negativa.");
        // Esquemas 0/1 não tinham revisão; os campos novos têm padrões seguros e migram sem descartar perfis.
        config.SchemaVersion = CurrentVersion;
    }
}
