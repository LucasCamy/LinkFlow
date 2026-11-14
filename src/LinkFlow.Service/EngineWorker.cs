using LinkFlow.Core;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Service;

public static class ServiceInfo
{
    public const string Name = "LinkFlowService";
    public const string DisplayName = "LinkFlow";
}

public sealed record WorkerOptions(bool Simulate);

/// <summary>
/// Hospeda o engine no serviço: o hardware continua controlado mesmo sem nenhuma janela aberta,
/// antes do logon e se a interface travar. Ao parar o serviço, o hub volta ao modo hardware.
/// </summary>
public sealed class EngineWorker : BackgroundService
{
    private readonly WorkerOptions _options;
    private LinkFlowEngine? _engine;
    private PipeEngineServer? _server;

    public EngineWorker(WorkerOptions options) => _options = options;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.CleanupOldLogs();
        Log.Info($"Serviço LinkFlow iniciando ({(_options.Simulate ? "simulação" : "hardware real")}, PID {Environment.ProcessId}).");

        IHardwareFactory factory = _options.Simulate ? new SimulatedHardwareFactory() : new RealHardwareFactory();
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
        if (_server != null) await _server.DisposeAsync();
        if (_engine != null)
        {
            await _engine.StopAsync(restoreHardwareMode: true);
            _engine.Dispose();
        }
        Log.Info("Serviço LinkFlow parado.");
    }
}
