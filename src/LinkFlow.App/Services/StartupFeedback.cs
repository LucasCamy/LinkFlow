using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;

namespace LinkFlow.App.Services;

/// <summary>
/// Sinal visível enquanto a interface espera o serviço subir (logo após o logon pode levar minutos).
/// Antes a espera travava a thread da interface: sem ícone na bandeja, sem janela e cliques sem resposta.
/// </summary>
public sealed class StartupFeedback : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _appIcon;
    private readonly Window? _splash;

    public StartupFeedback(bool showWindow)
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/linkflow.ico"))!.Stream;
        _appIcon = new Icon(stream, SystemInformation.SmallIconSize);
        _icon = new NotifyIcon { Icon = _appIcon, Text = "LinkFlow: aguardando o serviço…", Visible = true };

        if (!showWindow) return;
        _splash = new Window
        {
            Title = "LinkFlow",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowActivated = true,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 15, 25)),
            Content = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 22),
                Children =
                {
                    new TextBlock
                    {
                        Text = "LinkFlow", FontSize = 18, FontWeight = FontWeights.SemiBold,
                        Foreground = System.Windows.Media.Brushes.White
                    },
                    new TextBlock
                    {
                        Text = "Aguardando o serviço do Windows iniciar. O controle das ventoinhas continua com o serviço.",
                        Margin = new Thickness(0, 8, 0, 14), TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184))
                    },
                    new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 4 }
                }
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(_splash, "LinkFlow aguardando o serviço");
        _splash.Show();
    }

    public void Dispose()
    {
        _splash?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
