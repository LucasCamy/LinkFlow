namespace LinkFlow.App;

public sealed class StartupOptions
{
    /// <summary>Inicia escondido na bandeja (usado pelo "iniciar com o Windows").</summary>
    public bool StartInTray { get; private set; }

    /// <summary>Usa hardware simulado (testes/demonstração).</summary>
    public bool Simulate { get; private set; }

    /// <summary>Renderiza a janela num PNG e encerra (teste visual automatizado).</summary>
    public string? ScreenshotPath { get; private set; }
    public int ScreenshotPage { get; private set; }
    public int? ScreenshotWidth { get; private set; }
    public int? ScreenshotHeight { get; private set; }

    public string? ServiceCommand { get; private set; }

    /// <summary>Pede para a instância em execução encerrar de forma controlada (scripts de atualização).</summary>
    public bool ExitRunning { get; private set; }

    public static StartupOptions Parse(string[] args)
    {
        var o = new StartupOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case "--tray":
                case "--minimized":
                    o.StartInTray = true;
                    break;
                case "--exit":
                    o.ExitRunning = true;
                    break;
                case "--simulate":
                    o.Simulate = true;
                    break;
                case "--screenshot":
                    o.ScreenshotPath = Next();
                    break;
                case "--page":
                    o.ScreenshotPage = int.TryParse(Next(), out var p) ? p : 0;
                    break;
                case "--size":
                    var parts = Next()?.Split('x');
                    if (parts is { Length: 2 } && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                    {
                        o.ScreenshotWidth = w;
                        o.ScreenshotHeight = h;
                    }
                    break;
                case "--install-service":
                case "--uninstall-service":
                case "--start-service":
                case "--stop-service":
                    o.ServiceCommand = a[2..];
                    break;
            }
        }

        if (Environment.GetEnvironmentVariable("LINKFLOW_SIMULATE") == "1") o.Simulate = true;
        return o;
    }
}
