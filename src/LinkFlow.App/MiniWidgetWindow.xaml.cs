using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace LinkFlow.App;

public partial class MiniWidgetWindow : Window
{
    private bool _closingForExit;

    public MiniWidgetWindow()
    {
        InitializeComponent();

        var s = App.Current.Settings.Current;
        var work = SystemParameters.WorkArea;
        Left = s.MiniLeft ?? work.Right - Width - 30;
        Top = s.MiniTop ?? work.Top + 40;

        // Garante que fique visível se o monitor mudou
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(Left + 20, Top + 10, 60, 30)))
        {
            Left = work.Right - Width - 30;
            Top = work.Top + 40;
        }
    }

    public event Action? OpenMainRequested;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenMainRequested?.Invoke();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
            App.Current.Settings.Update(s =>
            {
                s.MiniLeft = Left;
                s.MiniTop = Top;
            });
        }
    }

    private void CloseWidget_Click(object sender, RoutedEventArgs e)
    {
        OpenMainRequested?.Invoke();
        Hide();
        App.Current.Settings.Update(s => s.MiniVisible = false);
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();

    private void HideWidget_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Current.Settings.Update(s => s.MiniVisible = false);
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_closingForExit)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
