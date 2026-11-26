using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Savepoint.Core.Scanning;

/// <summary>A save root resolved to a folder on this machine.</summary>
public sealed record ResolvedRoot(string Alias, string Directory);

/// <summary>A file stayed locked after retries; the scan is abandoned rather than
/// producing a manifest that's missing a file.</summary>
public sealed class FileLockedException : IOException
{
    public FileLockedException(string path, Exception inner)
        : base($"File is locked: {path}", inner) => FilePath = path;
    public string FilePath { get; }
}

public sealed class ScanOptions
{
    public int LockRetries { get; init; } = 4;
    public TimeSpan LockRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

public sealed class Scanner
{
    private readonly ScanOptions _options;
    private readonly ILogger _log;

    public Scanner(ScanOptions? options = null, ILogger? log = null)
    {
        _options = options ?? new ScanOptions();
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Walk every root and build a manifest. Files whose size and mtime match the
    /// <paramref name="cache"/> entry reuse its hash instead of being re-read.
    /// </summary>
    public Manifest Scan(IEnumerable<ResolvedRoot> roots, ExcludeMatcher excludes, Manifest? cache = null,
        CancellationToken ct = default)
    {
        var entries = new List<ManifestEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Directory)) continue;
            foreach (var file in EnumerateFiles(root.Directory, ct))
            {
                var rel = Path.GetRelativePath(root.Directory, file).Replace(Path.DirectorySeparatorChar, '/');
                if (excludes.IsExcluded(root.Alias, rel)) continue;
                var path = root.Alias + "/" + rel;
                if (!seen.Add(path)) continue;
                var entry = ScanFile(file, path, cache?.Get(path), ct);
                if (entry is not null) entries.Add(entry);
            }
        }
        return new Manifest(entries);
    }

    private ManifestEntry? ScanFile(string file, string path, ManifestEntry? cached, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) return null; // deleted mid-scan
                var size = info.Length;
                var mtime = Time.Normalize(info.LastWriteTimeUtc);
                if (cached is not null && cached.Size == size && cached.Mtime == mtime)
                    return cached with { Path = path };

                string hash;
                using (var fs = Hashing.OpenShared(file))
                {
                    hash = Hashing.Sha256Stream(fs);
                }
                // If the file changed while hashing, go round again.
                info.Refresh();
                if (info.Length != size || Time.Normalize(info.LastWriteTimeUtc) != mtime)
                {
                    if (attempt < _options.LockRetries) { Thread.Sleep(_options.LockRetryDelay); continue; }
                    throw new FileLockedException(file, new IOException("File kept changing while being read"));
                }
                return new ManifestEntry(path, hash, size, mtime);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException ex) when (ex is not FileLockedException)
            {
                if (attempt >= _options.LockRetries) throw new FileLockedException(file, ex);
                _log.LogDebug("Retrying locked file {File}: {Message}", file, ex.Message);
                Thread.Sleep(TimeSpan.FromTicks(_options.LockRetryDelay.Ticks * (attempt + 1)));
            }
            catch (UnauthorizedAccessException ex)
            {
                if (attempt >= _options.LockRetries) throw new FileLockedException(file, ex);
                Thread.Sleep(TimeSpan.FromTicks(_options.LockRetryDelay.Ticks * (attempt + 1)));
            }
        }
    }

    /// <summary>Recursive walk that doesn't follow directory symlinks/junctions (avoids loops)
    /// but does include files that are reparse points, like OneDrive placeholders.</summary>
    private IEnumerable<string> EnumerateFiles(string dir, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = stack.Pop();
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(current, "*", opts);
                subdirs = Directory.GetDirectories(current, "*", opts);
            }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException ex)
            {
                _log.LogWarning("Skipping unreadable folder {Dir}: {Message}", current, ex.Message);
                continue;
            }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files) yield return f;
            foreach (var d in subdirs)
            {
                try
                {
                    if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                }
                catch (IOException) { continue; }
                stack.Push(d);
            }
        }
    }
}
