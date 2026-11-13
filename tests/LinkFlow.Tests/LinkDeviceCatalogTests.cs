namespace LinkFlow.Tests;

public sealed class LinkDeviceCatalogTests
{
    [Theory]
    [InlineData(0x03, 0x00, LinkDeviceModel.FanRxMaxRgbSeries, "RX MAX RGB Fan #1", false)]
    [InlineData(0x07, 0x02, LinkDeviceModel.LiquidCoolerHSeries, "H150i Pump", true)]
    [InlineData(0x07, 0xFF, LinkDeviceModel.LiquidCoolerHSeries, "H-Series Pump", true)]
    [InlineData(0xFE, 0x00, LinkDeviceModel.Unknown, "Device #1 (0xFE)", false)]
    public void Parses_model_and_variant_without_changing_device_identity(int model, int variant,
        LinkDeviceModel expectedModel, string name, bool isPump)
    {
        var packet = new byte[17];
        packet[7] = 1; // Um canal no payload.
        var entry = packet.AsSpan(8);
        entry[2] = (byte)model;
        entry[3] = (byte)variant;
        entry[7] = 1;
        entry[8] = (byte)'A';

        var device = Assert.Single(LinkHubProtocol.ParseSubDevices(packet));
        Assert.Equal(expectedModel, device.Model);
        Assert.Equal(name, device.Name);
        Assert.Equal(isPump, device.IsPump);
        Assert.Equal("A", FanState.KeyFor(device));
    }
}
