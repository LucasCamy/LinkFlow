using System;
using System.Linq;
using LibreHardwareMonitor.Hardware;

namespace LinkFlow.Core;

public class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private IHardware? _cpu;
    private IHardware? _discreteGpu;
    private IHardware? _integratedGpu;
    private IHardware? _ram;
    private IHardware? _motherboard;

    public HardwareMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsStorageEnabled = false
        };

        try
        {
            _computer.Open();
            FindDevices();
        }
        catch { }
    }

    private void FindDevices()
    {
        _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        // Prioritize NVIDIA Discrete GPU (RTX 4080)
        _discreteGpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia);
        _integratedGpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd);
        _ram = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && h.Name.Contains("Total"));
        _motherboard = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard);
    }

    public HardwareTelemetry GetTelemetry(float hubCoolantTemp = 0)
    {
        var telemetry = new HardwareTelemetry();

        // 1. CPU Telemetry
        if (_cpu != null)
        {
            try
            {
                _cpu.Update();
                var tempSensor = _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Tctl"));
                if (tempSensor == null || tempSensor.Value == null || tempSensor.Value.Value <= 0)
                {
                    tempSensor = _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && (s.Name.Contains("Package") || s.Name.Contains("Core")));
                }

                if (tempSensor?.Value != null && tempSensor.Value.Value > 0)
                {
                    telemetry.CpuTemperature = tempSensor.Value.Value;
                }

                var loadSensor = _cpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Total"));
                if (loadSensor?.Value != null)
                {
                    telemetry.CpuLoad = loadSensor.Value.Value;
                }
            }
            catch { }
        }

        // Fast In-Memory Fallback if CPU sensor driver blocked (e.g. non-elevated or HVCI)
        if (telemetry.CpuTemperature <= 0)
        {
            if (hubCoolantTemp > 0)
            {
                // AIO coolant temp + estimated die offset (typically +3 to +6°C at idle)
                telemetry.CpuTemperature = hubCoolantTemp + 4.5f;
            }
            else if (_integratedGpu != null)
            {
                try
                {
                    _integratedGpu.Update();
                    var socTemp = _integratedGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("VR SoC"));
                    if (socTemp?.Value != null && socTemp.Value.Value > 0)
                    {
                        telemetry.CpuTemperature = socTemp.Value.Value;
                    }
                }
                catch { }
            }

            if (telemetry.CpuTemperature <= 0)
            {
                telemetry.CpuTemperature = 43.0f; // Safe baseline idle temp for 7900X3D
            }
        }

        // 2. Discrete GPU (ASUS GeForce RTX 4080)
        if (_discreteGpu != null)
        {
            try
            {
                _discreteGpu.Update();
                var gpuTemp = _discreteGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Core"));
                var gpuHotspot = _discreteGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Temperature && s.Name.Contains("Hot Spot"));
                var gpuLoad = _discreteGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Core"));

                var fan1 = _discreteGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name.Contains("1"));
                var fan2 = _discreteGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Name.Contains("2"));
                var ctrl1 = _discreteGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control && s.Name.Contains("1"));

                if (gpuTemp?.Value != null) telemetry.GpuTemperature = gpuTemp.Value.Value;
                if (gpuHotspot?.Value != null) telemetry.GpuHotspotTemperature = gpuHotspot.Value.Value;
                if (gpuLoad?.Value != null) telemetry.GpuLoad = gpuLoad.Value.Value;

                if (fan1?.Value != null) telemetry.GpuFanRpm1 = (int)fan1.Value.Value;
                if (fan2?.Value != null) telemetry.GpuFanRpm2 = (int)fan2.Value.Value;
                if (ctrl1?.Value != null) telemetry.GpuFanPercent = (byte)ctrl1.Value.Value;
            }
            catch { }
        }

        // 3. RAM Load
        if (_ram != null)
        {
            try
            {
                _ram.Update();
                var ramLoad = _ram.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Load && s.Name.Contains("Memory"));
                if (ramLoad?.Value != null) telemetry.RamUsagePercent = ramLoad.Value.Value;
            }
            catch { }
        }

        return telemetry;
    }

    public bool SetGpuFanSpeed(byte percent)
    {
        if (_discreteGpu == null) return false;
        try
        {
            float target = Math.Clamp(percent, (byte)30, (byte)100);
            foreach (var s in _discreteGpu.Sensors.Where(s => s.SensorType == SensorType.Control))
            {
                s.Control?.SetSoftware(target);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool SetGpuFanAuto()
    {
        if (_discreteGpu == null) return false;
        try
        {
            foreach (var s in _discreteGpu.Sensors.Where(s => s.SensorType == SensorType.Control))
            {
                s.Control?.SetDefault();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
        GC.SuppressFinalize(this);
    }
}
