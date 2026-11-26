#nullable disable
namespace Savepoint.Playnite
{
    public enum LaunchAction
    {
        /// <summary>Saves are in sync; launch.</summary>
        Proceed,
        /// <summary>Launch, but tell the user something (restored, or couldn't sync).</summary>
        ProceedWithNotice,
        /// <summary>Couldn't sync; launch or ask depending on settings.</summary>
        Unsynced,
        /// <summary>Manual mode: a newer server save exists. Ask: restore, play local, or cancel.</summary>
        AskRestore,
        /// <summary>A conflict needs a decision before playing.</summary>
        AskConflict,
    }

    public class LaunchDecision
    {
        public LaunchDecision(LaunchAction action, string message)
        {
            Action = action;
            Message = message;
        }

        public LaunchAction Action { get; }
        public string Message { get; }
    }

    /// <summary>Turns the agent's answer to a pre-launch pull into what the plugin should do.
    /// Never blocks a launch on its own: when in doubt the game starts (DESIGN §6).</summary>
    public static class LaunchGate
    {
        public static LaunchDecision AgentUnavailable(string reason) =>
            new LaunchDecision(LaunchAction.Unsynced,
                (reason ?? "The Savepoint agent isn't running.") + " Saves were not synced before launch.");

        public static LaunchDecision Decide(AgentResponse r, string gameName)
        {
            if (r == null) return AgentUnavailable(null);
            switch (r.Outcome)
            {
                case "no_change":
                case "adopted_head":
                case "pushed":
                case "skipped_game_running":
                    return new LaunchDecision(LaunchAction.Proceed, r.Message);

                case "restored":
                case "conflict_resolved_server":
                    return new LaunchDecision(LaunchAction.ProceedWithNotice,
                        $"Restored the latest {gameName} save from the server before launch.");

                case "conflict_resolved_local":
                    return new LaunchDecision(LaunchAction.ProceedWithNotice,
                        $"{gameName}: this PC's save was newer and is now the latest on the server.");

                case "restore_offered":
                    return new LaunchDecision(LaunchAction.AskRestore,
                        $"A newer {gameName} save is on the server (from another PC or the web UI).\n\nRestore it before playing?");

                case "conflict_pending":
                    return new LaunchDecision(LaunchAction.AskConflict,
                        $"{gameName}'s save was changed on this PC and on another PC since they last synced. " +
                        "Both versions are kept on the server.\n\nWhich save do you want to play with?");

                case "queued":
                case "offline":
                    return new LaunchDecision(LaunchAction.Unsynced,
                        "The Savepoint server is unreachable. Playing with this PC's saves; changes will upload when it's back.");

                case "busy":
                    return new LaunchDecision(LaunchAction.Unsynced,
                        "A save file was locked, so Savepoint couldn't check for a newer save.");

                default:
                    if (r.Message == "Unknown game")
                        return new LaunchDecision(LaunchAction.Unsynced,
                            $"{gameName} is linked to a Savepoint game that no longer exists. Re-link it from the game's menu.");
                    return new LaunchDecision(LaunchAction.Unsynced,
                        "Savepoint couldn't sync saves before launch: " + (r.Message ?? r.Error ?? r.Outcome ?? "unknown error"));
            }
        }
    }
}
