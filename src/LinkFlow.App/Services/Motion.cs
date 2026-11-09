using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LinkFlow.App.Services;

/// <summary>
/// Transições curtas (renderizadas pela GPU via composição do WPF; custo zero com a janela escondida).
/// Desligadas se o Windows estiver com "Efeitos de animação" desativado ou pela opção do LinkFlow.
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction Ease = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static bool UserEnabled { get; set; } = true;
    public static bool Enabled => UserEnabled && SystemParameters.ClientAreaAnimation;

    /// <summary>Página nova: aparece com fade e sobe 12 px (180 ms).</summary>
    public static void PageIn(UIElement element) => FadeMove(element, 0, 12, 180);

    /// <summary>Aviso inferior: sobe 16 px.</summary>
    public static void ToastIn(UIElement element) => FadeMove(element, 0, 16, 200);

    /// <summary>Painel de diálogo: fundo em fade e conteúdo cresce de 96% para 100%.</summary>
    public static void DialogIn(UIElement backdrop, UIElement panel)
    {
        if (!Enabled) return;
        Animate(backdrop, UIElement.OpacityProperty, 0, 1, 160);
        var scale = new ScaleTransform(0.96, 0.96);
        panel.RenderTransformOrigin = new Point(0.5, 0.5);
        panel.RenderTransform = scale;
        var a = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        Animate(panel, UIElement.OpacityProperty, 0, 1, 180);
    }

    private static void FadeMove(UIElement element, double fromOpacity, double offsetY, int ms)
    {
        if (!Enabled) return;
        var move = new TranslateTransform(0, offsetY);
        element.RenderTransform = move;
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
        Animate(element, UIElement.OpacityProperty, fromOpacity, 1, ms);
    }

    private static void Animate(UIElement element, DependencyProperty property, double from, double to, int ms)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop };
        a.Completed += (_, _) => element.SetValue(property, to);
        element.BeginAnimation(property, a);
    }
}
