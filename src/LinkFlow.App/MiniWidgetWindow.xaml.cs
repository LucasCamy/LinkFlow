using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;
using Screen = System.Windows.Forms.Screen;

namespace LinkFlow.App;

/// <summary>
/// Widget flutuante que acompanha o foco e a tela cheia sem ativar a própria janela.
/// </summary>
public partial class MiniWidgetWindow : Window
{
    private const int GwlExStyle = -20, WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const uint EventSystemForeground = 0x0003, GwHwndPrev = 3;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoActivate = 0x0010, SwpNoOwnerZOrder = 0x0200;
    private const uint DwmwaExtendedFrameBounds = 9, DwmwaCloaked = 14;
    private static readonly nint HwndTopmost = new(-1);
    private const double SnapDistance = 24;

    private readonly WidgetViewModel _vm;
    private readonly DispatcherTimer _desktopTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private readonly WinEventProc _foregroundChanged;
    private nint _hwnd, _foregroundHook, _lastForeground;
    private nint _fullscreenWindow;
    private string? _fullscreenMonitor;
    private DispatcherOperation? _foregroundCheck;
    private bool _closingForExit;
    private bool _dragging;

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

        _foregroundChanged = OnForegroundChanged;
        _desktopTimer.Tick += (_, _) => RefreshWindow();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            UpdateWindowStyle();
        };
        ContextMenu.Closed += (_, _) => RefreshWindow();
        vm.BehaviorChanged += ApplyBehavior;
        // Ao crescer/encolher (itens, escala), mantém o widget dentro da tela
        SizeChanged += (_, _) => KeepOnScreen();
    }

    public event Action? OpenMainRequested;
    public event Action? ConfigureRequested;

    public void SetWidgetEnabled(bool enabled)
    {
        _vm.IsWidgetEnabled = enabled;
        if (App.Current.Settings.Current.MiniVisible != enabled)
            App.Current.Settings.Update(s => s.MiniVisible = enabled);
        ApplyBehavior();
    }

    private void ApplyBehavior()
    {
        if (_closingForExit) return;
        if (_vm.IsWidgetEnabled && _hwnd == 0) new WindowInteropHelper(this).EnsureHandle();
        UpdateWindowStyle();
        if (_vm.IsWidgetEnabled && (_vm.AlwaysOnTop || _vm.HideInFullscreen))
        {
            if (_foregroundHook == 0)
                _foregroundHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, 0, _foregroundChanged, 0, 0, 0);
            _desktopTimer.Start();
        }
        else StopTrackingDesktop();
        RefreshWindow();
    }

    private void UpdateWindowStyle()
    {
        if (_hwnd == 0) return;
        int current = GetWindowLong(_hwnd, GwlExStyle);
        int style = current | WsExToolWindow | WsExNoActivate;
        style = _vm.ClickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        if (style != current) SetWindowLong(_hwnd, GwlExStyle, style);
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (hook != _foregroundHook || _foregroundCheck?.Status == DispatcherOperationStatus.Pending) return;
        _foregroundCheck = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RefreshWindow());
    }

    private void StopTrackingDesktop()
    {
        _desktopTimer.Stop();
        _foregroundCheck?.Abort();
        _foregroundCheck = null;
        if (_foregroundHook == 0) return;
        UnhookWinEvent(_foregroundHook);
        _foregroundHook = 0;
    }

    private void RefreshWindow()
    {
        if (_closingForExit || _hwnd == 0) return;
        if (!_vm.IsWidgetEnabled)
        {
            _fullscreenWindow = 0;
            _lastForeground = 0;
            if (IsVisible) Hide();
            return;
        }
        if (_dragging || ContextMenu?.IsOpen == true) return;

        var foreground = GetForegroundWindow();
        bool changed = foreground != 0 && foreground != _lastForeground;
        if (foreground != 0) _lastForeground = foreground;
        else foreground = _lastForeground;
        if (ShouldHideInFullscreen(foreground))
        {
            if (IsVisible) Hide();
            return;
        }

        bool showing = !IsVisible;
        if (showing) Show();
        if (_vm.AlwaysOnTop && (showing || changed ||
            (GetWindowLong(_hwnd, GwlExStyle) & WsExTopmost) == 0 || ForegroundCoversWidget(foreground)))
            SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private bool ShouldHideInFullscreen(nint foreground)
    {
        if (!_vm.HideInFullscreen)
        {
            _fullscreenWindow = 0;
            return false;
        }

        var monitor = Screen.FromHandle(_hwnd);
        if (_fullscreenMonitor != monitor.DeviceName)
        {
            _fullscreenMonitor = monitor.DeviceName;
            _fullscreenWindow = 0;
        }
        // Ao mudar o foco para outro monitor, a tela cheia pode continuar no monitor do widget.
        if (IsFullscreenOnMonitor(foreground, monitor.Bounds)) _fullscreenWindow = foreground;
        else if (foreground == _fullscreenWindow ||
                 (foreground != 0 && Screen.FromHandle(foreground).DeviceName == monitor.DeviceName) ||
                 !IsFullscreenOnMonitor(_fullscreenWindow, monitor.Bounds)) _fullscreenWindow = 0;
        return _fullscreenWindow != 0;
    }

    private bool IsFullscreenOnMonitor(nint window, Rectangle monitorBounds)
    {
        if (window == 0 || window == _hwnd || window == GetShellWindow() ||
            !IsWindowVisible(window) || IsIconic(window) || IsZoomed(window)) return false;

        if (DwmGetWindowAttribute(window, DwmwaExtendedFrameBounds, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(window, out bounds)) return false;
        return CoversMonitor(bounds.ToRectangle(), monitorBounds) &&
               (DwmGetWindowAttribute(window, DwmwaCloaked, out int cloaked, sizeof(int)) != 0 || cloaked == 0);
    }

    internal static bool CoversMonitor(Rectangle window, Rectangle monitor)
    {
        const int tolerance = 1;
        return window.Left <= monitor.Left + tolerance && window.Top <= monitor.Top + tolerance &&
               window.Right >= monitor.Right - tolerance && window.Bottom >= monitor.Bottom - tolerance;
    }

    private bool ForegroundCoversWidget(nint foreground)
    {
        if (foreground == 0 || foreground == _hwnd || (GetWindowLong(foreground, GwlExStyle) & WsExTopmost) == 0 ||
            !GetWindowRect(foreground, out var activeBounds) || !GetWindowRect(_hwnd, out var widgetBounds) ||
            !activeBounds.ToRectangle().IntersectsWith(widgetBounds.ToRectangle())) return false;

        // Só reafirma o topo se a janela ativa realmente estiver à frente do widget.
        var above = _hwnd;
        for (int i = 0; i < 64; i++)
        {
            above = GetWindow(above, GwHwndPrev);
            if (above == foreground) return true;
            if (above == 0) break;
        }
        return false;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OpenMainRequested?.Invoke();
            return;
        }
        if (_vm.LockPosition || e.ButtonState != MouseButtonState.Pressed) return;
        _dragging = true;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally { _dragging = false; }
        if (_vm.SnapToEdges) Snap();
        App.Current.Settings.Update(s =>
        {
            s.MiniLeft = Left;
            s.MiniTop = Top;
        });
        RefreshWindow();
    }

    private Rect CurrentWorkArea()
    {
        if (_hwnd == 0) return SystemParameters.WorkArea;
        var screen = Screen.FromHandle(_hwnd);
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
        if (Left < a.Left || Left + ActualWidth > a.Right) Left = Math.Clamp(Left, a.Left, Math.Max(a.Left, a.Right - ActualWidth - 8));
        if (Top < a.Top || Top + ActualHeight > a.Bottom) Top = Math.Clamp(Top, a.Top, Math.Max(a.Top, a.Bottom - ActualHeight - 8));
    }

    /// <summary>Move para um canto da tela atual (botões da aba Widget).</summary>
    public void MoveToCorner(string corner)
    {
        var a = CurrentWorkArea();
        const double m = 12;
        Left = corner.Contains("Left") ? a.Left + m : corner.Contains("Center") ? a.Left + (a.Width - ActualWidth) / 2 : a.Right - ActualWidth - m;
        Top = corner.Contains("Top") ? a.Top + m : a.Bottom - ActualHeight - m;
        App.Current.Settings.Update(s => { s.MiniLeft = Left; s.MiniTop = Top; });
        RefreshWindow();
    }

    private void OpenMain_Click(object sender, RoutedEventArgs e) => OpenMainRequested?.Invoke();
    private void Configure_Click(object sender, RoutedEventArgs e) => ConfigureRequested?.Invoke();

    private void HideWidget_Click(object sender, RoutedEventArgs e) => SetWidgetEnabled(false);

    public void CloseForExit()
    {
        _closingForExit = true;
        StopTrackingDesktop();
        _vm.BehaviorChanged -= ApplyBehavior;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_closingForExit)
        {
            e.Cancel = true;
            SetWidgetEnabled(false);
        }
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out NativeRect rect, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
}
