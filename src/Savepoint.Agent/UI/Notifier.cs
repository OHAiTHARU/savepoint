using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Savepoint.Core.Sync;

namespace Savepoint.Agent.UI;

public enum ToastAction { RestoreNow, KeepLocal, TakeServer, OpenWeb }

/// <summary>Windows toasts with action buttons, falling back to tray balloons if toasts
/// aren't available.</summary>
public sealed class Notifier
{
    private readonly NotifyIcon _tray;
    private readonly ILogger _log;
    private bool _toastsBroken;

    public Notifier(NotifyIcon tray, ILogger log)
    {
        _tray = tray;
        _log = log;
        try
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                if (args.TryGetValue("action", out var action) && Enum.TryParse<ToastAction>(action, out var a))
                {
                    args.TryGetValue("game", out var gameId);
                    Activated?.Invoke(this, (a, gameId ?? ""));
                }
            };
        }
        catch (Exception ex)
        {
            _toastsBroken = true;
            _log.LogWarning(ex, "Toast notifications unavailable; using balloons");
        }
    }

    /// <summary>A toast button was clicked. Raised on a background thread.</summary>
    public event EventHandler<(ToastAction Action, string GameId)>? Activated;

    public void Show(SyncNotification n)
    {
        var title = n.Kind switch
        {
            NotificationKind.Restored => $"{n.GameName}: save restored",
            NotificationKind.RestoreAvailable => $"{n.GameName}: newer save available",
            NotificationKind.ConflictNeedsDecision => $"{n.GameName}: conflict",
            NotificationKind.ConflictResolved => $"{n.GameName}: conflict resolved",
            NotificationKind.Queued => $"{n.GameName}: backup queued",
            NotificationKind.QueueFlushed => "Savepoint: back online",
            NotificationKind.Error => string.IsNullOrEmpty(n.GameId) ? "Savepoint" : $"{n.GameName}: sync problem",
            _ => n.GameName,
        };

        if (!_toastsBroken)
        {
            try
            {
                var b = new ToastContentBuilder().AddText(title).AddText(n.Message);
                switch (n.Kind)
                {
                    case NotificationKind.RestoreAvailable:
                        b.AddButton(Button("Restore now", ToastAction.RestoreNow, n.GameId));
                        b.AddButton(Button("Open in web UI", ToastAction.OpenWeb, n.GameId));
                        break;
                    case NotificationKind.ConflictNeedsDecision:
                        b.AddButton(Button("Keep this PC's", ToastAction.KeepLocal, n.GameId));
                        b.AddButton(Button("Take server's", ToastAction.TakeServer, n.GameId));
                        b.AddButton(Button("Decide in web UI", ToastAction.OpenWeb, n.GameId));
                        b.SetToastScenario(ToastScenario.Reminder); // stays until acted on
                        break;
                }
                b.Show();
                return;
            }
            catch (Exception ex)
            {
                _toastsBroken = true;
                _log.LogWarning(ex, "Toast failed; falling back to balloons");
            }
        }

        var icon = n.Kind is NotificationKind.Error or NotificationKind.ConflictNeedsDecision ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _tray.ShowBalloonTip(5000, title, n.Message, icon);
    }

    private static ToastButton Button(string text, ToastAction action, string gameId) =>
        new ToastButton().SetContent(text).AddArgument("action", action.ToString()).AddArgument("game", gameId).SetBackgroundActivation();

    /// <summary>Remove our toasts from Action Center (on quit).</summary>
    public static void Cleanup()
    {
        try { ToastNotificationManagerCompat.History.Clear(); } catch { /* best effort */ }
    }
}
