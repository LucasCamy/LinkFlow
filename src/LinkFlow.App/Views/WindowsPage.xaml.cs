using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LinkFlow.App.Services;
using LinkFlow.Core;

namespace LinkFlow.App.Views;

public partial class WindowsPage : UserControl
{
    private static readonly uint[] TimeoutSeconds = [60, 120, 300, 600, 900, 1200, 1800, 3600];
    private static readonly SettingsShortcut[] Shortcuts =
    [
        new("Tela", "\uE7F4", "ms-settings:display"),
        new("Gráficos", "\uE950", "ms-settings:display-advancedgraphics"),
        new("Energia e bateria", "\uE945", "ms-settings:powersleep"),
        new("Modo de Jogo", "\uE7FC", "ms-settings:gaming-gamemode"),
        new("Apps de inicialização", "\uE768", "ms-settings:startupapps"),
        new("Sensor de armazenamento", "\uE8B7", "ms-settings:storagesense")
    ];
    private static readonly SettingsShortcut[] GamingShortcuts =
    [
        new("Modo de Jogo", "\uE7FC", "ms-settings:gaming-gamemode", "Ajuste o comportamento do Windows durante jogos."),
        new("GPU por jogo", "\uE950", "ms-settings:display-advancedgraphics", "Escolha a preferência de GPU por aplicativo."),
        new("Capturas", "\uE714", "ms-settings:gaming-gamedvr", "Revise a gravação em segundo plano.")
    ];
    private static readonly DiagnosticQuery[] Diagnostics =
    [
        new("Estado do PC", "Tempo de atividade, reinício e espaço nos volumes.", "\uE7F4", WindowsDiagnostics.ReadSystem),
        new("Histórico de atualizações", "Resultados registrados pelo Windows Update.", "\uE81C", WindowsDiagnostics.ReadUpdateHistory),
        new("Eventos do sistema", "Erros, eventos críticos e avisos de hardware e disco.", "\uE9D9", WindowsDiagnostics.ReadSystemEvents),
        new("Saúde S.M.A.R.T.", "Alertas, temperatura e desgaste que os discos reportam.", "\uEDA2", WindowsDiskHealth.Read)
    ];
    private WindowsPowerState? _power;
    private PowerUndo? _undo;
    private WindowsDiagnosticResult? _diagnostic;

    private sealed record SettingsShortcut(string Title, string Icon, string Uri, string? Description = null);
    private sealed record DiagnosticQuery(string Title, string Description, string Icon, Func<WindowsDiagnosticResult> Read);

    private sealed record TimeoutOption(uint Seconds, string Label)
    {
        public override string ToString() => Label;
    }
    private sealed record PowerUndo(Func<WindowsPowerState, bool> IsApplied, Action Restore);

    private sealed class TimeoutEditor
    {
        public TimeoutEditor(PowerIdleTimeout setting)
        {
            Setting = setting;
            Options = TimeoutSeconds.Append(setting.Seconds).Where(value => value > 0).Distinct().Order()
                .Select(value => new TimeoutOption(value, value % 60 == 0 ? $"{value / 60} min" : $"{value} s"))
                .Append(new TimeoutOption(0, "Nunca")).ToArray();
            Selected = Options.First(option => option.Seconds == setting.Seconds);
        }

        public PowerIdleTimeout Setting { get; }
        public IReadOnlyList<TimeoutOption> Options { get; }
        public TimeoutOption? Selected { get; set; }
        public string Label => $"{(Setting.Action == PowerIdleAction.Sleep ? "Suspender o PC" : "Apagar a tela")} {(Setting.OnBattery ? "na bateria" : "na tomada")}";
    }

    public WindowsPage()
    {
        InitializeComponent();
        SettingsShortcuts.ItemsSource = Shortcuts;
        GameShortcuts.ItemsSource = GamingShortcuts;
        DiagnosticActions.ItemsSource = Diagnostics;
    }

    private async void ReadPower_Click(object sender, RoutedEventArgs e) => await RefreshPowerAsync();

    private void PlanSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox selector) return;
        var button = ReferenceEquals(selector, GamePlans) ? ActivateGamePlanButton : ActivatePlanButton;
        button.IsEnabled = selector.SelectedItem is PowerPlan plan && plan.Id != _power?.ActivePlan;
    }

    private async void ActivatePlan_Click(object sender, RoutedEventArgs e)
    {
        var selector = ReferenceEquals(sender, ActivateGamePlanButton) ? GamePlans : Plans;
        if (selector.SelectedItem is not PowerPlan plan || _power is not { } previous || previous.ActivePlan == plan.Id) return;
        var undo = new PowerUndo(state => state.ActivePlan == plan.Id, () => WindowsPower.Activate(previous.ActivePlan, plan.Id));
        await RefreshPowerAsync(() => WindowsPower.Activate(plan.Id, previous.ActivePlan), undo, $"Plano {plan.Name} ativado.");
    }

    private async void SaveTimeout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TimeoutEditor { Selected: { } option } editor } || _power is not { } power) return;
        var setting = editor.Setting;
        if (option.Seconds == setting.Seconds)
        {
            await RefreshPowerAsync();
            return;
        }

        var undo = new PowerUndo(state => state.ActivePlan == power.ActivePlan &&
                state.Timeout(setting.Action, setting.OnBattery) == option.Seconds,
            () => WindowsPower.SetIdleTimeout(power.ActivePlan, setting.Action, setting.OnBattery, option.Seconds, setting.Seconds));
        await RefreshPowerAsync(() => WindowsPower.SetIdleTimeout(power.ActivePlan, setting.Action, setting.OnBattery, setting.Seconds, option.Seconds),
            undo, setting.Action == PowerIdleAction.Sleep ? "Tempo para suspender salvo." : "Tempo para apagar a tela salvo.");
    }

    private async void SaveAnimations_Click(object sender, RoutedEventArgs e)
    {
        if (_power is not { } previous) return;
        bool enabled = Animations.IsChecked == true;
        if (previous.Animations == enabled)
        {
            await RefreshPowerAsync();
            return;
        }

        var undo = new PowerUndo(state => state.Animations == enabled, () => WindowsPower.SetAnimations(previous.Animations, enabled));
        await RefreshPowerAsync(() => WindowsPower.SetAnimations(enabled, previous.Animations), undo,
            enabled ? "Animações ativadas." : "Animações desativadas.");
    }

    private async void UndoPower_Click(object sender, RoutedEventArgs e)
    {
        if (_undo is { } undo) await RefreshPowerAsync(undo.Restore, success: "Último ajuste desfeito.");
    }

    private async Task RefreshPowerAsync(Action? change = null, PowerUndo? undo = null, string success = "Ajustes carregados.")
    {
        if (App.Current.Options.Simulate)
        {
            SetPowerStatus("Os ajustes de energia e tela estão disponíveis no modo real.");
            return;
        }

        PowerForm.IsEnabled = GamePowerForm.IsEnabled = false;
        SetPowerStatus(change == null ? "Lendo os ajustes do Windows…" : "Aplicando o ajuste…");
        try
        {
            if (change != null)
            {
                await Task.Run(change);
                _undo = undo;
            }
            _power = await Task.Run(WindowsPower.Read);
            if (_undo is { } applied && !applied.IsApplied(_power)) _undo = null;
            UndoPowerButton.Visibility = UndoGamePowerButton.Visibility = _undo == null ? Visibility.Collapsed : Visibility.Visible;
            var active = _power.Plans.FirstOrDefault(plan => plan.Id == _power.ActivePlan);
            Plans.ItemsSource = GamePlans.ItemsSource = _power.Plans;
            Plans.SelectedItem = GamePlans.SelectedItem = active;
            ActivePlanLabel.Text = GameActivePlanLabel.Text = $"Ativo · {active?.Name ?? "Plano não identificado"}";
            IdleTimeouts.ItemsSource = _power.IdleTimeouts.Select(setting => new TimeoutEditor(setting)).ToArray();
            Animations.IsChecked = _power.Animations;
            PowerControls.Visibility = GamePlanControls.Visibility = Visibility.Visible;
            ReadPowerButton.Content = "Atualizar ajustes";
            ReadGamePowerButton.Content = "Atualizar planos";
            SetPowerStatus(success);
        }
        catch (Exception ex)
        {
            Log.Error("Falha nos ajustes do Windows", ex);
            _power = null;
            PowerControls.Visibility = GamePlanControls.Visibility = Visibility.Collapsed;
            UndoPowerButton.Visibility = UndoGamePowerButton.Visibility = Visibility.Collapsed;
            ReadPowerButton.Content = "Ler ajustes";
            ReadGamePowerButton.Content = "Ler planos";
            SetPowerStatus(ex.Message);
        }
        finally
        {
            PowerForm.IsEnabled = GamePowerForm.IsEnabled = true;
        }
    }

    private void SetPowerStatus(string status) => PowerStatus.Text = GamePowerStatus.Text = status;

    private void OpenGameAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) window.ShowFanAutomation();
    }

    private async void ListApps_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private async void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OptionalWindowsApp app } || App.Current.Options.Simulate) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Remover {app.Name} desta conta do Windows?",
                "Remover aplicativo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
            await LoadAppsAsync(app);
    }

    private async Task LoadAppsAsync(OptionalWindowsApp? remove = null)
    {
        if (App.Current.Options.Simulate)
        {
            AppsStatus.Text = "A lista de apps opcionais está disponível no modo real.";
            return;
        }

        AppsForm.IsEnabled = false;
        AppsStatus.Text = remove == null ? "Lendo os apps desta conta…" : $"Removendo {remove.Name}…";
        string? result = null;
        try
        {
            if (remove != null)
            {
                bool removed = await OptionalWindowsApps.RemoveAsync(remove);
                result = removed ? $"{remove.Name} removido desta conta." : $"{remove.Name} já está indisponível para remoção.";
            }
            var apps = await OptionalWindowsApps.ListAsync();
            AppsList.ItemsSource = apps;
            ListAppsButton.Content = "Atualizar lista";
            AppsStatus.Text = result ?? (apps.Count == 0 ? "Nenhum app opcional do catálogo está instalado." : $"{apps.Count} apps disponíveis para revisão.");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao gerenciar apps opcionais", ex);
            AppsList.ItemsSource = null;
            AppsStatus.Text = result == null
                ? $"{ex.Message} Liste os apps novamente para conferir."
                : $"{result} A lista não pôde ser recarregada. Liste os apps novamente para conferir.";
        }
        finally
        {
            AppsForm.IsEnabled = true;
        }
    }

    private async void RunDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DiagnosticQuery query } || !DiagnosticActions.IsEnabled) return;

        _diagnostic = null;
        DiagnosticReport.Visibility = Visibility.Visible;
        DiagnosticTitle.Text = query.Title;
        DiagnosticEntries.ItemsSource = null;
        DiagnosticWarning.Visibility = DiagnosticFeedback.Visibility = CopyDiagnosticButton.Visibility = Visibility.Collapsed;
        if (App.Current.Options.Simulate)
        {
            DiagnosticSummary.Text = "As consultas do Windows estão disponíveis no modo real.";
            return;
        }

        DiagnosticActions.IsEnabled = false;
        DiagnosticSummary.Text = "Consultando o Windows…";
        try
        {
            _diagnostic = await Task.Run(query.Read);
            DiagnosticSummary.Text = _diagnostic.Summary;
            DiagnosticEntries.ItemsSource = _diagnostic.Entries;
            DiagnosticWarning.Text = _diagnostic.Warning ?? string.Empty;
            DiagnosticWarning.Visibility = _diagnostic.Warning == null ? Visibility.Collapsed : Visibility.Visible;
            CopyDiagnosticButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error($"Falha na consulta {query.Title}", ex);
            DiagnosticSummary.Text = ex is UnauthorizedAccessException || ex.HResult == unchecked((int)0x80070005)
                ? "O Windows restringiu o acesso a esta fonte nesta conta."
                : "Não foi possível concluir a consulta. Tente novamente.";
        }
        finally
        {
            DiagnosticActions.IsEnabled = true;
        }
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnostic is not { } diagnostic) return;

        var report = new StringBuilder().AppendLine(DiagnosticTitle.Text).AppendLine(diagnostic.Summary);
        if (diagnostic.Warning is { } warning) report.AppendLine(warning);
        foreach (var entry in diagnostic.Entries)
        {
            report.AppendLine().AppendLine(entry.Title).AppendLine(entry.Value);
            if (!string.IsNullOrWhiteSpace(entry.Details)) report.AppendLine(entry.Details);
        }

        DiagnosticFeedback.Visibility = Visibility.Visible;
        try
        {
            Clipboard.SetText(report.ToString().TrimEnd());
            DiagnosticFeedback.Text = "Relatório copiado.";
        }
        catch (ExternalException ex)
        {
            Log.Warn($"Falha ao copiar o diagnóstico: {ex.Message}");
            DiagnosticFeedback.Text = "A área de transferência está indisponível. Tente novamente.";
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string uri }) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            Log.Error($"Falha ao abrir {uri}", ex);
            MessageBox.Show(Window.GetWindow(this), "Não foi possível abrir a página das Configurações do Windows.",
                "Configurações do Windows", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
