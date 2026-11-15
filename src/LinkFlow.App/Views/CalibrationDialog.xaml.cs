using System.ComponentModel;
using System.Windows;
using LinkFlow.App.ViewModels;

namespace LinkFlow.App.Views;

/// <summary>Assistente "Calibrar ventoinhas" (DataContext: <see cref="CalibrationViewModel"/>).</summary>
public partial class CalibrationDialog : Window
{
    private readonly MainViewModel _main;
    private readonly CalibrationViewModel _vm;

    public CalibrationDialog(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        _vm = new CalibrationViewModel(main.Engine, main.Snapshot);
        DataContext = _vm;
        _main.SnapshotApplied += OnSnapshot;
        Closed += (_, _) => _main.SnapshotApplied -= OnSnapshot;
    }

    private void OnSnapshot() => _vm.Update(_main.Snapshot);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Ninguém acompanhando: a calibração não continua sozinha
        if (_vm.IsRunning) _vm.Cancel();
        base.OnClosing(e);
    }
}
