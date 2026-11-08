using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using LinkFlow.Core;
using Microsoft.Win32;

namespace LinkFlow.App.Services;

/// <summary>
/// "Iniciar com o Windows".
/// Com privilégio de administrador usa uma tarefa agendada "ao fazer logon / executar com privilégios mais altos"
/// (a chave Run do registro NÃO inicia apps que exigem elevação). Sem privilégio (UI cliente do serviço),
/// usa a chave Run do usuário.
/// </summary>
public static class AutoStartManager
{
    public const string TaskName = "LinkFlow";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "LinkFlow";

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LinkFlow.App.exe");

    public static bool IsEnabled() => TaskExists() || RunValueExists();

    public static bool IsTaskMode => TaskExists();

    /// <returns>Mensagem de erro, ou null em caso de sucesso.</returns>
    public static string? Enable(bool preferTask)
    {
        try
        {
            if (preferTask && IsElevated)
            {
                var error = CreateTask();
                if (error != null) return error;
                DeleteRunValue();
            }
            else
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key.SetValue(RunValue, $"\"{ExePath}\" --tray");
                if (IsElevated) DeleteTask();
            }
            Log.Info($"Iniciar com o Windows ativado ({(TaskExists() ? "tarefa agendada" : "registro")}).");
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Falha ao ativar início automático: {ex.Message}");
            return ex.Message;
        }
    }

    public static string? Disable()
    {
        try
        {
            DeleteRunValue();
            if (TaskExists())
            {
                if (!IsElevated) return "Remover a tarefa agendada requer o LinkFlow aberto como administrador.";
                DeleteTask();
            }
            Log.Info("Iniciar com o Windows desativado.");
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static bool RunValueExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    private static void DeleteRunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) != null) key.DeleteValue(RunValue);
    }

    private static bool TaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    private static void DeleteTask() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out _);

    private static string? CreateTask()
    {
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Inicia o LinkFlow na bandeja ao fazer logon (controle de ventoinhas, RGB e LCD).</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <Delay>PT8S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(user)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(ExePath)}</Command>
                  <Arguments>--tray</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(ExePath)!)}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var tmp = Path.Combine(Path.GetTempPath(), $"linkflow-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F", out var output);
            return code == 0 ? null : $"schtasks falhou ({code}): {output.Trim()}";
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static int RunSchtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(10000);
        return p.ExitCode;
    }
}
