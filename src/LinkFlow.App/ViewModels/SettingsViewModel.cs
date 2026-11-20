using LinkFlow.App.Services;
using LinkFlow.Core;
using LinkFlow.App.Resources;
using System.Globalization;

namespace LinkFlow.App.ViewModels;

/// <summary>Preferências do aplicativo (bandeja, início automático, notificações).</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly UiSettingsService _settings;
    private bool _startWithWindows;
    private string? _autoStartMessage;

    public SettingsViewModel(UiSettingsService settings)
    {
        _settings = settings;
        RefreshAutoStart();
    }

    /// <summary>Relê o início automático (ex.: depois de trocar a tarefa elevada pela chave Run).</summary>
    public void RefreshAutoStart() => Task.Run(() =>
    {
        bool enabled = AutoStartManager.IsEnabled();
        bool task = AutoStartManager.IsTaskMode;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _startWithWindows = enabled;
            _autoStartMessage = enabled ? (task ? Strings.Set_AtivoTarefaAgendadaPrivilegios : Strings.Set_AtivoIniciaBandejaFazer) : null;
            OnPropertiesChanged(nameof(StartWithWindows), nameof(AutoStartMessage));
        });
    });

    public UiSettings Current => _settings.Current;

    // ------------------------------------------------------------------ Idioma

    public sealed record LanguageOption(string Code, string Label)
    {
        public override string ToString() => Label;   // o modelo do ComboBox mostra o ToString do item escolhido
    }

    /// <summary>"" = automático (segue o Windows).</summary>
    public IReadOnlyList<LanguageOption> LanguageOptions { get; } =
    [
        new(string.Empty, Strings.Language_Automatic),
        new(UiLanguage.Portuguese, Strings.Language_Portuguese),
        new(UiLanguage.English, Strings.Language_English),
    ];

    public string LanguageChoice
    {
        get => _settings.Current.Language ?? string.Empty;
        set
        {
            string? language = string.IsNullOrEmpty(value) ? null : value;
            if (language == _settings.Current.Language) return;
            _settings.Update(s => s.Language = language);
            OnPropertiesChanged(nameof(LanguageChoice), nameof(LanguageChangePending));
        }
    }

    /// <summary>O idioma escolhido é diferente do que está em uso: falta reiniciar a interface.</summary>
    public bool LanguageChangePending =>
        UiLanguage.Resolve(null, _settings.Current.Language, CultureInfo.InstalledUICulture) != UiLanguage.Culture.Name;

    /// <summary>Após importar preferências: atualiza todos os interruptores da tela.</summary>
    public void NotifyAllChanged() => OnPropertiesChanged(nameof(CloseToTray), nameof(MinimizeToTray),
        nameof(ShowTrayTemperature), nameof(AlertNotifications), nameof(GlobalHotkeys), nameof(AdvancedSystemTools));

    public bool CloseToTray
    {
        get => _settings.Current.CloseToTray;
        set { _settings.Update(s => s.CloseToTray = value); OnPropertyChanged(); }
    }

    public bool MinimizeToTray
    {
        get => _settings.Current.MinimizeToTray;
        set { _settings.Update(s => s.MinimizeToTray = value); OnPropertyChanged(); }
    }

    public bool ShowTrayTemperature
    {
        get => _settings.Current.ShowTrayTemperature;
        set { _settings.Update(s => s.ShowTrayTemperature = value); OnPropertyChanged(); }
    }

    public bool AlertNotifications
    {
        get => _settings.Current.AlertNotifications;
        set { _settings.Update(s => s.AlertNotifications = value); OnPropertyChanged(); }
    }

    public bool Animations
    {
        get => _settings.Current.Animations;
        set
        {
            _settings.Update(s => s.Animations = value);
            Services.Motion.UserEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Drivers, remoção de apps e manutenção do Windows (desligado em instalações novas).</summary>
    public bool AdvancedSystemTools
    {
        get => _settings.Current.AdvancedSystemTools == true;
        set { _settings.Update(s => s.AdvancedSystemTools = value); OnPropertyChanged(); }
    }

    public bool GlobalHotkeys
    {
        get => _settings.Current.GlobalHotkeys;
        set { _settings.Update(s => s.GlobalHotkeys = value); OnPropertyChanged(); }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set => SetStartWithWindows(value);
    }

    public string? AutoStartMessage
    {
        get => _autoStartMessage;
        private set => SetProperty(ref _autoStartMessage, value);
    }

    public void SetStartWithWindows(bool enable)
    {
        if (_startWithWindows == enable) return;
        string? error = enable ? AutoStartManager.Enable(preferTask: !App.Current.IsServiceMode) : AutoStartManager.Disable();
        if (error == null)
        {
            _startWithWindows = enable;
            AutoStartMessage = enable
                ? (AutoStartManager.IsTaskMode ? Strings.Set_AtivoTarefaAgendadaPrivilegios : Strings.Set_AtivoIniciaBandejaFazer)
                : null;
        }
        else
        {
            AutoStartMessage = string.Format(Strings.Set_NaoFoiPossivelAlterar, error);
        }
        OnPropertyChanged(nameof(StartWithWindows));
        AutoStartChanged?.Invoke();
    }

    public event Action? AutoStartChanged;

    // ------------------------------------------------------------------ Serviço do Windows

    private IEngineClient? _engine;
    private ServiceState _serviceState = ServiceState.Unknown;
    private string? _serviceMessage;
    private bool _busy;

    public void AttachEngine(IEngineClient engine)
    {
        _engine = engine;
        engine.ConnectionChanged += _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            OnServicePropertiesChanged();
            RefreshServiceState();
        });
        RefreshServiceState();
    }

    public void RefreshServiceState()
    {
        if (_engine?.IsSimulated == true && !_engine.IsRemote)
        {
            _serviceState = ServiceState.Unknown;
            OnServicePropertiesChanged();
            return;
        }
        Task.Run(() =>
        {
            var state = ServiceManager.Query();
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _serviceState = state;
                OnServicePropertiesChanged();
            });
        });
    }

    private void OnServicePropertiesChanged() => OnPropertiesChanged(
        nameof(ServiceStatusText), nameof(ServiceDetailText), nameof(ServiceInstalled), nameof(ServiceCanInstall),
        nameof(IsServiceMode), nameof(IsServiceDisconnected), nameof(IsLimitedLocal), nameof(CanRestartAsAdmin), nameof(ExitHintText), nameof(ExitButtonText));

    public bool IsServiceMode => _engine?.IsRemote == true;
    public bool IsServiceDisconnected => _engine is { IsRemote: true, IsConnected: false };

    /// <summary>Modo local sem administrador: temperatura da CPU indisponível (LibreHardwareMonitor exige elevação).</summary>
    public bool IsLimitedLocal => _engine is { IsRemote: false, IsSimulated: false } && !AutoStartManager.IsElevated;
    public bool CanRestartAsAdmin => IsLimitedLocal;

    public bool ServiceInstalled => _serviceState is not (ServiceState.NotInstalled or ServiceState.Unknown);
    public bool ServiceCanInstall => !IsServiceMode && _serviceState == ServiceState.NotInstalled && !_busy;
    public string ExitButtonText => IsServiceMode ? Strings.Set_FecharInterface : Strings.Tray_Exit;

    public string ServiceStatusText
    {
        get
        {
            if (_serviceMessage != null) return _serviceMessage;
            if (IsServiceMode) return _engine!.IsConnected ? Strings.Set_ConectadoServico : Strings.Set_ServicoSemRespostaReconectando;
            return _serviceState switch
            {
                ServiceState.NotInstalled => Strings.Set_ServicoNaoInstaladoModo,
                ServiceState.Running => Strings.Set_ServicoExecucaoMasEsta,
                ServiceState.Stopped => Strings.Set_ServicoInstaladoParadoModo,
                ServiceState.Starting => Strings.Set_ServicoIniciando,
                ServiceState.Stopping => Strings.Set_ServicoParando,
                _ => Strings.Set_ModoLocal
            };
        }
    }

    public string ServiceDetailText
    {
        get
        {
            if (IsServiceMode) return Strings.Set_HardwareControladoPeloServico;
            if (!ServiceManager.ServiceExeExists) return Strings.Set_LinkFlowServiceExeNao;
            return AutoStartManager.IsElevated
                ? Strings.Set_ControleRodaDentroDeste
                : Strings.Set_ControleRodaDentroDeste2;
        }
    }

    public string ExitHintText => IsServiceMode
        ? Strings.Set_SairFechaSoInterface
        : Strings.Set_SairHubICUELINK;

    public void InstallService()
    {
        if (!ServiceManager.ServiceExeExists)
        {
            ShowServiceMessage(Strings.Set_LinkFlowServiceExeNao2);
            return;
        }
        var answer = System.Windows.MessageBox.Show(
            Strings.Set_LinkFlowVaiInstalarServico +
            Strings.Set_ControleVentoinhasRGBLCD +
            Strings.Set_IniciaAutomaticamenteWindowsReinicia +
            Strings.Set_InterfaceDeixaPedirPermissao +
            Strings.Set_WindowsVaiPedirConfirmacao,
            Strings.Set_InstalarServicoLinkFlow, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        if (!App.Current.SwitchMode("install-service")) ShowServiceMessage(Strings.Set_InstalacaoCancelada);
    }

    public void UninstallService()
    {
        var answer = System.Windows.MessageBox.Show(
            Strings.Set_RemoverServicoLinkFlowServiceLinkFlow,
            Strings.Set_RemoverServico, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        if (!App.Current.SwitchMode("uninstall-service")) ShowServiceMessage(Strings.Set_RemocaoCancelada);
    }

    public async void RestartService()
    {
        _busy = true;
        ShowServiceMessage(Strings.Set_ReiniciandoServico);
        var code = await App.Current.RunElevatedCommandAsync("restart-service");
        _busy = false;
        ShowServiceMessage(code switch
        {
            null => Strings.Set_OperacaoCancelada,
            0 => Strings.Set_ServicoReiniciado,
            var c => string.Format(Strings.Set_FalhaReiniciarServicoCodigo, c)
        });
        RefreshServiceState();
    }

    public void RestartAsAdmin()
    {
        if (!App.Current.RestartElevated()) ShowServiceMessage(Strings.Set_ElevacaoCancelada);
    }

    private void ShowServiceMessage(string message)
    {
        _serviceMessage = message;
        OnServicePropertiesChanged();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _serviceMessage = null;
            OnServicePropertiesChanged();
        };
        timer.Start();
    }
}
