using System.ServiceProcess;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace LinkFlow.Service;

/// <summary>Avisos de energia recebidos pelo próprio serviço (o retorno da suspensão não depende da interface).</summary>
public sealed class PowerEvents
{
    public event Action? Resumed;

    internal void RaiseResumed()
    {
        try { Resumed?.Invoke(); }
        catch (Exception ex) { LinkFlow.Core.Log.Error("Falha ao tratar o retorno da suspensão", ex); }
    }
}

/// <summary>
/// Ciclo de vida do serviço com eventos de energia. Antes só a interface avisava o retorno da suspensão:
/// sem janela aberta (antes do logon ou com o app fechado) o serviço não reconectava o hub de imediato.
/// </summary>
public sealed class LinkFlowServiceLifetime : WindowsServiceLifetime
{
    private readonly PowerEvents _events;

    public LinkFlowServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory, IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor, PowerEvents events)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _events = events;
        CanHandlePowerEvent = true;
    }

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        if (powerStatus is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic)
            _events.RaiseResumed();
        return base.OnPowerEvent(powerStatus);
    }
}
