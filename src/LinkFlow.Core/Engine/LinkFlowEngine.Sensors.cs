using System.Diagnostics;
using System.Globalization;

namespace LinkFlow.Core;

/// <summary>Fila de sensores: curvas (global e por grupo), modo de segurança e fan da GPU.</summary>
public sealed partial class LinkFlowEngine
{
    // ------------------------------------------------------------------ Curvas por grupo

    private volatile Dictionary<string, int> _groupOutputs = new();

    /// <summary>Cada grupo com curva própria tem seu controlador (suavização/histerese independentes).</summary>
    private void EvaluateGroupCurves(ControlConfig cfg, Dictionary<string, (CurveController Controller, FanCurveConfig Curve)> controllers,
        float? cpu, float? gpu, float? coolant, bool newSample, float? testTemp = null)
    {
        var outputs = new Dictionary<string, int>();
        foreach (var (group, curveCfg) in cfg.GroupCurves)
        {
            if (!controllers.TryGetValue(group, out var entry) || !entry.Curve.SameAs(curveCfg))
            {
                // A configuração é substituída inteira a cada alteração (nunca editada no lugar): guardar a referência é seguro
                entry = (new CurveController(), curveCfg); // curva editada: aplica sem esperar a histerese
                controllers[group] = entry;
            }
            if (testTemp is { } test)
            {
                outputs[group] = curveCfg.Evaluate(test);
                continue;
            }
            var selection = CurveController.SelectTemperature(curveCfg, cpu, gpu, coolant);
            var temp = selection.Temperature;
            if (temp is { } t && (newSample || entry.Controller.Current == null)) outputs[group] = entry.Controller.Evaluate(curveCfg, t);
            else if (entry.Controller.Current is { } cur) outputs[group] = cur;
            if (selection.IsFallback && outputs.TryGetValue(group, out var output)) outputs[group] = Math.Max(output, curveCfg.FallbackMinPercent);
        }
        foreach (var removed in controllers.Keys.Where(k => !cfg.GroupCurves.ContainsKey(k)).ToList()) controllers.Remove(removed);

        var prev = _groupOutputs;
        if (prev.Count != outputs.Count || outputs.Any(kv => !prev.TryGetValue(kv.Key, out var v) || v != kv.Value))
        {
            _groupOutputs = outputs;
            Signal(_hubSignal);
        }
    }
    // ------------------------------------------------------------------ Fila de sensores

    private async Task SensorLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        try { _sensors.Open(); }
        catch (Exception ex) { Log.Warn($"Sensores indisponíveis: {ex.Message}"); }
        lock (_stateLock) _sensorsReady = true;

        var curve = new CurveController();
        var groupControllers = new Dictionary<string, (CurveController Controller, FanCurveConfig Curve)>();
        float? lastCpu = null, lastGpu = null, lastCoolant = null;
        var lastMode = (FanControlMode)(-1);
        long appliedGpuVersion = -1;
        bool? gpuFailSafeApplied = null;
        long nextRead = 0;
        float? lastTemp = null;
        FanCurveConfig? lastCurve = null;
        bool sensorsEverReported = false;
        long? sensorsMissingSince = null;
        long lastSensorReopen = 0;
        bool curveTestWasActive = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                ControlConfig cfg;
                long gpuVersion;
                lock (_configLock) { cfg = _config; gpuVersion = _gpuFanVersion; }

                bool safetyChanged = false;
                bool newSample = Stopwatch.GetTimestamp() >= nextRead;
                if (newSample)
                {
                    nextRead = Stopwatch.GetTimestamp() + Ticks(_opt.TelemetryInterval);
                    HardwareTelemetry t;
                    bool readOk;
                    long readStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        t = _sensors.Read();
                        ct.ThrowIfCancellationRequested();
                        lock (_stateLock)
                        {
                            _telemetry = t;
                            RecordSensorAcquisition(t);
                        }
                        // Publish critical acquisition before optional Windows/storage providers can block.
                        SuperviseSafety(null);
                        readOk = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RecordSensorFailure(ex);
                        lock (_stateLock) t = _telemetry;
                        readOk = false;
                        Log.Warn($"Aquisição de sensores falhou: {ex.Message}");
                    }
                    finally { History.Metrics.Duration("sensors.read", Stopwatch.GetElapsedTime(readStarted)); }
                    NetworkState? network = null;
                    List<VolumeState> volumes = new();
                    List<DiskIoState>? disks = null;
                    // Optional Windows/storage reads must not prevent temperature safety evaluation.
                    try { network = _stats.ReadNetwork(); volumes = _stats.ReadVolumes(); }
                    catch (Exception ex) { Log.Debug($"Estatísticas do Windows: {ex.Message}"); }
                    try { UpdateStorageDemand(); disks = _sensors.ReadDisks(); }
                    catch (Exception ex) { Log.Debug($"Leitura de discos: {ex.Message}"); }
                    float? coolant;
                    lock (_stateLock)
                    {
                        _telemetry = t;
                        _network = network;
                        _volumes = volumes;
                        _disks = disks;
                        RecordOptionalAcquisitions(network, volumes, disks);
                        coolant = FreshValue("coolant.temp", _coolant);
                        lastCpu = FreshValue("cpu.temp", t.CpuSensorAvailable ? t.CpuTemperature : null);
                        lastGpu = FreshValue("gpu.temp", t.GpuTemperature);
                    }
                    lastCoolant = coolant;

                    // O LibreHardwareMonitor às vezes para de responder (ex.: após suspender): CPU e GPU que
                    // funcionavam somem. Reabre os sensores, no máximo uma vez por minuto.
                    if (lastCpu != null || lastGpu != null)
                    {
                        sensorsEverReported = true;
                        sensorsMissingSince = null;
                    }
                    else if (sensorsEverReported)
                    {
                        long since = sensorsMissingSince ??= Stopwatch.GetTimestamp();
                        if (Stopwatch.GetElapsedTime(since) >= _opt.SensorLossTimeout &&
                            (lastSensorReopen == 0 || Stopwatch.GetElapsedTime(lastSensorReopen) >= TimeSpan.FromMinutes(1)))
                        {
                            lastSensorReopen = Stopwatch.GetTimestamp();
                            ReopenSensors();
                        }
                    }
                    var selection = CurveController.SelectTemperature(cfg.Curve, lastCpu, lastGpu, coolant);
                    lastTemp = selection.Temperature;
                    bool previousSafety = _failSafe;
                    SuperviseSafety(null);
                    safetyChanged = previousSafety != _failSafe;
                    if (readOk) Beat(SensorLane);
                }
                else if (_failSafe && !cfg.Safety.FailSafeEnabled)
                {
                    SuperviseSafety(null);
                    safetyChanged = !_failSafe;
                }

                // Curva automática
                int previousOutput = _curveOutput;
                if (safetyChanged && !_failSafe)
                {
                    curve.Reset();
                    groupControllers.Clear();
                }
                bool curveEdited = lastCurve != null && !lastCurve.SameAs(cfg.Curve);
                if (cfg.Mode != lastMode || curveEdited)
                {
                    // Mudou o modo ou a curva foi editada: aplica imediatamente, sem histerese pendente
                    float? smoothed = curve.SmoothedTemp;
                    curve.Reset();
                    if (smoothed != null && cfg.Mode == lastMode) lastTemp ??= smoothed;
                    lastMode = cfg.Mode;
                    lastCurve = cfg.Curve;
                }

                // "Testar curva": temperatura simulada, aplicada direto (sem histerese nem suavização)
                float? testTemp = ActiveCurveTest();
                if (testTemp == null && curveTestWasActive)
                {
                    // Fim do teste: volta às leituras reais na hora, sem a histerese do valor simulado
                    curve.Reset();
                    groupControllers.Clear();
                }
                curveTestWasActive = testTemp != null;

                if (cfg.Mode != FanControlMode.Curve) _curveOutput = -1;
                else if (testTemp is { } test) _curveOutput = cfg.Curve.Evaluate(test);
                else if (lastTemp is { } temp && (newSample || curve.Current == null)) _curveOutput = curve.Evaluate(cfg.Curve, temp);
                if (_curveSelection.IsFallback && _curveOutput >= 0) _curveOutput = Math.Max(_curveOutput, cfg.Curve.FallbackMinPercent);
                lock (_stateLock) _curveInputTemp = testTemp ?? curve.SmoothedTemp ?? lastTemp;
                EvaluateGroupCurves(cfg, groupControllers, lastCpu, lastGpu, lastCoolant, newSample, testTemp);
                if (safetyChanged || previousOutput != _curveOutput) Signal(_hubSignal);

                // Fan da GPU (LibreHardwareMonitor)
                bool failSafe = _failSafe;
                if (gpuFailSafeApplied != failSafe || (!failSafe && appliedGpuVersion != gpuVersion))
                {
                    bool applied = failSafe ? _sensors.SetGpuFanSpeed(100) :
                        cfg.GpuFanAuto ? _sensors.SetGpuFanAuto() : _sensors.SetGpuFanSpeed(cfg.GpuFanPercent);
                    ReportDeviceCommandResult("gpu-fan", cfg.Revision, applied && !failSafe,
                        failSafe ? "deferred-by-safety" : applied ? "controller-accepted" : "unsupported-or-failed");
                    if (applied)
                    {
                        gpuFailSafeApplied = failSafe;
                        appliedGpuVersion = gpuVersion;
                    }
                    else gpuFailSafeApplied = null;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                RecordSensorFailure(ex);
                SuperviseSafety(null);
                Log.Error("Erro na fila de sensores", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextRead);
            await WaitAsync(_sensorSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fecha e abre de novo o LibreHardwareMonitor (chamado na fila de sensores).</summary>
    private void ReopenSensors()
    {
        Log.Warn("Sensores de CPU e GPU pararam de responder: reabrindo o LibreHardwareMonitor.");
        try { _sensors.Dispose(); } catch (Exception ex) { Log.Warn($"Falha ao fechar os sensores: {ex.Message}"); }
        try { _sensors.Open(); } catch (Exception ex) { Log.Warn($"Sensores indisponíveis: {ex.Message}"); }
        // Reaplica o que dependia da instância anterior: leitura de discos e fan manual da GPU
        _storageOn = false;
        Interlocked.Increment(ref _gpuFanVersion);
        Signal(_sensorSignal);
    }

    /// <summary>A curva global (no modo Curva) ou alguma curva de grupo não tem nenhuma temperatura disponível.</summary>
    private static bool CurveInputMissing(ControlConfig cfg, float? cpu, float? gpu, float? coolant)
    {
        if (cfg.Mode == FanControlMode.Curve && CurveController.SelectTemperature(cfg.Curve, cpu, gpu, coolant).Temperature == null) return true;
        foreach (var group in cfg.GroupCurves.Values)
            if (CurveController.SelectTemperature(group, cpu, gpu, coolant).Temperature == null) return true;
        return false;
    }

    /// <param name="curveInputMissingFor">Há quanto tempo a curva está sem temperatura (null = leituras normais).</param>
    private bool EvaluateSafety(SafetyConfig s, float? cpu, float? gpu, TimeSpan? curveInputMissingFor = null)
    {
        lock (_stateLock)
        {
            float? coolant = FreshValue("coolant.temp", _coolant);
            TempAlert("cpu-temp", EngineTexts.SensorCpu, cpu, s.CpuWarn, s.CpuCritical);
            TempAlert("gpu-temp", EngineTexts.SensorGpu, gpu, s.GpuWarn, s.GpuCritical);
            if (curveInputMissingFor is { } missing && missing >= _opt.SensorLossTimeout)
            {
                RaiseAlert("sensor-lost", s.FailSafeEnabled ? AlertSeverity.Critical : AlertSeverity.Warning,
                    EngineText.Of(EngineTexts.SensorLostTitle),
                    EngineText.Of(s.FailSafeEnabled ? EngineTexts.SensorLost : EngineTexts.SensorLostNoFailSafe, missing.TotalSeconds));
            }
            else if (curveInputMissingFor == null)
            {
                ClearAlert("sensor-lost");
            }
            if (s.FailSafeEnabled && coolant >= s.CoolantCritical && _coolantChannel is { } channel)
                _failSafeCoolantChannels.Add(channel);
            bool coolantReadingsValid = true;
            foreach (int source in _failSafeCoolantChannels)
            {
                if (IsFresh($"hub.temp:{source}") && _hubTemperatures != null && _hubTemperatures.TryGetValue(source, out var temperature) &&
                    temperature > 0 && float.IsFinite(temperature))
                {
                    if (coolant == null || temperature > coolant) coolant = temperature;
                }
                else coolantReadingsValid = false;
            }
            TempAlert("coolant-temp", EngineTexts.SensorCoolant, coolant, s.CoolantWarn, s.CoolantCritical);

            EngineAlert? critical = null;
            if (s.FailSafeEnabled)
            {
                foreach (var alert in _alerts.Values)
                {
                    if (alert.Severity != AlertSeverity.Critical) continue;
                    bool isTemperature = alert.Key is "cpu-temp" or "gpu-temp" or "coolant-temp" or "sensor-lost" or "cooling-telemetry-lost";
                    if (!isTemperature && !(_hubConnected && alert.Key.StartsWith("pump:", StringComparison.Ordinal))) continue;
                    _failSafeCauses.Add(alert.Key);
                    critical ??= alert;
                }
            }

            bool wasActive = _failSafe;
            if (!s.FailSafeEnabled)
                _failSafe = false;
            else if (critical != null)
            {
                _failSafe = true;
                _failSafeRecoveryStarted = null;
                _failSafeReason = critical.MessageText;   // atualizado a cada leitura (ex.: "há 12 s")
                RaiseAlert("failsafe", AlertSeverity.Critical, EngineText.Of(EngineTexts.FailSafeOnTitle),
                    EngineText.Of(EngineTexts.FailSafeOn, critical.MessageText ?? new EngineText()));
            }
            else if (_failSafe)
            {
                // Aguarda leituras estáveis abaixo dos limites, mesmo com um aviso de temperatura ainda ativo.
                bool hasReadings = _failSafeCauses.All(cause => cause switch
                {
                    "cpu-temp" => cpu is { } cpuTemp && float.IsFinite(cpuTemp),
                    "gpu-temp" => gpu is { } gpuTemp && float.IsFinite(gpuTemp),
                    "coolant-temp" => coolantReadingsValid,
                    "sensor-lost" => curveInputMissingFor == null,
                    "cooling-telemetry-lost" => IsFresh("hub.rpm") && _hubConnected && _hasHubSpeeds,
                    _ => _hubConnected && _hasHubSpeeds && _fans.Any(f => f.IsPump && $"pump:{f.Key}" == cause &&
                                                                          f.Rpm >= s.PumpMinRpm)
                });
                if (hasReadings)
                {
                    long started = _failSafeRecoveryStarted ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(started) >= _opt.FailSafeRecoveryDelay) _failSafe = false;
                    else RaiseAlert("failsafe", AlertSeverity.Critical, EngineText.Of(EngineTexts.FailSafeActiveTitle),
                        EngineText.Of(EngineTexts.FailSafeStabilizing));
                }
                else
                {
                    _failSafeRecoveryStarted = null;
                    RaiseAlert("failsafe", AlertSeverity.Critical, EngineText.Of(EngineTexts.FailSafeActiveTitle),
                        EngineText.Of(EngineTexts.FailSafeWaitingReadings));
                }
            }

            if (_failSafe && !wasActive)
            {
                Log.Warn("Modo de segurança ATIVADO.");
            }
            else if (!_failSafe && wasActive)
            {
                ClearAlert("failsafe");
                _failSafeCauses.Clear();
                _failSafeCoolantChannels.Clear();
                _failSafeRecoveryStarted = null;
                _failSafeReason = null;
                Log.Info($"Modo de segurança desativado. {(s.FailSafeEnabled ? "Leituras abaixo dos limites críticos." : "Proteção desativada nas configurações.")}");
            }
            return _failSafe != wasActive;
        }
    }

    private void TempAlert(string key, string labelCode, float? value, float warn, float critical)
    {
        if (value is not { } v || !float.IsFinite(v))
        {
            ClearAlert(key);
            return;
        }

        if (v >= critical)
        {
            var label = EngineText.Of(labelCode);
            RaiseAlert(key, AlertSeverity.Critical, EngineText.Of(EngineTexts.TempCriticalTitle, label),
                EngineText.Of(EngineTexts.TempCritical, label, v, critical));
        }
        else if (v >= warn)
        {
            var label = EngineText.Of(labelCode);
            RaiseAlert(key, AlertSeverity.Warning, EngineText.Of(EngineTexts.TempWarnTitle, label),
                EngineText.Of(EngineTexts.TempWarn, label, v, warn));
        }
        else if (v < warn - 3 || _alerts.GetValueOrDefault(key)?.Severity == AlertSeverity.Critical)
        {
            ClearAlert(key);
        }
        else if (_alerts.GetValueOrDefault(key)?.Severity == AlertSeverity.Warning)
        {
            // Mantém a mesma histerese: o aviso só encerra abaixo de warn-3. Atualizar o alerta existente
            // mostra a leitura atual sem recriar Since, registrar outro log ou emitir outra notificação.
            var label = EngineText.Of(labelCode);
            RaiseAlert(key, AlertSeverity.Warning, EngineText.Of(EngineTexts.TempWarnRecoveringTitle, label),
                EngineText.Of(EngineTexts.TempWarnRecovering, label, v, warn, warn - 3));
        }
    }

}
