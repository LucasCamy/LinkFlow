using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LinkFlow.Core;

namespace LinkFlow.App.Controls;

/// <summary>
/// Prévia ao vivo do efeito: desenha as ventoinhas como anéis de LEDs, com a mesma conta e o mesmo
/// relógio que o engine usa. Só anima enquanto está visível (efeito parado: 4 quadros/s, para
/// acompanhar mudanças de cor).
/// </summary>
public sealed class EffectPreview : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(Func<RgbConfig>), typeof(EffectPreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FanCountProperty = DependencyProperty.Register(
        nameof(FanCount), typeof(int), typeof(EffectPreview), new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsRender));

    public Func<RgbConfig>? Source { get => (Func<RgbConfig>?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public int FanCount { get => (int)GetValue(FanCountProperty); set => SetValue(FanCountProperty, value); }

    private const int LedsPerRing = 12;
    private static readonly Pen RingPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 2));
    private static readonly Brush HubBrush = Freeze(new SolidColorBrush(Color.FromRgb(15, 23, 42)));
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private readonly DispatcherTimer _timer;

    public EffectPreview()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => InvalidateVisual();
        IsVisibleChanged += (_, _) => UpdateTimer();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (IsVisible && IsLoaded) _timer.Start();
        else _timer.Stop();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, 64);

    protected override void OnRender(DrawingContext dc)
    {
        var cfg = Source?.Invoke();
        double w = ActualWidth, h = ActualHeight;
        if (cfg == null || w < 40 || h < 20) return;

        var interval = cfg.IsAnimated ? TimeSpan.FromMilliseconds(33) : TimeSpan.FromMilliseconds(250);
        if (_timer.Interval != interval) _timer.Interval = interval;

        int fans = Math.Clamp(FanCount, 1, 12);
        double size = Math.Min(h, (w - (fans - 1) * 10) / fans);
        double total = fans * size + (fans - 1) * 10;
        double x0 = (w - total) / 2, cy = h / 2;
        long now = RgbEffects.Now;
        int ledsPerDevice = CorsairLinkHub.LedsPerDevice;

        for (int f = 0; f < fans; f++)
        {
            var center = new Point(x0 + f * (size + 10) + size / 2, cy);
            double radius = size / 2 - 3;
            dc.DrawEllipse(HubBrush, RingPen, center, radius, radius);

            for (int k = 0; k < LedsPerRing; k++)
            {
                double phase = 0;
                if (cfg.SpreadOnFans && cfg.IsAnimated)
                {
                    int led = f * ledsPerDevice + k * ledsPerDevice / LedsPerRing;
                    phase = RgbEffects.LedPhase(cfg.Effect, led, ledsPerDevice, fans);
                }
                var (r, g, b) = RgbEffects.PreviewAt(cfg, now, phase);
                double angle = k * Math.PI * 2 / LedsPerRing - Math.PI / 2;
                var p = new Point(center.X + Math.Cos(angle) * radius * 0.72, center.Y + Math.Sin(angle) * radius * 0.72);
                var color = Color.FromRgb(r, g, b);
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, r, g, b)), null, p, size * 0.11, size * 0.11);
                dc.DrawEllipse(new SolidColorBrush(color), null, p, size * 0.055, size * 0.055);
            }
        }
    }
}
