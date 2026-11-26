using Savepoint.Core.Api;

namespace Savepoint.Core.Sync;

public enum SyncOutcome
{
    NoChange,
    Pushed,
    Restored,
    AdoptedHead,
    /// <summary>Pull skipped because the game is running; retry after it exits.</summary>
    SkippedGameRunning,
    /// <summary>Server unreachable; the push was queued.</summary>
    Queued,
    /// <summary>Server unreachable during a pull; nothing to do until it's back.</summary>
    Offline,
    /// <summary>Manual mode: a newer head is available and the user was asked.</summary>
    RestoreOffered,
    /// <summary>Manual mode: a conflict is waiting for the user's decision.</summary>
    ConflictPending,
    ConflictResolvedLocal,
    ConflictResolvedServer,
    /// <summary>A save file was locked or kept changing; try again later.</summary>
    Busy,
    Failed,
}

public sealed record SyncResult(SyncOutcome Outcome, string Message, VersionInfo? Version = null)
{
    public bool Succeeded => Outcome is not (SyncOutcome.Failed or SyncOutcome.Busy);
    public static SyncResult Of(SyncOutcome o, string message, VersionInfo? v = null) => new(o, message, v);
}

public enum ConflictChoice { KeepLocal, TakeServer }

public enum NotificationKind
{
    Restored,
    RestoreAvailable,
    ConflictNeedsDecision,
    ConflictResolved,
    Queued,
    QueueFlushed,
    Error,
}

public sealed record SyncNotification(NotificationKind Kind, string GameId, string GameName, string Message);
