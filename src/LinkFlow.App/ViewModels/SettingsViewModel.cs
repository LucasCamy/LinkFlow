using LinkFlow.App.Services;
using LinkFlow.Core;

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
        Task.Run(() =>
        {
            bool enabled = AutoStartManager.IsEnabled();
            bool task = AutoStartManager.IsTaskMode;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _startWithWindows = enabled;
                _autoStartMessage = enabled ? (task ? "Ativo • tarefa agendada com privilégios elevados, inicia na bandeja" : "Ativo • inicia na bandeja ao fazer logon") : null;
                OnPropertiesChanged(nameof(StartWithWindows), nameof(AutoStartMessage));
            });
        });
    }

    public UiSettings Current => _settings.Current;

    /// <summary>Após importar preferências: atualiza todos os interruptores da tela.</summary>
    public void NotifyAllChanged() => OnPropertiesChanged(nameof(CloseToTray), nameof(MinimizeToTray),
        nameof(ShowTrayTemperature), nameof(AlertNotifications), nameof(GlobalHotkeys));

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
                ? (AutoStartManager.IsTaskMode ? "Ativo • tarefa agendada com privilégios elevados, inicia na bandeja" : "Ativo • inicia na bandeja ao fazer logon")
                : null;
        }
        else
        {
            AutoStartMessage = $"Não foi possível alterar: {error}";
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
    public string ExitButtonText => IsServiceMode ? "Fechar interface" : "Sair do LinkFlow";

    public string ServiceStatusText
    {
        get
        {
            if (_serviceMessage != null) return _serviceMessage;
            if (IsServiceMode) return _engine!.IsConnected ? "Conectado ao serviço" : "Serviço sem resposta • reconectando...";
            return _serviceState switch
            {
                ServiceState.NotInstalled => "Serviço não instalado • modo local",
                ServiceState.Running => "Serviço em execução, mas esta janela está em modo local",
                ServiceState.Stopped => "Serviço instalado e parado • modo local",
                ServiceState.Starting => "Serviço iniciando...",
                ServiceState.Stopping => "Serviço parando...",
                _ => "Modo local"
            };
        }
    }

    public string ServiceDetailText
    {
        get
        {
            if (IsServiceMode) return "O hardware é controlado pelo serviço LinkFlowService (inicia com o Windows, reinicia sozinho se falhar).";
            if (!ServiceManager.ServiceExeExists) return "LinkFlow.Service.exe não está na pasta do aplicativo. Use o script de publicação para gerar.";
            return AutoStartManager.IsElevated
                ? "O controle roda dentro deste aplicativo, como administrador."
                : "O controle roda dentro deste aplicativo, sem administrador (temperatura da CPU indisponível).";
        }
    }

    public string ExitHintText => IsServiceMode
        ? "Sair fecha só a interface: o serviço continua controlando ventoinhas, RGB e LCD."
        : "Ao sair, o hub iCUE LINK volta ao modo hardware (curva interna) e a GPU volta à curva da vBIOS.";

    public void InstallService()
    {
        if (!ServiceManager.ServiceExeExists)
        {
            ShowServiceMessage("LinkFlow.Service.exe não encontrado na pasta do aplicativo.");
            return;
        }
        var answer = System.Windows.MessageBox.Show(
            "O LinkFlow vai instalar o serviço do Windows \"LinkFlowService\" e reabrir a interface conectada a ele.\n\n" +
            "• O controle das ventoinhas, RGB e LCD passa a rodar em segundo plano, mesmo sem janela aberta.\n" +
            "• Inicia automaticamente com o Windows e reinicia sozinho se falhar.\n" +
            "• A interface deixa de pedir permissão de administrador.\n\n" +
            "O Windows vai pedir confirmação (UAC). Continuar?",
            "Instalar serviço LinkFlow", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        if (!App.Current.SwitchMode("install-service")) ShowServiceMessage("Instalação cancelada.");
    }

    public void UninstallService()
    {
        var answer = System.Windows.MessageBox.Show(
            "Remover o serviço LinkFlowService?\n\nO LinkFlow volta ao modo local: o controle do hardware só funciona com o aplicativo aberto (ou na bandeja).",
            "Remover serviço", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;
        if (!App.Current.SwitchMode("uninstall-service")) ShowServiceMessage("Remoção cancelada.");
    }

    public async void RestartService()
    {
        _busy = true;
        ShowServiceMessage("Reiniciando serviço...");
        var code = await App.Current.RunElevatedCommandAsync("restart-service");
        _busy = false;
        ShowServiceMessage(code switch
        {
            null => "Operação cancelada.",
            0 => "Serviço reiniciado.",
            var c => $"Falha ao reiniciar o serviço (código {c})."
        });
        RefreshServiceState();
    }

    public void RestartAsAdmin()
    {
        if (!App.Current.RestartElevated()) ShowServiceMessage("Elevação cancelada.");
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
