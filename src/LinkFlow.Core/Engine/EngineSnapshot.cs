namespace LinkFlow.Core;

public enum AlertSeverity { Info, Warning, Critical }

public sealed class EngineAlert
{
    public string Key { get; set; } = string.Empty;
    public AlertSeverity Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime Since { get; set; } = DateTime.Now;
}

public sealed class FanState
{
    public string Key { get; set; } = string.Empty;
    public int Channel { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DefaultName { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool IsPump { get; set; }
    public int Rpm { get; set; }
    public byte AppliedPercent { get; set; }
    public float? Temperature { get; set; }

    public static string KeyFor(ConnectedSubDevice d) =>
        string.IsNullOrWhiteSpace(d.DeviceId) ? $"ch{d.Channel}" : d.DeviceId;
}

/// <summary>Estado atual do hardware publicado periodicamente pelo engine (somente leitura).</summary>
public sealed class EngineSnapshot
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsSimulated { get; set; }

    // Hub
    public bool HubConnected { get; set; }
    public string HubFirmware { get; set; } = string.Empty;
    public string HubStatus { get; set; } = "Procurando iCUE LINK Hub...";

    // Sensores
    public bool SensorsReady { get; set; }
    public bool CpuSensorAvailable { get; set; }
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }
    public float? CpuTemp { get; set; }
    public float? CpuLoad { get; set; }
    public float? GpuTemp { get; set; }
    public float? GpuHotspot { get; set; }
    public float? GpuLoad { get; set; }
    public float? RamUsage { get; set; }
    public float? RamUsedGb { get; set; }
    public float? RamTotalGb { get; set; }
    public float? CoolantTemp { get; set; }
    public int? GpuFanRpm1 { get; set; }
    public int? GpuFanRpm2 { get; set; }
    public float? GpuFanPercent { get; set; }

    // Controle
    public byte EffectiveGlobalPercent { get; set; }
    public float? CurveInputTemp { get; set; }
    public bool FailSafeActive { get; set; }
    public List<FanState> Fans { get; set; } = new();

    // Periféricos
    public bool LcdConnected { get; set; }
    public bool GpuRgbAvailable { get; set; }
    public string GpuRgbName { get; set; } = string.Empty;
    public bool MotherboardRgbAvailable { get; set; }
    public string MotherboardRgbName { get; set; } = string.Empty;
    public bool RgbDevicesReady { get; set; }
    public string RgbStatus { get; set; } = string.Empty;

    public List<EngineAlert> Alerts { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public int? AverageFanRpm
    {
        get
        {
            var list = Fans.Where(f => !f.IsPump && f.Rpm > 0).ToList();
            return list.Count > 0 ? (int)list.Average(f => f.Rpm) : null;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public int? PumpRpm => Fans.FirstOrDefault(f => f.IsPump)?.Rpm;
}
