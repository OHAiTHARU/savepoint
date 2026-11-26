using Microsoft.Extensions.Logging;
using Savepoint.Core.Api;
using Savepoint.Core.Paths;
using Savepoint.Core.Scanning;
using Savepoint.Core.State;
using Savepoint.Core.Sync;

namespace Savepoint.Agent.Services;

public enum AgentStatus { NotConfigured, Idle, Syncing, Offline, Attention, Paused }

/// <summary>
/// Everything the tray app does that isn't UI: connects to the server, keeps the game
/// list, and turns triggers (DESIGN §6 "Triggers") into sync-engine calls.
/// Events are raised on thread-pool threads; the UI marshals them.
/// </summary>
public sealed class AgentService : IDisposable
{
    private static readonly TimeSpan OfflineToastAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan GamesRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly StateStore _state;
    private readonly ILoggerFactory _logs;
    private readonly ILogger _log;
    private readonly string _dataDir;
    private readonly IPathEnvironment _paths = new SystemPathEnvironment();
    private readonly ExeMonitor _exes;
    private readonly WatcherManager _watchers;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<System.Threading.Timer> _timers = new();
    private readonly object _lock = new();

    private SavepointApi? _api;
    private SyncEngine? _engine;
    private IReadOnlyList<Game> _games = Array.Empty<Game>();
    private Dictionary<string, string> _knownHeads = new();
    private DateTimeOffset _lastGamesRefresh = DateTimeOffset.MinValue;
    private DateTimeOffset? _offlineSince;
    private bool _offlineToastShown;
    private string? _lastError;
    private int _polling;

    public AgentService(StateStore state, ILoggerFactory logs, string dataDir)
    {
        _state = state;
        _logs = logs;
        _log = logs.CreateLogger("Agent");
        _dataDir = dataDir;
        Config = state.LoadConfig();
        _games = state.LoadGames();
        _exes = new ExeMonitor(logs.CreateLogger("ExeMonitor"), TimeSpan.FromSeconds(5));
        _exes.SetGames(_games);
        _exes.GameExited += (_, g) => _ = OnGameExitedAsync(g);
        _exes.GameStarted += (_, g) => RaiseStatus();
        _watchers = new WatcherManager(logs.CreateLogger("Watcher"));
    }

    public AgentConfig Config { get; private set; }
    public IReadOnlyList<Game> Games { get { lock (_lock) return _games; } }
    public string? MachineName => Config.MachineName;
    public string ServerUrl => Config.ServerUrl;
    public string LogDirectory => Path.Combine(_dataDir, "logs");

    public event EventHandler? StatusChanged;
    public event EventHandler? GamesChanged;
    public event EventHandler<SyncNotification>? Notification;

    // ================================================================== status

    public AgentStatus Status
    {
        get
        {
            if (!Config.IsConfigured) return AgentStatus.NotConfigured;
            if (Config.Paused) return AgentStatus.Paused;
            if (_engine?.ActiveOperations > 0) return AgentStatus.Syncing;
            if (_offlineSince is not null) return AgentStatus.Offline;
            if (_state.GetConflicts().Count > 0 || _lastError is not null) return AgentStatus.Attention;
            return AgentStatus.Idle;
        }
    }

    public string StatusText
    {
        get
        {
            var running = _exes.RunningGameIds.Select(id => Games.FirstOrDefault(g => g.Id == id)?.Name).Where(n => n != null).ToList();
            var playing = running.Count > 0 ? $" · playing {string.Join(", ", running)}" : "";
            var pending = _state.GetPendingOps().Count;
            var queued = pending > 0 ? $" · {pending} queued" : "";
            return Status switch
            {
                AgentStatus.NotConfigured => "Not set up: open Settings",
                AgentStatus.Paused => "Sync paused",
                AgentStatus.Syncing => "Syncing…" + playing,
                AgentStatus.Offline => $"Offline since {_offlineSince!.Value.ToLocalTime():t}{queued}",
                AgentStatus.Attention => (_state.GetConflicts().Count > 0
                    ? $"{_state.GetConflicts().Count} conflict(s) need a decision"
                    : $"Last sync failed: {_lastError}") + playing,
                _ => $"Up to date{playing}{queued}",
            };
        }
    }

    public bool IsRunning(Game game) => _exes.IsRunning(game.Id);

    public PendingConflict? ConflictFor(string gameId) => _state.GetGameState(gameId).Conflict;

    private void RaiseStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);

    // ================================================================== lifecycle

    public async Task StartAsync()
    {
        if (!Config.IsConfigured)
        {
            RaiseStatus();
            return;
        }
        await ConnectAsync();
        StartTimers();
        _ = Task.Run(StartupSyncAsync);
    }

    private async Task ConnectAsync()
    {
        _api?.Dispose();
        _api = new SavepointApi(Config.ServerUrl, Config.ApiKey);
        _engine = new SyncEngine(_api, _state, _paths, g => _exes.IsRunning(g.Id),
            new SyncEngineOptions { DataDirectory = _dataDir }, _logs.CreateLogger("Sync"));
        _engine.Notification += (_, n) => Notification?.Invoke(this, n);
        _engine.ActivityChanged += (_, _) => RaiseStatus();

        try
        {
            var me = await _api.GetMeAsync(_shutdown.Token);
            Config.MachineId = me.Id;
            Config.MachineName = me.Name;
            _state.SaveConfig(Config);
            _log.LogInformation("Connected as {Machine}; clock offset {Offset}", me.Name, _api.ClockOffset);
            MarkOnline();
        }
        catch (ServerUnreachableException ex)
        {
            MarkOffline(ex.Message);
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            _lastError = "API key rejected; check Settings";
            Notification?.Invoke(this, new SyncNotification(NotificationKind.Error, "", "Savepoint",
                "The server rejected this PC's API key. Open Settings to enter a new one."));
        }
    }

    private async Task StartupSyncAsync()
    {
        await RefreshGamesAsync(force: true);
        if (Config.Paused) return;
        // Pull check for every game (also pushes changes made while the agent wasn't running).
        foreach (var g in Games) await RunAsync(e => e.PullCheckAsync(g, _shutdown.Token));
        await FlushPendingAsync();
        await PollHeadsAsync();
    }

    private void StartTimers()
    {
        foreach (var t in _timers) t.Dispose();
        _timers.Clear();
        var poll = TimeSpan.FromSeconds(Math.Max(15, Config.PollSeconds));
        _timers.Add(new System.Threading.Timer(_ => _ = PollHeadsAsync(), null, poll, poll));
        _timers.Add(new System.Threading.Timer(_ => _ = CheckQuietFoldersAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)));
        var rescan = TimeSpan.FromMinutes(Math.Max(1, Config.RescanMinutes));
        _timers.Add(new System.Threading.Timer(_ => _ = PeriodicRescanAsync(), null, rescan, rescan));
        _timers.Add(new System.Threading.Timer(_ => _ = FlushPendingAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)));
    }

    public async Task ApplyConfigAsync(AgentConfig config)
    {
        var reconnect = config.ServerUrl != Config.ServerUrl || config.ApiKey != Config.ApiKey;
        Config = config;
        _state.SaveConfig(config);
        try { StartupRegistration.Apply(config.StartWithWindows); }
        catch (Exception ex) { _log.LogWarning(ex, "Couldn't update the Run key"); }
        if (!config.IsConfigured) { RaiseStatus(); return; }
        if (reconnect || _engine is null)
        {
            _lastError = null;
            _knownHeads.Clear();
            await ConnectAsync();
            StartTimers();
            _ = Task.Run(StartupSyncAsync);
        }
        else
        {
            StartTimers();
        }
        RaiseStatus();
    }

    public static async Task<(bool Ok, string Message)> TestConnectionAsync(string url, string key)
    {
        try
        {
            using var api = new SavepointApi(url, key);
            var me = await api.GetMeAsync();
            var offset = api.ClockOffset;
            var clock = Math.Abs(offset.TotalSeconds) < 5
                ? "clock in sync"
                : $"this PC's clock is {Math.Abs(offset.TotalSeconds):0} s {(offset > TimeSpan.Zero ? "ahead" : "behind")}";
            return (true, $"Connected as \"{me.Name}\" ({clock}).");
        }
        catch (ApiException ex) when (ex.IsUnauthorized) { return (false, "The server rejected this API key."); }
        catch (ApiException ex) { return (false, ex.Message); }
        catch (ServerUnreachableException ex) { return (false, ex.Message); }
        catch (ArgumentException ex) { return (false, ex.Message); }
    }

    public void SetPaused(bool paused)
    {
        Config.Paused = paused;
        _state.SaveConfig(Config);
        RaiseStatus();
        if (!paused) _ = Task.Run(StartupSyncAsync);
    }

    // ================================================================== games

    public async Task RefreshGamesAsync(bool force = false)
    {
        if (_api is null) return;
        if (!force && DateTimeOffset.UtcNow - _lastGamesRefresh < GamesRefreshInterval) return;
        try
        {
            var games = await _api.GetGamesAsync(_shutdown.Token);
            _lastGamesRefresh = DateTimeOffset.UtcNow;
            SetGames(games);
            MarkOnline();
        }
        catch (ServerUnreachableException ex)
        {
            MarkOffline(ex.Message);
            SetGames(_state.LoadGames()); // keep working from the cache
        }
        catch (ApiException ex)
        {
            _log.LogWarning(ex, "Couldn't load games");
        }
    }

    private void SetGames(IReadOnlyList<Game> games)
    {
        lock (_lock) _games = games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _state.SaveGames(games);
        _exes.SetGames(games);
        RebuildWatchers();
        GamesChanged?.Invoke(this, EventArgs.Empty);
        RaiseStatus();
    }

    private void RebuildWatchers()
    {
        var engine = _engine;
        if (engine is null) return;
        var resolved = new List<(Game, IReadOnlyDictionary<string, ResolvedRoot>)>();
        foreach (var g in Games)
        {
            try { resolved.Add((g, engine.ResolveRoots(g))); }
            catch (PathTemplateException ex) { _log.LogWarning("{Game}: {Message}", g.Name, ex.Message); }
        }
        _watchers.Rebuild(resolved);
    }

    /// <summary>Create or update a game from the Add/Edit dialog, plus this machine's path overrides.</summary>
    public async Task<Game> SaveGameAsync(string? gameId, GameInput input, IReadOnlyDictionary<string, string?> overrides)
    {
        var api = _api ?? throw new InvalidOperationException("Not connected");
        var game = gameId is null
            ? await api.CreateGameAsync(input)
            : await api.UpdateGameAsync(gameId, input);
        foreach (var root in game.Roots)
        {
            overrides.TryGetValue(root.Alias, out var wanted);
            wanted = string.IsNullOrWhiteSpace(wanted) ? null : wanted.Trim();
            var current = root.ResolvedTemplate == root.PathTemplate ? null : root.ResolvedTemplate;
            if (wanted != current) game = await api.SetRootOverrideAsync(game.Id, root.Alias, wanted);
        }
        await RefreshGamesAsync(force: true);
        // A new game may already have saves here (or on the server).
        _ = Task.Run(() => RunAsync(e => e.PullCheckAsync(game, _shutdown.Token)));
        return game;
    }

    public string ResolvePreview(string template)
    {
        try { return PathTemplate.Resolve(template, _paths); }
        catch (PathTemplateException ex) { return "⚠ " + ex.Message; }
    }

    public IPathEnvironment Paths => _paths;

    // ================================================================== triggers

    private async Task OnGameExitedAsync(Game game)
    {
        RaiseStatus();
        if (Config.Paused) return;
        var current = Games.FirstOrDefault(g => g.Id == game.Id) ?? game;
        await RunAsync(e => e.PushAsync(current, VersionTypes.Session, _shutdown.Token));
        // A pull may have been skipped while the game ran.
        await RunAsync(e => e.PullCheckAsync(current, _shutdown.Token));
    }

    private async Task PollHeadsAsync()
    {
        if (_api is null || Config.Paused || Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var heads = await _api.GetHeadsAsync(_shutdown.Token);
            var wasOffline = _offlineSince is not null;
            MarkOnline();
            if (wasOffline) _ = FlushPendingAsync();

            var unknown = heads.Keys.Any(id => Games.All(g => g.Id != id));
            await RefreshGamesAsync(force: unknown);

            var changed = new List<string>();
            foreach (var (gameId, head) in heads)
            {
                if (!_knownHeads.TryGetValue(gameId, out var known) || known != head.VersionId) changed.Add(gameId);
            }
            _knownHeads = heads.ToDictionary(h => h.Key, h => h.Value.VersionId);

            foreach (var id in changed)
            {
                var game = Games.FirstOrDefault(g => g.Id == id);
                if (game is null) continue;
                if (_state.GetGameState(id).LastSyncedVersionId == heads[id].VersionId) continue; // our own push
                await RunAsync(e => e.PullCheckAsync(game, _shutdown.Token));
            }
        }
        catch (ServerUnreachableException ex)
        {
            MarkOffline(ex.Message);
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            _lastError = "API key rejected; check Settings";
            RaiseStatus();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Heads poll failed");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private async Task CheckQuietFoldersAsync()
    {
        if (Config.Paused) return;
        foreach (var id in _watchers.TakeSettled(TimeSpan.FromMinutes(Math.Max(1, Config.QuietMinutes))))
        {
            var game = Games.FirstOrDefault(g => g.Id == id);
            if (game is not null) await RunAsync(e => e.PushAsync(game, VersionTypes.Checkpoint, _shutdown.Token));
        }
    }

    private async Task PeriodicRescanAsync()
    {
        if (Config.Paused) return;
        if (_watchers.HasMissingRoots) RebuildWatchers(); // a save folder may have appeared
        foreach (var game in Games) await RunAsync(e => e.PushAsync(game, VersionTypes.Checkpoint, _shutdown.Token));
    }

    private async Task FlushPendingAsync()
    {
        var engine = _engine;
        if (engine is null || Config.Paused || !engine.HasPendingOps) return;
        var flushed = await engine.FlushPendingAsync(Games, _shutdown.Token);
        if (flushed > 0)
        {
            MarkOnline();
            Notification?.Invoke(this, new SyncNotification(NotificationKind.QueueFlushed, "", "Savepoint",
                $"Uploaded {flushed} queued backup{(flushed == 1 ? "" : "s")}."));
        }
        RaiseStatus();
    }

    // ================================================================== user actions

    public Task<SyncResult> BackUpNowAsync(Game game) => RunAsync(e => e.PushAsync(game, VersionTypes.Manual, _shutdown.Token));

    public Task<SyncResult> RestoreLatestAsync(Game game) => RunAsync(e => e.RestoreLatestAsync(game, _shutdown.Token));

    public Task<SyncResult> ResolveConflictAsync(Game game, ConflictChoice choice) =>
        RunAsync(e => e.ResolveConflictAsync(game, choice, _shutdown.Token));

    /// <summary>Blocking pull for Playnite's pre-launch hook, bounded by the configured timeout.</summary>
    public async Task<SyncResult> PullForLaunchAsync(string gameId)
    {
        var game = Games.FirstOrDefault(g => g.Id == gameId);
        if (game is null) return SyncResult.Of(SyncOutcome.Failed, "Unknown game");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, Config.PullTimeoutSeconds)));
        try
        {
            return await RunAsync(e => e.PullCheckAsync(game, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return SyncResult.Of(SyncOutcome.Failed, "Timed out waiting for the server");
        }
    }

    public Task<SyncResult> PushAsync(string gameId, string type)
    {
        var game = Games.FirstOrDefault(g => g.Id == gameId);
        return game is null
            ? Task.FromResult(SyncResult.Of(SyncOutcome.Failed, "Unknown game"))
            : RunAsync(e => e.PushAsync(game, type, _shutdown.Token));
    }

    private async Task<SyncResult> RunAsync(Func<SyncEngine, Task<SyncResult>> op)
    {
        var engine = _engine;
        if (engine is null) return SyncResult.Of(SyncOutcome.Failed, "Not connected; open Settings");
        SyncResult result;
        try
        {
            result = await op(engine);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return SyncResult.Of(SyncOutcome.Failed, "Shutting down");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unexpected sync error");
            result = SyncResult.Of(SyncOutcome.Failed, ex.Message);
        }

        switch (result.Outcome)
        {
            case SyncOutcome.Offline or SyncOutcome.Queued:
                MarkOffline(result.Message);
                break;
            case SyncOutcome.Failed:
                _lastError = result.Message;
                break;
            case SyncOutcome.Busy:
                break;
            default:
                _lastError = null;
                MarkOnline();
                break;
        }
        if (result.Outcome is not (SyncOutcome.NoChange or SyncOutcome.SkippedGameRunning))
            _log.LogInformation("{Outcome}: {Message}", result.Outcome, result.Message);
        RaiseStatus();
        return result;
    }

    private void MarkOnline()
    {
        if (_offlineSince is null) return;
        _log.LogInformation("Server reachable again");
        _offlineSince = null;
        _offlineToastShown = false;
        RaiseStatus();
    }

    private void MarkOffline(string reason)
    {
        _offlineSince ??= DateTimeOffset.UtcNow;
        if (!_offlineToastShown && DateTimeOffset.UtcNow - _offlineSince > OfflineToastAfter)
        {
            _offlineToastShown = true;
            Notification?.Invoke(this, new SyncNotification(NotificationKind.Error, "", "Savepoint",
                "The server has been unreachable for over an hour. Backups are queued and will upload when it's back."));
        }
        _log.LogDebug("Offline: {Reason}", reason);
        RaiseStatus();
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        foreach (var t in _timers) t.Dispose();
        _exes.Dispose();
        _watchers.Dispose();
        _api?.Dispose();
    }
}
