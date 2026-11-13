using System.Diagnostics;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>Aplica a iluminação LIGHTSYNC na sessão do usuário quando o engine está no serviço.</summary>
public sealed class PeripheralRgbBridge : IDisposable
{
    private const int RetryDelayMs = 10_000;
    private readonly IEngineClient _engine;
    private readonly LogitechPeripheralsRgb _device = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _lifetime = new();
    private readonly Thread _thread;
    private volatile bool _disposed;
    private int _resendRequested;
    private volatile BridgeStatus _status = new("Procurando o SDK do G HUB…", false);

    private sealed record BridgeStatus(string Text, bool HasIssue);

    public PeripheralRgbBridge(IEngineClient engine)
    {
        _engine = engine;
        _engine.AnyConfigChanged += OnConfigChanged;
        _thread = new Thread(Run) { IsBackground = true, Name = "LinkFlow Logitech", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public string Status => _status.Text;
    public bool HasIssue => _status.HasIssue;
    public bool IsAvailable => _device.IsAvailable;
    public event Action? StatusChanged;

    public void RequestResend()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _resendRequested, 1);
            _wake.Set();
        }
    }

    private void OnConfigChanged(ControlConfig config)
    {
        lock (_lifetime)
        {
            if (!_disposed) _wake.Set();
        }
    }

    private void SetStatus(string text, bool hasIssue = false)
    {
        if (_status.Text == text && _status.HasIssue == hasIssue) return;
        _status = new(text, hasIssue);
        StatusChanged?.Invoke();
    }

    private void Run()
    {
        var rgbClock = new RgbFrameClock();
        const int interval = RgbFrameClock.PeripheralIntervalMs;
        (byte R, byte G, byte B, byte Brightness)? applied = null;
        bool syncing = false;
        long nextAttempt = 0;
        try
        {
            while (!_disposed)
            {
                TimeSpan wait = Timeout.InfiniteTimeSpan;
                bool animated = false;
                try
                {
                    var rgb = _engine.Config.Rgb;
                    animated = rgb.IsAnimated;
                    bool resend = Interlocked.Exchange(ref _resendRequested, 0) != 0;
                    if (!rgb.SyncPeripherals)
                    {
                        if (syncing) _device.Disconnect();
                        syncing = false;
                        applied = null;
                        nextAttempt = 0;
                        SetStatus("Sincronização desligada • iluminação pelo G HUB.");
                    }
                    else
                    {
                        syncing = true;
                        if (resend)
                        {
                            _device.Disconnect();
                            applied = null;
                            nextAttempt = 0;
                            SetStatus("Reconectando ao SDK do G HUB…");
                        }

                        if (Environment.TickCount64 >= nextAttempt)
                        {
                            if (!_device.IsAvailable) _device.Initialize();
                            if (!_device.IsAvailable)
                            {
                                applied = null;
                                nextAttempt = Environment.TickCount64 + RetryDelayMs;
                                var diagnostic = _device.Diagnostic;
                                SetStatus(diagnostic ?? "Aguardando conexão com o SDK do G HUB…", diagnostic != null);
                            }
                            else
                            {
                                long frame = rgbClock.FrameTime(RgbEffects.Now, interval);
                                var color = rgb.OutputFor(RgbTargets.Peripherals, frame, 0);
                                TimeSpan? writeTime = null;
                                if (color != applied)
                                {
                                    long started = Stopwatch.GetTimestamp();
                                    if (_device.SetColor(color.R, color.G, color.B, color.Brightness))
                                    {
                                        writeTime = Stopwatch.GetElapsedTime(started);
                                        applied = color;
                                        SetStatus("Último envio aceito pelo G HUB.");
                                    }
                                    else
                                    {
                                        applied = null;
                                        nextAttempt = Environment.TickCount64 + RetryDelayMs;
                                        SetStatus(_device.Diagnostic ?? "O G HUB não aceitou o comando de iluminação.", hasIssue: true);
                                    }
                                }
                                if (animated) rgbClock.RecordFrame(frame, interval, writeTime);
                            }
                        }

                        long retryIn = nextAttempt - Environment.TickCount64;
                        if (retryIn > 0) wait = TimeSpan.FromMilliseconds(retryIn);
                        else if (animated) wait = rgbClock.UntilNextFrame(RgbEffects.Now, interval);
                    }
                }
                catch (Exception ex)
                {
                    applied = null;
                    nextAttempt = Environment.TickCount64 + RetryDelayMs;
                    wait = TimeSpan.FromMilliseconds(RetryDelayMs);
                    Log.Warn($"Periféricos Logitech: {ex.Message}");
                    SetStatus("A sincronização LIGHTSYNC encontrou um erro. Confira o G HUB ou tente reenviar.", hasIssue: true);
                }

                // Para cores estáticas, consolida os eventos rápidos do seletor de cor.
                if (!_disposed && _wake.WaitOne(wait) && !animated && !_disposed)
                    Thread.Sleep(100);
            }
        }
        finally
        {
            _engine.AnyConfigChanged -= OnConfigChanged;
            lock (_lifetime)
            {
                _disposed = true;
                _wake.Dispose();
            }
            _device.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            _wake.Set();
        }
        if (Thread.CurrentThread != _thread) _thread.Join(500);
    }
}
