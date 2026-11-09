using System.Windows.Controls;
using System.Windows.Input;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

public partial class DetailPanel : UserControl
{
    public DetailPanel()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) Services.Motion.DialogIn(Backdrop, Panel); };
    }

    private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        (DataContext as MainViewModel)?.CloseDetail();
        e.Handled = true;
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // clique dentro não fecha
}
