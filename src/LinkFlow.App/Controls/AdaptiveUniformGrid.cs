using System.Windows;
using System.Windows.Controls;

namespace LinkFlow.App.Controls;

/// <summary>
/// Grade de colunas iguais que escolhe sozinha o número de colunas pela largura disponível
/// (ex.: 4 cartões lado a lado em tela larga, 2 em janela média, 1 em janela estreita).
/// </summary>
public sealed class AdaptiveUniformGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    private int _columns = 1;

    private int VisibleCount
    {
        get
        {
            int n = 0;
            foreach (UIElement c in InternalChildren) if (c.Visibility != Visibility.Collapsed) n++;
            return n;
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        int count = VisibleCount;
        double width = double.IsInfinity(available.Width) ? MinItemWidth * Math.Max(1, count) : available.Width;
        _columns = Math.Clamp((int)(width / MinItemWidth), 1, Math.Max(1, Math.Min(MaxColumns, Math.Max(1, count))));
        double cellW = width / _columns;

        double rowHeight = 0;
        double total = 0;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(cellW, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++i % _columns == 0)
            {
                total += rowHeight;
                rowHeight = 0;
            }
        }
        total += rowHeight;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double cellW = finalSize.Width / _columns;
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double y = 0;
        for (int row = 0; row * _columns < visible.Count; row++)
        {
            var items = visible.Skip(row * _columns).Take(_columns).ToList();
            double h = items.Max(c => c.DesiredSize.Height);
            for (int col = 0; col < items.Count; col++)
            {
                items[col].Arrange(new Rect(col * cellW, y, cellW, h));
            }
            y += h;
        }
        return finalSize;
    }
}
