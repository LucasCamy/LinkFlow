using System.IO;

namespace LinkFlow.App.Services;

/// <summary>Runs user-selected exports off the dispatcher and only replaces the destination after a complete write.</summary>
internal static class AtomicUserExport
{
    internal static Task WriteAsync(string destination, Func<string, CancellationToken, Task> writeTemporary,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(destination);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".linkflow-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await writeTemporary(temporary, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { LinkFlow.Core.Log.Debug("Export: could not remove temporary file: " + ex.Message); }
        }
    }, cancellationToken);
}
