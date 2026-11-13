using System.Windows;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

public partial class AlertHistoryDialog : Window
{
    public AlertHistoryDialog(AlertsViewModel alerts)
    {
        InitializeComponent();
        DataContext = alerts;
    }
}
