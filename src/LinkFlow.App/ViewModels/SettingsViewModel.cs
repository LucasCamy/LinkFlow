using LinkFlow.App.Services;

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
}
