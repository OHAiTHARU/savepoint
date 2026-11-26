using System.IO.Compression;
using Savepoint.Core.Api;
using Savepoint.Core.Sync;

namespace Savepoint.Core.Tests;

/// <summary>Two simulated PCs syncing one game through the in-memory server.</summary>
public class SyncEngineTests : IDisposable
{
    private readonly FakeServer _server = new();
    private readonly TestMachine _pc;
    private readonly TestMachine _laptop;
    private Game _game;

    private static readonly DateTime T0 = new(2026, 11, 20, 18, 0, 0, DateTimeKind.Utc);

    public SyncEngineTests()
    {
        _pc = new TestMachine(new FakeApi(_server, "pc"), "pc");
        _laptop = new TestMachine(new FakeApi(_server, "laptop"), "laptop");
        _game = _server.AddGame("Elden Ring", SyncModes.Auto, new GameRoot("saves", @"%APPDATA%\Game"));
    }

    public void Dispose()
    {
        _pc.Dispose();
        _laptop.Dispose();
    }

    private Game Manual() => _game = _game with { SyncMode = SyncModes.Manual };

    private Game WithExcludes(params string[] patterns) => _game = _game with { Excludes = patterns.ToList() };

    // ------------------------------------------------------------------ basic flow

    [Fact]
    public async Task PushOnPcThenPullOnLaptopRestores()
    {
        _pc.Write("ER0000.sl2", "level 50", T0);
        _pc.Write("sub/profile.dat", "profile", T0);
        var push = await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.Pushed, push.Outcome);
        Assert.Equal(push.Version!.Id, _server.HeadOf(_game.Id));

        var pull = await _laptop.Engine.PullCheckAsync(_game);
        Assert.Equal(SyncOutcome.Restored, pull.Outcome);
        Assert.Equal("level 50", _laptop.Read("ER0000.sl2"));
        Assert.Equal("profile", _laptop.Read("sub/profile.dat"));
        // mtime carried over from the manifest.
        Assert.Equal(T0, File.GetLastWriteTimeUtc(Path.Combine(_laptop.SaveDir(), "ER0000.sl2")));
        Assert.Contains(_laptop.Notifications, n => n.Kind == NotificationKind.Restored);

        // Nothing more to do on either side.
        Assert.Equal(SyncOutcome.NoChange, (await _laptop.Engine.PullCheckAsync(_game)).Outcome);
        Assert.Equal(SyncOutcome.NoChange, (await _pc.Engine.PushAsync(_game, VersionTypes.Checkpoint)).Outcome);
    }

    [Fact]
    public async Task PushWithNoChangesDoesNothing()
    {
        _pc.Write("a.sav", "x", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        var again = await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.NoChange, again.Outcome);
        // Touching a file without changing it isn't a change either.
        File.SetLastWriteTimeUtc(Path.Combine(_pc.SaveDir(), "a.sav"), T0.AddHours(1));
        Assert.Equal(SyncOutcome.NoChange, (await _pc.Engine.PushAsync(_game, VersionTypes.Checkpoint)).Outcome);
        Assert.Single(_server.VersionsOf(_game.Id));
    }

    [Fact]
    public async Task OnlyMissingBlobsAreUploaded()
    {
        _pc.Write("big.sav", new string('x', 10000), T0);
        _pc.Write("small.sav", "1", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(2, _server.BlobUploads);
        _pc.Write("small.sav", "2", T0.AddMinutes(1));
        await _pc.Engine.PushAsync(_game, VersionTypes.Checkpoint);
        Assert.Equal(3, _server.BlobUploads);
    }

    [Fact]
    public async Task ManualModeOffersRestoreInsteadOfApplying()
    {
        Manual();
        _pc.Write("a.sav", "pc", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        var pull = await _laptop.Engine.PullCheckAsync(_game);
        Assert.Equal(SyncOutcome.RestoreOffered, pull.Outcome);
        Assert.Null(_laptop.Read("a.sav"));
        Assert.Contains(_laptop.Notifications, n => n.Kind == NotificationKind.RestoreAvailable);

        var restore = await _laptop.Engine.RestoreLatestAsync(_game);
        Assert.Equal(SyncOutcome.Restored, restore.Outcome);
        Assert.Equal("pc", _laptop.Read("a.sav"));
    }

    [Fact]
    public async Task PullIsSkippedWhileGameRuns()
    {
        _pc.Write("a.sav", "pc", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        _laptop.RunningGames.Add(_game.Id);
        Assert.Equal(SyncOutcome.SkippedGameRunning, (await _laptop.Engine.PullCheckAsync(_game)).Outcome);
        Assert.Null(_laptop.Read("a.sav"));
        _laptop.RunningGames.Clear();
        Assert.Equal(SyncOutcome.Restored, (await _laptop.Engine.PullCheckAsync(_game)).Outcome);
    }

    [Fact]
    public async Task LocalSavesMatchingHeadAreAdopted()
    {
        _pc.Write("a.sav", "same", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        _laptop.Write("a.sav", "same", T0.AddDays(-1)); // e.g. copied by hand
        var pull = await _laptop.Engine.PullCheckAsync(_game);
        Assert.Equal(SyncOutcome.AdoptedHead, pull.Outcome);
        Assert.Single(_server.VersionsOf(_game.Id));
    }

    // ------------------------------------------------------------------ offline

    [Fact]
    public async Task OfflinePushIsQueuedAndFlushedLater()
    {
        _pc.Write("a.sav", "v1", T0);
        _server.Offline = true;
        var push = await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.Queued, push.Outcome);
        Assert.True(_pc.Engine.HasPendingOps);

        // Still offline: flush backs off.
        Assert.Equal(0, await _pc.Engine.FlushPendingAsync(new[] { _game }));
        Assert.True(_pc.Engine.HasPendingOps);

        _server.Offline = false;
        foreach (var op in _pc.State.GetPendingOps()) _pc.State.FailOp(op.Id, "x", DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Equal(1, await _pc.Engine.FlushPendingAsync(new[] { _game }));
        Assert.False(_pc.Engine.HasPendingOps);
        var head = _server.Versions[_server.HeadOf(_game.Id)!];
        Assert.Equal(VersionTypes.Session, head.Type);
    }

    [Fact]
    public async Task ChangesMadeOfflineArePushedAtStartupAsOfflineType()
    {
        _pc.Write("a.sav", "v1", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        // Played while the agent wasn't running.
        _pc.Write("a.sav", "v2", T0.AddHours(1));
        var pull = await _pc.Engine.PullCheckAsync(_game);
        Assert.Equal(SyncOutcome.Pushed, pull.Outcome);
        Assert.Equal(VersionTypes.Offline, pull.Version!.Type);
    }

    [Fact]
    public async Task PullWhileOfflineQueuesLocalChanges()
    {
        _pc.Write("a.sav", "v1", T0);
        _server.Offline = true;
        Assert.Equal(SyncOutcome.Offline, (await _pc.Engine.PullCheckAsync(_game)).Outcome);
        Assert.True(_pc.Engine.HasPendingOps);
    }

    // ------------------------------------------------------------------ conflicts

    private async Task SetUpConflict(DateTime pcTime, DateTime laptopTime)
    {
        _pc.Write("a.sav", "base", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        await _laptop.Engine.PullCheckAsync(_game);
        // Both play from the same base; the PC syncs first.
        _pc.Write("a.sav", "pc progress", pcTime);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        _laptop.Write("a.sav", "laptop progress", laptopTime);
    }

    [Fact]
    public async Task AutoConflictLocalNewerIsPromoted()
    {
        await SetUpConflict(pcTime: T0.AddHours(1), laptopTime: T0.AddHours(2));
        var result = await _laptop.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.ConflictResolvedLocal, result.Outcome);
        var head = _server.Versions[_server.HeadOf(_game.Id)!];
        Assert.Equal(VersionTypes.Promoted, head.Type);
        Assert.Equal("laptop progress", _laptop.Read("a.sav"));
        // The PC's version is still on the server.
        Assert.Contains(_server.VersionsOf(_game.Id), v => v.MachineId == "pc" && v.Type == VersionTypes.Session && v.ParentVersionId != null);

        // PC picks up the laptop's save on its next pull.
        Assert.Equal(SyncOutcome.Restored, (await _pc.Engine.PullCheckAsync(_game)).Outcome);
        Assert.Equal("laptop progress", _pc.Read("a.sav"));
    }

    [Fact]
    public async Task AutoConflictServerNewerRestoresAndKeepsLocalAsVersion()
    {
        await SetUpConflict(pcTime: T0.AddHours(2), laptopTime: T0.AddHours(1));
        var result = await _laptop.Engine.PullCheckAsync(_game);
        Assert.Equal(SyncOutcome.ConflictResolvedServer, result.Outcome);
        Assert.Equal("pc progress", _laptop.Read("a.sav"));
        var kept = _server.VersionsOf(_game.Id).Single(v => v.MachineId == "laptop");
        Assert.False(kept.Id == _server.HeadOf(_game.Id));
        Assert.Equal(Hashing.Sha256Bytes("laptop progress"u8), kept.Files!.Single().Hash);
        // No duplicate pre_restore upload: the conflict upload already preserved it.
        Assert.DoesNotContain(_server.VersionsOf(_game.Id), v => v.Type == VersionTypes.PreRestore);
        Assert.Contains(_laptop.Notifications, n => n.Kind == NotificationKind.ConflictResolved);
    }

    [Fact]
    public async Task AutoConflictHonoursClockOffset()
    {
        // The laptop's file looks 2 minutes newer, but its clock runs 10 minutes fast.
        await SetUpConflict(pcTime: T0.AddHours(1), laptopTime: T0.AddHours(1).AddMinutes(2));
        ((FakeApi)_laptop.Api).ClockOffset = TimeSpan.FromMinutes(10);
        var result = await _laptop.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.ConflictResolvedServer, result.Outcome);
        Assert.Equal("pc progress", _laptop.Read("a.sav"));
    }

    [Fact]
    public async Task AutoConflictWhileGameRunningIsFinishedAfterExit()
    {
        await SetUpConflict(pcTime: T0.AddHours(2), laptopTime: T0.AddHours(1));
        _laptop.RunningGames.Add(_game.Id);
        var during = await _laptop.Engine.PushAsync(_game, VersionTypes.Checkpoint);
        Assert.Equal(SyncOutcome.SkippedGameRunning, during.Outcome);
        Assert.Equal("laptop progress", _laptop.Read("a.sav"));
        // Another checkpoint while still running must not upload a duplicate.
        await _laptop.Engine.PushAsync(_game, VersionTypes.Checkpoint);
        Assert.Single(_server.VersionsOf(_game.Id), v => v.MachineId == "laptop");

        _laptop.RunningGames.Clear();
        var after = await _laptop.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.ConflictResolvedServer, after.Outcome);
        Assert.Equal("pc progress", _laptop.Read("a.sav"));
        Assert.Null(_laptop.State.GetGameState(_game.Id).Conflict);
    }

    [Fact]
    public async Task ManualConflictWaitsThenKeepLocal()
    {
        Manual();
        await SetUpConflict(pcTime: T0.AddHours(2), laptopTime: T0.AddHours(1));
        var result = await _laptop.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.ConflictPending, result.Outcome);
        Assert.Contains(_laptop.Notifications, n => n.Kind == NotificationKind.ConflictNeedsDecision);
        Assert.Equal("laptop progress", _laptop.Read("a.sav"));
        // Further pushes wait for the decision.
        Assert.Equal(SyncOutcome.ConflictPending, (await _laptop.Engine.PushAsync(_game, VersionTypes.Checkpoint)).Outcome);

        var resolved = await _laptop.Engine.ResolveConflictAsync(_game, ConflictChoice.KeepLocal);
        Assert.Equal(SyncOutcome.ConflictResolvedLocal, resolved.Outcome);
        var head = _server.Versions[_server.HeadOf(_game.Id)!];
        Assert.Equal(Hashing.Sha256Bytes("laptop progress"u8), head.Files!.Single().Hash);
        Assert.Equal(SyncOutcome.NoChange, (await _laptop.Engine.PushAsync(_game, VersionTypes.Checkpoint)).Outcome);
    }

    [Fact]
    public async Task ManualConflictTakeServer()
    {
        Manual();
        await SetUpConflict(pcTime: T0.AddHours(1), laptopTime: T0.AddHours(2));
        await _laptop.Engine.PullCheckAsync(_game);
        var resolved = await _laptop.Engine.ResolveConflictAsync(_game, ConflictChoice.TakeServer);
        Assert.Equal(SyncOutcome.ConflictResolvedServer, resolved.Outcome);
        Assert.Equal("pc progress", _laptop.Read("a.sav"));
        Assert.DoesNotContain(_server.VersionsOf(_game.Id), v => v.Type == VersionTypes.PreRestore);
    }

    [Fact]
    public async Task ManualConflictDecidedInWebUi()
    {
        Manual();
        await SetUpConflict(pcTime: T0.AddHours(1), laptopTime: T0.AddHours(2));
        await _laptop.Engine.PullCheckAsync(_game);
        var laptopVersion = _laptop.State.GetGameState(_game.Id).Conflict!.LocalVersionId;
        // User promotes the laptop's version from their phone.
        _server.Promote(laptopVersion, null);
        var pull = await _laptop.Engine.PullCheckAsync(_game);
        Assert.Equal(SyncOutcome.Restored, pull.Outcome);
        Assert.Equal("laptop progress", _laptop.Read("a.sav"));
        Assert.Null(_laptop.State.GetGameState(_game.Id).Conflict);
    }

    [Fact]
    public async Task HeadMovedBetweenCheckAndUploadBecomesConflict()
    {
        _pc.Write("a.sav", "base", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        await _laptop.Engine.PullCheckAsync(_game);
        _laptop.Write("a.sav", "laptop", T0.AddHours(2));
        _pc.Write("a.sav", "pc", T0.AddHours(1));
        // The PC's push lands after the laptop checked /heads but before it created its version.
        ((FakeApi)_laptop.Api).BeforeNextCreate = () => _pc.Engine.PushAsync(_game, VersionTypes.Session).GetAwaiter().GetResult();
        var r = await _laptop.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.ConflictResolvedLocal, r.Outcome);
        Assert.Equal("laptop", _laptop.Read("a.sav"));
        Assert.Contains(_server.VersionsOf(_game.Id), v => v.MachineId == "pc" && v.Files!.Single().Hash == Hashing.Sha256Bytes("pc"u8));
    }

    [Fact]
    public async Task FirstPushFromSecondMachineIsAConflict()
    {
        // Laptop has never synced but has a save; PC already pushed one.
        _pc.Write("a.sav", "pc", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        _laptop.Write("a.sav", "laptop", T0.AddHours(1));
        var r = await _laptop.Engine.PushAsync(_game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.ConflictResolvedLocal, r.Outcome);
        Assert.Equal(2, _server.VersionsOf(_game.Id).Count(v => v.Type != VersionTypes.Promoted));
    }

    // ------------------------------------------------------------------ restore mirroring

    [Fact]
    public async Task RestoreMirrorsDeletesAndLeavesExcludedFilesAlone()
    {
        WithExcludes("**/*.log", "cache/");
        _pc.Write("keep.sav", "k", T0);
        _pc.Write("sub/new.sav", "n", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);

        _laptop.Write("keep.sav", "k", T0);               // identical: left in place
        _laptop.Write("stale.sav", "old", T0);            // not in V: deleted
        _laptop.Write("old/dir/stale2.sav", "old", T0);   // deleted, empty dirs removed
        _laptop.Write("debug.log", "log", T0);            // excluded: untouched
        _laptop.Write("cache/shader.bin", "c", T0);       // excluded: untouched
        _laptop.State.SetSynced(_game.Id, null, Savepoint.Core.Scanning.Manifest.Empty);

        var r = await _laptop.Engine.RestoreLatestAsync(_game);
        Assert.Equal(SyncOutcome.Restored, r.Outcome);
        Assert.Equal("k", _laptop.Read("keep.sav"));
        Assert.Equal("n", _laptop.Read("sub/new.sav"));
        Assert.Null(_laptop.Read("stale.sav"));
        Assert.Null(_laptop.Read("old/dir/stale2.sav"));
        Assert.False(Directory.Exists(Path.Combine(_laptop.SaveDir(), "old")));
        Assert.Equal("log", _laptop.Read("debug.log"));
        Assert.Equal("c", _laptop.Read("cache/shader.bin"));

        // Local changes were preserved before being overwritten: on the server and as a zip.
        var pre = Assert.Single(_server.VersionsOf(_game.Id), v => v.Type == VersionTypes.PreRestore);
        Assert.Contains(pre.Files!, f => f.Path == "saves/stale.sav");
        var zip = Assert.Single(_laptop.State.GetSnapshots(_game.Id));
        using var archive = ZipFile.OpenRead(zip.Path);
        Assert.Contains(archive.Entries, e => e.FullName == "saves/stale.sav");
        Assert.DoesNotContain(archive.Entries, e => e.FullName.EndsWith(".log"));
    }

    [Fact]
    public async Task RestoreHandlesFilesMovedBetweenPaths()
    {
        _pc.Write("slot1.sav", "AAA", T0);
        _pc.Write("slot2.sav", "BBB", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        await _laptop.Engine.PullCheckAsync(_game);
        // Swap the slots on the PC.
        _pc.Write("slot1.sav", "BBB", T0.AddMinutes(1));
        _pc.Write("slot2.sav", "AAA", T0.AddMinutes(1));
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        var before = _server.BlobUploads;
        await _laptop.Engine.PullCheckAsync(_game);
        Assert.Equal("BBB", _laptop.Read("slot1.sav"));
        Assert.Equal("AAA", _laptop.Read("slot2.sav"));
        Assert.Equal(before, _server.BlobUploads);
    }

    [Fact]
    public async Task OnlyThreeSnapshotsAreKept()
    {
        _pc.Write("a.sav", "0", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        for (var i = 1; i <= 5; i++)
        {
            _laptop.Write("a.sav", $"local {i}", T0.AddMinutes(i));
            await _laptop.Engine.RestoreLatestAsync(_game);
        }
        var snaps = _laptop.State.GetSnapshots(_game.Id);
        Assert.Equal(3, snaps.Count);
        Assert.All(snaps, s => Assert.True(File.Exists(s.Path)));
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_laptop.DataDir, "snapshots", _game.Id)).Length);
    }

    [Fact]
    public async Task PerMachinePathOverrideIsUsed()
    {
        _pc.Write("a.sav", "pc", T0);
        await _pc.Engine.PushAsync(_game, VersionTypes.Session);
        var laptopGame = _game with
        {
            Roots = new() { new GameRoot("saves", @"%APPDATA%\Game", @"%APPDATA%\Elsewhere") },
        };
        await _laptop.Engine.PullCheckAsync(laptopGame);
        Assert.Equal("pc", _laptop.Read("a.sav", sub: "Elsewhere"));
        Assert.Null(_laptop.Read("a.sav"));
    }

    [Fact]
    public async Task UnknownVariableFailsWithNotification()
    {
        var bad = _game with { Roots = new() { new GameRoot("saves", @"%NOPE%\x") } };
        var r = await _pc.Engine.PushAsync(bad, VersionTypes.Manual);
        Assert.Equal(SyncOutcome.Failed, r.Outcome);
        Assert.Contains(_pc.Notifications, n => n.Kind == NotificationKind.Error);
    }
}
