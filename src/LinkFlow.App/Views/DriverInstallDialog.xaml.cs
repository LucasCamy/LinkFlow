using System.Windows;
using LinkFlow.App.Services;

namespace LinkFlow.App.Views;

public partial class DriverInstallDialog : Window
{
    internal DriverInstallDialog(DriverUpdate driver)
    {
        InitializeComponent();
        DriverTitle.Text = driver.Title;
        if (driver.LicenseTerms is not { } terms) return;
        LicensePanel.Visibility = Visibility.Visible;
        LicenseText.Text = terms;
        ConfirmButton.Content = "Aceitar termos e instalar";
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
