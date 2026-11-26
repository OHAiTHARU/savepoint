using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Savepoint.Core.Api;
using Savepoint.Core.Sync;

namespace Savepoint.Agent.Services;

/// <summary>
/// Named pipe <c>\\.\pipe\savepoint</c> speaking JSON lines (DESIGN §6 "IPC"), for the
/// Playnite plugin and scripts. One request per line, one response per line:
/// <code>
/// {"cmd":"pull","game_id":"…"}                  → {"ok":true,"outcome":"restored","message":"…"}
/// {"cmd":"push","game_id":"…","type":"session"} → {"ok":true,"outcome":"pushed",…}
/// {"cmd":"status","game_id":"…"}                → {"ok":true,"status":"idle","text":"…","games":[…]}
/// {"cmd":"list_games"}                          → {"ok":true,"games":[{"id","name","exe_names","sync_mode"}]}
/// </code>
/// The pipe is only accessible to the current user (the .NET default ACL).
/// </summary>
public sealed class IpcServer : IDisposable
{
    public const string PipeName = "savepoint";

    private static readonly HashSet<string> PushTypes = new(StringComparer.Ordinal)
    {
        VersionTypes.Session, VersionTypes.Checkpoint, VersionTypes.Manual,
    };

    private readonly AgentService _agent;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();

    public IpcServer(AgentService agent, ILogger log)
    {
        _agent = agent;
        _log = log;
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token);
                var client = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "IPC accept failed");
                await Task.Delay(1000);
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            try
            {
                while (!_cts.IsCancellationRequested && pipe.IsConnected)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var response = await HandleAsync(line);
                    await writer.WriteLineAsync(response.ToJsonString());
                }
            }
            catch (IOException) { /* client went away */ }
            catch (OperationCanceledException) { }
        }
    }

    internal async Task<JsonObject> HandleAsync(string line)
    {
        JsonObject req;
        try
        {
            req = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("Expected an object");
        }
        catch (JsonException ex)
        {
            return Error("bad_request", ex.Message);
        }

        var cmd = req["cmd"]?.GetValue<string>();
        var gameId = req["game_id"]?.GetValue<string>();
        try
        {
            switch (cmd)
            {
                case "pull":
                    if (gameId is null) return Error("bad_request", "game_id is required");
                    return FromResult(await _agent.PullForLaunchAsync(gameId));

                case "push":
                {
                    if (gameId is null) return Error("bad_request", "game_id is required");
                    var type = req["type"]?.GetValue<string>() ?? VersionTypes.Session;
                    if (!PushTypes.Contains(type)) return Error("bad_request", $"type must be one of {string.Join(", ", PushTypes)}");
                    return FromResult(await _agent.PushAsync(gameId, type));
                }

                case "status":
                {
                    var games = _agent.Games.Where(g => gameId is null || g.Id == gameId).Select(g =>
                    {
                        var conflict = _agent.ConflictFor(g.Id);
                        return new JsonObject
                        {
                            ["id"] = g.Id,
                            ["name"] = g.Name,
                            ["running"] = _agent.IsRunning(g),
                            ["conflict"] = conflict is not null,
                        };
                    });
                    return new JsonObject
                    {
                        ["ok"] = true,
                        ["status"] = _agent.Status.ToString().ToLowerInvariant(),
                        ["text"] = _agent.StatusText,
                        ["games"] = new JsonArray(games.Cast<JsonNode?>().ToArray()),
                    };
                }

                case "list_games":
                {
                    var games = _agent.Games.Select(g => new JsonObject
                    {
                        ["id"] = g.Id,
                        ["name"] = g.Name,
                        ["exe_names"] = new JsonArray(g.ExeNames.Select(e => (JsonNode?)e).ToArray()),
                        ["sync_mode"] = g.SyncMode,
                    });
                    return new JsonObject { ["ok"] = true, ["games"] = new JsonArray(games.Cast<JsonNode?>().ToArray()) };
                }

                default:
                    return Error("unknown_command", $"Unknown cmd {cmd ?? "(missing)"}");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "IPC command {Cmd} failed", cmd);
            return Error("internal", ex.Message);
        }
    }

    private static JsonObject FromResult(SyncResult r) => new()
    {
        ["ok"] = r.Succeeded,
        ["outcome"] = ToSnake(r.Outcome.ToString()),
        ["message"] = r.Message,
        ["version_id"] = r.Version?.Id,
    };

    private static JsonObject Error(string code, string message) =>
        new() { ["ok"] = false, ["error"] = code, ["message"] = message };

    private static string ToSnake(string s) =>
        string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    public void Dispose() => _cts.Cancel();
}
