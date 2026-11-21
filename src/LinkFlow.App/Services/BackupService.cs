using System.IO;
using System.Text.Json;
using System.Windows;
using LinkFlow.App.ViewModels;
using LinkFlow.App.Views;
using LinkFlow.Core;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

/// <summary>Exportar/importar configurações, perfis e mídias do LCD (arquivo .linkflow).</summary>
public static class BackupService
{
    private static string AppVersion => typeof(App).Assembly.GetName().Version!.ToString(3);

    public static async Task<string?> ExportAsync(Window owner, MainViewModel vm)
    {
        if (!vm.TryBeginBackup()) return null;
        var cancellation = vm.UiWorkToken;
        try
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Strings.Set_ExportarConfiguracoesLinkFlow,
                Filter = string.Format(Strings.Set_BackupLinkFlowJSONJson, ConfigBackup.Extension, ConfigBackup.Extension),
                FileName = $"LinkFlow-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}{ConfigBackup.Extension}",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                AddExtension = true
            };
            if (dlg.ShowDialog(owner) != true) return null;

            // All state read from the UI is captured before dispatching file work.
            var config = vm.Engine.Config.Clone();
            var ui = JsonSerializer.SerializeToElement(App.Current.Settings.Current, Json.Options);
            vm.SetBackupStatus(OperationalText.Pick("Criando backup…", "Creating backup…"));
            var result = await BackupFileOperations.ExportAsync(config, ui, AppVersion, dlg.FileName, cancellation);
            cancellation.ThrowIfCancellationRequested();
            Log.Info($"Configurações exportadas para {dlg.FileName} ({result.Profiles} perfis, {result.Media} mídias).");
            vm.ShowToast(string.Format(Strings.Set_ExportadoPerfilIsImagem, Path.GetFileName(dlg.FileName), result.Profiles, result.Media), canUndo: false);
            return dlg.FileName;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Falha ao exportar configurações", ex);
            if (!vm.UiWorkStopped) MessageBox.Show(owner, string.Format(Strings.Set_NaoFoiPossivelExportar, ex.Message), Strings.Set_ExportarConfiguracoes, MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally { vm.EndBackup(); }
    }

    public static async Task ImportAsync(Window owner, MainViewModel vm, string? path = null)
    {
        if (!vm.CanImportBackup) return;
        if (!vm.TryBeginBackup()) return;
        var cancellation = vm.UiWorkToken;
        try
        {
            if (path == null)
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = Strings.Set_ImportarConfiguracoesLinkFlow,
                    Filter = string.Format(Strings.Set_BackupFilterImport, ConfigBackup.Extension, ConfigBackup.Extension),
                    InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };
                if (dlg.ShowDialog(owner) != true) return;
                path = dlg.FileName;
            }

            vm.SetBackupStatus(OperationalText.Pick("Lendo backup…", "Reading backup…"));
            var backup = await BackupFileOperations.LoadAsync(path, cancellation);
            cancellation.ThrowIfCancellationRequested();

            vm.SetBackupStatus(OperationalText.Pick("Aguardando revisão…", "Waiting for review…"));
            var dialog = new ImportDialog(backup) { Owner = owner };
            if (dialog.ShowDialog() != true || dialog.Sections == BackupSections.None) return;
            await ApplyCoreAsync(vm, backup, dialog.Sections, Path.GetFileName(path), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error("Falha ao importar configurações", ex);
            if (!vm.UiWorkStopped) MessageBox.Show(owner, ex.Message, Strings.Set_ImportarConfiguracoes, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { vm.EndBackup(); }
    }

    /// <summary>Aplica as partes escolhidas (com "Desfazer"). Público para testes.</summary>
    public static async Task ApplyAsync(MainViewModel vm, ConfigBackup backup, BackupSections sections, string sourceName)
    {
        if (!vm.CanImportBackup) return;
        if (!vm.TryBeginBackup()) return;
        try { await ApplyCoreAsync(vm, backup, sections, sourceName, vm.UiWorkToken); }
        finally { vm.EndBackup(); }
    }

    private static async Task ApplyCoreAsync(MainViewModel vm, ConfigBackup backup, BackupSections sections,
        string sourceName, CancellationToken cancellation)
    {
        var reviewed = vm.Engine.Config.Clone();
        long revision = reviewed.Revision;
        vm.SetBackupStatus(OperationalText.Pick("Preparando importação…", "Preparing import…"));
        var prepared = await BackupFileOperations.PrepareImportAsync(backup, sections, AppPaths.MediaDir,
            Path.Combine(AppPaths.UserDir, "media"), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (!vm.CanControl) throw new InvalidOperationException(OperationalText.Pick("A conexão de controle não está disponível. Reconecte e revise a importação.",
            "Control connection is unavailable. Reconnect and review the import."));
        if (vm.Engine.Config.Revision != revision)
            throw new InvalidOperationException(OperationalText.Pick("A configuração mudou durante a preparação. Revise e importe novamente.",
                "Configuration changed during preparation. Review and import again."));

        var next = reviewed.Clone();
        ConfigBackup.ApplyTo(next, backup.Config, sections, prepared.MediaMap);
        vm.SetBackupStatus(OperationalText.Pick("Aguardando aceite da configuração…", "Waiting for configuration acceptance…"));
        var receipt = await ReviewedConfigSubmission.SubmitAsync(vm.Engine, next, revision, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (vm.Engine.Config.Revision > receipt.Revision)
            throw new InvalidOperationException(OperationalText.Pick(
                "O backup foi aceito, mas a configuração mudou novamente. Revise o estado em Operações; as preferências da interface não foram importadas.",
                "The backup was accepted, but configuration changed again. Review Operations; interface preferences were not imported."));

        string message = OperationalText.Pick("Configuração do backup aceita. Confira a confirmação dos dispositivos em Operações.",
            "Backup configuration accepted. Check device confirmation in Operations.");
        if (prepared.MissingIncludedMedia > 0) message += OperationalText.Pick(" · Algumas imagens incluídas não puderam ser copiadas.", " · Some included images could not be copied.");
        vm.OfferBackupUndo(reviewed, message);

        if (prepared.Ui is { } ui)
        {
            App.Current.Settings.Update(s =>
            {
                // Só preferências: posição/tamanho da janela continuam os deste PC
                s.CloseToTray = ui.CloseToTray;
                s.MinimizeToTray = ui.MinimizeToTray;
                s.ShowTrayTemperature = ui.ShowTrayTemperature;
                s.AlertNotifications = ui.AlertNotifications;
                s.GlobalHotkeys = ui.GlobalHotkeys;
            });
            App.Current.SettingsVm.NotifyAllChanged();
        }
        Log.Info($"Importado de {sourceName}: {sections}.");
    }
}
