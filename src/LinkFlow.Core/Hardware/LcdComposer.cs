using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace LinkFlow.Core;

/// <summary>Valor pronto para exibição no LCD.</summary>
public readonly record struct LcdMetricValue(string Value, string Label, float? Ratio);

/// <summary>
/// Monta os quadros do LCD 480x480: painel de métricas personalizável (com imagem de fundo opcional)
/// ou imagens/GIFs em apresentação de slides. O mesmo código gera os quadros enviados ao display
/// e a prévia exibida na interface. Não é thread-safe: cada thread deve ter a sua instância.
/// </summary>
public sealed class LcdComposer : IDisposable
{
    public const int Size = 480;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private readonly Bitmap _bmp = new(Size, Size, PixelFormat.Format32bppArgb);
    private readonly Graphics _g;
    private readonly ImageCodecInfo _jpegEncoder;
    private readonly EncoderParameters _encParams;
    private readonly MemoryStream _ms = new(64 * 1024);
    private readonly Dictionary<string, MediaEntry?> _media = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> _jpegCache = new();
    private long _jpegCacheBytes;

    private readonly Font _titleFont = new("Segoe UI", 13, FontStyle.Bold);
    private readonly Font _mainFont = new("Segoe UI", 68, FontStyle.Bold);
    private readonly Font _mainLabelFont = new("Segoe UI", 14, FontStyle.Bold);
    private readonly Font _smallLabelFont = new("Segoe UI", 11, FontStyle.Bold);
    private readonly Font _smallValueFont = new("Segoe UI", 17, FontStyle.Bold);
    private readonly Font _statusFont = new("Segoe UI", 10, FontStyle.Bold);
    private readonly Font _overlayFont = new("Segoe UI", 20, FontStyle.Bold);
    private readonly Font _messageFont = new("Segoe UI", 15, FontStyle.Bold);

    private readonly SolidBrush _bgBrush = new(Color.FromArgb(11, 15, 25));
    private readonly SolidBrush _titleBrush = new(Color.FromArgb(148, 163, 184));
    private readonly SolidBrush _white = new(Color.White);
    private readonly SolidBrush _muted = new(Color.FromArgb(120, 136, 160));
    private readonly SolidBrush _ok = new(Color.FromArgb(52, 211, 153));
    private readonly SolidBrush _warn = new(Color.FromArgb(244, 63, 94));
    private readonly Pen _trackPen = new(Color.FromArgb(30, 41, 59), 10);
    private readonly StringFormat _center = new() { Alignment = StringAlignment.Center };
    private readonly StringFormat _centerBoth = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };

    // Estado da apresentação de slides / GIF
    private int _itemIndex;
    private long _itemStarted;
    private int _frameIndex;
    private long _frameStarted;
    private string _playlistKey = string.Empty;

    public LcdComposer()
    {
        _g = Graphics.FromImage(_bmp);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        _g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        _jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encParams = new EncoderParameters(1);
        _encParams.Param[0] = new EncoderParameter(Encoder.Quality, 86L);
    }

    /// <summary>Quadro atual (válido até a próxima chamada de <see cref="Compose"/>).</summary>
    public Bitmap Canvas => _bmp;

    /// <summary>Descrição do que está sendo exibido (ex.: "GIF 2/3").</summary>
    public string Status { get; private set; } = "Métricas";

    /// <summary>
    /// Desenha o quadro atual. Retorna quanto tempo até o próximo quadro ser necessário
    /// (1 s para métricas e imagens; o atraso do quadro para GIFs).
    /// </summary>
    /// <param name="applyRotation">False na prévia: mostra o quadro como ele aparece fisicamente, já compensado.</param>
    /// <param name="pinItem">Editor: mostra sempre este item (sem passar para o próximo slide).</param>
    public TimeSpan Compose(LcdConfig cfg, EngineSnapshot snap, RgbConfig rgb, bool applyRotation = true, int? pinItem = null)
    {
        var theme = rgb.SyncLcd ? Color.FromArgb(rgb.R, rgb.G, rgb.B) : Color.FromArgb(0, 240, 255);
        if (theme.R + theme.G + theme.B < 60) theme = Color.FromArgb(0, 240, 255); // cor apagada: mantém legível
        var critical = snap.Alerts.FirstOrDefault(a => a.Severity == AlertSeverity.Critical);
        bool warning = snap.FailSafeActive || critical != null;

        var g = _g;
        g.ResetTransform();
        TimeSpan next = TimeSpan.FromSeconds(1);
        IsAnimating = false;
        _pinItem = pinItem is { } pi && pi >= 0 && pi < cfg.Media.Count ? pi : null;

        if (cfg.Mode == LcdMode.Media && cfg.Media.Count > 0)
        {
            next = DrawMedia(cfg, snap, theme);
            if (warning) DrawWarningBanner(snap, critical);
        }
        else
        {
            next = DrawMetrics(cfg, snap, theme, warning, critical);
            Status = cfg.ShowTelemetry ? "Métricas" : "Imagem de fundo";
        }

        if (applyRotation && cfg.Rotation != 0)
        {
            _bmp.RotateFlip(cfg.Rotation switch
            {
                90 => RotateFlipType.Rotate90FlipNone,
                180 => RotateFlipType.Rotate180FlipNone,
                _ => RotateFlipType.Rotate270FlipNone
            });
        }
        return next;
    }

    public byte[] EncodeJpeg()
    {
        _ms.SetLength(0);
        _bmp.Save(_ms, _jpegEncoder, _encParams);
        return _ms.ToArray();
    }

    /// <summary>Compose + JPEG, reaproveitando quadros de GIF já codificados quando não há sobreposição.</summary>
    public (byte[] Jpeg, TimeSpan Next) ComposeJpeg(LcdConfig cfg, EngineSnapshot snap, RgbConfig rgb)
    {
        bool warning = snap.FailSafeActive || snap.Alerts.Any(a => a.Severity == AlertSeverity.Critical);
        bool cacheable = cfg.Mode == LcdMode.Media && cfg.Media.Count > 0 && !cfg.MediaOverlay && !warning;
        if (cacheable)
        {
            var next = Compose(cfg, snap, rgb);
            var item = CurrentItem(cfg);
            var key = $"{item.Path}|{cfg.Fit}|{cfg.Rotation}|{_frameIndex}|{item.Crop.Zoom:0.###}|{item.Crop.OffsetX:0.###}|{item.Crop.OffsetY:0.###}";
            if (_jpegCache.TryGetValue(key, out var cached)) return (cached, next);
            var jpeg = EncodeJpeg();
            if (_jpegCacheBytes + jpeg.Length > 24 * 1024 * 1024)
            {
                _jpegCache.Clear();
                _jpegCacheBytes = 0;
            }
            _jpegCache[key] = jpeg;
            _jpegCacheBytes += jpeg.Length;
            return (jpeg, next);
        }

        var n = Compose(cfg, snap, rgb);
        return (EncodeJpeg(), n);
    }

    // ------------------------------------------------------------------ Métricas

    private TimeSpan DrawMetrics(LcdConfig cfg, EngineSnapshot snap, Color theme, bool warning, EngineAlert? critical)
    {
        var g = _g;
        g.FillRectangle(_bgBrush, 0, 0, Size, Size);
        TimeSpan next = TimeSpan.FromSeconds(1);

        if (!string.IsNullOrWhiteSpace(cfg.BackgroundPath) && GetMedia(cfg.BackgroundPath!) is { } bg)
        {
            // Fundo animado (GIF): avança os quadros como no modo imagem
            if (bg.FrameCount > 1)
            {
                if (_bgPath != cfg.BackgroundPath) { _bgPath = cfg.BackgroundPath; _bgFrame = 0; _bgStarted = Stopwatch.GetTimestamp(); }
                var remaining = AdvanceFrames(bg, ref _bgFrame, ref _bgStarted);
                bg.Image.SelectActiveFrame(bg.Dimension, _bgFrame);
                next = TimeSpan.FromMilliseconds(Math.Max(MinFrameMs, remaining));
                IsAnimating = true;
            }
            DrawImageFit(bg.Image, LcdFit.Cover, cfg.BackgroundCrop);
            if (cfg.ShowTelemetry)
            {
                using var dim = new SolidBrush(Color.FromArgb((int)(cfg.BackgroundDim * 2.55), 6, 10, 18));
                g.FillRectangle(dim, 0, 0, Size, Size);
            }
            else
            {
                if (warning) DrawWarningBanner(snap, critical);
                return next; // só a imagem/GIF, sem números
            }
        }

        var main = Metric(cfg.MainMetric, snap);
        var ringColor = warning ? Color.FromArgb(244, 63, 94) : theme;
        if (cfg.ShowRing)
        {
            g.DrawArc(_trackPen, 35, 35, 410, 410, 135, 270);
            float sweep = main.Ratio is { } r ? Math.Clamp(r * 270f, 10f, 270f) : 270f;
            using (var progressPen = new Pen(main.Ratio == null ? Color.FromArgb(120, ringColor) : ringColor, 12) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(progressPen, 35, 35, 410, 410, 135, sweep);
            using var innerPen = new Pen(Color.FromArgb(40, theme), 2);
            g.DrawEllipse(innerPen, 65, 65, 350, 350);
        }

        if (!string.IsNullOrWhiteSpace(cfg.Title))
            g.DrawString(cfg.Title.ToUpperInvariant(), _titleFont, _titleBrush, new PointF(240, 95), _center);

        if (cfg.MainMetric != LcdMetric.None)
        {
            g.DrawString(main.Value, FitFont(main.Value, _mainFont, 330), _white, new PointF(240, 128), _center);
            using var themeBrush = new SolidBrush(ringColor);
            g.DrawString(main.Label, _mainLabelFont, themeBrush, new PointF(240, 238), _center);
        }

        var secondary = cfg.SecondaryMetrics.Take(LcdConfig.MaxSecondary).Select(m => Metric(m, snap)).ToList();
        float statusY = 375;
        switch (secondary.Count)
        {
            case 1:
                DrawSmall(secondary[0], 240, 288);
                break;
            case 2:
                DrawSmall(secondary[0], 160, 288);
                DrawSmall(secondary[1], 320, 288);
                break;
            case 3:
                DrawSmall(secondary[0], 135, 288);
                DrawSmall(secondary[1], 240, 288);
                DrawSmall(secondary[2], 345, 288);
                break;
            case 4:
                DrawSmall(secondary[0], 170, 276);
                DrawSmall(secondary[1], 310, 276);
                DrawSmall(secondary[2], 170, 330);
                DrawSmall(secondary[3], 310, 330);
                statusY = 392;
                break;
        }

        string status = snap.FailSafeActive ? "MODO SEGURANÇA" : critical != null ? "ALERTA" : "OPERACIONAL";
        g.DrawString($"● {status}", _statusFont, warning ? _warn : _ok, new PointF(240, statusY), _center);
        return next;
    }

    private void DrawSmall(LcdMetricValue m, float x, float y)
    {
        _g.DrawString(m.Label, _smallLabelFont, _muted, new PointF(x, y), _center);
        _g.DrawString(m.Value, _smallValueFont, _white, new PointF(x, y + 18), _center);
    }

    private Font FitFont(string text, Font font, float maxWidth)
    {
        var size = _g.MeasureString(text, font);
        if (size.Width <= maxWidth) return font;
        // Reduz proporcionalmente (ex.: "2.217 RPM")
        float scale = maxWidth / size.Width;
        return _fitCache.TryGetValue(scale, out var f) ? f : CacheFont(scale, font);
    }

    private readonly Dictionary<float, Font> _fitCache = new();

    private Font CacheFont(float scale, Font font)
    {
        if (_fitCache.Count > 16)
        {
            foreach (var f in _fitCache.Values) f.Dispose();
            _fitCache.Clear();
        }
        var created = new Font(font.FontFamily, Math.Max(18, font.Size * scale), font.Style);
        _fitCache[scale] = created;
        return created;
    }

    public static LcdMetricValue Metric(LcdMetric metric, EngineSnapshot s)
    {
        static string T(float? v, string fmt = "0") => v is { } x ? x.ToString(fmt, Pt) + "°" : "--";
        static string P(float? v) => v is { } x ? $"{x:0}%" : "--";
        static string W(float? v) => v is { } x ? $"{x:0} W" : "--";
        static float? R(float? v, float min, float max) => v is { } x ? Math.Clamp((x - min) / (max - min), 0f, 1f) : null;

        float? cpu = s.CpuSensorAvailable ? s.CpuTemp : null;
        return metric switch
        {
            LcdMetric.CpuTemp => new(T(cpu), "CPU", R(cpu, 0, 100)),
            LcdMetric.CpuLoad => new(P(s.CpuLoad), "USO CPU", R(s.CpuLoad, 0, 100)),
            LcdMetric.CpuPower => new(W(s.CpuPower), "CPU", R(s.CpuPower, 0, 200)),
            LcdMetric.CpuClock => new(s.CpuClockMhz is { } c ? (c / 1000).ToString("0.0", Pt) + " GHz" : "--", "CLOCK CPU", R(s.CpuClockMhz, 0, 6000)),
            LcdMetric.GpuTemp => new(T(s.GpuTemp), "GPU", R(s.GpuTemp, 0, 100)),
            LcdMetric.GpuHotspot => new(T(s.GpuHotspot), "HOTSPOT", R(s.GpuHotspot, 0, 110)),
            LcdMetric.GpuLoad => new(P(s.GpuLoad), "USO GPU", R(s.GpuLoad, 0, 100)),
            LcdMetric.GpuPower => new(W(s.GpuPower), "GPU", R(s.GpuPower, 0, 450)),
            LcdMetric.CoolantTemp => new(T(s.CoolantTemp, "0.0"), "LÍQUIDO", R(s.CoolantTemp, 20, 50)),
            LcdMetric.PumpRpm => new(s.PumpRpm is { } p ? p.ToString("N0", Pt) : "--", "BOMBA RPM", R(s.PumpRpm, 0, 3200)),
            LcdMetric.FanRpm => new(s.AverageFanRpm is { } f ? f.ToString("N0", Pt) : "--", "FANS RPM", R(s.AverageFanRpm, 0, 2400)),
            LcdMetric.FanPower => new($"{s.EffectiveGlobalPercent}%", "FANS", s.EffectiveGlobalPercent / 100f),
            LcdMetric.RamUsage => new(P(s.RamUsage), "RAM", R(s.RamUsage, 0, 100)),
            LcdMetric.Clock => new(DateTime.Now.ToString("HH:mm", Pt), DateTime.Now.ToString("ddd dd/MM", Pt).ToUpperInvariant(), null),
            LcdMetric.NetDown => new(s.Network is { } nd ? SensorCatalog.Rate(nd.DownBytesPerSec) : "--", "DOWNLOAD", s.Network is { } nr ? (float)Math.Clamp(nr.DownBytesPerSec / 12.5e6, 0, 1) : null),
            LcdMetric.NetUp => new(s.Network is { } nu ? SensorCatalog.Rate(nu.UpBytesPerSec) : "--", "UPLOAD", null),
            LcdMetric.DiskUsage => s.Volumes.FirstOrDefault() is { } v ? new($"{v.UsedPercent:0}%", $"DISCO {v.Letter}", (float)(v.UsedPercent / 100)) : new("--", "DISCO", null),
            LcdMetric.CpuCoreMax => s.CpuCoreLoads is { Length: > 0 } cl ? new($"{cl.Max():0}%", "NÚCLEO", cl.Max() / 100f) : new("--", "NÚCLEO", null),
            _ => new(string.Empty, string.Empty, null)
        };
    }

    // ------------------------------------------------------------------ Imagens / GIFs

    private LcdMediaItem CurrentItem(LcdConfig cfg)
    {
        if (_itemIndex >= cfg.Media.Count) _itemIndex = 0;
        return cfg.Media[_itemIndex];
    }

    private TimeSpan DrawMedia(LcdConfig cfg, EngineSnapshot snap, Color theme)
    {
        var g = _g;
        long now = Stopwatch.GetTimestamp();

        // Lista alterada: recomeça do primeiro item
        string key = string.Join("|", cfg.Media.Select(m => m.Path));
        if (key != _playlistKey)
        {
            _playlistKey = key;
            _itemIndex = 0;
            _itemStarted = now;
            _frameIndex = 0;
            _frameStarted = now;
        }

        // Editor aberto: fica no item que está sendo enquadrado
        if (_pinItem is { } pin && pin != _itemIndex)
        {
            _itemIndex = pin;
            _itemStarted = now;
            _frameIndex = 0;
            _frameStarted = now;
        }

        var item = CurrentItem(cfg);
        if (_pinItem == null && cfg.Media.Count > 1 && Stopwatch.GetElapsedTime(_itemStarted, now).TotalSeconds >= item.DurationSeconds)
        {
            _itemIndex = (_itemIndex + 1) % cfg.Media.Count;
            _itemStarted = now;
            _frameIndex = 0;
            _frameStarted = now;
            item = cfg.Media[_itemIndex];
        }

        g.FillRectangle(_bgBrush, 0, 0, Size, Size);
        var media = GetMedia(item.Path);
        TimeSpan next = TimeSpan.FromSeconds(1);
        string position = cfg.Media.Count > 1 ? $" {_itemIndex + 1}/{cfg.Media.Count}" : string.Empty;

        if (media == null)
        {
            g.DrawString("Arquivo não encontrado", _messageFont, _warn, new RectangleF(40, 200, 400, 40), _centerBoth);
            g.DrawString(item.Name, _statusFont, _muted, new RectangleF(40, 240, 400, 30), _centerBoth);
            Status = $"Imagem{position} • arquivo não encontrado";
        }
        else if (media.FrameCount > 1)
        {
            // Avança os quadros do GIF conforme o tempo real (pula quadros se atrasar)
            double remaining = AdvanceFrames(media, ref _frameIndex, ref _frameStarted);
            media.Image.SelectActiveFrame(media.Dimension, _frameIndex);
            DrawImageFit(media.Image, cfg.Fit, item.Crop);
            next = TimeSpan.FromMilliseconds(Math.Max(MinFrameMs, remaining));
            IsAnimating = true;
            Status = $"GIF{position} • {media.FrameCount} quadros";
        }
        else
        {
            DrawImageFit(media.Image, cfg.Fit, item.Crop);
            Status = $"Imagem{position}";
            if (cfg.Media.Count > 1 && _pinItem == null)
            {
                var left = item.DurationSeconds - Stopwatch.GetElapsedTime(_itemStarted, now).TotalSeconds;
                next = TimeSpan.FromSeconds(Math.Clamp(left, 0.1, 1));
            }
        }

        if (cfg.MediaOverlay && cfg.OverlayMetric != LcdMetric.None)
        {
            var m = Metric(cfg.OverlayMetric, snap);
            string text = $"{m.Label}  {m.Value}";
            var size = g.MeasureString(text, _overlayFont);
            var rect = new RectangleF(240 - size.Width / 2 - 16, 380, size.Width + 32, 44);
            using var pill = RoundedRect(rect, 22);
            using var bgBrush = new SolidBrush(Color.FromArgb(190, 8, 12, 22));
            using var border = new Pen(Color.FromArgb(160, theme), 2);
            g.FillPath(bgBrush, pill);
            g.DrawPath(border, pill);
            g.DrawString(text, _overlayFont, _white, rect, _centerBoth);
        }

        // Sempre volta a desenhar quando a mídia muda, mas com no máximo ~16 quadros/s para não saturar o USB
        return next;
    }

    private const double MinFrameMs = 60;

    private void DrawWarningBanner(EngineSnapshot snap, EngineAlert? critical)
    {
        var rect = new RectangleF(70, 52, 340, 50);
        using var path = RoundedRect(rect, 14);
        using var brush = new SolidBrush(Color.FromArgb(225, 159, 18, 57));
        _g.FillPath(brush, path);
        string text = snap.FailSafeActive ? "MODO SEGURANÇA" : critical?.Title.ToUpperInvariant() ?? "ALERTA";
        _g.DrawString(text, _messageFont, _white, rect, _centerBoth);
    }

    /// <summary>Tamanho da imagem na tela (px do LCD) para o encaixe e zoom escolhidos.</summary>
    public static (double W, double H) ScaledSize(double imgW, double imgH, LcdFit fit, double zoom)
    {
        double scale = fit == LcdFit.Cover ? Math.Max(Size / imgW, Size / imgH) : Math.Min(Size / imgW, Size / imgH);
        scale *= Math.Clamp(zoom, 1, LcdCrop.MaxZoom);
        return (imgW * scale, imgH * scale);
    }

    /// <summary>Quanto a imagem pode andar para cada lado (px do LCD): deslocamento ±1 = até a borda.</summary>
    public static (double X, double Y) PanRange(double imgW, double imgH, LcdFit fit, double zoom)
    {
        var (w, h) = ScaledSize(imgW, imgH, fit, zoom);
        return (Math.Abs(w - Size) / 2, Math.Abs(h - Size) / 2);
    }

    private void DrawImageFit(Image img, LcdFit fit, LcdCrop? crop = null)
    {
        double zoom = crop?.Zoom ?? 1;
        var (w, h) = ScaledSize(img.Width, img.Height, fit, zoom);
        var (rx, ry) = PanRange(img.Width, img.Height, fit, zoom);
        double x = (Size - w) / 2 + (crop?.OffsetX ?? 0) * rx;
        double y = (Size - h) / 2 + (crop?.OffsetY ?? 0) * ry;
        _g.DrawImage(img, new RectangleF((float)x, (float)y, (float)w, (float)h));
    }

    /// <summary>Avança os quadros do GIF pelo tempo real. Retorna quantos ms faltam para o próximo.</summary>
    private static double AdvanceFrames(MediaEntry media, ref int index, ref long started)
    {
        long now = Stopwatch.GetTimestamp();
        if (index >= media.FrameCount) index = 0;
        var elapsed = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
        int guard = 0;
        while (elapsed >= media.Delays[index] && guard++ < 1000)
        {
            elapsed -= media.Delays[index];
            index = (index + 1) % media.FrameCount;
            started = now - (long)(elapsed * Stopwatch.Frequency / 1000);
        }
        return media.Delays[index] - elapsed;
    }

    /// <summary>Há um GIF animando (o engine deve mandar quadros na velocidade dele).</summary>
    public bool IsAnimating { get; private set; }

    private int? _pinItem;
    private string? _bgPath;
    private int _bgFrame;
    private long _bgStarted;

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private MediaEntry? GetMedia(string path)
    {
        if (_media.TryGetValue(path, out var cached))
        {
            if (cached != null || !File.Exists(path)) return cached;
            _media.Remove(path); // arquivo apareceu depois: tenta de novo
        }
        var entry = MediaEntry.TryLoad(path);
        if (_media.Count > 32)
        {
            foreach (var e in _media.Values) e?.Dispose();
            _media.Clear();
            _jpegCache.Clear();
            _jpegCacheBytes = 0;
        }
        _media[path] = entry;
        return entry;
    }

    /// <summary>Descarta imagens em cache que não estão mais na configuração.</summary>
    public void Prune(LcdConfig cfg)
    {
        var keep = new HashSet<string>(cfg.Media.Select(m => m.Path), StringComparer.OrdinalIgnoreCase);
        if (cfg.BackgroundPath != null) keep.Add(cfg.BackgroundPath);
        foreach (var path in _media.Keys.Where(p => !keep.Contains(p)).ToList())
        {
            _media[path]?.Dispose();
            _media.Remove(path);
        }
        foreach (var k in _jpegCache.Keys.Where(k => !keep.Contains(k[..k.IndexOf('|')])).ToList())
        {
            _jpegCacheBytes -= _jpegCache[k].Length;
            _jpegCache.Remove(k);
        }
    }

    private sealed class MediaEntry : IDisposable
    {
        private readonly MemoryStream _stream;
        public Image Image { get; }
        public int FrameCount { get; }
        public int[] Delays { get; }
        public FrameDimension Dimension { get; }

        private MediaEntry(MemoryStream stream, Image image, int frames, int[] delays, FrameDimension dimension)
        {
            _stream = stream;
            Image = image;
            FrameCount = frames;
            Delays = delays;
            Dimension = dimension;
        }

        public static MediaEntry? TryLoad(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                // Carrega em memória: o arquivo não fica bloqueado e o GDI+ exige o stream aberto
                var ms = new MemoryStream(File.ReadAllBytes(path));
                var img = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
                int frames = 1;
                int[] delays = { 1000 };
                // GIFs normais usam a dimensão "tempo"; alguns gravadores (sem atraso por quadro) usam "página"
                var dimension = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid) ? FrameDimension.Time
                    : img.FrameDimensionsList.Contains(FrameDimension.Page.Guid) ? FrameDimension.Page : null;
                if (dimension != null && img.GetFrameCount(dimension) > 1)
                {
                    frames = img.GetFrameCount(dimension);
                    delays = new int[frames];
                    byte[]? raw = img.PropertyIdList.Contains(0x5100) ? img.GetPropertyItem(0x5100)?.Value : null;
                    for (int i = 0; i < frames; i++)
                    {
                        int centis = raw != null && raw.Length >= (i + 1) * 4 ? BitConverter.ToInt32(raw, i * 4) : 10;
                        // Navegadores tratam atrasos menores que 20 ms como 100 ms
                        delays[i] = centis <= 1 ? 100 : Math.Max(20, centis * 10);
                    }
                }
                return new MediaEntry(ms, img, frames, delays, dimension ?? FrameDimension.Page);
            }
            catch (Exception ex)
            {
                Log.Warn($"Não foi possível abrir a imagem do LCD {path}: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            Image.Dispose();
            _stream.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var e in _media.Values) e?.Dispose();
        foreach (var f in _fitCache.Values) f.Dispose();
        _g.Dispose();
        _bmp.Dispose();
        _encParams.Dispose();
        _ms.Dispose();
        _titleFont.Dispose(); _mainFont.Dispose(); _mainLabelFont.Dispose(); _smallLabelFont.Dispose();
        _smallValueFont.Dispose(); _statusFont.Dispose(); _overlayFont.Dispose(); _messageFont.Dispose();
        _bgBrush.Dispose(); _titleBrush.Dispose(); _white.Dispose(); _muted.Dispose(); _ok.Dispose(); _warn.Dispose();
        _trackPen.Dispose();
        _center.Dispose();
        _centerBoth.Dispose();
    }
}
