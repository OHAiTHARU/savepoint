using Savepoint.Core.Api;
using Savepoint.Core.Paths;
using Savepoint.Core.Scanning;
using Savepoint.Core.State;
using Savepoint.Core.Sync;

namespace Savepoint.Core.Tests;

public class PathTemplateTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "sp-paths");
    private readonly DictionaryPathEnvironment _env = new(new Dictionary<string, string>
    {
        ["APPDATA"] = Path.Combine(Base, "Roaming"),
        ["LOCALAPPDATA"] = Path.Combine(Base, "Local"),
        ["LOCALLOW"] = Path.Combine(Base, "LocalLow"),
        ["DOCUMENTS"] = Path.Combine(Base, "OneDrive", "Documents"),
        ["PUBLIC"] = Path.Combine(Base, "Public"),
    });

    [Fact]
    public void ResolvesVariablesAndBackslashes()
    {
        var p = PathTemplate.Resolve(@"%APPDATA%\EldenRing\76561198000000000", _env);
        Assert.Equal(Path.Combine(Base, "Roaming", "EldenRing", "76561198000000000"), p);
    }

    [Fact]
    public void VariablesAreCaseInsensitive() =>
        Assert.Equal(Path.Combine(Base, "Local", "x"), PathTemplate.Resolve("%localappdata%/x", _env));

    [Fact]
    public void DocumentsUsesKnownFolderValue() =>
        Assert.Equal(Path.Combine(Base, "OneDrive", "Documents", "My Games"), PathTemplate.Resolve(@"%DOCUMENTS%\My Games", _env));

    [Fact]
    public void UnknownVariableThrows() =>
        Assert.Throws<PathTemplateException>(() => PathTemplate.Resolve(@"%STEAM%\userdata", _env));

    [Fact]
    public void RelativePathThrows() =>
        Assert.Throws<PathTemplateException>(() => PathTemplate.Resolve(@"saves\slot1", _env));

    [Fact]
    public void AbsolutePathPassesThrough()
    {
        var abs = Path.Combine(Base, "Games", "Saves");
        Assert.Equal(abs, PathTemplate.Resolve(abs, _env));
    }

    [Fact]
    public void ToTemplatePicksMostSpecificVariable()
    {
        Assert.Equal(@"%LOCALLOW%\Team Cherry\Silksong",
            PathTemplate.ToTemplate(Path.Combine(Base, "LocalLow", "Team Cherry", "Silksong"), _env));
        Assert.Equal(@"%PUBLIC%", PathTemplate.ToTemplate(Path.Combine(Base, "Public"), _env));
    }
}

public class ExcludeTests
{
    [Theory]
    [InlineData("**/*.tmp", "a/b/c.tmp", true)]
    [InlineData("**/*.tmp", "c.tmp", true)]
    [InlineData("**/*.tmp", "c.tmp.bak", false)]
    [InlineData("*.log", "deep/dir/x.LOG", true)]
    [InlineData("**/shader_cache/**", "shader_cache/a.bin", true)]
    [InlineData("**/shader_cache/**", "x/shader_cache/y/z.bin", true)]
    [InlineData("**/shader_cache/**", "not_shader_cache/a", false)]
    [InlineData("cache/", "cache/a/b", true)]
    [InlineData("cache/", "other/cache/a", false)]
    [InlineData("saves/backup/*", "backup/x.sav", true)] // alias-prefixed pattern
    [InlineData("slot?.sav", "slot1.sav", true)]
    [InlineData("slot?.sav", "slot10.sav", false)]
    [InlineData("Profile*/settings.ini", "Profile1/settings.ini", true)]
    [InlineData("Profile*/settings.ini", "Profile1/sub/settings.ini", false)]
    public void Matches(string pattern, string path, bool excluded) =>
        Assert.Equal(excluded, new ExcludeMatcher(new[] { pattern }).IsExcluded("saves", path));

    [Fact]
    public void NoPatternsExcludeNothing() => Assert.False(ExcludeMatcher.None.IsExcluded("a", "b"));
}

public class ManifestTests
{
    private static ManifestEntry E(string p, string h, int min = 0) =>
        new(p, h.PadLeft(64, '0'), 1, new DateTimeOffset(2026, 1, 1, 0, min, 0, TimeSpan.Zero));

    [Fact]
    public void SameContentIgnoresMtime()
    {
        var a = new Manifest(new[] { E("s/a", "1", 0), E("s/b", "2", 0) });
        var b = new Manifest(new[] { E("s/b", "2", 5), E("s/a", "1", 9) });
        Assert.True(a.SameContentAs(b));
        Assert.False(a.SameContentAs(new Manifest(new[] { E("s/a", "1") })));
        Assert.False(a.SameContentAs(new Manifest(new[] { E("s/a", "1"), E("s/b", "3") })));
        Assert.True(Manifest.Empty.SameContentAs(new Manifest(Array.Empty<ManifestEntry>())));
    }

    [Fact]
    public void Diff()
    {
        var old = new Manifest(new[] { E("s/keep", "1"), E("s/change", "2"), E("s/gone", "3") });
        var @new = new Manifest(new[] { E("s/keep", "1"), E("s/change", "4"), E("s/new", "5") });
        var d = @new.DiffFrom(old);
        Assert.Equal(new[] { "s/new" }, d.Added.Select(e => e.Path));
        Assert.Equal(new[] { "s/change" }, d.Changed.Select(e => e.Path));
        Assert.Equal(new[] { "s/gone" }, d.Removed.Select(e => e.Path));
    }

    [Fact]
    public void TimeNormalizeTruncatesToMicroseconds()
    {
        var t = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(-4)).AddTicks(1234567);
        var n = Time.Normalize(t);
        Assert.Equal(TimeSpan.Zero, n.Offset);
        Assert.Equal(0, n.Ticks % 10);
        Assert.Equal(16, n.Hour);
    }
}

public class ScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-scan-" + Guid.NewGuid().ToString("N"));

    public ScannerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Write(string rel, string content)
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void ScansRecursivelyWithExcludesAndForwardSlashes()
    {
        Write("slot1.sav", "a");
        Write("sub/slot2.sav", "bb");
        Write("sub/temp.tmp", "x");
        Write("shader_cache/s.bin", "x");
        var m = new Scanner().Scan(new[] { new ResolvedRoot("saves", _dir) },
            new ExcludeMatcher(new[] { "**/*.tmp", "**/shader_cache/**" }));
        Assert.Equal(new[] { "saves/slot1.sav", "saves/sub/slot2.sav" }, m.Entries.Select(e => e.Path));
        Assert.Equal(Hashing.Sha256Bytes("bb"u8), m.Get("saves/sub/slot2.sav")!.Hash);
        Assert.Equal(2, m.Get("saves/sub/slot2.sav")!.Size);
    }

    [Fact]
    public void MissingRootIsEmpty() =>
        Assert.True(new Scanner().Scan(new[] { new ResolvedRoot("s", Path.Combine(_dir, "nope")) }, ExcludeMatcher.None).IsEmpty);

    [Fact]
    public void ReusesCachedHashWhenSizeAndMtimeMatch()
    {
        var p = Write("a.sav", "one");
        var first = new Scanner().Scan(new[] { new ResolvedRoot("s", _dir) }, ExcludeMatcher.None);
        // Same size and mtime, different bytes: the cache wins (that's the point of it).
        var mtime = File.GetLastWriteTimeUtc(p);
        File.WriteAllText(p, "two");
        File.SetLastWriteTimeUtc(p, mtime);
        var second = new Scanner().Scan(new[] { new ResolvedRoot("s", _dir) }, ExcludeMatcher.None, first);
        Assert.Equal(first.Entries[0].Hash, second.Entries[0].Hash);
        // A changed mtime forces a re-hash.
        File.SetLastWriteTimeUtc(p, mtime.AddSeconds(1));
        var third = new Scanner().Scan(new[] { new ResolvedRoot("s", _dir) }, ExcludeMatcher.None, first);
        Assert.Equal(Hashing.Sha256Bytes("two"u8), third.Entries[0].Hash);
    }
}

public class DecisionTableTests
{
    // The six L/S/H combinations from DESIGN §6 (with L≠S,H≠S split by whether L matches H),
    // each in both sync modes.
    [Theory]
    [InlineData(false, false, false, true, PullAction.Nothing)]
    [InlineData(false, false, false, false, PullAction.Nothing)]
    [InlineData(false, true, false, true, PullAction.RestoreHead)]
    [InlineData(false, true, false, false, PullAction.OfferRestore)]
    [InlineData(true, false, false, true, PullAction.PushOffline)]
    [InlineData(true, false, false, false, PullAction.PushOffline)]
    [InlineData(true, true, true, true, PullAction.AdoptHead)]
    [InlineData(true, true, true, false, PullAction.AdoptHead)]
    [InlineData(true, true, false, true, PullAction.Conflict)]
    [InlineData(true, true, false, false, PullAction.Conflict)]
    public void DecidePull(bool localChanged, bool serverMoved, bool localMatchesHead, bool auto, PullAction expected) =>
        Assert.Equal(expected, SyncDecider.DecidePull(localChanged, serverMoved, localMatchesHead, auto));

    private static readonly DateTimeOffset T = new(2026, 11, 20, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewerSideWins()
    {
        Assert.Equal(ConflictWinner.Local, SyncDecider.DecideConflict(T.AddMinutes(1), T, TimeSpan.Zero));
        Assert.Equal(ConflictWinner.Server, SyncDecider.DecideConflict(T, T.AddMinutes(1), TimeSpan.Zero));
        Assert.Equal(ConflictWinner.Server, SyncDecider.DecideConflict(T, T, TimeSpan.Zero)); // tie
    }

    [Fact]
    public void TimeZonesDoNotMatter()
    {
        // 18:01 in UTC-4 is 22:01 UTC: newer than a 22:00 UTC head.
        var local = new DateTimeOffset(2026, 11, 20, 18, 1, 0, TimeSpan.FromHours(-4));
        Assert.Equal(ConflictWinner.Local, SyncDecider.DecideConflict(local, T, TimeSpan.Zero));
    }

    [Fact]
    public void ClockOffsetIsCorrected()
    {
        // Local file looks 2 minutes newer, but this PC's clock runs 5 minutes fast.
        Assert.Equal(ConflictWinner.Server, SyncDecider.DecideConflict(T.AddMinutes(2), T, TimeSpan.FromMinutes(5)));
        // And a slow clock can make a genuinely newer local save look older.
        Assert.Equal(ConflictWinner.Local, SyncDecider.DecideConflict(T.AddMinutes(-2), T, TimeSpan.FromMinutes(-5)));
    }

    [Fact]
    public void MissingTimes()
    {
        Assert.Equal(ConflictWinner.Server, SyncDecider.DecideConflict(null, T, TimeSpan.Zero));
        Assert.Equal(ConflictWinner.Local, SyncDecider.DecideConflict(T, null, TimeSpan.Zero));
    }
}

public class StateStoreTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "sp-state-" + Guid.NewGuid().ToString("N") + ".db");
    public void Dispose() { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_db)!, Path.GetFileName(_db) + "*")) File.Delete(f); }

    [Fact]
    public void ConfigRoundTripKeepsKeyOutOfTheClear()
    {
        using (var s = new StateStore(_db, new PlainProtector()))
        {
            s.SaveConfig(new AgentConfig { ServerUrl = "https://sp.example", ApiKey = "sp_secret", QuietMinutes = 5 });
            Assert.DoesNotContain("sp_secret", s.GetValue("agent_config"));
        }
        using (var s = new StateStore(_db, new PlainProtector()))
        {
            var cfg = s.LoadConfig();
            Assert.Equal("https://sp.example", cfg.ServerUrl);
            Assert.Equal("sp_secret", cfg.ApiKey);
            Assert.Equal(5, cfg.QuietMinutes);
            Assert.True(cfg.IsConfigured);
        }
    }

    [Fact]
    public void PendingOpsAreDedupedPerGame()
    {
        using var s = new StateStore(_db, new PlainProtector());
        s.Enqueue("g1", "push", "{\"type\":\"checkpoint\"}");
        s.Enqueue("g1", "push", "{\"type\":\"session\"}");
        s.Enqueue("g2", "push");
        var ops = s.GetPendingOps();
        Assert.Equal(2, ops.Count);
        Assert.Contains("session", ops[0].PayloadJson);
        s.FailOp(ops[0].Id, "offline", DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(1, s.GetPendingOps()[0].Attempts);
        s.CompleteOp(ops[0].Id);
        Assert.Single(s.GetPendingOps());
    }

    [Fact]
    public void GameStateAndGamesCache()
    {
        using var s = new StateStore(_db, new PlainProtector());
        var m = new Manifest(new[] { new ManifestEntry("s/a", new string('a', 64), 3, DateTimeOffset.UnixEpoch.AddTicks(1234560)) });
        s.SetSynced("g", "v1", m);
        s.SetConflict("g", new PendingConflict("v2", "v1", DateTimeOffset.UtcNow));
        var st = s.GetGameState("g");
        Assert.Equal("v1", st.LastSyncedVersionId);
        Assert.True(st.LastSyncedManifest.SameContentAs(m));
        Assert.Equal(m.Entries[0].Mtime, st.LastSyncedManifest.Entries[0].Mtime);
        Assert.Equal("v2", st.Conflict!.LocalVersionId);
        Assert.Single(s.GetConflicts());

        s.SaveGames(new[] { new Game { Id = "g", Name = "Elden Ring", Roots = { new GameRoot("saves", "%APPDATA%\\ER") } } });
        Assert.Equal("Elden Ring", s.LoadGames()[0].Name);
    }
}
