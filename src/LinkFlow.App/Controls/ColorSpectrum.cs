using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkFlow.App.Controls;

/// <summary>
/// Quadro de cores (saturação × brilho) para a matiz atual, como nos seletores de cor clássicos.
/// Arrastar ou clicar escolhe a cor; as setas do teclado fazem ajuste fino.
/// </summary>
public sealed class ColorSpectrum : FrameworkElement
{
    public static readonly DependencyProperty HueProperty = DependencyProperty.Register(
        nameof(Hue), typeof(double), typeof(ColorSpectrum),
        new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SaturationProperty = DependencyProperty.Register(
        nameof(Saturation), typeof(double), typeof(ColorSpectrum),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ColorSpectrum),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public double Hue { get => (double)GetValue(HueProperty); set => SetValue(HueProperty, value); }
    public double Saturation { get => (double)GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private static readonly Brush WhiteToTransparent = Freeze(new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0));
    private static readonly Brush TransparentToBlack = Freeze(new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90));
    private static readonly Pen ThumbOuter = Freeze(new Pen(Brushes.White, 2.5));
    private static readonly Pen ThumbInner = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 1));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public ColorSpectrum()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        FocusVisualStyle = null;
        ClipToBounds = false; // o marcador pode passar da borda; o gradiente é recortado no OnRender
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rect = new Rect(0, 0, w, h);
        var geometry = new RectangleGeometry(rect, 10, 10);

        dc.PushClip(geometry);
        dc.DrawRectangle(new SolidColorBrush(HueColor(Hue)), null, rect);
        dc.DrawRectangle(WhiteToTransparent, null, rect);
        dc.DrawRectangle(TransparentToBlack, null, rect);
        dc.Pop();

        var p = new Point(Math.Clamp(Saturation, 0, 1) * w, (1 - Math.Clamp(Value, 0, 1)) * h);
        double r = IsKeyboardFocused ? 9 : 8;
        dc.DrawEllipse(null, ThumbInner, p, r + 1.5, r + 1.5);
        dc.DrawEllipse(null, ThumbOuter, p, r, r);
    }

    public static Color HueColor(double hue)
    {
        double h = ((hue % 360) + 360) % 360 / 60;
        double x = 1 - Math.Abs(h % 2 - 1);
        (double r, double g, double b) = (int)h switch
        {
            0 => (1d, x, 0d),
            1 => (x, 1d, 0d),
            2 => (0d, 1d, x),
            3 => (0d, x, 1d),
            4 => (x, 0d, 1d),
            _ => (1d, 0d, x)
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        Pick(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) Pick(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
            Committed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Soltou o mouse (fim do arraste): bom momento para guardar em "Recentes".</summary>
    public event EventHandler? Committed;

    private void Pick(Point p)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Saturation = Math.Round(Math.Clamp(p.X / ActualWidth, 0, 1), 3);
        Value = Math.Round(Math.Clamp(1 - p.Y / ActualHeight, 0, 1), 3);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
        switch (e.Key)
        {
            case Key.Left: Saturation = Math.Max(0, Saturation - step); break;
            case Key.Right: Saturation = Math.Min(1, Saturation + step); break;
            case Key.Up: Value = Math.Min(1, Value + step); break;
            case Key.Down: Value = Math.Max(0, Value - step); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) => InvalidateVisual();
}
