using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.Core.Ipc;

/// <summary>
/// Quem está do outro lado do pipe. Com o serviço parado, qualquer programa do usuário pode criar um pipe com
/// o mesmo nome e se passar pelo LinkFlow (mostrando leituras falsas e engolindo as alterações). A interface
/// só aceita o processo que o Windows registra como o serviço LinkFlowService.
/// </summary>
public static class PipeIdentity
{
    public const string ServiceName = "LinkFlowService";

    /// <summary>PID do processo do serviço segundo o Gerenciador de Serviços; null se não estiver rodando.</summary>
    public static int? ServiceProcessId(string serviceName = ServiceName)
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return null;
        try
        {
            IntPtr service = OpenServiceW(scm, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) return null;
            try
            {
                if (!QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    return null;
                return status.CurrentState == ServiceRunning && status.ProcessId != 0 ? (int)status.ProcessId : null;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public static int? ServerProcessId(PipeStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : null;

    public static int? ClientProcessId(PipeStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : null;

    /// <summary>True se o servidor do pipe é o processo do serviço LinkFlow.</summary>
    public static bool IsLinkFlowService(PipeStream pipe, out string? reason)
    {
        reason = null;
        var server = ServerProcessId(pipe);
        if (server == null)
        {
            reason = "não foi possível identificar o processo do pipe";
            return false;
        }
        var service = ServiceProcessId();
        if (service == null)
        {
            reason = $"o serviço {ServiceName} não está em execução (pipe criado pelo processo {Describe(server.Value)})";
            return false;
        }
        if (service != server)
        {
            reason = $"o pipe pertence ao processo {Describe(server.Value)}, não ao serviço (PID {service})";
            return false;
        }
        return true;
    }

    /// <summary>"1234 (nome.exe)" para o log.</summary>
    public static string Describe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return $"{pid} ({p.ProcessName})";
        }
        catch
        {
            return pid.ToString();
        }
    }

    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, uint size, out uint needed);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
