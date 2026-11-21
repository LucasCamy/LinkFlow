using System.IO;

namespace LinkFlow.Tests;

public sealed class InventoryHistoryTests
{
    [Fact]
    public void Provider_mutation_cannot_change_the_saved_baseline()
    {
        var history = new InventoryHistory();
        var gpu = Gpu("1");
        history.Observe(new() { Facts = [gpu] });
        gpu.Fields["driver"] = "2";
        var change = Assert.Single(history.Observe(new() { Facts = [Gpu("2")] }));
        Assert.Equal("1", change.Before["driver"]);
    }

    [Fact]
    public void Truncated_category_is_incomplete_instead_of_a_false_removal()
    {
        var history = new InventoryHistory();
        history.Observe(new() { Facts = [Gpu("1")] });
        var facts = Enumerable.Range(0, 512).Select(i => new InventoryFact { Id = "disk-" + i, Category = "disk", Name = "Disk" }).ToList();
        facts.Add(new() { Id = "gpu-other", Category = "gpu", Name = "Other GPU" });
        var changes = history.Observe(new() { Facts = facts });
        Assert.DoesNotContain(changes, c => c.Category == "gpu" && c.Change == "removed");
        Assert.Contains("gpu", history.Status.UnavailableCategories);
        Assert.Contains(history.Status.Devices, d => d.Id == "pci-gpu-1");
    }

    [Fact]
    public void Provider_failure_is_observable_without_discarding_last_inventory()
    {
        int attempts = 0;
        using var fixture = new EngineFixture(configure: options =>
        {
            options.InventoryInterval = TimeSpan.FromMilliseconds(50);
            options.InventoryProvider = () => Interlocked.Increment(ref attempts) == 1 ? new() { Facts = [Gpu("1")] } : throw new IOException("simulated inventory failure");
        });
        EngineFixture.WaitUntil(() => fixture.Engine.GetSnapshot().Inventory.Error != null);
        Assert.Single(fixture.Engine.GetSnapshot().Inventory.Devices);
    }

    private static InventoryFact Gpu(string version) => new() { Id = "pci-gpu-1", Category = "gpu", Name = "GPU", Fields = new() { ["driver"] = version } };

    [Fact]
    public void Changed_driver_is_recorded_and_failed_scan_does_not_remove_a_device()
    {
        var history = new InventoryHistory();
        Assert.Empty(history.Observe(new() { Facts = [Gpu("1")] }));
        var change = Assert.Single(history.Observe(new() { Facts = [Gpu("2")] }));
        Assert.Equal("1", change.Before["driver"]);
        Assert.Equal("2", change.After["driver"]);
        Assert.Empty(history.Observe(new() { UnavailableCategories = ["gpu"] }));
        Assert.Single(history.Status.Devices);
        Assert.Equal("removed", Assert.Single(history.Observe(new())).Change);
    }

    [Fact]
    public void Baseline_and_changes_survive_restart_without_sharing_mutable_state()
    {
        string path = Path.Combine(Path.GetTempPath(), "LinkFlow-inventory-" + Guid.NewGuid().ToString("N"), "inventory.json");
        var history = new InventoryHistory(path);
        history.Observe(new() { Facts = [Gpu("1")] });
        var restored = new InventoryHistory(path);
        Assert.Equal("changed", Assert.Single(restored.Observe(new() { Facts = [Gpu("2")] })).Change);
        restored.Status.Devices.Clear();
        Assert.Single(restored.Status.Devices);
    }
}
