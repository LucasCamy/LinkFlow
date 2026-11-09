using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>
/// Prévia fiel do LCD: usa o mesmo <see cref="LcdComposer"/> que gera os quadros do display,
/// numa thread de fundo, e só roda enquanto a página do LCD está visível.
/// </summary>
public sealed class LcdPreviewService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Dispatcher _ui;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _active;
    private volatile bool _disposed;

    public LcdPreviewService(MainViewModel vm, Dispatcher ui)
    {
        _vm = vm;
        _ui = ui;
        _vm.LcdConfigChanged += () => _wake.Set();
        _thread = new Thread(Run) { IsBackground = true, Name = "LinkFlow LCD preview", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            if (value) _wake.Set();
        }
    }

    /// <summary>Renderiza um quadro imediatamente (usado pelo modo de captura de tela).</summary>
    public void RenderNow() => _wake.Set();

    private void Run()
    {
        LcdComposer? composer = null;
        try
        {
            while (!_disposed)
            {
                if (!_active)
                {
                    _wake.WaitOne();
                    continue;
                }

                var cfg = _vm.Engine.Config;
                var snap = _vm.Snapshot;
                TimeSpan next;
                BitmapSource image;
                try
                {
                    composer ??= new LcdComposer();
                    next = composer.Compose(cfg.Lcd, snap, cfg.Rgb, applyRotation: false);
                    image = ToBitmapSource(composer.Canvas);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Prévia do LCD: {ex.Message}");
                    _wake.WaitOne(TimeSpan.FromSeconds(2));
                    continue;
                }

                _ui.BeginInvoke(DispatcherPriority.Background, () => _vm.LcdPreview = image);

                bool animated = cfg.Lcd.Mode == LcdMode.Media && cfg.Lcd.Media.Count > 0;
                var wait = animated ? next : TimeSpan.FromSeconds(1);
                if (wait < TimeSpan.FromMilliseconds(40)) wait = TimeSpan.FromMilliseconds(40);
                _wake.WaitOne(wait);
            }
        }
        finally
        {
            composer?.Dispose();
        }
    }

    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _active = true;
        _wake.Set();
    }
}
