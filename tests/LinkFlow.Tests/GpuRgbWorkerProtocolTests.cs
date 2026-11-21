using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LinkFlow.Tests;

public class GpuRgbWorkerProtocolTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(16384)]
    public async Task Exact_frame_boundary_is_decoded_without_consuming_another_frame(int limit)
    {
        byte[] json = Encoding.UTF8.GetBytes("\"" + new string('a', limit - 2) + "\"");
        using var stream = RawFrame(json);
        string? value = await GpuRgbWorkerFraming.ReadAsync<string>(stream, limit, CancellationToken.None);
        Assert.Equal(limit - 2, value!.Length);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1025)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_length_is_rejected_before_reading_or_allocating_its_body(int length)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header.Concat(new byte[32]).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerRequest>(
            stream, GpuRgbWorkerProtocol.MaximumRequestBytes, CancellationToken.None));
        Assert.Equal(4, stream.Position);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Truncated_header_or_payload_is_not_a_successful_end_of_stream(int bytes)
    {
        using var full = RawFrame(Encoding.UTF8.GetBytes("{}"));
        using var stream = new MemoryStream(full.ToArray()[..bytes]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerRequest>(
            stream, GpuRgbWorkerProtocol.MaximumRequestBytes, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"version\":1,\"unknown\":true}")]
    [InlineData("{\"version\":1,}")]
    public async Task Ambiguous_unknown_and_non_strict_json_are_rejected(string json)
    {
        using var stream = RawFrame(Encoding.UTF8.GetBytes(json));
        var exception = await Record.ExceptionAsync(() => GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerRequest>(
            stream, GpuRgbWorkerProtocol.MaximumRequestBytes, CancellationToken.None));
        Assert.True(exception is JsonException or InvalidDataException);
    }

    [Fact]
    public async Task Fake_host_roundtrip_keeps_probe_separate_from_color_and_disposes_on_eof()
    {
        var device = new FakeGpu();
        var result = await Host(device, Request(), Request(GpuRgbWorkerProtocol.Color, 2) with
        { R = 11, G = 22, B = 33, Brightness = 75 });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, device.Initializations);
        Assert.Equal((11, 22, 33, 75), device.Color);
        Assert.Equal(1, device.Disposals);
        using var replies = new MemoryStream(result.Bytes);
        var probe = await ReadReply(replies);
        Assert.True(probe!.Accepted);
        Assert.Null(probe.Status.LastOutputAccepted);
        var color = await ReadReply(replies);
        Assert.Equal(2, color!.RequestId);
        Assert.True(color.Status.LastOutputAccepted);
        Assert.Null(await ReadReply(replies));
    }

    [Fact]
    public async Task Native_false_remains_false_and_is_not_replaced_with_probe_success()
    {
        var device = new FakeGpu { RejectColor = true };
        var result = await Host(device, Request(), Request(GpuRgbWorkerProtocol.Color, 2));
        using var replies = new MemoryStream(result.Bytes);
        Assert.True((await ReadReply(replies))!.Accepted);
        var color = await ReadReply(replies);
        Assert.False(color!.Accepted);
        Assert.False(color.Status.LastOutputAccepted);
        Assert.Equal(-6, color.Status.LastNativeFailure!.Status);
    }

    [Fact]
    public async Task Native_exception_ends_worker_without_fabricating_a_boolean_sdk_rejection()
    {
        var device = new FakeGpu { ThrowColor = true };
        var result = await Host(device, Request(), Request(GpuRgbWorkerProtocol.Color, 2));
        Assert.Equal(5, result.ExitCode);
        Assert.Equal(1, device.Disposals);
        using var replies = new MemoryStream(result.Bytes);
        Assert.NotNull(await ReadReply(replies));
        Assert.Null(await ReadReply(replies));
    }

    [Theory]
    [InlineData("color", 1, 1, 1)]
    [InlineData("initialize", 0, 1, 1)]
    [InlineData("initialize", 1, 0, 1)]
    [InlineData("initialize", 1, 1, 2)]
    [InlineData("setConfig", 1, 1, 1)]
    public async Task Invalid_first_request_never_constructs_hardware(string operation, long generation, long id, int version)
    {
        int factories = 0;
        using var input = new MemoryStream();
        await GpuRgbWorkerFraming.WriteAsync(input, Request(operation, id, generation) with { Version = version },
            GpuRgbWorkerProtocol.MaximumRequestBytes, CancellationToken.None);
        input.Position = 0;
        using var output = new MemoryStream();
        int code = await GpuRgbWorkerHost.RunTrustedAsync(input, output, () => { factories++; return new FakeGpu(); }, CancellationToken.None);
        Assert.Equal(5, code);
        Assert.Equal(0, factories);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task Duplicate_ids_or_another_generation_never_reach_color_output(long nextId, long nextGeneration)
    {
        var device = new FakeGpu();
        var result = await Host(device, Request(), Request(GpuRgbWorkerProtocol.Color, nextId, nextGeneration));
        Assert.Equal(5, result.ExitCode);
        Assert.Equal(0, device.ColorCalls);
        Assert.Equal(1, device.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_terminates_owned_transport_even_when_read_or_write_ignores_cancellation(bool blockedWrite)
    {
        using var memory = new MemoryStream();
        using var blocked = new IgnoringCancellationStream();
        Stream input = blockedWrite ? blocked : memory;
        Stream output = blockedWrite ? memory : blocked;
        int terminations = 0;
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => GpuRgbWorkerSession.ExchangeWithinDeadlineAsync(
            input, output, Request(), TimeSpan.FromMilliseconds(40), CancellationToken.None,
            () => { Interlocked.Increment(ref terminations); blocked.End(); }));
        Assert.Equal(1, terminations);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), "An ignored IO cancellation exceeded the external deadline budget.");
    }

    [Fact]
    public async Task A_reply_from_another_epoch_aborts_without_replaying_the_request()
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await GpuRgbWorkerFraming.WriteAsync(output, new GpuRgbWorkerReply { Generation = 99, RequestId = 1 },
            GpuRgbWorkerProtocol.MaximumReplyBytes, CancellationToken.None);
        output.Position = 0;
        int terminations = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => GpuRgbWorkerSession.ExchangeWithinDeadlineAsync(
            input, output, Request(), TimeSpan.FromSeconds(1), CancellationToken.None, () => terminations++));
        Assert.Equal(1, terminations);
        input.Position = 0;
        Assert.NotNull(await GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerRequest>(input, 1024, CancellationToken.None));
        Assert.Null(await GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerRequest>(input, 1024, CancellationToken.None));
    }

    [Theory]
    [InlineData(1, 2, 0, 0, false, true)]
    [InlineData(1, 1, 1, 0, false, true)]
    [InlineData(1, 1, 0, 1, false, true)]
    [InlineData(1, 1, 0, 0, true, true)]
    [InlineData(1, 1, 0, 0, false, false)]
    public void Parent_policy_rejects_manual_simulated_foreign_path_and_nonservice_contexts(
        int parent, int scm, int childSession, int parentSession, bool forbiddenArguments, bool sameImage)
    {
        var now = DateTime.UtcNow;
        Assert.False(GpuRgbWorkerIdentity.IsAllowedParent(parent, scm, childSession, parentSession,
            @"C:\Program Files\LinkFlow\LinkFlow.Service.exe",
            sameImage ? @"C:\Program Files\LinkFlow\LinkFlow.Service.exe" : @"C:\Other\LinkFlow.Service.exe",
            now.AddSeconds(-1), now, forbiddenArguments));
    }

    [Fact]
    public void Parent_policy_requires_start_before_child_and_accepts_only_the_matching_service()
    {
        var now = DateTime.UtcNow;
        const string path = @"C:\Program Files\LinkFlow\LinkFlow.Service.exe";
        Assert.False(GpuRgbWorkerIdentity.IsAllowedParent(1, 1, 0, 0, path, path, now, now, false));
        Assert.True(GpuRgbWorkerIdentity.IsAllowedParent(1, 1, 0, 0, path, path, now.AddSeconds(-1), now, false));
    }

    private static GpuRgbWorkerRequest Request(string operation = GpuRgbWorkerProtocol.Initialize, long id = 1, long generation = 1) =>
        new() { Generation = generation, RequestId = id, Operation = operation, Brightness = 100 };

    private static MemoryStream RawFrame(byte[] bytes)
    {
        var stream = new MemoryStream();
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        stream.Write(header);
        stream.Write(bytes);
        stream.Position = 0;
        return stream;
    }

    private static Task<GpuRgbWorkerReply?> ReadReply(Stream stream) =>
        GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerReply>(stream, GpuRgbWorkerProtocol.MaximumReplyBytes, CancellationToken.None);

    private static async Task<(int ExitCode, byte[] Bytes)> Host(FakeGpu device, params GpuRgbWorkerRequest[] requests)
    {
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        foreach (var request in requests)
            await GpuRgbWorkerFraming.WriteAsync(input, request, GpuRgbWorkerProtocol.MaximumRequestBytes, CancellationToken.None);
        input.Position = 0;
        int code = await GpuRgbWorkerHost.RunTrustedAsync(input, output, () => device, CancellationToken.None);
        return (code, output.ToArray());
    }

    private sealed class FakeGpu : IRgbDevice, IRgbControlStatusProvider
    {
        public int Initializations, ColorCalls, Disposals;
        public bool RejectColor, ThrowColor;
        public (int R, int G, int B, int Brightness) Color;
        public string Name => "Isolated fake";
        public bool IsAvailable => Initializations > 0;
        public RgbControlStatus ControlStatus { get; private set; } = new();
        public void Initialize()
        {
            Initializations++;
            ControlStatus = new() { ControllerName = Name, ControlAvailable = true };
        }
        public bool SetColor(byte r, byte g, byte b, byte brightnessPercent)
        {
            ColorCalls++;
            if (ThrowColor) throw new IOException("Isolated fake SDK exception.");
            Color = (r, g, b, brightnessPercent);
            ControlStatus = ControlStatus with
            {
                LastOutputAccepted = !RejectColor,
                LastNativeFailure = RejectColor ? new RgbNativeOperationResult { Sequence = 1, Status = -6 } : null
            };
            return !RejectColor;
        }
        public void Dispose() => Disposals++;
    }

    private sealed class IgnoringCancellationStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void End() => _read.TrySetResult(0);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(_read.Task);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => new(_read.Task);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { End(); base.Dispose(disposing); }
    }
}
