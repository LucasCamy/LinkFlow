using System.Diagnostics;
using System.IO;
using System.Windows;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

/// <summary>Botão "Gerar pacote de diagnóstico (.zip)" das Configurações.</summary>
public static class DiagnosticService
{
    private static bool _busy;

    /// <summary>Pastas de log deste PC: a compartilhada (serviço e app) e a local, usada sem permissão na compartilhada.</summary>
    public static IReadOnlyList<DiagnosticLogSource> LogSources() =>
    [
        new("compartilhado", AppPaths.LogsDir),
        new("usuario", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkFlow", "logs")),
        new("atual", Log.CurrentLogDir)
    ];

    public static async Task GenerateAsync(Window owner, MainViewModel vm)
    {
        if (_busy) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.Set_SalvarPacoteDiagnosticoLinkFlow,
            Filter = Strings.Set_ArquivoZIPZipZip,
            FileName = DiagnosticBundle.SuggestedFileName(DateTime.Now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true
        };
        if (dlg.ShowDialog(owner) != true) return;

        _busy = true;
        vm.ShowToast(Strings.Set_GerandoPacoteDiagnostico, canUndo: false);
        try
        {
            var snapshot = vm.Snapshot;
            var config = vm.Engine.Config;
            string mode = vm.EngineModeLabel;
            string version = typeof(App).Assembly.GetName().Version!.ToString(3);
            string path = dlg.FileName;
            var operational = new Dictionary<string, string>
            {
                ["snapshot.json"] = System.Text.Json.JsonSerializer.Serialize(snapshot, Json.Options),
                ["windows-operations.json"] = System.Text.Json.JsonSerializer.Serialize(SystemOperationJournal.Shared.Read(), Json.Options)
            };
            if (Application.Current is App app && app.PeripheralsRgbDiagnostics is { } peripherals)
            {
                operational["peripheral-rgb.json"] = System.Text.Json.JsonSerializer.Serialize(peripherals, Json.Options);
                operational["peripheral-rgb-export-status.json"] = System.Text.Json.JsonSerializer.Serialize(app.PeripheralsRgbDiagnosticExportStatus, Json.Options);
            }
            try
            {
                using var historyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var events = await vm.Engine.QueryEventsAsync(new EventQuery { MaxItems = 200 }, historyTimeout.Token);
                operational["events.json"] = System.Text.Json.JsonSerializer.Serialize(events, Json.Options);
                var series = await Task.WhenAll(new[] { "cpu.temp", "gpu.temp", "coolant.temp" }.Select(key =>
                    vm.Engine.QueryHistoryAsync(new HistoryQuery { MetricKey = key, FromUtc = DateTimeOffset.UtcNow.AddDays(-1),
                        ToUtc = DateTimeOffset.UtcNow, BucketSeconds = 60, MaxPoints = 300 }, historyTimeout.Token)));
                operational["thermal-history.json"] = System.Text.Json.JsonSerializer.Serialize(series, Json.Options);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                operational["history-unavailable.txt"] = ex.Message;
            }
            var result = await Task.Run(() =>
            {
                var inventory = HardwareInventory.Read().Parts.Select(p => (p.Category, p.Name, p.Details));
                string summary = DiagnosticBundle.DescribeHardware(snapshot, version, mode, inventory);
                return DiagnosticBundle.Create(path, LogSources(), config, summary, operationalFiles: operational);
            });
            Log.Info($"Pacote de diagnóstico gerado: {result.Path} ({result.LogFiles} logs, {result.Bytes / 1024} KB).");
            vm.ToastVisible = false;

            var answer = MessageBox.Show(owner,
                string.Format(Strings.Set_PacoteDiagnosticoSalvoArquivo, result.Path, result.LogFiles) +
                Strings.Set_NomeUsuarioComputadorSubstituidos,
                Strings.Set_PacoteDiagnostico, MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao gerar o pacote de diagnóstico", ex);
            vm.ToastVisible = false;
            MessageBox.Show(owner, string.Format(Strings.Set_NaoFoiPossivelGerar, ex.Message), Strings.Set_PacoteDiagnostico,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }
}
