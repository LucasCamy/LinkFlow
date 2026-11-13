using System.Globalization;
using LinkFlow.App.Services;

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

    /// <summary>Teste visual: abre o painel de detalhes (cpu, gpu, coolant, fans, ram).</summary>
    public string? ScreenshotDetail { get; private set; }

    /// <summary>Teste visual: seleciona as N primeiras ventoinhas.</summary>
    public int ScreenshotSelect { get; private set; }
    public int? ScreenshotWidth { get; private set; }
    public int? ScreenshotHeight { get; private set; }

    public string? ServiceCommand { get; private set; }
    internal bool InstallDriverRequested { get; private set; }
    internal DriverIdentity? DriverToInstall { get; private set; }
    internal bool AcceptDriverLicense { get; private set; }

    /// <summary>Pede para a instância em execução encerrar de forma controlada (scripts de atualização).</summary>
    public bool ExitRunning { get; private set; }

    /// <summary>Espera este processo encerrar antes de iniciar (troca de modo / elevação).</summary>
    public int? WaitPid { get; private set; }

    /// <summary>Após o comando de serviço, reabre a interface como usuário comum.</summary>
    public bool LaunchUi { get; private set; }

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
                case "--wait-pid":
                    o.WaitPid = int.TryParse(Next(), out var pid) ? pid : null;
                    break;
                case "--launch-ui":
                    o.LaunchUi = true;
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
                case "--detail":
                    o.ScreenshotDetail = Next();
                    break;
                case "--select":
                    o.ScreenshotSelect = int.TryParse(Next(), out var sel) ? sel : 0;
                    break;
                case "--install-driver":
                    o.InstallDriverRequested = true;
                    var updateId = Next();
                    var revisionNumber = Next();
                    o.DriverToInstall = Guid.TryParseExact(updateId, "D", out Guid id) && id != Guid.Empty &&
                                        int.TryParse(revisionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int revision)
                        ? new DriverIdentity(id, revision) : null;
                    break;
                case "--accept-driver-license":
                    o.AcceptDriverLicense = true;
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
