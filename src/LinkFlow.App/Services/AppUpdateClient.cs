using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LinkFlow.Core;
using Microsoft.Win32;

namespace LinkFlow.App.Services;

internal sealed record AppRelease(Version Version, UpdateAsset? Installer)
{
    public string Page => $"{AppUpdateClient.ReleasesPage}/tag/v{Version.ToString(3)}";
}

internal sealed record UpdateAsset(string Name, Uri Url, long Size, byte[] Hash);

internal sealed class AppUpdateClient(HttpClient http)
{
    internal const string ReleasesPage = "https://github.com/LucasCamy/LinkFlow/releases";
    private const string ReleasesApi = "https://api.github.com/repos/LucasCamy/LinkFlow/releases/latest";
    private const long MaxInstallerSize = 256 * 1024 * 1024;

    public static AppUpdateClient Official { get; } = new(CreateHttpClient());

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LinkFlow-Updater/1.0");
        return client;
    }

    public async Task<AppRelease?> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token).ConfigureAwait(false);
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(timeout.Token).ConfigureAwait(false);

        if (release is not { Draft: false, Prerelease: false, Tag: { } tag } ||
            tag.Length < 2 || tag[0] != 'v' || !Version.TryParse(tag[1..], out var version) ||
            version.Build < 0 || version.Revision >= 0 || tag != $"v{version.ToString(3)}")
            throw new InvalidDataException("O GitHub não retornou uma versão estável do LinkFlow.");

        var current = new Version(currentVersion.Major, currentVersion.Minor, Math.Max(0, currentVersion.Build));
        if (version <= current) return null;

        var name = $"LinkFlow-Setup-{version.ToString(3)}.exe";
        var url = $"{ReleasesPage}/download/{tag}/{name}";
        var asset = release.Assets?.FirstOrDefault(item => item?.Name == name);
        if (asset is not { State: "uploaded", Size: > 0 and <= MaxInstallerSize, Digest: { Length: 71 } digest } ||
            asset.Url != url || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            digest.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") >= 0)
            return new AppRelease(version, null);

        return new AppRelease(version, new UpdateAsset(name, new Uri(url), asset.Size, Convert.FromHexString(digest[7..])));
    }

    public async Task<DownloadedInstaller> DownloadAsync(UpdateAsset asset, string cache, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        using var response = await OpenDownloadAsync(asset.Url, timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is long length && length != asset.Size)
            throw new InvalidDataException("O tamanho do instalador não corresponde ao publicado no GitHub.");

        var directory = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, asset.Name);
        Directory.CreateDirectory(directory);
        try
        {
            if (File.GetAttributes(cache).HasFlag(FileAttributes.ReparsePoint) ||
                File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("A pasta do download foi redirecionada.");

            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long received = 0;
                int percent = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > asset.Size) throw new InvalidDataException("O download ultrapassou o tamanho publicado.");
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    int nextPercent = (int)(99 * received / asset.Size);
                    if (nextPercent == percent) continue;
                    percent = nextPercent;
                    progress.Report(percent);
                }
                if (received != asset.Size) throw new InvalidDataException("O download do instalador ficou incompleto.");
            }

            // Mantém o arquivo verificado aberto sem permitir que seja alterado ou removido até o Setup iniciar.
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            try
            {
                if (file.Length != asset.Size ||
                    !(await SHA256.HashDataAsync(file, timeout.Token).ConfigureAwait(false)).AsSpan().SequenceEqual(asset.Hash))
                    throw new InvalidDataException("O SHA-256 do instalador não corresponde ao publicado no GitHub.");
                return new DownloadedInstaller(path, file);
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }
        catch
        {
            DownloadedInstaller.TryRemove(path);
            throw;
        }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri url, CancellationToken cancellationToken)
    {
        for (int count = 0; count < 5; count++)
        {
            var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return response;

            using (response)
            {
                var location = response.Headers.Location;
                if ((int)response.StatusCode is < 300 or >= 400) response.EnsureSuccessStatusCode();

                if (location == null || !Uri.TryCreate(url, location, out var next) || next.Scheme != Uri.UriSchemeHttps ||
                    !next.IsDefaultPort || next.UserInfo.Length != 0 || next.Fragment.Length != 0 ||
                    next.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new InvalidDataException("O GitHub retornou um endereço de download inválido.");
                url = next;
            }
        }
        throw new InvalidDataException("O GitHub retornou muitos redirecionamentos para o instalador.");
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string? Tag, bool? Draft, bool? Prerelease, GitHubAsset?[]? Assets);

    private sealed record GitHubAsset(string? Name, string? State, long Size,
        [property: JsonPropertyName("browser_download_url")] string? Url, string? Digest);
}

internal sealed class DownloadedInstaller(string path, FileStream file) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        file.Dispose();
        TryRemove(Path);
    }

    internal static void TryRemove(string path)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path)!;
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint) ||
                File.GetAttributes(System.IO.Path.GetDirectoryName(directory)!).HasFlag(FileAttributes.ReparsePoint)) return;
            File.Delete(path);
            Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // O instalador em execução pode manter o arquivo aberto até o fim da atualização.
        }
    }

    internal static void CleanOldDownloads(string cache)
    {
        try
        {
            if (!Directory.Exists(cache) || File.GetAttributes(cache).HasFlag(FileAttributes.ReparsePoint)) return;
            var cutoff = DateTime.UtcNow.AddDays(-2);
            foreach (var directory in new DirectoryInfo(cache).EnumerateDirectories())
            {
                if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.LastWriteTimeUtc >= cutoff ||
                    directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var candidate in directory.EnumerateFiles("LinkFlow-Setup-*.exe"))
                {
                    if (candidate.LastWriteTimeUtc < cutoff && !candidate.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        TryRemove(candidate.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"Cache de atualização indisponível: {ex.Message}");
        }
    }
}

internal static class LinkFlowInstallation
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{6F1A3C2E-8B4D-4E7A-9C51-2D7F0B9E4A13}_is1";

    internal static string? FindRegisteredDirectory(string executable)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var entry = machine.OpenSubKey(UninstallKey);
            if (entry?.GetValue("InstallLocation") is not string location || !Path.IsPathFullyQualified(location))
                return null;

            var expected = Path.Combine(Path.GetFullPath(location), "LinkFlow.App.exe");
            return string.Equals(Path.GetFullPath(executable), expected, StringComparison.OrdinalIgnoreCase) && File.Exists(expected)
                ? Path.GetDirectoryName(expected) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            Log.Warn($"Não foi possível identificar a instalação do LinkFlow: {ex.Message}");
            return null;
        }
    }
}
