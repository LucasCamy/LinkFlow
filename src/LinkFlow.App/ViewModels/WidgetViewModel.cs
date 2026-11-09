using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>Um item exibido no widget (valor atualizado 1x por segundo).</summary>
public sealed class WidgetItemViewModel : ObservableObject
{
    private readonly WidgetViewModel _owner;
    private string _text = "--";
    private double _ratio;
    private bool _available;

    public WidgetItemViewModel(WidgetViewModel owner, WidgetItemSetting setting, SensorDef? def)
    {
        _owner = owner;
        Setting = setting;
        Def = def;
    }

    public WidgetItemSetting Setting { get; }
    public SensorDef? Def { get; private set; }
    public string Id => Setting.Id;
    public string Label => Def?.ShortLabel.ToUpperInvariant() ?? Setting.Id;
    public string FullLabel => Def?.Label ?? $"{Setting.Id} (não encontrado)";
    public string Group => Def?.Group ?? string.Empty;
    public string Color => _owner.UseSensorColors ? Def?.Color ?? "#94A3B8" : _owner.AccentColor;
    public bool CanToggleDisplay => Def is { HasPercent: true, HasValue: true };
    public bool IsPercent => Setting.Display == SensorDisplay.Percent;
    public string DisplayLabel => IsPercent ? "%" : "Valor";
    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public double Ratio { get => _ratio; private set => SetProperty(ref _ratio, value); }
    public bool HasRatio => _available && Def != null;
    public bool IsAvailable { get => _available; private set => SetProperty(ref _available, value); }

    public void Update(EngineSnapshot s)
    {
        Def ??= SensorCatalog.Find(Setting.Id, s); // discos/ventoinhas podem aparecer depois
        if (Def == null)
        {
            Text = "--";
            IsAvailable = false;
            return;
        }
        var display = Def.HasPercent && !Def.HasValue ? SensorDisplay.Percent : Def.HasValue && !Def.HasPercent ? SensorDisplay.Value : Setting.Display;
        var r = Def.Read(s, display);
        Text = r.Text;
        Ratio = r.Ratio ?? 0;
        IsAvailable = r.Available;
        OnPropertyChanged(nameof(HasRatio));
    }

    public void RefreshStyle() => OnPropertiesChanged(nameof(Color), nameof(Label), nameof(FullLabel), nameof(DisplayLabel), nameof(IsPercent), nameof(CanToggleDisplay));
}

/// <summary>Opção do catálogo para adicionar ao widget.</summary>
public sealed record WidgetPickerItem(string Id, string Label, string Group, string Color, bool IsAdded);

public sealed record WidgetPickerGroup(string Name, IReadOnlyList<WidgetPickerItem> Items);

/// <summary>Widget flutuante: o que mostrar, aparência e comportamento. Compartilhado pela janela e pela aba de configuração.</summary>
public sealed class WidgetViewModel : ObservableObject
{
    private readonly WidgetSettings _s;
    private readonly Action _save;
    private readonly MainViewModel _main;
    private IReadOnlyList<WidgetPickerGroup> _picker = Array.Empty<WidgetPickerGroup>();
    private string _pickerSignature = string.Empty;
    private bool _isVisible;

    public WidgetViewModel(WidgetSettings settings, Action save, MainViewModel main)
    {
        _s = settings;
        _s.Normalize();
        _save = save;
        _main = main;
        foreach (var i in _s.Items) Items.Add(new WidgetItemViewModel(this, i, SensorCatalog.Find(i.Id, main.Snapshot)));
        Update(main.Snapshot);

        AddCommand = new RelayCommand(p => { if (p is string id) Add(id); });
        RemoveCommand = new RelayCommand(p => { if (p is WidgetItemViewModel i) Remove(i); });
        MoveUpCommand = new RelayCommand(p => Move(p as WidgetItemViewModel, -1));
        MoveDownCommand = new RelayCommand(p => Move(p as WidgetItemViewModel, +1));
        ToggleDisplayCommand = new RelayCommand(p => { if (p is WidgetItemViewModel i) ToggleDisplay(i); });
        SetLayoutCommand = new RelayCommand(p => { if (Enum.TryParse<WidgetLayout>(p?.ToString(), out var l)) Layout = l; });
        SetBackgroundCommand = new RelayCommand(p => { if (p is string hex) Background = hex; });
        SetAccentCommand = new RelayCommand(p => { if (p is string hex) { AccentColor = hex; UseSensorColors = false; } });
        SetFontCommand = new RelayCommand(p => { if (p is string f) FontFamily = f; });
        ResetCommand = new RelayCommand(Reset);
    }

    public ObservableCollection<WidgetItemViewModel> Items { get; } = new();
    public ICommand AddCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ToggleDisplayCommand { get; }
    public ICommand SetLayoutCommand { get; }
    public ICommand SetBackgroundCommand { get; }
    public ICommand SetAccentCommand { get; }
    public ICommand SetFontCommand { get; }
    public ICommand ResetCommand { get; }

    public event Action? BehaviorChanged;

    /// <summary>A janela do widget está aberta (para pedir leitura de discos só quando preciso).</summary>
    public bool IsWidgetVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public SensorDemand Demand => SensorCatalog.DemandFor(_s.Items.Select(i => i.Id));
    public bool HasItems => Items.Count > 0;

    // ------------------------------------------------------------------ Atualização

    public void Update(EngineSnapshot s)
    {
        foreach (var i in Items) i.Update(s);
    }

    /// <summary>Catálogo agrupado para o seletor (recalculado só quando muda o hardware ou os itens).</summary>
    public IReadOnlyList<WidgetPickerGroup> Picker
    {
        get
        {
            var all = SensorCatalog.All(_main.Snapshot);
            var added = _s.Items.Select(i => i.Id).ToHashSet();
            var sig = string.Join("|", all.Select(d => d.Id)) + "#" + string.Join("|", added);
            if (sig != _pickerSignature)
            {
                _pickerSignature = sig;
                _picker = all.GroupBy(d => d.Group)
                    .Select(g => new WidgetPickerGroup(g.Key, g.Select(d => new WidgetPickerItem(d.Id, d.Label, d.Group, d.Color, added.Contains(d.Id))).ToList()))
                    .ToList();
            }
            return _picker;
        }
    }

    public void RefreshPicker() => OnPropertyChanged(nameof(Picker));

    // ------------------------------------------------------------------ Itens

    public void Add(string id)
    {
        if (_s.Items.Any(i => i.Id == id))
        {
            Remove(Items.First(i => i.Id == id));
            return;
        }
        var def = SensorCatalog.Find(id, _main.Snapshot);
        var setting = new WidgetItemSetting { Id = id, Display = def is { HasPercent: true, HasValue: false } ? SensorDisplay.Percent : SensorDisplay.Value };
        _s.Items.Add(setting);
        var vm = new WidgetItemViewModel(this, setting, def);
        vm.Update(_main.Snapshot);
        Items.Add(vm);
        Changed();
    }

    public void Remove(WidgetItemViewModel item)
    {
        _s.Items.Remove(item.Setting);
        Items.Remove(item);
        Changed();
    }

    private void Move(WidgetItemViewModel? item, int delta)
    {
        if (item == null) return;
        int i = Items.IndexOf(item), j = i + delta;
        if (i < 0 || j < 0 || j >= Items.Count) return;
        Items.Move(i, j);
        (_s.Items[i], _s.Items[j]) = (_s.Items[j], _s.Items[i]);
        Changed();
    }

    private void ToggleDisplay(WidgetItemViewModel item)
    {
        item.Setting.Display = item.IsPercent ? SensorDisplay.Value : SensorDisplay.Percent;
        item.Update(_main.Snapshot);
        item.RefreshStyle();
        Changed();
    }

    private void Changed()
    {
        _save();
        OnPropertiesChanged(nameof(HasItems), nameof(Picker), nameof(Demand));
        _main.UpdateSensorDemand();
    }

    private void Reset()
    {
        var d = new WidgetSettings();
        _s.Items = WidgetSettings.DefaultItems();
        Items.Clear();
        foreach (var i in _s.Items) Items.Add(new WidgetItemViewModel(this, i, SensorCatalog.Find(i.Id, _main.Snapshot)));
        Update(_main.Snapshot);
        _s.Layout = d.Layout; _s.GridColumns = d.GridColumns; _s.Scale = d.Scale; _s.Opacity = d.Opacity;
        _s.Background = d.Background; _s.BackgroundOpacity = d.BackgroundOpacity; _s.CornerRadius = d.CornerRadius;
        _s.UseSensorColors = d.UseSensorColors; _s.AccentColor = d.AccentColor; _s.FontFamily = d.FontFamily;
        _s.ShowLabels = d.ShowLabels; _s.ShowBars = d.ShowBars; _s.ShowBorder = d.ShowBorder;
        Changed();
        OnPropertyChanged(string.Empty); // atualiza tudo
        foreach (var i in Items) i.RefreshStyle();
        BehaviorChanged?.Invoke();
    }

    // ------------------------------------------------------------------ Aparência

    private void Set<T>(T current, T value, Action<T> apply, bool behavior = false, bool restyleItems = false,
        [System.Runtime.CompilerServices.CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        _save();
        OnPropertyChanged(name);
        foreach (var a in also) OnPropertyChanged(a);
        if (restyleItems) foreach (var i in Items) i.RefreshStyle();
        if (behavior) BehaviorChanged?.Invoke();
    }

    public WidgetLayout Layout
    {
        get => _s.Layout;
        set => Set(_s.Layout, value, v => _s.Layout = v, also: new[] { nameof(IsHorizontal), nameof(IsVertical), nameof(IsGrid) });
    }

    public bool IsHorizontal => _s.Layout == WidgetLayout.Horizontal;
    public bool IsVertical => _s.Layout == WidgetLayout.Vertical;
    public bool IsGrid => _s.Layout == WidgetLayout.Grid;

    public int GridColumns { get => _s.GridColumns; set => Set(_s.GridColumns, Math.Clamp(value, 1, 6), v => _s.GridColumns = v); }
    public double Scale { get => _s.Scale; set => Set(_s.Scale, Math.Round(value, 2), v => _s.Scale = v, also: new[] { nameof(ScaleText) }); }
    public string ScaleText => $"{_s.Scale * 100:0}%";
    public double Opacity { get => _s.Opacity; set => Set(_s.Opacity, Math.Round(value, 2), v => _s.Opacity = v, also: new[] { nameof(OpacityText) }); }
    public string OpacityText => $"{_s.Opacity * 100:0}%";

    public string Background
    {
        get => _s.Background;
        set => Set(_s.Background, value, v => _s.Background = v, also: new[] { nameof(BackgroundBrush) });
    }

    public double BackgroundOpacity
    {
        get => _s.BackgroundOpacity;
        set => Set(_s.BackgroundOpacity, Math.Round(value, 2), v => _s.BackgroundOpacity = v, also: new[] { nameof(BackgroundBrush), nameof(BackgroundOpacityText) });
    }

    public string BackgroundOpacityText => $"{_s.BackgroundOpacity * 100:0}%";

    public Brush BackgroundBrush
    {
        get
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(_s.Background); }
            catch { c = Color.FromRgb(11, 17, 32); }
            c.A = (byte)Math.Round(_s.BackgroundOpacity * 255);
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    public double CornerRadius { get => _s.CornerRadius; set => Set(_s.CornerRadius, Math.Round(value), v => _s.CornerRadius = v); }
    public bool UseSensorColors { get => _s.UseSensorColors; set => Set(_s.UseSensorColors, value, v => _s.UseSensorColors = v, restyleItems: true); }
    public string AccentColor { get => _s.AccentColor; set => Set(_s.AccentColor, value, v => _s.AccentColor = v, restyleItems: true); }
    public string FontFamily { get => _s.FontFamily; set => Set(_s.FontFamily, value, v => _s.FontFamily = v); }
    public bool ShowLabels { get => _s.ShowLabels; set => Set(_s.ShowLabels, value, v => _s.ShowLabels = v); }
    public bool ShowBars { get => _s.ShowBars; set => Set(_s.ShowBars, value, v => _s.ShowBars = v); }
    public bool ShowBorder { get => _s.ShowBorder; set => Set(_s.ShowBorder, value, v => _s.ShowBorder = v); }

    // ------------------------------------------------------------------ Comportamento

    public bool AlwaysOnTop { get => _s.AlwaysOnTop; set => Set(_s.AlwaysOnTop, value, v => _s.AlwaysOnTop = v, behavior: true); }
    public bool ClickThrough { get => _s.ClickThrough; set => Set(_s.ClickThrough, value, v => _s.ClickThrough = v, behavior: true); }
    public bool HideInFullscreen { get => _s.HideInFullscreen; set => Set(_s.HideInFullscreen, value, v => _s.HideInFullscreen = v, behavior: true); }
    public bool SnapToEdges { get => _s.SnapToEdges; set => Set(_s.SnapToEdges, value, v => _s.SnapToEdges = v); }
    public bool LockPosition { get => _s.LockPosition; set => Set(_s.LockPosition, value, v => _s.LockPosition = v); }

    public WidgetSettings Settings => _s;

    public static IReadOnlyList<string> BackgroundOptions { get; } = new[] { "#0B1120", "#000000", "#111827", "#1E1B4B", "#0F2A2E", "#2A0F1E", "#F8FAFC" };
    public static IReadOnlyList<string> AccentOptions { get; } = new[] { "#00E5FF", "#34D399", "#A78BFA", "#F472B6", "#FBBF24", "#F87171", "#FFFFFF" };
    public static IReadOnlyList<string> FontOptions { get; } = new[] { "Segoe UI Variable Text", "Bahnschrift", "Consolas", "Segoe UI Black" };
}
