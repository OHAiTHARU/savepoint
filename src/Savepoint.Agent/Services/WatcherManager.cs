using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Savepoint.Core.Api;
using Savepoint.Core.Scanning;

namespace Savepoint.Agent.Services;

/// <summary>
/// One FileSystemWatcher per resolved save root. Changes mark the game dirty; once the
/// folder has been quiet for the configured period the game is reported as settled,
/// which triggers a checkpoint push. Overflows also mark the game dirty, and the
/// periodic rescan catches anything the watcher missed.
/// </summary>
public sealed class WatcherManager : IDisposable
{
    private readonly ILogger _log;
    private readonly object _lock = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _dirtySince = new();
    private readonly HashSet<string> _missingRoots = new(StringComparer.OrdinalIgnoreCase);

    public WatcherManager(ILogger log) => _log = log;

    public bool HasMissingRoots
    {
        get { lock (_lock) return _missingRoots.Count > 0; }
    }

    public void Rebuild(IEnumerable<(Game Game, IReadOnlyDictionary<string, ResolvedRoot> Roots)> games)
    {
        lock (_lock)
        {
            DisposeWatchers();
            _missingRoots.Clear();
            foreach (var (game, roots) in games)
            {
                var excludes = new ExcludeMatcher(game.Excludes);
                foreach (var root in roots.Values)
                {
                    if (!Directory.Exists(root.Directory))
                    {
                        _missingRoots.Add(root.Directory);
                        continue;
                    }
                    try
                    {
                        var w = new FileSystemWatcher(root.Directory)
                        {
                            IncludeSubdirectories = true,
                            InternalBufferSize = 64 * 1024,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                           NotifyFilters.LastWrite | NotifyFilters.Size,
                        };
                        var gameId = game.Id;
                        var alias = root.Alias;
                        var dir = root.Directory;
                        FileSystemEventHandler onChange = (_, e) => OnChange(gameId, alias, dir, e.FullPath, excludes);
                        w.Changed += onChange;
                        w.Created += onChange;
                        w.Deleted += onChange;
                        w.Renamed += (_, e) => OnChange(gameId, alias, dir, e.FullPath, excludes);
                        w.Error += (_, e) =>
                        {
                            _log.LogWarning(e.GetException(), "Watcher error for {Dir}; will rescan", dir);
                            MarkDirty(gameId);
                        };
                        w.EnableRaisingEvents = true;
                        _watchers.Add(w);
                    }
                    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                    {
                        _log.LogWarning(ex, "Can't watch {Dir}", root.Directory);
                    }
                }
            }
            _log.LogInformation("Watching {Count} folder(s); {Missing} root folder(s) don't exist yet",
                _watchers.Count, _missingRoots.Count);
        }
    }

    private void OnChange(string gameId, string alias, string rootDir, string fullPath, ExcludeMatcher excludes)
    {
        if (fullPath.EndsWith(".savepoint-tmp", StringComparison.OrdinalIgnoreCase)) return;
        var rel = Path.GetRelativePath(rootDir, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (excludes.IsExcluded(alias, rel)) return;
        MarkDirty(gameId);
    }

    public void MarkDirty(string gameId) => _dirtySince[gameId] = DateTimeOffset.UtcNow;

    /// <summary>Games whose folders have been quiet for at least <paramref name="quiet"/>.
    /// They are cleared from the dirty set.</summary>
    public IReadOnlyList<string> TakeSettled(TimeSpan quiet)
    {
        var settled = new List<string>();
        var cutoff = DateTimeOffset.UtcNow - quiet;
        foreach (var (gameId, last) in _dirtySince)
        {
            if (last <= cutoff && _dirtySince.TryRemove(new KeyValuePair<string, DateTimeOffset>(gameId, last)))
                settled.Add(gameId);
        }
        return settled;
    }

    private void DisposeWatchers()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_lock) DisposeWatchers();
    }
}
