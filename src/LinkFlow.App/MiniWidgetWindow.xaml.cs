using System.Windows;
using System.Windows.Input;

namespace LinkFlow.App;

public partial class MiniWidgetWindow : Window
{
    private readonly Window? _mainWindow;

    public MiniWidgetWindow(Window? mainWindow = null)
    {
        InitializeComponent();
        _mainWindow = mainWindow;

        // Position at top right by default
        Left = SystemParameters.WorkArea.Right - Width - 30;
        Top = SystemParameters.WorkArea.Top + 40;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void CloseWidget_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow != null)
        {
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Show();
        }
        Hide();
    }
}
