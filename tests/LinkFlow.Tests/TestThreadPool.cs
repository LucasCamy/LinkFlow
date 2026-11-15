using System.Runtime.CompilerServices;

namespace LinkFlow.Tests;

/// <summary>
/// A suíte roda vários engines simulados ao mesmo tempo no mesmo processo, e cada um tem laços próprios, além de
/// Dispose síncrono e chamadas de hardware simulado que bloqueiam threads. Com o mínimo padrão (= núcleos), o pool
/// fica sem threads e só cria novas aos poucos (cerca de uma por segundo): laços de 20 a 100 ms passam a levar segundos
/// e as esperas dos testes estouram quando a máquina está carregada. No serviço real há um único engine por processo.
/// </summary>
internal static class TestThreadPool
{
    [ModuleInitializer]
    internal static void Init()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }
}
