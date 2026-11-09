using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
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

    private MainWindow Owner => (MainWindow)Window.GetWindow(this);

    private void ExportBackup_Click(object sender, RoutedEventArgs e) => BackupService.Export(Owner, Owner.ViewModel);
    private void ImportBackup_Click(object sender, RoutedEventArgs e) => BackupService.Import(Owner, Owner.ViewModel);

    private void Backup_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f &&
                    (f[0].EndsWith(ConfigBackup.Extension, StringComparison.OrdinalIgnoreCase) || f[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Backup_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f)
        {
            e.Handled = true;
            BackupService.Import(Owner, Owner.ViewModel, f[0]);
        }
    }

    private void InstallService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.InstallService();
    private void UninstallService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.UninstallService();
    private void RestartService_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.RestartService();
    private void RestartAsAdmin_Click(object sender, RoutedEventArgs e) => App.Current.SettingsVm.RestartAsAdmin();
}
