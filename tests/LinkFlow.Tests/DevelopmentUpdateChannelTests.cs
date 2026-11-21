using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using LinkFlow.App.Services;
using Microsoft.Win32.SafeHandles;

namespace LinkFlow.Tests;

public sealed class DevelopmentUpdateChannelTests
{
    private const string Version = "3.2.1";
    private const string DevSetup = "LinkFlow-Dev-Setup-3.2.1.exe";
    private const string Setup = "LinkFlow-Setup-3.2.1.exe";
    private const string DevZip = "LinkFlow-3.2.1-dev-portable.zip";
    private const string Zip = "LinkFlow-3.2.1-portable.zip";
    private const string Pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private static readonly byte[] Bytes = [1, 3, 5, 7];
    private static string Url(string name) => $"{AppUpdateClient.ReleasesPage}/download/v{Version}/{name}";
    private static string Digest => "sha256:" + Convert.ToHexString(SHA256.HashData(Bytes));
    private static UpdateTrustPolicy Policy(bool official) => official
        ? new(DistributionChannel.Official, "Expected Publisher", Pin) : UpdateTrustPolicy.Development;

    [Fact]
    public async Task Development_prefers_explicit_dev_installer_and_portable_over_legacy_aliases()
    {
        using var http = new HttpClient(new Handler(_ => Release(DevSetup, Setup, DevZip, Zip)));
        var result = await new AppUpdateClient(http, Policy(false)).CheckAsync(new(3, 2, 0), default);
        Assert.Equal(DevSetup, result!.Installer!.Name);
        Assert.Equal(Url(DevSetup), result.Installer.Url.AbsoluteUri);
        Assert.Equal(DevZip, result.Portable!.Name);
        Assert.Equal(Url(DevZip), result.Portable.Url.AbsoluteUri);
    }

    [Fact]
    public async Task Official_selects_only_official_asset_names_even_when_dev_assets_are_present()
    {
        using var http = new HttpClient(new Handler(_ => Release(DevSetup, Setup, DevZip, Zip)));
        var result = await new AppUpdateClient(http, Policy(true)).CheckAsync(new(3, 2, 0), default);
        Assert.Equal(Setup, result!.Installer!.Name);
        Assert.Equal(Zip, result.Portable!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dev_only_release_is_discoverable_only_by_development_policy(bool official)
    {
        using var http = new HttpClient(new Handler(_ => Release(DevSetup, DevZip)));
        var result = await new AppUpdateClient(http, Policy(official)).CheckAsync(new(3, 2, 0), default);
        Assert.NotNull(result);
        if (official)
        {
            Assert.Null(result.Installer);
            Assert.Null(result.Portable);
        }
        else
        {
            Assert.Equal(DevSetup, result.Installer!.Name);
            Assert.Equal(DevZip, result.Portable!.Name);
        }
    }

    [Fact]
    public async Task Development_keeps_legacy_official_names_as_fallback_when_dev_names_are_absent()
    {
        using var http = new HttpClient(new Handler(_ => Release(Setup, Zip)));
        var result = await new AppUpdateClient(http, Policy(false)).CheckAsync(new(3, 2, 0), default);
        Assert.Equal(Setup, result!.Installer!.Name);
        Assert.Equal(Zip, result.Portable!.Name);
    }

    [Theory]
    [InlineData("url")]
    [InlineData("digest")]
    [InlineData("state")]
    [InlineData("size")]
    public async Task Invalid_preferred_dev_asset_does_not_silently_switch_to_a_legacy_filename(string fault)
    {
        using var http = new HttpClient(new Handler(_ => ReleaseWith(
            Asset(DevSetup, url: fault == "url" ? Url(Setup) : null,
                digest: fault == "digest" ? "" : null, state: fault == "state" ? "open" : "uploaded",
                size: fault == "size" ? 0 : Bytes.Length), Asset(Setup), Asset(DevZip))));
        var result = await new AppUpdateClient(http, Policy(false)).CheckAsync(new(3, 2, 0), default);
        Assert.NotNull(result);
        Assert.Null(result.Installer);
        Assert.Equal(DevZip, result.Portable!.Name);
    }

    [Fact]
    public async Task Portable_metadata_does_not_accept_a_url_for_another_filename()
    {
        using var http = new HttpClient(new Handler(_ => ReleaseWith(Asset(DevSetup), Asset(DevZip, url: Url(Zip)), Asset(Zip))));
        var result = await new AppUpdateClient(http, Policy(false)).CheckAsync(new(3, 2, 0), default);
        Assert.Equal(DevSetup, result!.Installer!.Name);
        Assert.Null(result.Portable);
    }

    [Fact]
    public async Task Duplicate_preferred_names_are_rejected_as_ambiguous()
    {
        using var http = new HttpClient(new Handler(_ => Release(DevSetup, DevSetup, Setup)));
        var result = await new AppUpdateClient(http, Policy(false)).CheckAsync(new(3, 2, 0), default);
        Assert.NotNull(result);
        Assert.Null(result.Installer);
    }

    [Fact]
    public async Task Development_download_preserves_dev_filename_and_never_claims_verified_publisher()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal(Url(DevSetup), request.RequestUri!.AbsoluteUri);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        });
        using var http = new HttpClient(handler);
        var verifier = new Verifier();
        var client = new AppUpdateClient(http, Policy(false), verifier);
        using var cache = new Cache();
        using (var installer = await client.DownloadAsync(
                   new(DevSetup, new Uri(Url(DevSetup)), Bytes.Length, SHA256.HashData(Bytes)),
                   cache.Path, new Progress<int>(), default))
        {
            Assert.Equal(DevSetup, Path.GetFileName(installer.Path));
            Assert.Equal(Bytes, File.ReadAllBytes(installer.Path));
            Assert.False(installer.Trust.PublisherVerified);
            Assert.Equal(DistributionChannel.Development, installer.Trust.Channel);
            Assert.Throws<IOException>(() => File.OpenWrite(installer.Path));
        }
        Assert.Equal(1, handler.Calls);
        Assert.Equal(0, verifier.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
    }

    [Theory]
    [InlineData(false, DevSetup, Setup)]
    [InlineData(false, Setup, DevSetup)]
    [InlineData(true, Setup, DevSetup)]
    [InlineData(true, DevSetup, DevSetup)]
    [InlineData(false, DevZip, DevZip)]
    public async Task Download_rejects_channel_or_filename_url_substitution_before_http(bool official, string name, string urlName)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Invalid metadata must be rejected before network."));
        using var http = new HttpClient(handler);
        var verifier = new Verifier();
        using var cache = new Cache();
        var client = new AppUpdateClient(http, Policy(official), verifier);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(
            new(name, new Uri(Url(urlName)), Bytes.Length, SHA256.HashData(Bytes)), cache.Path, new Progress<int>(), default));
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, verifier.Calls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cache.Path));
    }

    [Fact]
    public void Cleanup_removes_old_dev_and_legacy_installers_but_preserves_recent_files_and_non_guid_directories()
    {
        using var cache = new Cache();
        DateTime old = DateTime.UtcNow.AddDays(-3);
        var owned = new List<string>();
        string Create(string folder, string name, bool oldFile, bool oldDirectory)
        {
            string directory = Path.Combine(cache.Path, folder);
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, name);
            File.WriteAllBytes(file, Bytes);
            File.SetLastWriteTimeUtc(file, oldFile ? old : DateTime.UtcNow);
            Directory.SetLastWriteTimeUtc(directory, oldDirectory ? old : DateTime.UtcNow);
            owned.Add(file);
            return file;
        }
        string oldDev = Create(Guid.NewGuid().ToString("N"), DevSetup, true, true);
        string oldLegacy = Create(Guid.NewGuid().ToString("N"), Setup, true, true);
        string recent = Create(Guid.NewGuid().ToString("N"), DevSetup, false, false);
        string recentFileInOldDirectory = Create(Guid.NewGuid().ToString("N"), DevSetup, false, true);
        string unrelated = Create("not-a-managed-guid", DevSetup, true, true);
        try
        {
            DownloadedInstaller.CleanOldDownloads(cache.Path);
            Assert.False(File.Exists(oldDev));
            Assert.False(File.Exists(oldLegacy));
            Assert.False(Directory.Exists(Path.GetDirectoryName(oldDev)));
            Assert.False(Directory.Exists(Path.GetDirectoryName(oldLegacy)));
            Assert.Equal(Bytes, File.ReadAllBytes(recent));
            Assert.Equal(Bytes, File.ReadAllBytes(recentFileInOldDirectory));
            Assert.Equal(Bytes, File.ReadAllBytes(unrelated));
        }
        finally
        {
            foreach (string file in owned)
            {
                if (File.Exists(file)) File.Delete(file);
                string directory = Path.GetDirectoryName(file)!;
                if (Directory.Exists(directory)) Directory.Delete(directory);
            }
        }
    }

    private static object Asset(string name, string? url = null, string? digest = null, string state = "uploaded", long? size = null) =>
        new { name, state, size = size ?? Bytes.Length, digest = digest ?? Digest, browser_download_url = url ?? Url(name) };
    private static HttpResponseMessage Release(params string[] names) => ReleaseWith(names.Select(name => Asset(name)).ToArray());
    private static HttpResponseMessage ReleaseWith(params object[] assets) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { tag_name = "v" + Version, draft = false, prerelease = false, assets })
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
    private sealed class Verifier : IUpdateSignatureVerifier
    {
        public int Calls { get; private set; }
        public TrustedSignerIdentity Verify(string path, SafeFileHandle handle)
        {
            Calls++;
            return new("Expected Publisher", Pin);
        }
    }
    private sealed class Cache : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("LinkFlow-DevelopmentUpdate-").FullName;
        public void Dispose() => Directory.Delete(Path);
    }
}
