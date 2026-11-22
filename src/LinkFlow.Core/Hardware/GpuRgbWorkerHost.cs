using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using LinkFlow.Core.Ipc;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.Core;

/// <summary>Private service child entry point. No host, configuration store, sensor source or engine is constructed.</summary>
public static class GpuRgbWorkerHost
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        // Preserve the binary stream separately: ordinary Console/log output cannot corrupt protocol frames.
        using Stream input = Console.OpenStandardInput();
        using Stream output = Console.OpenStandardOutput();
        Console.SetOut(TextWriter.Null);
        Log.Configure("gpu-rgb-worker", (LogLevel)int.MaxValue);
        try
        {
            using Process parent = GpuRgbWorkerIdentity.AuthenticateParent();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task parentWatcher = WatchParentAsync(parent, lifetime);
            try { return await RunTrustedAsync(input, output, () => new AsusGpuRgb(), lifetime.Token).ConfigureAwait(false); }
            finally
            {
                lifetime.Cancel();
                await parentWatcher.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or
                                   Win32Exception or ArgumentException or NotSupportedException)
        {
            // No configuration or shared log writes from this process, including authentication failures.
            Console.Error.WriteLine("GPU RGB worker rejected: " + ex.GetType().Name);
            return 3;
        }
    }

    private static async Task WatchParentAsync(Process parent, CancellationTokenSource lifetime)
    {
        try
        {
            await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            lifetime.Cancel();
            // Also covers a stalled native call. The owned job is the independent parent-death backstop.
            Environment.Exit(4);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { lifetime.Cancel(); }
    }

    /// <summary>Internal deterministic seam: callers supply only in-memory streams and fake devices in tests.</summary>
    internal static async Task<int> RunTrustedAsync(Stream input, Stream output, Func<IRgbDevice> factory,
        CancellationToken token)
    {
        IRgbDevice? device = null;
        long generation = 0, lastRequestId = 0;
        long? sentFailureTotal = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var request = await GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerRequest>(input,
                    GpuRgbWorkerProtocol.MaximumRequestBytes, token).ConfigureAwait(false);
                if (request == null) return 0; // Parent closed stdin; dispose the GPU-only device.
                GpuRgbWorkerFraming.ValidateRequest(request);
                if (request.RequestId <= lastRequestId || (generation != 0 && request.Generation != generation) ||
                    (generation == 0 && request.Operation != GpuRgbWorkerProtocol.Initialize))
                    throw new InvalidDataException("Out-of-order GPU RGB worker request.");
                generation = request.Generation;
                lastRequestId = request.RequestId;
                // The launcher has assigned the job before it can send this first frame.
                device ??= factory();
                bool accepted;
                if (request.Operation == GpuRgbWorkerProtocol.Initialize)
                {
                    device.Initialize();
                    accepted = device.IsAvailable;
                }
                else accepted = device.SetColor(request.R, request.G, request.B, request.Brightness);
                var status = device is IRgbControlStatusProvider provider ? provider.ControlStatus : new RgbControlStatus
                {
                    ControllerName = device.Name, ControlAvailable = device.IsAvailable,
                    InitializationRetryNeeded = device.NeedsInitializationRetry,
                    LastOutputAccepted = request.Operation == GpuRgbWorkerProtocol.Color ? accepted : null,
                    DiagnosticText = device.DiagnosticText
                };
                // The provider caches this immutable window on failure, never on a successful native transfer.
                // Send a supported baseline once, then only a changed failure window. Healthy replies omit it.
                var history = status.NativeFailureHistory;
                var batch = history != null && sentFailureTotal != history.TotalFailures ? new GpuRgbNativeFailureBatch
                {
                    TotalFailures = history.TotalFailures, RingEvictedCount = history.EvictedCount,
                    Records = history.Records
                } : null;
                await GpuRgbWorkerFraming.WriteReplyAsync(output, new GpuRgbWorkerReply
                {
                    Generation = generation, RequestId = request.RequestId, Accepted = accepted,
                    Status = history == null ? status : status with { NativeFailureHistory = null }, NativeFailureBatch = batch
                }, token).ConfigureAwait(false);
                if (history != null) sentFailureTotal = history.TotalFailures;
            }
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (Exception) { return 5; } // Never invent a boolean native rejection for an exception/invalid protocol.
        finally { device?.Dispose(); }
    }
}

internal static class GpuRgbWorkerIdentity
{
    private const int MaximumCommandLineBytes = 128 * 1024;
    private static readonly HashSet<string> TrustedWriters = new(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544", // SYSTEM and Administrators
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" // TrustedInstaller
    };

    internal static void ValidateLauncher(string executablePath)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("The GPU RGB worker requires Windows x64.");
        using Process self = Process.GetCurrentProcess();
        string path = QueryImage(self);
        if (PipeIdentity.ServiceProcessId() != self.Id || self.SessionId != 0 ||
            !SamePath(path, executablePath) || !IsServiceImage(path) || HasForbiddenArguments(QueryCommandLine(self)))
            throw new UnauthorizedAccessException("Only the real, nonsimulated LinkFlow service can launch its GPU RGB child.");
        ValidateProtectedImage(path);
    }

    internal static Process AuthenticateParent()
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("The GPU RGB worker requires Windows x64.");
        using Process self = Process.GetCurrentProcess();
        if (NtQueryInformationProcess(self.SafeHandle, 0, out var basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            throw new UnauthorizedAccessException("The OS could not identify the GPU RGB worker parent.");
        long parentId = basic.ParentId.ToInt64();
        if (parentId is <= 0 or > int.MaxValue || parentId == self.Id)
            throw new UnauthorizedAccessException("Invalid GPU RGB worker parent.");
        Process parent = Process.GetProcessById((int)parentId);
        try
        {
            string image = QueryImage(self), parentImage = QueryImage(parent);
            if (!IsAllowedParent((int)parentId, PipeIdentity.ServiceProcessId(), self.SessionId, parent.SessionId,
                    image, parentImage, parent.StartTime.ToUniversalTime(), self.StartTime.ToUniversalTime(),
                    HasForbiddenArguments(QueryCommandLine(parent))) || parent.HasExited)
                throw new UnauthorizedAccessException("GPU RGB worker parent does not match the Windows service.");
            ValidateProtectedImage(image);
            return parent; // Keep the verified process object/handle alive for the lifetime watcher.
        }
        catch { parent.Dispose(); throw; }
    }

    internal static bool IsAllowedParent(int actualParent, int? servicePid, int childSession, int parentSession,
        string childImage, string parentImage, DateTime parentStartUtc, DateTime childStartUtc, bool forbiddenArguments) =>
        servicePid == actualParent && childSession == 0 && parentSession == 0 && !forbiddenArguments &&
        IsServiceImage(childImage) && SamePath(childImage, parentImage) && parentStartUtc < childStartUtc;

    private static bool IsServiceImage(string path) =>
        Path.GetFileName(path).Equals("LinkFlow.Service.exe", StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool HasForbiddenArguments(string commandLine)
    {
        IntPtr arguments = CommandLineToArgvW(commandLine, out int count);
        if (arguments == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if (count is < 1 or > 256) throw new InvalidDataException("Invalid service command line.");
            for (int index = 1; index < count; index++)
            {
                string argument = Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size)) ?? string.Empty;
                if (argument.StartsWith("--simulate", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("--gpu-rgb-worker", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { LocalFree(arguments); }
    }

    private static string QueryImage(Process process)
    {
        var path = new StringBuilder(32768);
        int length = path.Capacity;
        if (!QueryFullProcessImageNameW(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    private static string QueryCommandLine(Process process)
    {
        _ = NtQueryInformationProcess(process.SafeHandle, 60, IntPtr.Zero, 0, out int length);
        if (length < Marshal.SizeOf<UnicodeString>() || length > MaximumCommandLineBytes)
            throw new UnauthorizedAccessException("Cannot read the service command line within its limit.");
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryInformationProcess(process.SafeHandle, 60, buffer, length, out int returned) != 0 || returned > length)
                throw new UnauthorizedAccessException("Cannot authenticate service arguments.");
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            long start = text.Buffer.ToInt64(), first = buffer.ToInt64();
            if ((text.Length & 1) != 0 || start < first || start > first + length || text.Length > first + length - start)
                throw new InvalidDataException("Invalid native service command line.");
            return Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? string.Empty;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void ValidateProtectedImage(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The service executable must be a regular protected file.");
        RequireProtected(file.GetAccessControl(), FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
        var immediate = file.Directory ?? throw new UnauthorizedAccessException("Missing service directory.");
        RequireProtected(immediate.GetAccessControl(), FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
        for (DirectoryInfo? directory = immediate; directory != null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("GPU RGB worker paths cannot traverse a reparse point.");
    }

    private static void RequireProtected(FileSystemSecurity security, FileSystemRights dangerous)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner == null || !TrustedWriters.Contains(owner) ||
            new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl == null)
            throw new UnauthorizedAccessException("The service image must be owned and writable only by trusted administrators.");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (rules.Count > 512) throw new UnauthorizedAccessException("The service image ACL exceeds its validation limit.");
        foreach (FileSystemAccessRule rule in rules)
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & dangerous) != 0 && !TrustedWriters.Contains(rule.IdentityReference.Value))
                throw new UnauthorizedAccessException("The service image permits writes by an untrusted principal.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved, Peb, Reserved2, Reserved3, ProcessId, ParentId;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        out ProcessBasicInformation information, int length, out int returned);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int infoClass,
        IntPtr information, int length, out int returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
