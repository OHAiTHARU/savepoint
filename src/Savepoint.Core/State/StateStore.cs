using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Savepoint.Core.Api;
using Savepoint.Core.Scanning;

namespace Savepoint.Core.State;

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string? MachineId { get; set; }
    public string? MachineName { get; set; }
    public bool StartWithWindows { get; set; } = true;
    /// <summary>Minutes the file watcher must be quiet before a checkpoint push.</summary>
    public int QuietMinutes { get; set; } = 3;
    public int PollSeconds { get; set; } = 60;
    public int RescanMinutes { get; set; } = 15;
    /// <summary>How long a blocking pull (Playnite pre-launch) may take.</summary>
    public int PullTimeoutSeconds { get; set; } = 30;
    public bool Paused { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>What this machine last synced for a game (S in the sync algorithm).</summary>
public sealed record GameState(
    string GameId,
    string? LastSyncedVersionId,
    Manifest LastSyncedManifest,
    Manifest? LastScan,
    DateTimeOffset? LastScanAt,
    PendingConflict? Conflict);

/// <summary>A conflict waiting for the user (manual mode): our local state was uploaded
/// as <see cref="LocalVersionId"/> and the server head was <see cref="HeadVersionId"/>.</summary>
public sealed record PendingConflict(string LocalVersionId, string HeadVersionId, DateTimeOffset DetectedAt);

public sealed record PendingOp(long Id, string GameId, string Op, string PayloadJson, DateTimeOffset CreatedAt,
    int Attempts, DateTimeOffset NextAttemptAt, string? LastError);

public sealed record LocalSnapshot(long Id, string GameId, string Path, DateTimeOffset CreatedAt);

/// <summary>Local agent state in %LOCALAPPDATA%\Savepoint\state.db (DESIGN §6).</summary>
public sealed class StateStore : IDisposable
{
    private readonly string _connectionString;
    private readonly ISecretProtector _protector;
    private readonly object _lock = new();
    private SqliteConnection? _conn;

    public StateStore(string dbPath, ISecretProtector protector)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        _protector = protector;
        Migrate();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _conn?.Dispose();
            _conn = null;
        }
    }

    private SqliteConnection Conn()
    {
        if (_conn is null)
        {
            _conn = new SqliteConnection(_connectionString);
            _conn.Open();
            Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;");
        }
        return _conn;
    }

    private int Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = Conn().CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private void Migrate()
    {
        lock (_lock)
        {
            Exec("""
                CREATE TABLE IF NOT EXISTS config (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS game_state (
                    game_id TEXT PRIMARY KEY,
                    last_synced_version_id TEXT,
                    last_synced_manifest_json TEXT NOT NULL DEFAULT '[]',
                    last_scan_manifest_json TEXT,
                    last_scan_at TEXT,
                    conflict_json TEXT
                );
                CREATE TABLE IF NOT EXISTS pending_ops (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id TEXT NOT NULL,
                    op TEXT NOT NULL,
                    payload_json TEXT NOT NULL DEFAULT '{}',
                    created_at TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at TEXT NOT NULL,
                    last_error TEXT
                );
                CREATE TABLE IF NOT EXISTS local_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id TEXT NOT NULL,
                    path TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                """);
        }
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseIso(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    // ------------------------------------------------------------------ config

    public string? GetValue(string key)
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT value FROM config WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetValue(string key, string? value)
    {
        lock (_lock)
        {
            if (value is null) Exec("DELETE FROM config WHERE key = $k", ("$k", key));
            else Exec("INSERT INTO config(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("$k", key), ("$v", value));
        }
    }

    public AgentConfig LoadConfig()
    {
        var json = GetValue("agent_config");
        var cfg = json is null ? new AgentConfig() : JsonSerializer.Deserialize<AgentConfig>(json, Json.Options) ?? new AgentConfig();
        var key = GetValue("api_key");
        cfg.ApiKey = "";
        if (key is not null)
        {
            try { cfg.ApiKey = _protector.Unprotect(key); }
            catch (Exception) { cfg.ApiKey = ""; } // different user/PC: needs re-entry
        }
        return cfg;
    }

    public void SaveConfig(AgentConfig cfg)
    {
        var copy = JsonSerializer.Deserialize<AgentConfig>(JsonSerializer.Serialize(cfg, Json.Options), Json.Options)!;
        copy.ApiKey = ""; // never stored in the clear
        SetValue("agent_config", JsonSerializer.Serialize(copy, Json.Options));
        SetValue("api_key", string.IsNullOrEmpty(cfg.ApiKey) ? null : _protector.Protect(cfg.ApiKey));
    }

    /// <summary>The last game list from the server, so the agent can scan and queue
    /// pushes while offline.</summary>
    public IReadOnlyList<Game> LoadGames()
    {
        var json = GetValue("games_cache");
        return json is null ? Array.Empty<Game>() : JsonSerializer.Deserialize<List<Game>>(json, Json.Options) ?? new List<Game>();
    }

    public void SaveGames(IEnumerable<Game> games) =>
        SetValue("games_cache", JsonSerializer.Serialize(games.ToList(), Json.Options));

    // ------------------------------------------------------------------ game state

    public GameState GetGameState(string gameId)
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = """
                SELECT last_synced_version_id, last_synced_manifest_json, last_scan_manifest_json, last_scan_at, conflict_json
                FROM game_state WHERE game_id = $g
                """;
            cmd.Parameters.AddWithValue("$g", gameId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new GameState(gameId, null, Manifest.Empty, null, null, null);
            return new GameState(
                gameId,
                r.IsDBNull(0) ? null : r.GetString(0),
                ManifestFromJson(r.GetString(1)),
                r.IsDBNull(2) ? null : ManifestFromJson(r.GetString(2)),
                r.IsDBNull(3) ? null : ParseIso(r.GetString(3)),
                r.IsDBNull(4) ? null : JsonSerializer.Deserialize<PendingConflict>(r.GetString(4), Json.Options));
        }
    }

    public void SetSynced(string gameId, string? versionId, Manifest manifest)
    {
        lock (_lock)
        {
            Exec("""
                INSERT INTO game_state(game_id, last_synced_version_id, last_synced_manifest_json)
                VALUES($g, $v, $m)
                ON CONFLICT(game_id) DO UPDATE SET
                    last_synced_version_id = excluded.last_synced_version_id,
                    last_synced_manifest_json = excluded.last_synced_manifest_json
                """, ("$g", gameId), ("$v", versionId), ("$m", ManifestToJson(manifest)));
        }
    }

    public void SetLastScan(string gameId, Manifest scan, DateTimeOffset at)
    {
        lock (_lock)
        {
            Exec("""
                INSERT INTO game_state(game_id, last_scan_manifest_json, last_scan_at) VALUES($g, $m, $t)
                ON CONFLICT(game_id) DO UPDATE SET
                    last_scan_manifest_json = excluded.last_scan_manifest_json,
                    last_scan_at = excluded.last_scan_at
                """, ("$g", gameId), ("$m", ManifestToJson(scan)), ("$t", Iso(at)));
        }
    }

    public void SetConflict(string gameId, PendingConflict? conflict)
    {
        lock (_lock)
        {
            Exec("""
                INSERT INTO game_state(game_id, conflict_json) VALUES($g, $c)
                ON CONFLICT(game_id) DO UPDATE SET conflict_json = excluded.conflict_json
                """, ("$g", gameId), ("$c", conflict is null ? null : JsonSerializer.Serialize(conflict, Json.Options)));
        }
    }

    public IReadOnlyList<(string GameId, PendingConflict Conflict)> GetConflicts()
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT game_id, conflict_json FROM game_state WHERE conflict_json IS NOT NULL";
            using var r = cmd.ExecuteReader();
            var list = new List<(string, PendingConflict)>();
            while (r.Read())
            {
                var c = JsonSerializer.Deserialize<PendingConflict>(r.GetString(1), Json.Options);
                if (c is not null) list.Add((r.GetString(0), c));
            }
            return list;
        }
    }

    private static Manifest ManifestFromJson(string json) =>
        Manifest.FromDtos(JsonSerializer.Deserialize<List<FileEntryDto>>(json, Json.Options));

    private static string ManifestToJson(Manifest m) => JsonSerializer.Serialize(m.ToDtos(), Json.Options);

    // ------------------------------------------------------------------ pending ops

    /// <summary>Queue an op. A game has at most one pending op of each kind; queueing again
    /// keeps the original (its payload is replaced, e.g. with a newer push type).</summary>
    public void Enqueue(string gameId, string op, string payloadJson = "{}")
    {
        lock (_lock)
        {
            var updated = Exec("UPDATE pending_ops SET payload_json = $p WHERE game_id = $g AND op = $o",
                ("$g", gameId), ("$o", op), ("$p", payloadJson));
            if (updated == 0)
            {
                var now = Iso(DateTimeOffset.UtcNow);
                Exec("INSERT INTO pending_ops(game_id, op, payload_json, created_at, next_attempt_at) VALUES($g, $o, $p, $n, $n)",
                    ("$g", gameId), ("$o", op), ("$p", payloadJson), ("$n", now));
            }
        }
    }

    public IReadOnlyList<PendingOp> GetPendingOps()
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT id, game_id, op, payload_json, created_at, attempts, next_attempt_at, last_error FROM pending_ops ORDER BY id";
            using var r = cmd.ExecuteReader();
            var list = new List<PendingOp>();
            while (r.Read())
            {
                list.Add(new PendingOp(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    ParseIso(r.GetString(4)), r.GetInt32(5), ParseIso(r.GetString(6)), r.IsDBNull(7) ? null : r.GetString(7)));
            }
            return list;
        }
    }

    public void CompleteOp(long id)
    {
        lock (_lock) Exec("DELETE FROM pending_ops WHERE id = $id", ("$id", id));
    }

    public void FailOp(long id, string error, DateTimeOffset nextAttempt)
    {
        lock (_lock)
        {
            Exec("UPDATE pending_ops SET attempts = attempts + 1, last_error = $e, next_attempt_at = $n WHERE id = $id",
                ("$id", id), ("$e", error), ("$n", Iso(nextAttempt)));
        }
    }

    // ------------------------------------------------------------------ snapshots

    public void AddSnapshot(string gameId, string path, DateTimeOffset at)
    {
        lock (_lock) Exec("INSERT INTO local_snapshots(game_id, path, created_at) VALUES($g, $p, $t)",
            ("$g", gameId), ("$p", path), ("$t", Iso(at)));
    }

    public IReadOnlyList<LocalSnapshot> GetSnapshots(string gameId)
    {
        lock (_lock)
        {
            using var cmd = Conn().CreateCommand();
            cmd.CommandText = "SELECT id, game_id, path, created_at FROM local_snapshots WHERE game_id = $g ORDER BY id DESC";
            cmd.Parameters.AddWithValue("$g", gameId);
            using var r = cmd.ExecuteReader();
            var list = new List<LocalSnapshot>();
            while (r.Read()) list.Add(new LocalSnapshot(r.GetInt64(0), r.GetString(1), r.GetString(2), ParseIso(r.GetString(3))));
            return list;
        }
    }

    public void DeleteSnapshot(long id)
    {
        lock (_lock) Exec("DELETE FROM local_snapshots WHERE id = $id", ("$id", id));
    }
}
