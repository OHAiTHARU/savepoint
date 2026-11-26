using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Savepoint.Core.Sync;
using Savepoint.Playnite;

namespace Savepoint.Playnite.Tests;

/// <summary>A stand-in for the agent's pipe that answers with canned JSON lines.</summary>
internal sealed class FakeAgent : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    public string PipeName { get; } = "sp-test-" + Guid.NewGuid().ToString("N")[..12];
    public List<JsonObject> Requests { get; } = new();

    public FakeAgent(Func<JsonObject, string?> respond, TimeSpan? delay = null)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try { await pipe.WaitForConnectionAsync(_cts.Token); }
                catch (OperationCanceledException) { pipe.Dispose(); break; }
                _ = Task.Run(async () =>
                {
                    await using (pipe)
                    {
                        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                        var line = await reader.ReadLineAsync();
                        if (line is null) return;
                        var req = (JsonObject)JsonNode.Parse(line)!;
                        lock (Requests) Requests.Add(req);
                        if (delay is not null) await Task.Delay(delay.Value);
                        var resp = respond(req);
                        if (resp is not null)
                        {
                            try { await writer.WriteLineAsync(resp); } catch (IOException) { }
                        }
                    }
                });
            }
        });
    }

    public void Dispose() => _cts.Cancel();
}

public class AgentClientTests
{
    [Fact]
    public void PullSendsGameIdAndParsesResponse()
    {
        // Exactly what the agent's IpcServer writes (System.Text.Json escapes non-ASCII).
        using var agent = new FakeAgent(_ =>
            "{\"ok\":true,\"outcome\":\"restored\",\"message\":\"Restored latest save\\u2026\",\"version_id\":\"v-1\"}");
        var r = new AgentClient(agent.PipeName).Pull("game-1", TimeSpan.FromSeconds(5));
        Assert.True(r.Ok);
        Assert.Equal("restored", r.Outcome);
        Assert.Equal("Restored latest save…", r.Message);
        Assert.Equal("v-1", r.VersionId);
        var req = Assert.Single(agent.Requests);
        Assert.Equal("pull", (string?)req["cmd"]);
        Assert.Equal("game-1", (string?)req["game_id"]);
        Assert.Null(req["type"]); // omitted, not null
    }

    [Fact]
    public void PushResolveAndRestoreSendTheRightFields()
    {
        using var agent = new FakeAgent(_ => "{\"ok\":true,\"outcome\":\"pushed\",\"message\":\"ok\",\"version_id\":null}");
        var client = new AgentClient(agent.PipeName);
        client.Push("g", "session", TimeSpan.FromSeconds(5));
        client.Resolve("g", "keep_local", TimeSpan.FromSeconds(5));
        client.Restore("g", TimeSpan.FromSeconds(5));
        var reqs = agent.Requests.OrderBy(r => (string?)r["cmd"]).ToList();
        Assert.Equal(new[] { "push", "resolve", "restore" }, reqs.Select(r => (string?)r["cmd"]));
        Assert.Equal("session", (string?)reqs[0]["type"]);
        Assert.Equal("keep_local", (string?)reqs[1]["choice"]);
    }

    [Fact]
    public void ListGames()
    {
        using var agent = new FakeAgent(_ =>
            "{\"ok\":true,\"games\":[{\"id\":\"a\",\"name\":\"Elden Ring\",\"exe_names\":[\"eldenring.exe\"],\"sync_mode\":\"auto\"}," +
            "{\"id\":\"b\",\"name\":\"Hades II\",\"exe_names\":[],\"sync_mode\":\"manual\"}]}");
        var games = new AgentClient(agent.PipeName).ListGames(TimeSpan.FromSeconds(5));
        Assert.Equal(2, games.Count);
        Assert.Equal("eldenring.exe", games[0].ExeNames.Single());
        Assert.Equal("manual", games[1].SyncMode);
    }

    [Fact]
    public void NoAgentMeansUnavailable()
    {
        var client = new AgentClient("sp-nobody-" + Guid.NewGuid().ToString("N")[..8], connectTimeoutMs: 200);
        Assert.Throws<AgentUnavailableException>(() => client.Pull("g", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void SlowAgentTimesOut()
    {
        using var agent = new FakeAgent(_ => "{\"ok\":true}", delay: TimeSpan.FromSeconds(5));
        var ex = Assert.Throws<AgentUnavailableException>(() =>
            new AgentClient(agent.PipeName).Pull("g", TimeSpan.FromMilliseconds(300)));
        Assert.Contains("in time", ex.Message);
    }

    [Fact]
    public void AgentHangingUpIsUnavailable()
    {
        using var agent = new FakeAgent(_ => null);
        Assert.Throws<AgentUnavailableException>(() => new AgentClient(agent.PipeName).Pull("g", TimeSpan.FromSeconds(5)));
    }
}

public class LaunchGateTests
{
    private static AgentResponse R(string outcome, bool ok = true, string message = "m") =>
        new() { Ok = ok, Outcome = outcome, Message = message };

    [Theory]
    [InlineData("no_change", LaunchAction.Proceed)]
    [InlineData("adopted_head", LaunchAction.Proceed)]
    [InlineData("pushed", LaunchAction.Proceed)]
    [InlineData("restored", LaunchAction.ProceedWithNotice)]
    [InlineData("conflict_resolved_server", LaunchAction.ProceedWithNotice)]
    [InlineData("conflict_resolved_local", LaunchAction.ProceedWithNotice)]
    [InlineData("restore_offered", LaunchAction.AskRestore)]
    [InlineData("conflict_pending", LaunchAction.AskConflict)]
    [InlineData("offline", LaunchAction.Unsynced)]
    [InlineData("queued", LaunchAction.Unsynced)]
    [InlineData("busy", LaunchAction.Unsynced)]
    [InlineData("failed", LaunchAction.Unsynced)]
    public void Outcomes(string outcome, LaunchAction expected) =>
        Assert.Equal(expected, LaunchGate.Decide(R(outcome), "Elden Ring").Action);

    [Fact]
    public void UnknownGameAsksToRelink()
    {
        var d = LaunchGate.Decide(R("failed", ok: false, message: "Unknown game"), "Elden Ring");
        Assert.Equal(LaunchAction.Unsynced, d.Action);
        Assert.Contains("Re-link", d.Message);
    }

    [Fact]
    public void AgentNotRunningNeverBlocksOnItsOwn()
    {
        var d = LaunchGate.AgentUnavailable("The Savepoint agent isn't running.");
        Assert.Equal(LaunchAction.Unsynced, d.Action);
        Assert.Contains("not synced", d.Message);
    }

    /// <summary>The agent reports SyncOutcome names in snake_case. Every one must be handled
    /// deliberately, not fall through to the generic "couldn't sync" branch by accident.</summary>
    [Fact]
    public void EveryAgentOutcomeIsHandled()
    {
        var deliberateDefault = new HashSet<string> { "failed" };
        foreach (var outcome in Enum.GetValues<SyncOutcome>())
        {
            var snake = string.Concat(outcome.ToString().Select((c, i) =>
                i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
            var d = LaunchGate.Decide(R(snake), "G");
            if (deliberateDefault.Contains(snake)) continue;
            Assert.False(d.Message.StartsWith("Savepoint couldn't sync saves before launch"), $"{snake} is not handled explicitly");
        }
    }
}

public class LinkMatcherTests
{
    private static readonly List<AgentGame> Games = new()
    {
        new() { Id = "er", Name = "Elden Ring", ExeNames = new() { "eldenring.exe" } },
        new() { Id = "nr", Name = "Elden Ring Nightreign", ExeNames = new() { "nightreign.exe" } },
        new() { Id = "hk", Name = "Hollow Knight: Silksong", ExeNames = new() { "Hollow Knight Silksong.exe" } },
        new() { Id = "bg", Name = "Baldur's Gate 3", ExeNames = new() { "bg3.exe", "bg3_dx11.exe" } },
    };

    [Fact]
    public void ExeMatchWinsOverName() =>
        Assert.Equal("nr", LinkMatcher.Suggest("Some Odd Title", new[] { @"{InstallDir}\Game\nightreign.exe" }, Games)!.Id);

    [Fact]
    public void ExeMatchIsCaseInsensitiveAndHandlesQuotes() =>
        Assert.Equal("bg", LinkMatcher.Suggest("x", new[] { "\"C:\\Games\\BG3\\bin\\BG3_DX11.EXE\"" }, Games)!.Id);

    [Fact]
    public void NameMatchIgnoresPunctuationAndTrademarks()
    {
        Assert.Equal("er", LinkMatcher.Suggest("ELDEN RING™", null, Games)!.Id);
        Assert.Equal("hk", LinkMatcher.Suggest("Hollow Knight Silksong", Array.Empty<string>(), Games)!.Id);
        Assert.Equal("bg", LinkMatcher.Suggest("Baldurs Gate 3", null, Games)!.Id);
    }

    [Fact]
    public void AmbiguousPartialMatchIsNotGuessed()
    {
        // "Elden Ring Deluxe" contains "eldenring" but not "eldenringnightreign": one candidate, so it's fine.
        Assert.Equal("er", LinkMatcher.Suggest("Elden Ring Deluxe Edition", null, Games)!.Id);
        // "Elden" is contained in both Elden Ring games: ambiguous.
        Assert.Null(LinkMatcher.Suggest("Elden", null, Games));
        Assert.Null(LinkMatcher.Suggest("Hades", null, Games));
    }

    [Fact]
    public void NormalizeName() =>
        Assert.Equal("pokemonlegendsza", LinkMatcher.NormalizeName("Pokémon Legends: Z-A®"));
}
