using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Savepoint.Core.Updates;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string DownloadUrl,
    long Size,
    /// <summary>Lowercase hex SHA-256 published by GitHub for the asset, if available.</summary>
    string? Sha256,
    string ReleaseUrl,
    string? Notes);

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Checks GitHub Releases for a newer Savepoint.exe and downloads it, verifying the
/// size and the SHA-256 digest GitHub publishes for each release asset.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    public const string DefaultLatestUrl = "https://api.github.com/repos/OHAiTHARU/savepoint/releases/latest";
    public const string AssetName = "Savepoint.exe";

    private readonly HttpClient _http;
    private readonly string _latestUrl;

    public UpdateChecker(HttpMessageHandler? handler = null, string latestUrl = DefaultLatestUrl)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        // GitHub's API requires a User-Agent; keep it generic.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Savepoint-Agent");
        _latestUrl = latestUrl;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>"v1.2.3" / "1.2" → Version(1,2,3) / (1,2,0). Null if not a version.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash]; // pre-release/build suffixes aren't compared
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var v)) return null;
        return Normalize(v);
    }

    /// <summary>Major.Minor.Build, with missing parts as 0, so 0.2 == 0.2.0 == 0.2.0.0.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    /// <summary>The latest release if it's newer than <paramref name="current"/>, else null.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _latestUrl);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new UpdateException("Couldn't reach GitHub to check for updates.", ex);
        }
        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"GitHub returned {(int)resp.StatusCode} when checking for updates.");
            var json = await resp.Content.ReadAsStringAsync(ct);
            return Parse(json, current);
        }
    }

    internal static UpdateInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
        var tag = Str(root, "tag_name");
        var version = ParseTag(tag);
        if (version is null || version <= Normalize(current)) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            if (!string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            var url = Str(a, "browser_download_url");
            if (url is null) return null;
            var digest = Str(a, "digest");
            string? sha = null;
            if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = digest[7..].ToLowerInvariant();
            var notes = Str(root, "body");
            return new UpdateInfo(version, tag!, url, a.TryGetProperty("size", out var s) ? s.GetInt64() : 0, sha,
                Str(root, "html_url") ?? url, notes);
        }
        return null; // release without an exe (e.g. extension-only)
    }

    /// <summary>Download the update into <paramref name="directory"/> and verify it.
    /// Returns the path of the verified file.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, string directory, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"Savepoint-{update.Version}.exe");
        var tmp = target + ".download";
        try
        {
            using var resp = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"Download failed: GitHub returned {(int)resp.StatusCode}.");
            var total = resp.Content.Headers.ContentLength ?? update.Size;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[1 << 16];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    sha.AppendData(buffer, 0, n);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
                if (update.Size > 0 && read != update.Size)
                    throw new UpdateException($"Download was incomplete ({read} of {update.Size} bytes).");
            }
            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (update.Sha256 is not null && actual != update.Sha256)
                throw new UpdateException("The downloaded file doesn't match the release's checksum; not installing it.");
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException("Couldn't download the update.", ex);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
