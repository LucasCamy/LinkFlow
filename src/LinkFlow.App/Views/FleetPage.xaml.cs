using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

public partial class FleetPage : UserControl
{
    public FleetPage() => InitializeComponent();
    private FleetViewModel? Vm => DataContext as FleetViewModel;
    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Vm?.SetActive(IsVisible);
    private async void Pair_Click(object sender, RoutedEventArgs e) { if (Vm != null && await Vm.PairAsync(PairingToken.Password)) PairingToken.Clear(); }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (Vm != null) await Vm.RefreshAsync(); }
    private async void ApplyProfile_Click(object sender, RoutedEventArgs e) { if (Vm != null) await Vm.ApplyProfileAsync(); }
    private async void QueryHistory_Click(object sender, RoutedEventArgs e) { if (Vm != null) await Vm.QueryHistoryAsync(); }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        try { Vm?.RemoveSelected(); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "LinkFlow", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { CanExport: true } vm) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.json", FileName = "LinkFlow-remote-history.json" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await vm.ExportHistoryAsync(dialog.FileName);
    }
}
