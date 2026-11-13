using System.Windows;
using System.Windows.Controls;

namespace LinkFlow.App.Views;

public partial class LightingPage : UserControl
{
    public LightingPage() => InitializeComponent();

    private void ShowDeviceWarnings_Click(object sender, RoutedEventArgs e) => DevicesTab.IsSelected = true;
}
