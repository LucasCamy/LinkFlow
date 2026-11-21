using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Views;

public partial class WindowsMaintenancePage : UserControl
{
    private bool _readingVolumes;
    private bool _running;

    private static bool IsSimulation => Application.Current is App { Options.Simulate: true };
    private WindowsMaintenanceVolume? SelectedVolume => Volumes.SelectedItem as WindowsMaintenanceVolume;

    public WindowsMaintenancePage()
    {
        InitializeComponent();
        Tools.ItemsSource = WindowsMaintenanceOperation.Tools;
        Tools.SelectedIndex = 0;
    }

    private async void Tool_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Tools.SelectedItem is not WindowsMaintenanceTool tool) return;

        Operations.ItemsSource = tool.Operations;
        Operations.SelectedIndex = 0;
        if (Operations.SelectedItem is WindowsMaintenanceOperation { UsesVolume: true })
            await RefreshVolumesAsync();
    }

    private void Operation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RunStatus.Visibility = ResultsLocation.Visibility = Visibility.Collapsed;
        UpdatePreview();
    }

    private void Volume_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_running) RunStatus.Visibility = ResultsLocation.Visibility = Visibility.Collapsed;
        UpdatePreview();
    }

    private async void RefreshVolumes_Click(object sender, RoutedEventArgs e) => await RefreshVolumesAsync();

    private async Task RefreshVolumesAsync()
    {
        if (_readingVolumes || _running) return;

        string? previous = SelectedVolume?.Letter;
        _readingVolumes = true;
        RefreshVolumesButton.IsEnabled = Volumes.IsEnabled = false;
        VolumeStatus.Text = Strings.Win_ConsultandoVolumesLocais;
        UpdatePreview();
        try
        {
            IReadOnlyList<WindowsMaintenanceVolume> volumes = IsSimulation
                ? [new("C:", true)]
                : await Task.Run(WindowsMaintenanceOperation.ReadVolumes);
            Volumes.ItemsSource = volumes;
            Volumes.SelectedItem = volumes.FirstOrDefault(volume => volume.Letter == previous);
            VolumeStatus.Text = volumes.Count == 0 ? Strings.Win_NenhumVolumeLocalFixo : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao consultar volumes para o CHKDSK", ex);
            Volumes.ItemsSource = null;
            VolumeStatus.Text = Strings.Win_NaoFoiPossivelListar;
        }
        finally
        {
            _readingVolumes = false;
            RefreshVolumesButton.IsEnabled = Volumes.IsEnabled = true;
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        if (Operations.SelectedItem is not WindowsMaintenanceOperation operation)
        {
            RunButton.IsEnabled = false;
            return;
        }

        VolumeControls.Visibility = operation.UsesVolume ? Visibility.Visible : Visibility.Collapsed;
        bool hasTarget = !operation.UsesVolume || SelectedVolume != null;
        CommandPreview.Text = hasTarget ? operation.Preview(SelectedVolume?.Letter) : Strings.Win_SelecioneVolumeConferirComando;
        ImpactPreview.Text = operation.Impact;
        ExecutionHint.Text = IsSimulation ? Strings.Win_PreviaModoSimulado :
            operation.RequiresElevation ? Strings.Win_RequerPrivilegiosAdministrativosAndamento :
            Strings.Win_FerramentaAbreJanelaWindows;
        RunButton.Content = operation.IsDiskCleanup ? Strings.Win_AbrirLimpezaDisco :
            operation.IsRepair ? Strings.Win_RevisarExecutarReparo : Strings.Win_AbrirVerificacao;
        RunButton.IsEnabled = !_running && !IsSimulation && hasTarget && (!operation.UsesVolume || !_readingVolumes);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running || IsSimulation || !RunButton.IsEnabled || Operations.SelectedItem is not WindowsMaintenanceOperation operation) return;

        string? volume = SelectedVolume?.Letter;
        if (operation.IsRepair && MessageBox.Show(Window.GetWindow(this),
                string.Format(Strings.Win_ExecutarEsteReparo, operation.Preview(volume), operation.Impact),
                Strings.Win_RevisarReparo, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _running = true;
        MaintenanceForm.IsEnabled = false;
        RunStatus.Visibility = Visibility.Visible;
        ResultsLocation.Visibility = Visibility.Collapsed;
        RunStatus.Text = Strings.Win_AguardandoAberturaFerramentaPelo;
        bool started = false;
        string operationId = SystemOperationJournal.Shared.Start("maintenance", operation.Title);
        try
        {
            using var process = await Task.Run(() => operation.Start(volume));
            if (process == null)
            {
                SystemOperationJournal.Shared.Complete(operationId, WindowsOperationState.Unconfirmed);
                RunStatus.Text = Strings.Win_SolicitacaoEncaminhadaWindowsConfira;
                return;
            }

            started = true;
            SystemOperationJournal.Shared.AttachProcess(operationId, process);
            string? resultsLocation = operation.ResultsLocation;
            ResultsLocation.Text = resultsLocation ?? string.Empty;
            ResultsLocation.Visibility = resultsLocation == null ? Visibility.Collapsed : Visibility.Visible;
            RunStatus.Text = operation.IsDiskCleanup ? Strings.Win_AcompanheLimpezaDiscoJanela :
                Strings.Win_AndamentoAcompanheProgressoResponda;
            await process.WaitForExitAsync();
            int code = process.ExitCode;
            SystemOperationJournal.Shared.Complete(operationId, code == 3010 ? WindowsOperationState.RequiresRestart : WindowsOperationState.ProcessExited,
                code, operation.ResultsLocation);
            string displayCode = code < 0 ? $"0x{unchecked((uint)code):X8}" : code.ToString();
            RunStatus.Text = string.Format(Strings.Win_ProcessoEncerradoCodigo, displayCode);
            if (code == 3010) RunStatus.Text += Strings.Win_WindowsInformouNecessarioReiniciar;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SystemOperationJournal.Shared.Complete(operationId, WindowsOperationState.Canceled, ex.NativeErrorCode);
            RunStatus.Text = Strings.Win_SolicitacaoWindowsFoiCancelada;
        }
        catch (Exception ex)
        {
            SystemOperationJournal.Shared.Complete(operationId, started ? WindowsOperationState.Unconfirmed : WindowsOperationState.Failed, detail: ex.Message);
            Log.Error($"Falha na manutenção do Windows ({operation.Title})", ex);
            RunStatus.Text = started ? Strings.Win_FerramentaFoiIniciadaNao :
                ex is FileNotFoundException or InvalidOperationException ? ex.Message :
                ex is Win32Exception win32 ? string.Format(Strings.Win_WindowsNaoAbriuFerramenta, win32.NativeErrorCode) :
                Strings.Win_NaoFoiPossivelAbrir2;
        }
        finally
        {
            _running = false;
            MaintenanceForm.IsEnabled = true;
            UpdatePreview();
        }
    }
}
