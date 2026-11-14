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
        int rgbInterval = RgbFrameClock.IntervalMs(_opt.HubRgbMinInterval);
        long nextRead = 0, nextReconnect = 0, lastSpeedApply = 0, lastRgbApply = 0;
        long nextDeviceCheck = Stopwatch.GetTimestamp() + Ticks(_opt.SubDeviceCheckInterval);
        long appliedRgbVersion = -1;
        int failures = 0;
        Dictionary<int, byte>? lastSent = null;
        var zeroReads = new Dictionary<string, int>();
        long nextConflictCheck = 0;
        var conflictProbe = _opt.ConflictProbe ?? (IsSimulated ? null : ConflictDetector.FindRunning);

        while (!ct.IsCancellationRequested)
        {
            Beat(HubLane);
            long now = Stopwatch.GetTimestamp();
            try
            {
                if (conflictProbe != null && now >= nextConflictCheck)
                {
                    nextConflictCheck = now + Ticks(_opt.ConflictCheckInterval);
                    UpdateConflictAlert(conflictProbe());
                }

                if (Interlocked.Exchange(ref _forceReconnectHub, 0) == 1)
                {
                    if (_hub.IsConnected) _hub.Disconnect(restoreHardwareMode: false);
                    nextReconnect = 0;
                }

                if (Interlocked.Exchange(ref _usbChangedHub, 0) == 1 && !_hub.IsConnected) nextReconnect = 0;
                if (!_hub.IsConnected && now >= nextReconnect)
                {
                    bool ok = SafeCall(() => _hub.Connect());
                    nextReconnect = now + Ticks(_opt.ReconnectInterval);
                    if (ok)
                    {
                        failures = 0;
                        zeroReads.Clear();
                        lastSent = null;
                        appliedRgbVersion = -1;
                        nextRead = 0;
                        OnHubConnected();
                    }
                    else
                    {
                        OnHubDisconnected();
                    }
                }

                if (_hub.IsConnected)
                {
                    ControlConfig cfg;
                    lock (_configLock) cfg = _config;

                    // 1. Telemetria do hub
                    if (now >= nextRead)
                    {
                        nextRead = now + Ticks(_opt.TelemetryInterval);
                        var speeds = _hub.ReadSpeeds();
                        var temps = _hub.ReadTemperatures();

                        if (speeds == null && temps == null) failures++;
                        else failures = 0;

                        if (failures >= _opt.HubFailuresBeforeReconnect || !_hub.IsConnected)
                        {
                            Log.Warn("Hub sem resposta: reconectando.");
                            _hub.Disconnect(restoreHardwareMode: false);
                            OnHubDisconnected();
                            nextReconnect = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(1));
                            continue;
                        }

                        UpdateHubTelemetry(cfg, speeds, temps, zeroReads);

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
                    var desired = ComputeDesiredSpeeds(cfg);
                    bool reassert = Stopwatch.GetElapsedTime(lastSpeedApply) >= _opt.SpeedReassertInterval;
                    if (desired.Count > 0 && (lastSent == null || reassert || !SameSpeeds(desired, lastSent)))
                    {
                        if (_hub.SetSpeedsPercent(desired))
                        {
                            lastSent = desired;
                            lastSpeedApply = Stopwatch.GetTimestamp();
                            lock (_stateLock)
                            {
                                foreach (var f in _fans)
                                    if (desired.TryGetValue(f.Channel, out var p)) f.AppliedPercent = p;
                            }
                        }
                    }

                    // 3. RGB do hub (limitado a ~16 atualizações/s durante arrastes e efeitos)
                    long rgbVersion = Interlocked.Read(ref _rgbVersion);
                    bool hubAnimated = cfg.Rgb.SyncHub && cfg.Rgb.IsAnimated;
                    long frame = rgbClock.FrameTime(RgbEffects.Now, rgbInterval);
                    bool nextRgbFrame = hubAnimated && rgbClock.IsDue(frame, rgbInterval);
                    if ((appliedRgbVersion != rgbVersion || nextRgbFrame) && Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.HubRgbMinInterval)
                    {
                        bool changed = appliedRgbVersion != rgbVersion;
                        appliedRgbVersion = rgbVersion;
                        lastRgbApply = Stopwatch.GetTimestamp();
                        if (cfg.Rgb.SyncHub)
                        {
                            bool ok;
                            if (hubAnimated)
                            {
                                ok = ApplyHubFrame(cfg.Rgb, frame);
                                rgbClock.RecordFrame(frame, rgbInterval, ok ? Stopwatch.GetElapsedTime(lastRgbApply) : null);
                            }
                            else
                            {
                                var o = cfg.Rgb.OutputFor(RgbTargets.Hub);
                                ok = _hub.SetGlobalRgb(o.R, o.G, o.B, o.Brightness);
                            }
                            if (changed || _rgbHubOk != ok) lock (_stateLock) _rgbHubOk = ok;
                        }
                        else
                        {
                            lock (_stateLock) _rgbHubOk = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Erro na fila do hub", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            // Espera até a próxima leitura, ou acorda antes se a configuração mudar
            var wait = _hub.IsConnected
                ? Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextRead)
                : Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextReconnect);
            if (Interlocked.Read(ref _rgbVersion) != appliedRgbVersion && _hub.IsConnected)
                wait = Min(wait, _opt.HubRgbMinInterval);
            if (_hub.IsConnected && CurrentRgb is { SyncHub: true, IsAnimated: true })
                wait = Min(wait, RgbFrameWait(rgbClock, rgbInterval, lastRgbApply, _opt.HubRgbMinInterval));
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
            RaiseAlert("conflict", AlertSeverity.Warning, "Outro programa controla o hub",
                $"{conflict} está em execução. Dois programas no iCUE LINK Hub embaralham os comandos (cores trocadas, " +
                "leituras de 0 RPM e modo de segurança sem motivo). Feche-o ou desative o controle do hub nele.");
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
            _hubStatus = $"Conectado • FW {_hub.FirmwareVersion} • {devices.Count} dispositivos";
            _fans = devices.Select(d => new FanState
            {
                Key = FanState.KeyFor(d),
                Channel = d.Channel,
                DeviceId = d.DeviceId,
                DefaultName = d.Name,
                Model = d.Model.ToString(),
                IsPump = d.IsPump,
                Rpm = d.CurrentRpm
            }).ToList();
            foreach (var key in _alerts.Keys.Where(k => k.StartsWith("pump:", StringComparison.Ordinal) &&
                         !_fans.Any(f => f.IsPump && $"pump:{f.Key}" == k)).ToArray())
                ClearAlert(key);
            ClearAlert("hub-lost");
        }
        Log.Info($"Hub conectado: FW {_hub.FirmwareVersion}, {devices.Count} dispositivos.");
    }

    private void OnHubDisconnected()
    {
        lock (_stateLock)
        {
            bool wasConnected = _hubConnected;
            _hubConnected = false;
            _coolant = null;
            _coolantChannel = null;
            _hubTemperatures = null;
            _hasHubSpeeds = false;
            foreach (var fan in _fans)
            {
                fan.Rpm = 0;
                fan.Temperature = null;
            }
            _failSafeRecoveryStarted = null;
            _hubStatus = _hubEverConnected
                ? "Reconectando ao iCUE LINK Hub..."
                : _conflict != null
                    ? $"iCUE LINK Hub não encontrado: feche {_conflict}"
                    : "iCUE LINK Hub não encontrado (verifique o USB ou se o iCUE está aberto)";
            if (wasConnected || _hubEverConnected)
            {
                RaiseAlert("hub-lost", AlertSeverity.Warning, "Hub desconectado",
                    "O iCUE LINK Hub parou de responder. Tentando reconectar automaticamente.");
            }
        }
    }

    private void UpdateHubTelemetry(ControlConfig cfg, IReadOnlyDictionary<int, int>? speeds,
        IReadOnlyDictionary<int, float>? temps, Dictionary<string, int> zeroReads)
    {
        lock (_stateLock)
        {
            var pump = _fans.FirstOrDefault(f => f.IsPump);
            _hubTemperatures = temps;
            _coolant = null;
            _coolantChannel = null;
            if (temps != null)
            {
                if (pump != null && temps.TryGetValue(pump.Channel, out var pt) && pt > 0 && float.IsFinite(pt))
                {
                    _coolant = pt;
                    _coolantChannel = pump.Channel;
                }
                else
                {
                    // Reserva: bomba sem leitura própria. (O caso "líquido em outro canal" vinha de um erro de
                    // leitura que deslocava as temperaturas um canal, corrigido em LinkHubProtocol.)
                    foreach (var (channel, temperature) in temps)
                    {
                        if (temperature is not (> 15 and < 80) || temperature <= _coolant) continue;
                        _coolant = temperature;
                        _coolantChannel = channel;
                    }
                }
            }
            foreach (var f in _fans)
                f.Temperature = temps != null && temps.TryGetValue(f.Channel, out var t) ? t : null;

            _hasHubSpeeds = speeds != null;
            if (speeds == null) return;

            foreach (var f in _fans)
            {
                f.Rpm = speeds.TryGetValue(f.Channel, out var rpm) ? rpm : 0;

                string name = cfg.GetFan(f.Key).Name ?? f.DefaultName;
                if (f.IsPump)
                {
                    bool low = f.Rpm < cfg.Safety.PumpMinRpm;
                    int n = low ? zeroReads.GetValueOrDefault(f.Key) + 1 : 0;
                    zeroReads[f.Key] = n;
                    if (n >= _opt.PumpLowReadsBeforeAlert && (cfg.Safety.FailSafeEnabled || cfg.Safety.FanStopAlert))
                        RaiseAlert($"pump:{f.Key}", AlertSeverity.Critical, "Bomba com rotação baixa",
                            $"{name}: {f.Rpm} RPM (mínimo {cfg.Safety.PumpMinRpm}). Verifique a bomba!");
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

                    // Dispositivos de modelo desconhecido podem não ter tacômetro (ex.: módulos sem ventoinha)
                    bool stopped = f.Rpm == 0 && f.AppliedPercent >= FanLimits.MinPercent &&
                                   f.Model != nameof(LinkDeviceModel.Unknown);
                    int n = stopped ? zeroReads.GetValueOrDefault(f.Key) + 1 : 0;
                    zeroReads[f.Key] = n;
                    if (n >= _opt.FanStopReadsBeforeAlert)
                        RaiseAlert($"fan:{f.Key}", AlertSeverity.Warning, "Ventoinha parada",
                            $"{name} está em 0 RPM com {f.AppliedPercent}% de potência.");
                    else if (n == 0) ClearAlert($"fan:{f.Key}");
                }
            }
        }
    }

    private byte EffectiveGlobal(ControlConfig cfg)
    {
        if (_failSafe) return 100;
        if (cfg.Mode == FanControlMode.Curve && _curveOutput >= 0) return (byte)_curveOutput;
        return cfg.GlobalPercent;
    }

    /// <summary>Velocidade desejada por canal, considerando vínculo global, ajustes manuais, bomba e modo de segurança.</summary>
    public Dictionary<int, byte> ComputeDesiredSpeeds(ControlConfig cfg)
    {
        var result = new Dictionary<int, byte>();
        byte global = EffectiveGlobal(cfg);
        bool failSafe = _failSafe;
        var groupOutputs = _groupOutputs;
        foreach (var dev in _hub.SubDevices)
        {
            var ov = cfg.GetFan(FanState.KeyFor(dev));
            // Vinculada: segue a curva do seu grupo (se houver) ou a potência global/curva global
            byte linked = ov.Group != null && groupOutputs.TryGetValue(ov.Group, out var go) ? (byte)go : global;
            byte p = failSafe ? (byte)100 : ov.Linked ? linked : ov.Percent;
            if (dev.IsPump) p = Math.Max((byte)FanLimits.PumpMinPercent, p);
            result[dev.Channel] = Math.Clamp(p, (byte)FanLimits.MinPercent, (byte)100);
        }
        return result;
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
