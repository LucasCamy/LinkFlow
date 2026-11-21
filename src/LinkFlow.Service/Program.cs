using LinkFlow.Core;
using LinkFlow.Service;

// Private GPU-only child: precedes Host/AppPaths/configuration and can never recursively host the service.
if (args.Contains("--gpu-rgb-worker", StringComparer.OrdinalIgnoreCase))
{
    Environment.ExitCode = args.Length == 1 ? await GpuRgbWorkerHost.RunAsync() : 3;
    return;
}

// LinkFlow.Service.exe            → roda como serviço do Windows (ou console, se iniciado manualmente)
// LinkFlow.Service.exe --simulate → hardware simulado (testes)
// Precisa preceder Host/AppPaths/PipeProtocol: a simulação nunca usa dados ou endpoint da instalação real.
var startupEnvironment = ServiceStartupIsolation.Configure(args);
if (startupEnvironment.Simulated)
    Console.WriteLine($"Simulação isolada: dados em {startupEnvironment.DataDirectory}; pipe {startupEnvironment.PipeName}.");
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInfo.Name);
builder.Services.AddSingleton<PowerEvents>();
if (Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, LinkFlowServiceLifetime>();
builder.Services.AddSingleton(new WorkerOptions(args.Contains("--simulate", StringComparer.OrdinalIgnoreCase),
    args.Contains("--no-fleet", StringComparer.OrdinalIgnoreCase)));
builder.Services.AddHostedService<EngineWorker>();
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

Log.Configure("service", Environment.GetEnvironmentVariable("LINKFLOW_DEBUG") == "1" ? LinkFlow.Core.LogLevel.Debug : LinkFlow.Core.LogLevel.Info);
// Antes de qualquer gravação como SYSTEM (inclusive o log): a pasta não pode ficar sob controle de usuários
SharedDataSecurity.TryHarden(AppPaths.SharedDir);
await builder.Build().RunAsync();
