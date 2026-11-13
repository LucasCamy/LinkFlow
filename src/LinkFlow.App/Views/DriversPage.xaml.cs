using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.Views;

public partial class DriversPage : UserControl
{
    private IReadOnlyList<DriverUpdate> _updates = Array.Empty<DriverUpdate>();
    private InstalledDriverInventory? _installed;
    private CancellationTokenSource? _manufacturerDownload;

    private static readonly InstalledDriverInventory DemoInstalled = new(
    [
        new("Realtek High Definition Audio (simulado)", "Áudio", "Realtek", "6.0.9918.1", "oem42.inf",
            new DateOnly(2026, 5, 21), @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0B00&SUBSYS_1462EE12", null),
        new("Controlador NVM Express (simulado)", "Armazenamento", "Microsoft", "10.0.26100.1", "stornvme.inf", null, null, null)
    ], false);

    public DriversPage() => InitializeComponent();

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        SetResults(Array.Empty<DriverUpdate>());
        DriverStatus.Text = "Buscando drivers no Windows Update…";
        try
        {
            var result = App.Current.Options.Simulate
                ? new DriverSearchResult(
                [
                    new(new DriverIdentity(Guid.Parse("4319a80c-81ea-40c8-ae16-77fb270d8bb1"), 1),
                        "Driver de vídeo (simulado)", "Atualização de demonstração", null, null)
                ], false)
                : await Task.Run(WindowsDrivers.SearchAvailable);
            SetResults(result.Updates);
            DriverStatus.Text = _updates.Count == 0
                ? result.Incomplete ? "O Windows Update retornou uma busca parcial sem drivers para exibir." :
                    "O Windows Update não ofereceu drivers nesta busca."
                : $"{_updates.Count} {(_updates.Count == 1 ? "atualização disponível" : "atualizações disponíveis")}. Revise o driver que deseja instalar.";
            if (result.Incomplete && _updates.Count > 0) DriverStatus.Text += " O Windows Update retornou resultados parciais.";
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao buscar drivers", ex);
            DriverStatus.Text = ex is COMException com
                ? $"A busca falhou no Windows Update (0x{com.ErrorCode:X8}). Tente novamente ou abra o Windows Update."
                : "Não foi possível concluir a busca. Tente novamente ou abra o Windows Update.";
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
        DriverStatus.Text = $"Verificando, baixando e instalando {driver.Title}…";
        try
        {
            if (App.Current.Options.Simulate)
            {
                RemoveResult(driver);
                DriverStatus.Text = "Instalação simulada concluída.";
                return;
            }

            int? code = await WindowsDrivers.InstallAsync(driver.Identity, acceptLicense: driver.LicenseTerms != null);
            var status = (DriverInstallStatus?)code;
            if (status is DriverInstallStatus.Installed or DriverInstallStatus.InstalledRequiresRestart or DriverInstallStatus.NoLongerOffered)
                RemoveResult(driver);
            DriverStatus.Text = status switch
            {
                null => "Instalação cancelada na confirmação do Windows.",
                DriverInstallStatus.Installed => $"{driver.Title} instalado.",
                DriverInstallStatus.InstalledRequiresRestart => $"{driver.Title} instalado. Reinicie o Windows para concluir.",
                DriverInstallStatus.NoLongerOffered => "O Windows Update deixou de oferecer este driver. Faça uma nova busca para conferir.",
                DriverInstallStatus.RestartPending => "O Windows precisa ser reiniciado antes de instalar este driver.",
                DriverInstallStatus.InstallerBusy => "O Windows Update está instalando outra atualização. Tente novamente quando terminar.",
                DriverInstallStatus.RequiresReview => "Este driver precisa ser revisto no Windows Update. Abra a página para conferir.",
                DriverInstallStatus.DownloadFailed => "O Windows Update não concluiu o download do driver.",
                DriverInstallStatus.InstallationFailed => "O Windows Update não confirmou a instalação. Consulte o log do LinkFlow.",
                _ => $"O Windows Update retornou o erro 0x{code:X8}. Abra o Windows Update para conferir."
            };
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir o instalador de driver", ex);
            DriverStatus.Text = "Não foi possível iniciar a instalação do driver. Consulte o log do LinkFlow.";
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
    }

    private void RemoveResult(DriverUpdate driver) => SetResults(_updates.Where(update => update.Identity != driver.Identity).ToArray());

    private async void SearchManufacturers_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ManufacturerList.ItemsSource = null;
        ManufacturerStatus.Text = "Identificando as peças e consultando os fabricantes…";
        try
        {
            var result = App.Current.Options.Simulate
                ? new ManufacturerDriverSearch(
                [
                    new("NVIDIA GeForce RTX 4080 (simulada)", "Game Ready WHQL · instalada 621.01 · NVIDIA 621.24",
                        ManufacturerDrivers.NvidiaSupport, new("Game Ready 621.24 (simulado)", DriverPublisher.Nvidia, null)),
                    new("Radeon e Ryzen (simulados)", "AMD · Auto-Detect de drivers", ManufacturerDrivers.AmdSupport,
                        new("AMD Auto-Detect (simulado)", DriverPublisher.Amd, null, "Baixar Auto-Detect"))
                ], DemoInstalled, false)
                : await ManufacturerDrivers.SearchAsync();
            ManufacturerList.ItemsSource = result.Sources;
            ManufacturerStatus.Text = result.Sources.Count == 0
                ? "Nenhuma fonte oficial do catálogo foi associada ao hardware detectado."
                : $"{result.Sources.Count} {(result.Sources.Count == 1 ? "fonte encontrada" : "fontes encontradas")}.";
            if (result.Incomplete) ManufacturerStatus.Text += " Parte da consulta ficou indisponível.";
            ShowInstalled(result.Installed);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao consultar os fabricantes", ex);
            ManufacturerStatus.Text = "Não foi possível consultar os fabricantes. Tente novamente.";
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
        InstalledStatus.Text = "Lendo os drivers dos dispositivos conectados…";
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
            InstalledStatus.Text = "Não foi possível ler os drivers. Tente novamente.";
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
        ReadInstalledButton.Content = "Atualizar lista";
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
        InstalledStatus.Text = filter.Length == 0 ? $"{entries.Count} drivers de dispositivos conectados." :
            $"{entries.Count} de {inventory.Entries.Count} drivers correspondem ao filtro.";
        if (inventory.Incomplete) InstalledStatus.Text += " Parte do inventário ficou indisponível.";
    }

    private async void InstallManufacturer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ManufacturerDriverPackage package }) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Baixar {package.Title} e abrir o instalador oficial?\n\nConclua a instalação na janela do fabricante.",
                "Instalador do fabricante", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        if (App.Current.Options.Simulate)
        {
            ManufacturerStatus.Text = "Download e abertura do instalador simulados.";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _manufacturerDownload = cancellation;
        SetBusy(true);
        CancelDownloadButton.IsEnabled = true;
        CancelDownloadButton.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        DownloadProgress.Visibility = Visibility.Visible;
        ManufacturerStatus.Text = $"Preparando o download de {package.Title}…";
        try
        {
            var progress = new Progress<DriverDownloadProgress>(state =>
            {
                if (_manufacturerDownload != cancellation || cancellation.IsCancellationRequested) return;
                DownloadProgress.IsIndeterminate = state.Verifying || state.TotalBytes is not > 0;
                if (state.TotalBytes is > 0) DownloadProgress.Value = 100.0 * state.Bytes / state.TotalBytes.Value;
                ManufacturerStatus.Text = state.Verifying ? "Verificando a assinatura do instalador…" :
                    state.TotalBytes is > 0 ? $"Baixando {package.Title} · {100.0 * state.Bytes / state.TotalBytes:0}%" :
                    $"Baixando {package.Title} · {state.Bytes / 1048576.0:N0} MiB";
            });
            await OfficialDriverInstaller.DownloadAndOpenAsync(package, progress, cancellation.Token);
            ManufacturerStatus.Text = $"Instalador de {package.Title} aberto. Continue na janela do fabricante.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ManufacturerStatus.Text = "Download cancelado.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ManufacturerStatus.Text = "A abertura do instalador foi cancelada no Windows.";
        }
        catch (Exception ex)
        {
            Log.Error("Falha no instalador do fabricante", ex);
            ManufacturerStatus.Text = ex is InvalidOperationException or TimeoutException ? ex.Message :
                "O download ou a abertura falhou. Tente novamente ou use o site oficial.";
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
        ManufacturerStatus.Text = "Cancelando o download…";
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
            status.Text = "Não foi possível abrir a página oficial.";
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
            DriverStatus.Text = "Não foi possível abrir o Windows Update.";
        }
    }
}
