using Savepoint.Core.Api;

namespace Savepoint.Core.Scanning;

/// <summary>One file: "alias/relative/path" with forward slashes, SHA-256 of the content,
/// size, and last-write time in UTC (microsecond precision, matching the server).</summary>
public sealed record ManifestEntry(string Path, string Hash, long Size, DateTimeOffset Mtime)
{
    public string Alias => Path[..Path.IndexOf('/')];
    public string RelativePath => Path[(Path.IndexOf('/') + 1)..];

    public FileEntryDto ToDto() => new(Path, Hash, Size, Mtime.ToUniversalTime());
    public static ManifestEntry FromDto(FileEntryDto d) => new(d.Path, d.Hash, d.Size, Time.Normalize(d.Mtime));
}

public sealed class Manifest
{
    public static readonly Manifest Empty = new(Array.Empty<ManifestEntry>());

    private readonly Dictionary<string, ManifestEntry> _byPath;

    public Manifest(IEnumerable<ManifestEntry> entries)
    {
        Entries = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        _byPath = Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
    }

    public IReadOnlyList<ManifestEntry> Entries { get; }
    public int Count => Entries.Count;
    public bool IsEmpty => Entries.Count == 0;
    public long TotalSize => Entries.Sum(e => e.Size);
    public DateTimeOffset? LatestMtime => Entries.Count == 0 ? null : Entries.Max(e => e.Mtime);

    public ManifestEntry? Get(string path) => _byPath.TryGetValue(path, out var e) ? e : null;

    /// <summary>Same paths with the same content. mtimes alone don't count as a change,
    /// matching the server's dedupe rule.</summary>
    public bool SameContentAs(Manifest other)
    {
        if (other.Count != Count) return false;
        foreach (var e in Entries)
        {
            var o = other.Get(e.Path);
            if (o is null || o.Hash != e.Hash) return false;
        }
        return true;
    }

    public ManifestDiff DiffFrom(Manifest older) => ManifestDiff.Compute(older, this);

    public List<FileEntryDto> ToDtos() => Entries.Select(e => e.ToDto()).ToList();
    public static Manifest FromDtos(IEnumerable<FileEntryDto>? dtos) =>
        dtos is null ? Empty : new Manifest(dtos.Select(ManifestEntry.FromDto));
}

public sealed record ManifestDiff(
    IReadOnlyList<ManifestEntry> Added,
    IReadOnlyList<ManifestEntry> Changed,
    IReadOnlyList<ManifestEntry> Removed)
{
    public bool IsEmpty => Added.Count == 0 && Changed.Count == 0 && Removed.Count == 0;

    public static ManifestDiff Compute(Manifest from, Manifest to)
    {
        var added = new List<ManifestEntry>();
        var changed = new List<ManifestEntry>();
        var removed = new List<ManifestEntry>();
        foreach (var e in to.Entries)
        {
            var old = from.Get(e.Path);
            if (old is null) added.Add(e);
            else if (old.Hash != e.Hash) changed.Add(e);
        }
        foreach (var e in from.Entries)
        {
            if (to.Get(e.Path) is null) removed.Add(e);
        }
        return new ManifestDiff(added, changed, removed);
    }
}

public static class Time
{
    /// <summary>UTC, truncated to microseconds (the server's precision), so a time that
    /// round-trips through the server compares equal to the local one.</summary>
    public static DateTimeOffset Normalize(DateTimeOffset t)
    {
        var utc = t.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    public static DateTimeOffset Normalize(DateTime utc) =>
        Normalize(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
}
