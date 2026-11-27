using System.Net;
using Savepoint.Core;
using Savepoint.Core.Api;

namespace Savepoint.Core.Tests;

/// <summary>In-memory implementation of the server contract (DESIGN §4), shared by
/// several <see cref="FakeApi"/> "machines".</summary>
public sealed class FakeServer
{
    private readonly object _lock = new();
    private int _seq;
    public Dictionary<string, Game> Games { get; } = new();
    public Dictionary<string, byte[]> Blobs { get; } = new();
    public Dictionary<string, VersionInfo> Versions { get; } = new();
    public bool Offline { get; set; }
    public int BlobUploads { get; set; }

    private string NextId() => $"{DateTime.UtcNow.Ticks:x16}-{Interlocked.Increment(ref _seq):d6}";

    public Game AddGame(string name, string syncMode = SyncModes.Auto, params GameRoot[] roots)
    {
        var g = new Game
        {
            Id = "game-" + NextId(),
            Name = name,
            Roots = roots.ToList(),
            SyncMode = syncMode,
            Excludes = new List<string>(),
        };
        lock (_lock) Games[g.Id] = g;
        return g;
    }

    public string? HeadOf(string gameId)
    {
        lock (_lock) return Games[gameId].HeadVersionId;
    }

    public void SetHead(string gameId, string? versionId)
    {
        lock (_lock) Games[gameId] = Games[gameId] with { HeadVersionId = versionId };
    }

    internal void CheckOnline()
    {
        if (Offline) throw new ServerUnreachableException("fake server offline");
    }

    internal VersionInfo Create(string gameId, string? machineId, CreateVersionRequest req)
    {
        lock (_lock)
        {
            var game = Games[gameId];
            var missing = req.Files.Select(f => f.Hash).Distinct().Where(h => !Blobs.ContainsKey(h)).ToList();
            if (missing.Count > 0)
                throw new ApiException(HttpStatusCode.BadRequest, "missing_blobs", "missing", missing);

            if (game.HeadVersionId is not null)
            {
                var head = Versions[game.HeadVersionId];
                if (SameContent(head.Files!, req.Files))
                    return head with { Unchanged = true, IsHead = true };
            }
            if (req.MakeHead && req.ParentVersionId != game.HeadVersionId)
                throw new ApiException(HttpStatusCode.Conflict, "head_moved", "moved", headVersionId: game.HeadVersionId);

            var v = new VersionInfo
            {
                Id = "v-" + NextId(),
                GameId = gameId,
                ParentVersionId = req.ParentVersionId,
                Type = req.Type,
                MachineId = machineId,
                MachineName = machineId,
                CreatedAt = DateTimeOffset.UtcNow,
                FileCount = req.Files.Count,
                TotalSize = req.Files.Sum(f => f.Size),
                LatestMtime = req.Files.Count == 0 ? null : req.Files.Max(f => f.Mtime.ToUniversalTime()),
                Files = req.Files.Select(f => f with { Mtime = f.Mtime.ToUniversalTime() }).ToList(),
            };
            Versions[v.Id] = v;
            if (req.MakeHead) Games[gameId] = game with { HeadVersionId = v.Id };
            return v with { IsHead = req.MakeHead };
        }
    }

    internal VersionInfo Promote(string versionId, string? machineId)
    {
        lock (_lock)
        {
            var src = Versions[versionId];
            var game = Games[src.GameId];
            if (game.HeadVersionId == versionId) return src with { Unchanged = true, IsHead = true };
            var v = src with
            {
                Id = "v-" + NextId(),
                Type = VersionTypes.Promoted,
                SourceVersionId = src.Id,
                ParentVersionId = game.HeadVersionId,
                MachineId = machineId,
                MachineName = machineId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            Versions[v.Id] = v;
            Games[src.GameId] = game with { HeadVersionId = v.Id };
            return v with { IsHead = true };
        }
    }

    private static bool SameContent(List<FileEntryDto> a, List<FileEntryDto> b) =>
        a.Count == b.Count && a.Select(f => (f.Path, f.Hash)).ToHashSet().SetEquals(b.Select(f => (f.Path, f.Hash)));

    public List<VersionInfo> VersionsOf(string gameId)
    {
        lock (_lock) return Versions.Values.Where(v => v.GameId == gameId).OrderBy(v => v.Id).ToList();
    }
}

public sealed class FakeApi : ISavepointApi
{
    private readonly FakeServer _server;
    private readonly string _machineId;

    public FakeApi(FakeServer server, string machineId)
    {
        _server = server;
        _machineId = machineId;
    }

    public TimeSpan ClockOffset { get; set; }

    /// <summary>Runs once just before the next version is created (to simulate a race).</summary>
    public Action? BeforeNextCreate { get; set; }

    public Task<MachineInfo> GetMeAsync(CancellationToken ct = default)
    {
        _server.CheckOnline();
        return Task.FromResult(new MachineInfo(_machineId, _machineId, DateTimeOffset.UtcNow, 0));
    }

    public Task<IReadOnlyList<Game>> GetGamesAsync(CancellationToken ct = default)
    {
        _server.CheckOnline();
        return Task.FromResult<IReadOnlyList<Game>>(_server.Games.Values.ToList());
    }

    public Task<Game> CreateGameAsync(GameInput input, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Game> UpdateGameAsync(string gameId, GameInput input, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Game> SetRootOverrideAsync(string gameId, string alias, string? pathTemplate, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Game> SetSyncEnabledAsync(string gameId, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, HeadInfo>> GetHeadsAsync(CancellationToken ct = default)
    {
        _server.CheckOnline();
        var heads = _server.Games.Values.Where(g => g.HeadVersionId is not null)
            .ToDictionary(g => g.Id, g =>
            {
                var v = _server.Versions[g.HeadVersionId!];
                return new HeadInfo(v.Id, v.CreatedAt, v.MachineId);
            });
        return Task.FromResult<IReadOnlyDictionary<string, HeadInfo>>(heads);
    }

    public Task<IReadOnlyList<string>> CheckBlobsAsync(IEnumerable<string> hashes, CancellationToken ct = default)
    {
        _server.CheckOnline();
        lock (_server.Blobs)
            return Task.FromResult<IReadOnlyList<string>>(hashes.Distinct().Where(h => !_server.Blobs.ContainsKey(h)).ToList());
    }

    public Task UploadBlobAsync(string hash, string filePath, CancellationToken ct = default)
    {
        _server.CheckOnline();
        var data = File.ReadAllBytes(filePath);
        if (Hashing.Sha256Bytes(data) != hash)
            throw new ApiException(HttpStatusCode.BadRequest, "hash_mismatch", "mismatch");
        lock (_server.Blobs)
        {
            _server.Blobs[hash] = data;
            _server.BlobUploads++;
        }
        return Task.CompletedTask;
    }

    public async Task DownloadBlobAsync(string hash, Stream destination, CancellationToken ct = default)
    {
        _server.CheckOnline();
        byte[] data;
        lock (_server.Blobs) data = _server.Blobs[hash];
        await destination.WriteAsync(data, ct);
    }

    public Task<VersionInfo> CreateVersionAsync(string gameId, CreateVersionRequest request, CancellationToken ct = default)
    {
        _server.CheckOnline();
        var hook = BeforeNextCreate;
        BeforeNextCreate = null;
        hook?.Invoke();
        return Task.FromResult(_server.Create(gameId, _machineId, request));
    }

    public Task<VersionInfo> GetVersionAsync(string versionId, CancellationToken ct = default)
    {
        _server.CheckOnline();
        if (!_server.Versions.TryGetValue(versionId, out var v))
            throw new ApiException(HttpStatusCode.NotFound, "not_found", "Version not found");
        return Task.FromResult(v);
    }

    public Task<VersionInfo> PromoteAsync(string versionId, CancellationToken ct = default)
    {
        _server.CheckOnline();
        return Task.FromResult(_server.Promote(versionId, _machineId));
    }
}
