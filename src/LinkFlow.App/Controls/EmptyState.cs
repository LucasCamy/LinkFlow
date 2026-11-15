using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace LinkFlow.App.Controls;

/// <summary>
/// Mensagem centralizada para listas sem itens ("Nenhum alerta recente registrado"): ícone, título e uma linha de
/// explicação, nas cores do tema.
/// </summary>
public sealed class EmptyState : Control
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(EmptyState), new PropertyMetadata(""));
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty DetailProperty =
        DependencyProperty.Register(nameof(Detail), typeof(string), typeof(EmptyState), new PropertyMetadata(null));

    static EmptyState()
    {
        FocusableProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(false));
        TemplateProperty.OverrideMetadata(typeof(EmptyState), new FrameworkPropertyMetadata(BuildTemplate()));
    }

    public EmptyState()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 24, 0, 24);
    }

    /// <summary>Glifo do Segoe Fluent Icons (padrão: informação).</summary>
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Detail { get => (string?)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    private static ControlTemplate BuildTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(FrameworkElement.MaxWidthProperty, 420.0);
        panel.SetValue(AutomationProperties.LiveSettingProperty, AutomationLiveSetting.Polite);

        var tile = new FrameworkElementFactory(typeof(Border));
        tile.SetValue(FrameworkElement.WidthProperty, 48.0);
        tile.SetValue(FrameworkElement.HeightProperty, 48.0);
        tile.SetValue(Border.CornerRadiusProperty, new CornerRadius(24));
        tile.SetResourceReference(Border.BackgroundProperty, "SurfaceAltBrush");
        tile.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Icon)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetValue(TextBlock.FontSizeProperty, 20.0);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        icon.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        tile.AppendChild(icon);
        panel.AppendChild(tile);

        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Title)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.FontSizeProperty, 14.0);
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        title.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        title.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        title.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 12, 0, 0));
        panel.AppendChild(title);

        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(Detail)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        detail.SetValue(TextBlock.FontSizeProperty, 12.5);
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        detail.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        detail.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        detail.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0));
        panel.AppendChild(detail);

        return new ControlTemplate(typeof(EmptyState)) { VisualTree = panel };
    }
}
