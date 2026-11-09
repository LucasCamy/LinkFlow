using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

public sealed record LcdMetricOption(LcdMetric Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Informação que pode entrar no quadro do LCD (chip com ordem de exibição).</summary>
public sealed class LcdMetricChoice : ObservableObject
{
    private int _order;
    public LcdMetricChoice(LcdMetric metric, string label) { Metric = metric; Label = label; }
    public LcdMetric Metric { get; }
    public string Label { get; }

    /// <summary>Posição no quadro (1–4) ou 0 se não estiver no quadro.</summary>
    public int Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, value)) OnPropertiesChanged(nameof(IsSelected), nameof(OrderText));
        }
    }

    public bool IsSelected => _order > 0;
    public string OrderText => _order > 0 ? _order.ToString() : string.Empty;
}

/// <summary>Imagem ou GIF da apresentação do LCD.</summary>
public sealed class LcdMediaItemViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private int _duration;
    private ImageSource? _thumbnail;
    private bool _isCurrent;

    public LcdMediaItemViewModel(MainViewModel owner, LcdMediaItem item, int index)
    {
        _owner = owner;
        Path = item.Path;
        Name = string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileName(item.Path) : item.Name;
        _duration = item.DurationSeconds;
        Index = index;
        IsGif = string.Equals(System.IO.Path.GetExtension(item.Path), ".gif", StringComparison.OrdinalIgnoreCase);
        Exists = File.Exists(item.Path);
    }

    public string Path { get; }
    public string Name { get; }
    public int Index { get; }
    public bool IsGif { get; }
    public bool Exists { get; }
    public string Kind => !Exists ? "Arquivo não encontrado" : IsGif ? "GIF animado" : "Imagem";
    public string Position => $"{Index + 1}";

    public int DurationSeconds
    {
        get => _duration;
        set
        {
            int v = Math.Clamp(value, 2, 3600);
            if (!SetProperty(ref _duration, v)) return;
            OnPropertyChanged(nameof(DurationText));
            _owner.OnMediaDurationChanged(this);
        }
    }

    public string DurationText => _duration < 60 ? $"{_duration} s" : $"{_duration / 60} min {(_duration % 60 > 0 ? $"{_duration % 60} s" : "")}".Trim();

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>Miniatura carregada sob demanda (decodificada em tamanho pequeno).</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbnail == null && Exists)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bmp.DecodePixelWidth = 96;
                    bmp.UriSource = new Uri(Path);
                    bmp.EndInit();
                    bmp.Freeze();
                    _thumbnail = bmp;
                }
                catch (Exception ex)
                {
                    Log.Debug($"Miniatura indisponível para {Path}: {ex.Message}");
                }
            }
            return _thumbnail;
        }
    }
}

public partial class MainViewModel
{
    private LcdMode _lcdMode;
    private int _lcdRotation;
    private string _lcdTitle = string.Empty;
    private LcdMetric _lcdMainMetric;
    private bool _lcdShowRing;
    private string? _lcdBackgroundPath;
    private byte _lcdBackgroundDim;
    private LcdFit _lcdFit;
    private bool _lcdMediaOverlay;
    private LcdMetric _lcdOverlayMetric;
    private ImageSource? _lcdPreview;

    private ICommand? _setLcdModeCommand, _setLcdRotationCommand, _toggleSecondaryCommand, _setLcdFitCommand,
        _removeMediaCommand, _moveMediaUpCommand, _moveMediaDownCommand, _removeBackgroundCommand, _clearMediaCommand;

    public static IReadOnlyList<LcdMetricOption> LcdMetricOptions { get; } = new[]
    {
        new LcdMetricOption(LcdMetric.CpuTemp, "Temperatura da CPU"),
        new LcdMetricOption(LcdMetric.CpuLoad, "Uso da CPU"),
        new LcdMetricOption(LcdMetric.CpuPower, "Potência da CPU (W)"),
        new LcdMetricOption(LcdMetric.CpuClock, "Clock da CPU"),
        new LcdMetricOption(LcdMetric.GpuTemp, "Temperatura da GPU"),
        new LcdMetricOption(LcdMetric.GpuHotspot, "Hotspot da GPU"),
        new LcdMetricOption(LcdMetric.GpuLoad, "Uso da GPU"),
        new LcdMetricOption(LcdMetric.GpuPower, "Potência da GPU (W)"),
        new LcdMetricOption(LcdMetric.CoolantTemp, "Temperatura do líquido"),
        new LcdMetricOption(LcdMetric.PumpRpm, "Rotação da bomba"),
        new LcdMetricOption(LcdMetric.FanRpm, "Rotação média das ventoinhas"),
        new LcdMetricOption(LcdMetric.FanPower, "Potência das ventoinhas (%)"),
        new LcdMetricOption(LcdMetric.RamUsage, "Uso da memória"),
        new LcdMetricOption(LcdMetric.Clock, "Relógio (hora e data)"),
    };

    public ObservableCollection<LcdMetricChoice> LcdSecondaryChoices { get; } = new(
        LcdMetricOptions.Select(o => new LcdMetricChoice(o.Value, ShortLabel(o.Value))));

    public ObservableCollection<LcdMediaItemViewModel> LcdMedia { get; } = new();

    private static string ShortLabel(LcdMetric m) => m switch
    {
        LcdMetric.CpuTemp => "CPU °C",
        LcdMetric.CpuLoad => "CPU %",
        LcdMetric.CpuPower => "CPU W",
        LcdMetric.CpuClock => "CPU GHz",
        LcdMetric.GpuTemp => "GPU °C",
        LcdMetric.GpuHotspot => "Hotspot",
        LcdMetric.GpuLoad => "GPU %",
        LcdMetric.GpuPower => "GPU W",
        LcdMetric.CoolantTemp => "Líquido",
        LcdMetric.PumpRpm => "Bomba RPM",
        LcdMetric.FanRpm => "Fans RPM",
        LcdMetric.FanPower => "Fans %",
        LcdMetric.RamUsage => "RAM",
        LcdMetric.Clock => "Relógio",
        _ => m.ToString()
    };

    // ------------------------------------------------------------------ Comandos

    public ICommand SetLcdModeCommand => _setLcdModeCommand ??= new RelayCommand(p =>
        UpdateLcd(l => l.Mode = p?.ToString() == "Media" ? LcdMode.Media : LcdMode.Metrics));

    public ICommand SetLcdRotationCommand => _setLcdRotationCommand ??= new RelayCommand(p =>
    {
        if (int.TryParse(p?.ToString(), out var deg)) UpdateLcd(l => l.Rotation = deg);
    });

    public ICommand SetLcdFitCommand => _setLcdFitCommand ??= new RelayCommand(p =>
        UpdateLcd(l => l.Fit = p?.ToString() == "Contain" ? LcdFit.Contain : LcdFit.Cover));

    public ICommand ToggleSecondaryCommand => _toggleSecondaryCommand ??= new RelayCommand(p =>
    {
        if (p is not LcdMetric metric) return;
        var current = _engine.Config.Lcd.SecondaryMetrics;
        if (current.Contains(metric)) UpdateLcd(l => l.SecondaryMetrics.Remove(metric));
        else if (current.Count >= LcdConfig.MaxSecondary)
            ShowToast($"O quadro mostra até {LcdConfig.MaxSecondary} informações secundárias. Desmarque uma antes.", canUndo: false);
        else UpdateLcd(l => l.SecondaryMetrics.Add(metric));
    });

    public ICommand RemoveMediaCommand => _removeMediaCommand ??= new RelayCommand(p =>
    {
        if (p is LcdMediaItemViewModel item)
            WithUndo($"\"{item.Name}\" removido do LCD", () => UpdateLcd(l => l.Media.RemoveAt(Math.Min(item.Index, l.Media.Count - 1))));
    });

    public ICommand ClearMediaCommand => _clearMediaCommand ??= new RelayCommand(() =>
        WithUndo("Imagens removidas do LCD", () => UpdateLcd(l => l.Media.Clear())), () => LcdMedia.Count > 0);

    public ICommand MoveMediaUpCommand => _moveMediaUpCommand ??= new RelayCommand(p => MoveMedia(p as LcdMediaItemViewModel, -1));
    public ICommand MoveMediaDownCommand => _moveMediaDownCommand ??= new RelayCommand(p => MoveMedia(p as LcdMediaItemViewModel, +1));

    public ICommand RemoveBackgroundCommand => _removeBackgroundCommand ??= new RelayCommand(() => UpdateLcd(l => l.BackgroundPath = null));

    private void MoveMedia(LcdMediaItemViewModel? item, int delta)
    {
        if (item == null) return;
        UpdateLcd(l =>
        {
            int i = item.Index, j = i + delta;
            if (i < 0 || i >= l.Media.Count || j < 0 || j >= l.Media.Count) return;
            (l.Media[i], l.Media[j]) = (l.Media[j], l.Media[i]);
        });
    }

    private void UpdateLcd(Action<LcdConfig> mutate)
    {
        _engine.UpdateConfig(c => mutate(c.Lcd));
        LoadLcdConfig(_engine.Config);
        LcdConfigChanged?.Invoke();
    }

    /// <summary>A prévia escuta para redesenhar na hora.</summary>
    public event Action? LcdConfigChanged;

    internal void OnMediaDurationChanged(LcdMediaItemViewModel item)
    {
        if (IsLoadingConfig) return;
        int seconds = item.DurationSeconds;
        _engine.UpdateConfig(c =>
        {
            if (item.Index < c.Lcd.Media.Count) c.Lcd.Media[item.Index].DurationSeconds = seconds;
        });
        LcdConfigChanged?.Invoke();
    }

    /// <summary>Adiciona imagens/GIFs já copiados para a biblioteca de mídia.</summary>
    public void AddLcdMedia(IReadOnlyList<(string Path, string Name)> files)
    {
        if (files.Count == 0) return;
        WithUndo(files.Count == 1 ? $"\"{files[0].Name}\" adicionado ao LCD" : $"{files.Count} arquivos adicionados ao LCD", () =>
            UpdateLcd(l =>
            {
                foreach (var (path, name) in files)
                    l.Media.Add(new LcdMediaItem { Path = path, Name = name, DurationSeconds = 10 });
                l.Mode = LcdMode.Media;
            }));
    }

    public void SetLcdBackground(string path) => UpdateLcd(l => l.BackgroundPath = path);

    // ------------------------------------------------------------------ Propriedades

    public bool IsLcdMetricsMode => _lcdMode == LcdMode.Metrics;
    public bool IsLcdMediaMode => _lcdMode == LcdMode.Media;
    public int LcdRotation => _lcdRotation;
    public bool LcdFitCover => _lcdFit == LcdFit.Cover;
    public bool LcdFitContain => _lcdFit == LcdFit.Contain;
    public bool HasLcdMedia => LcdMedia.Count > 0;
    public bool HasLcdBackground => !string.IsNullOrEmpty(_lcdBackgroundPath);
    public string LcdBackgroundName => _lcdBackgroundPath is { } p ? Path.GetFileName(p) : "Nenhuma";
    public int LcdSecondaryCount => LcdSecondaryChoices.Count(c => c.IsSelected);
    public string LcdSecondarySummary => $"{LcdSecondaryCount} de {LcdConfig.MaxSecondary} • clique para adicionar ou tirar (a ordem do clique é a ordem no quadro)";

    public string LcdNowShowing => Snapshot.LcdConnected
        ? $"Exibindo: {Snapshot.LcdStatus ?? (IsLcdMediaMode ? "Imagens" : "Métricas")}"
        : "Display não conectado • a prévia mostra como vai ficar";

    public string LcdTitle
    {
        get => _lcdTitle;
        set
        {
            if (!SetProperty(ref _lcdTitle, value ?? string.Empty) || IsLoadingConfig) return;
            var t = _lcdTitle;
            _engine.UpdateConfig(c => c.Lcd.Title = t);
            LcdConfigChanged?.Invoke();
        }
    }

    public LcdMetric LcdMainMetric
    {
        get => _lcdMainMetric;
        set
        {
            if (!SetProperty(ref _lcdMainMetric, value) || IsLoadingConfig) return;
            UpdateLcd(l => l.MainMetric = value);
        }
    }

    public bool LcdShowRing
    {
        get => _lcdShowRing;
        set
        {
            if (!SetProperty(ref _lcdShowRing, value) || IsLoadingConfig) return;
            UpdateLcd(l => l.ShowRing = value);
        }
    }

    public byte LcdBackgroundDim
    {
        get => _lcdBackgroundDim;
        set
        {
            if (!SetProperty(ref _lcdBackgroundDim, value) || IsLoadingConfig) return;
            OnPropertyChanged(nameof(LcdBackgroundDimText));
            _engine.UpdateConfig(c => c.Lcd.BackgroundDim = value);
            LcdConfigChanged?.Invoke();
        }
    }

    public string LcdBackgroundDimText => $"{_lcdBackgroundDim}%";

    public bool LcdMediaOverlay
    {
        get => _lcdMediaOverlay;
        set
        {
            if (!SetProperty(ref _lcdMediaOverlay, value) || IsLoadingConfig) return;
            UpdateLcd(l => l.MediaOverlay = value);
        }
    }

    public LcdMetric LcdOverlayMetric
    {
        get => _lcdOverlayMetric;
        set
        {
            if (!SetProperty(ref _lcdOverlayMetric, value) || IsLoadingConfig) return;
            UpdateLcd(l => l.OverlayMetric = value);
        }
    }

    /// <summary>Quadro renderizado pela prévia (mesmo código que gera o quadro do display).</summary>
    public ImageSource? LcdPreview
    {
        get => _lcdPreview;
        set => SetProperty(ref _lcdPreview, value);
    }

    // ------------------------------------------------------------------ Configuração → UI

    private void LoadLcdConfig(ControlConfig c)
    {
        var l = c.Lcd;
        // Campos atribuídos direto (sem passar pelos setters): não gera escrita de volta no engine
        {
            _lcdMode = l.Mode;
            _lcdRotation = l.Rotation;
            _lcdFit = l.Fit;
            if (_lcdTitle != l.Title) { _lcdTitle = l.Title; OnPropertyChanged(nameof(LcdTitle)); }
            if (_lcdMainMetric != l.MainMetric) { _lcdMainMetric = l.MainMetric; OnPropertyChanged(nameof(LcdMainMetric)); }
            if (_lcdShowRing != l.ShowRing) { _lcdShowRing = l.ShowRing; OnPropertyChanged(nameof(LcdShowRing)); }
            if (_lcdBackgroundDim != l.BackgroundDim) { _lcdBackgroundDim = l.BackgroundDim; OnPropertiesChanged(nameof(LcdBackgroundDim), nameof(LcdBackgroundDimText)); }
            if (_lcdMediaOverlay != l.MediaOverlay) { _lcdMediaOverlay = l.MediaOverlay; OnPropertyChanged(nameof(LcdMediaOverlay)); }
            if (_lcdOverlayMetric != l.OverlayMetric) { _lcdOverlayMetric = l.OverlayMetric; OnPropertyChanged(nameof(LcdOverlayMetric)); }
            _lcdBackgroundPath = l.BackgroundPath;

            foreach (var choice in LcdSecondaryChoices)
                choice.Order = l.SecondaryMetrics.IndexOf(choice.Metric) + 1;

            var paths = l.Media.Select(m => (m.Path, m.DurationSeconds)).ToList();
            if (!paths.SequenceEqual(LcdMedia.Select(m => (m.Path, m.DurationSeconds))))
            {
                LcdMedia.Clear();
                for (int i = 0; i < l.Media.Count; i++) LcdMedia.Add(new LcdMediaItemViewModel(this, l.Media[i], i));
            }
        }

        OnPropertiesChanged(nameof(IsLcdMetricsMode), nameof(IsLcdMediaMode), nameof(LcdRotation), nameof(LcdFitCover), nameof(LcdFitContain),
            nameof(HasLcdMedia), nameof(HasLcdBackground), nameof(LcdBackgroundName), nameof(LcdSecondaryCount), nameof(LcdSecondarySummary),
            nameof(LcdNowShowing));
    }
}
