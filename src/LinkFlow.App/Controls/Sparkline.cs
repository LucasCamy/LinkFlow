using System.Windows;
using System.Windows.Media;

namespace LinkFlow.App.Controls;

/// <summary>Mini gráfico de linha (histórico dos últimos minutos). Desenho leve via OnRender.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinRangeProperty = DependencyProperty.Register(
        nameof(MinRange), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Faixa mínima do eixo Y (evita amplificar ruído de 0,1°C).</summary>
    public double MinRange
    {
        get => (double)GetValue(MinRangeProperty);
        set => SetValue(MinRangeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        double w = ActualWidth, h = ActualHeight;
        if (values == null || values.Length < 2 || w <= 0 || h <= 0) return;

        double min = values.Min(), max = values.Max();
        if (max - min < MinRange)
        {
            double mid = (max + min) / 2;
            min = mid - MinRange / 2;
            max = mid + MinRange / 2;
        }

        double step = w / (values.Length - 1);
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var ctx = line.Open())
        using (var fctx = fill.Open())
        {
            for (int i = 0; i < values.Length; i++)
            {
                var p = new Point(i * step, h - 2 - (values[i] - min) / (max - min) * (h - 4));
                if (i == 0)
                {
                    ctx.BeginFigure(p, false, false);
                    fctx.BeginFigure(new Point(0, h), true, true);
                    fctx.LineTo(p, false, false);
                }
                else
                {
                    ctx.LineTo(p, true, true);
                    fctx.LineTo(p, false, false);
                }
            }
            fctx.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        fill.Freeze();

        var fillBrush = Stroke.Clone();
        fillBrush.Opacity = 0.12;
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fill);

        var pen = new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}
