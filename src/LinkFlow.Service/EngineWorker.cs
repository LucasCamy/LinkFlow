using LinkFlow.Core;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Service;

public static class ServiceInfo
{
    public const string Name = PipeIdentity.ServiceName;
    public const string DisplayName = "LinkFlow";
}

public sealed record WorkerOptions(bool Simulate, bool DisableFleet = false);

/// <summary>
/// Hospeda o engine no serviço: o hardware continua controlado mesmo sem nenhuma janela aberta,
/// antes do logon e se a interface travar. Ao parar o serviço, o hub volta ao modo hardware.
/// </summary>
public sealed class EngineWorker : BackgroundService
{
    private readonly WorkerOptions _options;
    private readonly PowerEvents _power;
    private LinkFlowEngine? _engine;
    private PipeEngineServer? _server;
    private FleetAgentServer? _fleet;
    private System.Security.Cryptography.X509Certificates.X509Certificate2? _fleetCertificate;

    public EngineWorker(WorkerOptions options, PowerEvents power)
    {
        _options = options;
        _power = power;
        _power.Resumed += () => _engine?.NotifyResume();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.CleanupOldLogs();
        Log.Info($"Serviço LinkFlow iniciando ({(_options.Simulate ? "simulação" : "hardware real")}, PID {Environment.ProcessId}).");

        bool serviceOwnedWorker = !_options.Simulate &&
            Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
        IHardwareFactory factory = _options.Simulate ? new SimulatedHardwareFactory() : new RealHardwareFactory(
            gpuRgbFactory: serviceOwnedWorker
                ? () => new IsolatedGpuRgbDevice(Environment.ProcessPath ?? throw new InvalidOperationException("Missing service executable path."))
                : null);
        _engine = new LinkFlowEngine(factory, new EngineOptions
        {
            ConfigPath = AppPaths.ControlConfigFile,
            MediaLibraryOnly = true,
            // Fila travada: o engine já devolveu o hub ao modo hardware; encerrar o processo faz o Windows
            // reiniciar o serviço (ações de recuperação configuradas na instalação).
            LaneStalled = problem =>
            {
                if (System.Diagnostics.Debugger.IsAttached) return;
                Environment.FailFast($"LinkFlow: {problem}");
            }
        });
        _engine.Start();

        _server = new PipeEngineServer(_engine);
        _server.Start();

        if (!_options.DisableFleet)
        {
            try
            {
                var configured = FleetProvisioning.Load(FleetProvisioning.DefaultConfigPath);
                if (configured is { } fleet)
                {
                    _fleetCertificate = fleet.Certificate;
                    _fleet = new FleetAgentServer(new LocalEngineClient(_engine), fleet.Options, fleet.Certificate);
                    await _fleet.StartAsync(stoppingToken);
                    Log.Info("Agente TLS de observação/controle iniciado com configuração explícita.");
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or System.Security.Cryptography.CryptographicException or
                                       System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                _fleetCertificate?.Dispose();
                _fleetCertificate = null;
                Log.Warn("Agente de rede não iniciado: revise a configuração privada e o certificado. O controle local continua ativo.");
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Info("Serviço LinkFlow parando.");
        await base.StopAsync(cancellationToken);
        if (_fleet != null) await _fleet.DisposeAsync();
        _fleetCertificate?.Dispose();
        if (_server != null) await _server.DisposeAsync();
        if (_engine != null)
        {
            await _engine.StopAsync(restoreHardwareMode: true);
            _engine.Dispose();
        }
        Log.Info("Serviço LinkFlow parado.");
    }
}
