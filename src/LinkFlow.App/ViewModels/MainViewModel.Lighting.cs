using System.Diagnostics;
using System.Windows.Input;
using LinkFlow.Core;
using Microsoft.Win32;

namespace LinkFlow.App.ViewModels;

/// <summary>Correção de cor dos LEDs, brilho por dispositivo e conflito com a Iluminação Dinâmica do Windows.</summary>
public partial class MainViewModel
{
    private float _ledGamma = 2f;
    private readonly Dictionary<string, byte> _deviceBrightness = new();
    private bool _dynamicLightingOn;
    private ICommand? _openDynamicLightingCommand;

    /// <summary>Intensidade da correção (0 = desligada, 100 = gama 2,2).</summary>
    public double LedVividness
    {
        get => Math.Round((_ledGamma - 1f) / 1.2f * 100);
        set
        {
            float gamma = (float)(1 + Math.Clamp(value, 0, 100) / 100 * 1.2);
            if (Math.Abs(gamma - _ledGamma) < 0.005f) return;
            _ledGamma = gamma;
            OnPropertiesChanged(nameof(LedVividness), nameof(LedVividnessText));
            if (!IsLoadingConfig) _engine.UpdateConfig(c => c.Rgb.LedGamma = gamma);
        }
    }

    public string LedVividnessText => LedVividness < 1 ? "Desligada" : $"{LedVividness:0}%";

    private byte GetDeviceBrightness(string key) => _deviceBrightness.TryGetValue(key, out var v) ? v : (byte)100;

    private void SetDeviceBrightness(string key, byte value, string property)
    {
        if (GetDeviceBrightness(key) == value) return;
        _deviceBrightness[key] = value;
        OnPropertyChanged(property);
        if (!IsLoadingConfig) _engine.UpdateConfig(c => c.Rgb.DeviceBrightness[key] = value);
    }

    public byte HubBrightness { get => GetDeviceBrightness(RgbTargets.Hub); set => SetDeviceBrightness(RgbTargets.Hub, value, nameof(HubBrightness)); }
    public byte GpuBrightness { get => GetDeviceBrightness(RgbTargets.Gpu); set => SetDeviceBrightness(RgbTargets.Gpu, value, nameof(GpuBrightness)); }
    public byte MotherboardBrightness { get => GetDeviceBrightness(RgbTargets.Motherboard); set => SetDeviceBrightness(RgbTargets.Motherboard, value, nameof(MotherboardBrightness)); }
    public byte RamBrightness { get => GetDeviceBrightness(RgbTargets.Ram); set => SetDeviceBrightness(RgbTargets.Ram, value, nameof(RamBrightness)); }
    public byte PeripheralsBrightness { get => GetDeviceBrightness(RgbTargets.Peripherals); set => SetDeviceBrightness(RgbTargets.Peripherals, value, nameof(PeripheralsBrightness)); }

    /// <summary>Iluminação Dinâmica do Windows ligada: ela assume teclado/mouse compatíveis e briga com o G HUB.</summary>
    public bool IsDynamicLightingOn
    {
        get => _dynamicLightingOn;
        private set => SetProperty(ref _dynamicLightingOn, value);
    }

    public ICommand OpenDynamicLightingCommand => _openDynamicLightingCommand ??= new RelayCommand(() =>
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:personalization-lighting") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"Não foi possível abrir as configurações de iluminação: {ex.Message}"); }
    });

    /// <summary>Lê o registro (barato); chamado ao abrir a página de Iluminação.</summary>
    public void RefreshDynamicLighting()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting");
            IsDynamicLightingOn = key?.GetValue("AmbientLightingEnabled") is int v && v != 0;
        }
        catch
        {
            IsDynamicLightingOn = false;
        }
    }

    private void LoadLightingExtras(ControlConfig c)
    {
        if (Math.Abs(_ledGamma - c.Rgb.LedGamma) > 0.001f)
        {
            _ledGamma = c.Rgb.LedGamma;
            OnPropertiesChanged(nameof(LedVividness), nameof(LedVividnessText));
        }
        foreach (var (key, prop) in new[]
                 {
                     (RgbTargets.Hub, nameof(HubBrightness)), (RgbTargets.Gpu, nameof(GpuBrightness)),
                     (RgbTargets.Motherboard, nameof(MotherboardBrightness)), (RgbTargets.Ram, nameof(RamBrightness)),
                     (RgbTargets.Peripherals, nameof(PeripheralsBrightness))
                 })
        {
            byte v = c.Rgb.DeviceBrightness.TryGetValue(key, out var b) ? b : (byte)100;
            if (GetDeviceBrightness(key) != v)
            {
                _deviceBrightness[key] = v;
                OnPropertyChanged(prop);
            }
        }
    }
}
