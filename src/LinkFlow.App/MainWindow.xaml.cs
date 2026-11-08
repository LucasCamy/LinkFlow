using System.ComponentModel;
using System.Windows;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App;

public partial class MainWindow : Window
{
    private MiniWidgetWindow? _miniWidget;
    private bool _closingForExit;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }

    public MainViewModel ViewModel { get; }

    public void SelectPage(int index)
    {
        if (index >= 0 && index < MainTabs.Items.Count) MainTabs.SelectedIndex = index;
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        _miniWidget?.Close();
        Close();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => App.Current.ExitApplication();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_closingForExit)
        {
            // Alt+F4 / fechar pela barra de tarefas: encerra de forma controlada
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => App.Current.ExitApplication());
        }
    }

    public void ToggleMiniWidget()
    {
        _miniWidget ??= new MiniWidgetWindow(this) { DataContext = ViewModel };
        if (_miniWidget.IsVisible)
        {
            _miniWidget.Hide();
        }
        else
        {
            _miniWidget.Show();
            WindowState = WindowState.Minimized;
        }
    }

    private void ToggleMiniWidget_Click(object sender, RoutedEventArgs e) => ToggleMiniWidget();

    // Perfis
    private void PresetSilent_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Silent);
    private void PresetBalanced_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Balanced);
    private void PresetPerformance_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Performance);
    private void PresetMax_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Max);
    private void PresetAuto_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset(Core.FanPresets.Auto);

    // Cores rápidas
    private void ColorCyan_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(0, 240, 255);
    private void ColorPurple_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(168, 85, 247);
    private void ColorGreen_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(16, 185, 129);
    private void ColorRed_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(239, 68, 68);
    private void ColorWhite_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(255, 255, 255);
    private void ColorOrange_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(249, 115, 22);
    private void ColorYellow_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(250, 204, 21);
    private void ColorOff_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(0, 0, 0);

    /// <summary>As cores já são aplicadas em tempo real; o botão força reenvio a todos os dispositivos.</summary>
    private void ApplyRgbSync_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Engine.NotifyResume();
    }

    private void SyncLcd_Click(object sender, RoutedEventArgs e) => ViewModel.TransmitLcdImmediately();
}
