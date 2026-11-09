using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>
/// Estado da interface. Não acessa hardware: lê snapshots do engine (coalescidos na thread da UI)
/// e envia alterações de configuração que o engine aplica em segundo plano ("último valor vence").
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IEngineClient _engine;
    private readonly Dispatcher _dispatcher;
    private EngineSnapshot? _pendingSnapshot;
    private int _snapshotQueued;
    private bool _loadingConfig;

    // Configuração espelhada
    private byte _globalSpeedPercent;
    private FanControlMode _mode;
    private string _activePreset = string.Empty;
    private bool _isGpuFanAuto;
    private byte _gpuFanPercent;
    private byte _rgbR, _rgbG, _rgbB, _rgbBrightness;
    private bool _syncHub, _syncLcd, _syncGpu, _syncMotherboard, _syncRam, _syncPeripherals;
    private bool _isLcdAutoStream;

    public MainViewModel(IEngineClient engine, Dispatcher? dispatcher = null)
    {
        _engine = engine;
        _dispatcher = dispatcher ?? Application.Current.Dispatcher;

        LoadConfig(engine.Config);

        _engine.SnapshotUpdated += OnSnapshot;
        _engine.ConfigChanged += c => _dispatcher.BeginInvoke(() => LoadConfig(c));
        _engine.AlertRaised += a => _dispatcher.BeginInvoke(() => AlertRaised?.Invoke(a));
        _engine.ConnectionChanged += connected => _dispatcher.BeginInvoke(() =>
        {
            OnPropertiesChanged(nameof(EngineModeLabel), nameof(IsEngineDisconnected));
            if (connected) LoadConfig(_engine.Config);
        });

        InitializeCommands();
        if (engine.Snapshot is { } s) ApplySnapshot(s);
    }

    public IEngineClient Engine => _engine;
    public event Action<EngineAlert>? AlertRaised;
    public event Action? SnapshotApplied;

    public ObservableCollection<FanViewModel> Fans { get; } = new();
    public EngineSnapshot Snapshot { get; private set; } = new();

    // ------------------------------------------------------------------ Snapshot (engine → UI)

    private void OnSnapshot(EngineSnapshot s)
    {
        // Coalescência: se a UI estiver ocupada, só o snapshot mais recente é aplicado
        _pendingSnapshot = s;
        if (Interlocked.Exchange(ref _snapshotQueued, 1) == 1) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _snapshotQueued, 0);
            if (_pendingSnapshot is { } latest) ApplySnapshot(latest);
        });
    }

    private bool _isViewActive = true;

    /// <summary>
    /// False quando nenhuma janela está visível (app na bandeja): os snapshots continuam chegando
    /// (bandeja/alertas), mas a árvore visual não é atualizada, economizando CPU.
    /// </summary>
    public bool IsViewActive
    {
        get => _isViewActive;
        set
        {
            if (_isViewActive == value) return;
            _isViewActive = value;
            if (value) ApplySnapshot(Snapshot);
        }
    }

    private void ApplySnapshot(EngineSnapshot s)
    {
        Snapshot = s;
        if (!_isViewActive)
        {
            SnapshotApplied?.Invoke();
            return;
        }

        SyncFans(s.Fans);

        OnPropertiesChanged(
            nameof(Snapshot),
            nameof(IsConnected), nameof(ConnectionStatus), nameof(HubStatusColor),
            nameof(CpuName), nameof(GpuName),
            nameof(CpuTempFormatted), nameof(CpuLoadFormatted), nameof(CpuSensorHint),
            nameof(GpuTempFormatted), nameof(GpuLoadFormatted), nameof(GpuHotspotFormatted),
            nameof(RamUsageFormatted), nameof(RamDetail),
            nameof(CoolantTempFormatted),
            nameof(AverageFanRpm), nameof(AverageFanRpmFormatted), nameof(PumpRpm), nameof(PumpRpmFormatted),
            nameof(GpuFanRpmFormatted),
            nameof(EffectiveGlobalPercent), nameof(GlobalSpeedFormatted), nameof(CurveStatus), nameof(IsFailSafeActive),
            nameof(IsLcdConnected), nameof(LcdStatus),
            nameof(GpuRgbStatus), nameof(MotherboardRgbStatus), nameof(RamRgbStatus), nameof(PeripheralsRgbStatus), nameof(RgbSyncStatus),
            nameof(Alerts), nameof(HasAlerts), nameof(EngineModeLabel));

        if (_mode == FanControlMode.Curve || s.FailSafeActive)
        {
            foreach (var f in Fans) f.RefreshLinkedValue();
        }

        OnSnapshotAppliedPartial(s);
        SnapshotApplied?.Invoke();
    }

    private void SyncFans(List<FanState> states)
    {
        var cfg = _engine.Config;
        var byKey = Fans.ToDictionary(f => f.Key);
        bool structureChanged = states.Count != Fans.Count || states.Any(s => !byKey.ContainsKey(s.Key));

        if (structureChanged)
        {
            Fans.Clear();
            foreach (var st in states.OrderBy(s => s.IsPump).ThenBy(s => s.Channel))
            {
                var vm = byKey.TryGetValue(st.Key, out var existing) ? existing : new FanViewModel(this, st);
                vm.LoadOverride(cfg.GetFan(st.Key));
                vm.UpdateFrom(st);
                Fans.Add(vm);
            }
            OnFansStructureChanged();
            return;
        }

        foreach (var st in states) byKey[st.Key].UpdateFrom(st);
    }

    partial void OnFansStructureChangedPartial();
    partial void OnSnapshotAppliedPartial(EngineSnapshot s);
    private void OnFansStructureChanged() => OnFansStructureChangedPartial();

    // ------------------------------------------------------------------ Telemetria exibida

    public bool IsConnected => Snapshot.HubConnected;
    public string ConnectionStatus => Snapshot.HubStatus;
    public string HubStatusColor => Snapshot.HubConnected ? "#10B981" : "#F59E0B";
    public string EngineModeLabel => _engine.IsRemote
        ? (_engine.IsConnected ? "Serviço do Windows" : "Serviço desconectado")
        : _engine.ModeDescription;

    public bool IsEngineDisconnected => _engine.IsRemote && !_engine.IsConnected;

    public string CpuName => Snapshot.CpuName ?? "Processador";
    public string GpuName => Snapshot.GpuName ?? "Placa de vídeo";

    public string CpuTempFormatted => Fmt.Temp(Snapshot.CpuSensorAvailable ? Snapshot.CpuTemp : null);
    public string CpuLoadFormatted => Fmt.Pct(Snapshot.CpuLoad);
    public string? CpuSensorHint => Snapshot.SensorsReady && !Snapshot.CpuSensorAvailable
        ? "Sensor de temperatura da CPU indisponível. Execute como administrador ou instale o serviço LinkFlow."
        : null;

    public string GpuTempFormatted => Fmt.Temp(Snapshot.GpuTemp);
    public string GpuLoadFormatted => Fmt.Pct(Snapshot.GpuLoad);
    public string GpuHotspotFormatted => Fmt.Temp(Snapshot.GpuHotspot);

    public string RamUsageFormatted => Fmt.Pct(Snapshot.RamUsage);
    public string RamDetail => Snapshot.RamUsedGb is { } u && Snapshot.RamTotalGb is { } t
        ? $"{u:0.0} de {t:0} GB"
        : Fmt.Missing;

    public string CoolantTempFormatted => Fmt.Temp(Snapshot.CoolantTemp);

    public int? AverageFanRpm => Snapshot.AverageFanRpm;
    public string AverageFanRpmFormatted => Fmt.Rpm(Snapshot.AverageFanRpm);
    public int? PumpRpm => Snapshot.PumpRpm;
    public string PumpRpmFormatted => Fmt.Rpm(Snapshot.PumpRpm);

    public string GpuFanRpmFormatted => Snapshot.GpuFanRpm1 is null && Snapshot.GpuFanRpm2 is null
        ? "Rotação indisponível"
        : $"Fan 1: {Snapshot.GpuFanRpm1 ?? 0:N0} RPM  •  Fan 2: {Snapshot.GpuFanRpm2 ?? 0:N0} RPM";

    public bool IsFailSafeActive => Snapshot.FailSafeActive;
    public IReadOnlyList<EngineAlert> Alerts => Snapshot.Alerts;
    public bool HasAlerts => Snapshot.Alerts.Count > 0;

    public bool IsLcdConnected => Snapshot.LcdConnected;
    public string LcdStatus => Snapshot.LcdConnected
        ? "Display 480x480 conectado"
        : "Display LCD não encontrado • tentando reconectar";

    public string GpuRgbStatus => !Snapshot.RgbDevicesReady ? "Detectando..." :
        Snapshot.GpuRgbAvailable ? $"Conectado • {Snapshot.GpuRgbName}" : "Não detectado";
    public string MotherboardRgbStatus => !Snapshot.RgbDevicesReady ? "Detectando..." :
        Snapshot.MotherboardRgbAvailable ? $"Conectado • {Snapshot.MotherboardRgbName}" : "Não detectado";
    public string RamRgbStatus => !Snapshot.RgbDevicesReady ? "Detectando..." :
        Snapshot.RamRgbAvailable ? $"Conectado • {Snapshot.RamRgbName}" : "Não detectado";
    public string PeripheralsRgbStatus => !Snapshot.RgbDevicesReady ? "Detectando..." :
        Snapshot.PeripheralsRgbAvailable ? $"Conectado • {Snapshot.PeripheralsRgbName}" : "Não detectado";
    public string RgbSyncStatus => Snapshot.RgbStatus;

    // ------------------------------------------------------------------ Controle de ventoinhas (UI → engine)

    public byte GlobalSpeedPercent
    {
        get => _globalSpeedPercent;
        set
        {
            if (_globalSpeedPercent == value) return;
            _globalSpeedPercent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlobalSpeedFormatted));
            if (_loadingConfig) return;

            // Mexer no slider global sai da curva automática (antes os dois "brigavam")
            bool leftCurve = _mode == FanControlMode.Curve;
            if (leftCurve) SetModeLocal(FanControlMode.Fixed);
            SetActivePresetLocal("Personalizado");
            _engine.UpdateConfig(c =>
            {
                c.GlobalPercent = value;
                c.Mode = FanControlMode.Fixed;
                c.ActivePreset = "Personalizado";
            });
            foreach (var f in Fans) f.RefreshLinkedValue();
        }
    }

    public byte EffectiveGlobalPercent => _mode == FanControlMode.Curve || Snapshot.FailSafeActive
        ? Snapshot.EffectiveGlobalPercent
        : _globalSpeedPercent;

    public string GlobalSpeedFormatted => $"{EffectiveGlobalPercent}%";

    public FanControlMode Mode => _mode;
    public bool IsCurveMode => _mode == FanControlMode.Curve;
    public bool IsFixedMode => _mode == FanControlMode.Fixed;

    public string CurveStatus => _mode == FanControlMode.Curve
        ? $"Curva ativa • {(Snapshot.CurveInputTemp is { } t ? $"{t:0.0}°C" : "aguardando sensor")} → {Snapshot.EffectiveGlobalPercent}%"
        : "Potência fixa";

    public string ActivePreset => _activePreset;

    /// <summary>Nome amigável do perfil ativo (ex.: "Curva inteligente" em vez da chave "Automático").</summary>
    public string ActivePresetLabel => FanPresets.Find(_activePreset)?.Label ?? _activePreset;

    /// <summary>Velocidade que uma ventoinha vinculada recebe (exibição imediata, sem esperar o engine).</summary>
    public byte LinkedTargetFor(FanViewModel fan)
    {
        byte g = EffectiveGlobalPercent;
        return fan.IsPump ? Math.Max((byte)FanLimits.PumpMinPercent, g) : g;
    }

    public void ApplyPreset(string key)
    {
        var label = FanPresets.Find(key)?.Label ?? key;
        WithUndo($"Perfil \"{label}\" aplicado", () => _engine.UpdateConfig(c => FanPresets.Apply(c, key)));
        RefreshPresetChips();
    }

    public void ApplyProfileByName(string name)
    {
        WithUndo($"Perfil \"{name}\" aplicado",
            () => _engine.UpdateConfig(c => c.Profiles.FirstOrDefault(p => p.Name == name)?.ApplyTo(c)));
        RefreshPresetChips();
    }

    internal void OnFanOverrideChanged(FanViewModel fan)
    {
        if (_loadingConfig) return;
        bool linked = fan.IsLinked;
        byte pct = fan.ManualPercent;
        _engine.UpdateConfig(c =>
        {
            var o = c.EditFan(fan.Key);
            o.Linked = linked;
            o.Percent = pct;
        });
    }

    internal void OnFanRenamed(FanViewModel fan, string? name)
    {
        if (_loadingConfig) return;
        _engine.UpdateConfig(c => c.EditFan(fan.Key).Name = name);
    }

    internal void OnSelectionChanged() => OnSelectionChangedPartial();
    partial void OnSelectionChangedPartial();

    public bool IsGpuFanAuto
    {
        get => _isGpuFanAuto;
        set
        {
            if (!SetProperty(ref _isGpuFanAuto, value)) return;
            OnPropertiesChanged(nameof(GpuFanModeText), nameof(IsGpuFanManual));
            if (!_loadingConfig) _engine.UpdateConfig(c => c.GpuFanAuto = value);
        }
    }

    public bool IsGpuFanManual => !_isGpuFanAuto;
    public string GpuFanModeText => _isGpuFanAuto ? "Automático (curva da vBIOS)" : "Manual (controle LinkFlow)";

    public byte GpuFanPercent
    {
        get => _gpuFanPercent;
        set
        {
            if (!SetProperty(ref _gpuFanPercent, value)) return;
            OnPropertyChanged(nameof(GpuFanPercentFormatted));
            if (!_loadingConfig) _engine.UpdateConfig(c => c.GpuFanPercent = value);
        }
    }

    public string GpuFanPercentFormatted => $"{_gpuFanPercent}%";

    // ------------------------------------------------------------------ RGB

    public byte RgbR { get => _rgbR; set => SetRgb(ref _rgbR, value); }
    public byte RgbG { get => _rgbG; set => SetRgb(ref _rgbG, value); }
    public byte RgbB { get => _rgbB; set => SetRgb(ref _rgbB, value); }

    public byte RgbBrightness
    {
        get => _rgbBrightness;
        set
        {
            if (!SetProperty(ref _rgbBrightness, value)) return;
            OnPropertyChanged(nameof(RgbBrightnessFormatted));
            PushRgb();
        }
    }

    public string RgbBrightnessFormatted => $"{_rgbBrightness}%";
    public string CurrentRgbHex => $"#{_rgbR:X2}{_rgbG:X2}{_rgbB:X2}";

    private void SetRgb(ref byte field, byte value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        OnRgbColorChanged();
        PushRgb();
    }

    private void OnRgbColorChanged()
    {
        OnPropertyChanged(nameof(CurrentRgbHex));
        OnRgbColorChangedPartial();
    }

    partial void OnRgbColorChangedPartial();

    private void PushRgb()
    {
        if (_loadingConfig) return;
        byte r = _rgbR, g = _rgbG, b = _rgbB, br = _rgbBrightness;
        _engine.UpdateConfig(c =>
        {
            c.Rgb.R = r;
            c.Rgb.G = g;
            c.Rgb.B = b;
            c.Rgb.Brightness = br;
        });
    }

    public void SetColorPreset(byte r, byte g, byte b)
    {
        _loadingConfig = true;
        try
        {
            RgbR = r;
            RgbG = g;
            RgbB = b;
        }
        finally
        {
            _loadingConfig = false;
        }
        PushRgb();
    }

    public bool SyncHub { get => _syncHub; set => SetSync(ref _syncHub, value, (c, v) => c.Rgb.SyncHub = v); }
    public bool SyncLcd { get => _syncLcd; set => SetSync(ref _syncLcd, value, (c, v) => c.Rgb.SyncLcd = v); }
    public bool SyncGpu { get => _syncGpu; set => SetSync(ref _syncGpu, value, (c, v) => c.Rgb.SyncGpu = v); }
    public bool SyncMotherboard { get => _syncMotherboard; set => SetSync(ref _syncMotherboard, value, (c, v) => c.Rgb.SyncMotherboard = v); }
    public bool SyncRam { get => _syncRam; set => SetSync(ref _syncRam, value, (c, v) => c.Rgb.SyncRam = v); }
    public bool SyncPeripherals { get => _syncPeripherals; set => SetSync(ref _syncPeripherals, value, (c, v) => c.Rgb.SyncPeripherals = v); }

    private void SetSync(ref bool field, bool value, Action<ControlConfig, bool> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnPropertyChanged(name);
        if (!_loadingConfig) _engine.UpdateConfig(c => apply(c, value));
    }

    // ------------------------------------------------------------------ Mini Widget
    public bool MiniShowCpu
    {
        get => App.Current.Settings.Current.MiniShowCpu;
        set { App.Current.Settings.Update(s => s.MiniShowCpu = value); OnPropertyChanged(); }
    }
    public bool MiniShowGpu
    {
        get => App.Current.Settings.Current.MiniShowGpu;
        set { App.Current.Settings.Update(s => s.MiniShowGpu = value); OnPropertyChanged(); }
    }
    public bool MiniShowCoolant
    {
        get => App.Current.Settings.Current.MiniShowCoolant;
        set { App.Current.Settings.Update(s => s.MiniShowCoolant = value); OnPropertyChanged(); }
    }
    public bool MiniShowFans
    {
        get => App.Current.Settings.Current.MiniShowFans;
        set { App.Current.Settings.Update(s => s.MiniShowFans = value); OnPropertyChanged(); }
    }
    public bool MiniShowRam
    {
        get => App.Current.Settings.Current.MiniShowRam;
        set { App.Current.Settings.Update(s => s.MiniShowRam = value); OnPropertyChanged(); }
    }
    public bool MiniShowPump
    {
        get => App.Current.Settings.Current.MiniShowPump;
        set { App.Current.Settings.Update(s => s.MiniShowPump = value); OnPropertyChanged(); }
    }
    public double MiniOpacity
    {
        get => App.Current.Settings.Current.MiniOpacity;
        set { App.Current.Settings.Update(s => s.MiniOpacity = value); OnPropertyChanged(); }
    }

    // ------------------------------------------------------------------ LCD

    public bool IsLcdAutoStream
    {
        get => _isLcdAutoStream;
        set
        {
            if (!SetProperty(ref _isLcdAutoStream, value)) return;
            if (!_loadingConfig) _engine.UpdateConfig(c => c.Lcd.AutoStream = value);
        }
    }

    public void TransmitLcdImmediately() => _engine.RequestLcdFrame();

    // ------------------------------------------------------------------ Configuração (engine → UI)

    private void SetModeLocal(FanControlMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        OnPropertiesChanged(nameof(Mode), nameof(IsCurveMode), nameof(IsFixedMode), nameof(CurveStatus),
            nameof(EffectiveGlobalPercent), nameof(GlobalSpeedFormatted));
    }

    private void SetActivePresetLocal(string preset)
    {
        if (_activePreset == preset) return;
        _activePreset = preset;
        OnPropertiesChanged(nameof(ActivePreset), nameof(ActivePresetLabel));
        OnActivePresetChangedPartial();
    }

    partial void OnActivePresetChangedPartial();

    public void LoadConfig(ControlConfig c)
    {
        _loadingConfig = true;
        try
        {
            GlobalSpeedPercent = c.GlobalPercent;
            SetModeLocal(c.Mode);
            SetActivePresetLocal(c.ActivePreset);
            IsGpuFanAuto = c.GpuFanAuto;
            GpuFanPercent = c.GpuFanPercent;
            RgbR = c.Rgb.R;
            RgbG = c.Rgb.G;
            RgbB = c.Rgb.B;
            RgbBrightness = c.Rgb.Brightness;
            SyncHub = c.Rgb.SyncHub;
            SyncLcd = c.Rgb.SyncLcd;
            SyncGpu = c.Rgb.SyncGpu;
            SyncMotherboard = c.Rgb.SyncMotherboard;
            SyncRam = c.Rgb.SyncRam;
            SyncPeripherals = c.Rgb.SyncPeripherals;
            IsLcdAutoStream = c.Lcd.AutoStream;
            foreach (var f in Fans) f.LoadOverride(c.GetFan(f.Key));
            LoadConfigPartial(c);
        }
        finally
        {
            _loadingConfig = false;
        }
        foreach (var f in Fans) f.RefreshLinkedValue();
    }

    partial void LoadConfigPartial(ControlConfig c);
    partial void InitializeCommandsPartial();
    private void InitializeCommands() => InitializeCommandsPartial();

    protected bool IsLoadingConfig => _loadingConfig;

    public void Dispose()
    {
        _engine.SnapshotUpdated -= OnSnapshot;
        GC.SuppressFinalize(this);
    }
}
