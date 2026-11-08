namespace LinkFlow.Core;

public enum LinkDeviceModel : byte
{
    FanQxSeries = 0x01,
    FanLxSeries = 0x02,
    FanRxMaxRgbSeries = 0x03,
    FanRxMaxSeries = 0x04,
    LiquidCoolerHSeries = 0x07,
    WaterBlockXc7Series = 0x09,
    WaterBlockXg3Series = 0x0a,
    PsuHxiShiftSeries = 0x0b,
    PumpXd5Series = 0x0c,
    FanRxRgbSeries = 0x0f,
    CapSwapModuleVrmFan = 0x10,
    LiquidCoolerTitanSeries = 0x11,
    FanRxSeries = 0x13,
    PumpXd6Series = 0x19,
    CommanderDuoSeries = 0x1b,
    Unknown = 0xFF
}

public class ConnectedSubDevice
{
    public int Channel { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public LinkDeviceModel Model { get; set; }
    public byte Variant { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPump { get; set; }
    public int CurrentRpm { get; set; }
    public float? CurrentTemperature { get; set; }
    public byte TargetPowerPercent { get; set; } = 50;
}

public class HardwareTelemetry
{
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }

    /// <summary>False quando o sensor de temperatura da CPU não está acessível (ex.: sem privilégio de administrador).</summary>
    public bool CpuSensorAvailable { get; set; }

    public float? CpuTemperature { get; set; }
    public float? CpuLoad { get; set; }
    public float? GpuTemperature { get; set; }
    public float? GpuHotspotTemperature { get; set; }
    public float? GpuLoad { get; set; }
    public float? RamUsagePercent { get; set; }
    public float? RamUsedGb { get; set; }
    public float? RamTotalGb { get; set; }
    public int? GpuFanRpm1 { get; set; }
    public int? GpuFanRpm2 { get; set; }
    public float? GpuFanPercent { get; set; }
}
