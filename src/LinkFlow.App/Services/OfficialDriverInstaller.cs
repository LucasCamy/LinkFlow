using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using LinkFlow.Core;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.App.Services;

internal readonly record struct DriverDownloadProgress(long Bytes, long? TotalBytes, bool Verifying = false);

internal static class OfficialDriverInstaller
{
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(45);
    private static readonly HttpClient Client = CreateClient();
    private static readonly Regex AmdDownloadLink = new(
        "href\\s*=\\s*[\"'](https://drivers\\.amd\\.com/drivers/installer/[^\\s\"'<>]*minimalsetup[^\\s\"'<>]*\\.exe)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static Task<string> GetMetadataAsync(Uri uri, CancellationToken cancellationToken = default) =>
        Client.GetStringAsync(uri, cancellationToken);

    public static bool IsOfficialDownload(DriverPublisher publisher, Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            !url.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;

        return publisher switch
        {
            DriverPublisher.Nvidia => url.Host == "download.nvidia.com" || url.Host.EndsWith(".download.nvidia.com", StringComparison.Ordinal),
            DriverPublisher.Amd => url.Host == "drivers.amd.com" &&
                (url.AbsolutePath.StartsWith("/drivers/installer/", StringComparison.OrdinalIgnoreCase) ||
                 url.AbsolutePath.StartsWith("/drivers/amd_chipset_software_", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    public static async Task DownloadAndOpenAsync(ManufacturerDriverPackage package, IProgress<DriverDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        Uri url = package.DownloadUrl ?? (package.Publisher == DriverPublisher.Amd
            ? await ReadAmdAutoDetectAsync(cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException("O fabricante não forneceu o link do instalador."));
        string directory = Directory.CreateTempSubdirectory("LinkFlow-driver-").FullName;
        string path = Path.Combine(directory, package.Publisher == DriverPublisher.Nvidia ? "NVIDIA-driver.exe" : "AMD-driver.exe");
        try
        {
            await DownloadAsync(package.Publisher, url, path, progress, cancellationToken).ConfigureAwait(false);
            MarkInternetOrigin(path, url);

            // Mantém o instalador bloqueado para escrita e troca até o Windows abrir o executável verificado.
            Process process;
            using (var readLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                progress.Report(new DriverDownloadProgress(readLock.Length, readLock.Length, Verifying: true));
                await Task.Run(() => VerifySignature(path, package.Publisher, readLock.SafeFileHandle), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = directory })
                    ?? throw new InvalidOperationException("O Windows não conseguiu abrir o instalador do fabricante.");
            }
            _ = CleanAfterExitAsync(process, directory, path);
        }
        catch
        {
            CleanDownload(directory, path);
            throw;
        }
    }

    private static async Task<Uri> ReadAmdAutoDetectAsync(CancellationToken cancellationToken)
    {
        string html = await GetMetadataAsync(ManufacturerDrivers.AmdSupport, cancellationToken).ConfigureAwait(false);
        var links = AmdDownloadLink.Matches(html).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).Distinct().ToArray();
        if (links.Length == 1 && Uri.TryCreate(links[0], UriKind.Absolute, out var url) && IsOfficialDownload(DriverPublisher.Amd, url))
            return url;
        throw new InvalidOperationException("O link do AMD Auto-Detect mudou. Abra o site oficial para baixar a ferramenta.");
    }

    private static async Task DownloadAsync(DriverPublisher publisher, Uri url, string path, IProgress<DriverDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var response = await OpenDownloadAsync(publisher, url, cancellationToken).ConfigureAwait(false);
        long? total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        var reportTime = Stopwatch.StartNew();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(DownloadIdleTimeout);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) != 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                downloaded += count;
                if (reportTime.ElapsedMilliseconds < 250) continue;
                idle.CancelAfter(DownloadIdleTimeout);
                progress.Report(new DriverDownloadProgress(downloaded, total));
                reportTime.Restart();
            }
        }
        catch (OperationCanceledException ex) when (idle.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("O fabricante parou de enviar dados. Tente o download novamente.", ex);
        }

        if (downloaded == 0 || total is { } length && downloaded != length)
            throw new IOException("O download do instalador ficou incompleto.");
    }

    private static async Task<HttpResponseMessage> OpenDownloadAsync(DriverPublisher publisher, Uri url, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 4; redirects++)
        {
            if (!IsOfficialDownload(publisher, url)) throw new InvalidOperationException("O link do instalador saiu do domínio do fabricante.");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (publisher == DriverPublisher.Amd) request.Headers.Referrer = ManufacturerDrivers.AmdSupport;
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
                    response.EnsureSuccessStatusCode();
                else
                    url = new Uri(url, location);
            }
        }
        throw new InvalidOperationException("O fabricante retornou redirecionamentos demais para o instalador.");
    }

    private static void VerifySignature(string path, DriverPublisher publisher, SafeFileHandle file)
    {
        string subject = WindowsAuthenticode.TrustedPublisher(path, file).Trim();
        bool trustedPublisher = publisher switch
        {
            DriverPublisher.Nvidia => subject.Equals("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase),
            DriverPublisher.Amd => subject.Equals("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
                                   subject.Equals("Advanced Micro Devices, Inc.", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (trustedPublisher) return;

        Log.Warn($"Assinatura do instalador de {publisher}: editor '{subject}'.");
        throw new InvalidOperationException("O Windows não confirmou a assinatura do fabricante. Abra a página oficial para conferir o pacote.");
    }

    private static void MarkInternetOrigin(string path, Uri url)
    {
        try { File.WriteAllText($"{path}:Zone.Identifier", $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={url.AbsoluteUri}\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Debug($"Origem do instalador: {ex.Message}");
        }
    }

    private static async Task CleanAfterExitAsync(Process process, string directory, string path)
    {
        using (process)
        {
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Debug($"Aguardando instalador: {ex.Message}");
                return;
            }
        }
        CleanDownload(directory, path);
    }

    private static void CleanDownload(string directory, string path)
    {
        try
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Removendo download temporário: {ex.Message}");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) LinkFlow/2.0");
        return client;
    }
}
