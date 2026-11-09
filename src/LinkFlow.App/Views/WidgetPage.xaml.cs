using System.Windows;
using System.Windows.Controls;

namespace LinkFlow.App.Views;

public partial class WidgetPage : UserControl
{
    public WidgetPage() => InitializeComponent();

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void ToggleWidget_Click(object sender, RoutedEventArgs e)
    {
        if (Main is { } w) w.SetWidgetVisible(!w.IsMiniWidgetVisible);
    }

    private void Corner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string corner } && Main is { } w) w.MoveWidgetToCorner(corner);
    }
}
