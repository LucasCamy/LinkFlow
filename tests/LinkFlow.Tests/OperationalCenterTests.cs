using System.IO;
using System.Windows.Threading;
using LinkFlow.App.Controls;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

public sealed class OperationalCenterTests
{
    private static OperationalCenterViewModel Create(FakeClient client, SystemOperationJournal? journal = null) =>
        new(client, Dispatcher.CurrentDispatcher, journal ?? new SystemOperationJournal());

    [Fact]
    public void Safety_status_has_priority_over_successful_rgb_sync()
    {
        using var client = new FakeClient();
        using var vm = Create(client);
        vm.ApplySnapshot(new() { SensorsReady = true, FailSafeActive = true, RgbStatus = "Sincronizado", GpuRgbApplied = true,
            FailSafeReason = "Bomba sem leitura" });
        Assert.Equal("#FB7185", vm.ProtectionColor);
        Assert.Equal(OperationalText.Pick("Modo de segurança ativo", "Safety mode active"), vm.ProtectionTitle);
        Assert.Contains(vm.Guidance, g => g.Color == "#FB7185");
    }

    [Fact]
    public void Missing_rpm_is_unknown_and_lcd_does_not_offer_speed_control()
    {
        using var client = new FakeClient();
        using var vm = Create(client);
        vm.ApplySnapshot(new()
        {
            Fans = [new() { Key = "pump", DefaultName = "Pump", IsPump = true, RpmAvailable = false, Rpm = 0,
                RpmValidity = ReadingValidity.Stale, RequestedPercent = 100, AppliedPercent = 100, ControlReason = "safety" },
                new() { Key = "lcd", DefaultName = "LCD", Model = LinkDeviceModel.PumpLcd.ToString(), Rpm = 0, AppliedPercent = 50, ControlReason = "unsupported" }]
        });
        Assert.Equal("—", vm.Fans[0].Rpm);
        Assert.Equal("100%", vm.Fans[0].Requested);
        Assert.Equal("—", vm.Fans[1].Rpm);
        Assert.Equal("—", vm.Fans[1].Requested);
        Assert.Equal("—", vm.Fans[1].Accepted);
        Assert.Equal(OperationalText.Validity(ReadingValidity.NotSupported), vm.Fans[1].State);
    }

    [Fact]
    public void Disconnection_marks_even_previously_fresh_values_as_stale()
    {
        using var client = new FakeClient { IsConnected = false };
        using var vm = Create(client);
        vm.ApplySnapshot(new()
        {
            SensorsReady = true,
            TelemetryHealth = [new() { Key = "cpu.temp", Validity = ReadingValidity.Fresh, AgeSeconds = .1 }],
            Fans = [new() { Key = "fan", DefaultName = "Fan", RpmAvailable = true, Rpm = 1200, RpmValidity = ReadingValidity.Fresh }]
        });
        Assert.Equal(OperationalText.Validity(ReadingValidity.Stale), Assert.Single(vm.Sensors).State);
        Assert.Equal("#FBBF24", vm.Sensors[0].Color);
        Assert.Equal("—", Assert.Single(vm.Fans).Rpm);
        Assert.Equal(OperationalText.Pick("Sem comunicação", "Disconnected"), vm.ProtectionTitle);
    }

    [Fact]
    public void Failed_save_is_visible_with_active_and_saved_revisions()
    {
        using var client = new FakeClient();
        using var vm = Create(client);
        vm.ApplySnapshot(new()
        {
            SensorsReady = true, ConfigRevision = 11,
            Persistence = new() { ActiveRevision = 11, SavedRevision = 10, State = PersistenceState.Failed, Error = "disk full" }
        });
        Assert.Equal(OperationalText.Persistence(PersistenceState.Failed), vm.PersistenceTitle);
        Assert.Contains("11 / 10", vm.PersistenceDetail);
        Assert.Contains("disk full", vm.PersistenceDetail);
        Assert.Equal("#FBBF24", vm.ProtectionColor);
    }

    [Fact]
    public void Controller_acknowledgement_is_distinguished_from_physical_feedback()
    {
        using var client = new FakeClient();
        using var vm = Create(client);
        vm.ApplySnapshot(new()
        {
            Operations = [new() { Id = "test", Kind = "setConfig", State = CommandState.Applied,
                Confirmation = CommandConfirmation.Controller, Persistence = PersistenceState.Pending, UpdatedAtUtc = DateTimeOffset.UtcNow }]
        });
        var command = Assert.Single(vm.Commands);
        Assert.Equal(OperationalText.Confirmation(CommandConfirmation.Controller), command.Confirmation);
        Assert.NotEqual(OperationalText.Confirmation(CommandConfirmation.Physical), command.Confirmation);
        Assert.Equal(OperationalText.Persistence(PersistenceState.Pending), command.Persistence);
    }

    [Fact]
    public async Task History_query_preserves_gap_points_and_reports_statistics_from_the_service()
    {
        using var client = new FakeClient();
        var now = DateTimeOffset.UtcNow;
        client.QueryHandler = (_, _) => Task.FromResult(new HistorySeries
        {
            MetricKey = "cpu.temp", Unit = "°C", FromUtc = now.AddSeconds(-2), ToUtc = now,
            Points = [new() { TimestampUtc = now.AddSeconds(-2), Value = 50, Validity = ReadingValidity.Fresh },
                new() { TimestampUtc = now.AddSeconds(-1), Value = null, Validity = ReadingValidity.Unavailable }],
            Statistics = new() { Min = 50, Max = 50, Average = 50, P95 = 50, CoveragePercent = 50, ValidSamples = 1 }
        });
        using var vm = Create(client);
        await vm.RefreshHistoryAsync();
        Assert.Equal(2, vm.HistoryPoints.Count);
        Assert.Null(vm.HistoryPoints[1].Value);
        Assert.Contains("50", vm.Coverage);
        Assert.Equal(OperationalText.Number(50, "°C"), vm.P95);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Late_old_query_does_not_replace_a_newer_result()
    {
        using var client = new FakeClient();
        var oldResult = new TaskCompletionSource<HistorySeries>(TaskCreationOptions.RunContinuationsAsynchronously);
        int queries = 0;
        client.QueryHandler = (_, _) => Interlocked.Increment(ref queries) == 1 ? oldResult.Task : Task.FromResult(new HistorySeries { MetricKey = "newer" });
        using var vm = Create(client);
        var old = vm.RefreshHistoryAsync();
        await vm.RefreshHistoryAsync();
        oldResult.SetResult(new() { MetricKey = "older" });
        await old;
        Assert.Equal("newer", vm.Series!.MetricKey);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Disposing_the_view_model_cancels_inflight_query()
    {
        using var client = new FakeClient();
        bool canceled = false;
        client.QueryHandler = (_, ct) =>
        {
            var task = new TaskCompletionSource<HistorySeries>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => { canceled = true; task.TrySetCanceled(ct); });
            return task.Task;
        };
        var vm = Create(client);
        var query = vm.RefreshHistoryAsync();
        vm.Dispose();
        await query;
        Assert.True(canceled);
    }

    [Fact]
    public async Task Query_failure_is_reported_without_fabricating_a_successful_result()
    {
        using var client = new FakeClient { QueryHandler = (_, _) => Task.FromException<HistorySeries>(new IOException("pipe closed")) };
        using var vm = Create(client);
        await vm.RefreshHistoryAsync();
        Assert.Contains("pipe closed", vm.Status);
        Assert.Null(vm.Series);
        Assert.Equal("—", vm.Coverage);
    }

    [Fact]
    public void Chart_breaks_lines_at_invalid_and_missing_buckets()
    {
        var now = DateTimeOffset.UtcNow;
        var points = new HistoryPoint[]
        {
            new() { TimestampUtc = now, Value = 50, Validity = ReadingValidity.Fresh },
            new() { TimestampUtc = now.AddSeconds(1), Value = null, Validity = ReadingValidity.Unavailable },
            new() { TimestampUtc = now.AddSeconds(2), Value = 90, Validity = ReadingValidity.Stale },
            new() { TimestampUtc = now.AddSeconds(3), Value = 55, Validity = ReadingValidity.Fresh },
            new() { TimestampUtc = now.AddSeconds(4), Value = 60, Validity = ReadingValidity.Fresh }
        };
        var segments = OperationalHistoryChart.ValidSegments(points);
        Assert.Equal(2, segments.Count);
        Assert.Single(segments[0]);
        Assert.Equal(2, segments[1].Length);
        Assert.DoesNotContain(segments.SelectMany(s => s), p => p.Value == 90);
    }

    [Fact]
    public void Tool_process_exit_is_not_presented_as_a_verified_repair()
    {
        using var client = new FakeClient();
        var journal = new SystemOperationJournal();
        var id = journal.Start("maintenance", "SFC");
        journal.Complete(id, WindowsOperationState.ProcessExited, 0, "Check the tool log.");
        using var vm = Create(client, journal);
        vm.ApplySnapshot(new());
        var operation = Assert.Single(vm.WindowsOperations);
        Assert.Equal(OperationalText.Pick("Processo encerrado", "Process exited"), operation.State);
        Assert.Contains("Check the tool log", operation.Detail);
    }

    [Fact]
    public async Task Export_writes_the_service_result_after_a_complete_download()
    {
        string directory = Path.Combine(Path.GetTempPath(), "LinkFlow-operation-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var client = new FakeClient();
            using var vm = Create(client);
            string path = Path.Combine(directory, "history.csv");
            await vm.ExportAsync(path, true);
            Assert.Equal("timestamp_utc,value\n2026-11-20T00:00:00Z,50", await File.ReadAllTextAsync(path));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Individual_device_history_uses_the_selected_stable_device_key()
    {
        using var client = new FakeClient();
        string? requestedDevice = null;
        client.QueryHandler = (query, _) => { requestedDevice = query.DeviceId; return Task.FromResult(new HistorySeries()); };
        using var vm = Create(client);
        vm.SelectedMetric = vm.Metrics.Single(m => m.Key == "fan.rpm");
        vm.ApplySnapshot(new() { Fans = [new() { Key = "stable-fan-A", DefaultName = "Radiator fan" }] });
        vm.SelectedDevice = vm.Devices.Single(d => d.Id == "stable-fan-A");
        await vm.RefreshHistoryAsync();
        Assert.Equal("stable-fan-A", requestedDevice);
        Assert.True(vm.HasDeviceFilter);
        vm.ApplySnapshot(new());
        Assert.Equal("stable-fan-A", vm.SelectedDevice!.Id); // still available for historical queries after removal
    }

    [Fact]
    public void Old_snapshots_are_unconfirmed_even_if_the_pipe_is_still_connected()
    {
        using var client = new FakeClient();
        using var vm = Create(client);
        vm.ApplySnapshot(new() { Timestamp = DateTime.Now.AddSeconds(-10), SensorsReady = true,
            TelemetryHealth = [new() { Key = "cpu.temp", Validity = ReadingValidity.Fresh, LastSuccessUtc = DateTimeOffset.UtcNow.AddSeconds(-10) }] });
        Assert.Equal(OperationalText.Pick("Sem comunicação", "Disconnected"), vm.ProtectionTitle);
        Assert.Equal("0 / 1", vm.QualityTitle);
        Assert.Equal(OperationalText.Validity(ReadingValidity.Stale), vm.Sensors[0].State);
    }

    private sealed class FakeClient : IEngineClient
    {
        public bool IsRemote => true;
        public bool IsConnected { get; set; } = true;
        public bool IsSimulated => true;
        public string ModeDescription => "test";
        public ControlConfig Config { get; } = new();
        public EngineSnapshot? Snapshot => null;
        public Func<HistoryQuery, CancellationToken, Task<HistorySeries>> QueryHandler { get; set; } = (_, _) => Task.FromResult(new HistorySeries());
#pragma warning disable CS0067
        public event Action<EngineSnapshot>? SnapshotUpdated;
        public event Action<ControlConfig>? ConfigChanged;
        public event Action<ControlConfig>? AnyConfigChanged;
        public event Action<EngineAlert>? AlertRaised;
        public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
        public void Start() { }
        public void UpdateConfig(Action<ControlConfig> mutate) => mutate(Config);
        public void ReplaceConfig(ControlConfig config) { }
        public void RequestLcdFrame() { }
        public void NotifyResume() { }
        public void RefreshRgb() { }
        public void RescanDevices() { }
        public void SetCurveTest(float? temperature) { }
        public void StartCalibration() { }
        public void CancelCalibration() { }
        public void SetSensorDemand(SensorDemand demand) { }
        public Task ShutdownAsync(bool restoreHardwareMode = true) => Task.CompletedTask;
        public Task<HistorySeries> QueryHistoryAsync(HistoryQuery query, CancellationToken ct = default) => QueryHandler(query, ct);
        public Task<IReadOnlyList<OperationalEvent>> QueryEventsAsync(EventQuery query, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<OperationalEvent>>([]);
        public Task<IReadOnlyList<HistorySession>> QuerySessionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HistorySession>>([]);
        public Task<string> ExportHistoryAsync(HistoryQuery query, bool csv, EventQuery? events = null, CancellationToken ct = default) => Task.FromResult("timestamp_utc,value\n2026-11-20T00:00:00Z,50");
        public void Dispose() { }
    }
}
