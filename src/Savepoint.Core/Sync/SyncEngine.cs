using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Savepoint.Core.Api;
using Savepoint.Core.Paths;
using Savepoint.Core.Scanning;
using Savepoint.Core.State;

namespace Savepoint.Core.Sync;

public sealed class SyncEngineOptions
{
    /// <summary>Root for staging downloads and local snapshots (normally %LOCALAPPDATA%\Savepoint).</summary>
    public required string DataDirectory { get; init; }
    public int UploadParallelism { get; init; } = 4;
    public int SnapshotsToKeep { get; init; } = 3;
    public ScanOptions Scan { get; init; } = new();
}

/// <summary>
/// The sync algorithm from DESIGN §6. Terms:
/// L = local manifest (fresh scan), S = last-synced version/manifest, H = server head.
/// All operations on one game are serialized.
/// </summary>
public sealed class SyncEngine
{
    public const string PushOp = "push";

    private readonly ISavepointApi _api;
    private readonly StateStore _state;
    private readonly IPathEnvironment _paths;
    private readonly Func<Game, bool> _isRunning;
    private readonly SyncEngineOptions _options;
    private readonly ILogger _log;
    private readonly Scanner _scanner;
    private readonly SnapshotManager _snapshots;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private int _active;

    public SyncEngine(ISavepointApi api, StateStore state, IPathEnvironment paths, Func<Game, bool> isGameRunning,
        SyncEngineOptions options, ILogger? log = null)
    {
        _api = api;
        _state = state;
        _paths = paths;
        _isRunning = isGameRunning;
        _options = options;
        _log = log ?? NullLogger.Instance;
        _scanner = new Scanner(options.Scan, _log);
        _snapshots = new SnapshotManager(Path.Combine(options.DataDirectory, "snapshots"), state, options.SnapshotsToKeep);
    }

    /// <summary>Things worth telling the user about (toasts).</summary>
    public event EventHandler<SyncNotification>? Notification;

    /// <summary>Raised when the number of running operations changes (tray icon "syncing").</summary>
    public event EventHandler<int>? ActivityChanged;

    public int ActiveOperations => Volatile.Read(ref _active);

    // ================================================================== roots & scanning

    public IReadOnlyDictionary<string, ResolvedRoot> ResolveRoots(Game game)
    {
        var roots = new Dictionary<string, ResolvedRoot>(StringComparer.Ordinal);
        foreach (var r in game.Roots)
            roots[r.Alias] = new ResolvedRoot(r.Alias, PathTemplate.Resolve(r.EffectiveTemplate, _paths));
        return roots;
    }

    /// <summary>Scan the game's roots, reusing hashes from the previous scan when size and mtime match.</summary>
    public Manifest Scan(Game game, CancellationToken ct = default)
    {
        var roots = ResolveRoots(game);
        var st = _state.GetGameState(game.Id);
        var cache = MergeCaches(st.LastScan, st.LastSyncedManifest);
        var manifest = _scanner.Scan(roots.Values, new ExcludeMatcher(game.Excludes), cache, ct);
        _state.SetLastScan(game.Id, manifest, DateTimeOffset.UtcNow);
        return manifest;
    }

    private static Manifest? MergeCaches(Manifest? scan, Manifest synced)
    {
        if (scan is null) return synced.IsEmpty ? null : synced;
        if (synced.IsEmpty) return scan;
        var byPath = synced.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        foreach (var e in scan.Entries) byPath[e.Path] = e;
        return new Manifest(byPath.Values);
    }

    // ================================================================== public operations

    /// <summary>Pull check (DESIGN §6). Runs on startup, when /heads changes, and before launching from Playnite.</summary>
    public Task<SyncResult> PullCheckAsync(Game game, CancellationToken ct = default) =>
        WithGameLock(game, ct, () => PullCheckCoreAsync(game, ct));

    /// <summary>Push after a session, checkpoint or manual backup.</summary>
    public Task<SyncResult> PushAsync(Game game, string type, CancellationToken ct = default) =>
        WithGameLock(game, ct, () => PushCoreAsync(game, type, queueIfOffline: true, ct));

    /// <summary>Tray "Restore latest": take the server head even if there are local changes.
    /// Local changes are uploaded as pre_restore and zipped first.</summary>
    public Task<SyncResult> RestoreLatestAsync(Game game, CancellationToken ct = default) =>
        WithGameLock(game, ct, async () =>
        {
            if (_isRunning(game)) return SyncResult.Of(SyncOutcome.SkippedGameRunning, $"{game.Name} is running; close it first");
            var L = Scan(game, ct);
            var heads = await _api.GetHeadsAsync(ct);
            if (!heads.TryGetValue(game.Id, out var h)) return SyncResult.Of(SyncOutcome.NoChange, "The server has no versions of this game yet");
            var head = await _api.GetVersionAsync(h.VersionId, ct);
            await RestoreCoreAsync(game, head, L, alreadyUploaded: null, ct);
            _state.SetConflict(game.Id, null);
            return Restored(game, head);
        });

    /// <summary>Resolve a manual-mode conflict.</summary>
    public Task<SyncResult> ResolveConflictAsync(Game game, ConflictChoice choice, CancellationToken ct = default) =>
        WithGameLock(game, ct, async () =>
        {
            var st = _state.GetGameState(game.Id);
            if (st.Conflict is null) return SyncResult.Of(SyncOutcome.NoChange, "No conflict to resolve");
            if (choice == ConflictChoice.KeepLocal)
            {
                var promoted = await _api.PromoteAsync(st.Conflict.LocalVersionId, ct);
                var full = await _api.GetVersionAsync(promoted.Id, ct);
                _state.SetSynced(game.Id, full.Id, Manifest.FromDtos(full.Files));
                _state.SetConflict(game.Id, null);
                Notify(NotificationKind.ConflictResolved, game, "Kept this PC's save; it is now the latest version.");
                return SyncResult.Of(SyncOutcome.ConflictResolvedLocal, "Kept local save", full);
            }

            if (_isRunning(game)) return SyncResult.Of(SyncOutcome.SkippedGameRunning, $"{game.Name} is running; close it first");
            var L = Scan(game, ct);
            var heads = await _api.GetHeadsAsync(ct);
            var head = await _api.GetVersionAsync(heads[game.Id].VersionId, ct);
            var uploaded = await TryGetManifestAsync(st.Conflict.LocalVersionId, ct);
            await RestoreCoreAsync(game, head, L, uploaded, ct);
            _state.SetConflict(game.Id, null);
            Notify(NotificationKind.ConflictResolved, game, "Took the server's save. This PC's save is kept as a version.");
            return SyncResult.Of(SyncOutcome.ConflictResolvedServer, "Took server save", head);
        });

    /// <summary>Retry queued pushes whose backoff has elapsed.</summary>
    public async Task<int> FlushPendingAsync(IReadOnlyCollection<Game> games, CancellationToken ct = default)
    {
        var flushed = 0;
        foreach (var op in _state.GetPendingOps())
        {
            if (op.NextAttemptAt > DateTimeOffset.UtcNow) continue;
            var game = games.FirstOrDefault(g => g.Id == op.GameId);
            if (game is null)
            {
                _state.CompleteOp(op.Id); // game deleted on the server
                continue;
            }
            var type = ReadPushType(op.PayloadJson);
            SyncResult result;
            try
            {
                result = await WithGameLock(game, ct, () => PushCoreAsync(game, type, queueIfOffline: false, ct));
            }
            catch (ServerUnreachableException ex)
            {
                result = SyncResult.Of(SyncOutcome.Offline, ex.Message);
            }

            if (result.Outcome is SyncOutcome.Offline or SyncOutcome.Busy or SyncOutcome.Failed)
            {
                var delay = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Min(op.Attempts, 6))));
                _state.FailOp(op.Id, result.Message, DateTimeOffset.UtcNow + delay);
                if (result.Outcome == SyncOutcome.Offline) break; // no point trying the rest now
            }
            else
            {
                _state.CompleteOp(op.Id);
                flushed++;
            }
        }
        return flushed;
    }

    public bool HasPendingOps => _state.GetPendingOps().Count > 0;

    // ================================================================== pull

    private async Task<SyncResult> PullCheckCoreAsync(Game game, CancellationToken ct)
    {
        if (_isRunning(game))
            return SyncResult.Of(SyncOutcome.SkippedGameRunning, $"{game.Name} is running; will check after it exits");

        var L = Scan(game, ct);
        var st = _state.GetGameState(game.Id);

        IReadOnlyDictionary<string, HeadInfo> heads;
        try { heads = await _api.GetHeadsAsync(ct); }
        catch (ServerUnreachableException ex)
        {
            if (!L.SameContentAs(st.LastSyncedManifest)) QueuePush(game, VersionTypes.Offline);
            return SyncResult.Of(SyncOutcome.Offline, ex.Message);
        }

        heads.TryGetValue(game.Id, out var h);

        if (st.Conflict is not null)
        {
            // Waiting on the user. If the head moved since, they decided in the web UI.
            if (h is not null && h.VersionId != st.Conflict.HeadVersionId)
            {
                var head = await _api.GetVersionAsync(h.VersionId, ct);
                var uploaded = await TryGetManifestAsync(st.Conflict.LocalVersionId, ct);
                await RestoreCoreAsync(game, head, L, uploaded, ct);
                _state.SetConflict(game.Id, null);
                return Restored(game, head);
            }
            if (game.IsAuto && h is not null)
            {
                // Auto mode deferred a restore because the game was running; finish it now.
                var head = await _api.GetVersionAsync(h.VersionId, ct);
                return await HandleConflictAsync(game, L, st, head, VersionTypes.Offline, ct);
            }
            return SyncResult.Of(SyncOutcome.ConflictPending, "Waiting for you to resolve a conflict");
        }

        var localChanged = !L.SameContentAs(st.LastSyncedManifest);

        if (h is null)
        {
            // Nothing on the server yet.
            if (L.IsEmpty) return SyncResult.Of(SyncOutcome.NoChange, "No saves yet");
            return await UploadAndSetHeadAsync(game, L, parent: null, VersionTypes.Offline, ct);
        }

        var serverMoved = h.VersionId != st.LastSyncedVersionId;
        Manifest? headManifest = null;
        VersionInfo? headVersion = null;
        if (localChanged && serverMoved)
        {
            headVersion = await _api.GetVersionAsync(h.VersionId, ct);
            headManifest = Manifest.FromDtos(headVersion.Files);
        }

        var action = SyncDecider.DecidePull(localChanged, serverMoved,
            headManifest is not null && L.SameContentAs(headManifest), game.IsAuto);
        _log.LogInformation("Pull check {Game}: L{L}S, H{H}S → {Action}", game.Name,
            localChanged ? "≠" : "=", serverMoved ? "≠" : "=", action);

        switch (action)
        {
            case PullAction.Nothing:
                return SyncResult.Of(SyncOutcome.NoChange, "Up to date");

            case PullAction.RestoreHead:
            {
                var head = headVersion ?? await _api.GetVersionAsync(h.VersionId, ct);
                await RestoreCoreAsync(game, head, L, alreadyUploaded: null, ct);
                return Restored(game, head);
            }

            case PullAction.OfferRestore:
                Notify(NotificationKind.RestoreAvailable, game,
                    $"A newer save from {h.MachineId switch { null => "the web UI", _ => "another machine" }} is available.");
                return SyncResult.Of(SyncOutcome.RestoreOffered, "Newer save available");

            case PullAction.PushOffline:
                return await UploadAndSetHeadAsync(game, L, st.LastSyncedVersionId, VersionTypes.Offline, ct);

            case PullAction.AdoptHead:
                _state.SetSynced(game.Id, headVersion!.Id, headManifest!);
                return SyncResult.Of(SyncOutcome.AdoptedHead, "Local saves already match the server", headVersion);

            case PullAction.Conflict:
                return await HandleConflictAsync(game, L, st, headVersion!, VersionTypes.Offline, ct);

            default:
                throw new InvalidOperationException(action.ToString());
        }
    }

    // ================================================================== push

    private async Task<SyncResult> PushCoreAsync(Game game, string type, bool queueIfOffline, CancellationToken ct)
    {
        var st = _state.GetGameState(game.Id);
        if (st.Conflict is not null && !game.IsAuto)
            return SyncResult.Of(SyncOutcome.ConflictPending, "Resolve the pending conflict first");

        var L = Scan(game, ct);
        if (st.Conflict is null && L.SameContentAs(st.LastSyncedManifest))
            return SyncResult.Of(SyncOutcome.NoChange, "No changes since the last sync");

        try
        {
            var heads = await _api.GetHeadsAsync(ct);
            heads.TryGetValue(game.Id, out var h);
            if (h is not null && (h.VersionId != st.LastSyncedVersionId || st.Conflict is not null))
            {
                var head = await _api.GetVersionAsync(h.VersionId, ct);
                if (L.SameContentAs(Manifest.FromDtos(head.Files)))
                {
                    _state.SetSynced(game.Id, head.Id, Manifest.FromDtos(head.Files));
                    return SyncResult.Of(SyncOutcome.AdoptedHead, "Local saves already match the server", head);
                }
                return await HandleConflictAsync(game, L, st, head, type, ct);
            }
            // h is null: first version (or the server lost it); parent must be null to become head.
            return await UploadAndSetHeadAsync(game, L, h is null ? null : st.LastSyncedVersionId, type, ct);
        }
        catch (ServerUnreachableException ex)
        {
            if (queueIfOffline)
            {
                QueuePush(game, type);
                return SyncResult.Of(SyncOutcome.Queued, $"Server unreachable; backup queued ({ex.Message})");
            }
            return SyncResult.Of(SyncOutcome.Offline, ex.Message);
        }
    }

    private async Task<SyncResult> UploadAndSetHeadAsync(Game game, Manifest L, string? parent, string type, CancellationToken ct)
    {
        VersionInfo v;
        try
        {
            v = await UploadAsync(game, L, parent, type, makeHead: true, ct);
        }
        catch (ApiException ex) when (ex.IsHeadMoved)
        {
            // Another machine pushed between our /heads check and the upload.
            var head = await _api.GetVersionAsync(ex.HeadVersionId!, ct);
            return await HandleConflictAsync(game, L, _state.GetGameState(game.Id), head, type, ct);
        }
        _state.SetSynced(game.Id, v.Id, L);
        return SyncResult.Of(v.Unchanged ? SyncOutcome.AdoptedHead : SyncOutcome.Pushed,
            v.Unchanged ? "Server already had this save" : $"Backed up ({type})", v);
    }

    // ================================================================== conflicts

    private async Task<SyncResult> HandleConflictAsync(Game game, Manifest L, GameState st, VersionInfo head,
        string type, CancellationToken ct)
    {
        // 1. Preserve local state without touching the head (reusing an earlier upload of
        //    the same content from a deferred conflict, so retries don't pile up versions).
        VersionInfo? local = null;
        if (st.Conflict is not null)
        {
            var earlier = await TryGetVersionAsync(st.Conflict.LocalVersionId, ct);
            if (earlier is not null && L.SameContentAs(Manifest.FromDtos(earlier.Files))) local = earlier;
        }
        local ??= await UploadAsync(game, L, st.LastSyncedVersionId, type, makeHead: false, ct);
        if (local.Unchanged)
        {
            // Our content is identical to the head after all.
            _state.SetSynced(game.Id, local.Id, L);
            _state.SetConflict(game.Id, null);
            return SyncResult.Of(SyncOutcome.AdoptedHead, "Local saves already match the server", local);
        }

        if (!game.IsAuto)
        {
            _state.SetConflict(game.Id, new PendingConflict(local.Id, head.Id, DateTimeOffset.UtcNow));
            Notify(NotificationKind.ConflictNeedsDecision, game,
                $"This PC and {head.MachineName ?? "the server"} both changed the save. Both are kept; choose which to use.");
            return SyncResult.Of(SyncOutcome.ConflictPending, "Conflict needs a decision", local);
        }

        // 2. Auto mode: newer side wins.
        var winner = SyncDecider.DecideConflict(L.LatestMtime, head.LatestMtime, _api.ClockOffset);
        _log.LogInformation("Conflict on {Game}: local newest {Local:o} (offset {Offset}), head newest {Head:o} → {Winner}",
            game.Name, L.LatestMtime, _api.ClockOffset, head.LatestMtime, winner);
        if (winner == ConflictWinner.Local)
        {
            var promoted = await _api.PromoteAsync(local.Id, ct);
            _state.SetSynced(game.Id, promoted.Id, L);
            _state.SetConflict(game.Id, null);
            Notify(NotificationKind.ConflictResolved, game,
                $"Both PCs changed the save. This PC's was newer and is now the latest; the other is kept as a version.");
            return SyncResult.Of(SyncOutcome.ConflictResolvedLocal, "Conflict: local was newer", promoted);
        }

        if (_isRunning(game))
        {
            // Can't overwrite files under a running game. Remember the upload and finish after it exits.
            _state.SetConflict(game.Id, new PendingConflict(local.Id, head.Id, DateTimeOffset.UtcNow));
            return SyncResult.Of(SyncOutcome.SkippedGameRunning, "Conflict: server is newer; will restore after the game exits");
        }
        await RestoreCoreAsync(game, head, L, alreadyUploaded: L, ct);
        _state.SetConflict(game.Id, null);
        Notify(NotificationKind.ConflictResolved, game,
            $"Both PCs changed the save. {head.MachineName ?? "The server"}'s was newer and was restored; this PC's is kept as a version.");
        return SyncResult.Of(SyncOutcome.ConflictResolvedServer, "Conflict: server was newer", head);
    }

    // ================================================================== upload

    private async Task<VersionInfo> UploadAsync(Game game, Manifest L, string? parent, string type, bool makeHead,
        CancellationToken ct)
    {
        var roots = ResolveRoots(game);
        await UploadBlobsAsync(roots, L, (await _api.CheckBlobsAsync(L.Entries.Select(e => e.Hash), ct)).ToHashSet(), ct);
        var request = new CreateVersionRequest(parent, type, makeHead, DateTimeOffset.Now, L.ToDtos());
        try
        {
            return await _api.CreateVersionAsync(game.Id, request, ct);
        }
        catch (ApiException ex) when (ex.IsMissingBlobs)
        {
            // GC can remove a blob between /blobs/check and here; upload again and retry once.
            await UploadBlobsAsync(roots, L, ex.Missing.ToHashSet(), ct);
            return await _api.CreateVersionAsync(game.Id, request, ct);
        }
    }

    private async Task UploadBlobsAsync(IReadOnlyDictionary<string, ResolvedRoot> roots, Manifest L,
        HashSet<string> missing, CancellationToken ct)
    {
        if (missing.Count == 0) return;
        var byHash = L.Entries.Where(e => missing.Contains(e.Hash))
            .GroupBy(e => e.Hash).Select(g => g.First()).ToList();
        await Parallel.ForEachAsync(byHash,
            new ParallelOptions { MaxDegreeOfParallelism = _options.UploadParallelism, CancellationToken = ct },
            async (entry, token) =>
            {
                var file = LocalPath(roots, entry);
                try
                {
                    await _api.UploadBlobAsync(entry.Hash, file, token);
                }
                catch (ApiException ex) when (ex.Code == "hash_mismatch")
                {
                    throw new FileLockedException(file, new IOException("File changed while uploading"));
                }
            });
    }

    // ================================================================== restore

    /// <param name="alreadyUploaded">Local content already stored on the server (e.g. by
    /// conflict handling); if L still matches it, no pre_restore upload is needed.</param>
    private async Task RestoreCoreAsync(Game game, VersionInfo version, Manifest L, Manifest? alreadyUploaded,
        CancellationToken ct)
    {
        var roots = ResolveRoots(game);
        var st = _state.GetGameState(game.Id);
        var V = Manifest.FromDtos(version.Files ?? (await _api.GetVersionAsync(version.Id, ct)).Files);

        // 1. Safety nets: a local zip always, and a pre_restore version if L has unsynced changes.
        _snapshots.Create(game.Id, L, roots);
        var unsynced = !L.IsEmpty && !L.SameContentAs(st.LastSyncedManifest) && !L.SameContentAs(V);
        if (unsynced && (alreadyUploaded is null || !L.SameContentAs(alreadyUploaded)))
        {
            await UploadAsync(game, L, st.LastSyncedVersionId, VersionTypes.PreRestore, makeHead: false, ct);
        }

        // 2. Download what we don't already have locally into staging, verifying hashes.
        var staging = Path.Combine(_options.DataDirectory, "staging", game.Id);
        Directory.CreateDirectory(staging);
        try
        {
            var localByHash = L.Entries.GroupBy(e => e.Hash).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var needed = V.Entries.Select(e => e.Hash).Distinct()
                .Where(h => !localByHash.ContainsKey(h)).ToList();
            await Parallel.ForEachAsync(needed,
                new ParallelOptions { MaxDegreeOfParallelism = _options.UploadParallelism, CancellationToken = ct },
                async (hash, token) =>
                {
                    var dest = Path.Combine(staging, hash);
                    await using (var fs = File.Create(dest))
                    {
                        await _api.DownloadBlobAsync(hash, fs, token);
                    }
                    var actual = Hashing.Sha256File(dest);
                    if (actual != hash) throw new InvalidDataException($"Downloaded blob {hash} has hash {actual}");
                });

            // Copy files that already exist locally under another path into staging too, so
            // the mirror step never reads from a file it might be about to overwrite.
            foreach (var e in V.Entries)
            {
                var stagePath = Path.Combine(staging, e.Hash);
                if (File.Exists(stagePath)) continue;
                var existing = localByHash[e.Hash];
                if (existing.Path == e.Path) continue; // unchanged in place
                File.Copy(LocalPath(roots, existing), stagePath, overwrite: true);
            }

            // 3. Mirror V into the roots.
            foreach (var e in V.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!roots.ContainsKey(e.Alias))
                {
                    _log.LogWarning("Version {Version} has files for root {Alias}, which this game no longer has; skipped",
                        version.Id, e.Alias);
                    continue;
                }
                var target = LocalPath(roots, e);
                var local = L.Get(e.Path);
                if (local is null || local.Hash != e.Hash)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var tmp = target + ".savepoint-tmp";
                    File.Copy(Path.Combine(staging, e.Hash), tmp, overwrite: true);
                    File.Move(tmp, target, overwrite: true);
                }
                File.SetLastWriteTimeUtc(target, e.Mtime.UtcDateTime);
            }
            foreach (var e in L.Entries)
            {
                if (V.Get(e.Path) is not null || !roots.ContainsKey(e.Alias)) continue;
                // L only holds non-excluded files, so excluded files are never deleted.
                var file = LocalPath(roots, e);
                try { File.Delete(file); } catch (DirectoryNotFoundException) { }
                RemoveEmptyParents(Path.GetDirectoryName(file)!, roots[e.Alias].Directory);
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }

        // 4. S = V. The restored files now carry V's sizes and mtimes, so V doubles as the scan cache.
        _state.SetSynced(game.Id, version.Id, V);
        _state.SetLastScan(game.Id, V, DateTimeOffset.UtcNow);
    }

    private static void RemoveEmptyParents(string dir, string stopAt)
    {
        var stop = Path.GetFullPath(stopAt).TrimEnd(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length > stop.Length && current.StartsWith(stop, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(current).Any()) return;
                Directory.Delete(current);
            }
            catch (IOException) { return; }
            current = Path.GetDirectoryName(current)!;
        }
    }

    // ================================================================== helpers

    private static string LocalPath(IReadOnlyDictionary<string, ResolvedRoot> roots, ManifestEntry e) =>
        Path.Combine(roots[e.Alias].Directory, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    private async Task<Manifest?> TryGetManifestAsync(string versionId, CancellationToken ct)
    {
        var v = await TryGetVersionAsync(versionId, ct);
        return v is null ? null : Manifest.FromDtos(v.Files);
    }

    private async Task<VersionInfo?> TryGetVersionAsync(string versionId, CancellationToken ct)
    {
        try { return await _api.GetVersionAsync(versionId, ct); }
        catch (ApiException) { return null; } // e.g. deleted by retention
    }

    private SyncResult Restored(Game game, VersionInfo head)
    {
        Notify(NotificationKind.Restored, game,
            $"Restored the save from {head.MachineName ?? "the web UI"} ({head.CreatedAt.ToLocalTime():g}).");
        return SyncResult.Of(SyncOutcome.Restored, "Restored latest save", head);
    }

    private void QueuePush(Game game, string type)
    {
        _state.Enqueue(game.Id, PushOp, JsonSerializer.Serialize(new { type }));
        Notify(NotificationKind.Queued, game, "Server unreachable; the backup will upload when it's back.");
    }

    private static string ReadPushType(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? VersionTypes.Offline : VersionTypes.Offline;
        }
        catch (JsonException) { return VersionTypes.Offline; }
    }

    private void Notify(NotificationKind kind, Game game, string message)
    {
        try { Notification?.Invoke(this, new SyncNotification(kind, game.Id, game.Name, message)); }
        catch (Exception ex) { _log.LogWarning(ex, "Notification handler failed"); }
    }

    private async Task<SyncResult> WithGameLock(Game game, CancellationToken ct, Func<Task<SyncResult>> body)
    {
        var sem = _locks.GetOrAdd(game.Id, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        ActivityChanged?.Invoke(this, Interlocked.Increment(ref _active));
        try
        {
            return await body();
        }
        catch (FileLockedException ex)
        {
            _log.LogInformation("{Game}: {Message}; will retry", game.Name, ex.Message);
            return SyncResult.Of(SyncOutcome.Busy, ex.Message);
        }
        catch (PathTemplateException ex)
        {
            Notify(NotificationKind.Error, game, ex.Message);
            return SyncResult.Of(SyncOutcome.Failed, ex.Message);
        }
        catch (ServerUnreachableException ex)
        {
            return SyncResult.Of(SyncOutcome.Offline, ex.Message);
        }
        catch (ApiException ex)
        {
            _log.LogError(ex, "{Game}: server rejected the request", game.Name);
            Notify(NotificationKind.Error, game, ex.Message);
            return SyncResult.Of(SyncOutcome.Failed, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.LogError(ex, "{Game}: sync failed", game.Name);
            Notify(NotificationKind.Error, game, ex.Message);
            return SyncResult.Of(SyncOutcome.Failed, ex.Message);
        }
        finally
        {
            ActivityChanged?.Invoke(this, Interlocked.Decrement(ref _active));
            sem.Release();
        }
    }
}
