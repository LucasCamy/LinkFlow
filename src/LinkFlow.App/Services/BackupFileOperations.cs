using System.IO;
using System.Text.Json;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

internal sealed record BackupWriteResult(int Profiles, int Media);
internal sealed record PreparedBackupImport(IReadOnlyDictionary<string, string>? MediaMap, UiSettings? Ui, int MissingIncludedMedia);

/// <summary>Only backup data and file work run here. Dialogs, configuration commits and preferences stay on the UI thread.</summary>
internal static class BackupFileOperations
{
    internal static async Task<BackupWriteResult> ExportAsync(ControlConfig capturedConfig, JsonElement? capturedUi,
        string appVersion, string destination, CancellationToken cancellationToken)
    {
        BackupWriteResult? result = null;
        await AtomicUserExport.WriteAsync(destination, (temporary, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var backup = ConfigBackup.Create(capturedConfig, capturedUi, appVersion);
            ct.ThrowIfCancellationRequested();
            backup.Save(temporary);
            result = new(backup.Config.Profiles.Count, backup.Media.Count);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    internal static Task<ConfigBackup> LoadAsync(string path, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backup = ConfigBackup.Load(path);
        cancellationToken.ThrowIfCancellationRequested();
        return backup;
    }, cancellationToken);

    internal static Task<PreparedBackupImport> PrepareImportAsync(ConfigBackup backup, BackupSections sections,
        string primaryMediaDirectory, string fallbackMediaDirectory, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string>? map = null;
        if (sections.HasFlag(BackupSections.Lcd) && backup.Media.Count > 0)
        {
            try { map = backup.ExtractMedia(primaryMediaDirectory); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            { map = backup.ExtractMedia(fallbackMediaDirectory); }
        }
        UiSettings? ui = null;
        if (sections.HasFlag(BackupSections.Ui) && backup.Ui is { } preferences)
        {
            try { ui = preferences.Deserialize<UiSettings>(Json.Options); }
            catch (JsonException ex) { Log.Warn("Preferências da interface no backup são inválidas: " + ex.Message); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        int missing = sections.HasFlag(BackupSections.Lcd)
            ? backup.MediaMap.Count(pair => backup.Media.ContainsKey(pair.Value) && (map == null || !map.ContainsKey(pair.Key))) : 0;
        return new PreparedBackupImport(map, ui, missing);
    }, cancellationToken);
}
