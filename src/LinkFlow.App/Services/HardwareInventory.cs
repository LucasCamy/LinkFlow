using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using LinkFlow.Core;
using Microsoft.Win32;
using LinkFlow.App.Resources;

namespace LinkFlow.App.Services;

internal sealed record HardwarePart(string Category, string Name, string Details);

/// <summary>Inventário lido: um componente por cartão e as categorias que o Windows não deixou ler.</summary>
internal sealed record HardwareInventoryResult(IReadOnlyList<HardwareComponent> Components, IReadOnlyList<HardwareKind> Unavailable)
{
    /// <summary>Lista plana (categoria em português, nome, resumo) para o pacote de diagnóstico.</summary>
    public IReadOnlyList<HardwarePart> Parts => Components.Select(c => new HardwarePart(HardwareInventory.CategoryOf(c.Kind), c.Name,
        string.Join(" · ", c.Summary.Select(row => $"{row.Label}: {row.Value}")))).ToArray();
}

internal sealed record GraphicsAdapter(string Name, string? DriverVersion, string? PciVendor);

internal sealed record InstalledDeviceDriver(string Name, string DeviceClass, string Provider, string Version,
    string? InfName, DateOnly? DriverDate, string? HardwareId);

internal sealed record DriverHardware(IReadOnlyList<GraphicsAdapter> Graphics, string? BoardManufacturer,
    string? BoardModel, string? RyzenProcessor, bool HasIntel, IReadOnlyList<InstalledDeviceDriver> Devices, bool Incomplete);

internal static partial class HardwareInventory
{
    internal const string StorageScope = @"root\Microsoft\Windows\Storage";
    private const string GraphicsQuery = "SELECT Name, DriverVersion, PNPDeviceID FROM Win32_VideoController";
    private static readonly string[] DriverClasses = ["DISPLAY", "MEDIA", "NET", "BLUETOOTH", "SYSTEM", "USB", "SCSIADAPTER", "HDC"];

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

        var graphics = Read(GraphicsQuery, item => new GraphicsAdapter(Text(item, "Name") ?? Strings.Dash_Gpu,
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

    private static string? EdidText(ManagementBaseObject item, string property)
    {
        if (item[property] is not ushort[] chars) return null;
        string value = new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
        return value.Length > 0 ? value : null;
    }
}
