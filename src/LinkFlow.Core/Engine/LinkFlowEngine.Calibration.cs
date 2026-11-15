using System.Diagnostics;

namespace LinkFlow.Core;

// Calibração das ventoinhas: mede o RPM de cada uma de 100% até o mínimo do LinkFlow, descobre se alguma para em
// potência baixa (e quanto precisa para voltar a girar) e guarda o resultado em FanOverride.Calibration.
// Roda no engine (no serviço, quando instalado): a interface só inicia, cancela e mostra o progresso.
public sealed partial class LinkFlowEngine
{
    /// <summary>Potências medidas, da maior para a menor.</summary>
    internal static readonly byte[] CalibrationSweep = [100, 90, 80, 70, 60, 50, 40, 30, 25, 20];

    /// <summary>Até onde o teste de partida sobe, de 5 em 5%, procurando a potência em que a ventoinha volta a girar.</summary>
    private const byte CalibrationStartLimit = 60;

    private volatile IReadOnlyDictionary<int, byte>? _calibrationOverride;
    private CalibrationStatus? _calibrationStatus;
    private CancellationTokenSource? _calibrationCts;
    private int _calibrationRunId;

    /// <summary>Quando a potência de cada canal mudou pela última vez (com _stateLock).</summary>
    private readonly Dictionary<int, long> _appliedSince = new();

    /// <summary>RPM abaixo desta fração do calibrado, por <see cref="EngineOptions.FanWearReadsBeforeAlert"/> leituras, gera o alerta.</summary>
    internal const double FanWearAlertRatio = 0.75;

    /// <summary>Acima desta fração o alerta de desgaste some (folga para não piscar no limite).</summary>
    internal const double FanWearClearRatio = 0.85;

    /// <summary>
    /// Compara o RPM com o da calibração na mesma potência, com a rotação já estabilizada. Bem abaixo por cerca de um
    /// minuto: aviso de poeira ou rolamento gasto. Chamado com _stateLock, a cada leitura do hub.
    /// </summary>
    private void CheckFanWear(FanState f, FanCalibration? cal, string name, Dictionary<string, int> wearReads)
    {
        string key = $"fan-wear:{f.Key}";
        bool steady = _appliedSince.TryGetValue(f.Channel, out var since) && Stopwatch.GetElapsedTime(since) >= _opt.FanWearSteadyTime;
        if (cal == null || f.Rpm <= 0 || !steady || cal.ExpectedRpm(f.AppliedPercent) is not { } expected || expected <= 0)
        {
            // Sem referência agora (ex.: potência mudando): não conta nem limpa, só espera
            if (cal == null) { wearReads.Remove(f.Key); ClearAlert(key); }
            return;
        }

        double ratio = f.Rpm / (double)expected;
        if (ratio >= FanWearClearRatio)
        {
            wearReads.Remove(f.Key);
            ClearAlert(key);
            return;
        }
        if (ratio >= FanWearAlertRatio) return;

        int n = wearReads.GetValueOrDefault(f.Key) + 1;
        wearReads[f.Key] = n;
        if (n >= _opt.FanWearReadsBeforeAlert)
            RaiseAlert(key, AlertSeverity.Warning, "Ventoinha girando abaixo da calibração",
                $"{name}: {f.Rpm} RPM a {f.AppliedPercent}%, esperado cerca de {expected} RPM ({1 - ratio:P0} abaixo). " +
                "Limpe a poeira; se continuar, o rolamento pode estar gasto.");
    }

    /// <summary>Canais sendo medidos agora (alertas de parada e de desgaste ficam suspensos neles).</summary>
    private bool IsCalibrating(int channel) => _calibrationOverride?.ContainsKey(channel) == true;

    /// <summary>
    /// Inicia a calibração de todas as ventoinhas com tacômetro (a bomba fica de fora). Retorna false, com o motivo em
    /// <see cref="EngineSnapshot.Calibration"/>, se ela não puder começar agora.
    /// </summary>
    public bool StartCalibration()
    {
        lock (_stateLock)
        {
            if (_calibrationStatus?.Running == true) return false;
            int run = ++_calibrationRunId;
            string? refusal = !_hubConnected || !_hasHubSpeeds ? "O hub precisa estar conectado e informando a rotação."
                : _failSafe ? "O modo de segurança está ativo. Aguarde as temperaturas normalizarem."
                : null;
            var targets = _fans.Where(f => !f.IsPump && f.Model != nameof(LinkDeviceModel.Unknown)).Select(f => (f.Channel, f.Key)).ToList();
            refusal ??= targets.Count == 0 ? "Nenhuma ventoinha com tacômetro foi encontrada no hub." : null;
            if (refusal != null)
            {
                _calibrationStatus = new CalibrationStatus { RunId = run, Outcome = CalibrationOutcome.Aborted, Message = refusal };
                return false;
            }

            _calibrationCts?.Dispose();
            _calibrationCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _calibrationStatus = new CalibrationStatus { RunId = run, Outcome = CalibrationOutcome.Running, Message = "Preparando as ventoinhas..." };
            var ct = _calibrationCts.Token;
            Log.Info($"Calibração iniciada: {targets.Count} ventoinha(s).");
            _ = Task.Run(() => RunCalibrationAsync(targets, ct));
            return true;
        }
    }

    /// <summary>Interrompe a calibração em andamento; as ventoinhas voltam ao perfil na hora.</summary>
    public void CancelCalibration() => _calibrationCts?.Cancel();

    private async Task RunCalibrationAsync(List<(int Channel, string Key)> targets, CancellationToken ct)
    {
        var sweeps = targets.ToDictionary(t => t.Channel, _ => new List<CalibrationPoint>());
        var starts = new Dictionary<int, byte?>();
        string? abort = null;
        bool cancelled = false;
        try
        {
            // 1. Varredura descendente: todas as ventoinhas juntas, um degrau por vez
            int totalSteps = CalibrationSweep.Length + 1;
            for (int i = 0; i < CalibrationSweep.Length; i++)
            {
                byte percent = CalibrationSweep[i];
                SetCalibrationProgress(i / (double)totalSteps, percent, $"Medindo a rotação em {percent}%...");
                var rpm = await MeasureAsync(targets.ToDictionary(t => t.Channel, _ => percent), ct);
                foreach (var (channel, value) in rpm) sweeps[channel].Add(new CalibrationPoint(percent, value));
            }

            // 2. Teste de partida, só para as que pararam: sobem juntas de 5 em 5% a partir do mínimo
            var stalled = targets.Where(t => sweeps[t.Channel].Any(p => p.Rpm == 0)).Select(t => t.Channel).ToHashSet();
            if (stalled.Count > 0)
            {
                SetCalibrationProgress((totalSteps - 1) / (double)totalSteps, null, "Algumas ventoinhas pararam. Testando a partida...");
                for (byte percent = FanLimits.MinPercent; percent <= CalibrationStartLimit && stalled.Count > 0; percent += 5)
                {
                    // Fora do teste, as demais ficam no mínimo para não interferir
                    var speeds = targets.ToDictionary(t => t.Channel, t => stalled.Contains(t.Channel) ? percent : (byte)FanLimits.MinPercent);
                    SetCalibrationProgress((totalSteps - 1) / (double)totalSteps, percent, $"Testando a partida em {percent}%...");
                    var rpm = await MeasureAsync(speeds, ct);
                    foreach (int channel in stalled.ToList())
                    {
                        if (rpm.GetValueOrDefault(channel) <= 0) continue;
                        starts[channel] = percent;
                        stalled.Remove(channel);
                    }
                }
                // Não voltou a girar nem no limite do teste: o piso vai para ele (e a nota avisa)
                foreach (int channel in stalled) starts[channel] = CalibrationStartLimit;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { abort = "O LinkFlow foi encerrado durante a calibração."; }
        catch (OperationCanceledException)
        {
            abort = "Calibração cancelada.";
            cancelled = true;
        }
        catch (CalibrationAbortedException ex) { abort = ex.Message; }
        catch (Exception ex)
        {
            Log.Error("Erro na calibração", ex);
            abort = $"Erro na calibração: {ex.Message}";
        }
        finally
        {
            // Devolve as ventoinhas ao perfil antes de qualquer outra coisa
            _calibrationOverride = null;
            Signal(_hubSignal);
        }

        if (abort != null)
        {
            Log.Warn($"Calibração interrompida: {abort}");
            lock (_stateLock)
                _calibrationStatus = new CalibrationStatus
                {
                    RunId = _calibrationRunId,
                    Outcome = cancelled ? CalibrationOutcome.Cancelled : CalibrationOutcome.Aborted,
                    Message = cancelled ? "Calibração cancelada. Nada foi alterado." : $"{abort} Nada foi alterado."
                };
            return;
        }

        var now = DateTime.Now;
        var results = targets.ToDictionary(t => t.Key, t => FanCalibration.Analyze(sweeps[t.Channel], starts.GetValueOrDefault(t.Channel), now));
        UpdateConfig(c => { foreach (var (key, cal) in results) c.EditFan(key).Calibration = cal; });

        var summary = Summarize(targets, results);
        Log.Info("Calibração concluída: " + string.Join("; ", summary.Select(r => $"{r.Name} {r.MaxRpm} RPM, mín. {r.MinPercent}%{(r.Note != null ? $" ({r.Note})" : "")}")));
        lock (_stateLock)
            _calibrationStatus = new CalibrationStatus
            {
                RunId = _calibrationRunId,
                Outcome = CalibrationOutcome.Completed,
                Progress = 1,
                Message = summary.Any(r => r.Warning)
                    ? "Calibração concluída. Confira as ventoinhas marcadas."
                    : "Calibração concluída. Todas as ventoinhas responderam normalmente.",
                Results = summary
            };
    }

    private List<CalibrationFanResult> Summarize(List<(int Channel, string Key)> targets, Dictionary<string, FanCalibration> results)
    {
        ControlConfig cfg;
        lock (_configLock) cfg = _config;
        Dictionary<int, FanState> fans;
        lock (_stateLock) fans = _fans.ToDictionary(f => f.Channel);

        // Ventoinhas do mesmo modelo deveriam ter máximos parecidos: uma bem abaixo das outras merece atenção
        var medianByModel = targets
            .GroupBy(t => fans.TryGetValue(t.Channel, out var f) ? f.Model : string.Empty)
            .ToDictionary(g => g.Key, g =>
            {
                var max = g.Select(t => results[t.Key].MaxRpm).Where(r => r > 0).OrderBy(r => r).ToList();
                return max.Count >= 3 ? max[max.Count / 2] : 0;
            });

        return targets.Select(t =>
        {
            var cal = results[t.Key];
            fans.TryGetValue(t.Channel, out var f);
            string name = cfg.GetFan(t.Key).Name ?? f?.DefaultName ?? $"Canal {t.Channel}";
            int median = medianByModel.GetValueOrDefault(f?.Model ?? string.Empty);
            string? note = null;
            bool warning = false;
            if (cal.MaxRpm == 0)
            {
                note = "Não girou em nenhuma potência. Verifique o cabo e se a hélice está livre.";
                warning = true;
            }
            else if (median > 0 && cal.MaxRpm < median * 0.8)
            {
                note = $"Máximo {(1 - cal.MaxRpm / (double)median):P0} abaixo das outras do mesmo modelo. Pode ser poeira ou desgaste.";
                warning = true;
            }
            else if (cal.MinPercent > FanLimits.MinPercent)
            {
                note = cal.StartPercent is { } s
                    ? $"Parou em potência baixa e só voltou a girar em {s}%. O LinkFlow vai mantê-la em pelo menos {cal.MinPercent}%."
                    : $"O LinkFlow vai mantê-la em pelo menos {cal.MinPercent}%.";
            }
            return new CalibrationFanResult { Key = t.Key, Name = name, MaxRpm = cal.MaxRpm, MinPercent = cal.MinPercent, Note = note, Warning = warning };
        }).ToList();
    }

    /// <summary>Aplica as potências e devolve o RPM de cada canal depois que a rotação estabiliza.</summary>
    private async Task<Dictionary<int, int>> MeasureAsync(Dictionary<int, byte> speeds, CancellationToken ct)
    {
        _calibrationOverride = speeds;
        Signal(_hubSignal);

        // Espera o hub aplicar as potências
        var deadline = Stopwatch.GetTimestamp() + Ticks(_opt.CalibrationSettleMax);
        while (true)
        {
            CheckCalibrationSafety();
            bool applied;
            lock (_stateLock) applied = speeds.All(kv => _fans.FirstOrDefault(f => f.Channel == kv.Key)?.AppliedPercent == kv.Value);
            if (applied) break;
            if (Stopwatch.GetTimestamp() > deadline) throw new CalibrationAbortedException("O hub não aplicou as potências da calibração.");
            await Task.Delay(Poll, ct).ConfigureAwait(false);
        }

        // Tempo mínimo para acelerar ou desacelerar; depois, até duas leituras seguidas concordarem
        await Task.Delay(_opt.CalibrationSettle, ct).ConfigureAwait(false);
        deadline = Stopwatch.GetTimestamp() + Ticks(_opt.CalibrationSettleMax);
        Dictionary<int, int>? previous = null;
        while (true)
        {
            CheckCalibrationSafety();
            Dictionary<int, int> current;
            lock (_stateLock) current = speeds.Keys.ToDictionary(ch => ch, ch => _fans.FirstOrDefault(f => f.Channel == ch)?.Rpm ?? 0);
            bool stable = previous != null && current.All(kv =>
                Math.Abs(kv.Value - previous[kv.Key]) <= Math.Max(30, kv.Value * 0.03));
            if (stable || Stopwatch.GetTimestamp() > deadline) return current;
            previous = current;
            await Task.Delay(_opt.TelemetryInterval, ct).ConfigureAwait(false);
        }
    }

    private TimeSpan Poll => _opt.TelemetryInterval < TimeSpan.FromMilliseconds(200) ? _opt.TelemetryInterval : TimeSpan.FromMilliseconds(200);

    /// <summary>Interrompe se o PC esquentar, o modo de segurança disparar ou o hub sumir durante a calibração.</summary>
    private void CheckCalibrationSafety()
    {
        ControlConfig cfg;
        lock (_configLock) cfg = _config;
        float? cpu, gpu, coolant;
        bool hub, failSafe;
        lock (_stateLock)
        {
            cpu = _telemetry.CpuSensorAvailable ? _telemetry.CpuTemperature : null;
            gpu = _telemetry.GpuTemperature;
            coolant = _coolant;
            hub = _hubConnected && _hasHubSpeeds;
            failSafe = _failSafe;
        }
        var s = cfg.Safety;
        string? reason = failSafe ? "O modo de segurança foi acionado."
            : !hub ? "O hub deixou de responder."
            : cpu >= s.CpuWarn ? $"A CPU chegou a {cpu:0} °C."
            : gpu >= s.GpuWarn ? $"A GPU chegou a {gpu:0} °C."
            : coolant >= s.CoolantWarn ? $"O líquido chegou a {coolant:0.#} °C."
            : null;
        if (reason != null) throw new CalibrationAbortedException(reason);
    }

    private void SetCalibrationProgress(double progress, byte? percent, string message)
    {
        lock (_stateLock)
            _calibrationStatus = new CalibrationStatus
            {
                RunId = _calibrationRunId, Outcome = CalibrationOutcome.Running, Progress = Math.Clamp(progress, 0, 1), Percent = percent, Message = message
            };
    }

    private sealed class CalibrationAbortedException(string message) : Exception(message);
}
