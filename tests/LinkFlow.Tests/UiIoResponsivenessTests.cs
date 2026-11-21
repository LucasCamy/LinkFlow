using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

public sealed class UiIoResponsivenessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LinkFlow-ui-io-" + Guid.NewGuid().ToString("N"));
    public UiIoResponsivenessTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Export_preserves_the_destination_until_complete_and_cleans_temporary_data_on_cancellation()
    {
        string destination = Path.Combine(_directory, "export.json");
        await File.WriteAllTextAsync(destination, "original");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var writing = AtomicUserExport.WriteAsync(destination, async (temporary, ct) =>
        {
            await File.WriteAllTextAsync(temporary, "replacement", ct);
            entered.SetResult();
            await release.Task;
        }, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("original", await File.ReadAllTextAsync(destination));
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Cancel(); release.TrySetResult();
            try { await writing; } catch (OperationCanceledException) { }
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing);
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".linkflow-export-*.tmp"));
    }

    [Fact]
    public async Task Partial_export_failure_does_not_destroy_an_existing_file_and_a_retry_can_replace_it()
    {
        string destination = Path.Combine(_directory, "export.json");
        await File.WriteAllTextAsync(destination, "original");
        await Assert.ThrowsAsync<IOException>(() => AtomicUserExport.WriteAsync(destination, async (temporary, ct) =>
        {
            await File.WriteAllTextAsync(temporary, "partial", ct);
            throw new IOException("isolated writer failure");
        }));
        Assert.Equal("original", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(_directory, ".linkflow-export-*.tmp"));
        await AtomicUserExport.WriteAsync(destination, (temporary, ct) => File.WriteAllTextAsync(temporary, "complete", ct));
        Assert.Equal("complete", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task Async_backup_roundtrip_prepares_media_without_mutating_the_captured_configuration()
    {
        string media = Path.Combine(_directory, "source.png");
        SaveImage(media);
        var config = new ControlConfig { GlobalPercent = 71 };
        config.Lcd.Media.Add(new() { Path = media, Name = "image", DurationSeconds = 12 });
        string destination = Path.Combine(_directory, "backup.linkflow");
        var result = await BackupFileOperations.ExportAsync(config, null, "3.2.0", destination, CancellationToken.None);
        Assert.Equal(1, result.Media);
        File.Delete(media);
        var backup = await BackupFileOperations.LoadAsync(destination, CancellationToken.None);
        var prepared = await BackupFileOperations.PrepareImportAsync(backup, BackupSections.Lcd,
            Path.Combine(_directory, "media"), Path.Combine(_directory, "fallback"), CancellationToken.None);
        Assert.Equal(0, prepared.MissingIncludedMedia);
        Assert.NotNull(prepared.MediaMap);
        Assert.True(File.Exists(prepared.MediaMap[media]));
        Assert.Equal(media, config.Lcd.Media[0].Path);
        Assert.Equal(71, config.GlobalPercent);
    }

    [Fact]
    public async Task Invalid_async_backup_input_is_rejected_without_starting_media_extraction()
    {
        string invalid = Path.Combine(_directory, "invalid.linkflow");
        await File.WriteAllTextAsync(invalid, "{not valid");
        await Assert.ThrowsAsync<InvalidDataException>(() => BackupFileOperations.LoadAsync(invalid, CancellationToken.None));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public Task Import_rejects_a_revision_change_before_submission_without_overwriting_it_or_importing_ui_preferences() => OnUiAsync(async () =>
    {
        using var client = new UiClient();
        using var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher, Path.Combine(_directory, "alerts.json"));
        var backup = ConfigBackup.Create(new ControlConfig { GlobalPercent = 99 },
            System.Text.Json.JsonSerializer.SerializeToElement(new UiSettings { AlertNotifications = false }), "3.2.0", includeMedia: false);
        client.BeforeSubmission = () =>
        {
            client.Config.Revision++;
            client.Config.GlobalPercent = 63;
            client.BeforeSubmission = null;
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => BackupService.ApplyAsync(vm, backup, BackupSections.Control | BackupSections.Ui, "isolated backup"));
        Assert.Equal(63, client.Config.GlobalPercent);
        Assert.False(vm.IsBackupBusy);
        Assert.True(vm.CanImportBackup);
        Assert.False(vm.ToastVisible);
    });

    [Fact]
    public async Task Reviewed_submission_keeps_its_expected_revision_when_an_external_change_arrives_before_ack()
    {
        using var client = new UiClient { DeferSubmission = true };
        client.Config.Revision = 5;
        var next = client.Config.Clone(); next.GlobalPercent = 99;
        var submitted = ReviewedConfigSubmission.SubmitAsync(client, next, 5, CancellationToken.None);
        Assert.False(submitted.IsCompleted);
        Assert.Equal(5, client.LastRequest!.ExpectedRevision);
        client.Config.Revision = 6; client.Config.GlobalPercent = 63;
        client.CompleteSubmission();
        await Assert.ThrowsAsync<InvalidOperationException>(() => submitted);
        Assert.Equal(5, client.LastRequest.ExpectedRevision);
        Assert.Equal(63, client.Config.GlobalPercent);
        Assert.Equal(0, client.CommandObservers);
    }

    [Fact]
    public async Task Reviewed_submission_observes_a_synchronous_acceptance_without_using_the_rebasing_mutation_api()
    {
        using var client = new UiClient();
        var next = client.Config.Clone(); next.GlobalPercent = 77;
        var result = await ReviewedConfigSubmission.SubmitAsync(client, next, client.Config.Revision, CancellationToken.None);
        Assert.Equal(CommandState.Accepted, result.State);
        Assert.Equal(77, client.Config.GlobalPercent);
        Assert.Equal(0, client.ConfigurationMutations);
        Assert.Equal(0, client.CommandObservers);
    }

    [Fact]
    public async Task Canceling_an_unacknowledged_import_removes_its_subscription_and_does_not_treat_a_late_ack_as_completion()
    {
        using var client = new UiClient { DeferSubmission = true };
        using var cancellation = new CancellationTokenSource();
        var pending = ReviewedConfigSubmission.SubmitAsync(client, client.Config.Clone(), client.Config.Revision, cancellation.Token);
        Assert.Equal(1, client.CommandObservers);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, client.CommandObservers);
        client.CompleteSubmission();
        Assert.True(pending.IsCanceled);
    }

    [Fact]
    public async Task Unacknowledged_import_times_out_explicitly_and_removes_its_subscription()
    {
        using var client = new UiClient { DeferSubmission = true };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewedConfigSubmission.SubmitAsync(client,
            client.Config.Clone(), client.Config.Revision, CancellationToken.None, TimeSpan.FromMilliseconds(1)));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(0, client.CommandObservers);
    }

    [Fact]
    public Task Application_refresh_is_single_flight_keeps_the_ui_available_and_publishes_on_its_dispatcher() => OnUiAsync(async () =>
    {
        using var client = new UiClient();
        using var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher, Path.Combine(_directory, "alerts.json"));
        vm.RunningApps.Add("previous");
        int calls = 0, uiThread = Environment.CurrentManagedThreadId, readerThread = uiThread;
        var changedOn = new List<int>();
        vm.RunningApps.CollectionChanged += (_, _) => changedOn.Add(Environment.CurrentManagedThreadId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var refreshing = vm.RefreshAppsAsync(ct =>
        {
            Interlocked.Increment(ref calls); readerThread = Environment.CurrentManagedThreadId; entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5), ct)) throw new TimeoutException("isolated scan gate");
            return ["zeta", "Alpha", "alpha"];
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsRefreshingApps);
            Assert.False(vm.RefreshAppsCommand.CanExecute(null));
            await vm.RefreshAppsAsync(_ => { Interlocked.Increment(ref calls); return ["wrong"]; });
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal(["previous"], vm.RunningApps);
            Assert.Equal(1, calls);
            Assert.NotEqual(uiThread, readerThread);
        }
        finally { release.Set(); }
        await refreshing;
        Assert.Equal(["Alpha", "zeta"], vm.RunningApps);
        Assert.All(changedOn, id => Assert.Equal(uiThread, id));
        Assert.False(vm.IsRefreshingApps);
    });

    [Fact]
    public Task Application_refresh_failure_preserves_results_releases_busy_and_allows_a_successful_retry() => OnUiAsync(async () =>
    {
        using var client = new UiClient();
        using var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher, Path.Combine(_directory, "alerts.json"));
        vm.RunningApps.Add("previous");
        await vm.RefreshAppsAsync(_ => throw new IOException("isolated scan error"));
        Assert.Equal(["previous"], vm.RunningApps);
        Assert.Contains("isolated scan error", vm.AppRefreshStatus);
        Assert.True(vm.CanRefreshApps);
        await vm.RefreshAppsAsync(_ => ["recovered"]);
        Assert.Equal(["recovered"], vm.RunningApps);
        Assert.Equal(string.Empty, vm.AppRefreshStatus);
    });

    [Fact]
    public Task Disposing_a_view_model_prevents_late_application_results() => OnUiAsync(async () =>
    {
        using var client = new UiClient();
        using var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher, Path.Combine(_directory, "alerts.json"));
        vm.RunningApps.Add("previous");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var refresh = vm.RefreshAppsAsync(_ =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("isolated scan gate");
            return ["late"];
        });
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); vm.Dispose(); }
        finally { release.Set(); }
        await refresh;
        Assert.Equal(["previous"], vm.RunningApps);
        Assert.False(vm.CanRefreshApps);
        Assert.False(vm.IsRefreshingApps);
    });

    [Fact]
    public Task Fleet_export_captures_the_queried_peer_blocks_reentry_and_survives_selection_changes() => OnUiAsync(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FleetViewModel.HistoryExport? captured = null;
        int calls = 0;
        using var vm = new FleetViewModel(new FleetPeerRepository(Path.Combine(_directory, "peers.json")), async (_, document, ct) =>
        { calls++; captured = document; await release.Task.WaitAsync(ct); });
        var first = new FleetPeerViewModel(new() { Name = "First", Host = "first.example" });
        var second = new FleetPeerViewModel(new() { Name = "Second", Host = "second.example" });
        vm.Selected = first;
        var series = new HistorySeries { MetricKey = "cpu.temp" };
        Assert.True(vm.AcceptHistory(first, series));
        var exporting = vm.ExportHistoryAsync(Path.Combine(_directory, "export.json"));
        try
        {
            Assert.True(vm.IsBusy);
            Assert.False(vm.CanExport);
            Assert.False(await vm.ExportHistoryAsync(Path.Combine(_directory, "duplicate.json")));
            vm.Selected = second;
            Assert.False(vm.AcceptHistory(first, new()));
            Assert.Equal(1, calls);
        }
        finally { release.TrySetResult(); }
        Assert.True(await exporting);
        Assert.NotNull(captured);
        Assert.Equal("First", captured.Name);
        Assert.Equal("first.example", captured.Host);
        Assert.Same(series, captured.Series);
        Assert.False(vm.IsBusy);
        Assert.False(vm.CanExport);
    });

    [Fact]
    public Task Fleet_export_failure_releases_busy_without_reporting_success() => OnUiAsync(async () =>
    {
        using var vm = new FleetViewModel(new FleetPeerRepository(Path.Combine(_directory, "peers.json")),
            (_, _, _) => Task.FromException(new IOException("isolated export error")));
        var peer = new FleetPeerViewModel(new() { Name = "First", Host = "first.example" });
        vm.Selected = peer; vm.AcceptHistory(peer, new());
        Assert.False(await vm.ExportHistoryAsync(Path.Combine(_directory, "export.json")));
        Assert.Contains("isolated export error", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanExport);
    });

    [Fact]
    public async Task Thumbnail_admission_is_bounded_and_dispose_prevents_callbacks_from_a_delayed_decoder()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int callbacks = 0;
        using var loader = new LcdThumbnailLoader(queueCapacity: 1, read: _ =>
        { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("isolated decoder gate"); return new(null, true); });
        Assert.True(loader.TryQueue(Path.Combine(_directory, "first.png"), _ => Interlocked.Increment(ref callbacks)));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(loader.TryQueue(Path.Combine(_directory, "second.png"), _ => Interlocked.Increment(ref callbacks)));
            Assert.False(loader.TryQueue(Path.Combine(_directory, "third.png"), _ => Interlocked.Increment(ref callbacks)));
            loader.Dispose();
        }
        finally { release.Set(); }
        await loader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task Thumbnail_cache_is_bounded_by_lru_and_failed_decoding_does_not_poison_the_worker()
    {
        var counts = new Dictionary<string, int>();
        using var loader = new LcdThumbnailLoader(cacheCapacity: 2, read: path =>
        {
            string name = Path.GetFileName(path); counts[name] = counts.GetValueOrDefault(name) + 1;
            if (name == "bad.png") throw new IOException("isolated bad image");
            return new(Pixel(), true);
        });
        async Task<LcdThumbnailResult> Load(string name)
        {
            var completion = new TaskCompletionSource<LcdThumbnailResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(loader.TryQueue(Path.Combine(_directory, name), result => completion.SetResult(result)));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.NotNull((await Load("a.png")).Image);
        await Load("b.png"); await Load("a.png"); await Load("c.png"); await Load("b.png");
        Assert.Equal(1, counts["a.png"]);
        Assert.Equal(2, counts["b.png"]);
        Assert.Contains("isolated bad image", (await Load("bad.png")).Error);
        Assert.NotNull((await Load("recovered.png")).Image);
        loader.Dispose(); await loader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(160, 80)]
    [InlineData(80, 160)]
    [InlineData(8000, 8)]
    [InlineData(8, 8000)]
    public async Task Actual_thumbnail_preserves_aspect_ratio_is_frozen_bounded_and_releases_the_file(int width, int height)
    {
        string path = Path.Combine(_directory, "image.png");
        SaveImage(path, width, height);
        using var loader = new LcdThumbnailLoader();
        var completion = new TaskCompletionSource<LcdThumbnailResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(loader.TryQueue(path, result => completion.SetResult(result)));
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Exists);
        Assert.NotNull(result.Image);
        Assert.True(result.Image.IsFrozen);
        Assert.InRange(result.Image.PixelWidth, 1, 96);
        Assert.InRange(result.Image.PixelHeight, 1, 96);
        Assert.Equal(96, Math.Max(result.Image.PixelWidth, result.Image.PixelHeight));
        double scale = 96.0 / Math.Max(width, height);
        Assert.InRange(Math.Abs(result.Image.PixelWidth - width * scale), 0, 1);
        Assert.InRange(Math.Abs(result.Image.PixelHeight - height * scale), 0, 1);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(path);
        Assert.False(File.Exists(path));
        loader.Dispose(); await loader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static BitmapSource Pixel() => BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 32, 64, 128, 255 }, 4);
    private static void SaveImage(string path, int width = 1, int height = 1)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static Task OnUiAsync(Func<Task> action)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "LinkFlow UI-only IO tests" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    public void Dispose()
    {
        string target = Path.GetFullPath(_directory), boundary = Path.GetFullPath(Path.GetTempPath());
        if (!target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup target.");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
    private sealed class UiClient : IEngineClient
    {
        public bool IsRemote => true;
        public bool IsConnected => true;
        public bool IsSimulated => true;
        public string ModeDescription => "UI-only test";
        public ControlConfig Config { get; private set; } = new();
        internal Action? BeforeSubmission { get; set; }
        public IReadOnlyList<CommandReceipt> Operations => _operations;
        private readonly List<CommandReceipt> _operations = [];
        private Action<CommandReceipt>? _commandUpdated;
        public event Action<CommandReceipt>? CommandUpdated { add => _commandUpdated += value; remove => _commandUpdated -= value; }
        internal int CommandObservers => _commandUpdated?.GetInvocationList().Length ?? 0;
        internal bool DeferSubmission { get; set; }
        internal EngineCommandRequest? LastRequest { get; private set; }
        internal int ConfigurationMutations { get; private set; }
        public EngineSnapshot? Snapshot => null;
#pragma warning disable CS0067
        public event Action<EngineSnapshot>? SnapshotUpdated;
        public event Action<ControlConfig>? ConfigChanged;
        public event Action<ControlConfig>? AnyConfigChanged;
        public event Action<EngineAlert>? AlertRaised;
        public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
        public void UpdateConfig(Action<ControlConfig> mutate)
        {
            ConfigurationMutations++;
            var next = Config.Clone(); mutate(next); next.Revision++; Config = next;
        }
        public string SubmitCommand(EngineCommandRequest request)
        {
            BeforeSubmission?.Invoke();
            LastRequest = request;
            if (DeferSubmission)
            {
                var pending = new CommandReceipt { Id = request.Id, Kind = request.Kind, State = CommandState.Pending };
                _operations.Add(pending); _commandUpdated?.Invoke(pending);
            }
            else CompleteSubmission();
            return request.Id;
        }
        internal void CompleteSubmission()
        {
            var request = LastRequest!;
            bool matches = request.ExpectedRevision == Config.Revision;
            if (matches) { Config = request.Config!.Clone(); Config.Revision++; }
            var receipt = new CommandReceipt { Id = request.Id, Kind = request.Kind, Revision = Config.Revision,
                State = matches ? CommandState.Accepted : CommandState.Conflict, Message = "isolated reviewed request" };
            _operations.Add(receipt); _commandUpdated?.Invoke(receipt);
        }
        public void ReplaceConfig(ControlConfig config) { }
        public void Start() { }
        public void RequestLcdFrame() { }
        public void NotifyResume() { }
        public void RefreshRgb() { }
        public void RescanDevices() { }
        public void SetCurveTest(float? temperature) { }
        public void StartCalibration() { }
        public void CancelCalibration() { }
        public void SetSensorDemand(SensorDemand demand) { }
        public Task ShutdownAsync(bool restoreHardwareMode = true) => Task.CompletedTask;
        public void Dispose() { }
    }
}
