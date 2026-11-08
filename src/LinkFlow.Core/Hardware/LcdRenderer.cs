using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace LinkFlow.Core;

public readonly record struct LcdFrameData(
    float? CpuTemp,
    float? GpuTemp,
    float? CoolantTemp,
    int? PumpRpm,
    byte R, byte G, byte B,
    string StatusText,
    bool Warning);

/// <summary>
/// Renderiza o painel do LCD 480x480. Fontes, pincéis e encoder são criados uma vez e reutilizados
/// (antes eram recriados a cada quadro). Não é thread-safe: usar sempre da mesma thread.
/// </summary>
public sealed class LcdRenderer : IDisposable
{
    private const int Size = 480;

    private readonly Bitmap _bmp = new(Size, Size, PixelFormat.Format32bppArgb);
    private readonly Graphics _g;
    private readonly ImageCodecInfo _jpegEncoder;
    private readonly EncoderParameters _encParams;
    private readonly MemoryStream _ms = new(64 * 1024);

    private readonly Font _headerFont = new("Segoe UI", 13, FontStyle.Bold);
    private readonly Font _tempFont = new("Segoe UI", 68, FontStyle.Bold);
    private readonly Font _labelFont = new("Segoe UI", 14, FontStyle.Bold);
    private readonly Font _badgeFont = new("Segoe UI", 12, FontStyle.Regular);
    private readonly Font _badgeValFont = new("Segoe UI", 16, FontStyle.Bold);
    private readonly Font _statusFont = new("Segoe UI", 10, FontStyle.Bold);

    private readonly SolidBrush _bgBrush = new(Color.FromArgb(11, 15, 25));
    private readonly SolidBrush _headerBrush = new(Color.FromArgb(148, 163, 184));
    private readonly SolidBrush _white = new(Color.White);
    private readonly SolidBrush _muted = new(Color.FromArgb(100, 116, 139));
    private readonly SolidBrush _ok = new(Color.FromArgb(52, 211, 153));
    private readonly SolidBrush _warn = new(Color.FromArgb(244, 63, 94));
    private readonly Pen _trackPen = new(Color.FromArgb(30, 41, 59), 10);
    private readonly StringFormat _center = new() { Alignment = StringAlignment.Center };

    public LcdRenderer()
    {
        _g = Graphics.FromImage(_bmp);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        _jpegEncoder = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encParams = new EncoderParameters(1);
        _encParams.Param[0] = new EncoderParameter(Encoder.Quality, 88L);
    }

    public byte[] Render(in LcdFrameData d)
    {
        var g = _g;
        g.FillRectangle(_bgBrush, 0, 0, Size, Size);

        var theme = Color.FromArgb(d.R, d.G, d.B);
        if (d.R + d.G + d.B < 60) theme = Color.FromArgb(0, 240, 255); // cor apagada: mantém legível

        g.DrawArc(_trackPen, 35, 35, 410, 410, 135, 270);

        float cpu = d.CpuTemp ?? 0;
        float sweep = Math.Clamp(cpu / 100f * 270f, 10f, 270f);
        using (var progressPen = new Pen(d.Warning ? Color.FromArgb(244, 63, 94) : theme, 12) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawArc(progressPen, 35, 35, 410, 410, 135, sweep);
        }
        using (var innerPen = new Pen(Color.FromArgb(40, theme), 2))
        {
            g.DrawEllipse(innerPen, 65, 65, 350, 350);
        }

        g.DrawString("LINKFLOW", _headerFont, _headerBrush, new PointF(240, 95), _center);

        string cpuStr = d.CpuTemp is { } c ? $"{Math.Round(c):0}°" : "--";
        g.DrawString(cpuStr, _tempFont, _white, new PointF(240, 135), _center);

        using (var themeBrush = new SolidBrush(theme))
        {
            g.DrawString("CPU", _labelFont, themeBrush, new PointF(240, 245), _center);
        }

        g.DrawString("GPU", _badgeFont, _muted, new PointF(150, 290), _center);
        g.DrawString(d.GpuTemp is { } gt ? $"{gt:0}°C" : "--", _badgeValFont, _white, new PointF(150, 312), _center);

        if (d.CoolantTemp is { } ct)
        {
            g.DrawString("LÍQUIDO", _badgeFont, _muted, new PointF(330, 290), _center);
            g.DrawString($"{ct:0.0}°C", _badgeValFont, _white, new PointF(330, 312), _center);
        }
        else
        {
            g.DrawString("BOMBA", _badgeFont, _muted, new PointF(330, 290), _center);
            g.DrawString(d.PumpRpm is { } rpm ? $"{rpm} RPM" : "--", _badgeValFont, _white, new PointF(330, 312), _center);
        }

        g.DrawString($"● {d.StatusText}", _statusFont, d.Warning ? _warn : _ok, new PointF(240, 375), _center);

        _ms.SetLength(0);
        _bmp.Save(_ms, _jpegEncoder, _encParams);
        return _ms.ToArray();
    }

    public void Dispose()
    {
        _g.Dispose();
        _bmp.Dispose();
        _encParams.Dispose();
        _ms.Dispose();
        _headerFont.Dispose(); _tempFont.Dispose(); _labelFont.Dispose();
        _badgeFont.Dispose(); _badgeValFont.Dispose(); _statusFont.Dispose();
        _bgBrush.Dispose(); _headerBrush.Dispose(); _white.Dispose(); _muted.Dispose(); _ok.Dispose(); _warn.Dispose();
        _trackPen.Dispose();
        _center.Dispose();
    }
}
