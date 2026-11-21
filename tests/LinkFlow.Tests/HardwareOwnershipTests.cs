namespace LinkFlow.Tests;

public sealed class HardwareOwnershipTests
{
    [Fact]
    public void A_second_controller_is_rejected_until_first_releases_its_lease()
    {
        string name = @"Local\LinkFlow.Ownership.Test." + Guid.NewGuid().ToString("N");
        using (var owner = HardwareControlLease.AcquireForTests(name))
            Assert.Throws<InvalidOperationException>(() => HardwareControlLease.AcquireForTests(name));
        using var next = HardwareControlLease.AcquireForTests(name);
    }

    [Fact]
    public async Task Ownership_can_be_released_from_a_different_thread()
    {
        string name = @"Local\LinkFlow.Ownership.Test." + Guid.NewGuid().ToString("N");
        var owner = HardwareControlLease.AcquireForTests(name);
        await Task.Run(owner.Dispose);
        owner.Dispose();
        using var next = HardwareControlLease.AcquireForTests(name);
    }

    [Fact]
    public void Test_ownership_hook_refuses_the_production_name_without_opening_it()
    {
        Assert.Throws<ArgumentException>(() => HardwareControlLease.AcquireForTests(HardwareControlLease.DefaultName));
    }

    [Fact]
    public void Parallel_tests_use_independent_local_ownership_names()
    {
        string first = @"Local\LinkFlow.Ownership.Test." + Guid.NewGuid().ToString("N");
        string second = @"Local\LinkFlow.Ownership.Test." + Guid.NewGuid().ToString("N");
        using var firstOwner = HardwareControlLease.AcquireForTests(first);
        using var secondOwner = HardwareControlLease.AcquireForTests(second);
        Assert.Throws<InvalidOperationException>(() => HardwareControlLease.AcquireForTests(first));
        Assert.Throws<InvalidOperationException>(() => HardwareControlLease.AcquireForTests(second));
    }
}
