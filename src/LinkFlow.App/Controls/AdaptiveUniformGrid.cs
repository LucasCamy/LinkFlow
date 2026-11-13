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

    public static readonly DependencyProperty BalanceRowsProperty = DependencyProperty.Register(
        nameof(BalanceRows), typeof(bool), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StackColumnsProperty = DependencyProperty.Register(
        nameof(StackColumns), typeof(bool), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Reduz colunas para não deixar um item sozinho na última linha.</summary>
    public bool BalanceRows
    {
        get => (bool)GetValue(BalanceRowsProperty);
        set => SetValue(BalanceRowsProperty, value);
    }

    /// <summary>Empilha os cartões com altura própria em cada coluna.</summary>
    public bool StackColumns
    {
        get => (bool)GetValue(StackColumnsProperty);
        set => SetValue(StackColumnsProperty, value);
    }

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

        // Evita um item sozinho na última linha (ex.: 4 cartões em 3 colunas viram 2x2)
        if (BalanceRows && count > 0)
        {
            int cols = _columns;
            while (cols > 2 && count % cols == 1) cols--;
            if (count % cols != 1 || cols == _columns) _columns = cols;
        }
        double cellW = width / _columns;

        double rowHeight = 0;
        double total = 0;
        var columnHeights = StackColumns ? new double[_columns] : null;
        int i = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(cellW, double.PositiveInfinity));
            if (columnHeights != null)
            {
                columnHeights[i++ % _columns] += child.DesiredSize.Height;
                continue;
            }
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++i % _columns == 0)
            {
                total += rowHeight;
                rowHeight = 0;
            }
        }
        return new Size(width, columnHeights?.Max() ?? total + rowHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double cellW = finalSize.Width / _columns;
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();
        if (StackColumns)
        {
            var offsets = new double[_columns];
            for (int i = 0; i < visible.Length; i++)
            {
                int column = i % _columns;
                double height = visible[i].DesiredSize.Height;
                visible[i].Arrange(new Rect(column * cellW, offsets[column], cellW, height));
                offsets[column] += height;
            }
            return finalSize;
        }
        double y = 0;
        for (int row = 0; row * _columns < visible.Length; row++)
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
