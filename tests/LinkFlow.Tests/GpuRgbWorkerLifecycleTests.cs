using System.Reflection;
using System.IO;

namespace LinkFlow.Tests;

public sealed class GpuRgbWorkerLifecycleTests
{
    [Theory]
    [InlineData("gpu")]
    [InlineData("motherboard")]
    [InlineData("ram")]
    [InlineData("peripherals")]
    [InlineData("lcd")]
    public async Task Failed_device_termination_retains_exclusive_ownership_after_stop(string failingDevice)
    {
        // Every adapter is fake. Only this GUID-scoped Local test mutex is opened, never the service mutex.
        string name = @"Local\LinkFlow.Ownership.Test." + Guid.NewGuid().ToString("N");
        var factory = new FailingCleanupFactory(failingDevice);
        var engine = new LinkFlowEngine(factory, new EngineOptions
        {
            ConfigPath = null, HardwareLeaseNameForTests = name,
            History = new OperationalHistoryOptions { DirectoryPath = null },
            InventoryProvider = () => new InventoryObservation(),
            TelemetryInterval = TimeSpan.FromMilliseconds(30), SnapshotInterval = TimeSpan.FromMilliseconds(30)
        });
        var ownership = typeof(LinkFlowEngine).GetField("_hardwareOwnership", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(ownership);
        try
        {
            engine.Start();
            EngineFixture.WaitUntil(() => factory.Inner.Hub.ConnectCalls > 0 && factory.Inner.Sensors.OpenCalls > 0 &&
                factory.Rgb.Values.All(rgb => rgb.Initializations > 0), because: "todos os adaptadores simulados foram inicializados sob a posse de teste");
            Assert.Throws<InvalidOperationException>(() => HardwareControlLease.AcquireForTests(name));

            await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(9));

            Assert.NotNull(ownership.GetValue(engine));
            Assert.Equal(1, failingDevice == "lcd" ? factory.Lcd.DisposeCalls : factory.Rgb[failingDevice].DisposeCalls);
            Assert.Throws<InvalidOperationException>(() => HardwareControlLease.AcquireForTests(name));
            Assert.Throws<ObjectDisposedException>(engine.Start);
        }
        finally
        {
            // Production intentionally retains uncertain ownership until process exit. This isolated fake test
            // has no live native controller; release its exact lease to avoid leaving the dedicated test thread.
            try { await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(9)); }
            finally
            {
                if (ownership.GetValue(engine) is HardwareControlLease retained)
                {
                    retained.Dispose();
                    ownership.SetValue(engine, null);
                }
                engine.Dispose();
            }
        }
        using var afterTestCleanup = HardwareControlLease.AcquireForTests(name);
    }

    private sealed class FailingCleanupFactory : IHardwareFactory
    {
        internal SimulatedHardwareFactory Inner { get; } = new();
        internal Dictionary<string, FakeRgb> Rgb { get; }
        internal FakeLcd Lcd { get; }
        internal FailingCleanupFactory(string failingDevice)
        {
            Inner.Sensors.OpenDelay = TimeSpan.Zero;
            Rgb = new[] { "gpu", "motherboard", "ram", "peripherals" }
                .ToDictionary(id => id, id => new FakeRgb(id, id == failingDevice));
            Lcd = new FakeLcd(failingDevice == "lcd");
        }
        public bool IsSimulated => false; // Exercises only the explicitly isolated ownership hook.
        public IFanHub CreateHub() => Inner.Hub;
        public ISensorSource CreateSensors() => Inner.Sensors;
        public ILcdDisplay CreateLcd() => Lcd;
        public IRgbDevice CreateGpuRgb() => Rgb["gpu"];
        public IRgbDevice CreateMotherboardRgb() => Rgb["motherboard"];
        public IRgbDevice CreateRamRgb() => Rgb["ram"];
        public IRgbDevice CreatePeripheralsRgb() => Rgb["peripherals"];
    }

    private sealed class FakeRgb(string name, bool failDisposal) : IRgbDevice
    {
        private int _initializations, _disposeCalls;
        internal int Initializations => Volatile.Read(ref _initializations);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public string Name => name;
        public bool IsAvailable => Initializations > 0;
        public void Initialize() => Interlocked.Increment(ref _initializations);
        public bool SetColor(byte r, byte g, byte b, byte brightnessPercent) => true;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (failDisposal) throw new IOException("Fake controller termination was not confirmed.");
        }
    }

    private sealed class FakeLcd(bool failDisposal) : ILcdDisplay
    {
        private int _disposeCalls;
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public bool IsConnected => false;
        public bool Connect() => false;
        public void Disconnect() { }
        public bool SendJpegFrame(byte[] jpegBytes) => false;
        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCalls);
            if (failDisposal) throw new IOException("Fake display termination was not confirmed.");
        }
    }
}
