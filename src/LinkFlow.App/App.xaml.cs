using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;
using Microsoft.Win32;

namespace LinkFlow.App;

public partial class App : Application
{
    private IEngineClient? _engine;
    private MainViewModel? _vm;
    private SingleInstance? _instance;
    private TrayIconService? _tray;
    private int _exiting;
    private DateTime _lastTrayUpdate;

    public StartupOptions Options { get; private set; } = new();
    public MainWindow? MainView { get; private set; }
    public UiSettingsService Settings { get; private set; } = null!;
    public SettingsViewModel SettingsVm { get; private set; } = null!;
    public bool IsServiceMode => _engine?.IsRemote == true;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = StartupOptions.Parse(e.Args);

        if (Options.Simulate)
        {
            // Simulação nunca toca na configuração real
            AppPaths.SharedDir = Path.Combine(Path.GetTempPath(), "LinkFlow-sim");
            AppPaths.UserDir = Path.Combine(Path.GetTempPath(), "LinkFlow-sim", "user");
        }

        Log.Configure("app", Environment.GetEnvironmentVariable("LINKFLOW_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info);

        bool testRun = Options.ScreenshotPath != null;
        if (!testRun)
        {
            _instance = new SingleInstance(Options.Simulate ? "LinkFlow.App.Sim" : "LinkFlow.App");
            if (!_instance.IsFirstInstance)
            {
                if (Options.ExitRunning)
                {
                    Log.Info("--exit: pedindo encerramento da instância em execução.");
                    _instance.SignalExit();
                    _instance.WaitForFirstInstanceExit(TimeSpan.FromSeconds(15));
                }
                else
                {
                    Log.Info("LinkFlow já está aberto: trazendo a janela existente.");
                    _instance.SignalActivate();
                }
                _instance.Dispose();
                _instance = null;
                Shutdown();
                return;
            }
            if (Options.ExitRunning)
            {
                Shutdown(); // nada em execução para encerrar
                return;
            }
            _instance.Listen(
                onActivate: () => Dispatcher.BeginInvoke(ShowMainWindow),
                onExit: () => Dispatcher.BeginInvoke(ExitApplication));
        }

        Log.CleanupOldLogs();
        Log.Info($"LinkFlow iniciando (args: {string.Join(' ', e.Args)})");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Exceção não tratada", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Task não observada", ex.Exception); ex.SetObserved(); };

        Settings = new UiSettingsService();
        SettingsVm = new SettingsViewModel(Settings);

        _engine = CreateEngineClient();
        _engine.Start();

        _vm = new MainViewModel(_engine);
        _vm.AlertRaised += OnAlert;
        _vm.SnapshotApplied += OnSnapshotApplied;
        MainView = new MainWindow(_vm);

        if (testRun)
        {
            _ = RunScreenshotAsync(Options.ScreenshotPath!);
            return;
        }

        SetupTray();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (Options.StartInTray)
        {
            Log.Info("Iniciado na bandeja.");
            MainView.InitializeHidden();
        }
        else
        {
            ShowMainWindow();
        }
    }

    private IEngineClient CreateEngineClient()
    {
        IHardwareFactory factory = Options.Simulate ? new SimulatedHardwareFactory() : new RealHardwareFactory();
        var engine = new LinkFlowEngine(factory, new EngineOptions { ConfigPath = AppPaths.ControlConfigFile });
        return new LocalEngineClient(engine);
    }

    // ------------------------------------------------------------------ Bandeja

    private void SetupTray()
    {
        _tray = new TrayIconService();
        _tray.OpenRequested += ShowMainWindow;
        _tray.ExitRequested += ExitApplication;
        _tray.MiniWidgetToggled += () => MainView?.ToggleMiniWidget(fromTray: true);
        _tray.PresetRequested += key => _vm?.ApplyPreset(key);
        _tray.ProfileRequested += name => _vm?.ApplyProfileByName(name);
        _tray.LcdStreamToggled += on => { if (_vm != null) _vm.IsLcdAutoStream = on; };
        _tray.AutoStartToggled += on => SettingsVm.SetStartWithWindows(on);
        _tray.MenuOpening += RefreshTrayMenu;
        RefreshTrayMenu();
    }

    private void RefreshTrayMenu()
    {
        if (_tray == null || _vm == null || _engine == null) return;
        _tray.UpdateMenuState(_vm.ActivePreset, _engine.Config.Profiles, MainView?.IsMiniWidgetVisible == true,
            _vm.IsLcdAutoStream, SettingsVm.StartWithWindows);
    }

    private void OnSnapshotApplied()
    {
        if (_tray == null || _vm == null) return;
        if ((DateTime.Now - _lastTrayUpdate).TotalSeconds < 1.5) return;
        _lastTrayUpdate = DateTime.Now;
        _tray.UpdateStatus(_vm.Snapshot, Settings.Current.ShowTrayTemperature);
    }

    private void OnAlert(EngineAlert alert)
    {
        if (_tray == null || !Settings.Current.AlertNotifications) return;
        if (alert.Severity == AlertSeverity.Info) return;
        _tray.ShowNotification(alert.Title, alert.Message,
            alert.Severity == AlertSeverity.Critical ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Warning);
    }

    public void NotifyHiddenToTray()
    {
        if (_tray == null || Settings.Current.TrayHintShown) return;
        Settings.Update(s => s.TrayHintShown = true);
        _tray.ShowNotification("LinkFlow continua ativo",
            IsServiceMode
                ? "A janela foi fechada, mas o serviço continua controlando o hardware. Clique no ícone para abrir."
                : "O controle das ventoinhas e do RGB continua na bandeja. Clique no ícone para abrir; use \"Sair\" no menu para encerrar.");
    }

    public void ShowMainWindow() => MainView?.ShowFromTray();

    /// <summary>Confirma na bandeja um perfil aplicado por atalho global (útil dentro de jogos).</summary>
    public void NotifyPresetChanged(string preset)
    {
        if (MainView?.IsVisible == true && MainView.IsActive) return;
        _tray?.ShowNotification("Perfil aplicado", preset);
    }

    // ------------------------------------------------------------------ Energia e sessão

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            // Após suspender, os handles HID ficam inválidos e o hub volta ao modo hardware
            _engine?.NotifyResume();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info($"Sessão do Windows encerrando ({e.ReasonSessionEnding}).");
        ExitApplication();
        base.OnSessionEnding(e);
    }

    // ------------------------------------------------------------------ Encerramento

    /// <summary>Encerra o aplicativo de verdade (no modo local o hub volta ao modo hardware).</summary>
    public void ExitApplication()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        Log.Info("Encerrando LinkFlow.");
        try { MainView?.CloseForExit(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            Settings?.Flush();
            _tray?.Dispose();
            _vm?.Dispose();
            _engine?.ShutdownAsync(restoreHardwareMode: true).Wait(TimeSpan.FromSeconds(6));
            _instance?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao encerrar", ex);
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro na interface", e.Exception);
        e.Handled = true; // o controle das ventoinhas continua; não derruba o app por erro de UI
    }

    // ------------------------------------------------------------------ Teste visual

    private async Task RunScreenshotAsync(string path)
    {
        var w = MainView!;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = 0;
        w.Top = 0;
        if (Options.ScreenshotWidth is { } width) w.Width = width;
        if (Options.ScreenshotHeight is { } height) w.Height = height;
        w.Show();
        w.SelectPage(Options.ScreenshotPage);

        await Task.Delay(3500);
        w.UpdateLayout();

        var root = (FrameworkElement)w.Content;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using (var fs = File.Create(path)) encoder.Save(fs);

        Log.Info($"Screenshot salvo em {path}");
        ExitApplication();
    }
}
