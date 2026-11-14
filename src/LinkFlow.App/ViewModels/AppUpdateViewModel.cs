using System.Diagnostics;
using System.IO;
using LinkFlow.App.Services;
using LinkFlow.Core;

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
    private string ReadyStatus => $"LinkFlow {_release!.Version} baixado e verificado. O aplicativo será reaberto ao fim da instalação.";

    public AppUpdateViewModel(UiSettingsService settings, bool allowInstall)
    {
        _settings = settings;
        _installedDirectory = allowInstall ? LinkFlowInstallation.FindRegisteredDirectory(AutoStartManager.ExePath) : null;
        Status = _installedDirectory == null
            ? "Versão portátil. As atualizações são baixadas pelo navegador."
            : "Consulte as atualizações do LinkFlow no GitHub.";
    }

    public event Action<Version>? UpdateFound;

    public string VersionText => $"Versão atual {CurrentVersion.ToString(3)}";

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
    public string ActionText => _installer != null ? "Instalar e reiniciar" :
        CanDownloadInstaller ? "Baixar atualização" : "Baixar no navegador";

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
        if (!automatic) Status = "Consultando a versão mais recente…";
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
            else if (release != null) Status = $"LinkFlow {release.Version} disponível.";
            else Status = "Você está usando a versão mais recente do LinkFlow.";

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
            if (!automatic) Status = "Não foi possível consultar as atualizações. Tente novamente ou abra a página de downloads.";
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
            Status = "Não foi possível abrir a página de downloads.";
        }
    }

    public void CancelDownload() => _download?.Cancel();

    private async Task DownloadAsync(AppRelease release)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _download = download;
        Progress = 0;
        Status = $"Baixando o instalador do LinkFlow {release.Version}…";
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
            Status = "Download cancelado.";
        }
        catch (InvalidDataException ex)
        {
            Log.Warn($"Instalador do LinkFlow rejeitado: {ex.Message}");
            Status = $"{ex.Message} Tente baixar novamente.";
        }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao baixar a atualização do LinkFlow: {ex.Message}");
            Status = "Não foi possível baixar a atualização. Tente novamente ou abra a página de downloads.";
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
        Status = "Abrindo o instalador. Confirme no Windows se ele pedir permissão.";
        try
        {
            var directory = LinkFlowInstallation.FindRegisteredDirectory(AutoStartManager.ExePath);
            if (directory == null || !string.Equals(directory, _installedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _installer?.Dispose();
                _installer = null;
                Status = "A instalação do LinkFlow mudou. Reabra o aplicativo antes de tentar atualizar.";
                return;
            }

            // O próprio carregador do Inno solicita UAC e conserva a sessão de origem para reabrir a interface.
            var start = new ProcessStartInfo(installer.Path) { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory };
            foreach (var argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/NORESTARTAPPLICATIONS", "/LOG", "/LINKFLOWUPDATE=1", $"/DIR={directory}" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("O instalador não iniciou.");
            await process.WaitForExitAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Log.Info($"Instalador do LinkFlow encerrado com código {process.ExitCode}.");
                Status = "A atualização não foi concluída. Tente novamente ou abra a página de downloads.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao iniciar o instalador do LinkFlow: {ex.Message}");
            Status = "Não foi possível abrir o instalador. Tente novamente ou abra a página de downloads.";
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
