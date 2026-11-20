using System.Diagnostics;

namespace LinkFlow.Tests;

/// <summary>Retorno da suspensão com o hardware simulado: reconexão do hub, avisos repetidos e watchdog.</summary>
public class ResumeTests
{
    private static byte Speed(EngineFixture f, int ch) => f.Hw.Hub.LastSpeeds.TryGetValue(ch, out var p) ? p : (byte)0;

    private static bool Restored(EngineFixture f, byte percent) =>
        f.Hw.Hub.InSoftwareMode && Speed(f, 1) == percent && Speed(f, 11) == FanLimits.PumpMinPercent && f.Hw.Hub.LastRgb != null;

    [Fact]
    public void Resume_reconnects_the_hub_and_restores_speeds_and_color()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 });
        EngineFixture.WaitUntil(() => Restored(f, 40), because: "estado inicial aplicado");

        // O hub perdeu a energia na suspensão: voltou ao modo hardware, sem velocidades nem cor. O reforço periódico
        // das velocidades levaria 30 s e o modo software nunca voltaria sem reconectar.
        f.Hw.Hub.SimulateSuspend();
        int connects = f.Hw.Hub.ConnectCalls;
        f.Engine.NotifyResume();

        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls > connects && Restored(f, 40),
            because: "hub reconectado em modo software com velocidades e cor reenviadas");
        var snap = f.Engine.GetSnapshot();
        Assert.True(snap.HubConnected);
        Assert.Equal(11, snap.Fans.Count);
        Assert.DoesNotContain(snap.Alerts, a => a.Key == "hub-lost");
    }

    [Fact]
    public void Repeated_resume_notices_reconnect_the_hub_only_once()
    {
        // Intervalo longo: os avisos repetidos chegam bem depois de o hub já ter reconectado pelo primeiro
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, o => o.ResumeCoalesceWindow = TimeSpan.FromMinutes(5));
        EngineFixture.WaitUntil(() => Restored(f, 40));
        int connects = f.Hw.Hub.ConnectCalls;

        // Serviço recebe ResumeAutomatic e, em seguida, ResumeSuspend; a interface repassa o dela
        f.Engine.NotifyResume();
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls > connects && Restored(f, 40), because: "reconectou ao voltar da suspensão");
        f.Engine.NotifyResume();
        f.Engine.NotifyResume();
        Thread.Sleep(400);
        Assert.Equal(connects + 1, f.Hw.Hub.ConnectCalls);
    }

    [Fact]
    public void A_later_resume_reconnects_the_hub_again()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, o => o.ResumeCoalesceWindow = TimeSpan.FromMilliseconds(100));
        EngineFixture.WaitUntil(() => Restored(f, 40));
        int connects = f.Hw.Hub.ConnectCalls;

        f.Engine.NotifyResume();
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls == connects + 1, because: "primeira suspensão");
        Thread.Sleep(250);
        f.Hw.Hub.SimulateSuspend();
        f.Engine.NotifyResume();
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls == connects + 2 && Restored(f, 40), because: "segunda suspensão também tratada");
    }

    [Fact]
    public void Hub_missing_right_after_resume_is_retried_quickly_without_alert()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, o =>
        {
            o.ReconnectInterval = TimeSpan.FromSeconds(60);   // a tentativa normal não chegaria a tempo
            o.ResumeReconnectInterval = TimeSpan.FromMilliseconds(50);
            o.ResumeGracePeriod = TimeSpan.FromSeconds(30);
        });
        EngineFixture.WaitUntil(() => Restored(f, 40));
        var hubLost = 0;
        f.Engine.AlertRaised += a => { if (a.Key == "hub-lost") Interlocked.Increment(ref hubLost); };

        // USB do hub ainda não voltou quando o aviso de retorno chega
        f.Hw.Hub.Present = false;
        f.Hw.Hub.SimulateSuspend();
        int connects = f.Hw.Hub.ConnectCalls;
        f.Engine.NotifyResume();
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls >= connects + 3, because: "novas tentativas em sequência curta");
        Assert.False(f.Engine.GetSnapshot().HubConnected);

        f.Hw.Hub.Present = true;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().HubConnected && Restored(f, 40),
            because: "hub voltou sem esperar o intervalo normal de reconexão");
        Thread.Sleep(100);
        Assert.Equal(0, Volatile.Read(ref hubLost));
        Assert.DoesNotContain(f.Engine.GetSnapshot().Alerts, a => a.Key == "hub-lost");
    }

    [Fact]
    public void Hub_still_missing_after_the_resume_grace_period_is_reported()
    {
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, o =>
        {
            o.ResumeReconnectInterval = TimeSpan.FromMilliseconds(50);
            o.ResumeGracePeriod = TimeSpan.FromMilliseconds(300);
        });
        EngineFixture.WaitUntil(() => Restored(f, 40));

        f.Hw.Hub.Present = false;
        f.Engine.NotifyResume();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.Any(a => a.Key == "hub-lost"),
            because: "passado o período após a suspensão, o hub ausente vira alerta");

        f.Hw.Hub.Present = true;
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().HubConnected && Restored(f, 40), because: "hub voltou");
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Alerts.All(a => a.Key != "hub-lost"), because: "alerta limpo");
    }

    [Fact]
    public void Watchdog_does_not_report_stalled_lanes_after_the_clock_jumps_over_a_suspension()
    {
        var stalled = 0;
        using var f = new EngineFixture(new ControlConfig { GlobalPercent = 40 }, o =>
        {
            o.LaneStallTimeout = TimeSpan.FromHours(1);
            o.LaneStalled = _ => Interlocked.Increment(ref stalled);
        });
        EngineFixture.WaitUntil(() => Restored(f, 40) && f.Engine.GetSnapshot().SensorsReady && f.Hw.Sensors.IsOpen);
        Thread.Sleep(100); // as duas filas já bateram

        // O Stopwatch do Windows continua contando durante a suspensão: na primeira verificação depois de acordar,
        // a última batida das filas parece ter horas. Antes o serviço se encerrava (FailFast) logo ao voltar.
        long now = Stopwatch.GetTimestamp();
        f.Engine.CheckLanes(now + (long)(TimeSpan.FromHours(2).TotalSeconds * Stopwatch.Frequency));
        f.Engine.CheckLanes(now + (long)(TimeSpan.FromHours(5).TotalSeconds * Stopwatch.Frequency));

        Assert.Equal(0, Volatile.Read(ref stalled));
        Assert.DoesNotContain(f.Engine.GetSnapshot().Alerts, a => a.Key == "engine-stalled");
        Assert.True(f.Hw.Hub.InSoftwareMode, "o hub continua sob controle do LinkFlow");
    }
}
