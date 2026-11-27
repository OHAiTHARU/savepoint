using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Savepoint.Core.Api;

public interface ISavepointApi
{
    /// <summary>This machine's clock minus the server's, measured on the last /machines/me call.</summary>
    TimeSpan ClockOffset { get; }

    Task<MachineInfo> GetMeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Game>> GetGamesAsync(CancellationToken ct = default);
    Task<Game> CreateGameAsync(GameInput input, CancellationToken ct = default);
    Task<Game> UpdateGameAsync(string gameId, GameInput input, CancellationToken ct = default);
    Task<Game> SetRootOverrideAsync(string gameId, string alias, string? pathTemplate, CancellationToken ct = default);
    /// <summary>Turn syncing a game on or off for this machine.</summary>
    Task<Game> SetSyncEnabledAsync(string gameId, bool enabled, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, HeadInfo>> GetHeadsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> CheckBlobsAsync(IEnumerable<string> hashes, CancellationToken ct = default);
    Task UploadBlobAsync(string hash, string filePath, CancellationToken ct = default);
    Task DownloadBlobAsync(string hash, Stream destination, CancellationToken ct = default);
    Task<VersionInfo> CreateVersionAsync(string gameId, CreateVersionRequest request, CancellationToken ct = default);
    Task<VersionInfo> GetVersionAsync(string versionId, CancellationToken ct = default);
    Task<VersionInfo> PromoteAsync(string versionId, CancellationToken ct = default);
}

/// <summary>HTTP client for the savepoint-server /api/v1 contract (docs/DESIGN.md §4).</summary>
public sealed class SavepointApi : ISavepointApi, IDisposable
{
    public const string ClientTimeHeader = "X-Savepoint-Client-Time";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(1);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly object _headsLock = new();
    private string? _headsEtag;
    private IReadOnlyDictionary<string, HeadInfo> _headsCache = new Dictionary<string, HeadInfo>();

    public SavepointApi(string serverUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(NormalizeServerUrl(serverUrl), UriKind.Absolute, out var baseUri))
            throw new ArgumentException($"Invalid server URL: {serverUrl}", nameof(serverUrl));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = true;
        _http.BaseAddress = new Uri(baseUri, "api/v1/");
        _http.Timeout = Timeout.InfiniteTimeSpan; // per-request timeouts below
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Savepoint-Agent/1.0");
        ServerUrl = baseUri.ToString().TrimEnd('/');
    }

    public string ServerUrl { get; }
    public TimeSpan ClockOffset { get; private set; }

    /// <summary>"savepoint.example.com" → "https://savepoint.example.com/".</summary>
    public static string NormalizeServerUrl(string url)
    {
        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!url.EndsWith('/')) url += "/";
        return url;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ------------------------------------------------------------------ machines & games

    public async Task<MachineInfo> GetMeAsync(CancellationToken ct = default)
    {
        var before = DateTimeOffset.UtcNow;
        var me = await SendJsonAsync<MachineInfo>(HttpMethod.Get, "machines/me", null, ct);
        var after = DateTimeOffset.UtcNow;
        var localMid = before + (after - before) / 2;
        ClockOffset = localMid - me.ServerTime;
        return me;
    }

    public async Task<IReadOnlyList<Game>> GetGamesAsync(CancellationToken ct = default)
    {
        var body = await SendJsonAsync<GamesEnvelope>(HttpMethod.Get, "games", null, ct);
        return body.Games;
    }

    public Task<Game> CreateGameAsync(GameInput input, CancellationToken ct = default)
        => SendJsonAsync<Game>(HttpMethod.Post, "games", WithoutNulls(input), ct);

    public Task<Game> UpdateGameAsync(string gameId, GameInput input, CancellationToken ct = default)
        => SendJsonAsync<Game>(HttpMethod.Patch, $"games/{Uri.EscapeDataString(gameId)}", WithoutNulls(input), ct);

    public Task<Game> SetRootOverrideAsync(string gameId, string alias, string? pathTemplate, CancellationToken ct = default)
        => SendJsonAsync<Game>(HttpMethod.Put,
            $"games/{Uri.EscapeDataString(gameId)}/roots/{Uri.EscapeDataString(alias)}/override",
            new { path_template = string.IsNullOrWhiteSpace(pathTemplate) ? null : pathTemplate }, ct);

    public Task<Game> SetSyncEnabledAsync(string gameId, bool enabled, CancellationToken ct = default)
        => SendJsonAsync<Game>(HttpMethod.Put, $"games/{Uri.EscapeDataString(gameId)}/sync", new { enabled }, ct);

    // ------------------------------------------------------------------ heads

    public async Task<IReadOnlyDictionary<string, HeadInfo>> GetHeadsAsync(CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, "heads");
        string? etag;
        lock (_headsLock) etag = _headsEtag;
        if (etag is not null) req.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var resp = await SendAsync(req, DefaultTimeout, ct);
        if (resp.StatusCode == HttpStatusCode.NotModified)
        {
            lock (_headsLock) return _headsCache;
        }
        await EnsureSuccessAsync(resp, ct);
        var heads = await resp.Content.ReadFromJsonAsync<Dictionary<string, HeadInfo>>(Json.Options, ct)
                    ?? new Dictionary<string, HeadInfo>();
        lock (_headsLock)
        {
            _headsCache = heads;
            _headsEtag = resp.Headers.ETag?.ToString();
        }
        return heads;
    }

    // ------------------------------------------------------------------ blobs

    public async Task<IReadOnlyList<string>> CheckBlobsAsync(IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var list = hashes.Distinct(StringComparer.Ordinal).ToList();
        if (list.Count == 0) return Array.Empty<string>();
        var missing = new List<string>();
        foreach (var chunk in list.Chunk(5000))
        {
            var body = await SendJsonAsync<MissingEnvelope>(HttpMethod.Post, "blobs/check", new { hashes = chunk }, ct);
            missing.AddRange(body.Missing);
        }
        return missing;
    }

    public async Task UploadBlobAsync(string hash, string filePath, CancellationToken ct = default)
    {
        await using var fs = Hashing.OpenShared(filePath);
        using var req = NewRequest(HttpMethod.Put, $"blobs/{hash}");
        req.Content = new StreamContent(fs, 1 << 20);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentLength = fs.Length;
        using var resp = await SendAsync(req, TransferTimeout, ct);
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task DownloadBlobAsync(string hash, Stream destination, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, $"blobs/{hash}");
        using var resp = await SendAsync(req, TransferTimeout, ct, HttpCompletionOption.ResponseHeadersRead);
        await EnsureSuccessAsync(resp, ct);
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await src.CopyToAsync(destination, 1 << 20, ct);
    }

    // ------------------------------------------------------------------ versions

    public Task<VersionInfo> CreateVersionAsync(string gameId, CreateVersionRequest request, CancellationToken ct = default)
        => SendJsonAsync<VersionInfo>(HttpMethod.Post, $"games/{Uri.EscapeDataString(gameId)}/versions", request, ct);

    public Task<VersionInfo> GetVersionAsync(string versionId, CancellationToken ct = default)
        => SendJsonAsync<VersionInfo>(HttpMethod.Get, $"versions/{Uri.EscapeDataString(versionId)}", null, ct);

    public Task<VersionInfo> PromoteAsync(string versionId, CancellationToken ct = default)
        => SendJsonAsync<VersionInfo>(HttpMethod.Post, $"versions/{Uri.EscapeDataString(versionId)}/promote", null, ct);

    // ------------------------------------------------------------------ plumbing

    private sealed record GamesEnvelope(List<Game> Games);
    private sealed record MissingEnvelope(List<string> Missing);

    private static Dictionary<string, object?> WithoutNulls(GameInput input)
    {
        var d = new Dictionary<string, object?>();
        if (input.Name is not null) d["name"] = input.Name;
        if (input.Roots is not null) d["roots"] = input.Roots.Select(r => new { alias = r.Alias, path_template = r.PathTemplate });
        if (input.ExeNames is not null) d["exe_names"] = input.ExeNames;
        if (input.Excludes is not null) d["excludes"] = input.Excludes;
        if (input.SyncMode is not null) d["sync_mode"] = input.SyncMode;
        return d;
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        // Lets the server record this machine's clock offset (DESIGN §4 "Time and clocks").
        req.Headers.TryAddWithoutValidation(ClientTimeHeader, DateTimeOffset.Now.ToString("o"));
        return req;
    }

    private async Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var req = NewRequest(method, path);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json.Options);
        using var resp = await SendAsync(req, DefaultTimeout, ct);
        await EnsureSuccessAsync(resp, ct);
        var result = await resp.Content.ReadFromJsonAsync<T>(Json.Options, ct);
        return result ?? throw new ServerUnreachableException($"Empty response from {path}");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, TimeSpan timeout, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _http.SendAsync(req, completion, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ServerUnreachableException($"Request to {req.RequestUri} timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new ServerUnreachableException($"Could not reach the server: {ex.Message}", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var status = resp.StatusCode;
        string text = "";
        try { text = await resp.Content.ReadAsStringAsync(ct); } catch { /* ignore */ }

        if ((int)status >= 500 || status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests)
            throw new ServerUnreachableException($"Server returned {(int)status}: {Truncate(text)}");

        ErrorBody? err = null;
        try { err = JsonSerializer.Deserialize<ErrorEnvelope>(text, Json.Options)?.Error; } catch (JsonException) { }
        if (err is null)
        {
            // Not our server's error format: likely a proxy page (e.g. 413 from Nginx).
            throw new ApiException(status, "http_" + (int)status, Truncate(text));
        }
        throw new ApiException(status, err.Code, err.Message, err.Missing, err.HeadVersionId);
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
