using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace LinkFlow.Core;

public sealed record InventoryFact
{
    public string Id { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, string> Fields { get; init; } = new();
}
public sealed record InventoryObservation
{
    public DateTimeOffset ObservedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<InventoryFact> Facts { get; init; } = [];
    public List<string> UnavailableCategories { get; init; } = [];
}
public sealed record InventoryChange(DateTimeOffset TimestampUtc, string DeviceId, string Category, string Name,
    string Change, Dictionary<string, string> Before, Dictionary<string, string> After);
public sealed record InventoryStatus
{
    public DateTimeOffset? LastScanUtc { get; init; }
    public bool PersistenceAvailable { get; init; } = true;
    public string? Error { get; init; }
    public List<string> UnavailableCategories { get; init; } = [];
    public List<InventoryFact> Devices { get; init; } = [];
    public List<InventoryChange> Changes { get; init; } = [];
}

/// <summary>A comparação ignora remoções em categorias cuja leitura falhou.</summary>
public sealed class InventoryHistory
{
    private readonly object _gate = new();
    private readonly JsonFileStore<InventoryStatus>? _store;
    private InventoryStatus _status;
    public InventoryHistory(string? path = null)
    {
        _store = path == null ? null : new(path);
        var loaded = _store?.Load() ?? new();
        _status = loaded with
        {
            Devices = (loaded.Devices ?? []).Where(f => f != null && !string.IsNullOrWhiteSpace(f.Id)).Take(512)
                .Select(f => f with { Fields = new(f.Fields ?? []) }).ToList(),
            Changes = (loaded.Changes ?? []).Where(c => c != null).Take(100).ToList(),
            UnavailableCategories = loaded.UnavailableCategories ?? []
        };
    }
    public InventoryStatus Status
    {
        get { lock (_gate) return Json.Clone(_status); }
    }
    public IReadOnlyList<InventoryChange> Observe(InventoryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            var facts = observation.Facts.Take(512).Where(f => !string.IsNullOrWhiteSpace(f.Id))
                .DistinctBy(f => f.Id).ToDictionary(f => f.Id, f => f with { Fields = new(f.Fields) });
            var previous = (_status.Devices ?? []).DistinctBy(f => f.Id).ToDictionary(f => f.Id, f => f);
            var unavailable = observation.UnavailableCategories.ToHashSet(StringComparer.Ordinal);
            foreach (var truncated in observation.Facts.Skip(512)) unavailable.Add(truncated.Category);
            var changes = new List<InventoryChange>();
            foreach (var fact in facts.Values)
            {
                if (!previous.TryGetValue(fact.Id, out var before))
                {
                    if (_status.LastScanUtc != null) changes.Add(new(observation.ObservedAtUtc, fact.Id, fact.Category, fact.Name, "added", [], new(fact.Fields)));
                }
                else if (fact.Name != before.Name || !fact.Fields.OrderBy(kv => kv.Key).SequenceEqual(before.Fields.OrderBy(kv => kv.Key)))
                    changes.Add(new(observation.ObservedAtUtc, fact.Id, fact.Category, fact.Name, "changed", new(before.Fields), new(fact.Fields)));
            }
            foreach (var fact in previous.Values)
            {
                if (facts.ContainsKey(fact.Id)) continue;
                if (unavailable.Contains(fact.Category)) facts[fact.Id] = fact;
                else changes.Add(new(observation.ObservedAtUtc, fact.Id, fact.Category, fact.Name, "removed", new(fact.Fields), []));
            }
            var next = new InventoryStatus
            {
                LastScanUtc = observation.ObservedAtUtc, UnavailableCategories = unavailable.ToList(), Devices = facts.Values.ToList(),
                Changes = changes.Concat(_status.Changes ?? []).Take(100).ToList()
            };
            bool saved = _store?.Save(next) ?? true;
            _status = next with { PersistenceAvailable = saved, Error = saved ? null : "inventory-save-failed" };
            return changes;
        }
    }
}

public static class WindowsOperationalInventory
{
    public static InventoryObservation Read()
    {
        var facts = new List<InventoryFact>();
        var unavailable = new List<string>();
        void Read(string category, string table, string identity, string name, params string[] fields)
        {
            if (facts.Count >= 512) { unavailable.Add(category); return; }
            try
            {
                string? fallbackIdentity = table == "Win32_NetworkAdapter" ? "GUID" : table == "Win32_DiskDrive" ? "DeviceID" : null;
                string columns = string.Join(",", new[] { identity, name }.Concat(fields).Concat(fallbackIdentity == null ? [] : new[] { fallbackIdentity }).Distinct());
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                    new ObjectQuery($"SELECT {columns} FROM {table}"), new System.Management.EnumerationOptions { Rewindable = false, Timeout = TimeSpan.FromSeconds(5) });
                using var results = searcher.Get();
                foreach (ManagementBaseObject item in results)
                {
                    using (item)
                    {
                        string Text(string key) => Convert.ToString(item[key], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                        string label = Text(name);
                        string identityValue = table == "Win32_BIOS" ? "system-bios" : Text(identity);
                        if (identityValue.Length == 0 && fallbackIdentity != null) identityValue = Text(fallbackIdentity);
                        if (identityValue.Length == 0) { unavailable.Add(category); continue; }
                        string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{table}:{identityValue}")))[..24];
                        var values = fields.ToDictionary(field => field, Text);
                        facts.Add(new() { Id = id, Category = category, Name = label, Fields = values });
                    }
                    if (facts.Count >= 512) { unavailable.Add(category); break; }
                }
            }
            catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            { unavailable.Add(category); Log.Debug($"Inventário operacional ({category}): {ex.GetType().Name}."); }
        }
        Read("cpu", "Win32_Processor", "DeviceID", "Name", "NumberOfCores", "NumberOfLogicalProcessors");
        Read("board", "Win32_BaseBoard", "Tag", "Product", "Manufacturer", "Version");
        Read("bios", "Win32_BIOS", "Name", "Name", "SMBIOSBIOSVersion", "ReleaseDate");
        Read("gpu", "Win32_VideoController", "PNPDeviceID", "Name", "DriverVersion", "DriverDate");
        Read("memory", "Win32_PhysicalMemory", "Tag", "DeviceLocator", "Capacity", "Speed", "ConfiguredClockSpeed", "PartNumber");
        Read("disk", "Win32_DiskDrive", "PNPDeviceID", "Model", "Size", "FirmwareRevision", "InterfaceType");
        Read("network", "Win32_NetworkAdapter", "PNPDeviceID", "Name", "Manufacturer", "AdapterType");
        Read("os", "Win32_OperatingSystem", "CSName", "Caption", "Version", "BuildNumber", "OSArchitecture");
        return new() { Facts = facts, UnavailableCategories = unavailable };
    }
}
