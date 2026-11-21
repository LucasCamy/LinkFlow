using System.Diagnostics;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class LifecycleIntegrationTests
{
    [Fact]
    public async Task A_stopped_engine_cannot_open_hardware_or_restart_timers()
    {
        var factory = new CountingFactory(simulated: true);
        using var engine = new LinkFlowEngine(factory, new EngineOptions { ConfigPath = null });
        await engine.StopAsync();
        Assert.Throws<ObjectDisposedException>(engine.Start);
        Assert.Equal(0, factory.Inner.Hub.ConnectCalls);
        Assert.Equal(0, factory.Inner.Sensors.OpenCalls);
        Assert.Equal(0, factory.Inner.GpuRgb.InitializationCalls);
    }

    [Fact]
    public void Disposing_waiting_client_while_connector_blocks_disposes_the_returned_proxy_without_starting()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var proxy = new SpyClient();
        using var waiting = new WaitingServiceClient(() =>
        {
            entered.Set(); release.Wait(); return proxy;
        });
        int connectedEvents = 0;
        waiting.ConnectionChanged += _ => Interlocked.Increment(ref connectedEvents);
        waiting.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        waiting.Dispose();
        release.Set();
        EngineFixture.WaitUntil(() => proxy.DisposeCalls == 1, because: "proxy recebido depois do encerramento foi descartado");
        Assert.Equal(0, proxy.StartCalls);
        Assert.Equal(0, proxy.DemandCalls);
        Assert.Equal(0, connectedEvents);
        Assert.False(waiting.IsConnected);
    }

    [Fact]
    public async Task Shutdown_is_bounded_when_a_sensor_driver_holds_its_device_lock()
    {
        var factory = new CountingFactory(simulated: true);
        using var engine = new LinkFlowEngine(factory, new EngineOptions
        {
            ConfigPath = null, TelemetryInterval = TimeSpan.FromMilliseconds(40), SnapshotInterval = TimeSpan.FromMilliseconds(40),
            LaneStallTimeout = TimeSpan.FromSeconds(30), InventoryProvider = () => new InventoryObservation()
        });
        engine.Start();
        EngineFixture.WaitUntil(() => engine.GetSnapshot().CpuSensorAvailable);
        factory.Inner.Sensors.ReadGate.Reset();
        EngineFixture.WaitUntil(() => factory.Inner.Sensors.IsReadWaiting, because: "leitura simulada bloqueada dentro do lock do dispositivo");
        var watch = Stopwatch.StartNew();
        var stop = engine.StopAsync();
        try
        {
            await stop.WaitAsync(TimeSpan.FromSeconds(9));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"StopAsync demorou {watch.Elapsed}.");
            Assert.True(factory.Inner.Sensors.IsReadWaiting, "O encerramento retornou sem exigir que o driver desbloqueasse.");
            Assert.False(factory.Sensors.Disposed);
        }
        finally { factory.Inner.Sensors.ReadGate.Set(); }
        EngineFixture.WaitUntil(() => factory.Sensors.Disposed && !factory.Inner.Sensors.IsReadWaiting,
            because: "a limpeza pendente termina quando o driver finalmente responde");
    }

    [Fact]
    public async Task Failing_exclusive_start_never_controls_or_restores_the_other_owners_hardware()
    {
        // Adaptadores e posse são isolados: este teste não abre o mutex global do serviço real.
        string name = @"Local\LinkFlow.Ownership.Test." + Guid.NewGuid().ToString("N");
        using var owner = HardwareControlLease.AcquireForTests(name);
        var factory = new CountingFactory(simulated: false);
        using var engine = new LinkFlowEngine(factory, new EngineOptions
        { ConfigPath = null, InventoryProvider = () => new InventoryObservation(), HardwareLeaseNameForTests = name });
        Assert.Throws<InvalidOperationException>(engine.Start);
        await engine.StopAsync();
        Assert.Equal(0, factory.Inner.Hub.ConnectCalls);
        Assert.Equal(0, factory.Hub.DisconnectCalls);
        Assert.Equal(0, factory.Inner.Sensors.OpenCalls);
        Assert.Equal(0, factory.Sensors.AutoCalls);
        Assert.Equal(0, factory.Inner.GpuRgb.InitializationCalls);
        Assert.False(factory.Sensors.Disposed);
    }

    private sealed class CountingFactory(bool simulated) : IHardwareFactory
    {
        public SimulatedHardwareFactory Inner { get; } = new();
        private CountingSensors? _sensors;
        private CountingHub? _hub;
        public CountingSensors Sensors => _sensors ??= new(Inner.Sensors);
        public CountingHub Hub => _hub ??= new(Inner.Hub);
        public bool IsSimulated => simulated;
        public IFanHub CreateHub() => Hub;
        public ISensorSource CreateSensors() { Inner.Sensors.OpenDelay = TimeSpan.Zero; return Sensors; }
        public ILcdDisplay CreateLcd() => Inner.Lcd;
        public IRgbDevice CreateGpuRgb() => Inner.GpuRgb;
        public IRgbDevice CreateMotherboardRgb() => Inner.MotherboardRgb;
        public IRgbDevice CreateRamRgb() => Inner.RamRgb;
        public IRgbDevice CreatePeripheralsRgb() => Inner.PeripheralsRgb;
    }

    private sealed class CountingSensors(SimulatedSensors inner) : ISensorSource
    {
        private readonly object _driverLock = new();
        public int AutoCalls;
        public bool Disposed;
        public bool IsOpen => inner.IsOpen;
        public void Open() { lock (_driverLock) inner.Open(); }
        public HardwareTelemetry Read() { lock (_driverLock) return inner.Read(); }
        public void SetStorageEnabled(bool value) { lock (_driverLock) inner.SetStorageEnabled(value); }
        public List<DiskIoState>? ReadDisks() { lock (_driverLock) return inner.ReadDisks(); }
        public bool SetGpuFanSpeed(byte percent) { lock (_driverLock) return inner.SetGpuFanSpeed(percent); }
        public bool SetGpuFanAuto() { lock (_driverLock) { Interlocked.Increment(ref AutoCalls); return inner.SetGpuFanAuto(); } }
        public void Dispose() { lock (_driverLock) { inner.Dispose(); Disposed = true; } }
    }

    private sealed class CountingHub(SimulatedHub inner) : IFanHub
    {
        public int DisconnectCalls;
        public bool IsConnected => inner.IsConnected;
        public string FirmwareVersion => inner.FirmwareVersion;
        public IReadOnlyList<ConnectedSubDevice> SubDevices => inner.SubDevices;
        public bool Connect() => inner.Connect();
        public void Disconnect(bool restoreHardwareMode = true) { Interlocked.Increment(ref DisconnectCalls); inner.Disconnect(restoreHardwareMode); }
        public IReadOnlyDictionary<int, int>? ReadSpeeds() => inner.ReadSpeeds();
        public IReadOnlyDictionary<int, float>? ReadTemperatures() => inner.ReadTemperatures();
        public bool SetSpeedsPercent(IReadOnlyDictionary<int, byte> values) => inner.SetSpeedsPercent(values);
        public bool SetGlobalRgb(byte r, byte g, byte b, byte brightness) => inner.SetGlobalRgb(r, g, b, brightness);
        public bool SetRgbLeds(ReadOnlySpan<byte> colors, byte brightness) => inner.SetRgbLeds(colors, brightness);
        public bool RefreshSubDevices() => inner.RefreshSubDevices();
        public void Dispose() => inner.Dispose();
    }

    private sealed class SpyClient : IEngineClient
    {
        public int StartCalls, DisposeCalls, DemandCalls;
        public bool IsRemote => true;
        public bool IsConnected => false;
        public bool IsSimulated => true;
        public string ModeDescription => "proxy simulado";
        public ControlConfig Config { get; } = new();
        public EngineSnapshot? Snapshot => null;
#pragma warning disable CS0067
        public event Action<EngineSnapshot>? SnapshotUpdated;
        public event Action<ControlConfig>? ConfigChanged;
        public event Action<ControlConfig>? AnyConfigChanged;
        public event Action<EngineAlert>? AlertRaised;
        public event Action<bool>? ConnectionChanged;
#pragma warning restore CS0067
        public void Start() => Interlocked.Increment(ref StartCalls);
        public void UpdateConfig(Action<ControlConfig> change) { }
        public void ReplaceConfig(ControlConfig config) { }
        public void RequestLcdFrame() { }
        public void NotifyResume() { }
        public void RefreshRgb() { }
        public void RescanDevices() { }
        public void SetCurveTest(float? temperature) { }
        public void StartCalibration() { }
        public void CancelCalibration() { }
        public void SetSensorDemand(SensorDemand demand) => Interlocked.Increment(ref DemandCalls);
        public Task ShutdownAsync(bool restoreHardwareMode = true) { Dispose(); return Task.CompletedTask; }
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }
}
