using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LinkFlow.Core;

namespace LinkFlow.App.Controls;

/// <summary>
/// Editor da curva temperatura × potência. Arraste os pontos; clique duplo no fundo adiciona
/// um ponto; clique direito num ponto remove (mínimo 2). Mostra a temperatura atual e a saída da curva.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    private const double TempMin = 20, TempMax = 100;
    private const double PadLeft = 38, PadRight = 12, PadTop = 12, PadBottom = 26;
    private const double HitRadius = 10;

    private List<CurvePoint> _points = new();
    private int _dragIndex = -1;
    private int _hoverIndex = -1;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<CurvePoint>), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((CurveEditor)d).OnPointsChanged()));

    public static readonly DependencyProperty CurrentTempProperty = DependencyProperty.Register(
        nameof(CurrentTemp), typeof(double?), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentOutputProperty = DependencyProperty.Register(
        nameof(CurrentOutput), typeof(double?), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<CurvePoint>? Points
    {
        get => (IReadOnlyList<CurvePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double? CurrentTemp
    {
        get => (double?)GetValue(CurrentTempProperty);
        set => SetValue(CurrentTempProperty, value);
    }

    public double? CurrentOutput
    {
        get => (double?)GetValue(CurrentOutputProperty);
        set => SetValue(CurrentOutputProperty, value);
    }

    /// <summary>Disparado durante o arraste (com limitação) e ao soltar.</summary>
    public event Action<List<CurvePoint>, bool>? CurveEdited;

    public CurveEditor()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        MinHeight = 180;
        ToolTip = "Arraste os pontos • clique duplo adiciona • clique direito remove";
    }

    private void OnPointsChanged()
    {
        if (_dragIndex >= 0) return; // não sobrescrever durante o arraste
        _points = Points?.Select(p => new CurvePoint(p.Temp, p.Percent)).OrderBy(p => p.Temp).ToList() ?? new();
    }

    private Rect Plot => new(PadLeft, PadTop, Math.Max(10, ActualWidth - PadLeft - PadRight), Math.Max(10, ActualHeight - PadTop - PadBottom));

    private Point ToScreen(double temp, double pct)
    {
        var r = Plot;
        return new Point(r.Left + (temp - TempMin) / (TempMax - TempMin) * r.Width, r.Bottom - pct / 100.0 * r.Height);
    }

    private (double temp, double pct) FromScreen(Point p)
    {
        var r = Plot;
        double t = TempMin + (p.X - r.Left) / r.Width * (TempMax - TempMin);
        double pct = (r.Bottom - p.Y) / r.Height * 100.0;
        return (Math.Clamp(t, TempMin, TempMax), Math.Clamp(pct, FanLimits.MinPercent, 100));
    }

    private int HitTestPoint(Point p)
    {
        for (int i = 0; i < _points.Count; i++)
        {
            var s = ToScreen(_points[i].Temp, _points[i].Percent);
            if ((s - p).Length <= HitRadius) return i;
        }
        return -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = Plot;
        var bg = (Brush)FindResource("BgBrush");
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 148, 163, 184)), 1);
        var axisText = (Brush)FindResource("TextSubtleBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);

        for (int pct = 0; pct <= 100; pct += 25)
        {
            var a = ToScreen(TempMin, pct);
            dc.DrawLine(grid, a, new Point(r.Right, a.Y));
            var ft = new FormattedText($"{pct}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, axisText, dpi);
            dc.DrawText(ft, new Point(4, a.Y - ft.Height / 2));
        }
        for (int t = 20; t <= 100; t += 10)
        {
            var a = ToScreen(t, 0);
            dc.DrawLine(grid, new Point(a.X, r.Top), a);
            var ft = new FormattedText($"{t}°", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, axisText, dpi);
            dc.DrawText(ft, new Point(a.X - ft.Width / 2, r.Bottom + 6));
        }

        if (_points.Count >= 2)
        {
            var geo = new StreamGeometry();
            var fill = new StreamGeometry();
            using (var ctx = geo.Open())
            using (var fctx = fill.Open())
            {
                var first = ToScreen(TempMin, _points[0].Percent);
                ctx.BeginFigure(first, false, false);
                fctx.BeginFigure(new Point(first.X, r.Bottom), true, true);
                fctx.LineTo(first, false, false);
                foreach (var p in _points)
                {
                    var s = ToScreen(p.Temp, p.Percent);
                    ctx.LineTo(s, true, true);
                    fctx.LineTo(s, false, false);
                }
                var last = ToScreen(TempMax, _points[^1].Percent);
                ctx.LineTo(last, true, true);
                fctx.LineTo(last, false, false);
                fctx.LineTo(new Point(last.X, r.Bottom), false, false);
            }
            var fillBrush = accent.Clone();
            fillBrush.Opacity = 0.1;
            dc.DrawGeometry(fillBrush, null, fill);
            dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, geo);
        }

        // Temperatura atual
        if (CurrentTemp is { } ct && ct >= TempMin && ct <= TempMax)
        {
            var warn = (Brush)FindResource("WarningBrush");
            var x = ToScreen(ct, 0).X;
            dc.DrawLine(new Pen(warn, 1) { DashStyle = DashStyles.Dash }, new Point(x, r.Top), new Point(x, r.Bottom));
            if (CurrentOutput is { } co)
            {
                var pt = ToScreen(ct, co);
                dc.DrawEllipse(warn, null, pt, 5, 5);
                var label = new FormattedText($"{ct:0.0}°C → {co:0}%", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Semibold"), 11, warn, dpi);
                double lx = Math.Min(pt.X + 8, r.Right - label.Width);
                dc.DrawText(label, new Point(lx, Math.Max(r.Top, pt.Y - 20)));
            }
        }

        var text = (Brush)FindResource("TextBrush");
        for (int i = 0; i < _points.Count; i++)
        {
            var s = ToScreen(_points[i].Temp, _points[i].Percent);
            bool active = i == _dragIndex || i == _hoverIndex;
            dc.DrawEllipse(bg, new Pen(accent, 2.5), s, active ? 8 : 6, active ? 8 : 6);
            if (active)
            {
                var ft = new FormattedText($"{_points[i].Temp:0}°C • {_points[i].Percent}%", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, text, dpi);
                double lx = Math.Clamp(s.X - ft.Width / 2, r.Left, r.Right - ft.Width);
                dc.DrawText(ft, new Point(lx, Math.Max(0, s.Y - 26)));
            }
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);

        if (e.ClickCount == 2 && HitTestPoint(p) < 0)
        {
            var (t, pct) = FromScreen(p);
            _points.Add(new CurvePoint((float)Math.Round(t), (byte)Math.Round(pct)));
            _points = _points.OrderBy(x => x.Temp).ToList();
            InvalidateVisual();
            CurveEdited?.Invoke(Copy(), true);
            return;
        }

        _dragIndex = HitTestPoint(p);
        if (_dragIndex >= 0)
        {
            CaptureMouse();
            e.Handled = true;
        }
    }

    private long _lastLiveEdit;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);

        if (_dragIndex >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            var (t, pct) = FromScreen(p);
            // Mantém a ordem: o ponto não ultrapassa os vizinhos
            double lo = _dragIndex > 0 ? _points[_dragIndex - 1].Temp + 1 : TempMin;
            double hi = _dragIndex < _points.Count - 1 ? _points[_dragIndex + 1].Temp - 1 : TempMax;
            _points[_dragIndex].Temp = (float)Math.Round(Math.Clamp(t, lo, hi));
            _points[_dragIndex].Percent = (byte)Math.Round(pct);
            InvalidateVisual();

            if (Environment.TickCount64 - _lastLiveEdit > 120)
            {
                _lastLiveEdit = Environment.TickCount64;
                CurveEdited?.Invoke(Copy(), false);
            }
            return;
        }

        int hover = HitTestPoint(p);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? Cursors.SizeAll : Cursors.Cross;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        CurveEdited?.Invoke(Copy(), true);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        int i = HitTestPoint(e.GetPosition(this));
        if (i >= 0 && _points.Count > 2)
        {
            _points.RemoveAt(i);
            _hoverIndex = -1;
            InvalidateVisual();
            CurveEdited?.Invoke(Copy(), true);
            e.Handled = true;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    private List<CurvePoint> Copy() => _points.Select(p => new CurvePoint(p.Temp, p.Percent)).ToList();
}
