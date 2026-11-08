using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly CorsairLinkHub _hub;
    private readonly CorsairLinkLcd _lcd;
    private readonly HardwareMonitor _hwMonitor;
    private readonly AsusGpuRgb _gpuRgb;
    private readonly MsiMotherboardRgb _motherboardRgb;
    private readonly CancellationTokenSource _cts = new();

    private bool _isConnected;
    private string _connectionStatus = "Conectando ao iCUE LINK Hub...";
    private string _firmwareVersion = "0.0.0";
    private float _cpuTemp = 42.0f;
    private float _cpuLoad = 10.0f;
    private float _gpuTemp = 36.0f;
    private float _gpuLoad = 5.0f;
    private float _ramUsage = 38.0f;
    private float _coolantTemp = 41.5f;
    private byte _globalSpeedPercent = 50;
    private string _activePreset = "Equilibrado";
    private bool _isAutoCurve;

    // GPU Fan Controls
    private int _gpuFanRpm1 = 1000;
    private int _gpuFanRpm2 = 1000;
    private byte _gpuFanPercent = 35;
    private bool _isGpuFanAuto = true;

    // LCD Properties
    private bool _isLcdConnected;
    private string _lcdStatus = "Detectando módulo LCD (0x0C4E)...";
    private bool _isLcdAutoStream = true;

    // RGB Properties
    private byte _rgbR = 0;
    private byte _rgbG = 240;
    private byte _rgbB = 255;
    private byte _rgbBrightness = 100;
    private string _currentRgbHex = "#00F0FF";
    private string _rgbSyncStatus = "Pronto para sincronizar";
    private bool _syncFans = true;
    private bool _syncPump = true;
    private bool _syncLcd = true;
    private bool _syncGpu = true;
    private bool _syncMotherboard = true;
    private bool _syncRam = true;
    private string _gpuRgbStatus = "ASUS Aura Sync (Pronto)";
    private string _motherboardRgbStatus = "MSI Mystic Light (Pronto)";

    public ObservableCollection<FanViewModel> Fans { get; } = new();

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set { _connectionStatus = value; OnPropertyChanged(); }
    }

    public string FirmwareVersion
    {
        get => _firmwareVersion;
        set { _firmwareVersion = value; OnPropertyChanged(); }
    }

    public float CpuTemp
    {
        get => _cpuTemp;
        set { _cpuTemp = value; OnPropertyChanged(); OnPropertyChanged(nameof(CpuTempFormatted)); }
    }
    public string CpuTempFormatted => $"{_cpuTemp:F1}°C";

    public float CpuLoad
    {
        get => _cpuLoad;
        set { _cpuLoad = value; OnPropertyChanged(); OnPropertyChanged(nameof(CpuLoadFormatted)); }
    }
    public string CpuLoadFormatted => $"{_cpuLoad:F0}%";

    public float GpuTemp
    {
        get => _gpuTemp;
        set { _gpuTemp = value; OnPropertyChanged(); OnPropertyChanged(nameof(GpuTempFormatted)); }
    }
    public string GpuTempFormatted => $"{_gpuTemp:F1}°C";

    public float GpuLoad
    {
        get => _gpuLoad;
        set { _gpuLoad = value; OnPropertyChanged(); OnPropertyChanged(nameof(GpuLoadFormatted)); }
    }
    public string GpuLoadFormatted => $"{_gpuLoad:F0}%";

    public float RamUsage
    {
        get => _ramUsage;
        set { _ramUsage = value; OnPropertyChanged(); OnPropertyChanged(nameof(RamUsageFormatted)); }
    }
    public string RamUsageFormatted => $"{_ramUsage:F0}%";

    public float CoolantTemp
    {
        get => _coolantTemp;
        set { _coolantTemp = value; OnPropertyChanged(); OnPropertyChanged(nameof(CoolantTempFormatted)); }
    }
    public string CoolantTempFormatted => $"{_coolantTemp:F1}°C";

    // GPU Fan Getters / Setters
    public int GpuFanRpm1
    {
        get => _gpuFanRpm1;
        set { _gpuFanRpm1 = value; OnPropertyChanged(); OnPropertyChanged(nameof(GpuFanRpmFormatted)); }
    }

    public int GpuFanRpm2
    {
        get => _gpuFanRpm2;
        set { _gpuFanRpm2 = value; OnPropertyChanged(); OnPropertyChanged(nameof(GpuFanRpmFormatted)); }
    }

    public string GpuFanRpmFormatted => $"Fan 1: {_gpuFanRpm1} RPM  |  Fan 2: {_gpuFanRpm2} RPM";

    public byte GpuFanPercent
    {
        get => _gpuFanPercent;
        set
        {
            if (_gpuFanPercent != value)
            {
                _gpuFanPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GpuFanPercentFormatted));
                if (!_isGpuFanAuto)
                {
                    Task.Run(() => _hwMonitor.SetGpuFanSpeed(value));
                }
            }
        }
    }
    public string GpuFanPercentFormatted => $"{_gpuFanPercent}%";

    public bool IsGpuFanAuto
    {
        get => _isGpuFanAuto;
        set
        {
            if (_isGpuFanAuto != value)
            {
                _isGpuFanAuto = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GpuFanModeText));
                Task.Run(() =>
                {
                    if (value)
                    {
                        _hwMonitor.SetGpuFanAuto();
                    }
                    else
                    {
                        _hwMonitor.SetGpuFanSpeed(_gpuFanPercent);
                    }
                });
            }
        }
    }
    public string GpuFanModeText => _isGpuFanAuto ? "Automático (Curva vBIOS ASUS)" : "Manual (Controle LinkFlow)";

    // LCD Properties
    public bool IsLcdConnected
    {
        get => _isLcdConnected;
        set { _isLcdConnected = value; OnPropertyChanged(); }
    }

    public string LcdStatus
    {
        get => _lcdStatus;
        set { _lcdStatus = value; OnPropertyChanged(); }
    }

    public bool IsLcdAutoStream
    {
        get => _isLcdAutoStream;
        set { _isLcdAutoStream = value; OnPropertyChanged(); }
    }

    // Hub Fan Getters / Setters
    public byte GlobalSpeedPercent
    {
        get => _globalSpeedPercent;
        set
        {
            if (_globalSpeedPercent != value)
            {
                _globalSpeedPercent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GlobalSpeedFormatted));
                Task.Run(() => ApplyGlobalSpeed(value));
            }
        }
    }
    public string GlobalSpeedFormatted => $"{_globalSpeedPercent}%";

    public string ActivePreset
    {
        get => _activePreset;
        set { _activePreset = value; OnPropertyChanged(); }
    }

    public int AverageFanRpm
    {
        get
        {
            var fanList = Fans.Where(f => !f.IsPump && f.CurrentRpm > 0).ToList();
            return fanList.Any() ? (int)fanList.Average(f => f.CurrentRpm) : 0;
        }
    }

    public int PumpRpm
    {
        get
        {
            var pump = Fans.FirstOrDefault(f => f.IsPump);
            return pump?.CurrentRpm ?? 2950;
        }
    }

    // RGB Getters/Setters
    public byte RgbR
    {
        get => _rgbR;
        set { _rgbR = value; OnPropertyChanged(); UpdateHexFromRgb(); }
    }
    public byte RgbG
    {
        get => _rgbG;
        set { _rgbG = value; OnPropertyChanged(); UpdateHexFromRgb(); }
    }
    public byte RgbB
    {
        get => _rgbB;
        set { _rgbB = value; OnPropertyChanged(); UpdateHexFromRgb(); }
    }
    public byte RgbBrightness
    {
        get => _rgbBrightness;
        set { _rgbBrightness = value; OnPropertyChanged(); OnPropertyChanged(nameof(RgbBrightnessFormatted)); }
    }
    public string RgbBrightnessFormatted => $"{_rgbBrightness}%";

    public string CurrentRgbHex
    {
        get => _currentRgbHex;
        set { _currentRgbHex = value; OnPropertyChanged(); }
    }

    public string RgbSyncStatus
    {
        get => _rgbSyncStatus;
        set { _rgbSyncStatus = value; OnPropertyChanged(); }
    }

    public bool SyncFans
    {
        get => _syncFans;
        set { _syncFans = value; OnPropertyChanged(); }
    }
    public bool SyncPump
    {
        get => _syncPump;
        set { _syncPump = value; OnPropertyChanged(); }
    }
    public bool SyncLcd
    {
        get => _syncLcd;
        set { _syncLcd = value; OnPropertyChanged(); }
    }
    public bool SyncGpu
    {
        get => _syncGpu;
        set { _syncGpu = value; OnPropertyChanged(); }
    }
    public bool SyncRam
    {
        get => _syncRam;
        set { _syncRam = value; OnPropertyChanged(); }
    }
    public bool SyncMotherboard
    {
        get => _syncMotherboard;
        set { _syncMotherboard = value; OnPropertyChanged(); }
    }

    public string GpuRgbStatus
    {
        get => _gpuRgbStatus;
        set { _gpuRgbStatus = value; OnPropertyChanged(); }
    }
    public string MotherboardRgbStatus
    {
        get => _motherboardRgbStatus;
        set { _motherboardRgbStatus = value; OnPropertyChanged(); }
    }

    public MainViewModel()
    {
        _hub = new CorsairLinkHub();
        _lcd = new CorsairLinkLcd();
        _hwMonitor = new HardwareMonitor();
        _gpuRgb = new AsusGpuRgb();
        _motherboardRgb = new MsiMotherboardRgb();

        // Run hardware connection and loop in background (ZERO UI LAG!)
        Task.Run(() => InitializeHardwareAndLoopAsync(_cts.Token));
    }

    private async Task InitializeHardwareAndLoopAsync(CancellationToken token)
    {
        bool hubConnected = false;
        bool lcdConnected = false;

        try
        {
            hubConnected = _hub.Connect();
        }
        catch { }

        try
        {
            lcdConnected = _lcd.Connect();
        }
        catch { }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            if (hubConnected)
            {
                IsConnected = true;
                FirmwareVersion = _hub.FirmwareVersion;
                ConnectionStatus = $"● Conectado (FW: {_hub.FirmwareVersion}) - {_hub.SubDevices.Count} dispositivos";

                Fans.Clear();
                foreach (var dev in _hub.SubDevices)
                {
                    Fans.Add(new FanViewModel
                    {
                        Channel = dev.Channel,
                        Name = dev.Name,
                        DeviceId = dev.DeviceId,
                        IsPump = dev.IsPump,
                        CurrentRpm = dev.CurrentRpm,
                        TargetPowerPercent = dev.TargetPowerPercent
                    });
                }
            }
            else
            {
                IsConnected = false;
                ConnectionStatus = "○ Desconectado (Verifique se outro software está bloqueando)";
            }

            IsLcdConnected = lcdConnected;
            LcdStatus = lcdConnected
                ? "● Display 480x480 IPS Conectado (Corsair 0x0C4E)"
                : "○ Display LCD não encontrado (Verifique USB interno)";

            if (_gpuRgb.IsAvailable)
            {
                GpuRgbStatus = $"● ASUS Aura RTX 4080 Conectado ({_gpuRgb.ControllerName} - {_gpuRgb.LedCount} LEDs)";
            }
            else
            {
                GpuRgbStatus = "○ GPU Aura RGB não detectada";
            }

            if (_motherboardRgb.IsAvailable)
            {
                MotherboardRgbStatus = $"● Placa-Mãe MSI & Saídas ARGB Conectadas ({_motherboardRgb.DeviceName})";
            }
            else
            {
                MotherboardRgbStatus = "○ Placa-Mãe Mystic Light não encontrada";
            }
        });

        // Initial LCD Render Frame
        if (lcdConnected)
        {
            UpdateLcdFrameSafe();
        }

        // Apply initial RGB sync
        if (hubConnected)
        {
            _hub.SetGlobalRgb(_rgbR, _rgbG, _rgbB, _rgbBrightness);
        }
        if (_gpuRgb.IsAvailable)
        {
            _gpuRgb.SetColor(_rgbR, _rgbG, _rgbB);
        }
        if (_motherboardRgb.IsAvailable)
        {
            _motherboardRgb.SetColor(_rgbR, _rgbG, _rgbB, _rgbBrightness);
        }

        int frameCounter = 0;

        // 100% Background Polling Loop (Async, zero hitching)
        while (!token.IsCancellationRequested)
        {
            try
            {
                var t = _hwMonitor.GetTelemetry(_coolantTemp);
                Dictionary<int, int>? speeds = null;
                Dictionary<int, float>? temps = null;

                if (_hub.IsConnected)
                {
                    speeds = _hub.ReadSpeeds();
                    temps = _hub.ReadTemperatures();
                }

                // If LCD disconnected, retry connect occasionally
                if (!_lcd.IsConnected && (frameCounter % 5 == 0))
                {
                    _lcd.Connect();
                }

                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (t.CpuTemperature > 0) CpuTemp = t.CpuTemperature;
                    CpuLoad = t.CpuLoad;

                    if (t.GpuTemperature > 0) GpuTemp = t.GpuTemperature;
                    GpuLoad = t.GpuLoad;

                    RamUsage = t.RamUsagePercent;

                    if (t.GpuFanRpm1 > 0) GpuFanRpm1 = t.GpuFanRpm1;
                    if (t.GpuFanRpm2 > 0) GpuFanRpm2 = t.GpuFanRpm2;

                    if (temps != null && temps.Count > 0)
                    {
                        // Check coolant temp (Channel 14 or highest temp)
                        var highest = temps.Values.Max();
                        if (highest > 20 && highest < 80)
                        {
                            CoolantTemp = highest;
                        }
                    }

                    if (speeds != null)
                    {
                        foreach (var fan in Fans)
                        {
                            if (speeds.TryGetValue(fan.Channel, out int rpm))
                            {
                                fan.CurrentRpm = rpm;
                            }
                        }
                        OnPropertyChanged(nameof(AverageFanRpm));
                        OnPropertyChanged(nameof(PumpRpm));
                    }

                    IsLcdConnected = _lcd.IsConnected;
                    LcdStatus = _lcd.IsConnected
                        ? "● Display 480x480 IPS Conectado (Corsair 0x0C4E)"
                        : "○ Reconectando Display LCD...";

                    // Auto Curve Logic
                    if (_isAutoCurve)
                    {
                        float maxTemp = Math.Max(CpuTemp, GpuTemp);
                        byte calculated = CalculateAutoSpeed(maxTemp);
                        if (Math.Abs(calculated - _globalSpeedPercent) >= 3)
                        {
                            _globalSpeedPercent = calculated;
                            OnPropertyChanged(nameof(GlobalSpeedPercent));
                            OnPropertyChanged(nameof(GlobalSpeedFormatted));
                            Task.Run(() => ApplyGlobalSpeed(calculated));
                        }
                    }
                });

                // Update LCD Display every second
                if (_isLcdAutoStream && _lcd.IsConnected)
                {
                    UpdateLcdFrameSafe();
                }

                frameCounter++;
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(2000, token);
            }
        }
    }

    private void UpdateLcdFrameSafe()
    {
        try
        {
            var jpeg = _lcd.RenderDashboardFrame(
                CpuTemp,
                GpuTemp,
                PumpRpm,
                _rgbR,
                _rgbG,
                _rgbB,
                "OPERACIONAL"
            );

            _lcd.SendJpegFrame(jpeg);
        }
        catch { }
    }

    private static byte CalculateAutoSpeed(float temp)
    {
        if (temp <= 45) return 35;
        if (temp >= 85) return 100;
        float ratio = (temp - 45) / 40f;
        return (byte)(35 + ratio * 65);
    }

    public void ApplyPreset(string preset)
    {
        ActivePreset = preset;
        switch (preset)
        {
            case "Silencioso":
                _isAutoCurve = false;
                GlobalSpeedPercent = 40;
                IsGpuFanAuto = true;
                break;
            case "Equilibrado":
                _isAutoCurve = false;
                GlobalSpeedPercent = 55;
                IsGpuFanAuto = true;
                break;
            case "Desempenho":
                _isAutoCurve = false;
                GlobalSpeedPercent = 75;
                IsGpuFanAuto = false;
                GpuFanPercent = 65;
                break;
            case "Máximo":
                _isAutoCurve = false;
                GlobalSpeedPercent = 100;
                IsGpuFanAuto = false;
                GpuFanPercent = 100;
                break;
            case "Automático":
                _isAutoCurve = true;
                IsGpuFanAuto = true;
                break;
        }
    }

    public void ApplyGlobalSpeed(byte speed)
    {
        if (!_hub.IsConnected) return;

        var dict = new Dictionary<int, byte>();
        foreach (var fan in Fans)
        {
            if (fan.IsSyncedWithGlobal)
            {
                fan.TargetPowerPercent = fan.IsPump ? Math.Max((byte)60, speed) : speed;
            }
            dict[fan.Channel] = fan.TargetPowerPercent;
        }

        _hub.SetSpeedsPercent(dict);
    }

    public void ApplyIndividualSpeed(FanViewModel fan, byte speed)
    {
        if (!_hub.IsConnected) return;
        fan.TargetPowerPercent = fan.IsPump ? Math.Max((byte)60, speed) : speed;
        _hub.SetSpeedPercent(fan.Channel, fan.TargetPowerPercent);
    }

    // RGB Methods
    private void UpdateHexFromRgb()
    {
        CurrentRgbHex = $"#{_rgbR:X2}{_rgbG:X2}{_rgbB:X2}";
    }

    public void SetColorPreset(byte r, byte g, byte b)
    {
        _rgbR = r;
        _rgbG = g;
        _rgbB = b;
        OnPropertyChanged(nameof(RgbR));
        OnPropertyChanged(nameof(RgbG));
        OnPropertyChanged(nameof(RgbB));
        UpdateHexFromRgb();

        ApplyRgbSync();
    }

    public void ApplyRgbSync()
    {
        Task.Run(() =>
        {
            bool hubOk = false;
            if (_syncFans || _syncPump)
            {
                if (_hub.IsConnected)
                {
                    hubOk = _hub.SetGlobalRgb(_rgbR, _rgbG, _rgbB, _rgbBrightness);
                }
            }

            bool gpuOk = false;
            if (_syncGpu && _gpuRgb.IsAvailable)
            {
                gpuOk = _gpuRgb.SetColor(_rgbR, _rgbG, _rgbB);
            }

            bool mbOk = false;
            if (_syncMotherboard && _motherboardRgb.IsAvailable)
            {
                mbOk = _motherboardRgb.SetColor(_rgbR, _rgbG, _rgbB, _rgbBrightness);
            }

            // Immediately send current color frame to LCD
            if (_syncLcd && _lcd.IsConnected)
            {
                UpdateLcdFrameSafe();
            }

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var synced = new List<string>();
                if (hubOk) synced.Add("Fans/Bomba");
                if (gpuOk) synced.Add("RTX 4080");
                if (mbOk) synced.Add("Placa-Mãe/ARGB");
                if (_syncLcd && _lcd.IsConnected) synced.Add("Display LCD");

                string targetStr = synced.Count > 0 ? string.Join(", ", synced) : "Nenhum";
                RgbSyncStatus = $"✨ {targetStr} sincronizados! ({CurrentRgbHex})";
            });
        });
    }

    public void TransmitLcdImmediately()
    {
        Task.Run(() =>
        {
            if (!_lcd.IsConnected)
            {
                _lcd.Connect();
            }

            UpdateLcdFrameSafe();

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                LcdStatus = _lcd.IsConnected
                    ? "✨ Quadro transmitido com sucesso ao display 480x480!"
                    : "Falha ao conectar ao display LCD";
            });
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _hub.Dispose();
        _lcd.Dispose();
        _hwMonitor.Dispose();
        _gpuRgb.Dispose();
        _motherboardRgb.Dispose();
        GC.SuppressFinalize(this);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
