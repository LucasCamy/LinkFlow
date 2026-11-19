using System.Diagnostics;
using System.Windows.Input;
using LinkFlow.Core;
using Microsoft.Win32;
using LinkFlow.App.Services;
using LinkFlow.App.Resources;

namespace LinkFlow.App.ViewModels;

/// <summary>Correção de cor dos LEDs, brilho por dispositivo e conflito com a Iluminação Dinâmica do Windows.</summary>
public partial class MainViewModel
{
    private float _ledGamma = 2f;
    private readonly Dictionary<string, byte> _deviceBrightness = new();
    private bool _dynamicLightingOn;
    private ICommand? _openDynamicLightingCommand;
    private string? _gpuLightingConflict;
    private string? _peripheralsLightingConflict;
    private (string Status, bool HasIssue)? _externalPeripherals;

    internal event Action? RgbRefreshRequested;

    public string GpuRgbStatus => !SyncGpu ? Strings.Lit_SincronizacaoDesligada :
        IsEngineDisconnected ? Strings.Lit_AguardandoConexaoServicoLinkFlow :
        !Snapshot.RgbDevicesReady ? Strings.Lit_DetectandoControlador :
        Snapshot.GpuRgbRetrying ? Strings.Lit_GPUSemRespostaTentando :
        !Snapshot.GpuRgbAvailable ? Strings.Lit_ControleRGBIndisponivel :
        Snapshot.GpuRgbApplied switch
        {
            true => string.Format(Strings.Lit_CorEnviada, Snapshot.GpuRgbName),
            false => string.Format(Strings.Lit_EnvioRecusado, Snapshot.GpuRgbName),
            _ => string.Format(Strings.Lit_ControladorDetectado, Snapshot.GpuRgbName)
        };

    public string? GpuRgbIssue
    {
        get
        {
            if (!SyncGpu || IsEngineDisconnected || !Snapshot.RgbDevicesReady) return null;
            string? diagnostic = Snapshot.GpuRgbDiagnostic is { } gpu ? EngineTextUi.Localize(Snapshot.GpuRgbDiagnosticText, gpu) : null;
            if (Snapshot.GpuRgbRetrying) return diagnostic;
            return diagnostic ?? (Snapshot.GpuRgbAvailable
                ? Snapshot.GpuRgbApplied == false ? Strings.Lit_ControladorPlacaRecusouEnvio : null
                : Strings.Lit_NenhumControladorASUSAura);
        }
    }

    public string PeripheralsRgbStatus
    {
        get
        {
            if (!SyncPeripherals) return Strings.Lit_SincronizacaoDesligada;
            if (_externalPeripherals is { } bridge) return bridge.Status;
            if (_engine.IsRemote || !Snapshot.RgbDevicesReady) return Strings.Lit_VerificandoHUBSessaoWindows;
            return (Snapshot.PeripheralsRgbDiagnostic is { } peripherals
                    ? EngineTextUi.Localize(Snapshot.PeripheralsRgbDiagnosticText, peripherals) : null)
                ?? (!Snapshot.PeripheralsRgbAvailable ? Strings.Lit_ControleRGBPeloHUB :
                Snapshot.PeripheralsRgbApplied switch
                {
                    true => Strings.Lit_CorEnviadaPeloSDK,
                    false => Strings.Lit_SDKHUBRecusouCor,
                    _ => Strings.Lit_SDKHUBConectadoAguardando
                });
        }
    }

    public bool HasPeripheralsRgbIssue => SyncPeripherals && (_externalPeripherals?.HasIssue ??
        (!_engine.IsRemote && Snapshot.RgbDevicesReady &&
         (!Snapshot.PeripheralsRgbAvailable || Snapshot.PeripheralsRgbApplied == false || Snapshot.PeripheralsRgbDiagnostic != null)));

    public string? GpuLightingConflict => SyncGpu ? _gpuLightingConflict : null;
    public string? PeripheralsLightingConflict => SyncPeripherals ? _peripheralsLightingConflict : null;
    public bool HasLightingAttention => GpuRgbIssue != null || HasPeripheralsRgbIssue ||
        GpuLightingConflict != null || PeripheralsLightingConflict != null;

    internal void SetExternalPeripheralsStatus(string status, bool hasIssue)
    {
        _externalPeripherals = (status, hasIssue);
        NotifyLightingStatus();
    }

    private void NotifyLightingStatus() => OnPropertiesChanged(nameof(GpuRgbStatus), nameof(GpuRgbIssue),
        nameof(PeripheralsRgbStatus), nameof(HasPeripheralsRgbIssue), nameof(HasLightingAttention));

    private void RefreshRgb()
    {
        RefreshLightingDiagnostics();
        _engine.RefreshRgb();
        RgbRefreshRequested?.Invoke();
        ShowToast(Strings.Lit_ReenviandoIluminacaoDispositivosSelecionados, canUndo: false);
    }

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

    public string LedVividnessText => LedVividness < 1 ? Strings.Lit_Desligada : $"{LedVividness:0}%";

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

    public void RefreshLightingDiagnostics()
    {
        if (_engine.IsSimulated) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Lighting");
            IsDynamicLightingOn = key?.GetValue("AmbientLightingEnabled") is int v && v != 0;
        }
        catch
        {
            IsDynamicLightingOn = false;
        }

        try
        {
            var common = new List<string>();
            if (IsRunning("OpenRGB")) common.Add("OpenRGB");
            if (IsRunning("SignalRgb") || IsRunning("WhirlwindFX")) common.Add("SignalRGB");
            _peripheralsLightingConflict = DescribeConflict(common);
            if (IsRunning("LightingService") || IsRunning("Aura")) common.Add("ASUS Aura");
            _gpuLightingConflict = DescribeConflict(common);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _gpuLightingConflict = _peripheralsLightingConflict = null;
            Log.Debug($"Consulta de controladores RGB em execução: {ex.Message}");
        }
        OnPropertiesChanged(nameof(GpuLightingConflict), nameof(PeripheralsLightingConflict), nameof(HasLightingAttention));
    }

    private static string? DescribeConflict(List<string> controllers) => controllers.Count == 0 ? null :
        string.Format(Strings.Lit_ConflictRunning, string.Join(", ", controllers));

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
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
