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
    private readonly UpdateRecoveryStore _recovery = new(Path.Combine(AppPaths.UserDir, "updates"));
    private IEngineClient? _engine;
    private UpdateRecoveryRecord? _recoveryRecord;

    private AppRelease? _release;
    private DownloadedInstaller? _installer;
    private CancellationTokenSource? _download;
    private Version? _lastNotified;
    private bool _busy;
    private string _status = "";
    private int _progress;
    private string ReadyStatus => _installer?.Trust.PublisherVerified == true
        ? string.Format(Strings.Set_LinkFlowBaixadoVerificadoAplicativo, _release!.Version)
        : Text($"LinkFlow {_release!.Version}: integridade do download conferida. Distribuição de desenvolvimento sem confirmação de publicador.",
            $"LinkFlow {_release!.Version}: download integrity checked. Development distribution without publisher verification.");
    private static string Text(string pt, string en) => UiLanguage.Culture.TwoLetterISOLanguageName == "en" ? en : pt;

    public AppUpdateViewModel(UiSettingsService settings, bool allowInstall)
    {
        _settings = settings;
        _installedDirectory = allowInstall ? LinkFlowInstallation.FindRegisteredDirectory(AutoStartManager.ExePath) : null;
        Status = _installedDirectory == null
            ? Strings.Set_VersaoPortatilAtualizacoesSao
            : Strings.Set_ConsulteAtualizacoesLinkFlowGitHub;
    }

    public event Action<Version>? UpdateFound;
    public void AttachEngine(IEngineClient engine) => _engine = engine;
    public string DistributionStatus => AppUpdateClient.Official.Channel == DistributionChannel.Official
        ? Text("Distribuição oficial: assinatura e publicador são obrigatórios para instalar atualizações.", "Official distribution: update installation requires a verified signature and publisher.")
        : Text("Distribuição de desenvolvimento sem assinatura oficial. A verificação de SHA-256 confirma apenas a integridade do download.",
            "Development distribution without an official signature. SHA-256 verification confirms download integrity only.");
    public bool CanRestoreConfiguration => !_busy && _recoveryRecord?.State == UpdateRecoveryState.Failed && _engine is { IsConnected: true, CanWrite: true };
    public string RecoveryStatus => _recoveryRecord?.State switch
    {
        UpdateRecoveryState.Pending or UpdateRecoveryState.Validating => Text("Atualização aguardando validação da interface e do controle.", "Update awaiting application and control health validation."),
        UpdateRecoveryState.Healthy => Text("Última atualização validada: versão, comunicação e monitoramento disponíveis.", "Last update validated: version, communication and monitoring available."),
        UpdateRecoveryState.Failed => Text("A última atualização não passou pela validação de saúde. A configuração anterior foi preservada para recuperação.",
            "The last update did not pass health validation. The previous configuration is retained for recovery."),
        UpdateRecoveryState.ConfigurationRestored => Text("Configuração anterior restaurada. A versão do aplicativo permanece instalada.", "Previous configuration restored. The installed application version is unchanged."),
        _ => string.Empty
    };

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
            if (_engine is { } engine)
            {
                try
                {
                    _recoveryRecord = await _recovery.ValidatePendingAsync(() => new(CurrentVersion, engine.IsRemote, engine.IsConnected,
                        engine.ServiceVersion, engine.NegotiatedProtocolVersion, engine.Snapshot), TimeSpan.FromSeconds(30), cancellationToken);
                    OnPropertiesChanged(nameof(RecoveryStatus), nameof(CanRestoreConfiguration));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"Validação da atualização não concluída: {ex.Message}");
                    Status = Text("Não foi possível validar a última atualização. Consulte o diagnóstico antes de iniciar outra atualização.",
                        "Could not validate the last update. Review diagnostics before starting another update.");
                }
            }
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

    public async Task RestorePreviousConfigurationAsync()
    {
        if (!CanRestoreConfiguration || _engine == null) return;
        SetBusy(true);
        try
        {
            var result = await _recovery.RestoreConfigurationAsync(_engine, _lifetime.Token);
            _recoveryRecord = _recovery.Load();
            Status = _recoveryRecord?.State == UpdateRecoveryState.ConfigurationRestored ? RecoveryStatus : result.Message;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warn($"Recuperação de configuração: {ex.Message}"); Status = ex.Message; }
        finally { OnPropertyChanged(nameof(RecoveryStatus)); SetBusy(false); }
    }

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
            if (_engine == null) throw new InvalidOperationException(Text("O controle ainda não está disponível para preparar a recuperação da atualização.",
                "Control is not available yet to prepare update recovery."));
            _recoveryRecord = _recovery.Prepare(CurrentVersion, _release!.Version, _engine);
            OnPropertyChanged(nameof(RecoveryStatus));

            // O próprio carregador do Inno solicita UAC e conserva a sessão de origem para reabrir a interface.
            var start = new ProcessStartInfo(installer.Path) { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory };
            foreach (var argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/NORESTARTAPPLICATIONS", "/LOG", "/LINKFLOWUPDATE=1", $"/DIR={directory}" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException(Strings.Set_InstaladorNaoIniciou);
            await process.WaitForExitAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
            {
                Log.Info($"Instalador do LinkFlow encerrado com código {process.ExitCode}.");
                _recoveryRecord = _recovery.MarkFailed($"installer-exit-{process.ExitCode}");
                OnPropertyChanged(nameof(RecoveryStatus));
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
        OnPropertiesChanged(nameof(CanCheck), nameof(HasUpdate), nameof(IsDownloading), nameof(ShowDownloadsLink), nameof(ActionText), nameof(CanRestoreConfiguration));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _installer?.Dispose();
        _lifetime.Dispose();
    }
}
