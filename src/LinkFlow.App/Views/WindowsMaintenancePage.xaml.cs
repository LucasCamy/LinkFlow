using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.Core;

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
        VolumeStatus.Text = "Consultando volumes locais…";
        UpdatePreview();
        try
        {
            IReadOnlyList<WindowsMaintenanceVolume> volumes = IsSimulation
                ? [new("C:", true)]
                : await Task.Run(WindowsMaintenanceOperation.ReadVolumes);
            Volumes.ItemsSource = volumes;
            Volumes.SelectedItem = volumes.FirstOrDefault(volume => volume.Letter == previous);
            VolumeStatus.Text = volumes.Count == 0 ? "Nenhum volume local fixo está disponível." : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao consultar volumes para o CHKDSK", ex);
            Volumes.ItemsSource = null;
            VolumeStatus.Text = "Não foi possível listar os volumes. Atualize a lista para tentar novamente.";
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
        CommandPreview.Text = hasTarget ? operation.Preview(SelectedVolume?.Letter) : "Selecione um volume para conferir o comando.";
        ImpactPreview.Text = operation.Impact;
        ExecutionHint.Text = IsSimulation ? "Prévia em modo simulado." :
            operation.RequiresElevation ? "Requer privilégios administrativos. O andamento aparece no console do Windows." :
            "A ferramenta abre em uma janela do Windows.";
        RunButton.Content = operation.IsDiskCleanup ? "Abrir Limpeza de Disco" :
            operation.IsRepair ? "Revisar e executar reparo" : "Abrir verificação";
        RunButton.IsEnabled = !_running && !IsSimulation && hasTarget && (!operation.UsesVolume || !_readingVolumes);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_running || IsSimulation || !RunButton.IsEnabled || Operations.SelectedItem is not WindowsMaintenanceOperation operation) return;

        string? volume = SelectedVolume?.Letter;
        if (operation.IsRepair && MessageBox.Show(Window.GetWindow(this),
                $"{operation.Preview(volume)}\n\n{operation.Impact}\n\nExecutar este reparo?",
                "Revisar reparo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _running = true;
        MaintenanceForm.IsEnabled = false;
        RunStatus.Visibility = Visibility.Visible;
        ResultsLocation.Visibility = Visibility.Collapsed;
        RunStatus.Text = "Aguardando abertura da ferramenta pelo Windows…";
        bool started = false;
        try
        {
            using var process = await Task.Run(() => operation.Start(volume));
            if (process == null)
            {
                RunStatus.Text = "Solicitação encaminhada ao Windows. Confira a janela da ferramenta.";
                return;
            }

            started = true;
            string? resultsLocation = operation.ResultsLocation;
            ResultsLocation.Text = resultsLocation ?? string.Empty;
            ResultsLocation.Visibility = resultsLocation == null ? Visibility.Collapsed : Visibility.Visible;
            RunStatus.Text = operation.IsDiskCleanup ? "Acompanhe a Limpeza de Disco na janela do Windows." :
                "Em andamento. Acompanhe o progresso e responda às perguntas no console da ferramenta.";
            await process.WaitForExitAsync();
            int code = process.ExitCode;
            string displayCode = code < 0 ? $"0x{unchecked((uint)code):X8}" : code.ToString();
            RunStatus.Text = $"Processo encerrado · código {displayCode}.";
            if (code == 3010) RunStatus.Text += " O Windows informou que é necessário reiniciar.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RunStatus.Text = "A solicitação do Windows foi cancelada.";
        }
        catch (Exception ex)
        {
            Log.Error($"Falha na manutenção do Windows ({operation.Title})", ex);
            RunStatus.Text = started ? "A ferramenta foi iniciada. Não foi possível ler o código de saída." :
                ex is FileNotFoundException or InvalidOperationException ? ex.Message :
                ex is Win32Exception win32 ? $"O Windows não abriu a ferramenta. Código {win32.NativeErrorCode}." :
                "Não foi possível abrir a ferramenta. Tente novamente.";
        }
        finally
        {
            _running = false;
            MaintenanceForm.IsEnabled = true;
            UpdatePreview();
        }
    }
}
