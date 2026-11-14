using System.Diagnostics;

namespace LinkFlow.Core;

/// <summary>
/// Programas que também falam com o iCUE LINK Hub. Dois controladores no mesmo hub embaralham os comandos:
/// cores trocadas, leituras de 0 RPM e modo de segurança falso.
/// </summary>
public static class ConflictDetector
{
    /// <summary>Nome do processo (sem .exe) → nome mostrado ao usuário.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownControllers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["iCUE"] = "Corsair iCUE",
            ["iCUEDevicePluginHost"] = "Corsair iCUE",
            ["Corsair.Service"] = "serviço do Corsair iCUE",
            ["CorsairDeviceControlService"] = "serviço do Corsair iCUE",
            ["SignalRgb"] = "SignalRGB",
            ["OpenLinkHub"] = "OpenLinkHub",
            ["OpenRGB"] = "OpenRGB",
        };

    /// <summary>Nomes amigáveis (sem repetição) dos controladores em execução.</summary>
    public static IReadOnlyList<string> FindRunning()
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try { processes = Process.GetProcesses(); }
        catch (Exception ex)
        {
            Log.Debug($"Não foi possível listar os processos: {ex.Message}");
            return Array.Empty<string>();
        }
        foreach (var p in processes)
        {
            try
            {
                if (KnownControllers.TryGetValue(p.ProcessName, out var name)) found.Add(name);
            }
            catch { /* processo encerrou durante a leitura */ }
            finally { p.Dispose(); }
        }
        return found.ToList();
    }

    public static string Describe(IReadOnlyList<string> programs) => programs.Count switch
    {
        0 => string.Empty,
        1 => programs[0],
        _ => string.Join(", ", programs.Take(programs.Count - 1)) + " e " + programs[^1]
    };
}
