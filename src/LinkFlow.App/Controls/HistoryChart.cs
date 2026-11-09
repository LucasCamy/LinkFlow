using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkFlow.App.Controls;

/// <summary>Gráfico de histórico com grade, rótulos e leitura do valor sob o mouse.</summary>
public sealed class HistoryChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(HistoryChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(HistoryChart), new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MinRangeProperty = DependencyProperty.Register(
        nameof(MinRange), typeof(double), typeof(HistoryChart), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(HistoryChart), new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double MinRange { get => (double)GetValue(MinRangeProperty); set => SetValue(MinRangeProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(100, 116, 139)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1));
    private static readonly Brush TipBg = Freeze(new SolidColorBrush(Color.FromRgb(26, 36, 56)));
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private double? _hoverX;
    private const double LeftPad = 52, BottomPad = 20, TopPad = 8;

    public HistoryChart() => ClipToBounds = true;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverX = null;
        InvalidateVisual();
    }

    private FormattedText Text(string s, double size, Brush brush) =>
        new(s, Pt, FlowDirection.LeftToRight, Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private string Fmt(double v) => (Math.Abs(v) >= 100 ? v.ToString("N0", Pt) : v.ToString("0.#", Pt)) + (Unit == "°C" ? "°" : $" {Unit}");

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // recebe eventos do mouse
        var values = Values;
        double plotW = w - LeftPad, plotH = h - BottomPad - TopPad;
        if (plotW <= 10 || plotH <= 10) return;

        if (values == null || values.Length < 2)
        {
            var t = Text("Coletando dados...", 12, LabelBrush);
            dc.DrawText(t, new Point(LeftPad + (plotW - t.Width) / 2, TopPad + (plotH - t.Height) / 2));
            return;
        }

        double min = values.Min(), max = values.Max();
        if (max - min < MinRange)
        {
            double mid = (max + min) / 2;
            min = mid - MinRange / 2;
            max = mid + MinRange / 2;
        }
        double pad = (max - min) * 0.08;
        min -= pad;
        max += pad;
        if (min < 0 && values.Min() >= 0) { max -= min; min = 0; }

        double Y(double v) => TopPad + plotH - (v - min) / (max - min) * plotH;
        double step = plotW / (values.Length - 1);

        // Grade e rótulos do eixo Y
        for (int i = 0; i <= 4; i++)
        {
            double v = min + (max - min) * i / 4;
            double y = Y(v);
            dc.DrawLine(GridPen, new Point(LeftPad, y), new Point(w, y));
            var label = Text(Fmt(v), 10.5, LabelBrush);
            dc.DrawText(label, new Point(LeftPad - 8 - label.Width, y - label.Height / 2));
        }

        // Eixo X: tempo
        int seconds = values.Length;
        var start = Text(seconds >= 90 ? $"-{seconds / 60} min" : $"-{seconds} s", 10.5, LabelBrush);
        dc.DrawText(start, new Point(LeftPad, h - BottomPad + 4));
        var nowText = Text("agora", 10.5, LabelBrush);
        dc.DrawText(nowText, new Point(w - nowText.Width, h - BottomPad + 4));

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var ctx = line.Open())
        using (var fctx = fill.Open())
        {
            for (int i = 0; i < values.Length; i++)
            {
                var p = new Point(LeftPad + i * step, Y(values[i]));
                if (i == 0)
                {
                    ctx.BeginFigure(p, false, false);
                    fctx.BeginFigure(new Point(LeftPad, TopPad + plotH), true, true);
                    fctx.LineTo(p, false, false);
                }
                else
                {
                    ctx.LineTo(p, true, true);
                    fctx.LineTo(p, false, false);
                }
            }
            fctx.LineTo(new Point(LeftPad + plotW, TopPad + plotH), false, false);
        }
        line.Freeze();
        fill.Freeze();

        var c = (Stroke as SolidColorBrush)?.Color ?? Colors.Cyan;
        var fillBrush = new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fill);
        var pen = new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // Leitura sob o mouse
        if (_hoverX is { } hx && hx >= LeftPad)
        {
            int idx = (int)Math.Round((hx - LeftPad) / step);
            idx = Math.Clamp(idx, 0, values.Length - 1);
            var p = new Point(LeftPad + idx * step, Y(values[idx]));
            var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1) { DashStyle = DashStyles.Dash };
            crossPen.Freeze();
            dc.DrawLine(crossPen, new Point(p.X, TopPad), new Point(p.X, TopPad + plotH));
            dc.DrawEllipse(Stroke, new Pen(Brushes.White, 1.5), p, 4.5, 4.5);

            int ago = values.Length - 1 - idx;
            string when = ago == 0 ? "agora" : ago < 90 ? $"há {ago} s" : $"há {ago / 60} min {ago % 60} s";
            var tip = Text($"{Fmt(values[idx])}  •  {when}", 11.5, Brushes.White);
            double tx = Math.Clamp(p.X - tip.Width / 2 - 8, LeftPad, w - tip.Width - 16);
            double ty = Math.Max(TopPad, p.Y - tip.Height - 18);
            dc.DrawRoundedRectangle(TipBg, GridPen, new Rect(tx, ty, tip.Width + 16, tip.Height + 8), 6, 6);
            dc.DrawText(tip, new Point(tx + 8, ty + 4));
        }
    }
}
