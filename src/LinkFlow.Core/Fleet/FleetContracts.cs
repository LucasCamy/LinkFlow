namespace LinkFlow.Core;

[Flags]
public enum FleetPermission { Observe = 1, Control = 2 }

/// <summary>Token armazenado apenas como SHA-256 no agente; o segredo é entregue no pareamento explícito.</summary>
public sealed class FleetAccessKey
{
    public string Name { get; set; } = "Observador";
    public string TokenSha256 { get; set; } = string.Empty;
    public FleetPermission Permissions { get; set; } = FleetPermission.Observe;
}

public sealed class FleetAgentOptions
{
    public bool Enabled { get; set; }
    public string AgentId { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "LinkFlow";
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 47931;
    public bool AllowNetwork { get; set; }
    public bool AllowControl { get; set; }
    public int MaxConnections { get; set; } = 4;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int RequestsPerMinute { get; set; } = 240;
    public List<FleetAccessKey> AccessKeys { get; set; } = [];
    public string CertificateFile { get; set; } = "agent.pfx";
    public string ProtectedCertificatePassword { get; set; } = string.Empty;
}

public sealed class FleetPeerSettings
{
    public string Name { get; set; } = "LinkFlow";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 47931;
    public string CertificateSha256 { get; set; } = string.Empty;
    public string ProtectedToken { get; set; } = string.Empty;
}

public sealed record FleetSnapshot
{
    public string AgentId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public FleetPermission Permissions { get; init; }
    public DateTimeOffset CollectedAtUtc { get; init; }
    /// <summary>Idade na máquina de origem, para não depender de relógios iguais entre os PCs.</summary>
    public double? SnapshotAgeSeconds { get; init; }
    public ControlConfig Config { get; init; } = new();
    public EngineSnapshot Snapshot { get; init; } = new();
}

internal sealed class FleetRequest
{
    public int Protocol { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Token { get; set; } = string.Empty;
    public string Kind { get; set; } = "snapshot";
    public EngineCommandRequest? Command { get; set; }
    public HistoryQuery? History { get; set; }
    public EventQuery? Events { get; set; }
}

internal sealed class FleetResponse
{
    public string Id { get; set; } = string.Empty;
    public System.Text.Json.Nodes.JsonNode? Data { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class FleetRequestException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}
