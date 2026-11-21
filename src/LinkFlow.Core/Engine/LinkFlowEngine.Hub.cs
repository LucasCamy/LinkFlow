using System.Diagnostics;
using System.Globalization;

namespace LinkFlow.Core;

/// <summary>Fila do hub: telemetria do iCUE LINK, velocidades, RGB das ventoinhas e aviso de USB.</summary>
public sealed partial class LinkFlowEngine
{
    // ------------------------------------------------------------------ Fila do hub

    private async Task HubLoopAsync(CancellationToken ct)
    {
        var rgbClock = new RgbFrameClock();
        var hubRgbHealth = new RgbDeviceHealth("RGB do hub", _opt.RgbSync, periodicRefresh: false);
        int rgbInterval = RgbFrameClock.IntervalMs(_opt.HubRgbMinInterval);
        long nextRead = 0, nextReconnect = 0, lastSpeedApply = 0, lastRgbApply = 0;
        long nextDeviceCheck = Stopwatch.GetTimestamp() + Ticks(_opt.SubDeviceCheckInterval);
        long appliedRgbVersion = -1;
        long attemptedRgbVersion = -1;
        long reportedSpeedRevision = -1;
        bool? reportedSpeedProtection = null;
        bool? appliedSpeedProtection = null;
        int failures = 0;
        Dictionary<int, byte>? lastSent = null;
        var zeroReads = new Dictionary<string, int>();
        var wearReads = new Dictionary<string, int>();
        long nextConflictCheck = 0;
        long resumeGraceUntil = 0;
        var conflictProbe = _opt.ConflictProbe ?? (IsSimulated ? null : ConflictDetector.FindRunning);

        while (!ct.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            bool progressed = false;
            TimeSpan? pendingSpeedWait = null;
            try
            {
                if (conflictProbe != null && now >= nextConflictCheck)
                {
                    nextConflictCheck = now + Ticks(_opt.ConflictCheckInterval);
                    UpdateConflictAlert(conflictProbe());
                }

                // Retorno da suspensão: o USB do hub pode demorar a reaparecer. Tenta de novo a cada segundo por um
                // tempo, sem acusar hub perdido (antes: alerta a cada retorno e nova tentativa só 10 s depois).
                if (Interlocked.Exchange(ref _resumeHub, 0) == 1) resumeGraceUntil = now + Ticks(_opt.ResumeGracePeriod);
                bool inResumeGrace = now < resumeGraceUntil;

                if (Interlocked.Exchange(ref _forceReconnectHub, 0) == 1)
                {
                    // Handle inválido depois da suspensão: uma falha aqui não pode impedir a reconexão
                    if (_hub.IsConnected) SafeCall(() => { _hub.Disconnect(restoreHardwareMode: false); return true; });
                    nextReconnect = 0;
                }

                if (Interlocked.Exchange(ref _usbChangedHub, 0) == 1 && !_hub.IsConnected) nextReconnect = 0;
                if (!_hub.IsConnected && now >= nextReconnect)
                {
                    bool ok = SafeCall(() => _hub.Connect());
                    nextReconnect = now + Ticks(inResumeGrace ? _opt.ResumeReconnectInterval : _opt.ReconnectInterval);
                    if (ok)
                    {
                        failures = 0;
                        zeroReads.Clear();
                        lastSent = null;
                        appliedRgbVersion = -1;
                        attemptedRgbVersion = -1;
                        hubRgbHealth.Reset();
                        nextRead = 0;
                        bool reconnected;
                        lock (_stateLock) reconnected = _hubEverConnected;
                        resumeGraceUntil = 0;
                        OnHubConnected();
                        // O hub já recebe a cor de novo (acima); os outros dispositivos costumam ter passado pelo mesmo
                        // evento (USB reiniciado, suspensão): o supervisor ressincroniza todos
                        if (reconnected) RequestRgbResync("hub reconectado");
                    }
                    else
                    {
                        OnHubDisconnected(raiseAlert: !inResumeGrace);
                    }
                }

                if (_hub.IsConnected)
                {
                    ControlConfig cfg;
                    long rgbVersion;
                    // Configuração e versão do RGB lidas juntas: lidas em separado, uma alteração entre as duas
                    // marcaria a versão nova como aplicada com a cor antiga, e a cor nova nunca chegaria ao hub
                    lock (_configLock)
                    {
                        cfg = _config;
                        rgbVersion = Interlocked.Read(ref _rgbVersion);
                    }

                    // 1. Telemetria do hub
                    if (now >= nextRead)
                    {
                        nextRead = now + Ticks(_opt.TelemetryInterval);
                        long telemetryStarted = Stopwatch.GetTimestamp();
                        IReadOnlyDictionary<int, int>? speeds;
                        IReadOnlyDictionary<int, float>? temps;
                        try { speeds = _hub.ReadSpeeds(); temps = _hub.ReadTemperatures(); }
                        finally { History.Metrics.Duration("hub.telemetry", Stopwatch.GetElapsedTime(telemetryStarted)); }
                        ct.ThrowIfCancellationRequested();

                        if (speeds == null && temps == null) failures++;
                        else failures = 0;

                        if (failures >= _opt.HubFailuresBeforeReconnect || !_hub.IsConnected)
                        {
                            Log.Warn("Hub sem resposta: reconectando.");
                            _hub.Disconnect(restoreHardwareMode: false);
                            OnHubDisconnected(raiseAlert: !inResumeGrace);
                            nextReconnect = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(1));
                            Beat(HubLane); // The disconnect/retry policy completed; an absent device is not a stuck lane.
                            continue;
                        }

                        UpdateHubTelemetry(cfg, speeds, temps, zeroReads, wearReads);

                        // Ventoinha conectada/removida na corrente do hub (o USB do hub não muda)
                        if (now >= nextDeviceCheck)
                        {
                            nextDeviceCheck = now + Ticks(_opt.SubDeviceCheckInterval);
                            if (_hub.RefreshSubDevices())
                            {
                                Log.Info("Dispositivos do hub mudaram: atualizando a lista.");
                                OnHubConnected();
                                lastSent = null;
                            }
                        }
                    }

                    // 2. Velocidades (somente quando mudam, ou reafirmação periódica)
                    bool requestedProtection = _failSafe;
                    var desired = ComputeDesiredSpeeds(cfg, requestedProtection);
                    bool reassert = Stopwatch.GetElapsedTime(lastSpeedApply) >= _opt.SpeedReassertInterval;
                    bool speedChanged = desired.Count > 0 && (lastSent == null || reassert || !SameSpeeds(desired, lastSent));
                    bool speedDue = lastSent == null || appliedSpeedProtection != requestedProtection ||
                        Stopwatch.GetElapsedTime(lastSpeedApply) >= _opt.SpeedApplyMinInterval;
                    if (speedChanged && !speedDue)
                        pendingSpeedWait = _opt.SpeedApplyMinInterval - Stopwatch.GetElapsedTime(lastSpeedApply);
                    if (speedChanged && speedDue)
                    {
                        bool applied = _hub.SetSpeedsPercent(desired);
                        bool protectionOverride = requestedProtection;
                        ReportDeviceCommandResult("fans", cfg.Revision, applied && !protectionOverride,
                            protectionOverride ? "deferred-by-safety" : applied ? "hub-accepted;rpm-settling" : "hub-rejected");
                        reportedSpeedRevision = cfg.Revision;
                        reportedSpeedProtection = protectionOverride;
                        if (applied)
                        {
                            lastSent = desired;
                            appliedSpeedProtection = requestedProtection;
                            lastSpeedApply = Stopwatch.GetTimestamp();
                            lock (_stateLock)
                            {
                                foreach (var f in _fans)
                                {
                                    if (!desired.TryGetValue(f.Channel, out var p)) continue;
                                    if (f.AppliedPercent != p) _appliedSince[f.Channel] = Stopwatch.GetTimestamp();
                                    f.AppliedPercent = p;
                                }
                            }
                        }
                    }
                    else if (desired.Count > 0 && lastSent != null && SameSpeeds(desired, lastSent) &&
                        (reportedSpeedRevision != cfg.Revision || reportedSpeedProtection != requestedProtection))
                    {
                        bool protectionOverride = requestedProtection;
                        ReportDeviceCommandResult("fans", cfg.Revision, !protectionOverride, protectionOverride ? "deferred-by-safety" : "unchanged-confirmed-output");
                        reportedSpeedRevision = cfg.Revision;
                        reportedSpeedProtection = protectionOverride;
                    }

                    // 3. RGB do hub (limitado a ~16 atualizações/s durante arrastes e efeitos)
                    bool hubAnimated = cfg.Rgb.SyncHub && cfg.Rgb.IsAnimated;
                    long frame = rgbClock.FrameTime(RgbEffects.Now, rgbInterval);
                    bool nextRgbFrame = hubAnimated && rgbClock.IsDue(frame, rgbInterval);
                    bool rgbRetry = hubRgbHealth.Failing && hubRgbHealth.ResendDue(now, hubAnimated);
                    bool rgbNormal = !hubRgbHealth.Failing && (attemptedRgbVersion != rgbVersion || nextRgbFrame);
                    if ((!cfg.Rgb.SyncHub && appliedRgbVersion != rgbVersion || cfg.Rgb.SyncHub && (rgbNormal || rgbRetry)) &&
                        Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.HubRgbMinInterval)
                    {
                        bool changed = appliedRgbVersion != rgbVersion;
                        attemptedRgbVersion = rgbVersion;
                        lastRgbApply = Stopwatch.GetTimestamp();
                        if (cfg.Rgb.SyncHub)
                        {
                            if (rgbRetry) hubRgbHealth.Resent(lastRgbApply);
                            bool ok;
                            if (hubAnimated)
                            {
                                ok = SafeCall(() => ApplyHubFrame(cfg.Rgb, frame));
                                rgbClock.RecordFrame(frame, rgbInterval, ok ? Stopwatch.GetElapsedTime(lastRgbApply) : null);
                            }
                            else
                            {
                                var o = cfg.Rgb.OutputFor(RgbTargets.Hub);
                                ok = SafeCall(() => _hub.SetGlobalRgb(o.R, o.G, o.B, o.Brightness));
                            }
                            long confirmedAt = Stopwatch.GetTimestamp();
                            if (ok)
                            {
                                appliedRgbVersion = rgbVersion;
                                hubRgbHealth.Succeeded(confirmedAt, cfg.Rgb.OutputFor(RgbTargets.Hub, frame, 0));
                            }
                            else if (hubRgbHealth.Failed(confirmedAt) is { } line) Log.Warn(line);
                            if (changed || _rgbHubOk != ok) lock (_stateLock) _rgbHubOk = ok;
                            ReportDeviceCommandResult("rgb.hub", cfg.Revision, ok, ok ? "controller-accepted" : "hub-rejected");
                        }
                        else
                        {
                            appliedRgbVersion = rgbVersion;
                            hubRgbHealth.Reset();
                            lock (_stateLock) _rgbHubOk = null;
                        }
                    }
                    if (hubRgbHealth.Poll(Stopwatch.GetTimestamp()) is { } recoveredRgb) Log.Info(recoveredRgb);
                }
                progressed = true;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Erro na fila do hub", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            if (progressed) Beat(HubLane);

            // Espera até a próxima leitura, ou acorda antes se a configuração mudar
            var wait = _hub.IsConnected
                ? Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextRead)
                : Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextReconnect);
            if (pendingSpeedWait is { } speedWait) wait = Min(wait, speedWait);
            if (Interlocked.Read(ref _rgbVersion) != attemptedRgbVersion && _hub.IsConnected && !hubRgbHealth.Failing)
                wait = Min(wait, _opt.HubRgbMinInterval);
            if (_hub.IsConnected && !hubRgbHealth.Failing && CurrentRgb is { SyncHub: true, IsAnimated: true })
                wait = Min(wait, RgbFrameWait(rgbClock, rgbInterval, lastRgbApply, _opt.HubRgbMinInterval));
            if (_hub.IsConnected && hubRgbHealth.UntilNextAction(Stopwatch.GetTimestamp(), CurrentRgb.IsAnimated) is { } retryWait)
                wait = Min(wait, retryWait);
            await WaitAsync(_hubSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    private string? _conflict;

    private void UpdateConflictAlert(IReadOnlyList<string> programs)
    {
        string? conflict = programs.Count > 0 ? ConflictDetector.Describe(programs) : null;
        if (conflict == _conflict) return;
        _conflict = conflict;
        lock (_stateLock)
        {
            if (conflict == null)
            {
                ClearAlert("conflict");
                Log.Info("Nenhum outro programa disputando o hub.");
                return;
            }
            RaiseAlert("conflict", AlertSeverity.Warning, EngineText.Of(EngineTexts.ConflictTitle), EngineText.Of(EngineTexts.Conflict, conflict));
        }
    }

    private void OnHubConnected()
    {
        var devices = _hub.SubDevices;
        lock (_stateLock)
        {
            _hubConnected = true;
            _hubEverConnected = true;
            _hubFirmware = _hub.FirmwareVersion;
            _hubStatus = EngineText.Of(EngineTexts.HubConnected, _hub.FirmwareVersion, devices.Count);
            _fans = devices.Select(d => new FanState
            {
                Key = FanState.KeyFor(d),
                Channel = d.Channel,
                DeviceId = d.DeviceId,
                DefaultName = d.Name,
                Model = d.Model.ToString(),
                IsPump = d.IsPump,
                Rpm = d.CurrentRpm,
                RpmAvailable = false,
                RpmValidity = LinkDeviceCatalog.HasTachometer(d.Model.ToString()) ? ReadingValidity.Unavailable : ReadingValidity.NotSupported
            }).ToList();
            foreach (var key in _alerts.Keys.Where(k => k.StartsWith("pump:", StringComparison.Ordinal) &&
                         !_fans.Any(f => f.IsPump && $"pump:{f.Key}" == k)).ToArray())
                ClearAlert(key);
            ClearAlert("hub-lost");
        }
        Log.Info($"Hub conectado: FW {_hub.FirmwareVersion}, {devices.Count} dispositivos.");
    }

    /// <param name="raiseAlert">False logo depois da suspensão: o hub ainda está voltando, não foi perdido.</param>
    private void OnHubDisconnected(bool raiseAlert = true)
    {
        lock (_stateLock)
        {
            bool wasConnected = _hubConnected;
            _hubConnected = false;
            _coolant = null;
            _coolantChannel = null;
            _hubTemperatures = null;
            _hasHubSpeeds = false;
            var disconnectedAt = Stopwatch.GetTimestamp();
            var disconnectedUtc = DateTimeOffset.UtcNow;
            Acquisition("hub.rpm", "iCUE LINK").Record(false, false, "hub-disconnected", disconnectedAt, disconnectedUtc);
            Acquisition("coolant.temp", "iCUE LINK").Record(false, false, "hub-disconnected", disconnectedAt, disconnectedUtc);
            foreach (var fan in _fans)
            {
                fan.Rpm = 0;
                fan.RpmAvailable = false;
                if (fan.HasTachometer) Acquisition($"fan:{fan.Key}", "iCUE LINK").Record(false, false, "hub-disconnected", disconnectedAt, disconnectedUtc);
                fan.Temperature = null;
            }
            _failSafeRecoveryStarted = null;
            _hubStatus = _hubEverConnected
                ? EngineText.Of(EngineTexts.HubReconnecting)
                : _conflict != null
                    ? EngineText.Of(EngineTexts.HubNotFoundConflict, _conflict)
                    : EngineText.Of(EngineTexts.HubNotFound);
            if (raiseAlert && (wasConnected || _hubEverConnected))
            {
                RaiseAlert("hub-lost", AlertSeverity.Warning, EngineText.Of(EngineTexts.HubLostTitle), EngineText.Of(EngineTexts.HubLost));
            }
        }
    }

    private void UpdateHubTelemetry(ControlConfig cfg, IReadOnlyDictionary<int, int>? speeds,
        IReadOnlyDictionary<int, float>? temps, Dictionary<string, int> zeroReads, Dictionary<string, int> wearReads)
    {
        lock (_stateLock)
        {
            var pump = _fans.FirstOrDefault(f => f.IsPump);
            _hubTemperatures = temps;
            _coolant = null;
            _coolantChannel = null;
            if (pump != null && temps != null)
            {
                if (temps.TryGetValue(pump.Channel, out var pt) && pt > 0 && float.IsFinite(pt))
                {
                    _coolant = pt;
                    _coolantChannel = pump.Channel;
                }
                // A fan PCB/air sensor is not a coolant sensor. Channel numbering is now exact in the parser;
                // a missing pump temperature remains unknown rather than borrowing another device's reading.
            }
            foreach (var f in _fans)
                f.Temperature = temps != null && temps.TryGetValue(f.Channel, out var t) ? t : null;

            RecordHubAcquisition(speeds, temps);
            _hasHubSpeeds = speeds != null && _fans.Where(fan => fan.HasTachometer).All(fan => IsFresh($"fan:{fan.Key}"));
            if (_hasHubSpeeds) _hubSpeedReads++;
            if (speeds == null) return;

            foreach (var f in _fans)
            {
                f.RpmAvailable = f.HasTachometer && IsFresh($"fan:{f.Key}");
                f.Rpm = f.RpmAvailable && speeds.TryGetValue(f.Channel, out var rpm) ? rpm : 0;
                if (!f.RpmAvailable)
                {
                    zeroReads.Remove(f.Key);
                    wearReads.Remove(f.Key);
                    continue;
                }

                string name = cfg.GetFan(f.Key).Name ?? f.DefaultName;
                if (f.IsPump)
                {
                    bool low = f.Rpm < cfg.Safety.PumpMinRpm;
                    int n = low ? zeroReads.GetValueOrDefault(f.Key) + 1 : 0;
                    zeroReads[f.Key] = n;
                    if (n >= _opt.PumpLowReadsBeforeAlert && (cfg.Safety.FailSafeEnabled || cfg.Safety.FanStopAlert))
                        RaiseAlert($"pump:{f.Key}", AlertSeverity.Critical, EngineText.Of(EngineTexts.PumpLowTitle),
                            EngineText.Of(EngineTexts.PumpLow, name, f.Rpm, cfg.Safety.PumpMinRpm));
                    else ClearAlert($"pump:{f.Key}");
                }
                else
                {
                    if (!cfg.Safety.FanStopAlert)
                    {
                        zeroReads.Remove(f.Key);
                        ClearAlert($"fan:{f.Key}");
                        continue;
                    }

                    // Calibração mede justamente as paradas: alertas suspensos no canal enquanto ela roda
                    if (IsCalibrating(f.Channel))
                    {
                        zeroReads.Remove(f.Key);
                        wearReads.Remove(f.Key);
                        continue;
                    }
                    CheckFanWear(f, cfg.GetFan(f.Key).Calibration, name, wearReads);

                    // Só ventoinhas: módulos (LCD, blocos de água) e modelos desconhecidos não informam rotação
                    bool stopped = f.Rpm == 0 && f.AppliedPercent >= FanLimits.MinPercent && LinkDeviceCatalog.IsFan(f.Model);
                    int n = stopped ? zeroReads.GetValueOrDefault(f.Key) + 1 : 0;
                    zeroReads[f.Key] = n;
                    if (n >= _opt.FanStopReadsBeforeAlert)
                        RaiseAlert($"fan:{f.Key}", AlertSeverity.Warning, EngineText.Of(EngineTexts.FanStoppedTitle),
                            EngineText.Of(EngineTexts.FanStopped, name, f.AppliedPercent));
                    else if (n == 0) ClearAlert($"fan:{f.Key}");
                }
            }
        }
    }

    private byte EffectiveGlobal(ControlConfig cfg, bool? protection = null)
    {
        if (protection ?? _failSafe) return 100;
        if (cfg.Mode == FanControlMode.Curve && _curveOutput >= 0) return (byte)_curveOutput;
        return cfg.GlobalPercent;
    }

    /// <summary>Velocidade desejada por canal, considerando vínculo global, ajustes manuais, bomba e modo de segurança.</summary>
    public Dictionary<int, byte> ComputeDesiredSpeeds(ControlConfig cfg) => ComputeDesiredSpeeds(cfg, _failSafe);

    private Dictionary<int, byte> ComputeDesiredSpeeds(ControlConfig cfg, bool protection)
    {
        var result = new Dictionary<int, byte>();
        foreach (var dev in _hub.SubDevices)
        {
            if (!LinkDeviceCatalog.HasTachometer(dev.Model.ToString())) continue;
            result[dev.Channel] = DesiredPercent(cfg, FanState.KeyFor(dev), dev.IsPump, dev.Channel, protection);
        }
        return result;
    }

    private byte DesiredPercent(ControlConfig cfg, string key, bool isPump, int channel, bool? protection = null)
    {
        bool failSafe = protection ?? _failSafe;
        var ov = cfg.GetFan(key);
        byte linked = ov.Group != null && _groupOutputs.TryGetValue(ov.Group, out var output) ? (byte)output : EffectiveGlobal(cfg, failSafe);
        byte percent = failSafe ? (byte)100 : ov.Linked ? linked : ov.Percent;
        if (!failSafe && _calibrationOverride is { } calibration && calibration.TryGetValue(channel, out var measuring)) percent = measuring;
        else if (!isPump && ov.Calibration is { } cal && percent < cal.MinPercent) percent = cal.MinPercent;
        if (isPump) percent = Math.Max((byte)FanLimits.PumpMinPercent, percent);
        return Math.Clamp(percent, (byte)FanLimits.MinPercent, (byte)100);
    }

    private static bool SameSpeeds(Dictionary<int, byte> a, Dictionary<int, byte> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);



    // ------------------------------------------------------------------ Conexão de dispositivos (USB)

    private int _usbChangedHub, _usbChangedAux;

    /// <summary>
    /// Avisado pelo Windows quando algo USB/HID é conectado: hub/LCD reconectam na hora
    /// em vez de esperar a próxima tentativa (que fica mais espaçada e consome menos).
    /// </summary>
    private void WatchUsb()
    {
        try
        {
            HidSharp.DeviceList.Local.Changed += (_, _) =>
            {
                Interlocked.Exchange(ref _usbChangedHub, 1);
                Interlocked.Exchange(ref _usbChangedAux, 1);
                Signal(_hubSignal);
                Signal(_auxSignal);
                Signal(_ramSignal);
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"Aviso de USB indisponível: {ex.Message}");
        }
    }
}
