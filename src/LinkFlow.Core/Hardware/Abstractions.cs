namespace LinkFlow.Core;

/// <summary>Hub de ventoinhas/bomba (ex.: Corsair iCUE LINK).</summary>
public interface IFanHub : IDisposable
{
    bool IsConnected { get; }
    string FirmwareVersion { get; }
    IReadOnlyList<ConnectedSubDevice> SubDevices { get; }

    bool Connect();
    void Disconnect(bool restoreHardwareMode = true);

    /// <summary>RPM por canal. Retorna null quando a leitura falha.</summary>
    IReadOnlyDictionary<int, int>? ReadSpeeds();

    /// <summary>Temperaturas por canal (°C). Retorna null quando a leitura falha.</summary>
    IReadOnlyDictionary<int, float>? ReadTemperatures();

    bool SetSpeedsPercent(IReadOnlyDictionary<int, byte> channelSpeeds);
    bool SetGlobalRgb(byte r, byte g, byte b, byte brightnessPercent);
}

/// <summary>Sensores do sistema (CPU, GPU, RAM) e controle da ventoinha da GPU.</summary>
public interface ISensorSource : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Pode demorar alguns segundos (carrega driver). Nunca chamar na thread da UI.</summary>
    void Open();

    HardwareTelemetry Read();
    bool SetGpuFanSpeed(byte percent);
    bool SetGpuFanAuto();
}

/// <summary>Display LCD da bomba.</summary>
public interface ILcdDisplay : IDisposable
{
    bool IsConnected { get; }
    bool Connect();
    void Disconnect();
    bool SendJpegFrame(byte[] jpegBytes);
}

/// <summary>Dispositivo RGB simples de cor única (GPU, placa-mãe...).</summary>
public interface IRgbDevice : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }

    /// <summary>Detecção do dispositivo. Pode ser lenta (I2C/HID).</summary>
    void Initialize();

    bool SetColor(byte r, byte g, byte b, byte brightnessPercent);
}

public interface IHardwareFactory
{
    bool IsSimulated { get; }
    IFanHub CreateHub();
    ISensorSource CreateSensors();
    ILcdDisplay CreateLcd();
    IRgbDevice CreateGpuRgb();
    IRgbDevice CreateMotherboardRgb();
}

public sealed class RealHardwareFactory : IHardwareFactory
{
    public bool IsSimulated => false;
    public IFanHub CreateHub() => new CorsairLinkHub();
    public ISensorSource CreateSensors() => new HardwareMonitor();
    public ILcdDisplay CreateLcd() => new CorsairLinkLcd();
    public IRgbDevice CreateGpuRgb() => new AsusGpuRgb();
    public IRgbDevice CreateMotherboardRgb() => new MsiMotherboardRgb();
}
