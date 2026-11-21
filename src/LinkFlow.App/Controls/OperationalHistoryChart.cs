using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.Controls;

/// <summary>Plots actual timestamps and breaks the line at missing or invalid buckets.</summary>
public sealed class OperationalHistoryChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(HistorySeries),
        typeof(OperationalHistoryChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public HistorySeries? Series { get => (HistorySeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    private double? _hover;
    private static readonly Typeface Face = new("Segoe UI");
    public OperationalHistoryChart() => ClipToBounds = true;

    protected override void OnMouseMove(MouseEventArgs e) { _hover = e.GetPosition(this).X; InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; ToolTip = null; InvalidateVisual(); }
    private FormattedText Text(string text, double size, Brush color) => new(text, UiLanguage.Culture, FlowDirection.LeftToRight,
        Face, size, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    internal static IReadOnlyList<HistoryPoint[]> ValidSegments(IReadOnlyList<HistoryPoint> points)
    {
        var segments = new List<HistoryPoint[]>();
        var current = new List<HistoryPoint>();
        foreach (var point in points)
        {
            if (point.Value is { } value && double.IsFinite(value) && point.Validity == ReadingValidity.Fresh)
                current.Add(point);
            else if (current.Count > 0) { segments.Add(current.ToArray()); current.Clear(); }
        }
        if (current.Count > 0) segments.Add(current.ToArray());
        return segments;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width < 100 || height < 60) return;
        var muted = TryFindResource("TextMutedBrush") as Brush ?? Brushes.SlateGray;
        var accent = TryFindResource("AccentBrush") as Brush ?? Brushes.Cyan;
        var grid = new Pen(TryFindResource("BorderBrush") as Brush ?? Brushes.DarkSlateGray, 1);
        var series = Series;
        var valid = series?.Points.Where(p => p.Value is { } value && double.IsFinite(value) && p.Validity == ReadingValidity.Fresh).ToArray() ?? [];
        if (series == null || valid.Length == 0)
        {
            var text = Text(OperationalText.NoReadings, 12, muted);
            dc.DrawText(text, new Point(Math.Max(8, (width - text.Width) / 2), (height - text.Height) / 2));
            return;
        }
        double min = valid.Min(p => p.Min ?? p.Value!.Value), max = valid.Max(p => p.Max ?? p.Value!.Value);
        double span = Math.Max(max - min, Math.Max(1, Math.Abs(max) * .05));
        min -= span * .12; max += span * .12;
        if (min < 0 && valid.All(p => p.Value >= 0)) min = 0;
        if (max <= min) max = min + 1;
        var ticks = Enumerable.Range(0, 4).Select(i => min + (max - min) * i / 3).ToArray();
        string Value(double value) => value.ToString(Math.Abs(value) >= 100 ? "N0" : "0.0", UiLanguage.Culture);
        double left = Math.Max(44, ticks.Max(t => Text(Value(t), 10, muted).Width) + 12), right = width - 8, top = 10, bottom = height - 26;
        double seconds = Math.Max(1, (series.ToUtc - series.FromUtc).TotalSeconds);
        double X(DateTimeOffset time) => left + Math.Clamp((time - series.FromUtc).TotalSeconds / seconds, 0, 1) * (right - left);
        double Y(double value) => bottom - (value - min) / (max - min) * (bottom - top);
        foreach (double value in ticks)
        {
            double y = Y(value);
            var label = Text(Value(value), 10, muted);
            dc.DrawLine(grid, new Point(left, y), new Point(right, y));
            dc.DrawText(label, new Point(left - label.Width - 8, y - label.Height / 2));
        }
        var start = Text(series.FromUtc.ToLocalTime().ToString(seconds > 86400 ? "dd/MM HH:mm" : "HH:mm:ss", UiLanguage.Culture), 10, muted);
        var end = Text(series.ToUtc.ToLocalTime().ToString(seconds > 86400 ? "dd/MM HH:mm" : "HH:mm:ss", UiLanguage.Culture), 10, muted);
        dc.DrawText(start, new Point(left, bottom + 8));
        dc.DrawText(end, new Point(right - end.Width, bottom + 8));
        foreach (var segment in ValidSegments(series.Points))
        {
            if (segment.Length == 1) { dc.DrawEllipse(accent, null, new Point(X(segment[0].TimestampUtc), Y(segment[0].Value!.Value)), 2.5, 2.5); continue; }
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(new Point(X(segment[0].TimestampUtc), Y(segment[0].Value!.Value)), false, false);
                foreach (var point in segment.Skip(1)) path.LineTo(new Point(X(point.TimestampUtc), Y(point.Value!.Value)), true, false);
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 2), geometry);
        }
        if (_hover is { } hover && hover >= left && hover <= right)
        {
            double target = Math.Clamp((hover - left) / (right - left), 0, 1) * seconds;
            var point = series.Points.MinBy(p => Math.Abs((p.TimestampUtc - series.FromUtc).TotalSeconds - target));
            if (point == null) return;
            dc.DrawLine(new Pen(muted, 1) { DashStyle = DashStyles.Dash }, new Point(X(point.TimestampUtc), top), new Point(X(point.TimestampUtc), bottom));
            if (point.Value is { } value) dc.DrawEllipse(accent, null, new Point(X(point.TimestampUtc), Y(value)), 3.5, 3.5);
            ToolTip = OperationalText.Time(point.TimestampUtc) + "\n" + OperationalText.Number(point.Value, series.Unit) + " · " + OperationalText.Validity(point.Validity) +
                "\n" + OperationalText.LastSuccess + ": " + OperationalText.Time(point.AcquiredAtUtc);
        }
    }
}
