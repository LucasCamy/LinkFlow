using System.Diagnostics;
using System.Globalization;

namespace LinkFlow.Core;

/// <summary>
/// Limites do supervisor de sincronismo do RGB. Os padrões partem do que os logs mostraram: a GPU recusa a cor em
/// rajadas de 0,5 a 4 s (NVAPI -1), a RAM divide o SMBus com os sensores e grava a cor fixa em configuração, e a
/// placa-mãe e o G HUB aceitam um envio a mais sem custo visível.
/// </summary>
public sealed class RgbSyncPolicy
{
    /// <summary>Primeira nova tentativa depois de um envio recusado. Dobra a cada falha seguida até <see cref="RetryMax"/>.</summary>
    public TimeSpan RetryFirst { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan RetryMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Cor fixa sem nenhum envio confirmado por este tempo: reenvia a mesma cor (o controlador pode ter voltado ao
    /// efeito dele, ex.: depois de um reset do driver). Null desliga. A RAM nunca usa (grava configuração no módulo).
    /// </summary>
    public TimeSpan? StaticRefresh { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Falhas seguidas e tempo mínimo falhando antes de reinicializar (redetectar) só aquele dispositivo.</summary>
    public int FailuresBeforeReinit { get; set; } = 4;
    public TimeSpan ReinitAfter { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Espera entre reinicializações do mesmo dispositivo: começa em <see cref="ReinitAfter"/> e dobra até isto.</summary>
    public TimeSpan ReinitMax { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Teto de reenvios feitos pelo supervisor (novas tentativas, atualizações e ressincronizações) por dispositivo.</summary>
    public int MaxResendsPerWindow { get; set; } = 6;
    public TimeSpan ResendWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Falha que dura mais que isto vira uma linha no log (as rajadas normais da GPU duram até ~4 s).</summary>
    public TimeSpan LogAfter { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Depois de voltar, o episódio só termina se o dispositivo ficar bem por este tempo (liga/desliga vira um episódio só).</summary>
    public TimeSpan StableAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Efeito animado: o dispositivo que volta vai da última cor que mostrou até a fase certa neste tempo, sem salto.</summary>
    public TimeSpan CatchUp { get; set; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Ressincronização depois da suspensão ou da reconexão do hub: espera os controladores acordarem.</summary>
    public TimeSpan ResyncDelay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A fila dos periféricos parada por mais que isto (relógio do sistema) = o PC estava suspenso.</summary>
    public TimeSpan SleepGap { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Saúde de um dispositivo RGB para o supervisor de sincronismo ("controle de erros"): última cor confirmada, falhas
/// seguidas, quando tentar de novo e quando reinicializar. Só decide: quem envia é a fila do dispositivo. Não é
/// thread-safe; cada instância pertence a uma fila. Tempos em timestamps do <see cref="Stopwatch"/>.
/// </summary>
public sealed class RgbDeviceHealth
{
    private readonly RgbSyncPolicy _policy;
    private readonly bool _periodicRefresh;
    private readonly Queue<long> _resends = new();

    private long _episodeStart;      // 0 = sem episódio
    private long _failingSince;      // início das falhas seguidas atuais
    private long _okSince;           // primeiro envio aceito depois da última falha
    private long _retryAt;
    private long _nextReinit;
    private int _episodeFailures;
    private bool _logged;
    private bool _degraded;
    private long? _resyncAt;
    private (byte R, byte G, byte B, byte Brightness)? _catchUpFrom;
    private long _catchUpStart;

    /// <param name="label">Prefixo das linhas de log, em português (ex.: "RGB da placa-mãe").</param>
    /// <param name="periodicRefresh">Reenvia a cor fixa de tempos em tempos (<see cref="RgbSyncPolicy.StaticRefresh"/>).</param>
    public RgbDeviceHealth(string label, RgbSyncPolicy policy, bool periodicRefresh = true)
    {
        Label = label;
        _policy = policy;
        _periodicRefresh = periodicRefresh;
    }

    public string Label { get; }
    public int ConsecutiveFailures { get; private set; }
    public (byte R, byte G, byte B, byte Brightness)? LastConfirmed { get; private set; }
    public long LastConfirmedAt { get; private set; }
    public long? LastRecoveryAt { get; private set; }

    /// <summary>Reinicializações no episódio atual.</summary>
    public int Reinits { get; private set; }

    /// <summary>Já respondeu alguma vez: some depois disso = vale reinicializar.</summary>
    public bool EverConfirmed => LastConfirmedAt != 0;

    public bool Failing => ConsecutiveFailures > 0;

    /// <summary>Fora de sincronismo: recusando envios ou num modo lento que atrasa o efeito.</summary>
    public bool OutOfSync => Failing || _degraded;

    /// <summary>Episódio aberto (inclui o período de observação depois de voltar).</summary>
    public bool InEpisode => _episodeStart != 0;

    /// <summary>O episódio atual já foi para o log (as linhas repetidas de cada tentativa podem ser omitidas).</summary>
    public bool EpisodeLogged => _logged;

    /// <summary>Falhando e ainda esperando a próxima tentativa: o efeito pula este dispositivo em vez de insistir a cada quadro.</summary>
    public bool HoldFrame(long now) => Failing && now < _retryAt;

    /// <summary>O supervisor quer um envio agora (nova tentativa, ressincronização ou atualização da cor fixa).</summary>
    public bool ResendDue(long now, bool animated)
    {
        if (_resyncAt is { } resync && now >= resync) return true;
        if (Failing) return now >= _retryAt && RateAllows(now);
        if (!animated && _periodicRefresh && _policy.StaticRefresh is { } refresh && EverConfirmed &&
            Stopwatch.GetElapsedTime(LastConfirmedAt, now) >= refresh)
            return RateAllows(now);
        return false;
    }

    /// <summary>Hora de reinicializar só este dispositivo: falhas seguidas demais, por tempo demais (ou modo lento).</summary>
    public bool ReinitDue(long now)
    {
        if (now < _nextReinit) return false;
        if (_degraded) return Stopwatch.GetElapsedTime(_episodeStart, now) >= _policy.ReinitAfter;
        return ConsecutiveFailures >= _policy.FailuresBeforeReinit &&
               Stopwatch.GetElapsedTime(_failingSince, now) >= _policy.ReinitAfter;
    }

    /// <summary>Quanto falta para a próxima ação do supervisor (para a fila não dormir além dela), ou null.</summary>
    public TimeSpan? UntilNextAction(long now, bool animated)
    {
        long? at = null;
        void Consider(long t) => at = at is { } a && a <= t ? a : t;
        if (_resyncAt is { } resync) Consider(resync);
        if (Failing) Consider(Math.Max(_retryAt, RateFreeAt(now)));
        else if (!animated && _periodicRefresh && _policy.StaticRefresh is { } refresh && EverConfirmed)
            Consider(Math.Max(LastConfirmedAt + Ticks(refresh), RateFreeAt(now)));
        if (at is not { } next) return null;
        return next <= now ? TimeSpan.Zero : Stopwatch.GetElapsedTime(now, next);
    }

    /// <summary>Registra um envio feito por decisão do supervisor (conta no limite de frequência).</summary>
    public void Resent(long now)
    {
        _resends.Enqueue(now);
        _resyncAt = null;
    }

    /// <summary>Cor a enviar num efeito animado: depois de uma falha, sai da última cor mostrada e chega à fase certa aos poucos.</summary>
    public (byte R, byte G, byte B, byte Brightness) Output((byte R, byte G, byte B, byte Brightness) target, long now, bool animated)
    {
        if (!animated)
        {
            _catchUpFrom = null;
            return target;
        }
        if (Failing && LastConfirmed is { } shown && _catchUpFrom == null)
        {
            _catchUpFrom = shown;
            _catchUpStart = now;
        }
        if (_catchUpFrom is not { } from) return target;
        double t = _policy.CatchUp <= TimeSpan.Zero ? 1 : Stopwatch.GetElapsedTime(_catchUpStart, now) / _policy.CatchUp;
        if (Failing) t = 0; // ainda não voltou: a próxima tentativa recomeça da cor mostrada
        if (t >= 1)
        {
            _catchUpFrom = null;
            return target;
        }
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return (Mix(from.R, target.R, t), Mix(from.G, target.G, t), Mix(from.B, target.B, t), Mix(from.Brightness, target.Brightness, t));
    }

    /// <summary>Envio aceito.</summary>
    public void Succeeded(long now, (byte R, byte G, byte B, byte Brightness) color)
    {
        if (Failing) _okSince = now;
        if (Failing && _catchUpFrom != null) _catchUpStart = now; // o realinhamento conta a partir do primeiro envio aceito
        ConsecutiveFailures = 0;
        LastConfirmed = color;
        LastConfirmedAt = now;
    }

    /// <summary>Envio recusado. Devolve a linha de log quando a falha passa a ser prolongada (uma vez por episódio).</summary>
    public string? Failed(long now)
    {
        if (!InEpisode) StartEpisode(now);
        if (ConsecutiveFailures == 0) _failingSince = now;
        ConsecutiveFailures++;
        _episodeFailures++;
        _okSince = 0;
        var delay = TimeSpan.FromTicks(Math.Min(_policy.RetryMax.Ticks, _policy.RetryFirst.Ticks << Math.Min(ConsecutiveFailures - 1, 16)));
        _retryAt = now + Ticks(delay);
        if (_logged || Stopwatch.GetElapsedTime(_episodeStart, now) < _policy.LogAfter) return null;
        _logged = true;
        return $"{Label}: fora de sincronismo há {Duration(Stopwatch.GetElapsedTime(_episodeStart, now))} " +
               $"({_episodeFailures} envio(s) recusado(s)). Reenviando com espera crescente, sem mexer nos outros dispositivos.";
    }

    /// <summary>O dispositivo responde, mas num modo lento que deixa o efeito atrasado (ex.: RAM sem quadros diretos).</summary>
    public string? Degraded(long now, bool degraded)
    {
        if (!degraded)
        {
            if (_degraded && !Failing) _okSince = now;
            _degraded = false;
            return null;
        }
        if (!_degraded)
        {
            if (!InEpisode) StartEpisode(now);
            _degraded = true;
            _okSince = 0;
        }
        if (_logged || Stopwatch.GetElapsedTime(_episodeStart, now) < _policy.LogAfter) return null;
        _logged = true;
        return $"{Label}: efeito animado atrasado há {Duration(Stopwatch.GetElapsedTime(_episodeStart, now))} (modo lento). " +
               "Tentando voltar ao modo rápido reinicializando só este dispositivo.";
    }

    /// <summary>Registra uma reinicialização e agenda a próxima com espera dobrada. Devolve a linha de log (a primeira do episódio).</summary>
    public string? Reinitialized(long now)
    {
        if (!InEpisode) StartEpisode(now);
        Reinits++;
        long wait = Math.Min(Ticks(_policy.ReinitMax), Ticks(_policy.ReinitAfter) << Math.Min(Reinits - 1, 16));
        _nextReinit = now + Math.Max(wait, 1);
        _retryAt = now; // tenta enviar logo depois de reinicializar
        if (Reinits > 1) return null;
        _logged = true;
        string why = _degraded && !Failing ? "modo lento" : $"{ConsecutiveFailures} falha(s) seguida(s)";
        return $"{Label}: {why} em {Duration(Stopwatch.GetElapsedTime(_episodeStart, now))}; reinicializando só este dispositivo.";
    }

    /// <summary>
    /// Fecha o episódio quando o dispositivo fica bem por <see cref="RgbSyncPolicy.StableAfter"/>. Devolve a linha de log
    /// da recuperação (só se a entrada foi para o log).
    /// </summary>
    public string? Poll(long now)
    {
        if (!InEpisode || OutOfSync || _okSince == 0 || Stopwatch.GetElapsedTime(_okSince, now) < _policy.StableAfter) return null;
        var outFor = Stopwatch.GetElapsedTime(_episodeStart, _okSince);
        string? line = _logged
            ? $"{Label}: sincronismo recuperado depois de {Duration(outFor)} fora ({_episodeFailures} falha(s), {Reinits} reinicialização(ões))."
            : null;
        LastRecoveryAt = _okSince;
        _episodeStart = 0;
        _episodeFailures = 0;
        _logged = false;
        _nextReinit = 0;
        Reinits = 0;
        return line;
    }

    /// <summary>Ressincronização (suspensão, hub reconectado): um envio daqui a pouco, mesmo com tudo bem, e tentativas sem a espera acumulada.</summary>
    public void RequestResync(long now)
    {
        _resyncAt = now + Ticks(_policy.ResyncDelay);
        if (Failing) _retryAt = Math.Min(_retryAt, _resyncAt.Value);
        _nextReinit = Math.Min(_nextReinit, now);
    }

    /// <summary>Dispositivo desligado na configuração (ou sumiu de vez): esquece o histórico.</summary>
    public void Reset()
    {
        ConsecutiveFailures = 0;
        _episodeStart = _failingSince = _okSince = _retryAt = _nextReinit = 0;
        _episodeFailures = Reinits = 0;
        _logged = _degraded = false;
        _resyncAt = null;
        _catchUpFrom = null;
        _resends.Clear();
    }

    private void StartEpisode(long now)
    {
        _episodeStart = now;
        _episodeFailures = 0;
        _logged = false;
        Reinits = 0;
        _nextReinit = 0;
    }

    private bool RateAllows(long now)
    {
        long window = Ticks(_policy.ResendWindow);
        while (_resends.Count > 0 && now - _resends.Peek() >= window) _resends.Dequeue();
        return _resends.Count < Math.Max(1, _policy.MaxResendsPerWindow);
    }

    private long RateFreeAt(long now) =>
        RateAllows(now) ? now : _resends.Peek() + Ticks(_policy.ResendWindow);

    private static long Ticks(TimeSpan ts) => (long)(ts.TotalSeconds * Stopwatch.Frequency);

    /// <summary>"42 s", "3 min 5 s", "2 h 10 min".</summary>
    public static string Duration(TimeSpan d)
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        if (d.TotalSeconds < 60) return $"{Math.Max(0, (int)d.TotalSeconds).ToString(pt)} s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min {d.Seconds} s";
        return $"{(int)d.TotalHours} h {d.Minutes} min";
    }
}
