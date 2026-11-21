namespace LinkFlow.Core;

public sealed record ServiceStartupEnvironment(bool Simulated, string? DataDirectory, string? PipeName);

/// <summary>Executado antes de qualquer acesso aos singletons AppPaths/PipeProtocol, sem carregar hardware.</summary>
public static class ServiceStartupIsolation
{
    public static ServiceStartupEnvironment Configure(string[] args)
    {
        var environment = Resolve(args, Environment.GetEnvironmentVariable("LINKFLOW_DATA_DIR"), Environment.GetEnvironmentVariable("LINKFLOW_PIPE"));
        if (environment.Simulated)
        {
            Environment.SetEnvironmentVariable("LINKFLOW_DATA_DIR", environment.DataDirectory);
            Environment.SetEnvironmentVariable("LINKFLOW_PIPE", environment.PipeName);
        }
        return environment;
    }

    /// <summary>Plano puro para testes e validação; não modifica ambiente, diretórios, serviço ou pipe.</summary>
    public static ServiceStartupEnvironment Resolve(string[] args, string? dataOverride, string? pipeOverride,
        string? temporaryDirectory = null, string? productionDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool simulate = args.Contains("--simulate", StringComparer.OrdinalIgnoreCase);
        if (!simulate) return new(false, dataOverride, pipeOverride);
        string id = Guid.NewGuid().ToString("N");
        string production = Normalize(productionDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinkFlow"));
        string data = Normalize(string.IsNullOrWhiteSpace(dataOverride)
            ? Path.Combine(temporaryDirectory ?? Path.GetTempPath(), "LinkFlow", "simulation", id)
            : dataOverride);
        if (ContainsPath(production, data) || ContainsPath(data, production))
            throw new InvalidOperationException("A simulação não pode usar a pasta de produção, seus ancestrais ou subpastas. Defina LINKFLOW_DATA_DIR isolado.");
        RejectReparseAncestors(data);
        string pipe = string.IsNullOrWhiteSpace(pipeOverride) ? "LinkFlow.Simulation." + id : pipeOverride.Trim();
        // Constante inlined: não dispara a inicialização estática de PipeProtocol.
        if (pipe.Equals(Ipc.PipeProtocol.DefaultPipeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A simulação não pode usar o pipe de produção. Defina LINKFLOW_PIPE isolado.");
        if (pipe.Length is < 1 or > 180 || pipe.Any(c => !char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new ArgumentException("Nome de pipe de simulação inválido.");
        return new(true, data, pipe);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())));
    private static bool ContainsPath(string parent, string candidate) => parent.Equals(candidate, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RejectReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory != null; directory = directory.Parent)
            if (SharedDataSecurity.IsReparsePoint(directory.FullName))
                throw new IOException("A pasta de simulação não pode atravessar links ou junções.");
    }
}
