using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

internal enum DriverPublisher { Nvidia, Amd }

internal sealed record ManufacturerDriverPackage(string Title, DriverPublisher Publisher, Uri? DownloadUrl,
    string Action = "Baixar e instalar");

internal sealed record ManufacturerDriverSource(string Device, string Details, Uri? SupportUrl, ManufacturerDriverPackage? Package = null)
{
    public string? DownloadLabel => Package?.Action;
}

internal sealed record InstalledDriverEntry(string Name, string Category, string Provider, string Version, string? InfName,
    DateOnly? Date, string? HardwareId, Uri? SupportUrl)
{
    public string? DateText => Date?.ToString("d");

    public string Details => string.Join(" · ", new[] { Category, Version, Provider, InfName, DateText is { } date ? $"Pacote de {date}" : null }
        .Where(value => !string.IsNullOrWhiteSpace(value)));

    public Uri? CatalogUrl => HardwareId == null ? null :
        new Uri($"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(HardwareId)}");
}

internal sealed record InstalledDriverInventory(IReadOnlyList<InstalledDriverEntry> Entries, bool Incomplete);

internal sealed record ManufacturerDriverSearch(IReadOnlyList<ManufacturerDriverSource> Sources, InstalledDriverInventory Installed, bool Incomplete);

internal static class ManufacturerDrivers
{
    public static readonly Uri NvidiaSupport = new("https://www.nvidia.com/en-us/drivers/");
    public static readonly Uri AmdSupport = new("https://www.amd.com/en/support/download/drivers.html");
    private static readonly Uri IntelSupport = new("https://www.intel.com/content/www/us/en/support/detect.html");
    private static readonly Uri NvidiaProducts = new("https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3");
    private const string NvidiaLookup = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

    public static InstalledDriverInventory ReadInstalled()
    {
        var hardware = HardwareInventory.ReadDriverHardware();
        return InstalledInventory(hardware, GetBoardSource(hardware.BoardManufacturer, hardware.BoardModel)?.SupportUrl);
    }

    public static async Task<ManufacturerDriverSearch> SearchAsync()
    {
        var hardware = await Task.Run(HardwareInventory.ReadDriverHardware).ConfigureAwait(false);
        var sources = new List<ManufacturerDriverSource>();
        bool incomplete = hardware.Incomplete;
        var chipsetSearch = IsWindowsX64 ? AmdChipsetDrivers.SearchAsync(hardware.BoardModel, hardware.RyzenProcessor) :
            Task.FromResult<ManufacturerDriverSource?>(null);
        var nvidia = hardware.Graphics.Where(gpu => gpu.PciVendor == "10DE").ToArray();
        if (nvidia.Length > 0)
        {
            try
            {
                var found = await ReadNvidiaAsync(nvidia).ConfigureAwait(false);
                sources.AddRange(found.Sources);
                incomplete |= found.Incomplete;
            }
            catch (Exception ex)
            {
                incomplete = true;
                Log.Warn($"Catálogo de drivers NVIDIA: {ex.Message}");
                sources.AddRange(nvidia.Select(gpu => new ManufacturerDriverSource(gpu.Name,
                    "A consulta à NVIDIA falhou. Abra o site oficial para conferir.", NvidiaSupport)));
            }
        }

        ManufacturerDriverSource? chipset = null;
        try { chipset = await chipsetSearch.ConfigureAwait(false); }
        catch (Exception ex)
        {
            incomplete = true;
            Log.Warn($"Catálogo do chipset AMD: {ex.Message}");
        }
        if (chipset != null) sources.Add(chipset);

        var amd = hardware.Graphics.Where(gpu => gpu.PciVendor == "1002").Select(gpu => gpu.Name).ToList();
        if (hardware.RyzenProcessor != null && chipset == null) amd.Add("Chipset Ryzen");
        if (amd.Count > 0)
        {
            var installer = IsWindowsX64 ? new ManufacturerDriverPackage("AMD Auto-Detect", DriverPublisher.Amd, null, "Baixar Auto-Detect") : null;
            sources.Add(new ManufacturerDriverSource(string.Join(" · ", amd),
                "AMD · O Auto-Detect identifica o hardware e mostra os drivers compatíveis no instalador.", AmdSupport, installer));
        }

        if (hardware.HasIntel)
            sources.Add(new ManufacturerDriverSource("Dispositivos Intel", "Intel Driver & Support Assistant", IntelSupport));

        var board = GetBoardSource(hardware.BoardManufacturer, hardware.BoardModel);
        if (board != null) sources.Add(board);
        return new ManufacturerDriverSearch(sources, InstalledInventory(hardware, board?.SupportUrl), incomplete);
    }

    private static async Task<(IReadOnlyList<ManufacturerDriverSource> Sources, bool Incomplete)> ReadNvidiaAsync(IReadOnlyList<GraphicsAdapter> adapters)
    {
        if (!IsWindowsX64 || !adapters.Any(IsGeForce))
            return (adapters.Select(gpu => NvidiaSite(gpu, "NVIDIA · Consulte o catálogo oficial para este modelo.")).ToArray(), false);

        string xml = await OfficialDriverInstaller.GetMetadataAsync(NvidiaProducts).ConfigureAwait(false);
        var products = XDocument.Parse(xml).Descendants("LookupValue")
            .Where(value => value.Element("Name") != null)
            .ToLookup(value => NvidiaModelName(value.Element("Name")!.Value), value => (string?)value.Element("Value"),
                StringComparer.OrdinalIgnoreCase);
        if (products.Count == 0) throw new InvalidOperationException("O catálogo de modelos da NVIDIA está vazio.");

        var results = new List<ManufacturerDriverSource>(adapters.Count);
        bool incomplete = false;
        foreach (var gpu in adapters)
        {
            var ids = products[NvidiaModelName(gpu.Name)].Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
            if (!IsGeForce(gpu) || ids.Length != 1 || !int.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out int productId) || productId <= 0)
            {
                results.Add(NvidiaSite(gpu, "NVIDIA · Consulte o catálogo oficial para este modelo."));
                continue;
            }

            try { results.Add(await ReadNvidiaReleaseAsync(gpu, productId).ConfigureAwait(false)); }
            catch (Exception ex)
            {
                incomplete = true;
                Log.Warn($"Driver de {gpu.Name}: {ex.Message}");
                results.Add(NvidiaSite(gpu, "A consulta à NVIDIA falhou. Abra o site oficial para conferir."));
            }
        }
        return (results, incomplete);
    }

    private static async Task<ManufacturerDriverSource> ReadNvidiaReleaseAsync(GraphicsAdapter gpu, int productId)
    {
        int osId = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? 135 : 57;
        var request = new Uri(FormattableString.Invariant($"{NvidiaLookup}?func=DriverManualLookup&pfid={productId}&osID={osId}&dch=1&isWHQL=1&beta=0&dltype=1&languageCode=1033&numberOfResults=1"));
        string json = await OfficialDriverInstaller.GetMetadataAsync(request).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.GetProperty("IDS");
        foreach (var row in rows.EnumerateArray())
        {
            var item = row.GetProperty("downloadInfo");
            string? version = ReadJsonValue(item, "Version");
            if (ReadJsonValue(item, "IsWHQL") != "1" || !Version.TryParse(version, out var release) || release.Build != -1 ||
                !int.TryParse(ReadJsonValue(item, "ID"), NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id <= 0 ||
                !Uri.TryCreate(ReadJsonValue(item, "DownloadURL"), UriKind.Absolute, out var download) ||
                !OfficialDriverInstaller.IsOfficialDownload(DriverPublisher.Nvidia, download) ||
                !download.AbsolutePath.StartsWith($"/Windows/{version}/", StringComparison.OrdinalIgnoreCase)) continue;

            var current = NvidiaInstalledRelease(gpu.DriverVersion);
            string currentText = current == null ? "versão instalada indisponível" : $"instalada {current.Major}.{current.Minor:D2}";
            string name = Uri.UnescapeDataString(ReadJsonValue(item, "Name") ?? "Driver NVIDIA");
            string details = $"{name} WHQL · {currentText} · disponível {version}";
            if (current == release) details += " · versão atual";
            if (current > release) details += " · a versão instalada é mais recente";

            var support = new Uri($"https://www.nvidia.com/en-us/drivers/details/{id}/");
            var package = current == null || current < release
                ? new ManufacturerDriverPackage($"{name} {version}", DriverPublisher.Nvidia, download)
                : null;
            return new ManufacturerDriverSource(gpu.Name, details, support, package);
        }

        return NvidiaSite(gpu, "A NVIDIA não retornou um pacote WHQL para este modelo e versão do Windows.");
    }

    private static string? ReadJsonValue(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static ManufacturerDriverSource NvidiaSite(GraphicsAdapter gpu, string details) => new(gpu.Name, details, NvidiaSupport);

    private static InstalledDriverInventory InstalledInventory(DriverHardware hardware, Uri? boardSupport) => new(
        hardware.Devices.Select(device => InstalledEntry(device, boardSupport)).OrderBy(entry => entry.Category).ThenBy(entry => entry.Name).ToArray(),
        hardware.Incomplete);

    private static InstalledDriverEntry InstalledEntry(InstalledDeviceDriver device, Uri? boardSupport)
    {
        string category = device.DeviceClass.ToUpperInvariant() switch
        {
            "DISPLAY" => "Vídeo",
            "MEDIA" => "Áudio",
            "BLUETOOTH" => "Bluetooth",
            "SYSTEM" => "Sistema e chipset",
            "USB" => "USB",
            "SCSIADAPTER" or "HDC" => "Armazenamento",
            _ when device.Name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) ||
                   device.Name.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                   device.Name.Contains("WLAN", StringComparison.OrdinalIgnoreCase) => "Wi-Fi",
            _ when device.Name.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) ||
                   device.Name.Contains("GbE", StringComparison.OrdinalIgnoreCase) => "Ethernet",
            _ => "Rede"
        };
        string vendor = device.Provider.ToUpperInvariant();
        Uri? support = vendor switch
        {
            _ when vendor.Contains("NVIDIA") => NvidiaSupport,
            _ when vendor.Contains("ADVANCED MICRO DEVICES") => AmdSupport,
            _ when vendor.StartsWith("INTEL") => IntelSupport,
            _ when vendor.Contains("REALTEK") || vendor.Contains("MEDIATEK") => boardSupport,
            _ => null
        };
        return new InstalledDriverEntry(device.Name, category, device.Provider, device.Version, device.InfName,
            device.DriverDate, device.HardwareId, support);
    }

    private static bool IsWindowsX64 => OperatingSystem.IsWindowsVersionAtLeast(10) && RuntimeInformation.OSArchitecture == Architecture.X64;

    private static bool IsGeForce(GraphicsAdapter gpu) => NvidiaModelName(gpu.Name).StartsWith("GeForce ", StringComparison.OrdinalIgnoreCase);

    private static string NvidiaModelName(string name)
    {
        name = name.Trim();
        return name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) ? name[7..].TrimStart() : name;
    }

    private static Version? NvidiaInstalledRelease(string? windowsVersion)
    {
        if (!Version.TryParse(windowsVersion, out var version) || version.Build < 0 || version.Revision is < 0 or > 9999) return null;
        int release = (version.Build % 10) * 10000 + version.Revision;
        return new Version(release / 100, release % 100);
    }

    private static ManufacturerDriverSource? GetBoardSource(string? manufacturer, string? model)
    {
        if (manufacturer == null || model == null) return null;
        var vendor = manufacturer.ToUpperInvariant();
        if (vendor.Contains("MICRO-STAR") || vendor == "MSI")
        {
            int suffix = model.LastIndexOf(" (MS-", StringComparison.OrdinalIgnoreCase);
            string url = "https://www.msi.com/support/download/";
            if (suffix > 0)
            {
                string slug = Regex.Replace(model[..suffix].ToUpperInvariant(), "[^A-Z0-9-]+", "-").Trim('-');
                if (slug.Length > 0) url = $"https://www.msi.com/Motherboard/{slug}/support#driver";
            }
            return new ManufacturerDriverSource(model, "MSI · Drivers da placa-mãe", new Uri(url));
        }

        var support = vendor switch
        {
            _ when vendor.Contains("ASUSTEK") || vendor == "ASUS" => "https://www.asus.com/support/download-center/",
            _ when vendor.Contains("GIGABYTE") => "https://www.gigabyte.com/Support",
            _ when vendor.Contains("ASROCK") => "https://www.asrock.com/support/index.asp?cat=Drivers",
            _ => null
        };
        return support == null ? null : new ManufacturerDriverSource(model, $"{manufacturer} · Drivers da placa-mãe", new Uri(support));
    }
}
