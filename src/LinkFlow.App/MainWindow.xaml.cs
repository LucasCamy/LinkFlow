using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App;

public partial class MainWindow : Window
{
    private const double CompactWidth = 980;

    private MiniWidgetWindow? _miniWidget;
    private bool _closingForExit;
    private bool _boundsRestored;
    private HotkeyService? _hotkeys;
    private readonly LcdPreviewService _lcdPreview;
    private readonly WidgetGameOverlay? _gameOverlay;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        RestoreSavedBounds();

        ViewModel.SelectedPage = Math.Clamp(App.Current.Settings.Current.SelectedPage, 0, MainViewModel.PageTitles.Length - 1);
        _lcdPreview = new LcdPreviewService(viewModel, Dispatcher);
        // Widget flutuante: configurações (migra as opções antigas) e atualização só quando visível
        var ui = App.Current.Settings.Current;
        ui.Widget ??= WidgetSettings.MigrateFrom(ui);
        Widget = new WidgetViewModel(ui.Widget, () => App.Current.Settings.Update(_ => { }), viewModel);
        if (App.Current.Options.ScreenshotPath == null && !viewModel.Engine.IsSimulated)
            _gameOverlay = new WidgetGameOverlay(Widget, () => ViewModel.Snapshot, () => ViewModel.Engine.IsConnected, Dispatcher);
        ViewModel.SnapshotApplied += () =>
        {
            bool page = ViewModel.SelectedPage == MainViewModel.WidgetPageIndex && IsVisible;
            if (IsMiniWidgetVisible || page) Widget.Update(ViewModel.Snapshot);
            if (page) Widget.RefreshPicker();
        };
        ViewModel.SetWidgetDemandSource(() => IsMiniWidgetVisible || Widget.IsGameOverlayPublishing ? Widget.Demand : Core.SensorDemand.None);
        ViewModel.PageChanged += p =>
        {
            App.Current.Settings.Update(s => s.SelectedPage = p);
            if (p == 5) Settings.RefreshServiceState();
            if (p == 2) ViewModel.RefreshLightingDiagnostics();
            UpdateViewActive();
            // Transição: a página que acabou de aparecer entra com fade + deslize
            if (p >= 0 && p < PageHost.Children.Count) Motion.PageIn(PageHost.Children[p]);
        };
        Toast.IsVisibleChanged += (_, e) => { if (e.NewValue is true) Motion.ToastIn(Toast); };
        Motion.UserEnabled = App.Current.Settings.Current.Animations;
        if (ViewModel.SelectedPage == 2) ViewModel.RefreshLightingDiagnostics();
        Activated += (_, _) =>
        {
            if (ViewModel.SelectedPage == 2) ViewModel.RefreshLightingDiagnostics();
        };

        StateChanged += OnStateChanged;
        SizeChanged += (_, e) => ViewModel.IsCompact = e.NewSize.Width < CompactWidth;
        IsVisibleChanged += (_, _) => UpdateViewActive();
        SourceInitialized += (_, _) => SetupHotkeys();
        PreviewKeyDown += OnPreviewKeyDown;
        App.Current.Settings.Changed += () => _hotkeys?.SetEnabled(App.Current.Settings.Current.GlobalHotkeys);
    }

    public MainViewModel ViewModel { get; }
    public SettingsViewModel Settings => App.Current.SettingsVm;
    public bool IsMiniWidgetVisible => _miniWidget?.IsVisible == true;
    public WidgetViewModel Widget { get; }
    public string ShortcutHint => $"F1–F5 perfis  •  Ctrl+1–9 páginas  •  LinkFlow {typeof(App).Assembly.GetName().Version!.ToString(3)}";

    // ------------------------------------------------------------------ Atalhos

    private void SetupHotkeys()
    {
        if (App.Current.Options.ScreenshotPath != null) return;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _hotkeys = new HotkeyService(hwnd);
        _hotkeys.PresetHotkey += index =>
        {
            ViewModel.ApplyPresetCommand.Execute(index.ToString());
            App.Current.NotifyPresetChanged(ViewModel.ActivePreset);
        };
        _hotkeys.ShowHotkey += () =>
        {
            if (IsVisible && IsActive) HideToTray();
            else ShowFromTray();
        };
        _hotkeys.MiniHotkey += () => ToggleMiniWidget(fromTray: true);
        _hotkeys.SetEnabled(App.Current.Settings.Current.GlobalHotkeys);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        Core.Log.Debug($"Tecla {e.Key} ({Keyboard.Modifiers}) foco={Keyboard.FocusedElement?.GetType().Name ?? "nenhum"}");
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        bool inText = Keyboard.FocusedElement is TextBox;

        if (ctrl && e.Key == Key.M)
        {
            ToggleMiniWidget();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.R && !inText && ViewModel.SelectedPage == 1)
        {
            ShowFanDevices();
            if (!ViewModel.BeginRenameSelected())
                ViewModel.ShowToast("Selecione um cartão para renomear (ou use o lápis ao passar o mouse)", canUndo: false);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.Q)
        {
            App.Current.ExitApplication();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.W)
        {
            Close();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.A && !inText && ViewModel.SelectedPage == 1)
        {
            ShowFanDevices();
            ViewModel.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !inText)
        {
            // Esc: primeiro fecha detalhes/aviso/limpa seleção; depois esconde na bandeja
            if (ViewModel.CloseDetail()) { }
            else if (ViewModel.ToastVisible) ViewModel.ToastVisible = false;
            else if (ViewModel.SelectedPage == 1 && ViewModel.HasSelection) ViewModel.ClearSelectionCommand.Execute(null);
            else Close();
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ Janela / bandeja

    /// <summary>Inicia na bandeja: cria o handle (atalhos globais) sem mostrar a janela.</summary>
    public void InitializeHidden()
    {
        new WindowInteropHelper(this).EnsureHandle();
        UpdateViewActive();
    }

    public void ShowFromTray()
    {
        Core.Log.Debug($"ShowFromTray (visível={IsVisible}, estado={WindowState})");
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized)
            WindowState = App.Current.Settings.Current.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        Activate();
        Topmost = true;  // garante que venha para frente
        Topmost = false;
        Focus();
    }

    public void HideToTray()
    {
        Core.Log.Debug("HideToTray");
        SaveBounds();
        Hide();
        App.Current.NotifyHiddenToTray();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restaurar" : "Maximizar";

        if (WindowState == WindowState.Minimized && App.Current.Settings.Current.MinimizeToTray && !_closingForExit)
        {
            HideToTray();
        }
        else if (WindowState != WindowState.Minimized)
        {
            App.Current.Settings.Update(s => s.WindowMaximized = WindowState == WindowState.Maximized);
        }
        UpdateViewActive();
    }

    private void UpdateViewActive()
    {
        bool windowVisible = IsVisible && WindowState != WindowState.Minimized;
        ViewModel.IsViewActive = windowVisible || IsMiniWidgetVisible;
        // Na bandeja: depois de 4 s sem janela, devolve a memória da interface ao Windows
        ViewModel.UpdateSensorDemand();
        if (windowVisible || IsMiniWidgetVisible) MemoryTrimmer.Cancel();
        else MemoryTrimmer.ScheduleTrim(TimeSpan.FromSeconds(4));
        // A prévia do LCD só renderiza com a página do LCD aberta
        _lcdPreview.Active = windowVisible && ViewModel.SelectedPage == 3;
    }

    /// <summary>Modo de captura: renderiza a prévia mesmo com a janela fora da tela.</summary>
    public void ForceLcdPreview()
    {
        _lcdPreview.Active = true;
        _lcdPreview.RenderNow();
    }

    public void SelectPage(int index) => ViewModel.SelectedPage = index;

    internal void ShowFanDevices() => FansView.ShowDevices();

    internal void ShowFanAutomation()
    {
        SelectPage(1);
        FansView.ShowAutomation();
    }

    private void OpenAlertHistory_Click(object sender, RoutedEventArgs e) => DashboardView.ShowAlertHistory();

    public void CloseForExit()
    {
        _closingForExit = true;
        SaveBounds();
        _hotkeys?.Dispose();
        _lcdPreview.Dispose();
        _gameOverlay?.Dispose();
        _miniWidget?.CloseForExit();
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        Core.Log.Debug($"OnClosing (saindo={_closingForExit})");
        base.OnClosing(e);
        if (_closingForExit) return;

        e.Cancel = true;
        if (App.Current.Settings.Current.CloseToTray && App.Current.Options.ScreenshotPath == null)
        {
            HideToTray();
        }
        else
        {
            Dispatcher.BeginInvoke(() => App.Current.ExitApplication());
        }
    }

    private void RestoreSavedBounds()
    {
        var s = App.Current.Settings.Current;
        if (s.WindowWidth is { } w && s.WindowHeight is { } h && s.WindowLeft is { } l && s.WindowTop is { } t)
        {
            // Só restaura se a posição ainda estiver dentro da área visível (monitor pode ter sido removido)
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var r = new Rect(l, t, Math.Max(MinWidth, w), Math.Max(MinHeight, h));
            if (vs.IntersectsWith(new Rect(r.Left + 40, r.Top + 10, 120, 40)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = r.Left;
                Top = r.Top;
                Width = r.Width;
                Height = r.Height;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        _boundsRestored = true;
    }

    private void SaveBounds()
    {
        if (!_boundsRestored || App.Current.Options.ScreenshotPath != null) return;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (b.IsEmpty || b.Width < 100) return;
        App.Current.Settings.Update(s =>
        {
            s.WindowLeft = b.Left;
            s.WindowTop = b.Top;
            s.WindowWidth = b.Width;
            s.WindowHeight = b.Height;
            s.WindowMaximized = WindowState == WindowState.Maximized;
        });
    }

    // ------------------------------------------------------------------ Mini widget

    public void ToggleMiniWidget(bool fromTray = false)
    {
        if (Widget.IsWidgetEnabled)
        {
            SetWidgetEnabled(false);
        }
        else
        {
            SetWidgetEnabled(true);
            if (!fromTray && IsVisible) HideToTray();
        }
    }

    private void ToggleMiniWidget_Click(object sender, RoutedEventArgs e) => ToggleMiniWidget();

    /// <summary>Aba Widget: liga/desliga sem esconder a janela principal.</summary>
    public void SetWidgetEnabled(bool enabled)
    {
        if (enabled && _miniWidget == null)
        {
            _miniWidget = new MiniWidgetWindow(Widget);
            _miniWidget.ConfigureRequested += () => { ShowFromTray(); SelectPage(MainViewModel.WidgetPageIndex); };
            _miniWidget.OpenMainRequested += ShowFromTray;
            _miniWidget.IsVisibleChanged += (_, _) => UpdateViewActive();
        }
        _miniWidget?.SetWidgetEnabled(enabled);
        UpdateViewActive();
    }

    public void MoveWidgetToCorner(string corner)
    {
        if (!Widget.IsWidgetEnabled) SetWidgetEnabled(true);
        _miniWidget!.UpdateLayout();
        _miniWidget.MoveToCorner(corner);
    }
}
