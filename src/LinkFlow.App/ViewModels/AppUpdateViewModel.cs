using System.Diagnostics;
using System.IO;
using LinkFlow.App.Services;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.ViewModels;

public sealed class AppUpdateViewModel : ObservableObject, IDisposable
{
    private static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LinkFlow", "updates");
    private static readonly Version CurrentVersion = typeof(App).Assembly.GetName().Version!;
    private readonly UiSettingsService _settings;
    private readonly string? _installedDirectory;
    private readonly CancellationTokenSource _lifetime = new();

    private AppRelease? _release;
    private DownloadedInstaller? _installer;
    private CancellationTokenSource? _download;
    private Version? _lastNotified;
    private bool _busy;
    private string _status = "";
    private int _progress;
    private string ReadyStatus => string.Format(Strings.Set_LinkFlowBaixadoVerificadoAplicativo, _release!.Version);

    public AppUpdateViewModel(UiSettingsService settings, bool allowInstall)
    {
        _settings = settings;
        _installedDirectory = allowInstall ? LinkFlowInstallation.FindRegisteredDirectory(AutoStartManager.ExePath) : null;
        Status = _installedDirectory == null
            ? Strings.Set_VersaoPortatilAtualizacoesSao
            : Strings.Set_ConsulteAtualizacoesLinkFlowGitHub;
    }

    public event Action<Version>? UpdateFound;

    public string VersionText => string.Format(Strings.Set_VersaoAtual, CurrentVersion.ToString(3));

    public bool AutomaticCheck
    {
        get => _settings.Current.CheckForAppUpdates;
        set
        {
            if (value == AutomaticCheck) return;
            _settings.Update(settings => settings.CheckForAppUpdates = value);
            OnPropertyChanged();
            if (value) _ = CheckAsync(automatic: true);
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public int Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public bool CanCheck => !_busy;
    public bool HasUpdate => _release != null;
    public bool IsDownloading => _download != null;
    private bool CanDownloadInstaller => _installedDirectory != null && _release?.Installer != null;
    public bool ShowDownloadsLink => !HasUpdate || CanDownloadInstaller;
    public string ActionText => _installer != null ? Strings.Set_InstalarReiniciar :
        CanDownloadInstaller ? Strings.Set_BaixarAtualizacao : Strings.Set_BaixarNavegador;

    public async Task WatchAsync()
    {
        var cancellationToken = _lifetime.Token;
        _ = Task.Run(() => DownloadedInstaller.CleanOldDownloads(Cache), cancellationToken);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                if (AutomaticCheck && _installer == null) await CheckAsync(automatic: true);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task CheckAsync(bool automatic = false)
    {
        if (_busy) return;
        var cancellationToken = _lifetime.Token;
        if (!automatic) Status = Strings.Set_ConsultandoVersaoMaisRecente;
        SetBusy(true);
        try
        {
            var release = await AppUpdateClient.Official.CheckAsync(CurrentVersion, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            if (_installer != null && (release?.Installer is not { } asset || release.Version != _release!.Version ||
                !asset.Hash.AsSpan().SequenceEqual(_release.Installer!.Hash)))
            {
                _installer.Dispose();
                _installer = null;
            }
            _release = release;
            if (_installer != null) Status = ReadyStatus;
            else if (release != null) Status = string.Format(Strings.Set_LinkFlowDisponivel, release.Version);
            else Status = Strings.Set_VoceEstaUsandoVersao;

            if (release != null && release.Version != _lastNotified && (!automatic || AutomaticCheck))
            {
                _lastNotified = release.Version;
                if (automatic) UpdateFound?.Invoke(release.Version);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"Falha na consulta de atualização do LinkFlow: {ex.Message}");
            if (!automatic) Status = Strings.Set_NaoFoiPossivelConsultar;
        }
        finally { SetBusy(false); }
    }

    public Task RunActionAsync()
    {
        if (_busy || _release == null) return Task.CompletedTask;
        if (_installer != null) return InstallAsync(_installer);
        if (CanDownloadInstaller) return DownloadAsync(_release);
        OpenDownloads();
        return Task.CompletedTask;
    }

    public void OpenDownloads()
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(_release?.Page ?? AppUpdateClient.ReleasesPage)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao abrir a página de releases: {ex.Message}");
            Status = Strings.Set_NaoFoiPossivelAbrir;
        }
    }

    public void CancelDownload() => _download?.Cancel();

    private async Task DownloadAsync(AppRelease release)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = download;
        Progress = 0;
        Status = string.Format(Strings.Set_BaixandoInstaladorLinkFlow, release.Version);
        SetBusy(true);
        try
        {
            var installer = await AppUpdateClient.Official.DownloadAsync(release.Installer!, Cache,
                new Progress<int>(value => { if (_download == download) Progress = value; }), download.Token);
            if (download.IsCancellationRequested)
            {
                installer.Dispose();
                download.Token.ThrowIfCancellationRequested();
            }
            _installer = installer;
            Status = ReadyStatus;
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested)
        {
            Status = Strings.Set_DownloadCancelado;
        }
        catch (InvalidDataException ex)
        {
            Log.Warn($"Instalador do LinkFlow rejeitado: {ex.Message}");
            Status = string.Format(Strings.Set_TenteBaixarNovamente, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao baixar a atualização do LinkFlow: {ex.Message}");
            Status = Strings.Set_NaoFoiPossivelBaixar;
        }
        finally
        {
            _download = null;
            SetBusy(false);
        }
    }

    private async Task InstallAsync(DownloadedInstaller installer)
    {
        var cancellationToken = _lifetime.Token;
        SetBusy(true);
        Status = Strings.Set_AbrindoInstaladorConfirmeWindows;
        try
        {
            var directory = LinkFlowInstallation.FindRegisteredDirectory(AutoStartManager.ExePath);
            if (directory == null || !string.Equals(directory, _installedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _installer?.Dispose();
                _installer = null;
                Status = Strings.Set_InstalacaoLinkFlowMudouReabra;
                return;
            }

            // O próprio carregador do Inno solicita UAC e conserva a sessão de origem para reabrir a interface.
            var start = new ProcessStartInfo(installer.Path) { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory };
            foreach (var argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/NORESTARTAPPLICATIONS", "/LOG", "/LINKFLOWUPDATE=1", $"/DIR={directory}" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException(Strings.Set_InstaladorNaoIniciou);
            await process.WaitForExitAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Log.Info($"Instalador do LinkFlow encerrado com código {process.ExitCode}.");
                Status = Strings.Set_AtualizacaoNaoFoiConcluida;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao iniciar o instalador do LinkFlow: {ex.Message}");
            Status = Strings.Set_NaoFoiPossivelAbrir2;
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        OnPropertiesChanged(nameof(CanCheck), nameof(HasUpdate), nameof(IsDownloading), nameof(ShowDownloadsLink), nameof(ActionText));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _installer?.Dispose();
        _lifetime.Dispose();
    }
}
