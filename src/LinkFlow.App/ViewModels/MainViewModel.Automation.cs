using System.Collections.ObjectModel;
using System.Windows.Input;
using LinkFlow.Core;
using LinkFlow.App.Resources;
using LinkFlow.App.Services;

namespace LinkFlow.App.ViewModels;

public sealed record ProfileOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Regra "jogo/app → perfil" na tela.</summary>
public sealed class AppRuleItem : ObservableObject
{
    private readonly MainViewModel _owner;
    private string _profile;
    private bool _enabled;

    public AppRuleItem(MainViewModel owner, AppRule rule)
    {
        _owner = owner;
        Process = rule.Process;
        _profile = rule.Profile;
        _enabled = rule.Enabled;
    }

    public string Process { get; }

    public string Profile
    {
        get => _profile;
        set { if (SetProperty(ref _profile, value)) _owner.UpdateRule(Process, r => r.Profile = value); }
    }

    public bool Enabled
    {
        get => _enabled;
        set { if (SetProperty(ref _enabled, value)) _owner.UpdateRule(Process, r => r.Enabled = value); }
    }
}

/// <summary>Perfis automáticos por jogo/app e horário silencioso.</summary>
public partial class MainViewModel
{
    private string _newRuleProcess = string.Empty;
    private string _automationStatus = string.Empty;
    private ICommand? _addRuleCommand, _removeRuleCommand, _refreshAppsCommand;
    private bool _isRefreshingApps;
    private string _appRefreshStatus = string.Empty;

    public ObservableCollection<AppRuleItem> AppRules { get; } = new();
    public ObservableCollection<string> RunningApps { get; } = new();
    public event Action? AutomationChanged;

    public IReadOnlyList<ProfileOption> ProfileOptions =>
        FanPresets.All.Select(p => new ProfileOption(p.Key, p.Label))
            .Concat(_engine.Config.Profiles.Select(p => new ProfileOption(p.Name, string.Format(Strings.Fan_Salvo, p.Name))))
            .ToList();

    public string NewRuleProcess
    {
        get => _newRuleProcess;
        set => SetProperty(ref _newRuleProcess, value ?? string.Empty);
    }

    public string AutomationStatus
    {
        get => _automationStatus;
        set { if (SetProperty(ref _automationStatus, value)) OnPropertyChanged(nameof(HasAutomationStatus)); }
    }

    public bool HasAutomationStatus => _automationStatus.Length > 0;
    public bool HasAppRules => AppRules.Count > 0;

    public ICommand AddRuleCommand => _addRuleCommand ??= new RelayCommand(() =>
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(NewRuleProcess.Trim());
        if (name.Length == 0) return;
        if (_engine.Config.Automation.Rules.Any(r => string.Equals(r.Process, name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast(string.Format(Strings.Fan_JaExisteRegra, name), canUndo: false);
            return;
        }
        UpdateAutomation(a => a.Rules.Add(new AppRule { Process = name, Profile = FanPresets.Performance }));
        NewRuleProcess = string.Empty;
        ShowToast(string.Format(Strings.Fan_AbrirPerfilDesempenhoAplicado, name), canUndo: false);
    }, () => NewRuleProcess.Trim().Length > 0);

    public ICommand RemoveRuleCommand => _removeRuleCommand ??= new RelayCommand(p =>
    {
        if (p is AppRuleItem r) UpdateAutomation(a => a.Rules.RemoveAll(x => x.Process == r.Process));
    });

    /// <summary>Lista os programas com janela aberta (para escolher sem digitar o nome do .exe).</summary>
    public bool IsRefreshingApps
    {
        get => _isRefreshingApps;
        private set
        {
            if (SetProperty(ref _isRefreshingApps, value))
            {
                OnPropertyChanged(nameof(CanRefreshApps));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool CanRefreshApps => !_uiWorkStopped && !IsRefreshingApps;
    public string AppRefreshStatus { get => _appRefreshStatus; private set => SetProperty(ref _appRefreshStatus, value); }
    public ICommand RefreshAppsCommand => _refreshAppsCommand ??= new RelayCommand(async () => await RefreshAppsAsync(), () => CanRefreshApps);

    internal async Task RefreshAppsAsync(Func<CancellationToken, IReadOnlyList<string>>? read = null)
    {
        _dispatcher.VerifyAccess();
        if (!CanRefreshApps) return;
        IsRefreshingApps = true;
        AppRefreshStatus = OperationalText.Pick("Lendo programas abertos…", "Reading open applications…");
        var cancellation = UiWorkToken;
        try
        {
            var result = await Task.Run(() =>
            {
                var names = (read ?? RunningApplications.Read)(cancellation).Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name, StringComparer.OrdinalIgnoreCase).Take(513).ToArray();
                cancellation.ThrowIfCancellationRequested();
                return names;
            }, cancellation).ConfigureAwait(false);
            await PublishUiWorkAsync(() =>
            {
                if (_uiWorkStopped || cancellation.IsCancellationRequested) return;
                string currentInput = NewRuleProcess;
                RunningApps.Clear();
                foreach (var name in result.Take(512)) RunningApps.Add(name);
                NewRuleProcess = currentInput;
                OnPropertyChanged(nameof(NewRuleProcess)); // Preserve edits made while the editable ComboBox was loading.
                AppRefreshStatus = result.Length > 512 ? OperationalText.Pick("Exibindo os primeiros 512 programas.", "Showing the first 512 applications.")
                    : result.Length == 0 ? OperationalText.Pick("Nenhum programa com janela aberta foi encontrado.", "No applications with an open window were found.") : string.Empty;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await PublishUiWorkAsync(() =>
            {
                if (!_uiWorkStopped) AppRefreshStatus = OperationalText.Pick("Não foi possível atualizar os programas: ", "Could not refresh applications: ") + ex.Message;
            }).ConfigureAwait(false);
        }
        finally { await PublishUiWorkAsync(() => IsRefreshingApps = false).ConfigureAwait(false); }
    }

    public bool QuietEnabled
    {
        get => _engine.Config.Automation.Quiet.Enabled;
        set => UpdateAutomation(a => a.Quiet.Enabled = value);
    }

    public int QuietStart
    {
        get => _engine.Config.Automation.Quiet.StartHour;
        set => UpdateAutomation(a => a.Quiet.StartHour = value);
    }

    public int QuietEnd
    {
        get => _engine.Config.Automation.Quiet.EndHour;
        set => UpdateAutomation(a => a.Quiet.EndHour = value);
    }

    public string QuietProfile
    {
        get => _engine.Config.Automation.Quiet.Profile;
        set { if (value != null) UpdateAutomation(a => a.Quiet.Profile = value); }
    }

    public static IReadOnlyList<int> Hours { get; } = Enumerable.Range(0, 24).ToList();

    internal void UpdateRule(string process, Action<AppRule> change) =>
        UpdateAutomation(a => { if (a.Rules.FirstOrDefault(r => r.Process == process) is { } rule) change(rule); });

    private void UpdateAutomation(Action<AutomationConfig> change)
    {
        if (IsLoadingConfig) return;
        _engine.UpdateConfig(c => change(c.Automation));
        LoadAutomation(_engine.Config);
        AutomationChanged?.Invoke();
    }

    private void LoadAutomation(ControlConfig c)
    {
        var rules = c.Automation.Rules;
        if (!rules.Select(r => (r.Process, r.Profile, r.Enabled)).SequenceEqual(AppRules.Select(r => (r.Process, r.Profile, r.Enabled))))
        {
            AppRules.Clear();
            foreach (var r in rules) AppRules.Add(new AppRuleItem(this, r));
            OnPropertyChanged(nameof(HasAppRules));
        }
        OnPropertiesChanged(nameof(QuietEnabled), nameof(QuietStart), nameof(QuietEnd), nameof(QuietProfile), nameof(ProfileOptions));
    }

    /// <summary>Aplicado pela automação (sem "Desfazer": é automático e reversível pela própria regra).</summary>
    public void ApplyAutomationProfile(string key, string reason)
    {
        if (FanPresets.Find(key) != null) _engine.UpdateConfig(c => FanPresets.Apply(c, key));
        else _engine.UpdateConfig(c => c.Profiles.FirstOrDefault(p => p.Name == key)?.ApplyTo(c));
        LoadConfig(_engine.Config);
        RefreshPresetChips();
        ShowToast(string.Format(Strings.Fan_Perfil, PresetNames.Label(key), reason), canUndo: false);
        Log.Info($"Automação: perfil {key} ({reason}).");
    }
}
