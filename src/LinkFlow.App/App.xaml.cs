using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App;

public partial class App : Application
{
    private IEngineClient? _engine;
    private MainViewModel? _vm;
    private int _exiting;

    public StartupOptions Options { get; private set; } = new();
    public MainWindow? MainView { get; private set; }

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

        Log.Configure("app");
        Log.CleanupOldLogs();
        Log.Info($"LinkFlow iniciando (args: {string.Join(' ', e.Args)})");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Exceção não tratada", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("Task não observada", ex.Exception); ex.SetObserved(); };

        IHardwareFactory factory = Options.Simulate ? new SimulatedHardwareFactory() : new RealHardwareFactory();
        var engine = new LinkFlowEngine(factory, new EngineOptions { ConfigPath = AppPaths.ControlConfigFile });
        _engine = new LocalEngineClient(engine);
        _engine.Start();

        _vm = new MainViewModel(_engine);
        MainView = new MainWindow(_vm);

        if (Options.ScreenshotPath != null)
        {
            _ = RunScreenshotAsync(Options.ScreenshotPath);
            return;
        }

        MainView.Show();
    }

    /// <summary>Encerra o aplicativo de verdade (o hub volta ao modo hardware).</summary>
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
            _vm?.Dispose();
            _engine?.ShutdownAsync(restoreHardwareMode: true).Wait(TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            Log.Error("Erro ao encerrar engine", ex);
        }
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro na interface", e.Exception);
        e.Handled = true; // o controle das ventoinhas continua; não derruba o app por erro de UI
    }

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
