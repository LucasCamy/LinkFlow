using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

/// <summary>Modal de detalhes de um componente da tela Hardware (mesmo padrão dos Detalhes do Painel).</summary>
public partial class HardwareDetailPanel : UserControl
{
    public HardwareDetailPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            Services.Motion.DialogIn(Backdrop, Panel);
            Dispatcher.BeginInvoke(() => CloseButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        };
    }

    private void Close() => (DataContext as HardwareDetailViewModel)?.CloseCommand.Execute(null);

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // clique dentro não fecha

    /// <summary>Esc com o texto de um valor selecionado (a janela ignora o Esc dentro de campos de texto).</summary>
    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled) return;
        Close();
        e.Handled = true;
    }
}
