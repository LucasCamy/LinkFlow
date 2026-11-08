using System.Windows;
using System.Windows.Controls;

namespace LinkFlow.App.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage() => InitializeComponent();

    private void InstallService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.InstallService();
    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.RestartAsAdmin();
}
