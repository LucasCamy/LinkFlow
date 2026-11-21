using System.Diagnostics;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

/// <summary>Aplica a iluminação LIGHTSYNC na sessão interativa do Windows.</summary>
public sealed class PeripheralRgbBridge : IDisposable
{
    private const int RetryDelayMs = 10_000;
    private readonly IEngineClient _engine;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IPeripheralRgbBackend _device;
    private readonly PeripheralRgbDiagnosticRecorder _diagnostics = new();
    private readonly TaskCompletionSource _workerStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _lifetime = new();
    private readonly Thread _thread;
    private PeripheralRgbDiagnosticExporter? _diagnosticExporter;
    private volatile bool _disposed;
    private bool _syncPeripherals;
    private int _resendRequested;
    private volatile BridgeStatus _status = new(Strings.Lit_ProcurandoSDKHUB, false);

    private sealed record BridgeStatus(string Text, bool HasIssue);

    public PeripheralRgbBridge(IEngineClient engine) : this(engine,
        token => new LogitechPeripheralRgbBackend(token), Log.Info, Log.Warn) { }

    internal PeripheralRgbBridge(IEngineClient engine, Func<CancellationToken, IPeripheralRgbBackend> backendFactory,
        Action<string>? info = null, Action<string>? warn = null)
    {
        _engine = engine;
        _device = backendFactory(_shutdown.Token);
        // Fakes supply no logging sink, so isolated tests never write to the user's live logs.
        _info = info ?? (_ => { });
        _warn = warn ?? (_ => { });
        _syncPeripherals = engine.Config.Rgb.SyncPeripherals;
        _engine.AnyConfigChanged += OnConfigChanged;
        _thread = new Thread(Run) { IsBackground = true, Name = "LinkFlow Logitech", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public string Status => _status.Text;
    public bool HasIssue => _status.HasIssue;
    public bool IsAvailable => _device.IsAvailable;
    public PeripheralRgbDiagnosticSnapshot DiagnosticSnapshot => _diagnostics.Snapshot;
    public PeripheralRgbDiagnosticExportStatus DiagnosticExportStatus =>
        Volatile.Read(ref _diagnosticExporter)?.Status ?? new();
    internal Task WorkerCompletion => _workerStopped.Task;
    internal Task DiagnosticWriterCompletion => Volatile.Read(ref _diagnosticExporter)?.Completion ?? Task.CompletedTask;

    /// <summary>Opt-in current-state export. One independent worker, at most once per second; no SDK access.</summary>
    public void StartDiagnostics(string path)
    {
        lock (_lifetime)
        {
            if (_disposed || _diagnosticExporter != null) return;
            _diagnosticExporter = new(path, () => DiagnosticSnapshot);
        }
    }

    /// <summary>Diagnóstico do SDK no idioma da interface (null sem problema).</summary>
    private string? LocalizedDiagnostic => _device.Diagnostic is { } text ? EngineTextUi.Localize(_device.DiagnosticText, text) : null;
    public event Action? StatusChanged;

    public void RequestResend()
    {
        lock (_lifetime)
        {
            if (_disposed || !_engine.Config.Rgb.SyncPeripherals) return;
            Interlocked.Exchange(ref _resendRequested, 1);
            _wake.Set();
        }
    }

    private void OnConfigChanged(ControlConfig _)
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            bool enabled = _engine.Config.Rgb.SyncPeripherals;
            if (enabled && !_syncPeripherals) Interlocked.Exchange(ref _resendRequested, 1);
            _syncPeripherals = enabled;
            _wake.Set();
        }
    }

    private (byte R, byte G, byte B, byte Brightness)? ReadColor(RgbFrameClock clock, int interval)
    {
        var config = _engine.Config;
        var rgb = config.Rgb;
        long frame = clock.FrameTime(RgbEffects.Now, interval);
        (byte R, byte G, byte B, byte Brightness)? color = rgb.SyncPeripherals
            ? rgb.OutputFor(RgbTargets.Peripherals, frame, 0) : null;
        _diagnostics.Calculated(config, frame, color);
        return color;
    }

    private void SetStatus(string text, bool hasIssue = false)
    {
        if (_status.Text == text && _status.HasIssue == hasIssue) return;
        _status = new(text, hasIssue);
        _diagnostics.StatusChanged(text, hasIssue);
        if (StatusChanged is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _diagnostics.ObserverFailed(error); }
        }
    }

    private void InitializeBackend(ControlConfig config)
    {
        var operation = _diagnostics.Begin(PeripheralRgbOperationKind.Initialize, config);
        bool success = false;
        Exception? failure = null;
        try { _device.Initialize(); success = _device.IsAvailable; }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var availability = ReadBackendAvailability();
            _diagnostics.Complete(operation, success, error: failure,
                backendAvailable: availability.Available, availabilityError: availability.Error);
        }
    }

    private bool SetBackendColor(ControlConfig config, (byte R, byte G, byte B, byte Brightness) color)
    {
        var operation = _diagnostics.Begin(PeripheralRgbOperationKind.SetColor, config);
        bool success = false;
        Exception? failure = null;
        try { return success = _device.SetColor(color.R, color.G, color.B, color.Brightness); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var availability = ReadBackendAvailability();
            _diagnostics.Complete(operation, success, success ? color : null, failure, availability.Available, availability.Error);
        }
    }

    private (byte R, byte G, byte B, byte Brightness)? ResendBackend(ControlConfig config, RgbFrameClock clock, int interval)
    {
        var operation = _diagnostics.Begin(PeripheralRgbOperationKind.Resend, config);
        (byte R, byte G, byte B, byte Brightness)? color = null;
        Exception? failure = null;
        try
        {
            return color = _device.Resend(() => ReadColor(clock, interval), (attempt, total) =>
            {
                _diagnostics.ResendAttempt(attempt, total);
                SetStatus(string.Format(Strings.Lit_ReconectandoHUBTentativa, attempt, total));
            });
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            var availability = ReadBackendAvailability();
            _diagnostics.Complete(operation, color != null, color, failure, availability.Available, availability.Error);
        }
    }

    private (bool? Available, Exception? Error) ReadBackendAvailability()
    {
        try { return (_device.IsAvailable, null); }
        catch (Exception error) when (error is not OutOfMemoryException) { return (null, error); }
    }

    private void Run()
    {
        var rgbClock = new RgbFrameClock();
        const int interval = RgbFrameClock.PeripheralIntervalMs;
        (byte R, byte G, byte B, byte Brightness)? applied = null;
        bool syncing = false;
        long nextAttempt = 0;
        long nextReapply = 0;
        try
        {
            while (!_disposed)
            {
                TimeSpan wait = Timeout.InfiniteTimeSpan;
                bool animated = false;
                bool resend = false;
                try
                {
                    resend = Interlocked.Exchange(ref _resendRequested, 0) != 0;
                    var config = _engine.Config;
                    var rgb = config.Rgb;
                    _diagnostics.ObserveConfiguration(config);
                    animated = rgb.IsAnimated;
                    if (!rgb.SyncPeripherals)
                    {
                        if (syncing)
                        {
                            try { _device.Disconnect(); }
                            finally
                            {
                                var availability = ReadBackendAvailability();
                                _diagnostics.ObserveAvailability(availability.Available, availability.Error);
                            }
                        }
                        syncing = false;
                        applied = null;
                        nextAttempt = 0;
                        SetStatus(Strings.Lit_SincronizacaoDesligadaIluminacaoPelo);
                    }
                    else
                    {
                        syncing = true;
                        if (resend)
                        {
                            applied = null;
                            applied = ResendBackend(config, rgbClock, interval);
                            if (applied != null)
                            {
                                nextAttempt = 0;
                                nextReapply = Environment.TickCount64 + RetryDelayMs;
                                SetStatus(Strings.Lit_IluminacaoReenviadaPeloHUB);
                                _info("RGB Logitech: G HUB aceitou o reenvio.");
                            }
                            else if (_engine.Config.Rgb.SyncPeripherals && !_disposed)
                            {
                                nextAttempt = Environment.TickCount64 + RetryDelayMs;
                                SetStatus(LocalizedDiagnostic ?? Strings.Lit_HUBNaoAceitouReenvio, hasIssue: true);
                            }
                            if (_disposed || !_engine.Config.Rgb.SyncPeripherals) continue;
                            config = _engine.Config;
                            rgb = config.Rgb;
                            animated = rgb.IsAnimated;
                            if (animated && applied != null)
                                rgbClock.RecordFrame(rgbClock.FrameTime(RgbEffects.Now, interval), interval);
                        }

                        if (!resend && Environment.TickCount64 >= nextAttempt)
                        {
                            if (!_device.IsAvailable) InitializeBackend(config);
                            bool available = _device.IsAvailable;
                            _diagnostics.ObserveAvailability(available);
                            if (!available)
                            {
                                applied = null;
                                nextAttempt = Environment.TickCount64 + RetryDelayMs;
                                var diagnostic = LocalizedDiagnostic;
                                SetStatus(diagnostic ?? Strings.Lit_AguardandoConexaoSDKHUB, diagnostic != null);
                            }
                            else
                            {
                                config = _engine.Config;
                                rgb = config.Rgb;
                                if (!rgb.SyncPeripherals) continue;
                                animated = rgb.IsAnimated;
                                long frame = rgbClock.FrameTime(RgbEffects.Now, interval);
                                var color = rgb.OutputFor(RgbTargets.Peripherals, frame, 0);
                                _diagnostics.Calculated(config, frame, color);
                                TimeSpan? writeTime = null;
                                if (color != applied || Environment.TickCount64 >= nextReapply)
                                {
                                    long started = Stopwatch.GetTimestamp();
                                    if (SetBackendColor(config, color))
                                    {
                                        writeTime = Stopwatch.GetElapsedTime(started);
                                        if (applied == null) _info("RGB Logitech: G HUB aceitou a iluminação.");
                                        applied = color;
                                        nextReapply = Environment.TickCount64 + RetryDelayMs;
                                        SetStatus(Strings.Lit_UltimoEnvioAceitoPelo);
                                    }
                                    else
                                    {
                                        applied = null;
                                        nextAttempt = Environment.TickCount64 + RetryDelayMs;
                                        SetStatus(LocalizedDiagnostic ?? Strings.Lit_HUBNaoAceitouComando, hasIssue: true);
                                    }
                                }
                                if (animated) rgbClock.RecordFrame(frame, interval, writeTime);
                            }
                        }

                        long retryIn = nextAttempt - Environment.TickCount64;
                        if (retryIn > 0) wait = TimeSpan.FromMilliseconds(retryIn);
                        else if (animated) wait = rgbClock.UntilNextFrame(RgbEffects.Now, interval);
                        else wait = TimeSpan.FromMilliseconds(Math.Max(0, nextReapply - Environment.TickCount64));
                    }
                }
                catch (Exception ex)
                {
                    applied = null;
                    nextAttempt = Environment.TickCount64 + RetryDelayMs;
                    wait = TimeSpan.FromMilliseconds(RetryDelayMs);
                    _diagnostics.LoopFailed(ex);
                    _warn($"Periféricos Logitech: {ex.Message}");
                    SetStatus(Strings.Lit_SincronizacaoLIGHTSYNCEncontrouErro, hasIssue: true);
                }

                // Para cores estáticas, consolida os eventos rápidos do seletor de cor.
                if (!_disposed && _wake.WaitOne(wait) && !animated && !_disposed && Volatile.Read(ref _resendRequested) == 0)
                    Thread.Sleep(100);
            }
        }
        finally
        {
            _engine.AnyConfigChanged -= OnConfigChanged;
            lock (_lifetime)
            {
                _disposed = true;
                _diagnosticExporter?.Dispose();
                _wake.Dispose();
            }
            try { _device.Dispose(); }
            finally
            {
                _diagnostics.Stopped();
                _shutdown.Dispose();
                _workerStopped.TrySetResult();
            }
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            _diagnosticExporter?.Dispose();
            _shutdown.Cancel();
            _wake.Set();
        }
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }
}
