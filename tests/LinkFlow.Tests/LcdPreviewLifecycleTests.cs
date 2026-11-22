using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using LinkFlow.App.Services;
using LinkFlow.App.ViewModels;

namespace LinkFlow.Tests;

public sealed class LcdPreviewLifecycleTests
{
    [Fact]
    public async Task Busy_dispatcher_retains_one_frame_and_stops_additional_composition()
    {
        var renderer = new FakeRenderer();
        var queue = new FakeQueue();
        using var pump = Create(renderer, queue, _ => { });
        try
        {
            pump.Active = true;
            await Until(() => queue.Count == 1);
            await Task.Delay(150); // Several 40ms frame periods with no dispatcher progress.
            Assert.Equal(1, renderer.RenderCalls);
            Assert.Equal(1, queue.MaximumPending);
            Assert.Equal(0, Assert.Single(renderer.Frames).Releases);
        }
        finally { pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, Assert.Single(renderer.Frames).Releases);
    }

    [Fact]
    public async Task Invalidating_pending_content_replaces_it_without_adding_another_callback()
    {
        var renderer = new FakeRenderer();
        var queue = new FakeQueue();
        var published = new List<FakeFrame>();
        using var pump = Create(renderer, queue, published.Add);
        try
        {
            pump.Active = true;
            await Until(() => queue.Count == 1);
            var first = Assert.Single(renderer.Frames);
            pump.Invalidate();
            await Until(() => renderer.Frames.Count == 2 && pump.HasPendingFrame);
            Assert.Equal(1, queue.Count);
            Assert.Equal(1, first.Releases);
            queue.DrainOne();
            pump.Active = false;
            Assert.Equal(2, Assert.Single(published).Id);
            Assert.Equal(1, queue.MaximumPending);
        }
        finally { pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.All(renderer.Frames, frame => Assert.Equal(1, frame.Releases));
    }

    [Fact]
    public async Task Deactivation_drops_queued_frame_and_releases_renderer_on_its_owner_thread()
    {
        var renderer = new FakeRenderer();
        var queue = new FakeQueue();
        int published = 0;
        using var pump = Create(renderer, queue, _ => published++);
        try
        {
            pump.Active = true;
            await Until(() => queue.Count == 1);
            pump.Active = false;
            await Until(() => renderer.DisposeCalls == 1);
            queue.DrainOne();
            Assert.Equal(0, published);
            Assert.Equal(1, Assert.Single(renderer.Frames).Releases);
            Assert.Equal(renderer.RenderThread, renderer.DisposeThread);
        }
        finally { pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Disposing_during_render_returns_without_waiting_and_discards_the_inflight_frame()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var renderer = new FakeRenderer { Entered = entered, Continue = release };
        var queue = new FakeQueue();
        int published = 0;
        using var pump = Create(renderer, queue, _ => published++);
        try
        {
            pump.Active = true;
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            pump.Dispose();
            Assert.False(pump.Completion.IsCompleted); // Decoder is still inside its fake gate.
            Assert.Equal(0, renderer.DisposeCalls);
        }
        finally { release.Set(); pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, published);
        Assert.Equal(1, Assert.Single(renderer.Frames).Releases);
        Assert.Equal(renderer.RenderThread, renderer.DisposeThread);
    }

    [Fact]
    public async Task Configuration_change_during_render_prevents_publication_of_old_content()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var renderer = new FakeRenderer { Entered = entered, Continue = release };
        var queue = new FakeQueue();
        var published = new List<FakeFrame>();
        using var pump = Create(renderer, queue, published.Add);
        try
        {
            pump.Active = true;
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            pump.Invalidate();
            release.Set();
            await Until(() => renderer.Frames.Count >= 2 && pump.HasPendingFrame && queue.Count == 1);
            queue.DrainOne();
            pump.Active = false;
            Assert.Equal(2, Assert.Single(published).Id);
            Assert.Equal(1, renderer.Frames.First().Releases);
            Assert.Equal(1, queue.MaximumPending);
        }
        finally { release.Set(); pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Queued_callback_after_disposal_is_harmless_and_cannot_reactivate_the_pump()
    {
        var renderer = new FakeRenderer();
        var queue = new FakeQueue();
        int published = 0;
        using var pump = Create(renderer, queue, _ => published++);
        pump.Active = true;
        await Until(() => queue.Count == 1);
        pump.Dispose();
        await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        queue.DrainOne();
        pump.Active = true;
        pump.Invalidate();
        Assert.False(pump.Active);
        Assert.Equal(0, published);
        Assert.Equal(1, renderer.RenderCalls);
        Assert.Equal(1, Assert.Single(renderer.Frames).Releases);
    }

    [Fact]
    public async Task Reactivation_creates_a_fresh_renderer_while_reusing_only_the_pending_callback()
    {
        var first = new FakeRenderer();
        var second = new FakeRenderer();
        var renderers = new Queue<ILcdPreviewRenderer<FakeFrame>>([first, second]);
        var queue = new FakeQueue();
        var published = new List<FakeFrame>();
        using var pump = new LcdPreviewPump<FakeFrame>(() => renderers.Dequeue(), queue.Enqueue, published.Add, f => f.Release(), _ => { });
        try
        {
            pump.Active = true;
            await Until(() => queue.Count == 1);
            pump.Active = false;
            await Until(() => first.DisposeCalls == 1);
            pump.Active = true;
            await Until(() => second.Frames.Count == 1 && pump.HasPendingFrame);
            Assert.Equal(1, queue.Count);
            queue.DrainOne();
            pump.Active = false;
            Assert.Same(Assert.Single(second.Frames), Assert.Single(published));
            Assert.Equal(1, Assert.Single(first.Frames).Releases);
            Assert.Equal(1, queue.MaximumPending);
        }
        finally { pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Dispatcher_rejection_releases_the_frame_and_still_allows_clean_shutdown()
    {
        var renderer = new FakeRenderer();
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pump = new LcdPreviewPump<FakeFrame>(() => renderer, _ => throw new OperationCanceledException("Fake dispatcher stopped."),
            _ => throw new InvalidOperationException("No publication expected."), f => f.Release(), _ => error.TrySetResult());
        try { pump.Active = true; await error.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, Assert.Single(renderer.Frames).Releases);
        Assert.Equal(1, renderer.DisposeCalls);
    }

    [Fact]
    public async Task Dispatcher_progress_does_not_bypass_the_render_interval()
    {
        var renderer = new FakeRenderer { Delay = TimeSpan.FromMilliseconds(250) };
        var queue = new FakeQueue();
        using var pump = Create(renderer, queue, _ => { });
        try
        {
            pump.Active = true;
            await Until(() => queue.Count == 1);
            queue.DrainOne();
            await Until(() => renderer.Frames.Count == 2 && pump.HasPendingFrame);
            var frames = renderer.Frames.ToArray();
            Assert.True(Stopwatch.GetElapsedTime(frames[0].At, frames[1].At) >= TimeSpan.FromMilliseconds(200));
        }
        finally { pump.Dispose(); await pump.Completion.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task Disposed_view_model_detaches_config_and_ignores_already_queued_updates()
    {
        await OnUi(async () =>
        {
            var client = new FakeClient();
            using var vm = new MainViewModel(client, Dispatcher.CurrentDispatcher, alertHistoryPath: null);
            byte initialPercent = vm.GlobalSpeedPercent;
            foreach (string name in new[] { "_curveTestTimer", "_toastTimer", "_recentTimer" }) Timer(vm, name).Start();
            vm.RescanDevices(); // Fake client only, also starts the fourth timer.
            Assert.Equal(1, client.ConfigObservers);
            client.EmitConfig();
            client.EmitSnapshot();
            vm.Dispose();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Assert.Equal(initialPercent, vm.GlobalSpeedPercent);
            Assert.NotEqual(91f, vm.Snapshot.CpuTemp);
            Assert.Equal(0, client.ConfigObservers);
            Assert.Equal(0, client.SnapshotObservers);
            foreach (string name in new[] { "_curveTestTimer", "_toastTimer", "_recentTimer", "_rescanTimer" }) Assert.False(Timer(vm, name).IsEnabled);
        });
    }

    private static DispatcherTimer Timer(MainViewModel vm, string name) =>
        (DispatcherTimer)typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
    private static LcdPreviewPump<FakeFrame> Create(FakeRenderer renderer, FakeQueue queue, Action<FakeFrame> publish) =>
        new(() => renderer, queue.Enqueue, publish, frame => frame.Release(), _ => { });
    private static async Task Until(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition()) { if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Preview fixture did not progress."); await Task.Delay(10); }
    }
    private static Task OnUi(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class FakeFrame(int id)
    {
        private int _releases;
        internal int Id => id;
        internal long At { get; } = Stopwatch.GetTimestamp();
        internal int Releases => Volatile.Read(ref _releases);
        internal void Release() => Interlocked.Increment(ref _releases);
    }
    private sealed class FakeRenderer : ILcdPreviewRenderer<FakeFrame>
    {
        private int _renders, _disposes;
        internal ConcurrentQueue<FakeFrame> Frames { get; } = new();
        internal int RenderCalls => Volatile.Read(ref _renders);
        internal int DisposeCalls => Volatile.Read(ref _disposes);
        internal int RenderThread, DisposeThread;
        internal TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(40);
        internal ManualResetEventSlim? Entered, Continue;
        public (FakeFrame Frame, TimeSpan Delay) Render()
        {
            RenderThread = Environment.CurrentManagedThreadId;
            int id = Interlocked.Increment(ref _renders);
            Entered?.Set();
            if (Continue != null && !Continue.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fake renderer gate.");
            var frame = new FakeFrame(id); Frames.Enqueue(frame);
            return (frame, Delay);
        }
        public void Dispose() { DisposeThread = Environment.CurrentManagedThreadId; Interlocked.Increment(ref _disposes); }
    }
    private sealed class FakeQueue
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        private int _maximum;
        internal int Count => _queue.Count;
        internal int MaximumPending => Volatile.Read(ref _maximum);
        internal void Enqueue(Action action) { _queue.Enqueue(action); int count = _queue.Count; if (count > _maximum) Volatile.Write(ref _maximum, count); }
        internal void DrainOne() { Assert.True(_queue.TryDequeue(out var action)); action!(); }
    }
    private sealed class FakeClient : IEngineClient
    {
        private Action<ControlConfig>? _config;
        private Action<EngineSnapshot>? _snapshot;
        internal int ConfigObservers => _config?.GetInvocationList().Length ?? 0;
        internal int SnapshotObservers => _snapshot?.GetInvocationList().Length ?? 0;
        public bool IsRemote => true;
        public bool IsConnected => true;
        public bool IsSimulated => true;
        public string ModeDescription => "Preview UI fixture";
        public ControlConfig Config { get; } = new();
        public EngineSnapshot? Snapshot => null;
        public event Action<ControlConfig>? ConfigChanged { add => _config += value; remove => _config -= value; }
        public event Action<EngineSnapshot>? SnapshotUpdated { add => _snapshot += value; remove => _snapshot -= value; }
#pragma warning disable CS0067
        public event Action<ControlConfig>? AnyConfigChanged;
        public event Action<EngineAlert>? AlertRaised;
        public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
        internal void EmitConfig() { var next = Config.Clone(); next.GlobalPercent = 26; _config?.Invoke(next); }
        internal void EmitSnapshot() => _snapshot?.Invoke(new EngineSnapshot { CpuTemp = 91 });
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
        public void Dispose() { }
    }
}
