using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using LinkFlow.Core;
using Microsoft.Win32;

namespace LinkFlow.App.Services;

internal sealed record HardwarePart(string Category, string Name, string Details);

internal sealed record HardwareInventoryResult(IReadOnlyList<HardwarePart> Parts, IReadOnlyList<string> Unavailable);

internal sealed record GraphicsAdapter(string Name, string? DriverVersion, string? PciVendor);

internal sealed record InstalledDeviceDriver(string Name, string DeviceClass, string Provider, string Version,
    string? InfName, DateOnly? DriverDate, string? HardwareId);

internal sealed record DriverHardware(IReadOnlyList<GraphicsAdapter> Graphics, string? BoardManufacturer,
    string? BoardModel, string? RyzenProcessor, bool HasIntel, IReadOnlyList<InstalledDeviceDriver> Devices, bool Incomplete);

internal static class HardwareInventory
{
    internal const string StorageScope = @"root\Microsoft\Windows\Storage";
    private const string GraphicsQuery = "SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController";
    private static readonly string[] DriverClasses = ["DISPLAY", "MEDIA", "NET", "BLUETOOTH", "SYSTEM", "USB", "SCSIADAPTER", "HDC"];

    public static HardwareInventoryResult Read()
    {
        var parts = new List<HardwarePart>();
        var unavailable = new List<string>();

        void Add(string category, string query, Func<ManagementBaseObject, (string? Name, string Details)> describe,
            string scope = @"root\cimv2")
        {
            try
            {
                foreach (var (name, details) in Query(query, describe, scope))
                {
                    if (!string.IsNullOrWhiteSpace(name)) parts.Add(new HardwarePart(category, name, details));
                }
            }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                unavailable.Add(category);
                Log.Warn($"Inventário de {category}: {ex.Message}");
            }
        }

        var windowsVersion = ReadWindowsVersion();
        Add("Windows", "SELECT Caption, BuildNumber, OSArchitecture FROM Win32_OperatingSystem", item =>
        {
            string? build = Text(item, "BuildNumber");
            string? buildLabel = build == null ? null : $"Build {build}";
            if (build != null && windowsVersion.Build == build && windowsVersion.Revision is { } revision) buildLabel += $".{revision}";
            return (Text(item, "Caption"), Join(windowsVersion.Release, buildLabel, Text(item, "OSArchitecture")));
        });
        Add("Processador", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, SocketDesignation FROM Win32_Processor",
            item => (Text(item, "Name"), Join(Text(item, "NumberOfCores", " núcleos"), Text(item, "NumberOfLogicalProcessors", " threads"),
                Text(item, "SocketDesignation"))));
        Add("Placa-mãe", "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard",
            item => (Join(Text(item, "Manufacturer"), Text(item, "Product")), Text(item, "Version") is { } revision ? $"Revisão {revision}" : string.Empty));
        Add("BIOS", "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS",
            item => (Text(item, "SMBIOSBIOSVersion"), Join(Text(item, "Manufacturer"), WmiDate(item, "ReleaseDate")?.ToString("d"))));
        Add("Placa de vídeo", "SELECT Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController",
            item => (Text(item, "Name"), Join(Text(item, "DriverVersion") is { } version ? $"Driver {version}" : null, ActiveVideoMode(item))));
        Add("Memória", "SELECT Manufacturer, PartNumber, Capacity, DeviceLocator, ConfiguredClockSpeed, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory",
            item => (Text(item, "PartNumber") ?? "Módulo de memória",
                Join(Text(item, "Manufacturer"), MemoryType(item["SMBIOSMemoryType"]), Size(item, "Capacity", 1UL << 30, "GiB"), MemorySpeed(item),
                    Text(item, "DeviceLocator"))));
        var disks = ReadPhysicalDisks();
        if (disks.Length > 0) parts.AddRange(disks);
        else Add("Armazenamento", "SELECT Model, Size, FirmwareRevision FROM Win32_DiskDrive",
            item => (Text(item, "Model"), Join(Size(item, "Size", 1_000_000_000, "GB"),
                Text(item, "FirmwareRevision") is { } firmware ? $"Firmware {firmware}" : null)));
        Add("Rede", "SELECT Name, NetConnectionID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE",
            item => (Text(item, "Name"), Text(item, "NetConnectionID") ?? string.Empty));
        Add("Monitor", "SELECT ManufacturerName, UserFriendlyName, ProductCodeID FROM WmiMonitorID WHERE Active = TRUE",
            item => (EdidText(item, "UserFriendlyName") ?? EdidText(item, "ProductCodeID") ?? "Monitor conectado",
                EdidText(item, "ManufacturerName") ?? string.Empty), @"root\wmi");
        Add("Áudio", "SELECT Name, Manufacturer FROM Win32_SoundDevice",
            item => (Text(item, "Name"), Text(item, "Manufacturer") ?? string.Empty));
        Add("Periféricos", "SELECT Name, PNPClass FROM Win32_PnPEntity WHERE Present = TRUE AND (PNPClass = 'Keyboard' OR PNPClass = 'Mouse')",
            item => (Text(item, "Name"), Text(item, "PNPClass") == "Keyboard" ? "Teclado" : "Mouse"));

        return new HardwareInventoryResult(parts, unavailable);
    }

    public static DriverHardware ReadDriverHardware()
    {
        bool incomplete = false;

        T[] Read<T>(string query, Func<ManagementBaseObject, T> describe)
        {
            try { return Query(query, describe).ToArray(); }
            catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
            {
                incomplete = true;
                Log.Warn($"Inventário para drivers: {ex.Message}");
                return [];
            }
        }

        var graphics = Read(GraphicsQuery, item => new GraphicsAdapter(Text(item, "Name") ?? "Placa de vídeo",
            Text(item, "DriverVersion"), PciVendor(item))).Where(adapter => adapter.PciVendor != null).Distinct().ToArray();
        var board = Read("SELECT Manufacturer, Product FROM Win32_BaseBoard",
            item => (Manufacturer: Text(item, "Manufacturer"), Model: Text(item, "Product"))).FirstOrDefault();
        var processors = Read("SELECT Manufacturer, Name FROM Win32_Processor",
            item => (Manufacturer: Text(item, "Manufacturer"), Name: Text(item, "Name")));

        string? ryzen = processors.Select(cpu => cpu.Name).FirstOrDefault(name => name?.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) == true);
        bool intel = processors.Any(cpu => cpu.Manufacturer == "GenuineIntel") || graphics.Any(gpu => gpu.PciVendor == "8086") ||
            Read("SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE", PciVendor).Contains("8086");

        var present = Read($"SELECT DeviceID, HardwareID FROM Win32_PnPEntity WHERE Present = TRUE AND ({ClassFilter("PNPClass")})",
            item => (Id: Text(item, "DeviceID"), HardwareId: CatalogHardwareId(item["HardwareID"] as string[])))
            .Where(device => device.Id != null).DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(device => device.Id!, device => device.HardwareId, StringComparer.OrdinalIgnoreCase);
        InstalledDeviceDriver[] devices = present.Count == 0 ? [] : Read(
            "SELECT DeviceID, DeviceName, DeviceClass, DriverProviderName, DriverVersion, InfName, DriverDate " +
            $"FROM Win32_PnPSignedDriver WHERE ({ClassFilter("DeviceClass")})", item =>
            {
                string id = Text(item, "DeviceID") ?? string.Empty;
                var driver = new InstalledDeviceDriver(Text(item, "DeviceName") ?? string.Empty, Text(item, "DeviceClass") ?? string.Empty,
                    Text(item, "DriverProviderName") ?? string.Empty, Text(item, "DriverVersion") ?? string.Empty,
                    Text(item, "InfName"), WmiDate(item, "DriverDate"), present.GetValueOrDefault(id));
                return (Id: id, Driver: driver);
            })
            .Where(entry => present.ContainsKey(entry.Id) && entry.Driver.Name.Length > 0 && entry.Driver.Version.Length > 0 &&
                IncludeDriver(entry.Id, entry.Driver))
            .DistinctBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase).Select(entry => entry.Driver)
            .OrderBy(device => device.DeviceClass).ThenBy(device => device.Name).ToArray();

        return new DriverHardware(graphics, board.Manufacturer, board.Model, ryzen, intel, devices, incomplete);
    }

    private static string ClassFilter(string property) => string.Join(" OR ", DriverClasses.Select(name => $"{property} = '{name}'"));

    private static bool IncludeDriver(string id, InstalledDeviceDriver driver)
    {
        if (driver.DeviceClass.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
            return driver.Name.Contains("3D V-Cache", StringComparison.OrdinalIgnoreCase) ||
                (!driver.Provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && IsPhysicalDriverDevice(id));
        return IsPhysicalDriverDevice(id);
    }

    private static bool IsPhysicalDriverDevice(string id) =>
        id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"INTELAUDIO\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase) || id.StartsWith(@"SCSI\", StringComparison.OrdinalIgnoreCase);

    private static string? CatalogHardwareId(string[]? ids)
    {
        if (ids == null) return null;
        var physicalIds = ids.Where(id => id is { Length: > 0 and <= 256 } && IsPhysicalDriverDevice(id) && !id.Any(char.IsControl)).ToArray();
        return physicalIds.FirstOrDefault(id => id.Contains("&SUBSYS_", StringComparison.OrdinalIgnoreCase) &&
                                                !id.Contains("&REV_", StringComparison.OrdinalIgnoreCase)) ?? physicalIds.FirstOrDefault();
    }

    private static (string? Release, string? Build, int? Revision) ReadWindowsVersion()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return (key?.GetValue("DisplayVersion") as string, key?.GetValue("CurrentBuildNumber") as string, key?.GetValue("UBR") as int?);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return default;
        }
    }

    private static HardwarePart[] ReadPhysicalDisks()
    {
        try
        {
            return Query("SELECT FriendlyName, MediaType, BusType, FirmwareVersion, Size FROM MSFT_PhysicalDisk", item =>
                new HardwarePart("Armazenamento", Text(item, "FriendlyName") ?? "Disco físico",
                    Join(item["MediaType"] switch { (ushort)3 => "HDD", (ushort)4 => "SSD", (ushort)5 => "SCM", _ => null },
                        StorageBus(item["BusType"] as ushort?), Size(item, "Size", 1_000_000_000, "GB"),
                        Text(item, "FirmwareVersion") is { } firmware ? $"Firmware {firmware}" : null)),
                StorageScope).ToArray();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Log.Debug($"Informações de armazenamento: {ex.Message}");
            return [];
        }
    }

    internal static string? StorageBus(ushort? bus) => bus switch
    {
        (ushort)1 => "SCSI",
        (ushort)3 => "ATA",
        (ushort)7 => "USB",
        (ushort)8 => "RAID",
        (ushort)10 => "SAS",
        (ushort)11 => "SATA",
        (ushort)16 => "Storage Spaces",
        (ushort)17 => "NVMe",
        (ushort)19 => "UFS",
        _ => null
    };

    private static string? MemoryType(object? type) => type switch
    {
        (uint)20 => "DDR",
        (uint)21 => "DDR2",
        (uint)24 => "DDR3",
        (uint)26 => "DDR4",
        (uint)27 => "LPDDR",
        (uint)28 => "LPDDR2",
        (uint)29 => "LPDDR3",
        (uint)30 => "LPDDR4",
        (uint)34 => "DDR5",
        (uint)35 => "LPDDR5",
        _ => null
    };

    private static string? MemorySpeed(ManagementBaseObject item)
    {
        if (item["ConfiguredClockSpeed"] is uint configured && configured > 0) return $"{configured} MT/s configurados";
        return item["Speed"] is uint nominal && nominal > 0 ? $"{nominal} MT/s nominais" : null;
    }

    private static string? ActiveVideoMode(ManagementBaseObject item)
    {
        if (item["CurrentHorizontalResolution"] is not uint width || width == 0 ||
            item["CurrentVerticalResolution"] is not uint height || height == 0) return null;
        string resolution = $"{width} × {height}";
        return item["CurrentRefreshRate"] is uint refresh && refresh is > 1 and <= 1000 ? $"{resolution} · {refresh} Hz" : resolution;
    }

    private static DateOnly? WmiDate(ManagementBaseObject item, string property) =>
        Text(item, property) is { Length: >= 8 } value && DateOnly.TryParseExact(value.AsSpan(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    internal static IEnumerable<T> Query<T>(string query, Func<ManagementBaseObject, T> describe, string scope = @"root\cimv2")
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query),
            new EnumerationOptions { Rewindable = false, Timeout = TimeSpan.FromSeconds(5) });
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            using (item) yield return describe(item);
        }
    }

    private static string? PciVendor(ManagementBaseObject item) =>
        Text(item, "PNPDeviceID") is { Length: >= 12 } id && id.StartsWith(@"PCI\VEN_", StringComparison.OrdinalIgnoreCase)
            ? id.Substring(8, 4).ToUpperInvariant()
            : null;

    private static string? Text(ManagementBaseObject item, string property, string suffix = "") =>
        item[property]?.ToString()?.Trim() is { Length: > 0 } value ? value + suffix : null;

    private static string? Size(ManagementBaseObject item, string property, ulong unit, string label) =>
        item[property] is ulong size && size > 0 ? $"{(double)size / unit:0.#} {label}" : null;

    private static string Join(params string?[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private static string? EdidText(ManagementBaseObject item, string property)
    {
        if (item[property] is not ushort[] chars) return null;
        string value = new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
        return value.Length > 0 ? value : null;
    }
}
