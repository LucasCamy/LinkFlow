using System.Diagnostics;

namespace LinkFlow.Tests;

/// <summary>Supervisor de sincronismo do RGB: novas tentativas, reinicialização por dispositivo e ressincronização.</summary>
public class RgbSyncSupervisorTests
{
    private static readonly (byte, byte, byte, byte) Cyan = (0, 240, 255, 100);
    private static long S(double seconds) => 1_000_000 + (long)(seconds * Stopwatch.Frequency);

    // ------------------------------------------------------------------ Estado de saúde (sem engine)

    [Fact]
    public void Health_backs_off_reinitializes_and_logs_one_line_per_episode()
    {
        var policy = new RgbSyncPolicy
        {
            RetryFirst = TimeSpan.FromSeconds(1), RetryMax = TimeSpan.FromSeconds(4), LogAfter = TimeSpan.FromSeconds(5),
            StableAfter = TimeSpan.FromSeconds(5), FailuresBeforeReinit = 3, ReinitAfter = TimeSpan.FromSeconds(8),
            ReinitMax = TimeSpan.FromSeconds(20), MaxResendsPerWindow = 100
        };
        var h = new RgbDeviceHealth("RGB da placa-mãe", policy);
        h.Succeeded(S(0), Cyan);

        Assert.Null(h.Failed(S(1)));                       // próxima tentativa em 1 s
        Assert.True(h.HoldFrame(S(1.5)));
        Assert.False(h.ResendDue(S(1.5), animated: false));
        Assert.True(h.ResendDue(S(2), animated: false));
        Assert.Null(h.Failed(S(2)));                       // 2 s
        Assert.Null(h.Failed(S(4)));                       // 4 s
        var entered = h.Failed(S(8));                      // teto de 4 s; 7 s falhando → uma linha
        Assert.Contains("fora de sincronismo há 7 s (4 envio(s) recusado(s))", entered);
        Assert.Null(h.Failed(S(8.1)));

        Assert.False(h.ReinitDue(S(8.5)));                 // 4 falhas, mas só 7,5 s falhando
        Assert.True(h.ReinitDue(S(9)));
        Assert.Contains("reinicializando só este dispositivo", h.Reinitialized(S(9)));
        Assert.False(h.ReinitDue(S(16.9)));                // próxima reinicialização 8 s depois
        Assert.True(h.ReinitDue(S(17)));
        Assert.Null(h.Reinitialized(S(17)));               // a segunda não vai para o log

        h.Succeeded(S(20), Cyan);
        Assert.False(h.Failing);
        Assert.True(h.InEpisode);
        Assert.Null(h.Poll(S(24)));                        // ainda observando
        var recovered = h.Poll(S(25));
        Assert.Equal("RGB da placa-mãe: sincronismo recuperado depois de 19 s fora (5 falha(s), 2 reinicialização(ões)).", recovered);
        Assert.False(h.InEpisode);
        Assert.Equal(S(20), h.LastRecoveryAt);
        Assert.Null(h.Poll(S(40)));
    }

    [Fact]
    public void Short_glitch_is_retried_silently()
    {
        var h = new RgbDeviceHealth("RGB GPU", new RgbSyncPolicy());
        h.Succeeded(S(0), Cyan);
        Assert.Null(h.Failed(S(1)));
        Assert.Null(h.Failed(S(3)));
        h.Succeeded(S(4), Cyan);
        Assert.Null(h.Poll(S(60)));                        // fechou o episódio sem nenhuma linha
        Assert.False(h.InEpisode);
    }

    [Fact]
    public void Health_limits_supervisor_resends_per_window()
    {
        var policy = new RgbSyncPolicy
        {
            RetryFirst = TimeSpan.FromMilliseconds(100), RetryMax = TimeSpan.FromMilliseconds(100),
            MaxResendsPerWindow = 2, ResendWindow = TimeSpan.FromSeconds(10)
        };
        var h = new RgbDeviceHealth("RGB da placa-mãe", policy);
        h.Failed(S(1));
        Assert.True(h.ResendDue(S(1.2), false));
        h.Resent(S(1.2));
        h.Failed(S(1.2));
        Assert.True(h.ResendDue(S(1.4), false));
        h.Resent(S(1.4));
        h.Failed(S(1.4));
        Assert.False(h.ResendDue(S(1.6), false));          // 2 reenvios em 10 s: espera a janela
        Assert.Equal(9.6, h.UntilNextAction(S(1.6), false)!.Value.TotalSeconds, 3);
        Assert.True(h.ResendDue(S(11.3), false));
    }

    [Fact]
    public void Static_refresh_resync_and_ram_exception()
    {
        var policy = new RgbSyncPolicy { StaticRefresh = TimeSpan.FromSeconds(5), ResyncDelay = TimeSpan.FromSeconds(3) };
        var h = new RgbDeviceHealth("RGB da placa-mãe", policy);
        var ram = new RgbDeviceHealth("RGB da RAM", policy, periodicRefresh: false);
        h.Succeeded(S(0), Cyan);
        ram.Succeeded(S(0), Cyan);

        Assert.False(h.ResendDue(S(4.9), animated: false));
        Assert.True(h.ResendDue(S(5), animated: false));   // cor fixa sem envio há 5 s
        Assert.False(h.ResendDue(S(5), animated: true));   // efeito já envia todo quadro
        Assert.False(ram.ResendDue(S(60), animated: false)); // RAM grava configuração: sem atualização periódica

        h.Succeeded(S(5), Cyan);
        h.RequestResync(S(6));
        ram.RequestResync(S(6));
        Assert.False(h.ResendDue(S(8.9), false));
        Assert.True(h.ResendDue(S(9), false));
        Assert.True(ram.ResendDue(S(9), false));            // ressincronização vale para todos
        h.Resent(S(9));
        Assert.False(h.ResendDue(S(9.1), false));
    }

    [Fact]
    public void Animated_device_coming_back_realigns_smoothly()
    {
        var policy = new RgbSyncPolicy { CatchUp = TimeSpan.FromSeconds(1) };
        var h = new RgbDeviceHealth("RGB da placa-mãe", policy);
        var target = ((byte)0, (byte)0, (byte)200, (byte)100);
        h.Succeeded(S(0), (100, 0, 0, 100));
        h.Failed(S(1));

        var first = h.Output(target, S(2), animated: true);
        Assert.Equal(((byte)100, (byte)0, (byte)0, (byte)100), first);   // recomeça da cor que o dispositivo mostra
        h.Succeeded(S(2), first);
        Assert.Equal(((byte)50, (byte)0, (byte)100, (byte)100), h.Output(target, S(2.5), true));
        Assert.Equal(target, h.Output(target, S(3), true));               // em fase com os outros
        Assert.Equal(target, h.Output(target, S(3.1), true));
        Assert.Equal(target, h.Output(target, S(3.2), animated: false));  // cor fixa nunca mistura
    }

    // ------------------------------------------------------------------ Engine com hardware simulado

    private static EngineFixture Fixture(Action<RgbSyncPolicy>? tune = null, ControlConfig? config = null) =>
        new(config, o =>
        {
            var policy = new RgbSyncPolicy
            {
                RetryFirst = TimeSpan.FromMilliseconds(30), RetryMax = TimeSpan.FromMilliseconds(120),
                StaticRefresh = null, FailuresBeforeReinit = 1000, MaxResendsPerWindow = 100,
                ResyncDelay = TimeSpan.FromMilliseconds(100), LogAfter = TimeSpan.FromHours(1),
                StableAfter = TimeSpan.FromMilliseconds(100), SleepGap = TimeSpan.FromHours(1)
            };
            tune?.Invoke(policy);
            o.RgbSync = policy;
        });

    private static SimulatedRgb[] Devices(EngineFixture f) =>
        [f.Hw.GpuRgb, f.Hw.MotherboardRgb, f.Hw.RamRgb, f.Hw.PeripheralsRgb];

    private static void WaitAllApplied(EngineFixture f) =>
        EngineFixture.WaitUntil(() => Devices(f).All(d => d.Last != null) && f.Engine.GetSnapshot().GpuRgbApplied == true,
            because: "cor inicial em todos os dispositivos");

    private static void SetColor(EngineFixture f) =>
        f.Engine.UpdateConfig(c => { c.Rgb.R = 200; c.Rgb.G = 10; c.Rgb.B = 30; c.Rgb.LedGamma = 1f; });

    private static bool HasNewColor(SimulatedRgb d) => d.Last is { R: 200, G: 10, B: 30 };

    [Fact]
    public void Device_failing_for_a_while_is_resent_without_redetection_or_touching_the_others()
    {
        using var f = Fixture();
        WaitAllApplied(f);
        var calls = Devices(f).Select(d => d.Calls).ToArray();
        var inits = Devices(f).Select(d => d.InitializationCalls).ToArray();

        f.Hw.MotherboardRgb.FailNextColorWrites = 3;
        SetColor(f);

        EngineFixture.WaitUntil(() => Devices(f).All(HasNewColor), because: "placa-mãe recebe a cor depois de 3 recusas");
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().RgbStatus.StartsWith("Sincronizado:") &&
                                      !f.Engine.GetSnapshot().RgbStatus.Contains("Recuperando"), because: "status volta ao normal");
        Thread.Sleep(200);
        Assert.Equal(calls[1] + 4, f.Hw.MotherboardRgb.Calls);              // 1 envio + 3 novas tentativas
        Assert.Equal(calls[0] + 1, f.Hw.GpuRgb.Calls);                      // os outros: só o envio da mudança
        Assert.Equal(calls[2] + 1, f.Hw.RamRgb.Calls);
        Assert.Equal(calls[3] + 1, f.Hw.PeripheralsRgb.Calls);
        Assert.Equal(inits, Devices(f).Select(d => d.InitializationCalls).ToArray()); // ninguém redetectado
    }

    [Fact]
    public void Device_that_keeps_failing_is_reinitialized_alone()
    {
        using var f = Fixture(p =>
        {
            p.FailuresBeforeReinit = 3;
            p.ReinitAfter = TimeSpan.FromMilliseconds(150);
            p.ReinitMax = TimeSpan.FromMilliseconds(300);
        });
        WaitAllApplied(f);
        var inits = Devices(f).Select(d => d.InitializationCalls).ToArray();
        int hubConnections = f.Hw.Hub.ConnectCalls;

        f.Hw.MotherboardRgb.FailColorWrites = true;
        SetColor(f);

        EngineFixture.WaitUntil(() => f.Hw.MotherboardRgb.InitializationCalls >= inits[1] + 2, because: "placa-mãe reinicializada (com espera)");
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().RgbStatus.Contains("Recuperando automaticamente: Placa-mãe"),
            because: "status mostra a recuperação em andamento");
        Assert.Equal(inits[0], f.Hw.GpuRgb.InitializationCalls);
        Assert.Equal(inits[2], f.Hw.RamRgb.InitializationCalls);
        Assert.Equal(inits[3], f.Hw.PeripheralsRgb.InitializationCalls);
        Assert.Equal(hubConnections, f.Hw.Hub.ConnectCalls);                // hub (e ventoinhas) intacto

        f.Hw.MotherboardRgb.FailColorWrites = false;
        EngineFixture.WaitUntil(() => HasNewColor(f.Hw.MotherboardRgb) && !f.Engine.GetSnapshot().RgbStatus.Contains("Recuperando"),
            because: "placa-mãe volta sozinha");
    }

    [Fact]
    public void Resume_resyncs_every_device()
    {
        using var f = Fixture();
        WaitAllApplied(f);
        var calls = Devices(f).Select(d => d.Calls).ToArray();
        var inits = Devices(f).Select(d => d.InitializationCalls).ToArray();

        f.Engine.NotifyResume();

        // Reenvio imediato + ressincronização depois que os controladores acordam
        EngineFixture.WaitUntil(() => Devices(f).Select((d, i) => d.Calls >= calls[i] + 2).All(x => x),
            because: "todos os dispositivos reenviados duas vezes");
        Assert.Equal(inits[1], f.Hw.MotherboardRgb.InitializationCalls);
        Assert.Equal(inits[2], f.Hw.RamRgb.InitializationCalls);
        Assert.Equal(inits[3], f.Hw.PeripheralsRgb.InitializationCalls);
    }

    [Fact]
    public void Hub_reconnect_resyncs_the_other_devices()
    {
        using var f = Fixture();
        WaitAllApplied(f);
        var calls = Devices(f).Select(d => d.Calls).ToArray();
        int connections = f.Hw.Hub.ConnectCalls;

        f.Hw.Hub.FailReads = true;                                          // hub para de responder e reconecta
        EngineFixture.WaitUntil(() => !f.Engine.GetSnapshot().HubConnected, because: "hub caiu");
        f.Hw.Hub.FailReads = false;
        EngineFixture.WaitUntil(() => f.Hw.Hub.ConnectCalls > connections && f.Engine.GetSnapshot().HubConnected, because: "hub voltou");

        EngineFixture.WaitUntil(() => Devices(f).Select((d, i) => d.Calls > calls[i]).All(x => x),
            because: "GPU, placa-mãe, RAM e periféricos ressincronizados");
    }

    [Fact]
    public void Supervisor_never_resends_more_than_the_rate_limit()
    {
        using var f = Fixture(p =>
        {
            p.RetryFirst = TimeSpan.FromMilliseconds(5);
            p.RetryMax = TimeSpan.FromMilliseconds(5);
            p.MaxResendsPerWindow = 3;
            p.ResendWindow = TimeSpan.FromMinutes(1);
        });
        WaitAllApplied(f);
        int before = f.Hw.MotherboardRgb.Calls;

        f.Hw.MotherboardRgb.FailColorWrites = true;
        SetColor(f);
        EngineFixture.WaitUntil(() => f.Hw.MotherboardRgb.Calls >= before + 4, because: "envio + 3 novas tentativas");
        Thread.Sleep(1000);                                                 // muitas voltas da fila depois
        Assert.Equal(before + 4, f.Hw.MotherboardRgb.Calls);
    }

    [Fact]
    public void Static_color_is_refreshed_after_long_silence_except_on_ram()
    {
        using var f = Fixture(p => p.StaticRefresh = TimeSpan.FromMilliseconds(150));
        WaitAllApplied(f);
        int mb = f.Hw.MotherboardRgb.Calls, gpu = f.Hw.GpuRgb.Calls, ram = f.Hw.RamRgb.Calls;
        int gpuInit = f.Hw.GpuRgb.InitializationCalls;

        EngineFixture.WaitUntil(() => f.Hw.MotherboardRgb.Calls >= mb + 2 && f.Hw.GpuRgb.Calls >= gpu + 2,
            because: "cor fixa reenviada de tempos em tempos");
        Assert.Equal(ram, f.Hw.RamRgb.Calls);                               // RAM grava configuração: não repete
        Assert.Equal(gpuInit, f.Hw.GpuRgb.InitializationCalls);
    }

    [Fact]
    public void Ram_stuck_in_slow_mode_during_an_effect_is_reinitialized_alone()
    {
        var config = new ControlConfig();
        config.Rgb.Effect = RgbEffect.Rainbow;
        using var f = Fixture(p =>
        {
            p.ReinitAfter = TimeSpan.FromMilliseconds(150);
            p.ReinitMax = TimeSpan.FromMilliseconds(300);
        }, config);
        EngineFixture.WaitUntil(() => f.Hw.RamRgb.Calls > 3, because: "efeito na RAM");
        var inits = Devices(f).Select(d => d.InitializationCalls).ToArray();

        f.Hw.RamRgb.Degraded = true;
        EngineFixture.WaitUntil(() => f.Hw.RamRgb.InitializationCalls > inits[2], because: "RAM reinicializada para voltar ao modo rápido");
        Assert.False(f.Hw.RamRgb.Degraded);
        Assert.Equal(inits[0], f.Hw.GpuRgb.InitializationCalls);
        Assert.Equal(inits[1], f.Hw.MotherboardRgb.InitializationCalls);
        Assert.Equal(inits[3], f.Hw.PeripheralsRgb.InitializationCalls);
        int frames = f.Hw.RamRgb.Calls;
        EngineFixture.WaitUntil(() => f.Hw.RamRgb.Calls > frames + 3, because: "efeito continua na RAM");
    }

    [Fact]
    public void Failing_device_skips_effect_frames_instead_of_hammering_the_bus()
    {
        var config = new ControlConfig();
        config.Rgb.Effect = RgbEffect.Rainbow;
        using var f = Fixture(p =>
        {
            p.RetryFirst = TimeSpan.FromMilliseconds(400);
            p.RetryMax = TimeSpan.FromMilliseconds(400);
        }, config);
        EngineFixture.WaitUntil(() => f.Hw.MotherboardRgb.Calls > 3, because: "efeito na placa-mãe");

        f.Hw.MotherboardRgb.FailColorWrites = true;
        int failingFrom = f.Hw.MotherboardRgb.Calls;
        int periphFrom = f.Hw.PeripheralsRgb.Calls;
        Thread.Sleep(1000);
        int attempts = f.Hw.MotherboardRgb.Calls - failingFrom;
        Assert.InRange(attempts, 1, 5);                                     // ~1 tentativa a cada 400 ms, não 1 por quadro
        Assert.True(f.Hw.PeripheralsRgb.Calls - periphFrom > attempts * 2, "os outros seguem o efeito normalmente");

        f.Hw.MotherboardRgb.FailColorWrites = false;
        int recoveredFrom = f.Hw.MotherboardRgb.Calls;
        EngineFixture.WaitUntil(() => f.Hw.MotherboardRgb.Calls > recoveredFrom + 5, because: "placa-mãe volta ao efeito");
    }
}
