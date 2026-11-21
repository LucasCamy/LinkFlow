using LinkFlow.App.Services;
using System.IO;

namespace LinkFlow.Tests;

public sealed class SystemOperationTests
{
    [Fact]
    public void Lost_process_tracking_is_unconfirmed_after_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "LinkFlow-operation-" + Guid.NewGuid().ToString("N"), "operations.json");
        var journal = new SystemOperationJournal(path);
        journal.Start("maintenance", "Verify Windows");
        var restored = new SystemOperationJournal(path);
        Assert.Equal(WindowsOperationState.Unconfirmed, Assert.Single(restored.Read()).State);
    }

    [Fact]
    public void Process_exit_and_confirmed_installation_remain_distinct()
    {
        var journal = new SystemOperationJournal();
        var process = journal.Start("maintenance", "Repair Windows");
        journal.Complete(process, WindowsOperationState.ProcessExited, 0);
        var driver = journal.Start("driver", "Install reviewed driver");
        journal.Complete(driver, WindowsOperationState.RequiresRestart, 3010);
        Assert.Equal(WindowsOperationState.ProcessExited, journal.Read().Single(r => r.Id == process).State);
        Assert.Equal(WindowsOperationState.RequiresRestart, journal.Read().Single(r => r.Id == driver).State);
    }
}
