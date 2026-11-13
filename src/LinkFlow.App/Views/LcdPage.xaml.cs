using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

public partial class LcdPage : UserControl
{
    public LcdPage() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private static string[]? PickFiles(bool multiple, string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Filter = LcdMediaImporter.DialogFilter,
            Multiselect = multiple,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        return dlg.ShowDialog(Window.GetWindow(Application.Current.MainWindow)) == true ? dlg.FileNames : null;
    }

    private async void AddMedia_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles(true, "Escolher fotos ou GIFs para o LCD") is { } files) await ImportAsync(files);
    }

    private void EmptyDrop_Click(object sender, MouseButtonEventArgs e) => AddMedia_Click(sender, e);

    private async void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        if (PickFiles(false, "Escolher a foto de fundo do quadro") is not { Length: > 0 } files || Vm is not { } vm) return;
        var (ok, failed) = await LcdMediaImporter.ImportAsync(files);
        if (ok.Count > 0) vm.SetLcdBackground(ok[0].Path);
        else if (failed.Count > 0) vm.ShowToast($"Não foi possível abrir \"{failed[0]}\"", canUndo: false);
    }

    private async Task ImportAsync(IEnumerable<string> files)
    {
        if (Vm is not { } vm) return;
        var list = files.ToList();
        if (list.Count > 3) vm.ShowToast($"Preparando {list.Count} arquivos...", canUndo: false);
        var (ok, failed) = await LcdMediaImporter.ImportAsync(list);
        vm.AddLcdMedia(ok);
        if (failed.Count > 0)
            vm.ShowToast($"Não foi possível abrir: {string.Join(", ", failed.Take(3))}{(failed.Count > 3 ? "..." : "")}", canUndo: false);
    }

    private void Media_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                  e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(LcdMediaImporter.IsSupported);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Media_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        var supported = files.Where(f => File.Exists(f) && LcdMediaImporter.IsSupported(f)).ToList();
        if (supported.Count > 0) await ImportAsync(supported);
    }

    // ------------------------------------------------------------------ Enquadramento na prévia

    private Point? _dragFrom;

    private void Crop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        if (e.ClickCount == 2)
        {
            Vm?.ResetCropCommand.Execute(null);
            return;
        }
        _dragFrom = e.GetPosition(el);
        el.CaptureMouse();
        e.Handled = true;
    }

    private void Crop_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from || sender is not FrameworkElement el || Vm is not { } vm) return;
        var p = e.GetPosition(el);
        vm.PanCropBy(p.X - from.X, p.Y - from.Y, el.ActualWidth);
        _dragFrom = p;
    }

    private void Crop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = null;
        (sender as FrameworkElement)?.ReleaseMouseCapture();
    }

    private void Crop_LostCapture(object sender, MouseEventArgs e) => _dragFrom = null;

    private void Crop_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        Vm?.ZoomCropBy(e.Delta);
        e.Handled = true;
    }

    private void MediaRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindButton(d)) return; // botões da linha têm ação própria
        if (ItemFrom(sender) is { } item) Vm?.EditMedia(item.Index);
    }

    private static bool FindButton(DependencyObject d)
    {
        while (d != null && d is not Border { Name: "MediaRow" })
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private static LcdMediaItemViewModel? ItemFrom(object sender) =>
        sender is FrameworkElement { DataContext: LcdMediaItemViewModel item } ? item : null;

    private void DurationMinus_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) item.DurationSeconds -= item.DurationSeconds > 10 ? 5 : 1;
    }

    private void DurationPlus_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) item.DurationSeconds += item.DurationSeconds >= 10 ? 5 : 1;
    }
}
