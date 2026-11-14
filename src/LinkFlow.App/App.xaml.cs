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
    private bool _handoffToService;
    private DispatcherTimer? _serviceWatch;
    private DateTime _lastTrayUpdate;

    public StartupOptions Options { get; private set; } = new();
    public MainWindow? MainView { get; private set; }
    public UiSettingsService Settings { get; private set; } = null!;
    public SettingsViewModel SettingsVm { get; private set; } = null!;
    public AppUpdateViewModel Updates { get; private set; } = null!;
    public bool IsServiceMode => _engine?.IsRemote == true;
    private PeripheralRgbBridge? _peripheralBridge;
    private AutomationService? _automation;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Options = StartupOptions.Parse(e.Args);

        if (Options.Simulate && Environment.GetEnvironmentVariable("LINKFLOW_DATA_DIR") == null)
        {
            // Simulação nunca toca na configuração real
            AppPaths.SharedDir = Path.Combine(Path.GetTempPath(), "LinkFlow-sim");
            AppPaths.UserDir = Path.Combine(Path.GetTempPath(), "LinkFlow-sim", "user");
        }

        Log.Configure(Options.ServiceCommand != null || Options.InstallDriverRequested ? "app-admin" : "app",
            Environment.GetEnvironmentVariable("LINKFLOW_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info);
        // Como administrador o app grava configuração e logs na pasta compartilhada: protege antes
        // (inclusive ao instalar o serviço, antes de ele existir). Sem privilégio, não faz nada.
        if (!Options.Simulate && Environment.GetEnvironmentVariable("LINKFLOW_DATA_DIR") == null)
            SharedDataSecurity.TryHarden(AppPaths.SharedDir);

        if (Options.WaitPid is { } waitPid) WaitForProcessExit(waitPid);

        if (Options.InstallDriverRequested)
        {
            int code = !Options.Simulate && Options.DriverToInstall is { } driver
                ? WindowsDrivers.InstallElevated(driver, Options.AcceptDriverLicense)
                : 87;
            Shutdown(code);
            return;
        }

        // Processo auxiliar elevado: só executa o comando de serviço e sai
        if (Options.ServiceCommand != null)
        {
            RunServiceCommand(Options.ServiceCommand);
            return;
        }

        bool testRun = Options.ScreenshotPath != null;
        if (!testRun)
        {
            _instance = new SingleInstance(Options.Simulate ? "LinkFlow.App.Sim" : "LinkFlow.App");
            if (!_instance.IsFirstInstance)
            {
                int exitCode = 0;
                if (Options.ExitRunning)
                {
                    Log.Info("--exit: pedindo encerramento da instância em execução.");
                    _instance.SignalExit();
                    if (!_instance.WaitForFirstInstanceExit(TimeSpan.FromSeconds(15))) exitCode = 1460;
                }
                else
                {
                    Log.Info("LinkFlow já está aberto: trazendo a janela existente.");
                    _instance.SignalActivate();
                }
                _instance.Dispose();
                _instance = null;
                Shutdown(exitCode);
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
        Updates = new AppUpdateViewModel(Settings, allowInstall: !Options.Simulate && !testRun);

        _engine = CreateEngineClient();
        _engine.Start();
        if (!_engine.IsRemote && !Options.Simulate && !testRun) WatchForService();
        SettingsVm.AttachEngine(_engine);

        _vm = new MainViewModel(_engine, alertHistoryPath: testRun || Options.Simulate
            ? null : Path.Combine(AppPaths.UserDir, "alerts.json"));
        // O SDK Logitech fica na sessão do usuário, em uma fila independente.
        if (!_engine.IsSimulated && !testRun)
        {
            var bridge = _peripheralBridge = new PeripheralRgbBridge(_engine);
            void UpdateStatus() => _vm.SetExternalPeripheralsStatus(bridge.Status, bridge.HasIssue);
            bridge.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
            _vm.RgbRefreshRequested += bridge.RequestResend;
            UpdateStatus();
        }
        _vm.AlertRaised += OnAlert;
        _vm.SnapshotApplied += OnSnapshotApplied;
        MainView = new MainWindow(_vm);
        if (!testRun)
        {
            _automation = new AutomationService(_vm);
            _automation.StatusChanged += () => _vm.AutomationStatus = _automation.Status;
        }

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
        if (Settings.Current.MiniVisible) MainView.SetWidgetEnabled(true);
        if (!Options.Simulate)
        {
            Updates.UpdateFound += version => _tray?.ShowNotification($"LinkFlow {version} disponível",
                "Abra Configurações → Atualizações para baixar a nova versão.");
            _ = Updates.WatchAsync();
        }
    }

    /// <summary>
    /// Serviço do Windows rodando → a interface vira cliente (não toca no hardware).
    /// Caso contrário, o engine roda dentro do próprio app (modo local).
    /// </summary>
    private IEngineClient CreateEngineClient()
    {
        bool tryService = !Options.Simulate || Environment.GetEnvironmentVariable("LINKFLOW_PIPE") != null;
        if (tryService)
        {
            var remote = Core.Ipc.PipeEngineClient.TryConnect(TimeSpan.FromMilliseconds(700));
            if (remote != null)
            {
                Log.Info("Conectado ao serviço LinkFlow (modo serviço).");
                return remote;
            }

            var state = Options.Simulate ? ServiceState.NotInstalled : ServiceManager.Query();
            if (state is ServiceState.Running or ServiceState.Starting or ServiceState.Stopped)
            {
                // Serviço instalado: ele é o dono do hardware. Dois engines no mesmo hub embaralham os
                // comandos (cores doidas, leituras de 0 RPM → modo de segurança com tudo a 100%).
                // Logo após o logon ele pode ainda não ter subido: inicia se der e espera.
                if (state == ServiceState.Stopped && AutoStartManager.IsElevated)
                {
                    Log.Info("Serviço instalado mas parado: iniciando.");
                    ServiceManager.Execute("start-service");
                }
                var wait = Options.StartInTray ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(20);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.Elapsed < wait)
                {
                    remote = Core.Ipc.PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2));
                    if (remote != null)
                    {
                        Log.Info($"Conectado ao serviço LinkFlow após {sw.Elapsed.TotalSeconds:0} s (modo serviço).");
                        return remote;
                    }
                    Thread.Sleep(1000);
                }
                Log.Warn("Serviço instalado mas sem resposta; usando modo local até ele aparecer.");
            }
        }

        IHardwareFactory factory = Options.Simulate ? new SimulatedHardwareFactory() : new RealHardwareFactory(usePeripheralBridge: true);
        var engine = new LinkFlowEngine(factory, new EngineOptions { ConfigPath = ResolveLocalConfigPath() });
        Log.Info($"Modo local ({(AutoStartManager.IsElevated ? "administrador" : "sem privilégio de administrador")}).");
        return new LocalEngineClient(engine);
    }

    /// <summary>
    /// Modo local sem administrador: se o config compartilhado (criado pelo serviço/admin) não for gravável,
    /// usa uma cópia em %AppData%\LinkFlow para não perder as alterações.
    /// </summary>
    private static string ResolveLocalConfigPath()
    {
        var shared = AppPaths.ControlConfigFile;
        if (CanWrite(shared)) return shared;

        var user = Path.Combine(AppPaths.UserDir, "config.json");
        try
        {
            if (!File.Exists(user) && File.Exists(shared))
            {
                Directory.CreateDirectory(AppPaths.UserDir);
                File.Copy(shared, user);
            }
        }
        catch (Exception ex) { Log.Warn($"Não foi possível copiar a configuração: {ex.Message}"); }
        Log.Info($"Configuração compartilhada somente leitura; usando {user}.");
        return user;
    }

    private static bool CanWrite(string file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (File.Exists(file))
            {
                using var _ = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
            else
            {
                var probe = file + ".probe";
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ Serviço / elevação

    private static void WaitForProcessExit(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(15000);
        }
        catch (ArgumentException) { /* já encerrou */ }
        catch (Exception ex) { Log.Warn($"Falha aguardando processo {pid}: {ex.Message}"); }
    }

    private void RunServiceCommand(string command)
    {
        int code = ServiceManager.Execute(command);
        if (code == 0 && command is "install-service" or "restart-service" or "start-service")
        {
            ServiceManager.WaitForPipe(TimeSpan.FromSeconds(15));
        }
        if (Options.LaunchUi) ServiceManager.LaunchUiAsUser();
        Shutdown(code);
    }

    /// <summary>
    /// Troca de modo que exige elevação: inicia o auxiliar elevado, que espera esta instância
    /// fechar, executa o comando e reabre a interface.
    /// </summary>
    public bool SwitchMode(string serviceCommand)
    {
        string args = $"--{serviceCommand} --wait-pid {Environment.ProcessId} --launch-ui";
        if (!ServiceManager.StartElevated(args, wait: false, out _)) return false;
        Log.Info($"Trocando de modo ({serviceCommand}); a interface será reaberta.");
        ExitApplication();
        return true;
    }

    /// <summary>Reabre o LinkFlow como administrador (modo local completo).</summary>
    public bool RestartElevated()
    {
        if (!ServiceManager.StartElevated($"--wait-pid {Environment.ProcessId}", wait: false, out _)) return false;
        ExitApplication();
        return true;
    }

    /// <summary>Comando que não exige reabrir a interface (ex.: reiniciar o serviço).</summary>
    public Task<int?> RunElevatedCommandAsync(string serviceCommand) => Task.Run<int?>(() =>
        ServiceManager.StartElevated($"--{serviceCommand}", wait: true, out var code) ? code : null);

    // ------------------------------------------------------------------ Bandeja

    private void SetupTray()
    {
        _tray = new TrayIconService();
        _tray.OpenRequested += ShowMainWindow;
        _tray.UpdatesRequested += () => MainView?.ShowAppUpdates();
        _tray.ExitRequested += ExitApplication;
        _tray.MiniWidgetToggled += () => MainView?.ToggleMiniWidget(fromTray: true);
        _tray.PresetRequested += key => _vm?.ApplyPreset(key);
        _tray.ProfileRequested += name => _vm?.ApplyProfileByName(name);
        _tray.LcdStreamToggled += on => { if (_vm != null) _vm.IsLcdAutoStream = on; };
        _tray.AutoStartToggled += on => SettingsVm.SetStartWithWindows(on);
        _tray.MenuOpening += RefreshTrayMenu;
        _tray.SetServiceMode(IsServiceMode);
        RefreshTrayMenu();
    }

    private void RefreshTrayMenu()
    {
        if (_tray == null || _vm == null || _engine == null) return;
        _tray.UpdateMenuState(_vm.ActivePreset, _engine.Config.Profiles, MainView?.Widget.IsWidgetEnabled == true,
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
            _peripheralBridge?.RequestResend();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        Log.Info($"Sessão do Windows encerrando ({e.ReasonSessionEnding}).");
        ExitApplication();
        base.OnSessionEnding(e);
    }

    // ------------------------------------------------------------------ Modo local → serviço

    /// <summary>
    /// No modo local, vigia o pipe do serviço (barato: só lista os pipes a cada 5 s). Se o serviço subir,
    /// a interface solta o hardware e reabre como cliente — nunca dois engines no mesmo hub.
    /// </summary>
    private void WatchForService()
    {
        _serviceWatch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
        _serviceWatch.Tick += (_, _) =>
        {
            if (!ServicePipeExists()) return;
            _serviceWatch.Stop();
            Log.Warn("Serviço LinkFlow apareceu com a interface em modo local: devolvendo o hardware ao serviço.");
            _handoffToService = true;
            ExitApplication();
        };
        _serviceWatch.Start();
    }

    private static bool ServicePipeExists()
    {
        try { return Directory.EnumerateFiles(@"\\.\pipe\", Core.Ipc.PipeProtocol.PipeName).Any(); }
        catch { return false; }
    }

    private void RelaunchAsServiceClient()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AutoStartManager.ExePath, "--tray") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível reabrir a interface: {ex.Message}");
        }
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
            Updates?.Dispose();
            Settings?.Flush();
            _tray?.Dispose();
            _peripheralBridge?.Dispose();
            _automation?.Dispose();
            _vm?.Dispose();
            _engine?.ShutdownAsync(restoreHardwareMode: !_handoffToService).Wait(TimeSpan.FromSeconds(6));
            _instance?.Dispose();
            if (_handoffToService) RelaunchAsServiceClient();
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
        if (Options.ScreenshotPage == 3) w.ForceLcdPreview();

        await Task.Delay(3500);
        if (Options.ScreenshotSelect > 0)
        {
            if (Options.ScreenshotPage == 1) w.ShowFanDevices();
            foreach (var f in w.ViewModel.Fans.Take(Options.ScreenshotSelect)) f.IsSelected = true;
        }
        if (Options.ScreenshotDetail != null)
        {
            w.ViewModel.OpenDetail(Options.ScreenshotDetail);
            await Task.Delay(1500);
        }
        await Task.Delay(300);
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
