using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LinkFlow.Core;

namespace LinkFlow.App.ViewModels;

/// <summary>
/// Editor de enquadramento do LCD: arrastar na prévia move a imagem, a roda do mouse/slider dá zoom.
/// Vale para o item da apresentação selecionado (modo imagem) ou para a foto de fundo (modo telemetria).
/// </summary>
public partial class MainViewModel
{
    private int _editingMedia = -1;
    private volatile int _previewPin = -1;
    private readonly Dictionary<string, (int W, int H)> _imageSizes = new(StringComparer.OrdinalIgnoreCase);
    private ICommand? _editMediaCommand, _resetCropCommand;

    /// <summary>Item da apresentação mostrado fixo na prévia enquanto é enquadrado (-1 = nenhum).</summary>
    public int LcdPreviewPin => _previewPin;

    public ICommand EditMediaCommand => _editMediaCommand ??= new RelayCommand(p =>
    {
        if (p is LcdMediaItemViewModel item) EditMedia(item.Index);
    });

    public ICommand ResetCropCommand => _resetCropCommand ??= new RelayCommand(() => ChangeCrop(c => { c.Zoom = 1; c.OffsetX = 0; c.OffsetY = 0; }));

    public void EditMedia(int index)
    {
        _editingMedia = index >= 0 && index < LcdMedia.Count ? index : -1;
        _previewPin = IsLcdMediaMode ? _editingMedia : -1;
        foreach (var m in LcdMedia) m.IsCurrent = m.Index == _editingMedia;
        OnCropChanged();
        LcdConfigChanged?.Invoke();
    }

    /// <summary>O que está sendo enquadrado: item selecionado (modo imagem) ou a foto de fundo (telemetria).</summary>
    private (string Path, LcdFit Fit, LcdCrop Crop)? CropTarget
    {
        get
        {
            var l = _engine.Config.Lcd;
            if (l.Mode == LcdMode.Media && _editingMedia >= 0 && _editingMedia < l.Media.Count)
                return (l.Media[_editingMedia].Path, l.Fit, l.Media[_editingMedia].Crop);
            if (l.Mode == LcdMode.Metrics && !string.IsNullOrEmpty(l.BackgroundPath))
                return (l.BackgroundPath!, LcdFit.Cover, l.BackgroundCrop);
            return null;
        }
    }

    public bool HasCropTarget => CropTarget != null;

    public string CropTargetName => CropTarget is { } t ? Path.GetFileName(t.Path) is var n && _engine.Config.Lcd.Mode == LcdMode.Media
        ? LcdMedia.ElementAtOrDefault(_editingMedia)?.Name ?? n : $"foto de fundo ({n})" : string.Empty;

    public double CropZoom
    {
        get => CropTarget?.Crop.Zoom ?? 1;
        set => ChangeCrop(c => c.Zoom = Math.Clamp(Math.Round(value, 2), 1, LcdCrop.MaxZoom));
    }

    public string CropZoomText => $"{CropZoom * 100:0}%";

    /// <summary>Zoom pela roda do mouse (passos de 10%).</summary>
    public void ZoomCropBy(int wheelDelta) => CropZoom = CropZoom * (wheelDelta > 0 ? 1.1 : 1 / 1.1);

    /// <summary>Arrastar na prévia: converte pixels da prévia em deslocamento normalizado (sempre dentro da borda).</summary>
    public void PanCropBy(double dxPreview, double dyPreview, double previewSize)
    {
        if (CropTarget is not { } t || previewSize <= 0) return;
        var (w, h) = ImageSize(t.Path);
        if (w <= 0 || h <= 0) return;
        var (rx, ry) = LcdComposer.PanRange(w, h, t.Fit, t.Crop.Zoom);
        double toDevice = LcdComposer.Size / previewSize;
        ChangeCrop(c =>
        {
            if (rx > 0.5) c.OffsetX = Math.Clamp(c.OffsetX + dxPreview * toDevice / rx, -1, 1);
            if (ry > 0.5) c.OffsetY = Math.Clamp(c.OffsetY + dyPreview * toDevice / ry, -1, 1);
        });
    }

    private void ChangeCrop(Action<LcdCrop> change)
    {
        if (CropTarget == null) return;
        int index = _editingMedia;
        _engine.UpdateConfig(c =>
        {
            var l = c.Lcd;
            var crop = l.Mode == LcdMode.Media && index >= 0 && index < l.Media.Count ? l.Media[index].Crop : l.BackgroundCrop;
            change(crop);
            crop.Normalize();
        });
        OnCropChanged();
        LcdConfigChanged?.Invoke();
    }

    private void OnCropChanged() => OnPropertiesChanged(nameof(HasCropTarget), nameof(CropTargetName), nameof(CropZoom), nameof(CropZoomText));

    /// <summary>Lê só o cabeçalho da imagem (barato) e guarda.</summary>
    private (int W, int H) ImageSize(string path)
    {
        if (_imageSizes.TryGetValue(path, out var s)) return s;
        try
        {
            using var fs = File.OpenRead(path);
            var frame = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
            s = (frame.PixelWidth, frame.PixelHeight);
        }
        catch { s = (0, 0); }
        return _imageSizes[path] = s;
    }

    // ------------------------------------------------------------------ Telemetria sobre a foto de fundo

    public bool LcdShowTelemetry
    {
        get => _engine.Config.Lcd.ShowTelemetry;
        set => UpdateLcd(l => l.ShowTelemetry = value);
    }
}

public partial class MainViewModel
{
    /// <summary>Depois de recarregar a configuração: mantém (ou escolhe) o item em edição.</summary>
    private void SyncEditorAfterLoad()
    {
        if (_editingMedia >= LcdMedia.Count) _editingMedia = LcdMedia.Count - 1;
        if (_editingMedia < 0 && LcdMedia.Count > 0) _editingMedia = 0;
        _previewPin = IsLcdMediaMode ? _editingMedia : -1;
        foreach (var m in LcdMedia) m.IsCurrent = m.Index == _editingMedia;
        OnCropChanged();
    }
}
