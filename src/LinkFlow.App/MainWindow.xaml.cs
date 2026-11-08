using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App;

public partial class MainWindow : Window
{
    private MiniWidgetWindow? _miniWidget;
    private bool _closingForExit;
    private bool _boundsRestored;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        RestoreSavedBounds();
        StateChanged += OnStateChanged;
        IsVisibleChanged += (_, _) => UpdateViewActive();
    }

    public MainViewModel ViewModel { get; }
    public SettingsViewModel Settings => App.Current.SettingsVm;
    public bool IsMiniWidgetVisible => _miniWidget?.IsVisible == true;

    // ------------------------------------------------------------------ Janela / bandeja

    /// <summary>Inicia na bandeja: cria o handle (para atalhos globais) sem mostrar a janela.</summary>
    public void InitializeHidden()
    {
        new WindowInteropHelper(this).EnsureHandle();
        UpdateViewActive();
        if (App.Current.Settings.Current.MiniVisible) ShowMiniWidget();
    }

    public void ShowFromTray()
    {
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
        SaveBounds();
        Hide();
        App.Current.NotifyHiddenToTray();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
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
        ViewModel.IsViewActive = (IsVisible && WindowState != WindowState.Minimized) || IsMiniWidgetVisible;
    }

    public void SelectPage(int index)
    {
        if (index >= 0 && index < MainTabs.Items.Count) MainTabs.SelectedIndex = index;
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        SaveBounds();
        _miniWidget?.CloseForExit();
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
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
        if (IsMiniWidgetVisible)
        {
            _miniWidget!.Hide();
            App.Current.Settings.Update(s => s.MiniVisible = false);
        }
        else
        {
            ShowMiniWidget();
            if (!fromTray && IsVisible) HideToTray();
        }
        UpdateViewActive();
    }

    private void ShowMiniWidget()
    {
        if (_miniWidget == null)
        {
            _miniWidget = new MiniWidgetWindow { DataContext = ViewModel };
            _miniWidget.OpenMainRequested += ShowFromTray;
            _miniWidget.IsVisibleChanged += (_, _) => UpdateViewActive();
        }
        _miniWidget.Show();
        App.Current.Settings.Update(s => s.MiniVisible = true);
        UpdateViewActive();
    }

    private void ToggleMiniWidget_Click(object sender, RoutedEventArgs e) => ToggleMiniWidget();

    // ------------------------------------------------------------------ Ações

    private void PresetSilent_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Silent);
    private void PresetBalanced_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Balanced);
    private void PresetPerformance_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Performance);
    private void PresetMax_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Max);
    private void PresetAuto_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Auto);

    private void ColorCyan_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(0, 240, 255);
    private void ColorPurple_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(168, 85, 247);
    private void ColorGreen_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(16, 185, 129);
    private void ColorRed_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(239, 68, 68);
    private void ColorWhite_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(255, 255, 255);
    private void ColorOrange_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(249, 115, 22);
    private void ColorYellow_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(250, 204, 21);
    private void ColorOff_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(0, 0, 0);

    /// <summary>As cores já são aplicadas em tempo real; o botão força reconexão e reenvio a todos os dispositivos.</summary>
    private void ApplyRgbSync_Click(object sender, RoutedEventArgs e) => ViewModel.Engine.NotifyResume();

    private void SyncLcd_Click(object sender, RoutedEventArgs e) => ViewModel.TransmitLcdImmediately();

    private void ExitApp_Click(object sender, RoutedEventArgs e) => App.Current.ExitApplication();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Core.AppPaths.LogsDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Core.AppPaths.LogsDir) { UseShellExecute = true });
        }
        catch { }
    }
}
