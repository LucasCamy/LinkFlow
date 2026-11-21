using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

public partial class OperationalPage : UserControl
{
    public OperationalPage() => InitializeComponent();
    private OperationalCenterViewModel? ViewModel => DataContext as OperationalCenterViewModel;
    private void Page_Loaded(object sender, RoutedEventArgs e) => ViewModel?.SetActive(IsVisible);
    private void Page_Unloaded(object sender, RoutedEventArgs e) => ViewModel?.SetActive(false);
    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => ViewModel?.SetActive(IsVisible);
    private async void ExportCsv_Click(object sender, RoutedEventArgs e) => await ExportAsync(true);
    private async void ExportJson_Click(object sender, RoutedEventArgs e) => await ExportAsync(false);
    private async Task ExportAsync(bool csv)
    {
        if (ViewModel is not { } vm || Window.GetWindow(this) is not { } owner) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = csv ? OperationalText.ExportCsv : OperationalText.ExportJson,
            Filter = csv ? "CSV (*.csv)|*.csv" : "JSON (*.json)|*.json",
            FileName = $"LinkFlow-History-{DateTime.Now:yyyyMMdd-HHmm}.{(csv ? "csv" : "json")}",
            AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(owner) == true) await vm.ExportAsync(dialog.FileName, csv);
    }
}
