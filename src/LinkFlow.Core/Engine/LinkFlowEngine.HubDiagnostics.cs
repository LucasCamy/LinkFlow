namespace LinkFlow.Core;

public sealed partial class LinkFlowEngine
{
    /// <summary>Reads bounded cached reports from the existing controller. Never requests a hardware read.</summary>
    public HubTelemetryDiagnostics GetHubTelemetryDiagnostics()
    {
        if (IsSimulated)
            return new() { Supported = false, Provider = _hub.GetType().Name, Reason = "simulation-has-no-native-packets" };
        return _hub is IHubTelemetryDiagnostics diagnostics ? diagnostics.GetTelemetryDiagnostics() :
            new() { Supported = false, Provider = _hub.GetType().Name, Reason = "provider-does-not-support-raw-telemetry" };
    }
}
