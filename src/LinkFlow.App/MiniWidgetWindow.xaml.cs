using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App;

/// <summary>
/// Widget flutuante. Leve: só atualiza com a janela visível; a checagem de tela cheia roda a cada 2 s
/// apenas se a opção estiver ligada.
/// </summary>
public partial class MiniWidgetWindow : Window
{
    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExLayered = 0x80000, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const double SnapDistance = 24;

    private readonly WidgetViewModel _vm;
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closingForExit;
    private bool _hiddenForFullscreen;

    public MiniWidgetWindow(WidgetViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        var s = App.Current.Settings.Current;
        var work = SystemParameters.WorkArea;
        Left = s.MiniLeft ?? work.Right - 420;
        Top = s.MiniTop ?? work.Top + 40;
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!vs.IntersectsWith(new Rect(Left + 20, Top + 10, 60, 30)))
        {
            Left = work.Right - 420;
            Top = work.Top + 40;
        }

        _fullscreenTimer.Tick += (_, _) => CheckFullscreen();
        SourceInitialized += (_, _) => ApplyBehavior();
        vm.BehaviorChanged += ApplyBehavior;
        IsVisibleChanged += (_, _) => { vm.IsWidgetVisible = IsVisible || _hiddenForFullscreen; ApplyBehavior(); };
        // Ao crescer/encolher (itens, escala), mantém o widget dentro da tela
        SizeChanged += (_, _) => KeepOnScreen();
    }

    public event Action? OpenMainRequested;
    public event Action? ConfigureRequested;

    private void ApplyBehavior()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            int ex = GetWindowLong(hwnd, GwlExStyle) | WsExToolWindow | WsExNoActivate | WsExLayered;
            ex = _vm.ClickThrough ? ex | WsExTransparent : ex & ~WsExTransparent;
            SetWindowLong(hwnd, GwlExStyle, ex);
        }
        if (_vm.HideInFullscreen && (IsVisible || _hiddenForFullscreen)) _fullscreenTimer.Start();
        else
        {
            _fullscreenTimer.Stop();
            if (_hiddenForFullscreen) { _hiddenForFullscreen = false; Show(); }
        }
    }

    /// <summary>Jogo/vídeo em tela cheia ou apresentação: esconde e volta sozinho depois.</summary>
    private void CheckFullscreen()
    {
        if (SHQueryUserNotificationState(out int state) != 0) return;
        bool busy = state is 2 or 3 or 4; // QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
        if (busy && IsVisible)
        {
            _hiddenForFullscreen = true;
            Hide();
        }
        else if (!busy && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Show();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenMainRequested?.Invoke();
            return;
        }
        if (_vm.LockPosition || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { }
        if (_vm.SnapToEdges) Snap();
        App.Current.Settings.Update(s =>
        {
            s.MiniLeft = Left;
            s.MiniTop = Top;
        });
    }

    private Rect CurrentWorkArea()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left + ActualWidth / 2), (int)(Top + ActualHeight / 2)));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var w = screen.WorkingArea;
        return new Rect(w.Left / dpi.DpiScaleX, w.Top / dpi.DpiScaleY, w.Width / dpi.DpiScaleX, w.Height / dpi.DpiScaleY);
    }

    /// <summary>Gruda nas bordas/cantos da área de trabalho quando solto perto delas.</summary>
    private void Snap()
    {
        var a = CurrentWorkArea();
        const double m = 8;
        if (Math.Abs(Left - a.Left) < SnapDistance) Left = a.Left + m;
        if (Math.Abs(a.Right - (Left + ActualWidth)) < SnapDistance) Left = a.Right - ActualWidth - m;
        if (Math.Abs(Top - a.Top) < SnapDistance) Top = a.Top + m;
        if (Math.Abs(a.Bottom - (Top + ActualHeight)) < SnapDistance) Top = a.Bottom - ActualHeight - m;
    }

    private void KeepOnScreen()
    {
        if (!IsLoaded) return;
        var a = CurrentWorkArea();
        if (Left + ActualWidth > a.Right) Left = Math.Max(a.Left, a.Right - ActualWidth - 8);
        if (Top + ActualHeight > a.Bottom) Top = Math.Max(a.Top, a.Bottom - ActualHeight - 8);
    }

    /// <summary>Move para um canto da tela atual (botões da aba Widget).</summary>
    public void MoveToCorner(string corner)
    {
        var a = CurrentWorkArea();
        const double m = 12;
        Left = corner.Contains("Left") ? a.Left + m : corner.Contains("Center") ? a.Left + (a.Width - ActualWidth) / 2 : a.Right - ActualWidth - m;
        Top = corner.Contains("Top") ? a.Top + m : a.Bottom - ActualHeight - m;
        App.Current.Settings.Update(s => { s.MiniLeft = Left; s.MiniTop = Top; });
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();
    private void Configure_Click(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke();

    private void HideWidget_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Current.Settings.Update(s => s.MiniVisible = false);
    }

    public void CloseForExit()
    {
        _closingForExit = true;
        _fullscreenTimer.Stop();
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

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}
