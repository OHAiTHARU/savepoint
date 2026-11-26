using Savepoint.Core.Api;
using Savepoint.Core.Sync;

namespace Savepoint.Core.Tests;

/// <summary>
/// Two engines against a real savepoint-server (DESIGN §9 "End to end"). Skipped unless
/// SAVEPOINT_E2E_URL, SAVEPOINT_E2E_KEY_A and SAVEPOINT_E2E_KEY_B are set, e.g.:
///   docker run -p 8080:8080 -e SAVEPOINT_ADMIN_PASSWORD=… savepoint-server
/// then create two machine keys in the web UI.
/// </summary>
public class EndToEndTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("SAVEPOINT_E2E_URL");
    private static readonly string? KeyA = Environment.GetEnvironmentVariable("SAVEPOINT_E2E_KEY_A");
    private static readonly string? KeyB = Environment.GetEnvironmentVariable("SAVEPOINT_E2E_KEY_B");

    private static bool Enabled => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(KeyA) && !string.IsNullOrEmpty(KeyB);

    [SkippableFact]
    public async Task TwoMachinesSyncThroughTheRealServer()
    {
        Skip.IfNot(Enabled, "SAVEPOINT_E2E_* not set");
        using var apiA = new SavepointApi(Url!, KeyA!);
        using var apiB = new SavepointApi(Url!, KeyB!);
        using var pc = new TestMachine(apiA, "pc");
        using var laptop = new TestMachine(apiB, "laptop");

        var meA = await apiA.GetMeAsync();
        await apiB.GetMeAsync();
        Assert.True(Math.Abs(apiA.ClockOffset.TotalSeconds) < 30, $"clock offset {apiA.ClockOffset}");

        var game = await apiA.CreateGameAsync(new GameInput
        {
            Name = "E2E " + Guid.NewGuid().ToString("N")[..8],
            Roots = new() { new RootInput("saves", @"%APPDATA%\Game") },
            ExeNames = new() { "e2e.exe" },
            Excludes = new() { "**/*.tmp" },
            SyncMode = SyncModes.Auto,
        });
        // Each machine sees the game with its own resolved roots.
        game = (await apiB.GetGamesAsync()).Single(g => g.Id == game.Id);

        var t0 = DateTime.UtcNow.AddHours(-3);
        pc.Write("ER0000.sl2", new string('s', 300_000), t0);
        pc.Write("nested/profile.dat", "profile", t0);
        pc.Write("junk.tmp", "excluded", t0);
        var push = await pc.Engine.PushAsync(game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.Pushed, push.Outcome);
        Assert.Equal(meA.Id, push.Version!.MachineId);
        Assert.Equal(2, push.Version.FileCount);

        var pull = await laptop.Engine.PullCheckAsync(game);
        Assert.Equal(SyncOutcome.Restored, pull.Outcome);
        Assert.Equal("profile", laptop.Read("nested/profile.dat"));
        Assert.Null(laptop.Read("junk.tmp"));

        // Laptop plays and saves; PC picks it up.
        laptop.Write("ER0000.sl2", new string('t', 300_001), t0.AddHours(1));
        Assert.Equal(SyncOutcome.Pushed, (await laptop.Engine.PushAsync(game, VersionTypes.Checkpoint)).Outcome);
        Assert.Equal(SyncOutcome.Restored, (await pc.Engine.PullCheckAsync(game)).Outcome);
        Assert.Equal(300_001, new FileInfo(Path.Combine(pc.SaveDir(), "ER0000.sl2")).Length);

        // Both change it: a real 409/conflict round trip through the server.
        pc.Write("ER0000.sl2", "pc wins", t0.AddHours(3));
        laptop.Write("ER0000.sl2", "laptop loses", t0.AddHours(2));
        Assert.Equal(SyncOutcome.Pushed, (await pc.Engine.PushAsync(game, VersionTypes.Session)).Outcome);
        var conflict = await laptop.Engine.PushAsync(game, VersionTypes.Session);
        Assert.Equal(SyncOutcome.ConflictResolvedServer, conflict.Outcome);
        Assert.Equal("pc wins", laptop.Read("ER0000.sl2"));

        // Second heads poll with nothing changed is served from the ETag cache.
        var heads1 = await apiB.GetHeadsAsync();
        var heads2 = await apiB.GetHeadsAsync();
        Assert.Same(heads1, heads2);
    }
}
