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

                    var cores = _cpu.Sensors.Where(s => s.SensorType == SensorType.Load && s.Name.StartsWith("CPU Core #", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(s => s.Index).Select(s => s.Value ?? 0f).ToArray();
                    if (cores.Length > 0) t.CpuCoreLoads = cores;

                    var power = Find(_cpu, SensorType.Power, "Package");
                    if (power?.Value is > 0) t.CpuPower = power.Value;
                    var clocks = _cpu.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && s.Value > 0).ToList();
                    if (clocks.Count > 0)
                    {
                        t.CpuClockMhz = clocks.Average(s => s.Value!.Value);
                        t.CpuCores = clocks.Count;
                    }
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

                    var power = Find(_discreteGpu, SensorType.Power, "Package") ?? Find(_discreteGpu, SensorType.Power, "GPU");
                    if (power?.Value is > 0) t.GpuPower = power.Value;
                    var coreClock = Find(_discreteGpu, SensorType.Clock, "Core");
                    if (coreClock?.Value is > 0) t.GpuClockMhz = coreClock.Value;
                    var memClock = Find(_discreteGpu, SensorType.Clock, "Memory");
                    if (memClock?.Value is > 0) t.GpuMemClockMhz = memClock.Value;
                    var memUsed = Find(_discreteGpu, SensorType.SmallData, "Memory Used");
                    var memTotal = Find(_discreteGpu, SensorType.SmallData, "Memory Total");
                    if (memUsed?.Value is > 0) t.GpuVramUsedGb = memUsed.Value / 1024f;
                    if (memTotal?.Value is > 0) t.GpuVramTotalGb = memTotal.Value / 1024f;
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

    private bool _storageEnabled;
    private int _storageTick;
    private List<DiskIoState>? _lastDisks;

    /// <summary>Liga/desliga a leitura dos discos (só quando algo na tela usa).</summary>
    public void SetStorageEnabled(bool enabled)
    {
        lock (_lock)
        {
            if (_computer == null || _storageEnabled == enabled) return;
            try
            {
                _computer.IsStorageEnabled = enabled;
                _storageEnabled = enabled;
                _lastDisks = null;
                Log.Info(enabled ? "Leitura de discos ativada (há algo exibindo)." : "Leitura de discos desativada (ninguém exibindo).");
            }
            catch (Exception ex) { Log.Warn($"Discos indisponíveis: {ex.Message}"); }
        }
    }

    /// <summary>Discos: atualizados a cada 2 leituras (~2 s) para não pesar.</summary>
    public List<DiskIoState>? ReadDisks()
    {
        lock (_lock)
        {
            if (_computer == null || !_storageEnabled) return null;
            if (_lastDisks != null && ++_storageTick % 2 != 0) return _lastDisks;
            var list = new List<DiskIoState>();
            int index = 0;
            foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
            {
                try
                {
                    hw.Update();
                    list.Add(new DiskIoState
                    {
                        Index = index++,
                        Name = hw.Name,
                        Temperature = Find(hw, SensorType.Temperature, "Temperature")?.Value,
                        ActivityPercent = Find(hw, SensorType.Load, "Total Activity")?.Value,
                        ReadBytesPerSec = Find(hw, SensorType.Throughput, "Read")?.Value,
                        WriteBytesPerSec = Find(hw, SensorType.Throughput, "Write")?.Value
                    });
                }
                catch (Exception ex) { Log.Debug($"Disco {hw.Name}: {ex.Message}"); }
            }
            return _lastDisks = list;
        }
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
