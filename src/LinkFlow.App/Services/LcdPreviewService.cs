using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>LCD preview with one pending frozen frame, rendered only while its page is active.</summary>
public sealed class LcdPreviewService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly LcdPreviewPump<BitmapSource> _pump;
    private int _disposed;

    public LcdPreviewService(MainViewModel vm, Dispatcher ui)
    {
        _vm = vm;
        _pump = new(() => new BitmapRenderer(vm), action =>
        {
            if (ui.HasShutdownStarted || ui.HasShutdownFinished) throw new OperationCanceledException("Preview dispatcher stopped.");
            ui.BeginInvoke(DispatcherPriority.Background, action);
        }, image => vm.LcdPreview = image);
        _vm.LcdConfigChanged += OnConfigChanged;
    }

    public bool Active { get => _pump.Active; set => _pump.Active = value; }
    public void RenderNow() => _pump.Invalidate();
    private void OnConfigChanged() => _pump.Invalidate();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _vm.LcdConfigChanged -= OnConfigChanged;
        _pump.Dispose();
    }

    private sealed class BitmapRenderer(MainViewModel vm) : ILcdPreviewRenderer<BitmapSource>
    {
        private readonly LcdComposer _composer = new();
        private LcdConfig? _lastConfig;
        public (BitmapSource Frame, TimeSpan Delay) Render()
        {
            var cfg = vm.Engine.Config;
            if (!ReferenceEquals(_lastConfig, cfg.Lcd))
            {
                _composer.Prune(cfg.Lcd);
                _lastConfig = cfg.Lcd;
            }
            int pin = vm.LcdPreviewPin;
            var next = _composer.Compose(cfg.Lcd, vm.Snapshot, cfg.Rgb, applyRotation: false, pinItem: pin >= 0 ? pin : null);
            return (ToBitmapSource(_composer.Canvas), _composer.IsAnimating ? next : TimeSpan.FromSeconds(1));
        }
        public void Dispose() => _composer.Dispose();
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var image = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            image.Freeze();
            return image;
        }
        finally { bmp.UnlockBits(data); }
    }
}

/// <summary>Tests supply only an in-memory renderer and callback queue, without WPF/GDI or devices.</summary>
internal interface ILcdPreviewRenderer<T> : IDisposable where T : class
{
    (T Frame, TimeSpan Delay) Render();
}

internal sealed class LcdPreviewPump<T> : IDisposable where T : class
{
    private readonly object _gate = new(), _wakeGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Func<ILcdPreviewRenderer<T>> _createRenderer;
    private readonly Action<Action> _schedule;
    private readonly Action<T> _publish;
    private readonly Action<T>? _release;
    private readonly Action<Exception> _report;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private T? _pending;
    private bool _queued, _wakeClosed;
    private int _disposed, _active;
    private long _revision;

    internal LcdPreviewPump(Func<ILcdPreviewRenderer<T>> createRenderer, Action<Action> schedule, Action<T> publish,
        Action<T>? release = null, Action<Exception>? report = null)
    {
        _createRenderer = createRenderer;
        _schedule = schedule;
        _publish = publish;
        _release = release;
        _report = report ?? (ex => Log.Warn($"Prévia do LCD: {ex.Message}"));
        new Thread(Run) { IsBackground = true, Name = "LinkFlow LCD preview", Priority = ThreadPriority.BelowNormal }.Start();
    }

    internal Task Completion => _completion.Task;
    internal bool HasPendingFrame { get { lock (_gate) return _pending != null; } }
    internal bool Active
    {
        get => Volatile.Read(ref _active) != 0;
        set
        {
            lock (_gate)
            {
                if (_disposed != 0 || Active == value) return;
                Interlocked.Increment(ref _revision);
                // Publish activation only after its generation, so the worker cannot render an old epoch.
                Volatile.Write(ref _active, value ? 1 : 0);
                if (!value) DropPending();
            }
            Wake();
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            Interlocked.Increment(ref _revision);
            DropPending();
        }
        Wake();
    }

    private void Run()
    {
        ILcdPreviewRenderer<T>? renderer = null;
        long renderedRevision = -1, nextRender = 0;
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                if (!Active)
                {
                    DisposeRenderer();
                    nextRender = 0;
                    _wake.WaitOne();
                    continue;
                }
                long revision = Interlocked.Read(ref _revision);
                if (revision != renderedRevision) nextRender = 0;
                long now = Stopwatch.GetTimestamp();
                if (nextRender > now)
                {
                    _wake.WaitOne(Stopwatch.GetElapsedTime(now, nextRender));
                    continue;
                }
                if (HasPendingFrame)
                {
                    // A busy dispatcher needs neither more composition nor more copied pixel buffers.
                    _wake.WaitOne();
                    continue;
                }
                lock (_gate)
                {
                    // Deactivation may have discarded the pending frame after the earlier active check.
                    if (_disposed != 0 || !Active) continue;
                    revision = Interlocked.Read(ref _revision);
                }
                try
                {
                    renderer ??= _createRenderer();
                    var (frame, delay) = renderer.Render();
                    renderedRevision = revision;
                    double seconds = Math.Clamp(delay.TotalSeconds, .04, TimeSpan.FromDays(1).TotalSeconds);
                    nextRender = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);
                    Queue(frame, revision);
                }
                catch (Exception ex)
                {
                    if (Volatile.Read(ref _disposed) == 0) _report(ex);
                    _wake.WaitOne(TimeSpan.FromSeconds(2));
                }
            }
        }
        finally
        {
            DisposeRenderer();
            lock (_gate) DropPending();
            lock (_wakeGate) { _wakeClosed = true; _wake.Dispose(); }
            _completion.TrySetResult();
        }

        void DisposeRenderer()
        {
            var previous = renderer;
            renderer = null;
            try { previous?.Dispose(); }
            catch (Exception ex) { _report(ex); }
        }
    }

    private void Queue(T frame, long revision)
    {
        bool schedule = false;
        lock (_gate)
        {
            if (_disposed != 0 || !Active || revision != Interlocked.Read(ref _revision))
            { Release(frame); return; }
            DropPending();
            _pending = frame;
            if (!_queued) { _queued = true; schedule = true; }
        }
        if (!schedule) return;
        try { _schedule(Drain); }
        catch
        {
            lock (_gate) { _queued = false; DropPending(); }
            throw;
        }
    }

    private void Drain()
    {
        bool schedule = false;
        try
        {
            lock (_gate)
            {
                var frame = _pending;
                _pending = null;
                try
                {
                    // Serializes with deactivation/disposal: no publication can start after either returns.
                    if (frame != null && _disposed == 0 && Active) _publish(frame);
                }
                finally
                {
                    if (frame != null) Release(frame);
                    _queued = false;
                    if (_pending != null && _disposed == 0 && Active) { _queued = true; schedule = true; }
                }
            }
        }
        finally { Wake(); }
        if (schedule)
        {
            try { _schedule(Drain); }
            catch { lock (_gate) { _queued = false; DropPending(); } }
        }
    }

    // BitmapSource is not IDisposable: removing pending references lets WPF/GC reclaim those copies.
    // The view model retains only the displayed image. A release spy is supplied by in-memory tests.
    private void DropPending() { var frame = _pending; _pending = null; if (frame != null) Release(frame); }
    private void Release(T frame) => _release?.Invoke(frame);
    private void Wake() { lock (_wakeGate) { if (!_wakeClosed) _wake.Set(); } }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Volatile.Write(ref _active, 0);
            DropPending();
        }
        Wake(); // The owner thread releases its renderer; the UI never waits on an in-flight decoder.
    }
}
