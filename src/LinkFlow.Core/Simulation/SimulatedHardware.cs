namespace LinkFlow.Core;

/// <summary>
/// Hardware simulado (10 fans RX + bomba Titan, sensores, LCD e RGB) para testar a interface
/// e o engine sem o equipamento real: <c>LinkFlow.App.exe --simulate</c>.
/// </summary>
public sealed class SimulatedHardwareFactory : IHardwareFactory
{
    public SimulatedHub Hub { get; } = new();
    public SimulatedSensors Sensors { get; } = new();
    public SimulatedLcd Lcd { get; } = new();
    public SimulatedRgb GpuRgb { get; } = new("ASUS Aura (simulado)");
    public SimulatedRgb MotherboardRgb { get; } = new("MSI Mystic Light (simulado)");

    public bool IsSimulated => true;
    public IFanHub CreateHub() => Hub;
    public ISensorSource CreateSensors() => Sensors;
    public ILcdDisplay CreateLcd() => Lcd;
    public IRgbDevice CreateGpuRgb() => GpuRgb;
    public IRgbDevice CreateMotherboardRgb() => MotherboardRgb;
}

public sealed class SimulatedHub : IFanHub
{
    private readonly object _lock = new();
    private readonly Random _rnd = new(42);
    private readonly List<ConnectedSubDevice> _devices;
    private readonly Dictionary<int, double> _rpm = new();

    public bool Present { get; set; } = true;
    public bool FailReads { get; set; }
    public HashSet<int> StoppedChannels { get; } = new();
    public int SetSpeedsCalls { get; private set; }
    public int SetRgbCalls { get; private set; }
    public int ConnectCalls { get; private set; }
    public IReadOnlyDictionary<int, byte> LastSpeeds { get; private set; } = new Dictionary<int, byte>();
    public (byte R, byte G, byte B, byte Brightness)? LastRgb { get; private set; }
    public bool InSoftwareMode { get; private set; }
    public float CoolantTemp { get; set; } = 32.5f;

    public SimulatedHub()
    {
        _devices = new List<ConnectedSubDevice>();
        foreach (var s in (Environment.GetEnvironmentVariable("LINKFLOW_SIM_STOPPED") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (int.TryParse(s, out var stopped)) StoppedChannels.Add(stopped);
        for (int ch = 1; ch <= 10; ch++)
        {
            _devices.Add(new ConnectedSubDevice
            {
                Channel = ch, DeviceId = $"SIM-RX-{ch:D2}", Model = LinkDeviceModel.FanRxRgbSeries, Name = $"RX Fan #{ch}"
            });
        }
        _devices.Add(new ConnectedSubDevice
        {
            Channel = 11, DeviceId = "SIM-TITAN", Model = LinkDeviceModel.LiquidCoolerTitanSeries, Name = "Titan AIO Pump", IsPump = true
        });
    }

    public bool IsConnected { get; private set; }
    public string FirmwareVersion => "2.6.201 (sim)";
    public IReadOnlyList<ConnectedSubDevice> SubDevices => IsConnected ? _devices : Array.Empty<ConnectedSubDevice>();

    public bool Connect()
    {
        lock (_lock)
        {
            ConnectCalls++;
            IsConnected = Present;
            InSoftwareMode = Present;
            return Present;
        }
    }

    public void Disconnect(bool restoreHardwareMode = true)
    {
        lock (_lock)
        {
            if (restoreHardwareMode) InSoftwareMode = false;
            IsConnected = false;
        }
    }

    public IReadOnlyDictionary<int, int>? ReadSpeeds()
    {
        lock (_lock)
        {
            if (!IsConnected || FailReads) return null;
            var result = new Dictionary<int, int>();
            foreach (var d in _devices)
            {
                byte pct = LastSpeeds.TryGetValue(d.Channel, out var p) ? p : (byte)50;
                double target = StoppedChannels.Contains(d.Channel) ? 0 : d.IsPump ? 1200 + pct * 18 : 250 + pct * 19;
                _rpm.TryGetValue(d.Channel, out var cur);
                cur += (target - cur) * 0.6;
                _rpm[d.Channel] = cur;
                int rpm = target == 0 ? 0 : (int)(cur + _rnd.Next(-12, 12));
                if (rpm > 0) result[d.Channel] = rpm;
            }
            return result;
        }
    }

    public IReadOnlyDictionary<int, float>? ReadTemperatures()
    {
        lock (_lock)
        {
            if (!IsConnected || FailReads) return null;
            return new Dictionary<int, float> { [11] = CoolantTemp + (float)(_rnd.NextDouble() * 0.2) };
        }
    }

    public bool SetSpeedsPercent(IReadOnlyDictionary<int, byte> channelSpeeds)
    {
        lock (_lock)
        {
            if (!IsConnected) return false;
            SetSpeedsCalls++;
            var merged = new Dictionary<int, byte>(LastSpeeds);
            foreach (var kv in channelSpeeds) merged[kv.Key] = kv.Value;
            LastSpeeds = merged;
            return true;
        }
    }

    public bool SetGlobalRgb(byte r, byte g, byte b, byte brightnessPercent)
    {
        lock (_lock)
        {
            if (!IsConnected) return false;
            SetRgbCalls++;
            LastRgb = (r, g, b, brightnessPercent);
            Thread.Sleep(5);
            return true;
        }
    }

    public void Dispose() => Disconnect();
}

public sealed class SimulatedSensors : ISensorSource
{
    private readonly DateTime _start = DateTime.Now;
    public bool IsOpen { get; private set; }
    public float? ForcedCpuTemp { get; set; }
    public float? ForcedGpuTemp { get; set; }
    public bool CpuAvailable { get; set; } = true;
    public byte? GpuFanManual { get; private set; }
    public TimeSpan OpenDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public void Open()
    {
        Thread.Sleep(OpenDelay);
        IsOpen = true;
    }

    public HardwareTelemetry Read()
    {
        double s = (DateTime.Now - _start).TotalSeconds;
        float load = (float)(25 + 20 * Math.Sin(s / 9));
        return new HardwareTelemetry
        {
            CpuName = "AMD Ryzen 9 7900X3D (simulado)",
            GpuName = "NVIDIA GeForce RTX 4080 (simulado)",
            CpuSensorAvailable = CpuAvailable,
            CpuTemperature = CpuAvailable ? ForcedCpuTemp ?? (float)(52 + 12 * Math.Sin(s / 15)) : null,
            CpuLoad = load,
            GpuTemperature = ForcedGpuTemp ?? (float)(46 + 8 * Math.Sin(s / 21)),
            GpuHotspotTemperature = (float)(56 + 8 * Math.Sin(s / 21)),
            GpuLoad = (float)(15 + 10 * Math.Sin(s / 7)),
            RamUsagePercent = 41.3f,
            RamUsedGb = 26.4f,
            RamTotalGb = 63.9f,
            GpuFanRpm1 = GpuFanManual is { } p ? 600 + p * 22 : 1180,
            GpuFanRpm2 = GpuFanManual is { } p2 ? 610 + p2 * 22 : 1192,
            GpuFanPercent = GpuFanManual,
            CpuPower = 45 + load * 1.2f,
            CpuClockMhz = 4200 + load * 8,
            CpuCores = 12,
            GpuPower = 60 + load * 2.5f,
            GpuClockMhz = 2205 + load * 3,
            GpuMemClockMhz = 11201,
            GpuVramUsedGb = 3.4f + load / 50f,
            GpuVramTotalGb = 16f
        };
    }

    public bool SetGpuFanSpeed(byte percent) { GpuFanManual = percent; return true; }
    public bool SetGpuFanAuto() { GpuFanManual = null; return true; }
    public void Dispose() => IsOpen = false;
}

public sealed class SimulatedLcd : ILcdDisplay
{
    public bool IsConnected { get; private set; }
    public int Frames { get; private set; }
    public byte[]? LastFrame { get; private set; }
    public bool Connect() => IsConnected = true;
    public void Disconnect() => IsConnected = false;
    public bool SendJpegFrame(byte[] jpegBytes) { Frames++; LastFrame = jpegBytes; return IsConnected; }
    public void Dispose() => Disconnect();
}

public sealed class SimulatedRgb : IRgbDevice
{
    public SimulatedRgb(string name) => Name = name;
    public string Name { get; }
    public bool IsAvailable { get; private set; }
    public int Calls { get; private set; }
    public (byte R, byte G, byte B, byte Brightness)? Last { get; private set; }
    public void Initialize() => IsAvailable = true;
    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent) { Calls++; Last = (r, g, b, brightnessPercent); return true; }
    public void Dispose() { }
}
