using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinkFlow.Core;
using LinkFlow.App.Services;
using LinkFlow.App.Resources;

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
    private bool? _exists;
    private bool _thumbnailDemanded, _thumbnailQueued, _thumbnailResolved, _retired;
    private string? _thumbnailError;

    public LcdMediaItemViewModel(MainViewModel owner, LcdMediaItem item, int index)
    {
        _owner = owner;
        Path = item.Path;
        Name = string.IsNullOrWhiteSpace(item.Name) ? System.IO.Path.GetFileName(item.Path) : item.Name;
        _duration = item.DurationSeconds;
        Index = index;
        IsGif = string.Equals(System.IO.Path.GetExtension(item.Path), ".gif", StringComparison.OrdinalIgnoreCase);
    }

    public string Path { get; }
    public string Name { get; }
    public int Index { get; }
    public bool IsGif { get; }
    public bool? Exists => _exists;
    public string Kind => _exists == false ? Strings.Lcd_ArquivoNaoEncontrado : _thumbnailResolved && _thumbnail == null
        ? OperationalText.Pick("Miniatura indisponível", "Thumbnail unavailable") : _exists == null ? OperationalText.Pick("Verificando arquivo…", "Checking file…")
        : IsGif ? Strings.Lcd_GIFAnimado : Strings.Lcd_Imagem;
    public bool IsThumbnailLoading => _thumbnailDemanded && !_thumbnailResolved && !_retired;
    public string? ThumbnailError => _thumbnailError;
    internal bool NeedsThumbnail => _thumbnailDemanded && !_thumbnailQueued && !_thumbnailResolved && !_retired;
    internal bool IsRetired => _retired;
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
            if (!_thumbnailDemanded && !_retired)
            {
                _thumbnailDemanded = true;
                _owner.DemandLcdThumbnail();
            }
            return _thumbnail;
        }
    }

    internal void ThumbnailQueued() => _thumbnailQueued = true;
    internal void ApplyThumbnail(LcdThumbnailResult result)
    {
        if (_retired) return;
        _thumbnail = result.Image; _exists = result.Exists; _thumbnailError = result.Error;
        _thumbnailResolved = true; _thumbnailQueued = false;
        OnPropertiesChanged(nameof(Thumbnail), nameof(Exists), nameof(Kind), nameof(IsThumbnailLoading), nameof(ThumbnailError));
    }
    internal void Retire() { _retired = true; _thumbnail = null; }
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
    private LcdThumbnailLoader? _lcdThumbnailLoader;
    private int _thumbnailPumpQueued;

    internal void DemandLcdThumbnail()
    {
        if (_uiWorkStopped || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished ||
            Interlocked.Exchange(ref _thumbnailPumpQueued, 1) != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            Interlocked.Exchange(ref _thumbnailPumpQueued, 0);
            if (_uiWorkStopped) return;
            if (_lcdThumbnailLoader == null)
            {
                _lcdThumbnailLoader = new();
                _lcdThumbnailLoader.CapacityAvailable += DemandLcdThumbnail;
            }
            // Pending demand is a flag on existing rows, not an unbounded queue of waiting Tasks.
            // Completion frees capacity and coalesces another dispatcher pass for the remaining rows.
            foreach (var item in LcdMedia.Where(item => item.NeedsThumbnail))
            {
                if (!_lcdThumbnailLoader.TryQueue(item.Path, result => PublishThumbnail(item, result))) break;
                item.ThumbnailQueued();
            }
        });
    }

    private void PublishThumbnail(LcdMediaItemViewModel item, LcdThumbnailResult result)
    {
        if (_uiWorkStopped || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_uiWorkStopped && !item.IsRetired) item.ApplyThumbnail(result);
        });
    }

    private ICommand? _setLcdModeCommand, _setLcdRotationCommand, _toggleSecondaryCommand, _setLcdFitCommand,
        _removeMediaCommand, _moveMediaUpCommand, _moveMediaDownCommand, _removeBackgroundCommand, _clearMediaCommand;

    public static IReadOnlyList<LcdMetricOption> LcdMetricOptions { get; } = new[]
    {
        new LcdMetricOption(LcdMetric.CpuTemp, Strings.Fan_TemperaturaCPU),
        new LcdMetricOption(LcdMetric.CpuLoad, Strings.Dash_CpuUsage),
        new LcdMetricOption(LcdMetric.CpuPower, Strings.Lcd_PotenciaCPU),
        new LcdMetricOption(LcdMetric.CpuClock, Strings.Lcd_ClockCPU),
        new LcdMetricOption(LcdMetric.GpuTemp, Strings.Fan_TemperaturaGPU),
        new LcdMetricOption(LcdMetric.GpuHotspot, Strings.Lcd_HotspotGPU),
        new LcdMetricOption(LcdMetric.GpuLoad, Strings.Dash_GpuUsage),
        new LcdMetricOption(LcdMetric.GpuPower, Strings.Lcd_PotenciaGPU),
        new LcdMetricOption(LcdMetric.CoolantTemp, Strings.Det_CoolantTemp),
        new LcdMetricOption(LcdMetric.PumpRpm, Strings.Lcd_RotacaoBomba),
        new LcdMetricOption(LcdMetric.FanRpm, Strings.Lcd_RotacaoMediaVentoinhas),
        new LcdMetricOption(LcdMetric.FanPower, Strings.Lcd_PotenciaVentoinhas),
        new LcdMetricOption(LcdMetric.RamUsage, Strings.Dash_MemoryUsage),
        new LcdMetricOption(LcdMetric.Clock, Strings.Lcd_RelogioHoraData),
        new LcdMetricOption(LcdMetric.CpuCoreMax, Strings.Lcd_NucleoCPUMaisOcupado),
        new LcdMetricOption(LcdMetric.NetDown, Strings.Lcd_DownloadRede),
        new LcdMetricOption(LcdMetric.NetUp, Strings.Lcd_UploadRede),
        new LcdMetricOption(LcdMetric.DiskUsage, Strings.Lcd_EspacoUsadoDisco),
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
        LcdMetric.GpuHotspot => Strings.Det_Hotspot,
        LcdMetric.GpuLoad => "GPU %",
        LcdMetric.GpuPower => "GPU W",
        LcdMetric.CoolantTemp => Strings.Det_Coolant,
        LcdMetric.PumpRpm => Strings.Lcd_BombaRPM,
        LcdMetric.FanRpm => Strings.Lcd_FansRPM,
        LcdMetric.FanPower => Strings.Lcd_Fans,
        LcdMetric.RamUsage => "RAM",
        LcdMetric.Clock => Strings.Lcd_Relogio,
        LcdMetric.CpuCoreMax => Strings.Lcd_Nucleo,
        LcdMetric.NetDown => Strings.Lcd_Rede,
        LcdMetric.NetUp => Strings.Lcd_Rede2,
        LcdMetric.DiskUsage => Strings.Lcd_Disco,
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
            ShowToast(string.Format(Strings.Lcd_QuadroMostraAteInformacoes, LcdConfig.MaxSecondary), canUndo: false);
        else UpdateLcd(l => l.SecondaryMetrics.Add(metric));
    });

    public ICommand RemoveMediaCommand => _removeMediaCommand ??= new RelayCommand(p =>
    {
        if (p is LcdMediaItemViewModel item)
            WithUndo(string.Format(Strings.Lcd_RemovidoLCD, item.Name), () => UpdateLcd(l => l.Media.RemoveAt(Math.Min(item.Index, l.Media.Count - 1))));
    });

    public ICommand ClearMediaCommand => _clearMediaCommand ??= new RelayCommand(() =>
        WithUndo(Strings.Lcd_ImagensRemovidasLCD, () => UpdateLcd(l => l.Media.Clear())), () => LcdMedia.Count > 0);

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
        WithUndo(files.Count == 1 ? string.Format(Strings.Lcd_AdicionadoLCD, files[0].Name) : string.Format(Strings.Lcd_ArquivosAdicionadosLCD, files.Count), () =>
            UpdateLcd(l =>
            {
                foreach (var (path, name) in files)
                    l.Media.Add(new LcdMediaItem { Path = path, Name = name, DurationSeconds = 10 });
                l.Mode = LcdMode.Media;
            }));
        EditMedia(LcdMedia.Count - 1); // já abre o enquadramento do que acabou de entrar
    }

    public void SetLcdBackground(string path) => UpdateLcd(l =>
    {
        l.BackgroundPath = path;
        l.BackgroundCrop = new LcdCrop();
    });

    // ------------------------------------------------------------------ Propriedades

    public bool IsLcdMetricsMode => _lcdMode == LcdMode.Metrics;
    public bool IsLcdMediaMode => _lcdMode == LcdMode.Media;
    public int LcdRotation => _lcdRotation;
    public bool LcdFitCover => _lcdFit == LcdFit.Cover;
    public bool LcdFitContain => _lcdFit == LcdFit.Contain;
    public bool HasLcdMedia => LcdMedia.Count > 0;
    public bool HasLcdBackground => !string.IsNullOrEmpty(_lcdBackgroundPath);
    public string LcdBackgroundName => _lcdBackgroundPath is { } p ? Path.GetFileName(p) : Strings.Det_NoneStopped;
    public int LcdSecondaryCount => LcdSecondaryChoices.Count(c => c.IsSelected);
    public string LcdSecondarySummary => string.Format(Strings.Lcd_CliqueAdicionarOuTirar, LcdSecondaryCount, LcdConfig.MaxSecondary);

    public string LcdNowShowing => Snapshot.LcdConnected
        ? string.Format(Strings.Lcd_Showing, Snapshot.LcdStatus is { } shown ? EngineTextUi.Localize(Snapshot.LcdStatusText, shown) : IsLcdMediaMode ? Strings.Lcd_ShowingImages : Strings.Lcd_ShowingMetrics)
        : Strings.Lcd_DisplayNaoConectadoPrevia;

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
                foreach (var item in LcdMedia) item.Retire();
                LcdMedia.Clear();
                for (int i = 0; i < l.Media.Count; i++) LcdMedia.Add(new LcdMediaItemViewModel(this, l.Media[i], i));
            }
        }

        OnPropertiesChanged(nameof(IsLcdMetricsMode), nameof(IsLcdMediaMode), nameof(LcdRotation), nameof(LcdFitCover), nameof(LcdFitContain),
            nameof(HasLcdMedia), nameof(HasLcdBackground), nameof(LcdBackgroundName), nameof(LcdSecondaryCount), nameof(LcdSecondarySummary),
            nameof(LcdNowShowing), nameof(LcdShowTelemetry));
        SyncEditorAfterLoad();
    }
}
