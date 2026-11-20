using System.Diagnostics;
using System.Globalization;

namespace LinkFlow.Core;

/// <summary>Filas de periféricos e memórias: RGB da GPU, placa-mãe, periféricos, RAM e quadros do LCD.</summary>
public sealed partial class LinkFlowEngine
{
    // ------------------------------------------------------------------ Fila de periféricos

    private async Task AuxLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        SafeCall(() => { _gpuRgb.Initialize(); return true; });
        SafeCall(() => { _mbRgb.Initialize(); return true; });
        SafeCall(() => { _ramRgb.Initialize(); return true; });
        SafeCall(() => { _peripheralsRgb.Initialize(); return true; });
        lock (_stateLock) _rgbReady = true;
        Log.Info($"RGB: GPU={(_gpuRgb.IsAvailable ? _gpuRgb.Name : "não detectada")} • Placa-mãe={(_mbRgb.IsAvailable ? _mbRgb.Name : "não detectada")} • RAM={(_ramRgb.IsAvailable ? _ramRgb.Name : "não detectada")} • Periféricos={(_peripheralsRgb.IsAvailable ? _peripheralsRgb.Name : "não detectados")}");
        if (_gpuRgb.Diagnostic is { } initialGpuDiagnostic) Log.Info($"RGB GPU: {initialGpuDiagnostic}");

        using var composer = new LcdComposer(_opt.MediaLibraryOnly);
        var rgbClock = new RgbFrameClock();
        int rgbInterval = RgbFrameClock.IntervalMs(_opt.AuxRgbMinInterval);
        long appliedRgbVersion = -1, lastRgbApply = 0, nextFrame = 0, nextLcdReconnect = 0;
        long nextGpuRetry = Stopwatch.GetTimestamp() + Ticks(_opt.ReconnectInterval);
        long handledGpuResendVersion = 0, nextGpuResend = 0;
        int gpuResendRemaining = 0;
        var gpuBusy = new GpuBusyEpisode();
        long nextPeripheralRetry = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(30));
        LcdConfig? lastLcd = null;

        // Supervisor de sincronismo: um estado de saúde por dispositivo desta fila (a RAM tem o dela)
        var sync = _opt.RgbSync;
        var gpuHealth = new RgbDeviceHealth("RGB GPU", sync);
        var mbHealth = new RgbDeviceHealth("RGB da placa-mãe", sync);
        var periphHealth = new RgbDeviceHealth("RGB dos periféricos", sync);
        long handledResync = Interlocked.Read(ref _rgbResyncGeneration);
        long lastTick = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // O relógio do sistema anda durante a suspensão (o desta fila não): salto grande = o PC dormiu.
                // Cobre o caso em que o aviso de energia do Windows não chega.
                long tick = Environment.TickCount64;
                if (tick - lastTick > sync.SleepGap.TotalMilliseconds)
                {
                    Interlocked.Increment(ref _rgbVersion);
                    RequestRgbResync($"fila parada por {RgbDeviceHealth.Duration(TimeSpan.FromMilliseconds(tick - lastTick))}, provável suspensão");
                }
                lastTick = tick;

                bool rescanRgb = Interlocked.Exchange(ref _rescanRgb, 0) == 1;
                ControlConfig cfg;
                long rgbVersion, gpuResendVersion;
                lock (_configLock)
                {
                    cfg = _config;
                    rgbVersion = Interlocked.Read(ref _rgbVersion);
                    gpuResendVersion = _gpuRgbResendVersion;
                }
                long now = Stopwatch.GetTimestamp();

                long resync = Interlocked.Read(ref _rgbResyncGeneration);
                if (resync != handledResync)
                {
                    handledResync = resync;
                    gpuHealth.RequestResync(now);
                    mbHealth.RequestResync(now);
                    periphHealth.RequestResync(now);
                }
                if (!cfg.Rgb.SyncGpu) gpuHealth.Reset();

                if (Interlocked.Exchange(ref _forceReconnectAux, 0) == 1)
                {
                    _lcd.Disconnect();
                    nextLcdReconnect = 0;
                }

                // G HUB costuma abrir depois do LinkFlow no logon: tenta os periféricos de novo a cada 30 s
                if (!_peripheralsRgb.IsAvailable && now >= nextPeripheralRetry)
                {
                    nextPeripheralRetry = now + Ticks(TimeSpan.FromSeconds(30));
                    if (SafeCall(() => { _peripheralsRgb.Initialize(); return _peripheralsRgb.IsAvailable; }))
                    {
                        Log.Info($"Periféricos detectados: {_peripheralsRgb.Name}");
                        appliedRgbVersion = -1;
                    }
                }

                bool newGpuResend = handledGpuResendVersion != gpuResendVersion;
                handledGpuResendVersion = gpuResendVersion;
                if (!cfg.Rgb.SyncGpu) gpuResendRemaining = 0;
                else if (newGpuResend || rescanRgb)
                {
                    gpuResendRemaining = GpuRgbResendAttempts;
                    nextGpuResend = now;
                }
                bool retryGpu = cfg.Rgb.SyncGpu && gpuResendRemaining == 0 && _gpuRgb.NeedsInitializationRetry && now >= nextGpuRetry;
                if (retryGpu)
                {
                    bool found = SafeCall(() => { _gpuRgb.Initialize(); return _gpuRgb.IsAvailable; });
                    nextGpuRetry = Stopwatch.GetTimestamp() + Ticks(_opt.ReconnectInterval);
                    if (found)
                    {
                        // Num episódio já registrado (ex.: adaptador que some e volta a cada poucos segundos), a linha
                        // de recuperação do supervisor resume tudo
                        if (!gpuHealth.EpisodeLogged) Log.Info($"RGB GPU: conexão recuperada com {_gpuRgb.Name}.");
                        appliedRgbVersion = -1;
                    }
                    else if (gpuHealth.EverConfirmed && gpuHealth.Failed(Stopwatch.GetTimestamp()) is { } line) Log.Warn(line);
                }

                if (rescanRgb)
                {
                    if (!_mbRgb.IsAvailable) SafeCall(() => { _mbRgb.Initialize(); return true; });
                    if (!_ramRgb.IsAvailable) SafeCall(() => { _ramRgb.Initialize(); return true; });
                    if (!_peripheralsRgb.IsAvailable) SafeCall(() => { _peripheralsRgb.Initialize(); return true; });
                    Interlocked.Increment(ref _rgbVersion);
                    Log.Info($"RGB redetectado: GPU={(_gpuRgb.IsAvailable ? _gpuRgb.Name : "não detectada")} • Placa-mãe={(_mbRgb.IsAvailable ? _mbRgb.Name : "não detectada")} • RAM={(_ramRgb.IsAvailable ? _ramRgb.Name : "não detectada")} • Periféricos={(_peripheralsRgb.IsAvailable ? _peripheralsRgb.Name : "não detectados")}");
                }

                // Configuração do LCD mudou: novo quadro imediatamente e libera imagens que saíram da lista
                if (!ReferenceEquals(lastLcd, cfg.Lcd))
                {
                    if (lastLcd != null) composer.Prune(cfg.Lcd);
                    lastLcd = cfg.Lcd;
                    nextFrame = 0;
                }

                // RGB da GPU, placa-mãe e periféricos (a RAM, lenta, tem fila própria)
                var rgb = cfg.Rgb;
                long supervisedAt = Stopwatch.GetTimestamp();
                if (!rgb.SyncMotherboard) mbHealth.Reset();
                if (!rgb.SyncPeripherals) periphHealth.Reset();
                bool animated = rgb.IsAnimated && HasAuxRgb(rgb);
                long frame = rgbClock.FrameTime(RgbEffects.Now, rgbInterval);
                if (!rgb.SyncGpu || !_gpuRgb.IsAvailable) gpuBusy.Reset();

                // Supervisor, GPU: recusas seguidas por tempo demais (não as rajadas curtas do barramento) → reinicializa só
                // a GPU pelo reenvio com redetecção. Adaptador perdido continua com a redetecção de sempre.
                if (rgb.SyncGpu && _gpuRgb.IsAvailable && gpuResendRemaining == 0 && gpuHealth.ReinitDue(supervisedAt))
                {
                    if (gpuHealth.Reinitialized(supervisedAt) is { } reinitLine) Log.Warn(reinitLine);
                    gpuBusy.Reset();
                    gpuResendRemaining = GpuRgbResendAttempts;
                    nextGpuResend = supervisedAt;
                }
                bool gpuResendDue = gpuResendRemaining > 0 && Stopwatch.GetTimestamp() >= nextGpuResend;
                // Cor recusada com o controlador presente: tenta de novo só a GPU (cor fixa não teria outro envio)
                bool gpuRetryDue = rgb.SyncGpu && gpuResendRemaining == 0 && gpuBusy.RetryDue(Stopwatch.GetTimestamp());
                // Supervisor: atualização da cor fixa ou ressincronização (fora de uma rajada, que tem a própria espera)
                bool gpuSupervised = rgb.SyncGpu && _gpuRgb.IsAvailable && gpuResendRemaining == 0 && gpuBusy.RetryAt == null &&
                                     gpuHealth.ResendDue(supervisedAt, animated);
                // Efeito com o barramento ocupado: a GPU pula quadros até a próxima tentativa em vez de insistir em todos
                bool gpuHeld = animated && gpuBusy.RetryAt is { } busyUntil && supervisedAt < busyUntil;
                bool mbSupervised = Supervised(mbHealth, _mbRgb, rgb.SyncMotherboard, supervisedAt, animated);
                bool periphSupervised = Supervised(periphHealth, _peripheralsRgb, rgb.SyncPeripherals, supervisedAt, animated);

                bool versionChanged = appliedRgbVersion != rgbVersion;
                bool frameDue = versionChanged || (animated && rgbClock.IsDue(frame, rgbInterval));
                bool due = frameDue || gpuResendDue;
                if ((due || gpuRetryDue || gpuSupervised || mbSupervised || periphSupervised) &&
                    Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.AuxRgbMinInterval)
                {
                    bool changed = versionChanged;
                    appliedRgbVersion = rgbVersion;
                    lastRgbApply = Stopwatch.GetTimestamp();
                    bool? previousMbOk, previousPeriphOk;
                    lock (_stateLock) (previousMbOk, previousPeriphOk) = (_rgbMbOk, _rgbPeripheralsOk);
                    bool? mbOk = SuperviseWrite(_mbRgb, mbHealth, rgb, RgbTargets.Motherboard, rgb.SyncMotherboard, frame, frameDue,
                        changed, animated, previousMbOk, out bool mbWritten);
                    bool? periphOk = SuperviseWrite(_peripheralsRgb, periphHealth, rgb, RgbTargets.Peripherals, rgb.SyncPeripherals, frame,
                        frameDue, changed, animated, previousPeriphOk, out bool periphWritten);
                    var previousGpuOk = _rgbGpuOk;
                    var previousGpuDiagnostic = _gpuRgb.Diagnostic;
                    bool? gpuOk = rgb.SyncGpu ? previousGpuOk : null;
                    bool gpuWritten = false;
                    if (gpuResendDue)
                    {
                        int attempt = GpuRgbResendAttempts - gpuResendRemaining + 1;
                        gpuOk = ResendGpuRgb(rgbClock, rgbInterval, attempt, gpuHealth);
                        gpuResendRemaining = gpuOk == false ? gpuResendRemaining - 1 : 0;
                        nextGpuResend = Stopwatch.GetTimestamp() + Ticks(GpuRgbResendDelay);
                        nextGpuRetry = Stopwatch.GetTimestamp() + Ticks(_opt.ReconnectInterval);
                        if (gpuOk == true) gpuBusy.Reset();
                    }
                    else if (rgb.SyncGpu && gpuResendRemaining == 0 && _gpuRgb.IsAvailable &&
                             ((frameDue && !gpuHeld) || gpuRetryDue || gpuSupervised))
                    {
                        long sentAt = Stopwatch.GetTimestamp();
                        var gpuColor = gpuHealth.Output(rgb.OutputFor(RgbTargets.Gpu, frame, 0), sentAt, rgb.IsAnimated);
                        gpuOk = SafeCall(() => Write(_gpuRgb, gpuColor, rgb.IsAnimated));
                        if (gpuSupervised) gpuHealth.Resent(sentAt);
                        gpuWritten = gpuOk == true;
                        long writtenAt = Stopwatch.GetTimestamp();
                        if (gpuWritten)
                        {
                            bool quiet = gpuHealth.EpisodeLogged;
                            gpuHealth.Succeeded(writtenAt, gpuColor);
                            if (gpuBusy.Recovered(writtenAt) is { } busy)
                                Log.Info($"RGB GPU: barramento I²C ocupado por {busy.Duration.TotalSeconds.ToString("0.0", CultureInfo.GetCultureInfo("pt-BR"))} s " +
                                         $"({busy.Failures} envio(s) recusado(s)); cor reenviada.");
                            else if (previousGpuOk != true && !quiet)
                                Log.Info($"RGB GPU: cor aceita pelo controlador {_gpuRgb.Name}.");
                        }
                        else if (gpuOk == false && !_gpuRgb.IsAvailable)
                        {
                            // Adaptador perdido: a redetecção cuida disso
                            bool quiet = gpuHealth.EpisodeLogged;
                            if (gpuHealth.Failed(writtenAt) is { } line) Log.Warn(line);
                            else if (!quiet && (previousGpuOk != false || previousGpuDiagnostic != _gpuRgb.Diagnostic))
                                Log.Warn($"RGB GPU: {_gpuRgb.Diagnostic ?? "O controlador recusou o envio da cor."}");
                        }
                        else if (gpuOk == false)
                        {
                            // Barramento ocupado: nova tentativa rápida com espera crescente. Se passar das rajadas
                            // normais, o supervisor registra o episódio (uma linha) e depois reinicializa só a GPU.
                            gpuBusy.Failed(writtenAt, GpuRgbBusyWarnAfter);
                            if (gpuHealth.Failed(writtenAt) is { } line) Log.Warn($"{line} {_gpuRgb.Diagnostic}".TrimEnd());
                        }
                    }
                    if (animated && due)
                    {
                        var writeTime = !gpuResendDue && (gpuWritten || (mbWritten && mbOk == true) || (periphWritten && periphOk == true))
                            ? Stopwatch.GetElapsedTime(lastRgbApply) : (TimeSpan?)null;
                        rgbClock.RecordFrame(frame, rgbInterval, writeTime);
                    }
                    lock (_stateLock)
                    {
                        _rgbGpuOk = gpuOk;
                        _rgbMbOk = mbOk;
                        _rgbPeripheralsOk = periphOk;
                        _rgbGpuRecovering = gpuHealth.OutOfSync;
                        _rgbMbRecovering = mbHealth.OutOfSync;
                        _rgbPeripheralsRecovering = periphHealth.OutOfSync;
                    }
                    // O anel do LCD segue a cor; durante efeitos ele acompanha no ritmo normal dos quadros
                    if (rgb.SyncLcd && changed) Interlocked.Exchange(ref _lcdRequested, 1);
                }

                // Episódios encerrados (dispositivo estável de novo): uma linha de recuperação, se a entrada foi registrada
                long polledAt = Stopwatch.GetTimestamp();
                foreach (var health in new[] { gpuHealth, mbHealth, periphHealth })
                    if (health.Poll(polledAt) is { } recovered) Log.Info(recovered);

                // LCD
                if (Interlocked.Exchange(ref _usbChangedAux, 0) == 1) nextLcdReconnect = 0; // algo foi conectado: tenta já
                if (!_lcd.IsConnected && now >= nextLcdReconnect)
                {
                    SafeCall(() => _lcd.Connect());
                    nextLcdReconnect = now + Ticks(_opt.ReconnectInterval);
                }
                lock (_stateLock) _lcdConnected = _lcd.IsConnected;

                bool requested = Interlocked.Exchange(ref _lcdRequested, 0) == 1;
                if (_lcd.IsConnected && (requested || (cfg.Lcd.AutoStream && now >= nextFrame)))
                {
                    var (jpeg, next) = composer.ComposeJpeg(cfg.Lcd, GetSnapshot(), RgbForLcd(cfg.Rgb));
                    // Métricas: 1 quadro/s (opção do engine); GIFs: o atraso do próprio quadro
                    bool fast = composer.IsAnimating || (cfg.Lcd.Mode == LcdMode.Media && cfg.Lcd.Media.Count > 1);
                    var lcdInterval = fast ? next : _opt.LcdFrameInterval;
                    nextFrame = Stopwatch.GetTimestamp() + Ticks(lcdInterval);
                    bool ok = _lcd.SendJpegFrame(jpeg);
                    lock (_stateLock)
                    {
                        _lcdConnected = _lcd.IsConnected;
                        _lcdStatus = composer.Status;
                        _rgbLcdOk = cfg.Rgb.SyncLcd ? ok : null;
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Erro na fila de periféricos", ex);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            ControlConfig c2;
            lock (_configLock) c2 = _config;
            var wait = c2.Lcd.AutoStream && _lcd.IsConnected
                ? Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextFrame)
                : _opt.TelemetryInterval;
            if (Interlocked.Read(ref _rgbVersion) != appliedRgbVersion) wait = Min(wait, _opt.AuxRgbMinInterval);
            if (c2.Rgb.IsAnimated && HasAuxRgb(c2.Rgb))
                wait = Min(wait, RgbFrameWait(rgbClock, rgbInterval, lastRgbApply, _opt.AuxRgbMinInterval));
            if (c2.Rgb.SyncGpu && gpuResendRemaining > 0)
            {
                var untilResend = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextGpuResend);
                var untilWrite = _opt.AuxRgbMinInterval - Stopwatch.GetElapsedTime(lastRgbApply);
                wait = Min(wait, untilResend > untilWrite ? untilResend : untilWrite);
            }
            else if (c2.Rgb.SyncGpu && _gpuRgb.NeedsInitializationRetry)
                wait = Min(wait, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextGpuRetry));
            else if (c2.Rgb.SyncGpu && gpuBusy.RetryAt is { } retryAt)
            {
                var untilRetry = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), retryAt);
                var untilWrite = _opt.AuxRgbMinInterval - Stopwatch.GetElapsedTime(lastRgbApply);
                wait = Min(wait, untilRetry > untilWrite ? untilRetry : untilWrite);
            }

            // Supervisor: acorda na próxima tentativa, atualização ou ressincronização de cada dispositivo
            long waitFrom = Stopwatch.GetTimestamp();
            bool animatedNow = c2.Rgb.IsAnimated && HasAuxRgb(c2.Rgb);
            var untilAuxWrite = _opt.AuxRgbMinInterval - Stopwatch.GetElapsedTime(lastRgbApply);
            void WakeFor(RgbDeviceHealth health, IRgbDevice device, bool enabled)
            {
                if (enabled && device.IsAvailable && health.UntilNextAction(waitFrom, animatedNow) is { } until)
                    wait = Min(wait, until > untilAuxWrite ? until : untilAuxWrite);
            }
            WakeFor(gpuHealth, _gpuRgb, c2.Rgb.SyncGpu && gpuResendRemaining == 0 && gpuBusy.RetryAt == null);
            WakeFor(mbHealth, _mbRgb, c2.Rgb.SyncMotherboard);
            WakeFor(periphHealth, _peripheralsRgb, c2.Rgb.SyncPeripherals);
            await WaitAsync(_auxSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    private bool? ResendGpuRgb(RgbFrameClock clock, int interval, int attempt, RgbDeviceHealth health)
    {
        if (!CurrentRgb.SyncGpu) return null;
        bool found = SafeCall(() => { _gpuRgb.Initialize(); return _gpuRgb.IsAvailable; });
        var rgb = CurrentRgb;
        if (!rgb.SyncGpu) return null;

        var color = rgb.OutputFor(RgbTargets.Gpu, clock.FrameTime(RgbEffects.Now, interval), 0);
        bool applied = found && SafeCall(() => Write(_gpuRgb, color, rgb.IsAnimated));
        long now = Stopwatch.GetTimestamp();
        bool quiet = health.EpisodeLogged; // episódio já no log: a linha de recuperação resume as tentativas
        if (applied)
        {
            health.Succeeded(now, color);
            if (!quiet) Log.Info($"RGB GPU: reenvio aceito pelo controlador {_gpuRgb.Name} (tentativa {attempt}).");
        }
        else if (health.Failed(now) is { } line) Log.Warn(line);
        else if (!quiet) Log.Warn($"RGB GPU: tentativa {attempt} de {GpuRgbResendAttempts}. {_gpuRgb.Diagnostic ?? "O controlador não confirmou a cor."}");
        return applied;
    }

    /// <summary>O supervisor quer mexer neste dispositivo agora (nova tentativa, atualização, ressincronização ou reinicialização).</summary>
    private static bool Supervised(RgbDeviceHealth health, IRgbDevice device, bool enabled, long now, bool animated) =>
        enabled && (device.IsAvailable
            ? health.ResendDue(now, animated) || health.ReinitDue(now)
            : health.EverConfirmed && health.ReinitDue(now));

    /// <summary>
    /// Envio de um dispositivo sob o supervisor. Quadros e mudanças de cor seguem como sempre; com o dispositivo
    /// falhando, os quadros do efeito esperam a próxima tentativa (espera crescente, limite de frequência) em vez de
    /// insistir a cada quadro. Falhas seguidas demais reinicializam só este dispositivo.
    /// </summary>
    private static bool? SuperviseWrite(IRgbDevice device, RgbDeviceHealth health, RgbConfig rgb, string target, bool enabled,
        long frame, bool frameDue, bool versionChanged, bool animated, bool? previous, out bool written)
    {
        written = false;
        if (!enabled) return null;
        long now = Stopwatch.GetTimestamp();

        if ((device.IsAvailable || health.EverConfirmed) && health.ReinitDue(now))
        {
            if (health.Reinitialized(now) is { } line) Log.Warn(line);
            SafeCall(() => { device.Initialize(); return true; });
            if (!device.IsAvailable)
            {
                if (health.Failed(Stopwatch.GetTimestamp()) is { } failedLine) Log.Warn(failedLine);
                return false;
            }
        }
        if (!device.IsAvailable) return health.EverConfirmed ? false : null;

        // Mudança de cor pedida pelo usuário vai sempre; quadros do efeito só com o dispositivo bem
        bool normal = versionChanged || (frameDue && !health.Failing);
        bool supervised = !normal && health.ResendDue(now, animated);
        if (!normal && !supervised) return previous;

        var color = health.Output(rgb.OutputFor(target, frame, 0), now, rgb.IsAnimated);
        bool ok = SafeCall(() => Write(device, color, rgb.IsAnimated));
        written = true;
        long writtenAt = Stopwatch.GetTimestamp();
        if (supervised) health.Resent(writtenAt);
        if (ok) health.Succeeded(writtenAt, color);
        else if (health.Failed(writtenAt) is { } line) Log.Warn(line);
        return ok;
    }

    /// <summary>
    /// A fila da memória usa a cadência do controlador sem atrasar os outros dispositivos.
    /// </summary>
    private async Task RamLoopAsync(CancellationToken ct)
    {
        var rgbClock = new RgbFrameClock();
        long applied = -1, lastApply = 0;
        // A cor fixa da RAM é gravada como configuração no módulo: sem atualização periódica, só novas tentativas
        var health = new RgbDeviceHealth("RGB da RAM", _opt.RgbSync, periodicRefresh: false);
        long handledResync = Interlocked.Read(ref _rgbResyncGeneration);
        while (!ct.IsCancellationRequested)
        {
            ControlConfig cfg;
            long version;
            // Lidas juntas (ver a fila do hub): senão a RAM pode ficar com a cor antiga
            lock (_configLock)
            {
                cfg = _config;
                version = Interlocked.Read(ref _rgbVersion);
            }
            bool ready;
            lock (_stateLock) ready = _rgbReady;
            var rgb = cfg.Rgb;
            bool animated = rgb.IsAnimated && rgb.SyncRam && _ramRgb.IsAvailable;
            var minInterval = animated && _ramRgb.MinAnimationInterval is { } streamingInterval
                ? Min(_opt.RamRgbMinInterval, streamingInterval)
                : _opt.RamRgbMinInterval;
            int interval = RgbFrameClock.IntervalMs(minInterval);
            long frame = rgbClock.FrameTime(RgbEffects.Now, interval);
            long now = Stopwatch.GetTimestamp();
            try
            {
                long resync = Interlocked.Read(ref _rgbResyncGeneration);
                if (resync != handledResync)
                {
                    handledResync = resync;
                    health.RequestResync(now);
                }
                if (!rgb.SyncRam) health.Reset();

                bool? reinitResult = null;
                if (ready && rgb.SyncRam)
                {
                    // Quadros diretos perdidos no meio de um efeito: a RAM fica no envio por configuração, bem mais lento,
                    // e atrasa em relação aos outros. O supervisor tenta voltar ao modo rápido reinicializando só a RAM.
                    if (_ramRgb.IsAvailable && health.Degraded(now, animated && _ramRgb.IsDegraded) is { } slowLine) Log.Warn(slowLine);
                    if ((_ramRgb.IsAvailable || health.EverConfirmed) && health.ReinitDue(now))
                    {
                        if (health.Reinitialized(now) is { } line) Log.Warn(line);
                        SafeCall(() => { _ramRgb.Initialize(); return true; });
                        if (!_ramRgb.IsAvailable)
                        {
                            if (health.Failed(Stopwatch.GetTimestamp()) is { } failedLine) Log.Warn(failedLine);
                            reinitResult = false;
                        }
                        else health.Degraded(Stopwatch.GetTimestamp(), animated && _ramRgb.IsDegraded);
                    }
                }

                bool versionChanged = applied != version;
                bool normal = versionChanged || (animated && rgbClock.IsDue(frame, interval) && !health.Failing);
                bool supervised = !normal && ready && rgb.SyncRam && _ramRgb.IsAvailable && health.ResendDue(now, animated);
                if (ready && (normal || supervised) && Stopwatch.GetElapsedTime(lastApply) >= minInterval)
                {
                    bool changed = versionChanged;
                    applied = version;
                    lastApply = Stopwatch.GetTimestamp();
                    bool? ok = null;
                    if (rgb.SyncRam && _ramRgb.IsAvailable)
                    {
                        var color = health.Output(rgb.OutputFor(RgbTargets.Ram, frame, 0), lastApply, rgb.IsAnimated);
                        ok = SafeCall(() => Write(_ramRgb, color, rgb.IsAnimated));
                        long writtenAt = Stopwatch.GetTimestamp();
                        if (supervised) health.Resent(writtenAt);
                        if (ok == true) health.Succeeded(writtenAt, color);
                        else if (health.Failed(writtenAt) is { } line) Log.Warn(line);
                    }
                    else if (rgb.SyncRam && health.EverConfirmed) ok = false;
                    if (animated) rgbClock.RecordFrame(frame, interval, ok == true ? Stopwatch.GetElapsedTime(lastApply) : null);
                    if (changed || _rgbRamOk != ok || _rgbRamRecovering != health.OutOfSync)
                        lock (_stateLock) (_rgbRamOk, _rgbRamRecovering) = (ok, health.OutOfSync);
                }
                else if (reinitResult == false) lock (_stateLock) (_rgbRamOk, _rgbRamRecovering) = (false, true);

                if (health.Poll(Stopwatch.GetTimestamp()) is { } recovered) Log.Info(recovered);
            }
            catch (Exception ex)
            {
                Log.Warn($"RGB da RAM: {ex.Message}");
            }

            var wait = !ready ? TimeSpan.FromMilliseconds(200)
                : applied != Interlocked.Read(ref _rgbVersion) ? minInterval - Stopwatch.GetElapsedTime(lastApply)
                : animated && !health.Failing ? RgbFrameWait(rgbClock, interval, lastApply, minInterval)
                : TimeSpan.FromSeconds(5);
            if (ready && rgb.SyncRam && _ramRgb.IsAvailable && health.UntilNextAction(Stopwatch.GetTimestamp(), animated) is { } untilSupervisor)
            {
                var untilWrite = minInterval - Stopwatch.GetElapsedTime(lastApply);
                wait = Min(wait, untilSupervisor > untilWrite ? untilSupervisor : untilWrite);
            }
            try { await WaitAsync(_ramSignal, Clamp(wait), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private RgbConfig CurrentRgb
    {
        get { lock (_configLock) return _config.Rgb; }
    }

    private bool HasAuxRgb(RgbConfig rgb) =>
        (rgb.SyncGpu && _gpuRgb.IsAvailable) || (rgb.SyncMotherboard && _mbRgb.IsAvailable) ||
        (rgb.SyncPeripherals && _peripheralsRgb.IsAvailable);

    private static TimeSpan RgbFrameWait(RgbFrameClock clock, int intervalMs, long lastSend, TimeSpan minInterval)
    {
        var frameWait = clock.UntilNextFrame(RgbEffects.Now, intervalMs);
        var rateWait = minInterval - Stopwatch.GetElapsedTime(lastSend);
        return frameWait > rateWait ? frameWait : rateWait;
    }

    private byte[] _hubFrame = Array.Empty<byte>();

    /// <summary>Um quadro do efeito nas ventoinhas: igual em todas, ou espalhado LED a LED pela corrente.</summary>
    private bool ApplyHubFrame(RgbConfig rgb, long frame)
    {
        if (!rgb.SpreadOnFans)
        {
            var o = rgb.OutputFor(RgbTargets.Hub, frame, 0);
            return _hub.SetGlobalRgb(o.R, o.G, o.B, o.Brightness);
        }

        int devices = Math.Max(_hub.SubDevices.Count, 1);
        int leds = Math.Max(devices * CorsairLinkHub.LedsPerDevice, 400);
        if (_hubFrame.Length != leds * 3) _hubFrame = new byte[leds * 3];
        var (_, _, _, brightness) = rgb.Finish(RgbTargets.Hub, 0, 0, 0);
        for (int i = 0; i < leds; i++)
        {
            double phase = RgbEffects.LedPhase(rgb.Effect, i, CorsairLinkHub.LedsPerDevice, devices);
            var (r, g, b, level) = RgbEffects.ColorAt(rgb, frame, phase);
            var (cr, cg, cb, _) = rgb.Finish(RgbTargets.Hub, r, g, b);
            _hubFrame[i * 3] = (byte)(cr * level);
            _hubFrame[i * 3 + 1] = (byte)(cg * level);
            _hubFrame[i * 3 + 2] = (byte)(cb * level);
        }
        return _hub.SetRgbLeds(_hubFrame, brightness);
    }

    /// <summary>O anel do LCD acompanha a cor do efeito no momento do quadro.</summary>
    private static RgbConfig RgbForLcd(RgbConfig rgb)
    {
        if (!rgb.IsAnimated) return rgb;
        var (r, g, b, _) = RgbEffects.ColorAt(rgb, RgbEffects.Now);
        return new RgbConfig { R = r, G = g, B = b, Brightness = rgb.Brightness, SyncLcd = rgb.SyncLcd };
    }

    private EngineText BuildRgbStatus(RgbConfig rgb)
    {
        if (!_rgbReady) return EngineText.Of(EngineTexts.RgbDetecting);
        var ok = new List<EngineText>();
        var fail = new List<EngineText>();
        var recovering = new List<EngineText>();
        void Add(bool enabled, bool? r, string device, bool supervised = false)
        {
            if (!enabled) return;
            if (r == true) ok.Add(EngineText.Of(device));
            else if (r == false) (supervised ? recovering : fail).Add(EngineText.Of(device));
        }
        Add(rgb.SyncHub, _rgbHubOk, EngineTexts.RgbDevHub);
        Add(rgb.SyncGpu, _rgbGpuOk, EngineTexts.RgbDevGpu, _rgbGpuRecovering);
        Add(rgb.SyncMotherboard, _rgbMbOk, EngineTexts.RgbDevMotherboard, _rgbMbRecovering);
        Add(rgb.SyncRam, _rgbRamOk, EngineTexts.RgbDevRam, _rgbRamRecovering);
        Add(rgb.SyncPeripherals, _rgbPeripheralsOk, EngineTexts.RgbDevPeripherals, _rgbPeripheralsRecovering);
        Add(rgb.SyncLcd, _rgbLcdOk, EngineTexts.RgbDevLcd);

        // Lista de tamanho variável num texto de modelo fixo: "a, b, c" vira par(par(a, b), c)
        static EngineText List(List<EngineText> items) => items.Aggregate((a, b) => EngineText.Of(EngineTexts.RgbListPair, a, b));
        if (recovering.Count > 0 && ok.Count == 0 && fail.Count == 0) return EngineText.Of(EngineTexts.RgbRecovering, List(recovering));
        var status = (ok.Count, fail.Count) switch
        {
            (0, 0) => EngineText.Of(EngineTexts.RgbNoneSynced),
            (_, 0) => EngineText.Of(EngineTexts.RgbSynced, List(ok)),
            (0, _) => EngineText.Of(EngineTexts.RgbFailed, List(fail)),
            _ => EngineText.Of(EngineTexts.RgbSyncedAndFailed, List(ok), List(fail)),
        };
        // O supervisor está reenviando com espera crescente: avisa que não precisa mexer
        return recovering.Count > 0 ? EngineText.Of(EngineTexts.RgbWithRecovering, status, List(recovering)) : status;
    }
}
