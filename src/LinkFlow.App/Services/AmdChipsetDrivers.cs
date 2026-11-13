using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LinkFlow.App.Services;

internal static class AmdChipsetDrivers
{
    private static readonly Regex BoardChipset = new(@"\b(X870E|X870|X670E|X670|B850|B840|B650E|B650|A620|X570|B550|A520)(?:M|I)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ChipsetDownload = new(
        "href\\s*=\\s*[\"'](https://drivers\\.amd\\.com/drivers/amd_chipset_software_([0-9]+(?:\\.[0-9]+){3})\\.exe)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static async Task<ManufacturerDriverSource?> SearchAsync(string? boardModel, string? processor)
    {
        if (boardModel == null || processor == null) return null;
        var match = BoardChipset.Match(boardModel);
        if (!match.Success) return null;

        string chipset = match.Groups[1].Value.ToUpperInvariant();
        string name = $"Chipset AMD {chipset}";
        string socket = chipset is "X570" or "B550" or "A520" ? "am4" : "am5";
        var support = new Uri($"https://www.amd.com/en/support/downloads/drivers.html/chipsets/{socket}/{chipset.ToLowerInvariant()}.html");
        string html = await OfficialDriverInstaller.GetMetadataAsync(support).ConfigureAwait(false);
        var packages = ChipsetDownload.Matches(html).DistinctBy(item => item.Groups[1].Value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (packages.Length != 1 || !Uri.TryCreate(packages[0].Groups[1].Value, UriKind.Absolute, out var download) ||
            !OfficialDriverInstaller.IsOfficialDownload(DriverPublisher.Amd, download) ||
            !Version.TryParse(packages[0].Groups[2].Value, out var available))
            return new ManufacturerDriverSource(name, $"{processor} · consulte o pacote indicado para o Windows na página da AMD.", support);

        string version = packages[0].Groups[2].Value;
        var (installedText, installed) = InstalledVersion();
        string current = installedText == null ? "versão instalada indisponível" : $"instalada {installedText}";
        string details = $"{processor} · {current} · disponível {version}";
        if (installed == available) details += " · versão atual";
        if (installed > available) details += " · a versão instalada é mais recente";
        var package = installed == null || installed < available
            ? new ManufacturerDriverPackage($"AMD Chipset {chipset} {version}", DriverPublisher.Amd, download)
            : null;
        return new ManufacturerDriverSource(name, details, support, package);
    }

    private static (string? Text, Version? Version) InstalledVersion()
    {
        string? text = null;
        Version? latest = null;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall == null) continue;

            foreach (string key in uninstall.GetSubKeyNames())
            {
                using var item = uninstall.OpenSubKey(key);
                if (!string.Equals(item?.GetValue("DisplayName") as string, "AMD Chipset Software", StringComparison.OrdinalIgnoreCase)) continue;
                string? value = item?.GetValue("DisplayVersion") as string;
                if (!Version.TryParse(value, out var installed) || installed <= latest) continue;
                text = value;
                latest = installed;
            }
        }
        return (text, latest);
    }
}
