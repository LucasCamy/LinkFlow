namespace LinkFlow.Core;

public sealed partial class LinkFlowEngine
{
    private InventoryHistory? _inventoryHistory;
    private Task? _inventoryTask;
    private InventoryStatus _inventorySnapshot = new();
    private void StartInventory()
    {
        string? path = _opt.ConfigPath == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_opt.ConfigPath))!, "inventory.json");
        _inventoryHistory = new(path);
        Volatile.Write(ref _inventorySnapshot, _inventoryHistory.Status);
        var provider = _opt.InventoryProvider ?? (IsSimulated ? null : WindowsOperationalInventory.Read);
        if (provider == null) return;
        var ct = _cts.Token;
        _inventoryTask = Task.Run(async () =>
        {
            var interval = _opt.InventoryInterval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : _opt.InventoryInterval;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var previousError = Volatile.Read(ref _inventorySnapshot).Error;
                    var changes = _inventoryHistory.Observe(provider());
                    Volatile.Write(ref _inventorySnapshot, _inventoryHistory.Status);
                    if (previousError != null && Volatile.Read(ref _inventorySnapshot).Error == null)
                        RecordOperationalEvent("inventory.scan.recovered", "Inventory acquisition recovered", source: "inventory");
                    foreach (var change in changes)
                        RecordOperationalEvent("inventory." + change.Change, change.Name, source: "inventory", deviceId: change.DeviceId,
                            data: new() { ["category"] = change.Category, ["before"] = System.Text.Json.JsonSerializer.Serialize(change.Before, Json.Compact), ["after"] = System.Text.Json.JsonSerializer.Serialize(change.After, Json.Compact) });
                }
                catch (Exception ex)
                {
                    var previous = Volatile.Read(ref _inventorySnapshot);
                    Volatile.Write(ref _inventorySnapshot, previous with { Error = "inventory-read-failed", UnavailableCategories = previous.Devices.Select(d => d.Category).Distinct().ToList() });
                    if (previous.Error != "inventory-read-failed") RecordOperationalEvent("inventory.scan.failed", "Inventory acquisition failed", AlertSeverity.Warning, "inventory",
                        data: new() { ["reason"] = ex.GetType().Name });
                    Log.Warn($"Inventário operacional: {ex.Message}");
                }
                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
    }
}
