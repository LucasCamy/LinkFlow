using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage() => InitializeComponent();

    internal void ShowAlertHistory()
    {
        if (DataContext is not MainViewModel viewModel || Window.GetWindow(this) is not { } owner) return;
        var dialog = new AlertHistoryDialog(viewModel.AlertCenter) { Owner = owner };
        dialog.ShowDialog();
    }

    private void OpenAlertHistory_Click(object sender, RoutedEventArgs e) => ShowAlertHistory();

    private void InstallService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.InstallService();
    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.RestartAsAdmin();
}
