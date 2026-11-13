using System.Diagnostics;
using System.IO;
using System.Text;

namespace LinkFlow.App.Services;

internal sealed record OptionalWindowsApp(string PackageName, string Name);

internal static class OptionalWindowsApps
{
    private static readonly OptionalWindowsApp[] Catalog =
    [
        new("Clipchamp.Clipchamp", "Clipchamp"),
        new("Microsoft.Getstarted", "Dicas do Windows"),
        new("Microsoft.WindowsFeedbackHub", "Hub de Comentários"),
        new("Microsoft.MicrosoftSolitaireCollection", "Microsoft Solitaire Collection"),
        new("Microsoft.BingNews", "Notícias"),
        new("Microsoft.GetHelp", "Obter Ajuda"),
        new("Microsoft.People", "Pessoas"),
        new("Microsoft.BingWeather", "Clima"),
        new("Microsoft.YourPhone", "Vincular ao Celular"),
        new("Microsoft.WindowsMaps", "Mapas do Windows")
    ];

    private const string ScriptHeader = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        """;

    private const string RemovablePackages = """
        Where-Object { $_.NonRemovable -eq $false -and $_.IsFramework -eq $false -and $_.IsResourcePackage -eq $false }
        """;

    public static async Task<IReadOnlyList<OptionalWindowsApp>> ListAsync()
    {
        string script = $"""
            {ScriptHeader}
            Get-AppxPackage -PackageTypeFilter Main | {RemovablePackages} | Select-Object -ExpandProperty Name
            """;
        var (code, output) = await RunAsync(script).ConfigureAwait(false);
        if (code != 0) throw new InvalidOperationException("O Windows não conseguiu listar os aplicativos desta conta.");
        var installed = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Catalog.Where(app => installed.Contains(app.PackageName)).OrderBy(app => app.Name).ToArray();
    }

    public static async Task<bool> RemoveAsync(OptionalWindowsApp app)
    {
        if (!Catalog.Contains(app)) throw new ArgumentException("Aplicativo fora da lista de remoção.", nameof(app));
        string script = $$"""
            {{ScriptHeader}}
            $packages = @(Get-AppxPackage -Name $env:LINKFLOW_APPX_NAME -PackageTypeFilter Main | {{RemovablePackages}})
            if ($packages.Count -eq 0) { exit 10 }
            foreach ($package in $packages) {
                Remove-AppxPackage -Package $package.PackageFullName
            }
            if (Get-AppxPackage -Name $env:LINKFLOW_APPX_NAME -PackageTypeFilter Main) { exit 11 }
            """;
        var (code, _) = await RunAsync(script, app.PackageName).ConfigureAwait(false);
        return code switch
        {
            0 => true,
            10 => false,
            _ => throw new InvalidOperationException($"O Windows não concluiu a remoção de {app.Name}. Consulte o log do LinkFlow."),
        };
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string script, string? packageName = null)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }
        };
        if (packageName != null) info.Environment["LINKFLOW_APPX_NAME"] = packageName;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar o Windows PowerShell.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string error = (await errors.ConfigureAwait(false)).Trim();
        if (error.Length > 0) Core.Log.Warn($"Aplicativos opcionais: {error}");
        return (process.ExitCode, await output.ConfigureAwait(false));
    }
}
