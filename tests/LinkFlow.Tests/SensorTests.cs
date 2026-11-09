namespace LinkFlow.Tests;

/// <summary>Catálogo de sensores (rede, discos, % ou valor) e leitura de discos sob demanda.</summary>
public class SensorTests
{
    private static EngineSnapshot Snap() => new()
    {
        RamUsage = 41, RamUsedGb = 26.4f, RamTotalGb = 64,
        GpuVramUsedGb = 4, GpuVramTotalGb = 16,
        CpuCoreLoads = new[] { 10f, 85f, 30f },
        Network = new NetworkState { Adapter = "Ethernet", DownBytesPerSec = 25e6, UpBytesPerSec = 1.5e6, LinkBitsPerSec = 1_000_000_000 },
        Volumes = { new VolumeState { Letter = "C:", TotalGb = 1000, FreeGb = 250 } },
        Disks = new() { new DiskIoState { Index = 0, Name = "SSD", ActivityPercent = 12, ReadBytesPerSec = 5e6f, WriteBytesPerSec = 0, Temperature = 40 } },
        Fans = { new FanState { Key = "AAA", DefaultName = "RX Fan #1", Rpm = 1200, AppliedPercent = 55 } }
    };

    [Theory]
    [InlineData("ram.usage", SensorDisplay.Value, "26,4 GB")]
    [InlineData("ram.usage", SensorDisplay.Percent, "41%")]
    [InlineData("gpu.vram", SensorDisplay.Percent, "25%")]
    [InlineData("cpu.coremax", SensorDisplay.Percent, "85%")]
    [InlineData("net.down", SensorDisplay.Value, "25 MB/s")]
    [InlineData("net.down", SensorDisplay.Percent, "20%")]
    [InlineData("net.up", SensorDisplay.Value, "1,5 MB/s")]
    [InlineData("vol:C:", SensorDisplay.Percent, "75% usado")]
    [InlineData("vol:C:", SensorDisplay.Value, "250 GB livres")]
    [InlineData("disk:0:activity", SensorDisplay.Percent, "12%")]
    [InlineData("fan:AAA", SensorDisplay.Value, "1.200 RPM")]
    [InlineData("fan:AAA", SensorDisplay.Percent, "55%")]
    public void Catalog_formats_values_and_percentages(string id, SensorDisplay display, string expected)
    {
        var s = Snap();
        var def = SensorCatalog.Find(id, s);
        Assert.NotNull(def);
        var r = def!.Read(s, display);
        Assert.True(r.Available);
        Assert.Equal(expected, r.Text);
    }

    [Fact]
    public void Missing_data_shows_dashes_and_disk_items_need_demand()
    {
        var empty = new EngineSnapshot();
        Assert.Equal("--", SensorCatalog.Find("net.down", empty)!.Read(empty, SensorDisplay.Value).Text);
        Assert.Null(SensorCatalog.Find("disk:0:activity", empty));
        Assert.Equal(SensorDemand.DiskIo, SensorCatalog.DemandFor(new[] { "cpu.temp", "disk:0:rw" }));
        Assert.Equal(SensorDemand.None, SensorCatalog.DemandFor(new[] { "cpu.temp", "vol:C:" }));
    }

    [Fact]
    public void Engine_reads_disks_only_while_someone_asks()
    {
        using var f = new EngineFixture();
        var client = new object();
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Network != null, because: "rede sempre medida (barata)");
        Assert.Null(f.Engine.GetSnapshot().Disks);
        Assert.False(f.Hw.Sensors.StorageEnabled);

        f.Engine.SetSensorDemand(client, SensorDemand.DiskIo);
        EngineFixture.WaitUntil(() => f.Engine.GetSnapshot().Disks is { Count: 2 }, because: "discos ligados sob demanda");
        Assert.True(f.Hw.Sensors.StorageEnabled);
    }
}
