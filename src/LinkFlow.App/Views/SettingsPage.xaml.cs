using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.Core;

namespace LinkFlow.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível abrir {path}: {ex.Message}");
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(Log.CurrentLogDir);
    private void OpenConfig_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.SharedDir);
    private void ExitApp_Click(object sender, RoutedEventArgs e) => App.Current.ExitApplication();

    private void InstallService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.InstallService();
    private void UninstallService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.UninstallService();
    private void RestartService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.RestartService();
    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.RestartAsAdmin();
}
