using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

public partial class FansPage : UserControl
{
    public FansPage()
    {
        InitializeComponent();
        CurveEditorControl.CurveEdited += (points, final) => Vm?.OnCurveEdited(points, final);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    internal void ShowDevices() => DevicesTab.IsSelected = true;
    internal void ShowAutomation() => AutomationTab.IsSelected = true;

    private void GroupCurve_Click(object sender, RoutedEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (CurvePanel.IsVisible) CurvePanel.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Clique no cartão: simples seleciona só ele; Ctrl alterna; Shift seleciona intervalo.</summary>
    private void FanCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsInteractive(src)) return;
        if (sender is FrameworkElement { DataContext: FanViewModel fan } && Vm != null)
        {
            var mods = Keyboard.Modifiers;
            Vm.HandleFanClick(fan, mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift));
            e.Handled = true;
        }
    }

    private static bool IsInteractive(DependencyObject d)
    {
        while (d != null && d is not Border { Name: "Card" })
        {
            if (d is ButtonBase or Slider or TextBox or Thumb or RepeatButton) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void FanName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: FanViewModel fan })
        {
            Vm?.BeginRename(fan);
            e.Handled = true;
        }
    }

    private static FanViewModel? FanFrom(object sender) => sender is FrameworkElement { DataContext: FanViewModel f } ? f : null;

    private void RenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (FanFrom(sender) is { } fan) Vm?.BeginRename(fan);
    }

    private void RenameMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FanFrom(sender) is { } fan) Vm?.BeginRename(fan);
    }

    private void ResetNameMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FanFrom(sender) is { } fan) Vm?.ResetName(fan);
    }

    private void LinkMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FanFrom(sender) is { } fan) fan.IsLinked = true;
    }

    private void SelectGroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (FanFrom(sender) is { Group: { } g }) Vm?.SelectGroupCommand.Execute(g);
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FanViewModel fan } box) return;
        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            fan.IsEditingName = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            fan.IsEditingName = false;
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: FanViewModel fan } box && fan.IsEditingName)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            fan.IsEditingName = false;
        }
    }

    private void RunningApps_DropDownOpened(object sender, EventArgs e) => Vm?.RefreshAppsCommand.Execute(null);

    private void ProfileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm?.SaveProfileCommand.CanExecute(null) == true)
        {
            Vm.SaveProfileCommand.Execute(null);
            e.Handled = true;
        }
    }
}
