using System.IO;
using System.IO.Pipes;
using LinkFlow.Core.Ipc;

namespace LinkFlow.Tests;

/// <summary>Unique in-process pipes only: no engine, service endpoint, SDK or hardware is constructed.</summary>
public sealed class PipeConnectionLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Idle_client_disconnect_completes_its_handler_without_canceling_the_server()
    {
        using var serverLifetime = new CancellationTokenSource();
        var (pipe, client) = await ConnectedPairAsync();
        using var ownedClient = client;
        using var connection = new PipeEngineServer.Connection(pipe);
        Task writer = connection.RunWriterAsync(serverLifetime.Token);
        Task handler = ReadUntilDisconnectAndCloseAsync(connection, writer, serverLifetime.Token);
        try
        {
            Assert.False(writer.IsCompleted);
            Assert.False(handler.IsCompleted);
            client.Dispose();
            // Before the fix the reader reached finally, but its writer waited on _signal until this
            // entire server token was canceled; completing the reliable channel did not wake it.
            await handler.WaitAsync(Deadline);
            Assert.True(writer.IsCompletedSuccessfully);
            Assert.False(serverLifetime.IsCancellationRequested);
        }
        finally
        {
            serverLifetime.Cancel();
            connection.Dispose();
            await Task.WhenAll(writer, handler).WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task Repeated_idle_connection_closures_leave_no_writers_waiting_for_service_shutdown()
    {
        using var serverLifetime = new CancellationTokenSource();
        var writers = new List<Task>();
        try
        {
            for (int i = 0; i < 64; i++)
            {
                using var connection = new PipeEngineServer.Connection(NewServer());
                Task writer = connection.RunWriterAsync(serverLifetime.Token);
                writers.Add(writer);
                Assert.False(writer.IsCompleted);
                connection.Dispose();
                await writer.WaitAsync(Deadline);
            }
            Assert.All(writers, writer => Assert.True(writer.IsCompletedSuccessfully));
            Assert.False(serverLifetime.IsCancellationRequested);
        }
        finally
        {
            serverLifetime.Cancel();
            await Task.WhenAll(writers).WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task Enqueue_dispose_and_writer_exit_can_race_without_retaining_snapshots_or_throwing()
    {
        using var serverLifetime = new CancellationTokenSource();
        string config = PipeProtocol.Encode(PipeProtocol.Config, new { revision = 1 });
        string receipt = PipeProtocol.Encode(PipeProtocol.Receipt, new { id = "receipt" });
        for (int iteration = 0; iteration < 16; iteration++)
        {
            using var connection = new PipeEngineServer.Connection(NewServer());
            Task writer = connection.RunWriterAsync(serverLifetime.Token);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] producers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await start.Task;
                for (int i = 0; i < 128; i++)
                {
                    connection.Enqueue("snapshot", isSnapshot: true);
                    connection.Enqueue(config);
                    connection.Enqueue(receipt);
                }
            })).ToArray();
            Task closer = Task.Run(async () =>
            {
                await start.Task;
                connection.Dispose();
                connection.Dispose();
            });
            start.SetResult();
            await Task.WhenAll(producers.Append(closer).Append(writer)).WaitAsync(Deadline);
            Assert.Equal(0, connection.PendingSnapshots);
            // Exercise the completed/disposed semaphore as well, rather than relying only on scheduling.
            connection.Enqueue("late snapshot", isSnapshot: true);
            connection.Enqueue(receipt);
            connection.Dispose();
            Assert.Equal(0, connection.PendingSnapshots);
        }
    }

    [Fact]
    public async Task Disposing_before_writer_start_is_idempotent_and_does_not_keep_payloads()
    {
        using var connection = new PipeEngineServer.Connection(NewServer());
        connection.Enqueue("pending", isSnapshot: true);
        Assert.Equal(1, connection.PendingSnapshots);
        connection.Dispose();
        connection.Dispose();
        connection.Enqueue("late", isSnapshot: true);
        await connection.RunWriterAsync(CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(0, connection.PendingSnapshots);
    }

    [Fact]
    public async Task Reliable_messages_preserve_order_while_config_and_snapshot_are_coalesced()
    {
        using var serverLifetime = new CancellationTokenSource(Deadline);
        var (pipe, client) = await ConnectedPairAsync();
        using var ownedClient = client;
        using var connection = new PipeEngineServer.Connection(pipe);
        string first = PipeProtocol.Encode(PipeProtocol.Receipt, new { id = "first" });
        string second = PipeProtocol.Encode(PipeProtocol.Receipt, new { id = "second" });
        string latestConfig = PipeProtocol.Encode(PipeProtocol.Config, new { revision = 2 });
        string latestSnapshot = PipeProtocol.Encode(PipeProtocol.Snapshot, new { tick = 2 });
        connection.Enqueue(first);
        connection.Enqueue(second);
        connection.Enqueue(PipeProtocol.Encode(PipeProtocol.Config, new { revision = 1 }));
        connection.Enqueue(latestConfig);
        connection.Enqueue(PipeProtocol.Encode(PipeProtocol.Snapshot, new { tick = 1 }), isSnapshot: true);
        connection.Enqueue(latestSnapshot, isSnapshot: true);
        Task writer = connection.RunWriterAsync(serverLifetime.Token);
        try
        {
            using var pending = new MemoryStream();
            var buffer = new byte[1024];
            foreach (string expected in new[] { first, second, latestConfig, latestSnapshot })
                Assert.Equal(expected, await PipeProtocol.ReadLineAsync(client, buffer, pending, serverLifetime.Token).WaitAsync(Deadline));
            Assert.Equal(0, connection.PendingSnapshots);
        }
        finally
        {
            connection.Dispose();
            await writer.WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task Full_reliable_queue_still_disconnects_instead_of_silently_dropping_receipts()
    {
        using var timeout = new CancellationTokenSource(Deadline);
        var (pipe, client) = await ConnectedPairAsync();
        using var ownedClient = client;
        using var connection = new PipeEngineServer.Connection(pipe);
        string receipt = PipeProtocol.Encode(PipeProtocol.Receipt, new { id = "bounded" });
        for (int i = 0; i < 129; i++) connection.Enqueue(receipt);
        byte[] buffer = new byte[1];
        try { Assert.Equal(0, await client.ReadAsync(buffer, timeout.Token)); }
        catch (IOException) { /* Windows can report a broken pipe instead of EOF on explicit close. */ }
        await connection.RunWriterAsync(CancellationToken.None).WaitAsync(Deadline);
        Assert.Equal(0, connection.PendingSnapshots);
    }

    private static NamedPipeServerStream NewServer() => new($"LinkFlow.ConnectionLifecycle.{Guid.NewGuid():N}",
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> ConnectedPairAsync()
    {
        string name = $"LinkFlow.ConnectionLifecycle.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(Deadline);
        try
        {
            await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
            return (server, client);
        }
        catch { server.Dispose(); client.Dispose(); throw; }
    }

    private static async Task ReadUntilDisconnectAndCloseAsync(PipeEngineServer.Connection connection, Task writer,
        CancellationToken token)
    {
        try
        {
            byte[] buffer = new byte[1];
            while (await connection.Pipe.ReadAsync(buffer, token) != 0) { }
        }
        catch (IOException) { /* Peer disconnect can be reported as a broken pipe instead of EOF. */ }
        finally
        {
            // Same owner cleanup used by ServeAsync, without constructing the engine or accessing its events.
            connection.Dispose();
            await writer;
        }
    }
}
