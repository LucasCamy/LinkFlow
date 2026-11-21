using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>The existing SDK session, abstracted so bridge tests never load a native SDK.</summary>
internal interface IPeripheralRgbBackend : IDisposable
{
    bool IsAvailable { get; }
    EngineText? DiagnosticText { get; }
    string? Diagnostic { get; }
    void Initialize();
    bool SetColor(byte r, byte g, byte b, byte brightness);
    (byte R, byte G, byte B, byte Brightness)? Resend(
        Func<(byte R, byte G, byte B, byte Brightness)?> readColor, Action<int, int> onAttempt);
    void Disconnect();
}

internal sealed class LogitechPeripheralRgbBackend(CancellationToken lifetimeToken) : IPeripheralRgbBackend
{
    private readonly LogitechPeripheralsRgb _device = new(lifetimeToken);
    public bool IsAvailable => _device.IsAvailable;
    public EngineText? DiagnosticText => _device.DiagnosticText;
    public string? Diagnostic => _device.Diagnostic;
    public void Initialize() => _device.Initialize();
    public bool SetColor(byte r, byte g, byte b, byte brightness) => _device.SetColor(r, g, b, brightness);
    public (byte R, byte G, byte B, byte Brightness)? Resend(
        Func<(byte R, byte G, byte B, byte Brightness)?> readColor, Action<int, int> onAttempt) =>
        _device.Resend(readColor, onAttempt);
    public void Disconnect() => _device.Disconnect();
    public void Dispose() => _device.Dispose();
}
