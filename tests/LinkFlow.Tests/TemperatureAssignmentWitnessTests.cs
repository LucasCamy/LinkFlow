using System.Reflection;
using LibreHardwareMonitor.Hardware;

namespace LinkFlow.Tests;

public sealed class TemperatureAssignmentWitnessTests
{
    // Only the managed standard Sensor is instantiated. No Computer.Open, CPU/GPU
    // provider constructor, SDK, driver, process probe or official pipe is used.
    private static readonly Type SensorImplementation = typeof(Computer).Assembly.GetType("LibreHardwareMonitor.Hardware.Sensor", true)!;
    private static readonly PropertyInfo SensorValue = SensorImplementation.GetProperty(nameof(ISensor.Value))!;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cached_positive_without_assignment_is_not_a_new_acquisition(bool cpu)
    {
        var hardware = new MemoryHardware(cpu ? HardwareType.Cpu : HardwareType.GpuNvidia);
        var temperature = hardware.Add(cpu ? "Core (Tctl/Tdie)" : "GPU Core", SensorType.Temperature, 60);
        var other = hardware.Add(cpu ? "CPU Total" : "GPU Core", SensorType.Load, 35);
        using var monitor = Monitor(hardware, cpu);

        var sample = monitor.Read();

        Assert.Equal(1, hardware.UpdateCalls);
        Assert.Null(cpu ? sample.CpuTemperature : sample.GpuTemperature);
        Assert.Equal("provider-not-updated", cpu ? sample.CpuTemperatureUnavailableReason : sample.GpuTemperatureUnavailableReason);
        Assert.Equal(60, temperature.Value); // The provider cache remains intact but is not promoted to Fresh.
        Assert.Equal(35, cpu ? sample.CpuLoad : sample.GpuLoad);
        Assert.Equal(35, other.Min); // Only the selected critical temperature statistics were reset.
        Assert.Equal(35, other.Max);
    }

    [Theory]
    [InlineData(HardwareType.Cpu)]
    [InlineData(HardwareType.GpuNvidia)]
    [InlineData(HardwareType.GpuAmd)]
    [InlineData(HardwareType.GpuIntel)]
    public void Identical_finite_assignment_refreshes_without_a_platform_whitelist(HardwareType type)
    {
        bool cpu = type == HardwareType.Cpu;
        var hardware = new MemoryHardware(type);
        var temperature = hardware.Add(cpu ? "CPU Package" : "GPU Core", SensorType.Temperature, 60);
        hardware.OnUpdate = () => Assign(temperature, 60);
        using var monitor = Monitor(hardware, cpu);

        var sample = monitor.Read();

        Assert.True(TemperatureAssignmentWitness.SupportsKnownSensor(temperature));
        Assert.Equal(1, hardware.UpdateCalls);
        Assert.Equal(60, cpu ? sample.CpuTemperature : sample.GpuTemperature);
        Assert.Null(cpu ? sample.CpuTemperatureUnavailableReason : sample.GpuTemperatureUnavailableReason);
        if (cpu) Assert.True(sample.CpuSensorAvailable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_or_missing_assignments_never_resurrect_the_positive_cache(float? value)
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        var temperature = hardware.Add("GPU Core", SensorType.Temperature, 60);
        hardware.OnUpdate = () => Assign(temperature, value);
        using var monitor = Monitor(hardware, false);

        var sample = monitor.Read();

        Assert.Null(sample.GpuTemperature);
        Assert.Equal("not-reported", sample.GpuTemperatureUnavailableReason);
        Assert.Equal(1, hardware.UpdateCalls);
    }

    [Fact]
    public void New_sensor_with_preexisting_statistics_cannot_inherit_the_old_marker()
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        var original = hardware.Add("GPU Core", SensorType.Temperature, 60);
        hardware.OnUpdate = () =>
        {
            hardware.Remove(original);
            hardware.Add("GPU Core", SensorType.Temperature, 61);
        };
        using var monitor = Monitor(hardware, false);

        var sample = monitor.Read();

        Assert.Null(sample.GpuTemperature);
        Assert.Equal("provider-sensor-replaced", sample.GpuTemperatureUnavailableReason);
        Assert.Equal(1, hardware.UpdateCalls);
    }

    [Fact]
    public void Throwing_update_is_not_retried_or_confirmed()
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        hardware.Add("GPU Core", SensorType.Temperature, 60);
        hardware.OnUpdate = () => throw new InvalidOperationException("managed fixture failure");
        using var monitor = Monitor(hardware, false);

        var sample = monitor.Read();

        Assert.Null(sample.GpuTemperature);
        Assert.Equal("provider-read-failed", sample.GpuTemperatureUnavailableReason);
        Assert.Equal(1, hardware.UpdateCalls);
    }

    [Fact]
    public void Unsupported_capability_still_updates_other_fields_once()
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        var temperature = hardware.Add("GPU Core", SensorType.Temperature, 60);
        var load = hardware.Add("GPU Core", SensorType.Load, 35);
        hardware.OnUpdate = () => { Assign(temperature, 61); Assign(load, 40); };
        using var monitor = new HardwareMonitor(null, hardware, _ => false);

        var sample = monitor.Read();

        Assert.Null(sample.GpuTemperature);
        Assert.Equal("provider-witness-unsupported", sample.GpuTemperatureUnavailableReason);
        Assert.Equal(40, sample.GpuLoad);
        Assert.Equal(1, hardware.UpdateCalls);
    }

    [Fact]
    public void Tracking_disabled_sensor_cannot_claim_an_assignment()
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        var temperature = hardware.Add("GPU Core", SensorType.Temperature, 60, disableHistory: true);
        hardware.OnUpdate = () => Assign(temperature, 60);
        using var monitor = Monitor(hardware, false);

        var sample = monitor.Read();

        Assert.Null(sample.GpuTemperature);
        Assert.Equal("provider-not-updated", sample.GpuTemperatureUnavailableReason);
        Assert.Null(temperature.Min);
        Assert.Null(temperature.Max);
    }

    [Fact]
    public void Foreign_sensor_implementation_is_rejected_by_the_production_guard()
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        var sensor = new ForeignSensor(hardware);
        hardware.Add(sensor);
        using var monitor = Monitor(hardware, false);

        var sample = monitor.Read();

        Assert.False(TemperatureAssignmentWitness.SupportsKnownSensor(sensor));
        Assert.Null(sample.GpuTemperature);
        Assert.Equal("provider-witness-unsupported", sample.GpuTemperatureUnavailableReason);
        Assert.Equal(0, sensor.ResetCalls);
        Assert.Equal(1, hardware.UpdateCalls);
    }

    [Fact]
    public void Failed_managed_marker_does_not_skip_the_existing_provider_update()
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        var sensor = new ForeignSensor(hardware) { ThrowOnReset = true };
        hardware.Add(sensor);
        using var monitor = new HardwareMonitor(null, hardware, _ => true);

        var sample = monitor.Read();

        Assert.Null(sample.GpuTemperature);
        Assert.Equal("provider-witness-unsupported", sample.GpuTemperatureUnavailableReason);
        Assert.Equal(1, sensor.ResetCalls);
        Assert.Equal(1, hardware.UpdateCalls);
    }

    [Fact]
    public void Another_temperature_assignment_cannot_confirm_the_cached_core()
    {
        var hardware = new MemoryHardware(HardwareType.GpuNvidia);
        hardware.Add("GPU Core", SensorType.Temperature, 60);
        var hotSpot = hardware.Add("GPU Hot Spot", SensorType.Temperature, 70);
        hardware.OnUpdate = () => Assign(hotSpot, 71);
        using var monitor = Monitor(hardware, false);

        var sample = monitor.Read();

        Assert.Null(sample.GpuTemperature);
        Assert.Equal("provider-not-updated", sample.GpuTemperatureUnavailableReason);
        Assert.Equal(71, sample.GpuHotspotTemperature);
        Assert.Equal(70, hotSpot.Min);
        Assert.Equal(71, hotSpot.Max);
        Assert.Equal(1, hardware.UpdateCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Engine_health_preserves_last_success_until_an_identical_value_is_reassigned(bool cpu)
    {
        var hardware = new MemoryHardware(cpu ? HardwareType.Cpu : HardwareType.GpuNvidia);
        var temperature = hardware.Add(cpu ? "CPU Package" : "GPU Core", SensorType.Temperature, 60);
        hardware.OnUpdate = () => Assign(temperature, 60);
        using var monitor = Monitor(hardware, cpu);
        using var engine = new LinkFlowEngine(new SimulatedHardwareFactory(), new EngineOptions
        {
            ConfigPath = null, History = new OperationalHistoryOptions { DirectoryPath = null }
        }, new ControlConfig());
        var record = typeof(LinkFlowEngine).GetMethod("RecordSensorAcquisition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string key = cpu ? "cpu.temp" : "gpu.temp";

        record.Invoke(engine, [monitor.Read()]);
        var first = Assert.Single(engine.GetSnapshot().TelemetryHealth, health => health.Key == key);
        Assert.Equal(ReadingValidity.Fresh, first.Validity);
        Assert.NotNull(first.LastSuccessUtc);

        hardware.OnUpdate = null;
        record.Invoke(engine, [monitor.Read()]);
        var stale = Assert.Single(engine.GetSnapshot().TelemetryHealth, health => health.Key == key);
        Assert.Equal(ReadingValidity.Stale, stale.Validity);
        Assert.Equal(first.LastSuccessUtc, stale.LastSuccessUtc);
        Assert.Equal("provider-not-updated", stale.Reason);
        Assert.Equal(1, stale.ConsecutiveFailures);

        hardware.OnUpdate = () => Assign(temperature, 60);
        record.Invoke(engine, [monitor.Read()]);
        var recovered = Assert.Single(engine.GetSnapshot().TelemetryHealth, health => health.Key == key);
        Assert.Equal(ReadingValidity.Fresh, recovered.Validity);
        Assert.True(recovered.LastSuccessUtc >= stale.LastSuccessUtc);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Equal(3, hardware.UpdateCalls);
    }

    private static HardwareMonitor Monitor(MemoryHardware hardware, bool cpu) =>
        cpu ? new HardwareMonitor(hardware, null) : new HardwareMonitor(null, hardware);

    private static void Assign(ISensor sensor, float? value) => SensorValue.SetValue(sensor, value);

    private sealed class MemoryHardware(HardwareType type) : Hardware("managed fixture", new Identifier("memory-fixture"), new MemorySettings())
    {
        public int UpdateCalls { get; private set; }
        public Action? OnUpdate { get; set; }
        public override HardwareType HardwareType => type;
        public override void Update() { UpdateCalls++; OnUpdate?.Invoke(); }

        public ISensor Add(string name, SensorType type, float? value, bool disableHistory = false)
        {
            var sensor = (ISensor)Activator.CreateInstance(SensorImplementation,
                [name, Sensors.Length, false, type, this, null!, new MemorySettings(), disableHistory])!;
            Assign(sensor, value);
            ActivateSensor(sensor);
            return sensor;
        }

        public void Remove(ISensor sensor) => DeactivateSensor(sensor);
        public void Add(ISensor sensor) => ActivateSensor(sensor);
    }

    private sealed class MemorySettings : ISettings
    {
        public bool Contains(string name) => false;
        public string GetValue(string name, string value) => value;
        public void SetValue(string name, string value) { }
        public void Remove(string name) { }
    }

    private sealed class ForeignSensor(IHardware hardware) : ISensor
    {
        public bool ThrowOnReset { get; init; }
        public int ResetCalls { get; private set; }
        public IControl Control => null!;
        public IHardware Hardware => hardware;
        public Identifier Identifier => new("foreign-fixture");
        public int Index => 0;
        public bool IsDefaultHidden => false;
        public float? Min { get; private set; } = 60;
        public float? Max { get; private set; } = 60;
        public string Name { get; set; } = "GPU Core";
        public IReadOnlyList<IParameter> Parameters => [];
        public SensorType SensorType => SensorType.Temperature;
        public float? Value => 60;
        public IEnumerable<SensorValue> Values => [];
        public TimeSpan ValuesTimeWindow { get; set; }
        public void ResetMin() { ResetCalls++; if (ThrowOnReset) throw new InvalidOperationException("managed reset failure"); Min = null; }
        public void ResetMax() { ResetCalls++; Max = null; }
        public void ClearValues() { }
        public void Accept(IVisitor visitor) { }
        public void Traverse(IVisitor visitor) { }
    }
}
