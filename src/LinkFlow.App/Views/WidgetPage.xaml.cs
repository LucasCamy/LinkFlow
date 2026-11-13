using System.Windows;
using System.Windows.Controls;

namespace LinkFlow.App.Views;

public partial class WidgetPage : UserControl
{
    public WidgetPage() => InitializeComponent();

    private MainWindow? Main => Window.GetWindow(this) as MainWindow;

    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var dock = e.NewSize.Width >= 860 ? Dock.Right : Dock.Top;
        bool besideEditor = dock == Dock.Right;
        if (DockPanel.GetDock(PreviewCard) != dock)
        {
            DockPanel.SetDock(PreviewCard, dock);
            PreviewCard.Width = besideEditor ? 392 : double.NaN;
            PreviewCard.Padding = new Thickness(besideEditor ? 18 : 12);
            PreviewCard.Margin = besideEditor ? new Thickness(12, 58, 0, 0) : new Thickness(0, 0, 0, 12);
            PreviewStage.MinHeight = besideEditor ? 110 : 72;
        }

        PreviewStage.MaxHeight = besideEditor
            ? Math.Clamp(e.NewSize.Height - 128, 110, 380)
            : Math.Clamp(e.NewSize.Height / 3, 84, 200);
    }

    private void GoToPicker_Click(object sender, RoutedEventArgs e) => PickerPanel.BringIntoView();

    private void ToggleWidget_Click(object sender, RoutedEventArgs e)
    {
        if (Main is { } w) w.SetWidgetVisible(!w.IsMiniWidgetVisible);
    }

    private void Corner_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string corner } && Main is { } w) w.MoveWidgetToCorner(corner);
    }
}
