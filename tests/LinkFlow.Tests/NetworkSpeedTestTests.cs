using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class NetworkSpeedTestTests
{
    [Fact]
    public async Task EachDirectionSharesOneTrafficBudgetAcrossFourTransfers()
    {
        const int limit = 35 * NetworkSpeedTest.BytesPerMiB;
        using var handler = new TestServer();
        using var client = new HttpClient(handler);
        var progress = new RecordingProgress();

        var result = await NetworkSpeedTest.RunAsync(client, TimeSpan.FromSeconds(4), limit, SuccessfulPing, progress, default);

        Assert.Equal(4, handler.MaxDownloads);
        Assert.Equal(4, handler.MaxUploads);
        Assert.Equal(limit, handler.DownloadRequested);
        Assert.Equal(limit, handler.UploadRequested);
        Assert.True(result.Download.Value > 0);
        Assert.True(result.Upload.Value > 0);
        Assert.False(result.Download.Incomplete);
        Assert.False(result.Upload.Incomplete);
        Assert.Equal(new[] { NetworkTestStage.Latency, NetworkTestStage.Download, NetworkTestStage.Upload },
            progress.Samples.Select(sample => sample.Stage).Distinct().ToArray());
        Assert.All(progress.Samples, sample =>
        {
            Assert.InRange(sample.Bytes, 0L, limit);
            Assert.InRange(sample.Fraction, 0, 1);
        });
    }

    [Fact]
    public async Task KeepsDownloadedBytesAtDeadlineAndExcludesUnconfirmedUploads()
    {
        const int limit = 64 * NetworkSpeedTest.BytesPerMiB;
        using var handler = new TestServer
        {
            StallDownload = true,
            UploadStatus = async (_, cancellation) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return HttpStatusCode.OK;
            }
        };
        using var client = new HttpClient(handler);
        var progress = new RecordingProgress();

        var result = await NetworkSpeedTest.RunAsync(client, TimeSpan.FromSeconds(1), limit, SuccessfulPing, progress, default);

        Assert.True(result.Download.Value > 0);
        Assert.False(result.Download.Incomplete);
        Assert.True(handler.UploadRequested > 0);
        Assert.Null(result.Upload.Value);
        Assert.True(result.Upload.Incomplete);
        Assert.Contains(progress.Samples, sample => sample.Stage == NetworkTestStage.Download && sample.Mbps > 0);
        Assert.All(progress.Samples.Where(sample => sample.Stage == NetworkTestStage.Upload), sample =>
        {
            Assert.Null(sample.Mbps);
            Assert.Equal(0, sample.Bytes);
        });
    }

    [Fact]
    public async Task ContinuesWithoutPingAndKeepsDownloadWhenUploadFails()
    {
        using var handler = new TestServer { UploadStatus = (_, _) => Task.FromResult(HttpStatusCode.ServiceUnavailable) };
        using var client = new HttpClient(handler);
        var progress = new RecordingProgress();
        var missedPing = new NetworkMeasurement(null, "Sem resposta ao ping.", true);

        var result = await NetworkSpeedTest.RunAsync(client, TimeSpan.FromSeconds(3), 2 * NetworkSpeedTest.BytesPerMiB,
            _ => Task.FromResult(missedPing), progress, default);

        Assert.Equal(missedPing, result.Latency);
        Assert.Contains(progress.Samples, sample => sample.Stage == NetworkTestStage.Latency && sample.Measurement == missedPing);
        Assert.True(result.Download.Value > 0);
        Assert.Null(result.Upload.Value);
        Assert.True(result.Upload.Incomplete);
        Assert.Contains("HTTP 503", result.Upload.Detail);
    }

    [Fact]
    public async Task CancellationStopsActiveTransfersBeforeStartingUpload()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new TestServer { StallDownload = true };
        using var client = new HttpClient(handler);

        var measurement = NetworkSpeedTest.RunAsync(client, TimeSpan.FromSeconds(4), 64 * NetworkSpeedTest.BytesPerMiB,
            SuccessfulPing, null, cancellation.Token);
        await handler.DownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measurement.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(0, handler.UploadRequested);
    }

    [Fact]
    public async Task PublishesFinalMeasurementsBeforeStartingTheNextStage()
    {
        const int limit = 2 * NetworkSpeedTest.BytesPerMiB;
        using var handler = new TestServer();
        using var client = new HttpClient(handler);
        var progress = new RecordingProgress(sample =>
        {
            if (sample.Measurement is null) return;
            if (sample.Stage == NetworkTestStage.Latency) Assert.Equal(0, handler.DownloadRequested);
            if (sample.Stage == NetworkTestStage.Download) Assert.Equal(0, handler.UploadRequested);
        });

        var result = await NetworkSpeedTest.RunAsync(client, TimeSpan.FromSeconds(4), limit, SuccessfulPing, progress, default);

        var transitions = progress.Samples
            .Where(sample => sample.Seconds == 0)
            .Select(sample => (sample.Stage, sample.Measurement))
            .ToArray();
        Assert.Equal(new (NetworkTestStage, NetworkMeasurement?)[]
        {
            (NetworkTestStage.Latency, null), (NetworkTestStage.Latency, result.Latency),
            (NetworkTestStage.Download, null), (NetworkTestStage.Download, result.Download),
            (NetworkTestStage.Upload, null), (NetworkTestStage.Upload, result.Upload)
        }, transitions);
    }

    private static Task<NetworkMeasurement> SuccessfulPing(CancellationToken _) =>
        Task.FromResult(new NetworkMeasurement(16, "3 de 3 respostas"));

    private sealed class RecordingProgress(Action<NetworkTestProgress>? onReport = null) : IProgress<NetworkTestProgress>
    {
        public List<NetworkTestProgress> Samples { get; } = [];

        public void Report(NetworkTestProgress value)
        {
            Samples.Add(value);
            onReport?.Invoke(value);
        }
    }

    private sealed class TestServer : HttpMessageHandler
    {
        public bool StallDownload { get; init; }
        public Func<int, CancellationToken, Task<HttpStatusCode>>? UploadStatus { get; init; }
        public TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly object _gate = new();
        private int _activeDownloads, _activeUploads;
        public int MaxDownloads { get; private set; }
        public int MaxUploads { get; private set; }
        public int DownloadRequested { get; private set; }
        public int UploadRequested { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Assert.Equal("speed.cloudflare.com", request.RequestUri!.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            Assert.Equal(HttpVersion.Version11, request.Version);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/__up", request.RequestUri.AbsolutePath);
                int bytes = (int)request.Content!.Headers.ContentLength!.Value;
                lock (_gate)
                {
                    MaxUploads = Math.Max(MaxUploads, ++_activeUploads);
                    UploadRequested += bytes;
                }
                try
                {
                    await request.Content.CopyToAsync(Stream.Null, cancellation);
                    await Task.Delay(20, cancellation);
                    var status = UploadStatus is null ? HttpStatusCode.OK : await UploadStatus(bytes, cancellation);
                    var response = new HttpResponseMessage(status);
                    response.Headers.Add("cf-meta-upload-bytes", bytes.ToString(CultureInfo.InvariantCulture));
                    return response;
                }
                finally
                {
                    lock (_gate) _activeUploads--;
                }
            }

            Assert.Equal("/__down", request.RequestUri.AbsolutePath);
            int size = int.Parse(request.RequestUri.Query.AsSpan("?bytes=".Length), CultureInfo.InvariantCulture);
            Assert.True(size > 0);
            lock (_gate)
            {
                MaxDownloads = Math.Max(MaxDownloads, ++_activeDownloads);
                DownloadRequested += size;
            }
            try
            {
                DownloadStarted.TrySetResult();
                await Task.Delay(20, cancellation);
                return new(HttpStatusCode.OK) { Content = new StreamContent(new DownloadStream(size, StallDownload)) };
            }
            finally
            {
                lock (_gate) _activeDownloads--;
            }
        }
    }

    private sealed class DownloadStream(int bytes, bool stall) : MemoryStream(new byte[bytes])
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (stall && Position > 0) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
