namespace LinkFlow.Tests;

public sealed class GpuRgbWorkerHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 22, 0, 0, TimeSpan.Zero);
    private static OperationalHistory History() => new(new() { DirectoryPath = null, TimeProvider = new Clock() });
    private static RgbControlStatus Ready(long generation, int pid) => new()
    {
        ControlAvailable = true, AdapterObservation = RgbAdapterObservation.Present,
        LastOutputAccepted = true, WorkerGeneration = generation, WorkerProcessId = pid,
        WorkerRestartCount = generation - 1
    };
    private static void Observe(OperationalHistory history, int second, RgbControlStatus status) =>
        history.RecordSnapshot(new() { Timestamp = Start.AddSeconds(second).LocalDateTime, GpuRgbControl = status });
    private static IReadOnlyList<OperationalEvent> Events(OperationalHistory history) => history.QueryEvents(new()
    { FromUtc = Start.AddSeconds(-1), ToUtc = Start.AddMinutes(5), DeviceId = "rgb.gpu", MaxItems = 1000 });

    [Fact]
    public async Task Worker_replacement_is_journaled_once_with_generation_pid_and_reason()
    {
        await using var history = History();
        Observe(history, 0, Ready(1, 100));
        Observe(history, 1, Ready(1, 100));
        var renewed = Ready(2, 200) with { LastWorkerRestartReason = "persistent-output-rejection" };
        for (int second = 2; second < 20; second++) Observe(history, second, renewed);
        var events = Events(history);
        Assert.Single(events, e => e.Kind == "rgb.worker.observed");
        var replaced = Assert.Single(events, e => e.Kind == "rgb.worker.restarted");
        Assert.Equal("2", replaced.Data["workerGeneration"]);
        Assert.Equal("1", replaced.Data["workerRestartCount"]);
        Assert.Equal("200", replaced.Data["workerProcessId"]);
        Assert.Equal("persistent-output-rejection", replaced.Data["workerRestartReason"]);
        Assert.Equal("unverified", replaced.Data["physicalOutput"]);
        Assert.DoesNotContain(events, e => e.Kind == "rgb.transport.failed");
    }

    [Fact]
    public async Task A_reserved_generation_without_a_worker_is_not_a_process_start_or_restart()
    {
        await using var history = History();
        Observe(history, 0, new() { WorkerGeneration = 1, WorkerTransportFailures = 1, InitializationRetryNeeded = true });
        Observe(history, 1, new() { WorkerGeneration = 2, WorkerTransportFailures = 2, InitializationRetryNeeded = true });
        Assert.DoesNotContain(Events(history), e => e.Kind is "rgb.worker.observed" or "rgb.worker.restarted");
        Observe(history, 2, Ready(3, 300) with { WorkerRestartCount = 0, WorkerTransportFailures = 2 });
        Assert.Single(Events(history), e => e.Kind == "rgb.worker.observed");
        Assert.DoesNotContain(Events(history), e => e.Kind == "rgb.worker.restarted");
    }

    [Fact]
    public async Task Worker_communication_error_does_not_invent_a_native_failure_or_repeat_on_each_snapshot()
    {
        await using var history = History();
        Observe(history, 0, Ready(1, 100));
        var failed = Ready(1, 100) with
        {
            ControlAvailable = false, LastOutputAccepted = false, InitializationRetryNeeded = true,
            WorkerTransportFailures = 1, LastWorkerFailureUtc = Start.AddSeconds(1),
            LastWorkerFailure = "Response deadline exceeded"
        };
        for (int second = 1; second < 10; second++) Observe(history, second, failed);
        Observe(history, 10, Ready(2, 200) with
        {
            WorkerTransportFailures = 1, LastWorkerFailureUtc = failed.LastWorkerFailureUtc,
            LastWorkerFailure = failed.LastWorkerFailure
        });
        var events = Events(history);
        var error = Assert.Single(events, e => e.Kind == "rgb.worker.transport.failed");
        Assert.Equal("1", error.Data["workerTransportFailures"]);
        Assert.Equal("0", error.Data["totalNativeFailures"]);
        Assert.Equal("Response deadline exceeded", error.Data["workerLastFailure"]);
        Assert.False(error.Data.ContainsKey("nativeStatus"));
        Assert.DoesNotContain(events, e => e.Kind == "rgb.transport.failed");
    }

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Start.AddMinutes(1);
    }
}
