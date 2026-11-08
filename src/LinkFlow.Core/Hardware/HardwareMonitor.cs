using LibreHardwareMonitor.Hardware;

namespace LinkFlow.Core;

/// <summary>
/// Leitura de sensores via LibreHardwareMonitor. <see cref="Open"/> é lento (carrega driver),
/// por isso é chamado pelo engine numa thread de fundo e nunca no construtor.
/// </summary>
public sealed class HardwareMonitor : ISensorSource
{
    private readonly object _lock = new();
    private Computer? _computer;
    private IHardware? _cpu;
    private IHardware? _discreteGpu;
    private IHardware? _ram;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        lock (_lock)
        {
            if (IsOpen) return;

            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = false,
                IsControllerEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = false
            };

            try
            {
                _computer.Open();
            }
            catch (Exception ex)
            {
                Log.Warn($"LibreHardwareMonitor não abriu: {ex.Message}");
            }

            _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            _discreteGpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                           ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd && !IsIntegrated(h))
                           ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);
            _ram = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && h.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                   ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Memory && !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));

            IsOpen = true;
            Log.Info($"Sensores: CPU={_cpu?.Name ?? "-"} GPU={_discreteGpu?.Name ?? "-"} RAM={_ram?.Name ?? "-"}");
        }
    }

    private static bool IsIntegrated(IHardware h) =>
        h.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
        h.Name.Equals("AMD Radeon Graphics", StringComparison.OrdinalIgnoreCase);

    public HardwareTelemetry Read()
    {
        var t = new HardwareTelemetry();
        lock (_lock)
        {
            if (!IsOpen) return t;

            if (_cpu != null)
            {
                t.CpuName = _cpu.Name;
                try
                {
                    _cpu.Update();
                    var temp = Find(_cpu, SensorType.Temperature, "Tctl")
                               ?? Find(_cpu, SensorType.Temperature, "Package")
                               ?? Find(_cpu, SensorType.Temperature, "Core");
                    if (temp?.Value is > 0 and < 150)
                    {
                        t.CpuTemperature = temp.Value;
                        t.CpuSensorAvailable = true;
                    }

                    var load = Find(_cpu, SensorType.Load, "Total");
                    if (load?.Value != null) t.CpuLoad = load.Value;
                }
                catch (Exception ex) { Log.Debug($"CPU update: {ex.Message}"); }
            }

            if (_discreteGpu != null)
            {
                t.GpuName = _discreteGpu.Name;
                try
                {
                    _discreteGpu.Update();
                    var temp = Find(_discreteGpu, SensorType.Temperature, "Core");
                    var hot = Find(_discreteGpu, SensorType.Temperature, "Hot Spot");
                    var load = Find(_discreteGpu, SensorType.Load, "Core");
                    var fans = _discreteGpu.Sensors.Where(s => s.SensorType == SensorType.Fan).OrderBy(s => s.Index).ToList();
                    var ctrl = _discreteGpu.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Control);

                    if (temp?.Value is > 0) t.GpuTemperature = temp.Value;
                    if (hot?.Value is > 0) t.GpuHotspotTemperature = hot.Value;
                    if (load?.Value != null) t.GpuLoad = load.Value;
                    if (fans.Count > 0 && fans[0].Value != null) t.GpuFanRpm1 = (int)fans[0].Value!.Value;
                    if (fans.Count > 1 && fans[1].Value != null) t.GpuFanRpm2 = (int)fans[1].Value!.Value;
                    if (ctrl?.Value != null) t.GpuFanPercent = ctrl.Value;
                }
                catch (Exception ex) { Log.Debug($"GPU update: {ex.Message}"); }
            }

            if (_ram != null)
            {
                try
                {
                    _ram.Update();
                    var load = Find(_ram, SensorType.Load, "Memory");
                    var used = Find(_ram, SensorType.Data, "Used");
                    var avail = Find(_ram, SensorType.Data, "Available");
                    if (load?.Value != null) t.RamUsagePercent = load.Value;
                    if (used?.Value != null) t.RamUsedGb = used.Value;
                    if (used?.Value != null && avail?.Value != null) t.RamTotalGb = used.Value + avail.Value;
                }
                catch (Exception ex) { Log.Debug($"RAM update: {ex.Message}"); }
            }
        }

        return t;
    }

    private static ISensor? Find(IHardware hw, SensorType type, string nameContains) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

    public bool SetGpuFanSpeed(byte percent)
    {
        lock (_lock)
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
            catch (Exception ex)
            {
                Log.Warn($"Falha ao definir fan da GPU: {ex.Message}");
                return false;
            }
        }
    }

    public bool SetGpuFanAuto()
    {
        lock (_lock)
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
            catch (Exception ex)
            {
                Log.Warn($"Falha ao restaurar fan da GPU: {ex.Message}");
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
            IsOpen = false;
        }
    }
}
