using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Views;

public partial class DriversPage : UserControl
{
    private IReadOnlyList<DriverUpdate> _updates = Array.Empty<DriverUpdate>();
    private InstalledDriverInventory? _installed;
    private CancellationTokenSource? _manufacturerDownload;

    private static readonly InstalledDriverInventory DemoInstalled = new(
    [
        new(Strings.Drv_RealtekHighDefinitionAudio, Strings.Hw_CatAudio, "Realtek", "6.0.9918.1", "oem42.inf",
            new DateOnly(2026, 5, 21), @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0B00&SUBSYS_1462EE12", null),
        new(Strings.Drv_ControladorNVMExpressSimulado, Strings.Dash_Storage, "Microsoft", "10.0.26100.1", "stornvme.inf", null, null, null)
    ], false);

    public DriversPage() => InitializeComponent();

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        SetResults(Array.Empty<DriverUpdate>());
        DriverStatus.Text = Strings.Drv_BuscandoDriversWindowsUpdate;
        try
        {
            var result = App.Current.Options.Simulate
                ? new DriverSearchResult(
                [
                    new(new DriverIdentity(Guid.Parse("4319a80c-81ea-40c8-ae16-77fb270d8bb1"), 1),
                        Strings.Drv_DriverVideoSimulado, Strings.Drv_AtualizacaoDemonstracao, null, null)
                ], false)
                : await Task.Run(WindowsDrivers.SearchAvailable);
            SetResults(result.Updates);
            DriverStatus.Text = _updates.Count == 0
                ? result.Incomplete ? Strings.Drv_WindowsUpdateRetornouBusca :
                    Strings.Drv_WindowsUpdateNaoOfereceu2
                : string.Format(_updates.Count == 1 ? Strings.Drv_UpdatesOne : Strings.Drv_UpdatesMany, _updates.Count);
            if (result.Incomplete && _updates.Count > 0) DriverStatus.Text += Strings.Drv_WindowsUpdateRetornouResultados;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao buscar drivers", ex);
            DriverStatus.Text = ex is COMException com
                ? string.Format(Strings.Drv_BuscaFalhouWindowsUpdate, com.ErrorCode)
                : Strings.Drv_NaoFoiPossivelConcluir;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DriverUpdate { CanInstall: true } driver }) return;
        var review = new DriverInstallDialog(driver) { Owner = Window.GetWindow(this) };
        if (review.ShowDialog() != true) return;

        SetBusy(true);
        DriverStatus.Text = string.Format(Strings.Drv_VerificandoBaixandoInstalando, driver.Title);
        string operationId = SystemOperationJournal.Shared.Start("windows-update-driver", driver.Title);
        try
        {
            if (App.Current.Options.Simulate)
            {
                RemoveResult(driver);
                DriverStatus.Text = Strings.Drv_InstalacaoSimuladaConcluida;
                SystemOperationJournal.Shared.Complete(operationId, WindowsOperationState.Unconfirmed, detail: "simulation");
                return;
            }

            int? code = await WindowsDrivers.InstallAsync(driver.Identity, acceptLicense: driver.LicenseTerms != null);
            var status = (DriverInstallStatus?)code;
            SystemOperationJournal.Shared.Complete(operationId, status switch
            {
                DriverInstallStatus.Installed => WindowsOperationState.Succeeded,
                DriverInstallStatus.InstalledRequiresRestart or DriverInstallStatus.RestartPending => WindowsOperationState.RequiresRestart,
                null => WindowsOperationState.Canceled,
                _ => WindowsOperationState.Unconfirmed
            }, code);
            if (status is DriverInstallStatus.Installed or DriverInstallStatus.InstalledRequiresRestart or DriverInstallStatus.NoLongerOffered)
                RemoveResult(driver);
            DriverStatus.Text = status switch
            {
                null => Strings.Drv_InstalacaoCanceladaConfirmacaoWindows,
                DriverInstallStatus.Installed => string.Format(Strings.Drv_Instalado, driver.Title),
                DriverInstallStatus.InstalledRequiresRestart => string.Format(Strings.Drv_InstaladoReinicieWindowsConcluir, driver.Title),
                DriverInstallStatus.NoLongerOffered => Strings.Drv_WindowsUpdateDeixouOferecer,
                DriverInstallStatus.RestartPending => Strings.Drv_WindowsPrecisaSerReiniciado,
                DriverInstallStatus.InstallerBusy => Strings.Drv_WindowsUpdateEstaInstalando,
                DriverInstallStatus.RequiresReview => Strings.Drv_EsteDriverPrecisaSer,
                DriverInstallStatus.DownloadFailed => Strings.Drv_WindowsUpdateNaoConcluiu,
                DriverInstallStatus.InstallationFailed => Strings.Drv_WindowsUpdateNaoConfirmou,
                _ => string.Format(Strings.Drv_WindowsUpdateRetornouErro, code)
            };
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir o instalador de driver", ex);
            SystemOperationJournal.Shared.Complete(operationId, WindowsOperationState.Failed, detail: ex.Message);
            DriverStatus.Text = Strings.Drv_NaoFoiPossivelIniciar;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetResults(IReadOnlyList<DriverUpdate> updates)
    {
        _updates = updates;
        DriverList.ItemsSource = updates;
        DriverEmpty.Visibility = updates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveResult(DriverUpdate driver) => SetResults(_updates.Where(update => update.Identity != driver.Identity).ToArray());

    private async void SearchManufacturers_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ManufacturerList.ItemsSource = null;
        ManufacturerEmpty.Visibility = Visibility.Collapsed;
        ManufacturerStatus.Text = Strings.Drv_IdentificandoPecasConsultandoFabricantes;
        try
        {
            var result = App.Current.Options.Simulate
                ? new ManufacturerDriverSearch(
                [
                    new(Strings.Drv_NVIDIAGeForceRTX4080, Strings.Drv_GameReadyWHQLInstalada,
                        ManufacturerDrivers.NvidiaSupport, new(Strings.Drv_GameReady62124, DriverPublisher.Nvidia, null)),
                    new(Strings.Drv_RadeonRyzenSimulados, Strings.Drv_AMDAutoDetectDrivers, ManufacturerDrivers.AmdSupport,
                        new(Strings.Drv_AMDAutoDetectSimulado, DriverPublisher.Amd, null, Strings.Drv_BaixarAutoDetect))
                ], DemoInstalled, false)
                : await ManufacturerDrivers.SearchAsync();
            ManufacturerList.ItemsSource = result.Sources;
            ManufacturerEmpty.Visibility = result.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ManufacturerStatus.Text = result.Sources.Count == 0
                ? Strings.Drv_NenhumaFonteOficialCatalogo
                : string.Format(result.Sources.Count == 1 ? Strings.Drv_SourcesOne : Strings.Drv_SourcesMany, result.Sources.Count);
            if (result.Incomplete) ManufacturerStatus.Text += Strings.Drv_ParteConsultaFicouIndisponivel;
            ShowInstalled(result.Installed);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao consultar os fabricantes", ex);
            ManufacturerStatus.Text = Strings.Drv_NaoFoiPossivelConsultar;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void ReadInstalled_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        InstalledFilterPanel.IsEnabled = false;
        InstalledStatus.Text = Strings.Drv_LendoDriversDispositivosConectados;
        try
        {
            var result = App.Current.Options.Simulate ? DemoInstalled : await Task.Run(ManufacturerDrivers.ReadInstalled);
            ShowInstalled(result);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao ler drivers instalados", ex);
            _installed = null;
            InstalledList.ItemsSource = null;
            InstalledFilterPanel.Visibility = Visibility.Collapsed;
            InstalledStatus.Text = Strings.Drv_NaoFoiPossivelLer;
        }
        finally
        {
            InstalledFilterPanel.IsEnabled = true;
            SetBusy(false);
        }
    }

    private void ShowInstalled(InstalledDriverInventory inventory)
    {
        _installed = inventory;
        InstalledFilterPanel.Visibility = inventory.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReadInstalledButton.Content = Strings.Win_AtualizarLista;
        FilterInstalled();
    }

    private void InstalledFilter_Changed(object sender, TextChangedEventArgs e) => FilterInstalled();

    private void FilterInstalled()
    {
        if (_installed is not { } inventory) return;
        string filter = InstalledFilter.Text.Trim();
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        bool Contains(string? value) => value != null && compare.IndexOf(value, filter, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
        var entries = filter.Length == 0 ? inventory.Entries : inventory.Entries.Where(entry =>
            Contains(entry.Name) || Contains(entry.Details) || Contains(entry.HardwareId)).ToArray();
        InstalledList.ItemsSource = entries;
        InstalledEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (entries.Count == 0 && filter.Length == 0)
        {
            InstalledEmpty.Title = Strings.Drv_NenhumDriverDispositivoEncontrado;
            InstalledEmpty.Detail = Strings.Drv_WindowsNaoInformouDrivers;
        }
        else
        {
            InstalledEmpty.Title = Strings.Drv_NenhumDriverCorrespondeFiltro;
            InstalledEmpty.Detail = Strings.Drv_TenteNomeDispositivoFabricante;
        }
        InstalledStatus.Text = filter.Length == 0 ? string.Format(Strings.Drv_DriversDispositivosConectados, entries.Count) :
            string.Format(Strings.Drv_DriversCorrespondemFiltro, entries.Count, inventory.Entries.Count);
        if (inventory.Incomplete) InstalledStatus.Text += Strings.Drv_ParteInventarioFicouIndisponivel;
    }

    private async void InstallManufacturer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManufacturerDriverPackage package }) return;
        if (MessageBox.Show(Window.GetWindow(this), string.Format(Strings.Drv_BaixarAbrirInstaladorOficial, package.Title),
                Strings.Drv_InstaladorFabricante, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (App.Current.Options.Simulate)
        {
            ManufacturerStatus.Text = Strings.Drv_DownloadAberturaInstaladorSimulados;
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _manufacturerDownload = cancellation;
        SetBusy(true);
        CancelDownloadButton.IsEnabled = true;
        CancelDownloadButton.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Visibility = Visibility.Visible;
        ManufacturerStatus.Text = string.Format(Strings.Drv_PreparandoDownload, package.Title);
        try
        {
            var progress = new Progress<DriverDownloadProgress>(state =>
            {
                if (_manufacturerDownload != cancellation || cancellation.IsCancellationRequested) return;
                DownloadProgress.IsIndeterminate = state.Verifying || state.TotalBytes is not > 0;
                if (state.TotalBytes is > 0) DownloadProgress.Value = 100.0 * state.Bytes / state.TotalBytes.Value;
                ManufacturerStatus.Text = state.Verifying ? Strings.Drv_VerificandoAssinaturaInstalador :
                    state.TotalBytes is > 0 ? string.Format(Strings.Drv_Baixando, package.Title, 100.0 * state.Bytes / state.TotalBytes) :
                    string.Format(Strings.Drv_BaixandoMiB, package.Title, state.Bytes / 1048576.0);
            });
            await OfficialDriverInstaller.DownloadAndOpenAsync(package, progress, cancellation.Token);
            ManufacturerStatus.Text = string.Format(Strings.Drv_InstaladorAbertoContinueJanela, package.Title);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ManufacturerStatus.Text = Strings.Set_DownloadCancelado;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ManufacturerStatus.Text = Strings.Drv_AberturaInstaladorFoiCancelada;
        }
        catch (Exception ex)
        {
            Log.Error("Falha no instalador do fabricante", ex);
            ManufacturerStatus.Text = ex is InvalidOperationException or TimeoutException ? ex.Message :
                Strings.Drv_DownloadOuAberturaFalhou;
        }
        finally
        {
            _manufacturerDownload = null;
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            DownloadProgress.Visibility = Visibility.Collapsed;
            DownloadProgress.IsIndeterminate = false;
            DownloadProgress.Value = 0;
            SetBusy(false);
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        CancelDownloadButton.IsEnabled = false;
        _manufacturerDownload?.Cancel();
        ManufacturerStatus.Text = Strings.Drv_CancelandoDownload;
    }

    private void OpenManufacturer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ManufacturerDriverSource { SupportUrl: { } url } }) OpenDriverLink(url, ManufacturerStatus);
    }

    private void OpenInstalledLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri url }) OpenDriverLink(url, InstalledStatus);
    }

    private static void OpenDriverLink(Uri url, TextBlock status)
    {
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir a página do driver", ex);
            status.Text = Strings.Drv_NaoFoiPossivelAbrir;
        }
    }

    private void SetBusy(bool busy) =>
        DriverForm.IsEnabled = DriverList.IsEnabled = SearchManufacturersButton.IsEnabled =
            ManufacturerList.IsEnabled = ReadInstalledButton.IsEnabled = !busy;

    private void OpenUpdate_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir o Windows Update", ex);
            DriverStatus.Text = Strings.Drv_NaoFoiPossivelAbrir2;
        }
    }
}
