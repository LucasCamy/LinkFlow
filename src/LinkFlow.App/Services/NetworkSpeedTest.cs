using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using LinkFlow.Core;

namespace LinkFlow.App.Services;

internal enum NetworkTestStage { Latency, Download, Upload }

internal sealed record NetworkTestProgress(NetworkTestStage Stage, double Seconds = 0, long Bytes = 0,
    double? Mbps = null, double Fraction = 0, NetworkMeasurement? Measurement = null);
internal sealed record NetworkMeasurement(double? Value, string Detail, bool Incomplete = false);
internal sealed record NetworkTestResult(NetworkMeasurement Latency, NetworkMeasurement Download, NetworkMeasurement Upload);

// Método adaptado de TesteVelocidade.cs do SuporteTI-Desktop (veja THIRD-PARTY-NOTICES.txt).
internal static class NetworkSpeedTest
{
    public const int BytesPerMiB = 1024 * 1024;
    public const int Connections = 4;
    public const int SecondsPerDirection = 10;
    public const int LimitMiBPerDirection = 512;
    public const string PingTarget = "8.8.8.8";
    private const int PingSamples = 3;
    private const int MaxBlock = 8 * BytesPerMiB;
    private const int MinUploadBlock = 64 * 1024;
    private const string Server = "https://speed.cloudflare.com/";

    public static async Task<NetworkTestResult> RunAsync(IProgress<NetworkTestProgress> progress, CancellationToken cancellation)
    {
        using var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            MaxConnectionsPerServer = Connections,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            MaxResponseDrainSize = 0
        }) { Timeout = Timeout.InfiniteTimeSpan };

        return await RunAsync(client, TimeSpan.FromSeconds(SecondsPerDirection), LimitMiBPerDirection * BytesPerMiB,
            MeasurePingAsync, progress, cancellation).ConfigureAwait(false);
    }

    internal static async Task<NetworkTestResult> RunAsync(HttpClient client, TimeSpan window, int limit,
        Func<CancellationToken, Task<NetworkMeasurement>> measurePing, IProgress<NetworkTestProgress>? progress,
        CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new(NetworkTestStage.Latency));
        var latency = await measurePing(cancellation).ConfigureAwait(false);
        progress?.Report(new(NetworkTestStage.Latency, Measurement: latency));

        var download = await MeasureRateAsync(client, NetworkTestStage.Download, window, limit, progress, cancellation).ConfigureAwait(false);
        progress?.Report(new(NetworkTestStage.Download, Fraction: 0.5, Measurement: download));

        var upload = await MeasureRateAsync(client, NetworkTestStage.Upload, window, limit, progress, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        progress?.Report(new(NetworkTestStage.Upload, Fraction: 1, Measurement: upload));
        return new(latency, download, upload);
    }

    private static async Task<NetworkMeasurement> MeasurePingAsync(CancellationToken cancellation)
    {
        using var ping = new Ping();
        var address = IPAddress.Parse(PingTarget);
        var samples = new List<long>(PingSamples);
        for (int i = 0; i < PingSamples; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromSeconds(1), cancellationToken: cancellation).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) samples.Add(reply.RoundtripTime);
            }
            catch (PingException ex)
            {
                Log.Debug($"Ping para {PingTarget}: {ex.Message}");
            }
        }

        cancellation.ThrowIfCancellationRequested();
        if (samples.Count == 0) return new(null, $"{PingTarget} · Sem resposta ao ping.", true);

        samples.Sort();
        double median = (samples[(samples.Count - 1) / 2] + samples[samples.Count / 2]) / 2.0;
        return new(median, $"{PingTarget} · {samples.Count} de {PingSamples} respostas", samples.Count < PingSamples);
    }

    private static async Task<NetworkMeasurement> MeasureRateAsync(HttpClient client, NetworkTestStage stage,
        TimeSpan window, int limit, IProgress<NetworkTestProgress>? progress, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        bool upload = stage == NetworkTestStage.Upload;
        double phaseStart = upload ? 0.5 : 0;
        progress?.Report(new(stage, Fraction: phaseStart));

        byte[] payload = upload ? new byte[Math.Min(MaxBlock, limit)] : [];
        if (upload) Random.Shared.NextBytes(payload);

        int remainingBudget = limit;
        long validBytes = 0;
        string? failure = null;
        var budgetLock = new object();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var timer = Stopwatch.StartNew();
        deadline.CancelAfter(window);

        async Task TransferAsync()
        {
            byte[] buffer = upload ? [] : new byte[64 * 1024];
            int block = upload ? MinUploadBlock : MaxBlock;
            while (!deadline.IsCancellationRequested)
            {
                int size;
                lock (budgetLock)
                {
                    // Reserva antes do envio para limitar o tráfego de todas as conexões juntas.
                    size = Math.Min(block, remainingBudget);
                    remainingBudget -= size;
                }
                if (size == 0) return;

                try
                {
                    using var request = Request(upload, size);
                    if (upload)
                    {
                        request.Content = new ByteArrayContent(payload, 0, size);
                        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    }

                    long start = Stopwatch.GetTimestamp();
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (upload)
                    {
                        if (response.Headers.TryGetValues("cf-meta-upload-bytes", out var counts) &&
                            (!int.TryParse(counts.FirstOrDefault(), out int confirmed) || confirmed != size))
                            throw new InvalidDataException("O servidor confirmou um volume de upload diferente do enviado.");

                        Interlocked.Add(ref validBytes, size);
                        double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                        if (elapsed < 0.4) block = Math.Min(MaxBlock, block * 2);
                        else if (elapsed > 1.5) block = Math.Max(MinUploadBlock, block / 2);
                    }
                    else
                    {
                        if (response.Content.Headers.ContentLength is { } length && length != size)
                            throw new InvalidDataException("O servidor retornou um tamanho de download diferente do solicitado.");

                        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                        for (int remaining = size; remaining > 0;)
                        {
                            int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), deadline.Token).ConfigureAwait(false);
                            if (read == 0) throw new InvalidDataException("O servidor encerrou o bloco de download antes do fim.");
                            remaining -= read;
                            Interlocked.Add(ref validBytes, read);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
                {
                    if (!deadline.IsCancellationRequested) Interlocked.CompareExchange(ref failure, Failure(ex), null);
                    return;
                }
            }
        }

        var transfers = Task.WhenAll(Enumerable.Range(0, Connections).Select(_ => TransferAsync()));
        while (progress is not null && !transfers.IsCompleted && !deadline.IsCancellationRequested)
        {
            var interval = Task.Delay(150, deadline.Token);
            if (await Task.WhenAny(transfers, interval).ConfigureAwait(false) != interval || deadline.IsCancellationRequested)
                break;

            double elapsed = timer.Elapsed.TotalSeconds;
            long bytes = Interlocked.Read(ref validBytes);
            double fraction = Math.Clamp(Math.Max(elapsed / window.TotalSeconds, bytes / (double)limit), 0, 1);
            progress.Report(new(stage, elapsed, bytes, bytes > 0 ? ToMbps(bytes, elapsed) : null, phaseStart + fraction / 2));
        }

        await transfers.ConfigureAwait(false);
        timer.Stop();
        cancellation.ThrowIfCancellationRequested();
        if (validBytes == 0)
            return new(null, failure ?? (upload ? "Nenhum bloco de upload foi confirmado a tempo." : "Nenhum dado de download recebido a tempo."), true);

        double seconds = timer.Elapsed.TotalSeconds;
        string detail = $"{validBytes / (double)BytesPerMiB:N1} MiB · {seconds:N1} s";
        if (failure is not null) detail += $" · {failure}";
        if (validBytes == limit)
            detail += seconds < 1 ? " · Limite de dados atingido em menos de 1 s." : " · Limite de dados atingido.";
        return new(ToMbps(validBytes, seconds), detail, failure is not null);
    }

    private static double ToMbps(long bytes, double seconds) => bytes * 8.0 / Math.Max(seconds, 0.001) / 1_000_000;

    private static HttpRequestMessage Request(bool upload, int bytes)
    {
        var request = new HttpRequestMessage(upload ? HttpMethod.Post : HttpMethod.Get, upload ? Server + "__up" :
            FormattableString.Invariant($"{Server}__down?bytes={bytes}"))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    private static string Failure(Exception ex)
    {
        Log.Debug($"Teste de rede: {ex.Message}");
        return ex switch
        {
            HttpRequestException { StatusCode: { } status } => $"Servidor respondeu HTTP {(int)status}.",
            OperationCanceledException => "Tempo limite atingido.",
            InvalidDataException => ex.Message,
            _ => "A comunicação com o servidor falhou."
        };
    }
}
