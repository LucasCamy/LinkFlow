using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using LinkFlow.App.ViewModels;
using LinkFlow.App.Views;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

/// <summary>Exportar/importar configurações, perfis e mídias do LCD (arquivo .linkflow).</summary>
public static class BackupService
{
    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.0.0";

    public static string? Export(Window owner, MainViewModel vm)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar configurações do LinkFlow",
            Filter = $"Backup do LinkFlow (*{ConfigBackup.Extension})|*{ConfigBackup.Extension}|JSON (*.json)|*.json",
            FileName = $"LinkFlow-{Environment.MachineName}-{DateTime.Now:yyyy-MM-dd}{ConfigBackup.Extension}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            AddExtension = true
        };
        if (dlg.ShowDialog(owner) != true) return null;

        try
        {
            var ui = JsonSerializer.SerializeToElement(App.Current.Settings.Current, Json.Options);
            var backup = ConfigBackup.Create(vm.Engine.Config, ui, AppVersion);
            backup.Save(dlg.FileName);
            Log.Info($"Configurações exportadas para {dlg.FileName} ({backup.Config.Profiles.Count} perfis, {backup.Media.Count} mídias).");
            vm.ShowToast($"Exportado: {Path.GetFileName(dlg.FileName)} • {backup.Config.Profiles.Count} perfil(is), {backup.Media.Count} imagem(ns)", canUndo: false);
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao exportar configurações", ex);
            MessageBox.Show(owner, $"Não foi possível exportar:\n{ex.Message}", "Exportar configurações", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    public static void Import(Window owner, MainViewModel vm, string? path = null)
    {
        if (path == null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importar configurações do LinkFlow",
                Filter = $"Backup do LinkFlow (*{ConfigBackup.Extension};*.json)|*{ConfigBackup.Extension};*.json|Todos os arquivos|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(owner) != true) return;
            path = dlg.FileName;
        }

        ConfigBackup backup;
        try
        {
            backup = ConfigBackup.Load(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, ex.Message, "Importar configurações", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new ImportDialog(backup) { Owner = owner };
        if (dialog.ShowDialog() != true || dialog.Sections == BackupSections.None) return;
        Apply(vm, backup, dialog.Sections, Path.GetFileName(path));
    }

    /// <summary>Aplica as partes escolhidas (com "Desfazer"). Público para testes.</summary>
    public static void Apply(MainViewModel vm, ConfigBackup backup, BackupSections sections, string sourceName)
    {
        IReadOnlyDictionary<string, string>? mediaMap = null;
        if (sections.HasFlag(BackupSections.Lcd) && backup.Media.Count > 0)
        {
            try { mediaMap = backup.ExtractMedia(AppPaths.MediaDir); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                mediaMap = backup.ExtractMedia(Path.Combine(AppPaths.UserDir, "media"));
            }
        }

        vm.WithUndo($"Configurações importadas de \"{sourceName}\"", () =>
            vm.Engine.UpdateConfig(c => ConfigBackup.ApplyTo(c, backup.Config, sections, mediaMap)));

        if (sections.HasFlag(BackupSections.Ui) && backup.Ui is { } uiJson)
        {
            try
            {
                var ui = uiJson.Deserialize<UiSettings>(Json.Options);
                if (ui != null)
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
            }
            catch (JsonException ex)
            {
                Log.Warn($"Preferências da interface no backup são inválidas: {ex.Message}");
            }
        }
        Log.Info($"Importado de {sourceName}: {sections}.");
    }
}
