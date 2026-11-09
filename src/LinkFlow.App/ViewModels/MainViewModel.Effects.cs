using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>Um efeito de iluminação na lista de escolha.</summary>
public sealed class RgbEffectOption : ObservableObject
{
    private bool _isSelected;

    public RgbEffectOption(RgbEffect effect, string name, string icon, string description)
    {
        Effect = effect;
        Name = name;
        Icon = icon;
        Description = description;
    }

    public RgbEffect Effect { get; }
    public string Name { get; }
    public string Icon { get; }
    public string Description { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>Paleta pronta (mostrada como um degradê clicável).</summary>
public sealed record PalettePreset(string Name, IReadOnlyList<string> Colors, Brush Preview);

/// <summary>Efeitos (respirar, arco-íris, paleta, aleatório), velocidade e paleta de cores.</summary>
public partial class MainViewModel
{
    private RgbEffect _effect;
    private byte _effectSpeed = 5;
    private bool _spreadOnFans = true;
    private ICommand? _selectEffectCommand, _addPaletteColorCommand, _removePaletteColorCommand, _applyPalettePresetCommand;

    public IReadOnlyList<RgbEffectOption> EffectOptions { get; } = new[]
    {
        new RgbEffectOption(RgbEffect.Static, "Cor fixa", "", "Todos os dispositivos ficam com a cor escolhida."),
        new RgbEffectOption(RgbEffect.Breathing, "Respirar", "", "A cor escolhida acende e apaga devagar, como uma respiração."),
        new RgbEffectOption(RgbEffect.Rainbow, "Arco-íris", "", "Passa por todas as cores. Espalhado, forma uma onda pelas ventoinhas."),
        new RgbEffectOption(RgbEffect.Palette, "Paleta", "", "Passa suavemente pelas cores da paleta, na ordem."),
        new RgbEffectOption(RgbEffect.Random, "Aleatório", "", "Sorteia uma cor nova de tempos em tempos, com transição suave."),
    };

    public IReadOnlyList<PalettePreset> PalettePresets { get; } = RgbEffects.Presets
        .Select(p => new PalettePreset(p.Name, p.Colors, Gradient(p.Colors))).ToList();

    public ObservableCollection<string> PaletteColors { get; } = new();

    public RgbEffect Effect
    {
        get => _effect;
        set
        {
            if (_effect == value) return;
            _effect = value;
            foreach (var o in EffectOptions) o.IsSelected = o.Effect == value;
            OnPropertiesChanged(nameof(Effect), nameof(EffectDescription), nameof(IsEffectAnimated), nameof(UsesPickedColor),
                nameof(IsPaletteEffect), nameof(EffectColorHint), nameof(CanSpread));
            if (!_loadingConfig) _engine.UpdateConfig(c => c.Rgb.Effect = value);
        }
    }

    public string EffectDescription => EffectOptions.First(o => o.Effect == _effect).Description;
    public bool IsEffectAnimated => _effect != RgbEffect.Static;
    public bool IsPaletteEffect => _effect == RgbEffect.Palette;
    public bool CanSpread => _effect != RgbEffect.Static;

    /// <summary>A cor do quadro só vale na cor fixa e no respirar; nos outros efeitos ela alimenta a paleta.</summary>
    public bool UsesPickedColor => _effect is RgbEffect.Static or RgbEffect.Breathing or RgbEffect.Palette;

    public string EffectColorHint => _effect switch
    {
        RgbEffect.Palette => "Escolha uma cor e toque em \"Adicionar à paleta\".",
        RgbEffect.Rainbow => "O arco-íris usa todas as cores: a cor escolhida fica guardada para quando voltar à cor fixa.",
        RgbEffect.Random => "O aleatório sorteia as cores: a cor escolhida fica guardada para quando voltar à cor fixa.",
        _ => string.Empty
    };

    public byte EffectSpeed
    {
        get => _effectSpeed;
        set
        {
            value = Math.Clamp(value, (byte)1, (byte)10);
            if (!SetProperty(ref _effectSpeed, value)) return;
            OnPropertyChanged(nameof(EffectSpeedText));
            if (!_loadingConfig) _engine.UpdateConfig(c => c.Rgb.EffectSpeed = value);
        }
    }

    public string EffectSpeedText => _effectSpeed switch { <= 2 => "Bem lento", <= 4 => "Lento", <= 6 => "Médio", <= 8 => "Rápido", _ => "Muito rápido" };

    public bool SpreadOnFans
    {
        get => _spreadOnFans;
        set
        {
            if (!SetProperty(ref _spreadOnFans, value)) return;
            if (!_loadingConfig) _engine.UpdateConfig(c => c.Rgb.SpreadOnFans = value);
        }
    }

    /// <summary>Fonte da prévia animada: lê a configuração atual a cada quadro, sem cópia.</summary>
    public Func<RgbConfig> RgbPreviewSource => () => _engine.Config.Rgb;

    /// <summary>Quantas ventoinhas desenhar na prévia (as do hub, entre 3 e 8).</summary>
    public int PreviewFanCount => Math.Clamp(Fans.Count(f => !f.IsPump), 3, 8);

    public ICommand SelectEffectCommand => _selectEffectCommand ??= new RelayCommand(p =>
    {
        if (p is RgbEffectOption o) Effect = o.Effect;
        else if (p is RgbEffect e) Effect = e;
        // Clicar no efeito já escolhido não pode desmarcá-lo
        foreach (var opt in EffectOptions) opt.IsSelected = opt.Effect == _effect;
    });

    public bool CanAddPaletteColor => PaletteColors.Count < RgbEffects.MaxPalette;

    public ICommand AddPaletteColorCommand => _addPaletteColorCommand ??= new RelayCommand(() =>
    {
        if (!CanAddPaletteColor) return;
        string hex = CurrentRgbHex;
        UpdatePalette(list => list.Add(hex));
    });

    public ICommand RemovePaletteColorCommand => _removePaletteColorCommand ??= new RelayCommand(p =>
    {
        if (p is not string hex || PaletteColors.Count <= 1) return;
        int index = PaletteColors.IndexOf(hex);
        if (index >= 0) UpdatePalette(list => list.RemoveAt(index));
    });

    public ICommand ApplyPalettePresetCommand => _applyPalettePresetCommand ??= new RelayCommand(p =>
    {
        if (p is not PalettePreset preset) return;
        UpdatePalette(list => { list.Clear(); list.AddRange(preset.Colors); });
        if (_effect is RgbEffect.Static or RgbEffect.Breathing) Effect = RgbEffect.Palette;
    });

    private void UpdatePalette(Action<List<string>> change)
    {
        var list = PaletteColors.ToList();
        change(list);
        list = list.Take(RgbEffects.MaxPalette).ToList();
        if (list.Count == 0) return;
        SyncPalette(list);
        _engine.UpdateConfig(c => c.Rgb.Palette = list.ToList());
    }

    private void SyncPalette(IReadOnlyList<string> colors)
    {
        if (PaletteColors.SequenceEqual(colors)) return;
        PaletteColors.Clear();
        foreach (var c in colors) PaletteColors.Add(c);
        OnPropertyChanged(nameof(CanAddPaletteColor));
    }

    private void LoadEffects(ControlConfig c)
    {
        Effect = c.Rgb.Effect;
        foreach (var o in EffectOptions) o.IsSelected = o.Effect == c.Rgb.Effect;
        EffectSpeed = c.Rgb.EffectSpeed;
        SpreadOnFans = c.Rgb.SpreadOnFans;
        SyncPalette(c.Rgb.Palette);
        OnPropertyChanged(nameof(PreviewFanCount));
    }

    private static Brush Gradient(IReadOnlyList<string> colors)
    {
        var stops = new GradientStopCollection();
        for (int i = 0; i < colors.Count; i++)
            if (RgbEffects.TryParseHex(colors[i], out var c))
                stops.Add(new GradientStop(Color.FromRgb(c.R, c.G, c.B), colors.Count == 1 ? 0 : (double)i / (colors.Count - 1)));
        var brush = new LinearGradientBrush(stops, 0);
        brush.Freeze();
        return brush;
    }
}
