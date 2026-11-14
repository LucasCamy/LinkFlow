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
        long nextPeripheralRetry = Stopwatch.GetTimestamp() + Ticks(TimeSpan.FromSeconds(30));
        LcdConfig? lastLcd = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
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
                        Log.Info($"RGB GPU: conexão recuperada com {_gpuRgb.Name}.");
                        appliedRgbVersion = -1;
                    }
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
                bool gpuResendDue = gpuResendRemaining > 0 && Stopwatch.GetTimestamp() >= nextGpuResend;
                bool animated = rgb.IsAnimated && HasAuxRgb(rgb);
                long frame = rgbClock.FrameTime(RgbEffects.Now, rgbInterval);
                if ((appliedRgbVersion != rgbVersion || gpuResendDue || (animated && rgbClock.IsDue(frame, rgbInterval))) &&
                    Stopwatch.GetElapsedTime(lastRgbApply) >= _opt.AuxRgbMinInterval)
                {
                    bool changed = appliedRgbVersion != rgbVersion;
                    appliedRgbVersion = rgbVersion;
                    lastRgbApply = Stopwatch.GetTimestamp();
                    bool? mbOk = rgb.SyncMotherboard && _mbRgb.IsAvailable ? SafeCall(() => Apply(_mbRgb, rgb, RgbTargets.Motherboard, frame)) : null;
                    bool? periphOk = rgb.SyncPeripherals && _peripheralsRgb.IsAvailable ? SafeCall(() => Apply(_peripheralsRgb, rgb, RgbTargets.Peripherals, frame)) : null;
                    var previousGpuOk = _rgbGpuOk;
                    var previousGpuDiagnostic = _gpuRgb.Diagnostic;
                    bool? gpuOk = rgb.SyncGpu ? previousGpuOk : null;
                    bool gpuWritten = false;
                    if (gpuResendDue)
                    {
                        int attempt = GpuRgbResendAttempts - gpuResendRemaining + 1;
                        gpuOk = ResendGpuRgb(rgbClock, rgbInterval, attempt);
                        gpuResendRemaining = gpuOk == false ? gpuResendRemaining - 1 : 0;
                        nextGpuResend = Stopwatch.GetTimestamp() + Ticks(GpuRgbResendDelay);
                        nextGpuRetry = Stopwatch.GetTimestamp() + Ticks(_opt.ReconnectInterval);
                    }
                    else if (rgb.SyncGpu && gpuResendRemaining == 0 && _gpuRgb.IsAvailable)
                    {
                        gpuOk = SafeCall(() => Apply(_gpuRgb, rgb, RgbTargets.Gpu, frame));
                        gpuWritten = gpuOk == true;
                        if (gpuOk == false && (previousGpuOk != false || previousGpuDiagnostic != _gpuRgb.Diagnostic))
                            Log.Warn($"RGB GPU: {_gpuRgb.Diagnostic ?? "O controlador recusou o envio da cor."}");
                        else if (gpuWritten && previousGpuOk != true)
                            Log.Info($"RGB GPU: cor aceita pelo controlador {_gpuRgb.Name}.");
                    }
                    if (animated)
                    {
                        var writeTime = !gpuResendDue && (gpuWritten || mbOk == true || periphOk == true)
                            ? Stopwatch.GetElapsedTime(lastRgbApply) : (TimeSpan?)null;
                        rgbClock.RecordFrame(frame, rgbInterval, writeTime);
                    }
                    lock (_stateLock)
                    {
                        _rgbGpuOk = gpuOk;
                        _rgbMbOk = mbOk;
                        _rgbPeripheralsOk = periphOk;
                    }
                    // O anel do LCD segue a cor; durante efeitos ele acompanha no ritmo normal dos quadros
                    if (rgb.SyncLcd && changed) Interlocked.Exchange(ref _lcdRequested, 1);
                }

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
            await WaitAsync(_auxSignal, Clamp(wait), ct).ConfigureAwait(false);
        }
    }

    private bool? ResendGpuRgb(RgbFrameClock clock, int interval, int attempt)
    {
        if (!CurrentRgb.SyncGpu) return null;
        bool found = SafeCall(() => { _gpuRgb.Initialize(); return _gpuRgb.IsAvailable; });
        var rgb = CurrentRgb;
        if (!rgb.SyncGpu) return null;

        bool applied = found && SafeCall(() => Apply(_gpuRgb, rgb, RgbTargets.Gpu, clock.FrameTime(RgbEffects.Now, interval)));
        if (applied) Log.Info($"RGB GPU: reenvio aceito pelo controlador {_gpuRgb.Name} (tentativa {attempt}).");
        else Log.Warn($"RGB GPU: tentativa {attempt} de {GpuRgbResendAttempts}. {_gpuRgb.Diagnostic ?? "O controlador não confirmou a cor."}");
        return applied;
    }

    /// <summary>
    /// A fila da memória usa a cadência do controlador sem atrasar os outros dispositivos.
    /// </summary>
    private async Task RamLoopAsync(CancellationToken ct)
    {
        var rgbClock = new RgbFrameClock();
        long applied = -1, lastApply = 0;
        while (!ct.IsCancellationRequested)
        {
            ControlConfig cfg;
            lock (_configLock) cfg = _config;
            bool ready;
            lock (_stateLock) ready = _rgbReady;
            var rgb = cfg.Rgb;
            long version = Interlocked.Read(ref _rgbVersion);
            bool animated = rgb.IsAnimated && rgb.SyncRam && _ramRgb.IsAvailable;
            var minInterval = animated && _ramRgb.MinAnimationInterval is { } streamingInterval
                ? Min(_opt.RamRgbMinInterval, streamingInterval)
                : _opt.RamRgbMinInterval;
            int interval = RgbFrameClock.IntervalMs(minInterval);
            long frame = rgbClock.FrameTime(RgbEffects.Now, interval);
            try
            {
                if (ready && (applied != version || (animated && rgbClock.IsDue(frame, interval))) &&
                    Stopwatch.GetElapsedTime(lastApply) >= minInterval)
                {
                    bool changed = applied != version;
                    applied = version;
                    lastApply = Stopwatch.GetTimestamp();
                    bool? ok = rgb.SyncRam && _ramRgb.IsAvailable ? SafeCall(() => Apply(_ramRgb, rgb, RgbTargets.Ram, frame)) : null;
                    if (animated) rgbClock.RecordFrame(frame, interval, ok == true ? Stopwatch.GetElapsedTime(lastApply) : null);
                    if (changed || _rgbRamOk != ok) lock (_stateLock) _rgbRamOk = ok;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"RGB da RAM: {ex.Message}");
            }

            var wait = !ready ? TimeSpan.FromMilliseconds(200)
                : applied != Interlocked.Read(ref _rgbVersion) ? minInterval - Stopwatch.GetElapsedTime(lastApply)
                : animated ? RgbFrameWait(rgbClock, interval, lastApply, minInterval)
                : TimeSpan.FromSeconds(5);
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

    private string BuildRgbStatus(RgbConfig rgb)
    {
        if (!_rgbReady) return "Detectando dispositivos RGB...";
        var ok = new List<string>();
        var fail = new List<string>();
        void Add(bool enabled, bool? r, string name)
        {
            if (!enabled) return;
            if (r == true) ok.Add(name);
            else if (r == false) fail.Add(name);
        }
        Add(rgb.SyncHub, _rgbHubOk, "Fans/Bomba");
        Add(rgb.SyncGpu, _rgbGpuOk, "GPU");
        Add(rgb.SyncMotherboard, _rgbMbOk, "Placa-mãe");
        Add(rgb.SyncRam, _rgbRamOk, "RAM DDR5");
        Add(rgb.SyncPeripherals, _rgbPeripheralsOk, "Periféricos");
        Add(rgb.SyncLcd, _rgbLcdOk, "LCD");
        if (ok.Count == 0 && fail.Count == 0) return "Nenhum dispositivo sincronizado";
        var s = ok.Count > 0 ? $"Sincronizado: {string.Join(", ", ok)}" : string.Empty;
        if (fail.Count > 0) s += (s.Length > 0 ? " • " : string.Empty) + $"Falhou: {string.Join(", ", fail)}";
        return s;
    }
}
