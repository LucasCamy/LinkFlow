using System.ComponentModel;
using System.Diagnostics;

namespace LinkFlow.App.Services;

internal static class RunningApplications
{
    internal static IReadOnlyList<string> Read(CancellationToken cancellationToken)
    {
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.SessionId == session && process.MainWindowHandle != IntPtr.Zero)
                        names.Add(process.ProcessName);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                { /* Processes may exit or reject access while the list is being read. */ }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        names.Remove("LinkFlow.App");
        return names.ToArray();
    }
}
