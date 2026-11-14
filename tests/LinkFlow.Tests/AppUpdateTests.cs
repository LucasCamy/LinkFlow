using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LinkFlow.App.Services;

namespace LinkFlow.Tests;

public sealed class AppUpdateTests
{
    private const string InstallerName = "LinkFlow-Setup-2.10.0.exe";
    private const string InstallerUrl = $"{AppUpdateClient.ReleasesPage}/download/v2.10.0/{InstallerName}";
    private static readonly byte[] InstallerBytes = [1, 2, 3, 4];
    private static readonly byte[] InstallerHash = SHA256.HashData(InstallerBytes);
    private static readonly string Digest = $"sha256:{Convert.ToHexString(InstallerHash)}";

    [Fact]
    public async Task OffersOnlyANewerVersion()
    {
        using var http = CreateHttpClient(_ => ReleaseResponse());
        var client = new AppUpdateClient(http);

        var release = await client.CheckAsync(new Version(2, 9, 9, 0), default);

        Assert.NotNull(release);
        Assert.Equal(new Version(2, 10, 0), release.Version);
        Assert.Equal(InstallerUrl, release.Installer!.Url.AbsoluteUri);
        Assert.Null(await client.CheckAsync(new Version(2, 10, 0, 0), default));
        Assert.Null(await client.CheckAsync(new Version(3, 0, 0), default));
    }

    [Theory]
    [InlineData("v2.10.0-rc1", false, false)]
    [InlineData("v2.10.0", true, false)]
    [InlineData("v2.10.0", false, true)]
    public async Task RejectsUnstableReleases(string tag, bool draft, bool prerelease)
    {
        using var http = CreateHttpClient(_ => ReleaseResponse(tag: tag, draft: draft, prerelease: prerelease));
        var client = new AppUpdateClient(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.CheckAsync(new Version(2, 3, 0), default));
    }

    [Theory]
    [InlineData("https://github.com/LucasCamy/Other/releases/download/v2.10.0/LinkFlow-Setup-2.10.0.exe", true, 4, "uploaded")]
    [InlineData(InstallerUrl, false, 4, "uploaded")]
    [InlineData(InstallerUrl, true, 268435457, "uploaded")]
    [InlineData(InstallerUrl, true, 4, "open")]
    public async Task OffersBrowserWhenInstallerMetadataCannotBeVerified(string url, bool hasDigest, long size, string state)
    {
        using var http = CreateHttpClient(_ => ReleaseResponse(url: url, digest: hasDigest ? Digest : "", size: size, state: state));
        var release = await new AppUpdateClient(http).CheckAsync(new Version(2, 3, 0), default);

        Assert.NotNull(release);
        Assert.Null(release.Installer);
        Assert.Equal($"{AppUpdateClient.ReleasesPage}/tag/v2.10.0", release.Page);
    }

    [Fact]
    public async Task FollowsGitHubRedirectAndLocksVerifiedInstaller()
    {
        var assetUrl = new Uri("https://release-assets.githubusercontent.com/file");
        using var http = CreateHttpClient(uri => uri == assetUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InstallerBytes) }
            : Redirect(assetUrl));
        var client = new AppUpdateClient(http);
        using var cache = new TestCache();

        string path;
        using (var installer = await client.DownloadAsync(CreateAsset(), cache.Path, new Progress<int>(), default))
        {
            path = installer.Path;
            Assert.Equal(InstallerBytes, File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => File.OpenWrite(path));
        }

        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
    }

    [Theory]
    [InlineData("http://release-assets.githubusercontent.com/file")]
    [InlineData("https://release-assets.githubusercontent.com.example.com/file")]
    [InlineData("https://objects.githubusercontent.com:444/file")]
    public async Task RejectsRedirectsOutsideGitHubAssetHosts(string destination)
    {
        using var http = CreateHttpClient(uri =>
        {
            Assert.Equal(InstallerUrl, uri.AbsoluteUri);
            return Redirect(new Uri(destination));
        });
        var client = new AppUpdateClient(http);
        using var cache = new TestCache();

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(CreateAsset(), cache.Path, new Progress<int>(), default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemovesIncompleteOrCorruptedDownloads(bool incomplete)
    {
        var content = new ByteArrayContent(incomplete ? [1, 2, 3] : [1, 2, 3, 5]);
        content.Headers.ContentLength = InstallerBytes.Length;
        using var http = CreateHttpClient(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        var client = new AppUpdateClient(http);
        using var cache = new TestCache();

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(CreateAsset(), cache.Path, new Progress<int>(), default));
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
    }

    private static UpdateAsset CreateAsset() => new(InstallerName, new Uri(InstallerUrl), InstallerBytes.Length, InstallerHash);

    private static HttpResponseMessage ReleaseResponse(string tag = "v2.10.0", bool draft = false, bool prerelease = false,
        string url = InstallerUrl, string? digest = null, long size = 4, string state = "uploaded") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            tag_name = tag, draft, prerelease,
            assets = new[] { new { name = InstallerName, state, size, digest = digest ?? Digest, browser_download_url = url } }
        })
    };

    private static HttpResponseMessage Redirect(Uri location) => new(HttpStatusCode.Redirect) { Headers = { Location = location } };

    private static HttpClient CreateHttpClient(Func<Uri, HttpResponseMessage> respond) => new(new TestHandler(respond));

    private sealed class TestHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request.RequestUri!));
    }

    private sealed class TestCache : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("LinkFlowUpdateTest-").FullName;
        public void Dispose() => Directory.Delete(Path);
    }
}
