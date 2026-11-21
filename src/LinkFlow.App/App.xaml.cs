using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;
using Microsoft.Win32;
using LinkFlow.App.Resources;
using System.Diagnostics;

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
    private TextWriterTraceListener? _screenshotBindingTrace;

    public StartupOptions Options { get; private set; } = new();
    public MainWindow? MainView { get; private set; }
    public UiSettingsService Settings { get; private set; } = null!;
    public SettingsViewModel SettingsVm { get; private set; } = null!;
    public AppUpdateViewModel Updates { get; private set; } = null!;
    public bool IsServiceMode => _engine?.IsRemote == true;
    private PeripheralRgbBridge? _peripheralBridge;
    private AutomationService? _automation;

    internal PeripheralRgbDiagnosticSnapshot? PeripheralsRgbDiagnostics => _peripheralBridge?.DiagnosticSnapshot;
    internal PeripheralRgbDiagnosticExportStatus? PeripheralsRgbDiagnosticExportStatus => _peripheralBridge?.DiagnosticExportStatus;

    public static new App Current => (App)Application.Current;

    public App()
    {
        Options = StartupOptions.Parse(Environment.GetCommandLineArgs()[1..]);
        if (Options.Simulate && Environment.GetEnvironmentVariable("LINKFLOW_DATA_DIR") == null)
        {
            // Simulação nunca toca na configuração real
            AppPaths.SharedDir = Path.Combine(Path.GetTempPath(), "LinkFlow-sim");
            AppPaths.UserDir = Path.Combine(Path.GetTempPath(), "LinkFlow-sim", "user");
        }

        // O idioma vai aqui, antes de o WPF começar a despachar: aplicado dentro do OnStartup, as operações seguintes
        // da thread da interface voltavam para a cultura do Windows (com a interface em inglês, números com vírgula).
        UiLanguage.Apply(UiLanguage.Resolve(Options.Language, UiLanguage.ReadSavedLanguage(AppPaths.UiSettingsFile),
            CultureInfo.CurrentUICulture));

        if (Options.ScreenshotPath is { } screenshot)
        {
            var bindingLog = Path.GetFullPath(screenshot + ".bindings.log");
            Directory.CreateDirectory(Path.GetDirectoryName(bindingLog)!);
            _screenshotBindingTrace = new TextWriterTraceListener(new StreamWriter(bindingLog, append: false));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            PresentationTraceSources.DataBindingSource.Listeners.Add(_screenshotBindingTrace);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Configure(Options.ServiceCommand != null || Options.InstallDriverRequested ? "app-admin" : "app",
            Environment.GetEnvironmentVariable("LINKFLOW_DEBUG") == "1" ? LogLevel.Debug : LogLevel.Info);
        Log.Info($"Idioma da interface: {UiLanguage.Culture.Name}.");
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
                else if (Options.StartInTray)
                {
                    // Início automático duplicado (tarefa antiga + chave Run): não abre a janela de quem já está na bandeja.
                    Log.Info("LinkFlow já está aberto: início automático repetido ignorado.");
                    if (AutoStartManager.IsElevated && !Options.Simulate && ServiceManager.Query() != ServiceState.NotInstalled)
                        AutoStartManager.MigrateTaskToRunValue();
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

        // A espera pelo serviço roda fora da thread da interface (pode durar minutos logo após o logon)
        StartupFeedback? feedback = null;
        try
        {
            var client = await Task.Run(() => CreateEngineClient(onWaiting: () => Dispatcher.Invoke(() =>
                feedback ??= new StartupFeedback(showWindow: !Options.StartInTray && !testRun)), _startupCancel.Token));
            feedback?.Dispose();
            feedback = null;
            if (Volatile.Read(ref _exiting) == 1)
            {
                // Pediram para sair durante a espera: o engine nem começou a controlar o hardware
                await client.ShutdownAsync(restoreHardwareMode: true);
                return;
            }
            _engine = client;
            CompleteStartup(testRun);
        }
        catch (Exception ex)
        {
            feedback?.Dispose();
            Log.Error("Falha ao iniciar a interface", ex);
            MessageBox.Show(string.Format(Strings.Startup_Failed, ex.Message), "LinkFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private readonly CancellationTokenSource _startupCancel = new();
    private bool _activateWhenReady;

    private void CompleteStartup(bool testRun)
    {
        _engine!.Start();
        if (!_engine.IsRemote && !Options.Simulate && !testRun) WatchForService();
        if (_engine.IsRemote && !Options.Simulate && !testRun) _ = Task.Run(LeaveElevatedAutoStart);
        SettingsVm.AttachEngine(_engine);
        Updates.AttachEngine(_engine);

        _vm = new MainViewModel(_engine, alertHistoryPath: testRun || Options.Simulate
            ? null : Path.Combine(AppPaths.UserDir, "alerts.json"));
        if (!testRun && !Options.Simulate)
        {
            SystemOperationJournal.Shared.Changed += operation => _engine.ReportExternalOperation(operation);
            foreach (var operation in SystemOperationJournal.Shared.Read().Where(o => o.State == WindowsOperationState.Unconfirmed).Take(10))
                _engine.ReportExternalOperation(operation);
        }
        // O SDK Logitech fica na sessão do usuário, em uma fila independente.
        if (!_engine.IsSimulated && !testRun)
        {
            var bridge = _peripheralBridge = new PeripheralRgbBridge(_engine);
            if (Options.PeripheralRgbDiagnosticsEnabled)
                bridge.StartDiagnostics(Path.Combine(AppPaths.UserDir, "diagnostics", $"peripheral-rgb-{Environment.ProcessId}.json"));
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

        if (Options.StartInTray && !_activateWhenReady)
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
            Updates.UpdateFound += version => _tray?.ShowNotification(string.Format(Strings.Tray_UpdateFoundTitle, version),
                Strings.Tray_UpdateFoundText);
            _ = Updates.WatchAsync();
        }
    }

    /// <summary>
    /// Serviço do Windows rodando → a interface vira cliente (não toca no hardware).
    /// Caso contrário, o engine roda dentro do próprio app (modo local).
    /// </summary>
    /// <param name="onWaiting">Chamado uma vez se for preciso esperar o serviço subir (para mostrar o aviso).</param>
    private IEngineClient CreateEngineClient(Action onWaiting, CancellationToken cancel)
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
                onWaiting();
                while (sw.Elapsed < wait && !cancel.IsCancellationRequested)
                {
                    remote = Core.Ipc.PipeEngineClient.TryConnect(TimeSpan.FromSeconds(2));
                    if (remote != null)
                    {
                        Log.Info($"Conectado ao serviço LinkFlow após {sw.Elapsed.TotalSeconds:0} s (modo serviço).");
                        return remote;
                    }
                    cancel.WaitHandle.WaitOne(1000);
                }
                // A indisponibilidade do pipe não transfere a posse do hardware. O serviço pode estar
                // controlando normalmente, ou abrir o hardware antes de publicar seu endpoint.
                if (cancel.IsCancellationRequested) throw new OperationCanceledException(cancel);
                Log.Warn("Serviço instalado sem resposta: abrindo interface em modo de espera, sem assumir o hardware.");
                return new WaitingServiceClient();
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

    /// <summary>
    /// Modo serviço: troca a tarefa elevada antiga pela chave Run. Se foi essa tarefa que abriu esta
    /// instância, reabre a interface sem administrador para a iluminação Logitech já funcionar nesta sessão.
    /// </summary>
    private void LeaveElevatedAutoStart()
    {
        var result = AutoStartManager.MigrateTaskToRunValue();
        if (result == AutoStartMigration.NotNeeded) return;
        SettingsVm.RefreshAutoStart();
        if (result != AutoStartMigration.Migrated || !Options.StartInTray || !AutoStartManager.IsElevated) return;

        if (UnelevatedLauncher.Start(AutoStartManager.ExePath, $"--tray --wait-pid {Environment.ProcessId}", AppContext.BaseDirectory))
        {
            Log.Info("Reabrindo a interface sem administrador.");
            Dispatcher.BeginInvoke(ExitApplication);
        }
    }

    private void RunServiceCommand(string command)
    {
        int code = ServiceManager.Execute(command);
        // O serviço passa a ser o dono do hardware: a interface não deve mais abrir elevada no logon.
        if (code == 0 && command == "install-service") AutoStartManager.MigrateTaskToRunValue();
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
        _tray.ShowNotification(alert.LocalizedTitle(), alert.LocalizedMessage(),
            alert.Severity == AlertSeverity.Critical ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Warning);
    }

    public void NotifyHiddenToTray()
    {
        if (_tray == null || Settings.Current.TrayHintShown) return;
        Settings.Update(s => s.TrayHintShown = true);
        _tray.ShowNotification(Strings.Tray_StillRunningTitle,
            IsServiceMode ? Strings.Tray_StillRunningService : Strings.Tray_StillRunningLocal);
    }

    public void ShowMainWindow()
    {
        // Pedido de abrir a janela (novo clique no atalho) enquanto ainda espera o serviço: abre ao terminar
        if (MainView == null) _activateWhenReady = true;
        else MainView.ShowFromTray();
    }

    /// <summary>Confirma na bandeja um perfil aplicado por atalho global (útil dentro de jogos).</summary>
    public void NotifyPresetChanged(string preset)
    {
        if (MainView?.IsVisible == true && MainView.IsActive) return;
        _tray?.ShowNotification(Strings.Tray_ProfileApplied, preset);
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
            // O Windows precisa confirmar que o serviço está rodando: outro programa poderia criar um pipe com o
            // mesmo nome e fazer a interface largar o hardware e reabrir em ciclo.
            if (!ServicePipeExists() || ServiceManager.Query() != ServiceState.Running) return;
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
        // Como cliente do serviço a interface não precisa de administrador: não herda a elevação do modo local.
        if (!UnelevatedLauncher.Start(AutoStartManager.ExePath, $"--tray --wait-pid {Environment.ProcessId}", AppContext.BaseDirectory))
            Log.Warn("Não foi possível reabrir a interface. Abra o LinkFlow pelo menu Iniciar.");
    }

    // ------------------------------------------------------------------ Encerramento

    /// <summary>
    /// Reabre a interface (troca de idioma). No modo serviço o controle do hardware não para; no modo local o engine
    /// reinicia junto com o aplicativo.
    /// </summary>
    public void RestartInterface()
    {
        Settings.Flush();
        string args = $"--wait-pid {Environment.ProcessId}" + (Options.Simulate ? " --simulate" : string.Empty);
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Não foi possível reabrir o LinkFlow para trocar o idioma: {ex.Message}");
            return;
        }
        Log.Info("Reiniciando a interface para trocar o idioma.");
        ExitApplication();
    }

    /// <summary>Encerra o aplicativo de verdade (no modo local o hub volta ao modo hardware).</summary>
    public void ExitApplication()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        Log.Info("Encerrando LinkFlow.");
        _startupCancel.Cancel();
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
        if (Options.ScreenshotPage == 1 && Options.ScreenshotDetail == "devices") w.ShowFanDevices();
        if (Options.ScreenshotPage == 3) w.ForceLcdPreview();

        await Task.Delay(3500);
        if (Options.ScreenshotSelect > 0)
        {
            if (Options.ScreenshotPage == 1) w.ShowFanDevices();
            foreach (var f in w.ViewModel.Fans.Take(Options.ScreenshotSelect)) f.IsSelected = true;
        }
        if (Options.ScreenshotPage == 1 && Options.ScreenshotGroupName is { } group)
            w.ViewModel.GroupNameInput = group;
        if (Options.ScreenshotHardwareDetail != null &&
            Enum.TryParse<Services.HardwareKind>(Options.ScreenshotHardwareDetail, ignoreCase: true, out var hardwareKind))
        {
            await w.OpenHardwareDetailAsync(hardwareKind);
            await Task.Delay(1500);
        }
        if (Options.ScreenshotDetail != null)
        {
            w.ViewModel.OpenDetail(Options.ScreenshotDetail);
            await Task.Delay(1500);
        }
        await Task.Delay(300);
        w.UpdateLayout();

        var root = (FrameworkElement)w.Content;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path + ".layout.json", CaptureScreenshotLayout(root));
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        await using (var fs = File.Create(path)) encoder.Save(fs);

        Log.Info($"Screenshot salvo em {path}");
        _screenshotBindingTrace?.Flush();
        if (_screenshotBindingTrace is { } bindingTrace)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingTrace);
            bindingTrace.Dispose();
            _screenshotBindingTrace = null;
        }
        ExitApplication();
    }

    private static string CaptureScreenshotLayout(FrameworkElement root)
    {
        var pending = new Stack<DependencyObject>();
        var controls = new List<object>();
        pending.Push(root);
        while (pending.TryPop(out var node) && controls.Count < 512)
        {
            if (node is FrameworkElement element && element.IsVisible &&
                (node is System.Windows.Controls.Button or System.Windows.Controls.TextBox or System.Windows.Controls.ComboBox ||
                    element.Name.StartsWith("Batch", StringComparison.Ordinal)))
            {
                var origin = element.TransformToAncestor(root).Transform(new Point());
                var padding = node switch
                {
                    System.Windows.Controls.Control control => control.Padding,
                    System.Windows.Controls.Border border => border.Padding,
                    _ => new Thickness()
                };
                controls.Add(new
                {
                    element.Name, Type = element.GetType().Name,
                    X = origin.X, Y = origin.Y, Width = element.ActualWidth, Height = element.ActualHeight,
                    DesiredWidth = element.DesiredSize.Width, DesiredHeight = element.DesiredSize.Height,
                    element.MinWidth, element.MinHeight, element.IsEnabled,
                    FontSize = node is System.Windows.Controls.Control fontControl ? (double?)fontControl.FontSize : null,
                    MaxWidth = double.IsFinite(element.MaxWidth) ? (double?)element.MaxWidth : null,
                    MarginLeft = element.Margin.Left, MarginTop = element.Margin.Top,
                    MarginRight = element.Margin.Right, MarginBottom = element.Margin.Bottom,
                    PaddingLeft = padding.Left, PaddingTop = padding.Top,
                    PaddingRight = padding.Right, PaddingBottom = padding.Bottom,
                    BoundsInsideRoot = origin.X >= 0 && origin.Y >= 0 &&
                        origin.X + element.ActualWidth <= root.ActualWidth && origin.Y + element.ActualHeight <= root.ActualHeight
                });
            }
            for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            ViewportWidth = root.ActualWidth, ViewportHeight = root.ActualHeight,
            DpiScale = VisualTreeHelper.GetDpi(root).DpiScaleX, Controls = controls,
            Scope = "Layout measurements only; no input values, device output or hardware confirmation"
        }, Json.Options);
    }
}
