using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public sealed class PresetItem : ObservableObject
{
    private readonly MainViewModel _owner;
    public PresetItem(MainViewModel owner, FanPreset p) { _owner = owner; Preset = p; }
    public FanPreset Preset { get; }
    public string Key => Preset.Key;
    public string Label => Preset.Label;
    public string Icon => Preset.Icon;
    public string Description => Preset.Description;
    public int Index => FanPresets.All.ToList().IndexOf(Preset) + 1;
    public string Tooltip => $"{Description}  •  F{Index} (Ctrl+Alt+{Index} em qualquer lugar)";
    public bool IsActive => _owner.ActivePreset == Key;
    public void Refresh() => OnPropertyChanged(nameof(IsActive));
}

public sealed class ProfileItem : ObservableObject
{
    private readonly MainViewModel _owner;
    public ProfileItem(MainViewModel owner, string name) { _owner = owner; Name = name; }
    public string Name { get; }
    public bool IsActive => _owner.ActivePreset == Name;
    public void Refresh() => OnPropertyChanged(nameof(IsActive));
}

public sealed record NamedColor(string Name, string Hex);

public sealed record CurveSourceOption(CurveSource Value, string Label)
{
    public override string ToString() => Label;
}

public partial class MainViewModel
{
    private const int HistoryLength = 90;

    // ------------------------------------------------------------------ Comandos

    public ICommand NavigateCommand { get; private set; } = null!;
    public ICommand ApplyPresetCommand { get; private set; } = null!;
    public ICommand ApplyProfileCommand { get; private set; } = null!;
    public ICommand SaveProfileCommand { get; private set; } = null!;
    public ICommand DeleteProfileCommand { get; private set; } = null!;
    public ICommand UndoCommand { get; private set; } = null!;
    public ICommand DismissToastCommand { get; private set; } = null!;
    public ICommand SelectAllCommand { get; private set; } = null!;
    public ICommand SelectFansCommand { get; private set; } = null!;
    public ICommand SelectPumpsCommand { get; private set; } = null!;
    public ICommand SelectGroupCommand { get; private set; } = null!;
    public ICommand InvertSelectionCommand { get; private set; } = null!;
    public ICommand ClearSelectionCommand { get; private set; } = null!;
    public ICommand ApplyBatchCommand { get; private set; } = null!;
    public ICommand LinkSelectedCommand { get; private set; } = null!;
    public ICommand SetGroupCommand { get; private set; } = null!;
    public ICommand ClearGroupCommand { get; private set; } = null!;
    public ICommand ToggleLinkCommand { get; private set; } = null!;
    public ICommand RelinkAllCommand { get; private set; } = null!;
    public ICommand SetModeCommand { get; private set; } = null!;
    public ICommand ResetCurveCommand { get; private set; } = null!;
    public ICommand ApplyColorCommand { get; private set; } = null!;
    public ICommand ResendRgbCommand { get; private set; } = null!;
    public ICommand TransmitLcdCommand { get; private set; } = null!;
    public ICommand GlobalStepCommand { get; private set; } = null!;

    partial void InitializeCommandsPartial()
    {
        NavigateCommand = new RelayCommand(p => { if (int.TryParse(p?.ToString(), out var i)) SelectedPage = i; });
        ApplyPresetCommand = new RelayCommand(p =>
        {
            // Atalhos F1–F5 / Ctrl+Alt+1–5 enviam o número do perfil; os chips enviam a chave
            if (int.TryParse(p?.ToString(), out var i))
            {
                if (i >= 1 && i <= FanPresets.All.Count) ApplyPreset(FanPresets.All[i - 1].Key);
            }
            else if (p is string key && FanPresets.Find(key) != null)
            {
                ApplyPreset(key);
            }
        });
        ApplyProfileCommand = new RelayCommand(p => { if (p is string n) ApplyProfileByName(n); });
        SaveProfileCommand = new RelayCommand(SaveProfile, () => !string.IsNullOrWhiteSpace(NewProfileName));
        DeleteProfileCommand = new RelayCommand(p => { if (p is string n) DeleteProfile(n); });
        UndoCommand = new RelayCommand(Undo, () => _undoConfig != null);
        DismissToastCommand = new RelayCommand(() => ToastVisible = false);

        SelectAllCommand = new RelayCommand(() => SelectWhere(_ => true));
        SelectFansCommand = new RelayCommand(() => SelectWhere(f => !f.IsPump));
        SelectPumpsCommand = new RelayCommand(() => SelectWhere(f => f.IsPump));
        SelectGroupCommand = new RelayCommand(p => { if (p is string g) SelectWhere(f => f.Group == g); });
        InvertSelectionCommand = new RelayCommand(() => BatchSelect(() => { foreach (var f in Fans) f.IsSelected = !f.IsSelected; }));
        ClearSelectionCommand = new RelayCommand(() => SelectWhere(_ => false));
        ApplyBatchCommand = new RelayCommand(ApplyBatch, () => HasSelection);
        LinkSelectedCommand = new RelayCommand(() => SetLinkForSelection(true), () => HasSelection);
        SetGroupCommand = new RelayCommand(SetGroupForSelection, () => HasSelection && !string.IsNullOrWhiteSpace(GroupNameInput));
        ClearGroupCommand = new RelayCommand(() => SetGroupNameForSelection(null), () => HasSelection);
        ToggleLinkCommand = new RelayCommand(p => { if (p is FanViewModel f) f.IsLinked = !f.IsLinked; });
        RelinkAllCommand = new RelayCommand(() => WithUndo("Todas as ventoinhas voltaram a seguir a potência global", () =>
            _engine.UpdateConfig(c => { foreach (var f in c.Fans.Values) f.Linked = true; })));

        SetModeCommand = new RelayCommand(p => SetMode(p?.ToString() == "Curve" ? FanControlMode.Curve : FanControlMode.Fixed));
        ResetCurveCommand = new RelayCommand(() => WithUndo("Curva restaurada para o padrão", () =>
            _engine.UpdateConfig(c => TargetCurve(c).Points = FanCurveConfig.DefaultPoints())));

        ApplyColorCommand = new RelayCommand(p => { if (p is string hex) ApplyHex(hex, addRecent: true); });
        ResendRgbCommand = new RelayCommand(() => { _engine.NotifyResume(); ShowToast("Reenviando cor e velocidades para todos os dispositivos...", canUndo: false); });
        TransmitLcdCommand = new RelayCommand(() => { _engine.RequestLcdFrame(); ShowToast("Quadro enviado ao display LCD", canUndo: false); });
        GlobalStepCommand = new RelayCommand(p =>
        {
            if (int.TryParse(p?.ToString(), out var step))
                GlobalSpeedPercent = (byte)Math.Clamp(_globalSpeedPercent + step, 25, 100);
        });

        PresetItems = FanPresets.All.Select(p => new PresetItem(this, p)).ToList();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastVisible = false; };
        _recentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _recentTimer.Tick += (_, _) => { _recentTimer.Stop(); AddRecentColor(CurrentRgbHex); };
    }

    // ------------------------------------------------------------------ Navegação

    private int _selectedPage;
    private bool _isCompact;

    public static readonly string[] PageTitles = { "Painel", "Ventoinhas", "Iluminação", "Tela LCD", "Widget", "Configurações" };

    public int SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value < 0 || value >= PageTitles.Length || !SetProperty(ref _selectedPage, value)) return;
            CloseDetail();
            OnPropertyChanged(nameof(PageTitle));
            UpdateSensorDemand();
            PageChanged?.Invoke(value);
        }
    }

    public event Action<int>? PageChanged;
    public string PageTitle => PageTitles[_selectedPage];

    /// <summary>Janela estreita: navegação lateral mostra só ícones.</summary>
    public bool IsCompact
    {
        get => _isCompact;
        set => SetProperty(ref _isCompact, value);
    }

    // ------------------------------------------------------------------ Histórico (sparklines)

    private readonly Queue<double> _cpuHist = new(), _gpuHist = new(), _coolHist = new(), _fanHist = new();
    private DateTime _lastHistory;

    public double[]? CpuHistory { get; private set; }
    public double[]? GpuHistory { get; private set; }
    public double[]? CoolantHistory { get; private set; }
    public double[]? FanHistory { get; private set; }

    partial void OnSnapshotAppliedPartial(EngineSnapshot s)
    {
        if ((s.Timestamp - _lastHistory).TotalSeconds < 0.9) return;
        _lastHistory = s.Timestamp;

        static double[]? Push(Queue<double> q, double? v)
        {
            if (v is { } x)
            {
                q.Enqueue(x);
                while (q.Count > HistoryLength) q.Dequeue();
            }
            return q.Count >= 2 ? q.ToArray() : null;
        }

        CpuHistory = Push(_cpuHist, s.CpuSensorAvailable ? s.CpuTemp : null);
        GpuHistory = Push(_gpuHist, s.GpuTemp);
        CoolantHistory = Push(_coolHist, s.CoolantTemp);
        FanHistory = Push(_fanHist, s.AverageFanRpm);
        RamHistory = Push(_ramHist, s.RamUsage);
        RecordExtraHistory(s);
        OnPropertiesChanged(nameof(CpuHistory), nameof(GpuHistory), nameof(CoolantHistory), nameof(FanHistory), nameof(RamHistory),
            nameof(CurveCurrentTemp), nameof(CurveCurrentOutput), nameof(TopAlert), nameof(FanCountLabel), nameof(LcdNowShowing), nameof(PreviewFanCount));
        RecordHistory(s);
    }

    public EngineAlert? TopAlert => Snapshot.Alerts.FirstOrDefault();

    // ------------------------------------------------------------------ Perfis e desfazer

    public IReadOnlyList<PresetItem> PresetItems { get; private set; } = Array.Empty<PresetItem>();
    public ObservableCollection<ProfileItem> Profiles { get; } = new();

    private string _newProfileName = string.Empty;
    public string NewProfileName
    {
        get => _newProfileName;
        set => SetProperty(ref _newProfileName, value);
    }

    partial void OnActivePresetChangedPartial()
    {
        foreach (var p in PresetItems) p.Refresh();
        foreach (var p in Profiles) p.Refresh();
    }

    private void RefreshPresetChips()
    {
        foreach (var p in PresetItems) p.Refresh();
        foreach (var p in Profiles) p.Refresh();
    }

    private void SaveProfile()
    {
        var name = NewProfileName.Trim();
        if (name.Length == 0) return;
        if (FanPresets.Find(name) != null)
        {
            ShowToast($"\"{name}\" é um perfil pronto. Escolha outro nome.", canUndo: false);
            return;
        }
        bool replaced = _engine.Config.Profiles.Any(p => p.Name == name);
        WithUndo(replaced ? $"Perfil \"{name}\" atualizado" : $"Perfil \"{name}\" salvo", () =>
            _engine.UpdateConfig(c =>
            {
                c.Profiles.RemoveAll(p => p.Name == name);
                c.Profiles.Add(FanProfile.Capture(name, c));
                c.ActivePreset = name;
            }));
        NewProfileName = string.Empty;
    }

    private void DeleteProfile(string name) =>
        WithUndo($"Perfil \"{name}\" excluído", () => _engine.UpdateConfig(c => c.Profiles.RemoveAll(p => p.Name == name)));

    private ControlConfig? _undoConfig;
    private DispatcherTimer _toastTimer = null!;
    private string _toastText = string.Empty;
    private bool _toastVisible;
    private bool _toastCanUndo;

    public string ToastText { get => _toastText; private set => SetProperty(ref _toastText, value); }
    public bool ToastVisible { get => _toastVisible; set => SetProperty(ref _toastVisible, value); }
    public bool ToastCanUndo { get => _toastCanUndo; private set => SetProperty(ref _toastCanUndo, value); }

    /// <summary>Executa uma alteração e oferece "Desfazer" por alguns segundos.</summary>
    public void WithUndo(string message, Action action)
    {
        var before = Json.Clone(_engine.Config);
        action();
        LoadConfig(_engine.Config);
        _undoConfig = before;
        ShowToast(message, canUndo: true);
    }

    public void ShowToast(string message, bool canUndo)
    {
        if (!canUndo) _undoConfig = null;
        ToastText = message;
        ToastCanUndo = canUndo;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void Undo()
    {
        if (_undoConfig is not { } prev) return;
        _undoConfig = null;
        _engine.ReplaceConfig(prev);
        LoadConfig(_engine.Config);
        ShowToast("Alteração desfeita", canUndo: false);
    }

    // ------------------------------------------------------------------ Seleção múltipla e grupos

    private int _anchorIndex = -1;
    private bool _batchSelecting;
    private byte _batchPercent = 60;
    private string _groupNameInput = string.Empty;

    public ObservableCollection<string> AvailableGroups { get; } = new();

    public int SelectedCount => Fans.Count(f => f.IsSelected);
    public bool HasSelection => SelectedCount > 0;
    public string SelectionSummary => SelectedCount switch
    {
        0 => "Nenhuma selecionada • Ctrl+clique ou Shift+clique nos cartões para selecionar várias",
        1 => "1 selecionada",
        var n => $"{n} selecionadas"
    };

    public string FanCountLabel
    {
        get
        {
            int fans = Fans.Count(f => !f.IsPump), pumps = Fans.Count(f => f.IsPump);
            int manual = Fans.Count(f => !f.IsLinked);
            var s = $"{fans} ventoinha{(fans == 1 ? "" : "s")}";
            if (pumps > 0) s += $" • {pumps} bomba{(pumps == 1 ? "" : "s")}";
            if (manual > 0) s += $" • {manual} em controle manual";
            return s;
        }
    }

    public byte BatchPercent
    {
        get => _batchPercent;
        set => SetProperty(ref _batchPercent, value);
    }

    public string GroupNameInput
    {
        get => _groupNameInput;
        set => SetProperty(ref _groupNameInput, value);
    }

    partial void OnSelectionChangedPartial()
    {
        if (_batchSelecting) return;
        OnPropertiesChanged(nameof(SelectedCount), nameof(HasSelection), nameof(SelectionSummary));
        CommandManager.InvalidateRequerySuggested(); // botões de lote reativam mesmo com seleção por teclado
    }

    partial void OnFansStructureChangedPartial()
    {
        RefreshGroups();
        RefreshCurveTargets(_engine.Config);
        OnPropertiesChanged(nameof(FanCountLabel), nameof(SelectedCount), nameof(HasSelection), nameof(SelectionSummary));
    }

    private void BatchSelect(Action a)
    {
        _batchSelecting = true;
        try { a(); }
        finally { _batchSelecting = false; }
        OnSelectionChangedPartial();
    }

    private void SelectWhere(Func<FanViewModel, bool> predicate) =>
        BatchSelect(() => { foreach (var f in Fans) f.IsSelected = predicate(f); });

    /// <summary>Clique no cartão: simples = só este; Ctrl = alterna; Shift = intervalo.</summary>
    public void HandleFanClick(FanViewModel fan, bool ctrl, bool shift)
    {
        int index = Fans.IndexOf(fan);
        if (shift && _anchorIndex >= 0)
        {
            int a = Math.Min(_anchorIndex, index), b = Math.Max(_anchorIndex, index);
            BatchSelect(() =>
            {
                for (int i = 0; i < Fans.Count; i++)
                    Fans[i].IsSelected = (ctrl && Fans[i].IsSelected) || (i >= a && i <= b);
            });
            return;
        }

        if (ctrl) fan.IsSelected = !fan.IsSelected;
        else
        {
            bool onlyThis = fan.IsSelected && SelectedCount == 1;
            SelectWhere(f => f == fan && !onlyThis);
        }
        _anchorIndex = index;
    }

    private List<FanViewModel> Selected => Fans.Where(f => f.IsSelected).ToList();

    private void ApplyBatch()
    {
        var targets = Selected;
        byte pct = _batchPercent;
        WithUndo($"{pct}% aplicado em {targets.Count} dispositivo(s)", () =>
            _engine.UpdateConfig(c =>
            {
                foreach (var f in targets)
                {
                    var o = c.EditFan(f.Key);
                    o.Linked = false;
                    o.Percent = f.IsPump ? Math.Max((byte)FanLimits.PumpMinPercent, pct) : pct;
                }
                c.ActivePreset = "Personalizado";
            }));
    }

    private void SetLinkForSelection(bool linked)
    {
        var targets = Selected;
        WithUndo($"{targets.Count} dispositivo(s) {(linked ? "vinculados à potência global" : "em controle manual")}", () =>
            _engine.UpdateConfig(c => { foreach (var f in targets) c.EditFan(f.Key).Linked = linked; }));
    }

    private void SetGroupForSelection() => SetGroupNameForSelection(GroupNameInput.Trim());

    private void SetGroupNameForSelection(string? group)
    {
        var targets = Selected;
        WithUndo(group == null ? $"Grupo removido de {targets.Count} dispositivo(s)" : $"{targets.Count} dispositivo(s) no grupo \"{group}\"", () =>
            _engine.UpdateConfig(c => { foreach (var f in targets) c.EditFan(f.Key).Group = group; }));
        GroupNameInput = string.Empty;
        RefreshGroups();
    }

    private void RefreshGroups()
    {
        var groups = Fans.Select(f => f.Group).Where(g => !string.IsNullOrEmpty(g)).Distinct().OrderBy(g => g).Cast<string>().ToList();
        if (groups.SequenceEqual(AvailableGroups)) return;
        AvailableGroups.Clear();
        foreach (var g in groups) AvailableGroups.Add(g);
        OnPropertyChanged(nameof(HasGroups));
    }

    public bool HasGroups => AvailableGroups.Count > 0;

    // ------------------------------------------------------------------ Curva

    private IReadOnlyList<CurvePoint> _curvePoints = FanCurveConfig.DefaultPoints();
    private CurveSource _curveSource;
    private float _curveHysteresis;

    public IReadOnlyList<CurveSourceOption> CurveSources { get; } = new[]
    {
        new CurveSourceOption(CurveSource.MaxCpuGpu, "Maior entre CPU e GPU"),
        new CurveSourceOption(CurveSource.Cpu, "Temperatura da CPU"),
        new CurveSourceOption(CurveSource.Gpu, "Temperatura da GPU"),
        new CurveSourceOption(CurveSource.Coolant, "Temperatura do líquido (AIO)"),
    };

    public IReadOnlyList<CurvePoint> CurvePoints
    {
        get => _curvePoints;
        private set => SetProperty(ref _curvePoints, value);
    }

    public CurveSource CurveSourceValue
    {
        get => _curveSource;
        set
        {
            if (!SetProperty(ref _curveSource, value) || IsLoadingConfig) return;
            _engine.UpdateConfig(c => TargetCurve(c).Source = value);
        }
    }

    public float CurveHysteresis
    {
        get => _curveHysteresis;
        set
        {
            if (!SetProperty(ref _curveHysteresis, value)) return;
            OnPropertyChanged(nameof(CurveHysteresisFormatted));
            if (!IsLoadingConfig) _engine.UpdateConfig(c => TargetCurve(c).Hysteresis = value);
        }
    }

    public string CurveHysteresisFormatted => $"{_curveHysteresis:0}°C";

    public double? CurveCurrentTemp => _curveTarget != null && _engine.Config.GroupCurves.TryGetValue(_curveTarget, out var gc)
        ? CurveController.SelectTemp(gc.Source, Snapshot.CpuSensorAvailable ? Snapshot.CpuTemp : null, Snapshot.GpuTemp, Snapshot.CoolantTemp)
        : Snapshot.CurveInputTemp;
    public double? CurveCurrentOutput => _curveTarget != null
        ? Snapshot.GroupOutputs.TryGetValue(_curveTarget, out var go) ? go : null
        : IsCurveMode ? Snapshot.EffectiveGlobalPercent : null;

    public void OnCurveEdited(List<CurvePoint> points, bool final)
    {
        _engine.UpdateConfig(c =>
        {
            TargetCurve(c).Points = points;
            if (final && _curveTarget == null && c.Mode == FanControlMode.Curve) c.ActivePreset = FanPresets.Auto;
        });
        if (final) CurvePoints = ShownCurve(_engine.Config).Points;
    }

    private void SetMode(FanControlMode mode)
    {
        if (mode == Mode) return;
        WithUndo(mode == FanControlMode.Curve ? "Curva automática ativada" : "Potência fixa ativada", () =>
            _engine.UpdateConfig(c =>
            {
                c.Mode = mode;
                c.ActivePreset = mode == FanControlMode.Curve ? FanPresets.Auto : "Personalizado";
            }));
    }

    // ------------------------------------------------------------------ Cor

    private string _hexInput = string.Empty;
    private bool _hexInvalid;
    private DispatcherTimer _recentTimer = null!;

    public ObservableCollection<string> RecentColors { get; } = new();

    public IReadOnlyList<NamedColor> QuickColors { get; } = new[]
    {
        new NamedColor("Ciano", "#00F0FF"), new NamedColor("Azul", "#2563EB"), new NamedColor("Roxo", "#A855F7"),
        new NamedColor("Rosa", "#EC4899"), new NamedColor("Vermelho", "#EF4444"), new NamedColor("Laranja", "#F97316"),
        new NamedColor("Amarelo", "#FACC15"), new NamedColor("Verde", "#10B981"), new NamedColor("Branco", "#FFFFFF"),
        new NamedColor("Apagado", "#000000"),
    };

    public string HexInput
    {
        get => _hexInput;
        set
        {
            if (!SetProperty(ref _hexInput, value)) return;
            bool ok = TryParseHex(value, out _, out _, out _);
            HexInvalid = !ok && value.Trim().Length > 0;
            if (ok) ApplyHex(value, addRecent: false);
        }
    }

    public bool HexInvalid
    {
        get => _hexInvalid;
        private set => SetProperty(ref _hexInvalid, value);
    }

    // HSV guardado: arrastar até o preto/cinza não perde a matiz escolhida
    private double _hsvH = 186, _hsvS = 1, _hsvV = 1;
    private bool _settingHsv;

    /// <summary>Matiz (0–359). Numa cor cinza/apagada, mover a matiz volta a cor para saturada.</summary>
    public double Hue
    {
        get => _hsvH;
        set => SetHsv(value, _hsvS < 0.05 ? 1 : _hsvS, _hsvV < 0.05 ? 1 : _hsvV);
    }

    /// <summary>Saturação (0–1): eixo horizontal do quadro de cores.</summary>
    public double ColorSaturation
    {
        get => _hsvS;
        set => SetHsv(_hsvH, value, _hsvV);
    }

    /// <summary>Brilho da cor (0–1): eixo vertical do quadro de cores.</summary>
    public double ColorValue
    {
        get => _hsvV;
        set => SetHsv(_hsvH, _hsvS, value);
    }

    private void SetHsv(double h, double s, double v)
    {
        _hsvH = ((h % 360) + 360) % 360;
        _hsvS = Math.Clamp(s, 0, 1);
        _hsvV = Math.Clamp(v, 0, 1);
        HsvToRgb(_hsvH, _hsvS, _hsvV, out var r, out var g, out var b);
        _settingHsv = true;
        try { SetColorPreset(r, g, b); }
        finally { _settingHsv = false; }
        OnPropertiesChanged(nameof(Hue), nameof(ColorSaturation), nameof(ColorValue));
        _recentTimer?.Stop();
        _recentTimer?.Start();
    }

    partial void OnRgbColorChangedPartial()
    {
        if (!string.Equals(_hexInput, CurrentRgbHex, StringComparison.OrdinalIgnoreCase) && !TryParseHexEquals(_hexInput, CurrentRgbHex))
        {
            _hexInput = CurrentRgbHex;
            OnPropertyChanged(nameof(HexInput));
        }
        HexInvalid = false;
        if (!_settingHsv)
        {
            RgbToHsv(RgbR, RgbG, RgbB, out var h, out var s, out var v);
            if (s > 0.001 && v > 0.001) _hsvH = h; // cinza/preto não têm matiz: mantém a anterior
            if (v > 0.001) _hsvS = s;
            _hsvV = v;
            OnPropertiesChanged(nameof(Hue), nameof(ColorSaturation), nameof(ColorValue));
        }
        if (!IsLoadingConfig && _recentTimer != null)
        {
            _recentTimer.Stop();
            _recentTimer.Start();
        }
    }

    private static bool TryParseHexEquals(string a, string b) =>
        TryParseHex(a, out var r1, out var g1, out var b1) && TryParseHex(b, out var r2, out var g2, out var b2) && r1 == r2 && g1 == g2 && b1 == b2;

    private void ApplyHex(string hex, bool addRecent)
    {
        if (!TryParseHex(hex, out var r, out var g, out var b)) return;
        SetColorPreset(r, g, b);
        if (addRecent) AddRecentColor(CurrentRgbHex);
    }

    private void AddRecentColor(string hex)
    {
        if (RecentColors.FirstOrDefault() == hex) return;
        RecentColors.Remove(hex);
        RecentColors.Insert(0, hex);
        while (RecentColors.Count > 8) RecentColors.RemoveAt(RecentColors.Count - 1);
        var list = RecentColors.ToList();
        _engine.UpdateConfig(c => c.Rgb.RecentColors = list);
    }

    public static bool TryParseHex(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        r = (byte)(v >> 16);
        g = (byte)(v >> 8);
        b = (byte)v;
        return true;
    }

    internal static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd)), min = Math.Min(rd, Math.Min(gd, bd)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == rd) h = 60 * (((gd - bd) / d) % 6);
        else if (max == gd) h = 60 * (((bd - rd) / d) + 2);
        else h = 60 * (((rd - gd) / d) + 4);
        if (h < 0) h += 360;
    }

    internal static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        (double rd, double gd, double bd) = (h % 360) switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        r = (byte)Math.Round((rd + m) * 255);
        g = (byte)Math.Round((gd + m) * 255);
        b = (byte)Math.Round((bd + m) * 255);
    }

    // ------------------------------------------------------------------ Segurança

    private bool _failSafeEnabled, _fanStopAlert;
    private float _cpuWarn, _cpuCritical, _gpuWarn, _gpuCritical, _coolantWarn, _coolantCritical;

    public bool FailSafeEnabled { get => _failSafeEnabled; set => SetSafety(ref _failSafeEnabled, value, (c, v) => c.Safety.FailSafeEnabled = v); }
    public bool FanStopAlert { get => _fanStopAlert; set => SetSafety(ref _fanStopAlert, value, (c, v) => c.Safety.FanStopAlert = v); }
    public float CpuWarn { get => _cpuWarn; set => SetSafety(ref _cpuWarn, value, (c, v) => c.Safety.CpuWarn = v); }
    public float CpuCritical { get => _cpuCritical; set => SetSafety(ref _cpuCritical, value, (c, v) => c.Safety.CpuCritical = v); }
    public float GpuWarn { get => _gpuWarn; set => SetSafety(ref _gpuWarn, value, (c, v) => c.Safety.GpuWarn = v); }
    public float GpuCritical { get => _gpuCritical; set => SetSafety(ref _gpuCritical, value, (c, v) => c.Safety.GpuCritical = v); }
    public float CoolantWarn { get => _coolantWarn; set => SetSafety(ref _coolantWarn, value, (c, v) => c.Safety.CoolantWarn = v); }
    public float CoolantCritical { get => _coolantCritical; set => SetSafety(ref _coolantCritical, value, (c, v) => c.Safety.CoolantCritical = v); }

    private void SetSafety<T>(ref T field, T value, Action<ControlConfig, T> apply, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
        if (!IsLoadingConfig) _engine.UpdateConfig(c => apply(c, value));
    }

    // ------------------------------------------------------------------ Carregar configuração

    partial void LoadConfigPartial(ControlConfig c)
    {
        var shown = ShownCurve(c);
        CurvePoints = shown.Points;
        CurveSourceValue = shown.Source;
        CurveHysteresis = shown.Hysteresis;
        RefreshCurveTargets(c);

        FailSafeEnabled = c.Safety.FailSafeEnabled;
        FanStopAlert = c.Safety.FanStopAlert;
        CpuWarn = c.Safety.CpuWarn;
        CpuCritical = c.Safety.CpuCritical;
        GpuWarn = c.Safety.GpuWarn;
        GpuCritical = c.Safety.GpuCritical;
        CoolantWarn = c.Safety.CoolantWarn;
        CoolantCritical = c.Safety.CoolantCritical;

        if (!c.Rgb.RecentColors.SequenceEqual(RecentColors))
        {
            RecentColors.Clear();
            foreach (var hex in c.Rgb.RecentColors) RecentColors.Add(hex);
        }

        var names = c.Profiles.Select(p => p.Name).ToList();
        if (!names.SequenceEqual(Profiles.Select(p => p.Name)))
        {
            Profiles.Clear();
            foreach (var n in names) Profiles.Add(new ProfileItem(this, n));
            OnPropertyChanged(nameof(HasProfiles));
        }

        RefreshGroups();
        RefreshPresetChips();
        LoadLcdConfig(c);
        LoadLightingExtras(c);
        LoadEffects(c);
        LoadAutomation(c);
        LcdConfigChanged?.Invoke();
        OnPropertiesChanged(nameof(FanCountLabel), nameof(CurveCurrentOutput));
    }

    public bool HasProfiles => Profiles.Count > 0;
}
