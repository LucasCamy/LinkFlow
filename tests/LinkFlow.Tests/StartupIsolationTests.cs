using System.IO;
using System.Text;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

public sealed class StartupIsolationTests
{
    [Fact]
    public void Simulated_service_gets_unique_temporary_data_and_pipe_without_modifying_the_process_environment()
    {
        string? beforeData = Environment.GetEnvironmentVariable("LINKFLOW_DATA_DIR");
        string? beforePipe = Environment.GetEnvironmentVariable("LINKFLOW_PIPE");
        var first = ServiceStartupIsolation.Resolve(["--simulate"], null, null);
        var second = ServiceStartupIsolation.Resolve(["--SIMULATE"], null, null);
        Assert.True(first.Simulated);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "LinkFlow", "simulation"), first.DataDirectory!);
        Assert.NotEqual(first.DataDirectory, second.DataDirectory);
        Assert.StartsWith("LinkFlow.Simulation.", first.PipeName!);
        Assert.NotEqual(first.PipeName, second.PipeName);
        Assert.Equal(beforeData, Environment.GetEnvironmentVariable("LINKFLOW_DATA_DIR"));
        Assert.Equal(beforePipe, Environment.GetEnvironmentVariable("LINKFLOW_PIPE"));
        Assert.False(Directory.Exists(first.DataDirectory));
    }

    [Fact]
    public void Explicit_nonproduction_isolation_is_honored_and_normal_mode_is_unchanged()
    {
        string data = Path.Combine(Path.GetTempPath(), "LinkFlow-explicit-test-" + Guid.NewGuid().ToString("N"));
        var simulated = ServiceStartupIsolation.Resolve(["--simulate"], data, "LinkFlow.Test.Custom");
        Assert.Equal(Path.GetFullPath(data), simulated.DataDirectory);
        Assert.Equal("LinkFlow.Test.Custom", simulated.PipeName);
        var normal = ServiceStartupIsolation.Resolve([], data, "LinkFlow.Engine.v1");
        Assert.False(normal.Simulated);
        Assert.Equal(data, normal.DataDirectory);
        Assert.Equal("LinkFlow.Engine.v1", normal.PipeName);
    }

    [Theory]
    [InlineData("LinkFlow.Engine.v1")]
    [InlineData("LINKFLOW.ENGINE.V1")]
    public void Simulation_rejects_the_production_pipe_even_with_isolated_data(string pipe)
    {
        Assert.Throws<InvalidOperationException>(() => ServiceStartupIsolation.Resolve(["--simulate"], null, pipe));
    }

    [Fact]
    public void Simulation_rejects_production_data_and_ancestor_or_descendant_overrides()
    {
        string production = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LinkFlow");
        foreach (string candidate in new[] { production, Path.Combine(production, "simulation"), Path.GetDirectoryName(production)!, Path.GetPathRoot(production)! })
            Assert.Throws<InvalidOperationException>(() => ServiceStartupIsolation.Resolve(["--simulate"], candidate, "LinkFlow.Isolated", productionDirectory: production));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Pipe_line_limit_applies_before_decoding_even_when_newline_is_already_buffered(int difference)
    {
        int payloadLength = PipeProtocol.MaxMessageBytes + difference;
        byte[] payload = Encoding.UTF8.GetBytes(new string('a', payloadLength) + "\n");
        using var stream = new MemoryStream();
        using var pending = new MemoryStream();
        pending.Write(payload);
        if (difference > 0)
            await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadLineAsync(stream, new byte[8192], pending, CancellationToken.None));
        else
        {
            var line = await PipeProtocol.ReadLineAsync(stream, new byte[8192], pending, CancellationToken.None);
            Assert.Equal(payloadLength, line!.Length);
        }
    }

    [Fact]
    public async Task Pipe_exact_limit_with_newline_arriving_in_the_same_read_is_accepted()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('a', PipeProtocol.MaxMessageBytes) + "\n");
        using var stream = new MemoryStream(payload);
        using var pending = new MemoryStream();
        var line = await PipeProtocol.ReadLineAsync(stream, new byte[payload.Length], pending, CancellationToken.None);
        Assert.Equal(PipeProtocol.MaxMessageBytes, line!.Length);
    }
}
