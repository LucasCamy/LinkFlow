using System.IO;

namespace LinkFlow.Tests;

public sealed class GpuRgbNativeWireTests
{
    [Fact]
    public async Task Real_gpu_provider_status_with_native_error_and_localized_arguments_crosses_the_private_wire()
    {
        var backend = new FakeAsusGpuRgbBackend();
        backend.FailNext(0x8020, 0x00, -6);
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await Request(input, 1, GpuRgbWorkerProtocol.Initialize);
        await Request(input, 2, GpuRgbWorkerProtocol.Color);
        input.Position = 0;
        Assert.Equal(0, await GpuRgbWorkerHost.RunTrustedAsync(input, output, () => new AsusGpuRgb(backend), CancellationToken.None));
        output.Position = 0;
        var probe = await Reply(output);
        Assert.True(probe!.Accepted);
        var rejected = await Reply(output);
        Assert.False(rejected!.Accepted);
        Assert.Equal(EngineTexts.GpuAuraFailed, rejected.Status.DiagnosticText!.Code);
        Assert.Equal(["8020", "00", "-6"], rejected.Status.DiagnosticText.Args.Select(a => a.S));
        Assert.Equal(-6, rejected.Status.LastNativeFailure!.Status);
        Assert.Equal((ushort)0x8020, rejected.Status.LastNativeFailure.Register);
        Assert.Contains("NVAPI -6", rejected.Status.Diagnostic);
        Assert.False(rejected.Status.ControlAvailable);
        Assert.True(rejected.Status.InitializationRetryNeeded);
    }

    [Fact]
    public async Task Unsupported_firmware_diagnostic_crosses_the_wire_without_becoming_a_retry_or_native_failure()
    {
        var backend = new FakeAsusGpuRgbBackend("XUMA0-E6K5-1110");
        using var input = new MemoryStream();
        using var output = new MemoryStream();
        await Request(input, 1, GpuRgbWorkerProtocol.Initialize);
        input.Position = 0;
        Assert.Equal(0, await GpuRgbWorkerHost.RunTrustedAsync(input, output, () => new AsusGpuRgb(backend), CancellationToken.None));
        output.Position = 0;
        var probe = await Reply(output);
        Assert.False(probe!.Accepted);
        Assert.Equal(EngineTexts.GpuFirmwareUnknown, probe.Status.DiagnosticText!.Code);
        Assert.Equal("XUMA0-E6K5-1110", Assert.Single(probe.Status.DiagnosticText.Args).S);
        Assert.False(probe.Status.InitializationRetryNeeded);
        Assert.Equal(0, probe.Status.TotalNativeFailures);
        Assert.Null(probe.Status.LastNativeFailure);
    }

    private static Task Request(Stream stream, long id, string operation) => GpuRgbWorkerFraming.WriteAsync(stream,
        new GpuRgbWorkerRequest { Generation = 1, RequestId = id, Operation = operation, R = 10, G = 20, B = 30, Brightness = 100 },
        GpuRgbWorkerProtocol.MaximumRequestBytes, CancellationToken.None);
    private static Task<GpuRgbWorkerReply?> Reply(Stream stream) => GpuRgbWorkerFraming.ReadAsync<GpuRgbWorkerReply>(stream,
        GpuRgbWorkerProtocol.MaximumReplyBytes, CancellationToken.None);
}
