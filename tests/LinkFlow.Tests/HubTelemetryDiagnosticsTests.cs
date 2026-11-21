using System.IO;
using System.IO.Pipes;
using System.Text;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class HubTelemetryDiagnosticsTests
{
    private static byte[] Packet(byte[] type, int count = 52)
    {
        var packet = new byte[LinkHubProtocol.PacketSizeOut];
        type.CopyTo(packet, LinkHubProtocol.DataTypeOffset);
        packet[7] = (byte)count;
        for (int i = 8; i + 2 < packet.Length; i += 3) packet[i] = 1;
        packet[47] = 0; // Device channel 13.
        packet[48] = 0xD7; packet[49] = 0x01;
        packet[^1] = 0xAB; // Retain the complete report, including the previously unobserved tail.
        return packet;
    }

    [Fact]
    public void Cached_packets_preserve_exact_bytes_and_old_snapshots_after_buffer_reuse_and_clock_changes()
    {
        var clock = new Clock();
        var cache = new HubTelemetryDiagnosticsCache(clock);
        var raw = Packet(LinkHubProtocol.DataTypeTemperatures);
        string expected = Convert.ToHexString(raw);
        Assert.True(cache.Capture(raw, LinkHubProtocol.DataTypeTemperatures, "4.1.656"));
        var before = cache.Snapshot().Temperature!;
        Array.Fill(raw, (byte)0xFF);
        clock.Advance(TimeSpan.FromSeconds(2));
        clock.Utc = clock.Utc.AddYears(-20);
        var after = cache.Snapshot().Temperature!;

        Assert.Equal(expected, before.Hex);
        Assert.Equal(expected, after.Hex);
        Assert.Equal(1026, after.Hex.Length);
        Assert.EndsWith("AB", after.Hex);
        Assert.Equal(513, after.ReceivedBytes);
        Assert.Equal(52, after.DeclaredRecordCount);
        Assert.Equal("1000", after.DataType);
        Assert.Equal("4.1.656", after.Firmware);
        Assert.Equal(before.AcquiredAtUtc, after.AcquiredAtUtc);
        Assert.Equal(before.AcquiredAtTimestamp, after.AcquiredAtTimestamp);
        Assert.Equal(1000, after.TimestampFrequency);
        Assert.Equal(0, before.AgeSeconds);
        Assert.Equal(2, after.AgeSeconds);
        Assert.Null(after.ParseRejection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(514)]
    public void Invalid_report_sizes_do_not_replace_a_known_cached_report(int length)
    {
        var cache = new HubTelemetryDiagnosticsCache();
        Assert.True(cache.Capture(Packet(LinkHubProtocol.DataTypeSpeeds, 26), LinkHubProtocol.DataTypeSpeeds, "4.1.656"));
        string expected = cache.Snapshot().Rpm!.Hex;
        var invalid = new byte[length];
        if (length >= 7) LinkHubProtocol.DataTypeSpeeds.CopyTo(invalid, LinkHubProtocol.DataTypeOffset);
        Assert.False(cache.Capture(invalid, LinkHubProtocol.DataTypeSpeeds, "4.1.656"));
        Assert.Equal(expected, cache.Snapshot().Rpm!.Hex);
        Assert.Null(cache.Snapshot().Temperature);
    }

    [Fact]
    public void Parser_rejected_native_response_is_retained_at_its_actual_length_for_diagnosis()
    {
        var cache = new HubTelemetryDiagnosticsCache();
        var raw = Packet(LinkHubProtocol.DataTypeTemperatures).AsSpan(0, 96).ToArray();
        Assert.True(cache.Capture(raw, LinkHubProtocol.DataTypeTemperatures, "4.1.656"));
        var diagnostic = cache.Snapshot().Temperature!;
        Assert.Equal(96, diagnostic.ReceivedBytes);
        Assert.Equal(192, diagnostic.Hex.Length);
        Assert.Equal(52, diagnostic.DeclaredRecordCount);
        Assert.Equal("truncated-sensor-records", diagnostic.ParseRejection);
        Assert.Null(LinkHubProtocol.ParseTemperatures(Convert.FromHexString(diagnostic.Hex)));
    }

    [Fact]
    public void Only_latest_rpm_and_temperature_are_retained_and_other_packet_types_cannot_create_slots()
    {
        var clock = new Clock();
        var cache = new HubTelemetryDiagnosticsCache(clock);
        Assert.True(cache.Capture(Packet(LinkHubProtocol.DataTypeTemperatures), LinkHubProtocol.DataTypeTemperatures, "4.1.656"));
        var original = cache.Snapshot().Temperature!;
        var speed = Packet(LinkHubProtocol.DataTypeSpeeds, 26);
        for (int i = 0; i < 1000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            speed[12] = (byte)i;
            Assert.True(cache.Capture(speed, LinkHubProtocol.DataTypeSpeeds, "4.1.656"));
        }
        Assert.False(cache.Capture(Packet(LinkHubProtocol.DataTypeSubDevices), LinkHubProtocol.DataTypeSubDevices, "4.1.656"));
        Assert.False(cache.Capture(speed, LinkHubProtocol.DataTypeTemperatures, "4.1.656"));
        var latest = cache.Snapshot();
        Assert.Equal(Convert.ToHexString(speed), latest.Rpm!.Hex);
        Assert.Equal(0, latest.Rpm.AgeSeconds);
        Assert.Equal(original.Hex, latest.Temperature!.Hex);
        Assert.Equal(1, latest.Temperature.AgeSeconds);
    }

    [Fact]
    public void Native_diagnostic_getter_before_connection_is_explicitly_empty_and_does_not_request_hardware()
    {
        using var hub = new CorsairLinkHub(); // Construction and this getter do not enumerate/open HID.
        var diagnostic = hub.GetTelemetryDiagnostics();
        Assert.True(diagnostic.Supported);
        Assert.Equal("no-packets-received", diagnostic.Reason);
        Assert.Null(diagnostic.Rpm);
        Assert.Null(diagnostic.Temperature);
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public void Simulation_and_unsupported_providers_never_fabricate_native_packets()
    {
        using var simulation = new LinkFlowEngine(new SimulatedHardwareFactory(), new() { ConfigPath = null });
        Assert.Equal("simulation-has-no-native-packets", simulation.GetHubTelemetryDiagnostics().Reason);
        Assert.False(simulation.GetHubTelemetryDiagnostics().Supported);
        using var unsupported = new LinkFlowEngine(new Factory(new SimulatedHub()), new() { ConfigPath = null });
        Assert.False(unsupported.GetHubTelemetryDiagnostics().Supported);
        Assert.Equal("provider-does-not-support-raw-telemetry", unsupported.GetHubTelemetryDiagnostics().Reason);
        Assert.Null(unsupported.GetHubTelemetryDiagnostics().Temperature);
    }

    [Fact]
    public async Task Read_only_diagnostic_rpc_returns_cached_full_report_without_acquisition_control_or_config_changes()
    {
        var hub = new DiagnosticHub();
        hub.Cache.Capture(Packet(LinkHubProtocol.DataTypeTemperatures), LinkHubProtocol.DataTypeTemperatures, "4.1.656");
        using var engine = new LinkFlowEngine(new Factory(hub), new() { ConfigPath = null });
        long revision = engine.GetConfig().Revision;
        string name = "LinkFlow.HubDiagnostics." + Guid.NewGuid().ToString("N");
        await using var server = new PipeEngineServer(engine, name);
        server.Start(); // The engine is deliberately not started: no hardware lanes or ownership acquisition.
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(deadline.Token);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
        var request = new PipeProtocol.ReadRequestInfo { Kind = "hubTelemetry" };
        await writer.WriteLineAsync(PipeProtocol.Encode(PipeProtocol.ReadRequest, request));
        HubTelemetryDiagnostics? result = null;
        while (result == null)
        {
            string? line = await reader.ReadLineAsync(deadline.Token);
            Assert.NotNull(line);
            if (!PipeProtocol.TryDecode(line, out var type, out var node) || type != PipeProtocol.ReadResponse) continue;
            Assert.True(PipeProtocol.TryRead<PipeProtocol.ReadResponseInfo>(node, out var response));
            Assert.NotNull(response);
            Assert.Equal(request.Id, response.Id);
            Assert.Null(response.Error);
            Assert.True(PipeProtocol.TryRead<HubTelemetryDiagnostics>(response.Data, out result));
        }
        Assert.True(result.Supported);
        Assert.Equal(513, result.Temperature!.ReceivedBytes);
        Assert.Equal(52, result.Temperature.DeclaredRecordCount);
        Assert.EndsWith("AB", result.Temperature.Hex);
        Assert.Equal(0, hub.NativeCalls);
        Assert.Equal(revision, engine.GetConfig().Revision);
        Assert.Empty(engine.GetCommandReceipts());
    }

    private sealed class Clock : TimeProvider
    {
        private long _timestamp;
        public DateTimeOffset Utc = DateTimeOffset.Parse("2026-11-20T20:00:00Z", global::System.Globalization.CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => _timestamp;
        public override long TimestampFrequency => 1000;
        public void Advance(TimeSpan elapsed) { _timestamp += (long)elapsed.TotalMilliseconds; Utc += elapsed; }
    }

    private sealed class Factory(IFanHub hub) : IHardwareFactory
    {
        private readonly SimulatedHardwareFactory _inner = new();
        public bool IsSimulated => false; // Fake provider, never started and never eligible for hardware ownership.
        public IFanHub CreateHub() => hub;
        public ISensorSource CreateSensors() => _inner.Sensors;
        public ILcdDisplay CreateLcd() => _inner.Lcd;
        public IRgbDevice CreateGpuRgb() => _inner.GpuRgb;
        public IRgbDevice CreateMotherboardRgb() => _inner.MotherboardRgb;
        public IRgbDevice CreateRamRgb() => _inner.RamRgb;
        public IRgbDevice CreatePeripheralsRgb() => _inner.PeripheralsRgb;
    }

    private sealed class DiagnosticHub : IFanHub, IHubTelemetryDiagnostics
    {
        public readonly HubTelemetryDiagnosticsCache Cache = new();
        public int NativeCalls;
        private Exception Unexpected() { NativeCalls++; return new InvalidOperationException("Diagnostic getter requested native I/O."); }
        public HubTelemetryDiagnostics GetTelemetryDiagnostics() => Cache.Snapshot();
        public bool IsConnected => throw Unexpected();
        public string FirmwareVersion => throw Unexpected();
        public IReadOnlyList<ConnectedSubDevice> SubDevices => throw Unexpected();
        public bool Connect() => throw Unexpected();
        public void Disconnect(bool restoreHardwareMode = true) => throw Unexpected();
        public IReadOnlyDictionary<int, int>? ReadSpeeds() => throw Unexpected();
        public IReadOnlyDictionary<int, float>? ReadTemperatures() => throw Unexpected();
        public bool SetSpeedsPercent(IReadOnlyDictionary<int, byte> channelSpeeds) => throw Unexpected();
        public bool SetGlobalRgb(byte r, byte g, byte b, byte brightnessPercent) => throw Unexpected();
        public bool SetRgbLeds(ReadOnlySpan<byte> colors, byte brightnessPercent) => throw Unexpected();
        public bool RefreshSubDevices() => throw Unexpected();
        public void Dispose() { }
    }
}
