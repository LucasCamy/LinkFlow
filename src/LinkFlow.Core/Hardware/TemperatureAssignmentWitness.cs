using LibreHardwareMonitor.Hardware;

namespace LinkFlow.Core;

/// <summary>
/// LHM 0.9.6 can retain a positive Value when an update skips a sensor. Its standard
/// Sensor resets Min/Max to null and refills them on every finite assignment, even
/// an unchanged value. These statistics belong to our private Computer; they are
/// deliberately per-read markers here, not lifetime statistics or physical timestamps.
/// </summary>
internal sealed class TemperatureAssignmentWitness
{
    private static readonly System.Reflection.Assembly ProviderAssembly = typeof(Computer).Assembly;
    private static readonly bool SupportedVersion = ProviderAssembly.GetName().Version == new Version(0, 9, 6, 0);
    private readonly ISensor? _sensor;
    private readonly string? _unavailable;

    private TemperatureAssignmentWitness(ISensor? sensor, string? unavailable)
    {
        _sensor = sensor;
        _unavailable = unavailable;
    }

    internal static bool SupportsKnownSensor(ISensor sensor) => SupportedVersion &&
        sensor.SensorType == SensorType.Temperature && sensor.GetType().Assembly == ProviderAssembly &&
        sensor.GetType().FullName == "LibreHardwareMonitor.Hardware.Sensor";

    internal static TemperatureAssignmentWitness Arm(ISensor? sensor, Func<ISensor, bool> supports)
    {
        if (sensor == null) return new(null, "not-reported");
        try
        {
            if (!supports(sensor)) return new(sensor, "provider-witness-unsupported");
            sensor.ResetMin();
            sensor.ResetMax();
            return new(sensor, sensor.Min == null && sensor.Max == null ? null : "provider-witness-unsupported");
        }
        catch (Exception)
        {
            // An unsupported managed marker must not prevent the one existing Update
            // from acquiring other telemetry. No native call is made by this helper.
            return new(sensor, "provider-witness-unsupported");
        }
    }

    internal (float? Value, string? Reason) Complete(ISensor? current)
    {
        if (!ReferenceEquals(current, _sensor)) return (null, "provider-sensor-replaced");
        if (_unavailable != null) return (null, _unavailable);
        if (current?.Value is not { } value || !float.IsFinite(value) || value <= 0)
            return (null, "not-reported");
        if (current.Min is not { } min || current.Max is not { } max ||
            !float.IsFinite(min) || !float.IsFinite(max) || min > value || max < value)
            return (null, "provider-not-updated");
        return (value, null);
    }
}
