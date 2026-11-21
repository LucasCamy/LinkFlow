namespace LinkFlow.Core;

public enum WindowsOperationState { Running, ProcessExited, Succeeded, RequiresRestart, Canceled, Failed, Unconfirmed }

/// <summary>A execução de uma ferramenta e a comprovação do reparo são estados distintos.</summary>
public sealed record WindowsOperationRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public WindowsOperationState State { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int? ProcessId { get; init; }
    public DateTimeOffset? ProcessStartUtc { get; init; }
    public int? ExitCode { get; init; }
    public string? Detail { get; init; }
}

public sealed partial class LinkFlowEngine
{
    public void ReportExternalOperation(WindowsOperationRecord operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Id is not { Length: > 0 and <= 80 } || operation.Kind is not { Length: > 0 and <= 80 } ||
            operation.Title is not { Length: > 0 and <= 256 } || operation.Detail?.Length > 1024 || !Enum.IsDefined(operation.State))
            throw new ArgumentException("Relato de operação inválido.");
        RecordOperationalEvent("operation." + operation.State.ToString().ToLowerInvariant(), operation.Title,
            operation.State == WindowsOperationState.Failed ? AlertSeverity.Warning : AlertSeverity.Info, "windows",
            data: new() { ["id"] = operation.Id, ["kind"] = operation.Kind, ["state"] = operation.State.ToString(),
                ["exitCode"] = operation.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["detail"] = operation.Detail ?? string.Empty });
    }
}
