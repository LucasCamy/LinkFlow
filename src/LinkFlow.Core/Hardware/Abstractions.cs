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

    /// <summary>Uma cor por LED (R,G,B na ordem da corrente); usado pelos efeitos espalhados.</summary>
    bool SetRgbLeds(ReadOnlySpan<byte> colors, byte brightnessPercent);

    /// <summary>Relê a lista de dispositivos ligados ao hub. True se mudou (ventoinha conectada/removida).</summary>
    bool RefreshSubDevices();
}

/// <summary>Sensores do sistema (CPU, GPU, RAM) e controle da ventoinha da GPU.</summary>
public interface ISensorSource : IDisposable
{
    bool IsOpen { get; }

    /// <summary>Pode demorar alguns segundos (carrega driver). Nunca chamar na thread da UI.</summary>
    void Open();

    HardwareTelemetry Read();

    /// <summary>Liga a leitura dos discos só quando algo exibe (custa mais que os outros sensores).</summary>
    void SetStorageEnabled(bool enabled);
    List<DiskIoState>? ReadDisks();
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
    string? Diagnostic => null;
    bool NeedsInitializationRetry => false;
    TimeSpan? MinAnimationInterval => null;

    /// <summary>Detecção do dispositivo. Pode ser lenta (I2C/HID).</summary>
    void Initialize();

    bool SetColor(byte r, byte g, byte b, byte brightnessPercent);
    bool SetAnimationColor(byte r, byte g, byte b, byte brightnessPercent) => SetColor(r, g, b, brightnessPercent);
}

public interface IHardwareFactory
{
    bool IsSimulated { get; }
    IFanHub CreateHub();
    ISensorSource CreateSensors();
    ILcdDisplay CreateLcd();
    IRgbDevice CreateGpuRgb();
    IRgbDevice CreateMotherboardRgb();
    IRgbDevice CreateRamRgb();
    IRgbDevice CreatePeripheralsRgb();
}

public sealed class RealHardwareFactory : IHardwareFactory
{
    public bool IsSimulated => false;
    public IFanHub CreateHub() => new CorsairLinkHub();
    public ISensorSource CreateSensors() => new HardwareMonitor();
    public ILcdDisplay CreateLcd() => new CorsairLinkLcd();
    public IRgbDevice CreateGpuRgb() => new AsusGpuRgb();
    public IRgbDevice CreateMotherboardRgb() => new MsiMotherboardRgb();
    public IRgbDevice CreateRamRgb() => new CorsairDramRgb();
    /// <summary>
    /// O G HUB só aceita o SDK de iluminação de processos na sessão do usuário. Como serviço (sessão 0)
    /// os periféricos ficam a cargo da interface (<c>PeripheralRgbBridge</c>).
    /// </summary>
    public IRgbDevice CreatePeripheralsRgb() => System.Diagnostics.Process.GetCurrentProcess().SessionId == 0
        ? new UnavailableRgbDevice("Periféricos Logitech G (controlados pela interface)")
        : new LogitechPeripheralsRgb();
}

/// <summary>Dispositivo que não pode ser controlado neste processo.</summary>
public sealed class UnavailableRgbDevice : IRgbDevice
{
    public UnavailableRgbDevice(string name) => Name = name;
    public string Name { get; }
    public bool IsAvailable => false;
    public void Initialize() { }
    public bool SetColor(byte r, byte g, byte b, byte brightnessPercent) => false;
    public void Dispose() { }
}
