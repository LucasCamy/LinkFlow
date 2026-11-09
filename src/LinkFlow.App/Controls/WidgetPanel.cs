using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;

namespace LinkFlow.App.Controls;

/// <summary>Painel do widget: itens em linha, em coluna ou em grade (colunas configuráveis).</summary>
public sealed class WidgetPanel : Panel
{
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout), typeof(WidgetLayout), typeof(WidgetPanel),
        new FrameworkPropertyMetadata(WidgetLayout.Horizontal, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(WidgetPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WidgetPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public WidgetLayout Layout { get => (WidgetLayout)GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public int Columns { get => (int)GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private int Cols => Layout switch
    {
        WidgetLayout.Horizontal => Math.Max(1, InternalChildren.Count),
        WidgetLayout.Vertical => 1,
        _ => Math.Max(1, Math.Min(Columns, Math.Max(1, InternalChildren.Count)))
    };

    private double[] _colW = Array.Empty<double>();
    private double[] _rowH = Array.Empty<double>();

    protected override Size MeasureOverride(Size available)
    {
        int n = InternalChildren.Count, cols = Cols, rows = (n + cols - 1) / cols;
        _colW = new double[cols];
        _rowH = new double[Math.Max(rows, 1)];
        for (int i = 0; i < n; i++)
        {
            var c = InternalChildren[i];
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            int col = i % cols, row = i / cols;
            _colW[col] = Math.Max(_colW[col], c.DesiredSize.Width);
            _rowH[row] = Math.Max(_rowH[row], c.DesiredSize.Height);
        }
        // Grade e vertical: colunas com a mesma largura (alinhamento limpo)
        if (Layout != WidgetLayout.Horizontal && cols > 0)
        {
            double max = _colW.DefaultIfEmpty(0).Max();
            for (int i = 0; i < cols; i++) _colW[i] = max;
        }
        double w = _colW.Sum() + Spacing * Math.Max(0, cols - 1);
        double h = _rowH.Sum() + Spacing * 0.6 * Math.Max(0, rows - 1);
        return new Size(n == 0 ? 0 : w, n == 0 ? 0 : h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int n = InternalChildren.Count, cols = Cols;
        double y = 0;
        for (int row = 0; row * cols < n; row++)
        {
            double x = 0;
            for (int col = 0; col < cols && row * cols + col < n; col++)
            {
                InternalChildren[row * cols + col].Arrange(new Rect(x, y, _colW[col], _rowH[row]));
                x += _colW[col] + Spacing;
            }
            y += _rowH[row] + Spacing * 0.6;
        }
        return finalSize;
    }
}
