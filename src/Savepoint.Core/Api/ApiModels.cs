using System.Text.Json;
using System.Text.Json.Serialization;

namespace Savepoint.Core.Api;

/// <summary>JSON settings matching the server: snake_case, nulls written explicitly.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };
}

public static class VersionTypes
{
    public const string Session = "session";
    public const string Checkpoint = "checkpoint";
    public const string Manual = "manual";
    public const string Offline = "offline";
    public const string PreRestore = "pre_restore";
    public const string Promoted = "promoted";
}

public static class SyncModes
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}

public sealed record MachineInfo(string Id, string Name, DateTimeOffset ServerTime, long? ClockSkewMs);

public sealed record GameRoot(string Alias, string PathTemplate, string? ResolvedTemplate = null)
{
    /// <summary>The template to use on this machine (override applied by the server).</summary>
    [JsonIgnore]
    public string EffectiveTemplate => string.IsNullOrWhiteSpace(ResolvedTemplate) ? PathTemplate : ResolvedTemplate!;
}

public sealed record RetentionPolicy(
    int KeepNewest = 5,
    int? CheckpointKeepHours = 48,
    int? SessionKeepAllDays = 14,
    int? SessionDailyDays = 90,
    int? SessionWeeklyDays = 365,
    int? MaxVersions = null);

public sealed record Game
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public List<GameRoot> Roots { get; init; } = new();
    public List<string> ExeNames { get; init; } = new();
    public List<string> Excludes { get; init; } = new();
    public string SyncMode { get; init; } = SyncModes.Auto;
    public RetentionPolicy? Retention { get; init; }
    public string? HeadVersionId { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Machine ids this game syncs on, or null for all machines.</summary>
    public List<string>? SyncMachines { get; init; }

    /// <summary>Whether this game syncs on the calling machine. Defaults to true for
    /// servers that predate per-machine selection.</summary>
    public bool SyncEnabled { get; init; } = true;

    [JsonIgnore]
    public bool IsAuto => SyncMode == SyncModes.Auto;
}

/// <summary>Body for POST /games and PATCH /games/{id}.</summary>
public sealed record GameInput
{
    public string? Name { get; init; }
    public List<RootInput>? Roots { get; init; }
    public List<string>? ExeNames { get; init; }
    public List<string>? Excludes { get; init; }
    public string? SyncMode { get; init; }
}

public sealed record RootInput(string Alias, string PathTemplate);

public sealed record HeadInfo(string VersionId, DateTimeOffset CreatedAt, string? MachineId);

public sealed record FileEntryDto(string Path, string Hash, long Size, DateTimeOffset Mtime);

public sealed record VersionInfo
{
    public string Id { get; init; } = "";
    public string GameId { get; init; } = "";
    public string? ParentVersionId { get; init; }
    public string? SourceVersionId { get; init; }
    public string Type { get; init; } = "";
    public string? MachineId { get; init; }
    public string? MachineName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClientCreatedAt { get; init; }
    public int FileCount { get; init; }
    public long TotalSize { get; init; }
    public DateTimeOffset? LatestMtime { get; init; }
    public List<string> Tags { get; init; } = new();
    public string? Note { get; init; }
    public bool Pinned { get; init; }
    public bool IsHead { get; init; }
    public bool Unchanged { get; init; }
    public List<FileEntryDto>? Files { get; init; }
}

public sealed record CreateVersionRequest(
    string? ParentVersionId,
    string Type,
    bool MakeHead,
    DateTimeOffset ClientCreatedAt,
    List<FileEntryDto> Files);

internal sealed record ErrorEnvelope(ErrorBody? Error);

internal sealed record ErrorBody(string Code, string Message, List<string>? Missing, string? HeadVersionId);
