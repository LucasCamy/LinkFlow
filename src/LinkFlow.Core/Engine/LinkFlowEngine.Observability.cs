namespace LinkFlow.Core;

public sealed partial class LinkFlowEngine
{
    private OperationalHistory? _operationalHistory;
    private ControlConfig? _observedConfig;
    private readonly object _observationConfigLock = new();

    /// <summary>History is collected by the engine and is independent of connected interfaces.</summary>
    public OperationalHistory History => _operationalHistory ?? throw new InvalidOperationException("Operational history is not initialized.");

    private void InitializeObservability()
    {
        var options = _opt.History;
        if (options == null)
        {
            options = new OperationalHistoryOptions
            {
                DirectoryPath = _opt.ConfigPath == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_opt.ConfigPath))!, "history")
            };
        }
        _operationalHistory = new(options);
        _observedConfig = _config.Clone();
        ConfigChanged += ObserveConfigurationChange;
        AlertRaised += ObserveRaisedAlert;
        CommandUpdated += ObserveCommandReceipt;
    }

    private void ObserveConfigurationChange(ControlConfig configuration, object? origin)
    {
        ControlConfig? previous;
        lock (_observationConfigLock)
        {
            previous = _observedConfig;
            _observedConfig = configuration;
        }
        if (previous != null)
            _operationalHistory?.RecordConfigChange(previous, configuration, origin is string source ? source : origin?.GetType().Name ?? "engine");
    }

    private void ObserveRaisedAlert(EngineAlert alert) => _operationalHistory?.RecordAlert(alert);

    private void ObserveCommandReceipt(CommandReceipt receipt) => _operationalHistory?.RecordEvent(new()
    {
        Kind = "command." + receipt.State.ToString().ToLowerInvariant(), TimestampUtc = receipt.UpdatedAtUtc,
        Severity = receipt.State is CommandState.Failed or CommandState.Rejected or CommandState.Conflict or CommandState.Expired
            ? AlertSeverity.Warning : AlertSeverity.Info,
        Source = "control", Message = receipt.Message,
        Data = new()
        {
            ["commandId"] = receipt.Id, ["kind"] = receipt.Kind, ["revision"] = receipt.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["confirmation"] = receipt.Confirmation.ToString(), ["persistence"] = receipt.Persistence.ToString(),
            ["requestedAtUtc"] = receipt.RequestedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
        }
    });

    private void PublishObservability(EngineSnapshot snapshot) => _operationalHistory?.RecordSnapshot(snapshot);

    internal void RecordOperationalEvent(string kind, string message, AlertSeverity severity = AlertSeverity.Info,
        string source = "engine", string? deviceId = null, Dictionary<string, string>? data = null) =>
        _operationalHistory?.RecordEvent(new()
        {
            Kind = kind, Message = message, Severity = severity, Source = source, DeviceId = deviceId,
            Data = data ?? new()
        });

    private void RecordAlertResolution(string key) => _operationalHistory?.RecordAlertResolution(key);

    private async Task StopObservabilityAsync()
    {
        ConfigChanged -= ObserveConfigurationChange;
        AlertRaised -= ObserveRaisedAlert;
        CommandUpdated -= ObserveCommandReceipt;
        if (_operationalHistory != null) await _operationalHistory.DisposeAsync().ConfigureAwait(false);
    }
}
