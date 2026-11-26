using System.IO.Compression;
using Savepoint.Core.Scanning;
using Savepoint.Core.State;

namespace Savepoint.Core.Sync;

/// <summary>Local zips of a game's saves taken before every restore. A second safety net
/// that works offline (DESIGN §6 "Local state").</summary>
public sealed class SnapshotManager
{
    private readonly string _dir;
    private readonly StateStore _state;
    private readonly int _keep;

    public SnapshotManager(string dir, StateStore state, int keep = 3)
    {
        _dir = dir;
        _state = state;
        _keep = keep;
    }

    public string? Create(string gameId, Manifest manifest, IReadOnlyDictionary<string, ResolvedRoot> roots)
    {
        if (manifest.IsEmpty) return null;
        var now = DateTimeOffset.UtcNow;
        var gameDir = Path.Combine(_dir, gameId);
        Directory.CreateDirectory(gameDir);
        var path = Path.Combine(gameDir, $"{now:yyyyMMdd-HHmmss-fff}.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var e in manifest.Entries)
            {
                if (!roots.TryGetValue(e.Alias, out var root)) continue;
                var file = Path.Combine(root.Directory, e.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) continue;
                var entry = zip.CreateEntry(e.Path, CompressionLevel.Fastest);
                entry.LastWriteTime = e.Mtime;
                using var src = Hashing.OpenShared(file);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        _state.AddSnapshot(gameId, path, now);
        Prune(gameId);
        return path;
    }

    private void Prune(string gameId)
    {
        foreach (var old in _state.GetSnapshots(gameId).Skip(_keep))
        {
            try { File.Delete(old.Path); } catch (IOException) { }
            _state.DeleteSnapshot(old.Id);
        }
    }
}
