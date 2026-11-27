using System.Net;
using System.Text;
using Savepoint.Core.Updates;

namespace Savepoint.Core.Tests;

public class UpdateCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-upd-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static readonly byte[] Exe = Encoding.UTF8.GetBytes("pretend this is Savepoint.exe");

    private static string Release(string tag, string? digest = null, bool prerelease = false, long? size = null,
        string asset = "Savepoint.exe") => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/OHAiTHARU/savepoint/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "What's new",
          "assets": [
            { "name": "Savepoint.Playnite.pext", "size": 10, "browser_download_url": "https://example.invalid/x.pext" },
            { "name": "{{asset}}", "size": {{size ?? Exe.Length}},
              "digest": {{(digest is null ? "null" : $"\"{digest}\"")}},
              "browser_download_url": "https://example.invalid/Savepoint.exe" }
          ]
        }
        """;

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.2", "0.2.0")]
    [InlineData("v1.10.3-beta.1", "1.10.3")]
    [InlineData("V2", "2.0.0")]
    public void ParsesTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsNonVersionTags(string? tag) => Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public async Task NewerReleaseIsOffered()
    {
        var sha = Hashing.Sha256Bytes(Exe);
        using var checker = new UpdateChecker(new Handler(_ => Json(Release("v0.3.0", "sha256:" + sha))));
        var u = await checker.CheckAsync(new Version(0, 2, 0, 0)); // assembly versions have 4 parts
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 3, 0), u!.Version);
        Assert.Equal("https://example.invalid/Savepoint.exe", u.DownloadUrl);
        Assert.Equal(sha, u.Sha256);
        Assert.Equal("What's new", u.Notes);
    }

    [Theory]
    [InlineData("v0.2.0")] // same
    [InlineData("v0.1.9")] // older
    public async Task SameOrOlderIsNotOffered(string tag)
    {
        using var checker = new UpdateChecker(new Handler(_ => Json(Release(tag))));
        Assert.Null(await checker.CheckAsync(new Version(0, 2, 0)));
    }

    [Fact]
    public async Task PrereleasesAndReleasesWithoutAnExeAreIgnored()
    {
        using (var c = new UpdateChecker(new Handler(_ => Json(Release("v9.0.0", prerelease: true)))))
            Assert.Null(await c.CheckAsync(new Version(0, 2, 0)));
        using (var c = new UpdateChecker(new Handler(_ => Json(Release("v9.0.0", asset: "Other.zip")))))
            Assert.Null(await c.CheckAsync(new Version(0, 2, 0)));
    }

    [Fact]
    public async Task NoReleasesIsNotAnError()
    {
        using var checker = new UpdateChecker(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await checker.CheckAsync(new Version(0, 2, 0)));
    }

    [Fact]
    public async Task RateLimitIsReportedAsUpdateException()
    {
        using var checker = new UpdateChecker(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        await Assert.ThrowsAsync<UpdateException>(() => checker.CheckAsync(new Version(0, 2, 0)));
    }

    [Fact]
    public async Task DownloadVerifiesChecksum()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith(".exe")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Exe) }
            : Json(Release("v0.3.0", "sha256:" + Hashing.Sha256Bytes(Exe))));
        using var checker = new UpdateChecker(handler);
        var u = (await checker.CheckAsync(new Version(0, 2, 0)))!;
        var path = await checker.DownloadAsync(u, _dir);
        Assert.Equal(Exe, File.ReadAllBytes(path));
        Assert.EndsWith("Savepoint-0.3.0.exe", path);
    }

    [Fact]
    public async Task DownloadWithWrongChecksumIsRejectedAndRemoved()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith(".exe")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Exe) }
            : Json(Release("v0.3.0", "sha256:" + new string('0', 64))));
        using var checker = new UpdateChecker(handler);
        var u = (await checker.CheckAsync(new Version(0, 2, 0)))!;
        var ex = await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(u, _dir));
        Assert.Contains("checksum", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task TruncatedDownloadIsRejected()
    {
        var handler = new Handler(r => r.RequestUri!.AbsolutePath.EndsWith(".exe")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Exe[..5]) }
            : Json(Release("v0.3.0", null)));
        using var checker = new UpdateChecker(handler);
        var u = (await checker.CheckAsync(new Version(0, 2, 0)))!;
        await Assert.ThrowsAsync<UpdateException>(() => checker.DownloadAsync(u, _dir));
    }
}

/// <summary>Against the real GitHub release. Opt-in: set SAVEPOINT_E2E_GITHUB=1.</summary>
public class UpdateCheckerLiveTests
{
    [SkippableFact]
    public async Task FindsAndVerifiesTheRealLatestRelease()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("SAVEPOINT_E2E_GITHUB") == "1", "SAVEPOINT_E2E_GITHUB not set");
        using var checker = new UpdateChecker();
        var u = await checker.CheckAsync(new Version(0, 0, 1));
        Assert.NotNull(u);
        Assert.NotNull(u!.Sha256);
        var dir = Path.Combine(Path.GetTempPath(), "sp-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = await checker.DownloadAsync(u, dir);
            Assert.Equal(u.Size, new FileInfo(path).Length);
        }
        finally { Directory.Delete(dir, true); }
    }
}
