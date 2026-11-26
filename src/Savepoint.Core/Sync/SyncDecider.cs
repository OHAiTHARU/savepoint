namespace Savepoint.Core.Sync;

public enum PullAction
{
    /// <summary>L = S and H = S.</summary>
    Nothing,
    /// <summary>L = S, H ≠ S, auto mode: restore H.</summary>
    RestoreHead,
    /// <summary>L = S, H ≠ S, manual mode: ask the user.</summary>
    OfferRestore,
    /// <summary>L ≠ S, H = S: local changes the server lacks (e.g. played offline).</summary>
    PushOffline,
    /// <summary>L ≠ S, H ≠ S, but L already matches H's content: just record H as synced.</summary>
    AdoptHead,
    /// <summary>L ≠ S, H ≠ S, and they differ.</summary>
    Conflict,
}

public enum ConflictWinner { Local, Server }

/// <summary>The decision table from DESIGN §6, as pure functions.</summary>
public static class SyncDecider
{
    /// <param name="localChanged">L ≠ S (content differs from what was last synced).</param>
    /// <param name="serverMoved">H ≠ S (head is not the version last synced).</param>
    /// <param name="localMatchesHead">L has the same content as H.</param>
    /// <param name="autoMode">The game's sync_mode is "auto".</param>
    public static PullAction DecidePull(bool localChanged, bool serverMoved, bool localMatchesHead, bool autoMode)
    {
        if (!localChanged && !serverMoved) return PullAction.Nothing;
        if (!localChanged) return autoMode ? PullAction.RestoreHead : PullAction.OfferRestore;
        if (!serverMoved) return PullAction.PushOffline;
        return localMatchesHead ? PullAction.AdoptHead : PullAction.Conflict;
    }

    /// <summary>
    /// Auto-mode conflict: the side with the newest file wins. Times are compared in UTC,
    /// with this machine's measured clock offset removed from the local times. Ties go to
    /// the server; the local state has been uploaded either way, so nothing is lost.
    /// </summary>
    public static ConflictWinner DecideConflict(DateTimeOffset? localLatestMtime, DateTimeOffset? headLatestMtime,
        TimeSpan localClockOffset)
    {
        if (localLatestMtime is null) return ConflictWinner.Server;
        if (headLatestMtime is null) return ConflictWinner.Local;
        var localCorrected = localLatestMtime.Value.ToUniversalTime() - localClockOffset;
        return localCorrected > headLatestMtime.Value.ToUniversalTime() ? ConflictWinner.Local : ConflictWinner.Server;
    }
}
