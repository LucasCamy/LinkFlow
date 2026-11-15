using System.Diagnostics;
using System.IO;
using System.Windows;
using LinkFlow.App.ViewModels;
using LinkFlow.Core;

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
            Title = "Salvar pacote de diagnóstico do LinkFlow",
            Filter = "Arquivo ZIP (*.zip)|*.zip",
            FileName = DiagnosticBundle.SuggestedFileName(DateTime.Now),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true
        };
        if (dlg.ShowDialog(owner) != true) return;

        _busy = true;
        vm.ShowToast("Gerando o pacote de diagnóstico...", canUndo: false);
        try
        {
            var snapshot = vm.Snapshot;
            var config = vm.Engine.Config;
            string mode = vm.EngineModeLabel;
            string version = typeof(App).Assembly.GetName().Version!.ToString(3);
            string path = dlg.FileName;
            var result = await Task.Run(() =>
            {
                var inventory = HardwareInventory.Read().Parts.Select(p => (p.Category, p.Name, p.Details));
                string summary = DiagnosticBundle.DescribeHardware(snapshot, version, mode, inventory);
                return DiagnosticBundle.Create(path, LogSources(), config, summary);
            });
            Log.Info($"Pacote de diagnóstico gerado: {result.Path} ({result.LogFiles} logs, {result.Bytes / 1024} KB).");
            vm.ToastVisible = false;

            var answer = MessageBox.Show(owner,
                $"Pacote de diagnóstico salvo em:\n{result.Path}\n\n{result.LogFiles} arquivo(s) de log, config.json e hardware-summary.txt " +
                "(nome de usuário e do computador substituídos).\n\nAbrir a pasta do arquivo?",
                "Pacote de diagnóstico", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{result.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao gerar o pacote de diagnóstico", ex);
            vm.ToastVisible = false;
            MessageBox.Show(owner, $"Não foi possível gerar o pacote de diagnóstico:\n{ex.Message}", "Pacote de diagnóstico",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }
}
