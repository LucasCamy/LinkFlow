using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace LinkFlow.App.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage() => InitializeComponent();

    internal void ShowAlerts()
    {
        DashboardTabs.SelectedIndex = 0;
        Dispatcher.InvokeAsync(AlertPanel.BringIntoView, DispatcherPriority.Loaded);
    }

    private void InstallService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.InstallService();
    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.RestartAsAdmin();
}
