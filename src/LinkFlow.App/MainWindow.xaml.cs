using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App;

public partial class MainWindow : Window
{
    private MiniWidgetWindow? _miniWidget;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch { }
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Dispose();
        _miniWidget?.Close();
        Application.Current.Shutdown();
    }

    private void ToggleMiniWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_miniWidget == null)
        {
            _miniWidget = new MiniWidgetWindow(this)
            {
                DataContext = ViewModel
            };
        }

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

    // Fan Presets
    private void PresetSilent_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset("Silencioso");
    private void PresetBalanced_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset("Equilibrado");
    private void PresetPerformance_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset("Desempenho");
    private void PresetMax_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset("Máximo");
    private void PresetAuto_Click(object sender, RoutedEventArgs e) => ViewModel.ApplyPreset("Automático");

    private void IndividualSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider slider && slider.Tag is FanViewModel fan)
        {
            fan.IsSyncedWithGlobal = false;
            ViewModel.ApplyIndividualSpeed(fan, (byte)e.NewValue);
        }
    }

    // RGB Preset Handlers
    private void ColorCyan_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(0, 240, 255);
    private void ColorPurple_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(168, 85, 247);
    private void ColorGreen_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(16, 185, 129);
    private void ColorRed_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(239, 68, 68);
    private void ColorWhite_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(255, 255, 255);
    private void ColorOrange_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(249, 115, 22);
    private void ColorYellow_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(250, 204, 21);
    private void ColorOff_Click(object sender, RoutedEventArgs e) => ViewModel.SetColorPreset(0, 0, 0);

    private void ApplyRgbSync_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ApplyRgbSync();
    }

    private void SyncLcd_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.TransmitLcdImmediately();
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Dispose();
        _miniWidget?.Close();
        base.OnClosed(e);
    }
}
