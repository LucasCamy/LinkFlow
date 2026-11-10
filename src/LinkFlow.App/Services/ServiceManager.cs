using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using LinkFlow.Core;
using LinkFlow.Core.Ipc;

namespace LinkFlow.App.Services;

public enum ServiceState { NotInstalled, Stopped, Starting, Stopping, Running, Unknown }

/// <summary>
/// Instalação e controle do serviço "LinkFlowService" via sc.exe.
/// Operações que alteram o serviço rodam num processo auxiliar elevado (UAC) para que a
/// interface não precise ser administradora.
/// </summary>
public static class ServiceManager
{
    public const string Name = "LinkFlowService";
    public const string DisplayName = "LinkFlow";
    public const string Description = "Controla ventoinhas, bomba, RGB e o display LCD (Corsair iCUE LINK) em segundo plano.";

    public static string ServiceExePath => Path.Combine(AppContext.BaseDirectory, "LinkFlow.Service.exe");
    public static bool ServiceExeExists => File.Exists(ServiceExePath);

    public static ServiceState Query()
    {
        int code = Sc($"query {Name}", out var output);
        if (code == 1060) return ServiceState.NotInstalled;
        if (code != 0) return ServiceState.Unknown;
        if (output.Contains("RUNNING")) return ServiceState.Running;
        if (output.Contains("START_PENDING")) return ServiceState.Starting;
        if (output.Contains("STOP_PENDING")) return ServiceState.Stopping;
        if (output.Contains("STOPPED")) return ServiceState.Stopped;
        return ServiceState.Unknown;
    }

    // ------------------------------------------------------------------ Execução elevada (processo auxiliar)

    /// <summary>Executa um comando de serviço no processo atual (já elevado). Retorna o código de saída.</summary>
    public static int Execute(string command)
    {
        Log.Info($"Comando de serviço: {command}");
        try
        {
            return command switch
            {
                "install-service" => Install(),
                "uninstall-service" => Uninstall(),
                "start-service" => StartService(),
                "stop-service" => StopService(),
                "restart-service" => StopService() is var s && s is 0 or 1062 ? StartService() : s,
                _ => 87
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Falha no comando {command}", ex);
            return 1;
        }
    }

    private static int Install()
    {
        if (!ServiceExeExists)
        {
            Log.Warn($"Executável do serviço não encontrado: {ServiceExePath}");
            return 2;
        }

        var state = Query();
        if (state == ServiceState.NotInstalled)
        {
            int code = Sc($"create {Name} binPath= \"\\\"{ServiceExePath}\\\"\" start= auto DisplayName= \"{DisplayName}\"", out var output);
            if (code != 0)
            {
                Log.Warn($"sc create falhou ({code}): {output}");
                return code;
            }
        }
        else
        {
            // Já existe: atualiza o caminho (ex.: pasta do app mudou)
            StopService();
            Sc($"config {Name} binPath= \"\\\"{ServiceExePath}\\\"\" start= auto", out _);
        }

        Sc($"description {Name} \"{Description}\"", out _);
        // Reinicia automaticamente se o serviço cair
        Sc($"failure {Name} reset= 86400 actions= restart/3000/restart/5000/restart/15000", out _);
        Sc($"failureflag {Name} 1", out _);
        return StartService();
    }

    private static int Uninstall()
    {
        if (Query() == ServiceState.NotInstalled) return 0;
        StopService();
        int code = Sc($"delete {Name}", out var output);
        if (code != 0) Log.Warn($"sc delete falhou ({code}): {output}");
        return code;
    }

    private static int StartService()
    {
        int code = Sc($"start {Name}", out var output);
        if (code is not (0 or 1056)) // 1056 = já em execução
        {
            Log.Warn($"sc start falhou ({code}): {output}");
            return code;
        }
        return WaitFor(ServiceState.Running, TimeSpan.FromSeconds(20)) ? 0 : 1053;
    }

    private static int StopService()
    {
        var state = Query();
        if (state is ServiceState.NotInstalled or ServiceState.Stopped) return 0;
        int code = Sc($"stop {Name}", out _);
        if (code is not (0 or 1062)) return code; // 1062 = não iniciado
        return WaitFor(ServiceState.Stopped, TimeSpan.FromSeconds(20)) ? 0 : 1053;
    }

    private static bool WaitFor(ServiceState target, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (Query() == target) return true;
            Thread.Sleep(300);
        }
        return false;
    }

    /// <summary>Aguarda o serviço aceitar conexões no pipe.</summary>
    public static bool WaitForPipe(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            using var client = PipeEngineClient.TryConnect(TimeSpan.FromMilliseconds(500));
            if (client != null) return true;
            Thread.Sleep(300);
        }
        return false;
    }

    // ------------------------------------------------------------------ Chamado pela interface (não elevada)

    /// <summary>
    /// Inicia o próprio LinkFlow elevado com os argumentos dados (mostra o UAC).
    /// Retorna false se o usuário cancelar.
    /// </summary>
    public static bool StartElevated(string args, bool wait, out int exitCode)
    {
        exitCode = -1;
        try
        {
            var psi = new ProcessStartInfo(AutoStartManager.ExePath, args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (wait)
            {
                p.WaitForExit();
                exitCode = p.ExitCode;
            }
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Info("Elevação cancelada pelo usuário.");
            return false;
        }
    }

    /// <summary>Abre a interface como usuário comum (sem herdar a elevação), via Explorer.</summary>
    public static void LaunchUiAsUser()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AutoStartManager.ExePath}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn($"Não foi possível reabrir a interface: {ex.Message}");
        }
    }

    private static int Sc(string args, out string output)
    {
        var psi = new ProcessStartInfo("sc.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi)!;
        output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return p.ExitCode;
    }
}
