using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Savepoint.Agent.Services;
using Savepoint.Core.Api;
using Savepoint.Core.Sync;

namespace Savepoint.Agent.UI;

/// <summary>The tray icon, its menu, and the dialogs it opens (DESIGN §6 "Tray UI").</summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly AgentService _agent;
    private readonly IpcServer _ipc;
    private readonly UpdateService _updates;
    private bool _updating;
    private readonly ILogger _log;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly Notifier _notifier;
    private readonly Control _ui = new(); // marshals background events onto the UI thread
    private Form? _openDialog;

    public TrayContext(AgentService agent, IpcServer ipc, UpdateService updates, ILogger log,
        bool startedWithWindows, bool justUpdated)
    {
        _agent = agent;
        _ipc = ipc;
        _updates = updates;
        _log = log;
        _ = _ui.Handle; // create the window now so InvokeRequired/BeginInvoke work from any thread

        _tray = new NotifyIcon
        {
            Icon = TrayIcons.For(agent.Status),
            Text = "Savepoint",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _menu.Opening += (_, _) => BuildMenu();
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowMenuAtCursor();
        };
        _tray.BalloonTipClicked += (_, _) => OpenWeb(null);

        _notifier = new Notifier(_tray, log);
        _notifier.Activated += (_, a) => OnUi(() => _ = HandleToastAsync(a.Action, a.GameId));

        _agent.StatusChanged += (_, _) => OnUi(UpdateStatus);
        _agent.Notification += (_, n) => OnUi(() => _notifier.Show(n));
        _updates.UpdateFound += (_, u) => OnUi(() => _notifier.ShowUpdate(u, _updates.CurrentVersion));
        _updates.Start();
        if (justUpdated)
            _tray.ShowBalloonTip(4000, "Savepoint", $"Updated to {_updates.CurrentVersion}.", ToolTipIcon.Info);

        UpdateStatus();
        _ = StartAsync(startedWithWindows);
    }

    private async Task StartAsync(bool startedWithWindows)
    {
        _ipc.Start();
        await _agent.StartAsync();
        if (!_agent.Config.IsConfigured)
        {
            if (!startedWithWindows) OpenSettings();
            else _tray.ShowBalloonTip(8000, "Savepoint", "Click here or right-click the tray icon to set up the server.", ToolTipIcon.Info);
        }
    }

    private void OnUi(Action action)
    {
        if (_ui.IsDisposed) return;
        if (_ui.InvokeRequired) _ui.BeginInvoke(action);
        else action();
    }

    private void UpdateStatus()
    {
        _tray.Icon = TrayIcons.For(_agent.Status);
        var text = "Savepoint: " + _agent.StatusText;
        _tray.Text = text.Length > 127 ? text[..124] + "…" : text; // NotifyIcon limit
    }

    private void ShowMenuAtCursor()
    {
        // NotifyIcon only shows the context menu on right-click; reuse its private helper for left-click.
        var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        mi?.Invoke(_tray, null);
    }

    // ================================================================== menu

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem($"Savepoint {_updates.CurrentVersion}") { Enabled = false });
        var status = new ToolStripMenuItem(_agent.StatusText) { Enabled = false };
        _menu.Items.Add(status);
        if (_agent.MachineName is not null)
            _menu.Items.Add(new ToolStripMenuItem($"This PC: {_agent.MachineName}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var games = _agent.Games;
        if (games.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("No games yet") { Enabled = false });
        }
        foreach (var game in games.Where(g => !g.SyncEnabled))
        {
            // Listed but inert: syncing is off for this game on this PC.
            var off = new ToolStripMenuItem(game.Name + "  (not on this PC)") { ForeColor = SystemColors.GrayText };
            off.DropDownItems.Add(new ToolStripMenuItem("Not synced on this PC") { Enabled = false });
            off.DropDownItems.Add("Sync on this PC", null, (_, _) => _ = ToggleSync(game, true));
            off.DropDownItems.Add("Open in web UI", null, (_, _) => OpenWeb(game.Id));
            off.DropDownItems.Add(new ToolStripSeparator());
            off.DropDownItems.Add("Edit game…", null, (_, _) => OpenGameEditor(game));
            _menu.Items.Add(off);
        }
        foreach (var game in games.Where(g => g.SyncEnabled))
        {
            var conflict = _agent.ConflictFor(game.Id);
            var running = _agent.IsRunning(game);
            var label = game.Name + (conflict is not null ? "  ⚠" : running ? "  ▶" : "");
            var item = new ToolStripMenuItem(label);
            if (conflict is not null)
            {
                item.DropDownItems.Add(new ToolStripMenuItem("Conflict: both PCs changed this save") { Enabled = false });
                item.DropDownItems.Add("Keep this PC's save", null, (_, _) => _ = RunWithFeedback(game, _agent.ResolveConflictAsync(game, ConflictChoice.KeepLocal)));
                item.DropDownItems.Add("Take the server's save", null, (_, _) => _ = RunWithFeedback(game, _agent.ResolveConflictAsync(game, ConflictChoice.TakeServer)));
                item.DropDownItems.Add("Decide in web UI", null, (_, _) => OpenWeb(game.Id));
                item.DropDownItems.Add(new ToolStripSeparator());
            }
            item.DropDownItems.Add("Back up now", null, (_, _) => _ = RunWithFeedback(game, _agent.BackUpNowAsync(game)));
            var restore = new ToolStripMenuItem("Restore latest", null, (_, _) => ConfirmRestore(game)) { Enabled = !running };
            if (running) restore.ToolTipText = "Close the game first";
            item.DropDownItems.Add(restore);
            item.DropDownItems.Add("Open in web UI", null, (_, _) => OpenWeb(game.Id));
            item.DropDownItems.Add("Open save folder", null, (_, _) => OpenSaveFolder(game));
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add("Edit game…", null, (_, _) => OpenGameEditor(game));
            item.DropDownItems.Add("Stop syncing on this PC", null, (_, _) =>
            {
                var answer = MessageBox.Show(
                    $"Stop syncing {game.Name} on this PC?\n\nThis PC won't back it up or restore it any more. " +
                    "Its saves here and its versions on the server are left as they are.",
                    "Savepoint", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.OK) _ = ToggleSync(game, false);
            });
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Add game…", null, (_, _) => OpenGameEditor(null)) { Enabled = _agent.Config.IsConfigured });
        _menu.Items.Add("Open web UI", null, (_, _) => OpenWeb(null));
        _menu.Items.Add("Settings…", null, (_, _) => OpenSettings());
        var pause = new ToolStripMenuItem("Pause sync") { Checked = _agent.Config.Paused, CheckOnClick = true };
        pause.Click += (_, _) => _agent.SetPaused(pause.Checked);
        _menu.Items.Add(pause);
        _menu.Items.Add("Open log folder", null, (_, _) => OpenPath(_agent.LogDirectory));
        var available = _updates.Available;
        if (available is not null)
        {
            var upd = new ToolStripMenuItem($"Update to {available.Version}…", null, (_, _) => _ = InstallUpdateAsync(available))
            {
                Font = new Font(_menu.Font, FontStyle.Bold),
                Enabled = !_updating,
            };
            _menu.Items.Add(upd);
        }
        else
        {
            _menu.Items.Add(new ToolStripMenuItem("Check for updates", null, (_, _) => _ = CheckForUpdatesAsync()) { Enabled = !_updating });
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Quit", null, (_, _) => Quit());
    }

    private async Task ToggleSync(Game game, bool enabled)
    {
        try
        {
            await _agent.SetSyncEnabledAsync(game, enabled);
            OnUi(() => _tray.ShowBalloonTip(4000, game.Name,
                enabled ? "Now syncing on this PC." : "No longer syncing on this PC.", ToolTipIcon.Info));
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            OnUi(() => _tray.ShowBalloonTip(5000, game.Name, "Couldn't change sync: " + ex.Message, ToolTipIcon.Error));
        }
    }

    private void ConfirmRestore(Game game)
    {
        var answer = MessageBox.Show(
            $"Replace {game.Name}'s saves on this PC with the latest version from the server?\n\n" +
            "Your current saves are zipped locally first, and uploaded as a version if they were never synced.",
            "Restore latest", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.OK) _ = RunWithFeedback(game, _agent.RestoreLatestAsync(game));
    }

    private async Task RunWithFeedback(Game game, Task<SyncResult> task)
    {
        var r = await task;
        var (icon, text) = r.Outcome switch
        {
            SyncOutcome.Pushed => (ToolTipIcon.Info, "Backed up."),
            SyncOutcome.NoChange => (ToolTipIcon.Info, "Already up to date."),
            SyncOutcome.AdoptedHead => (ToolTipIcon.Info, "Already up to date."),
            SyncOutcome.Queued => (ToolTipIcon.Warning, "Server unreachable; the backup is queued."),
            SyncOutcome.SkippedGameRunning => (ToolTipIcon.Warning, r.Message),
            SyncOutcome.Failed or SyncOutcome.Busy or SyncOutcome.Offline => (ToolTipIcon.Error, r.Message),
            _ => (ToolTipIcon.None, ""), // restores and conflicts raise their own notifications
        };
        if (icon != ToolTipIcon.None) OnUi(() => _tray.ShowBalloonTip(4000, game.Name, text, icon));
    }

    private async Task HandleToastAsync(ToastAction action, string gameId)
    {
        var game = _agent.Games.FirstOrDefault(g => g.Id == gameId);
        switch (action)
        {
            case ToastAction.InstallUpdate when _updates.Available is not null:
                await InstallUpdateAsync(_updates.Available);
                break;
            case ToastAction.OpenWeb:
                OpenWeb(string.IsNullOrEmpty(gameId) ? null : gameId);
                break;
            case ToastAction.RestoreNow when game is not null:
                await RunWithFeedback(game, _agent.RestoreLatestAsync(game));
                break;
            case ToastAction.KeepLocal when game is not null:
                await RunWithFeedback(game, _agent.ResolveConflictAsync(game, ConflictChoice.KeepLocal));
                break;
            case ToastAction.TakeServer when game is not null:
                await RunWithFeedback(game, _agent.ResolveConflictAsync(game, ConflictChoice.TakeServer));
                break;
        }
    }

    // ================================================================== dialogs

    private void OpenSettings()
    {
        if (FocusOpenDialog()) return;
        using var form = new SettingsForm(_agent.Config);
        _openDialog = form;
        try
        {
            if (form.ShowDialog() == DialogResult.OK) _ = _agent.ApplyConfigAsync(form.Result);
        }
        finally { _openDialog = null; }
    }

    private void OpenGameEditor(Game? game)
    {
        if (FocusOpenDialog()) return;
        using var form = new GameEditForm(_agent, game);
        _openDialog = form;
        try { form.ShowDialog(); }
        finally { _openDialog = null; }
    }

    private bool FocusOpenDialog()
    {
        if (_openDialog is null || _openDialog.IsDisposed) return false;
        _openDialog.Activate();
        return true;
    }

    private void OpenWeb(string? gameId)
    {
        if (string.IsNullOrWhiteSpace(_agent.ServerUrl)) { OpenSettings(); return; }
        var url = SavepointApi.NormalizeServerUrl(_agent.ServerUrl).TrimEnd('/') + (gameId is null ? "/" : $"/games/{gameId}");
        OpenPath(url);
    }

    private void OpenSaveFolder(Game game)
    {
        var root = game.Roots.FirstOrDefault();
        if (root is null) return;
        var path = _agent.ResolvePreview(root.EffectiveTemplate);
        if (Directory.Exists(path)) OpenPath(path);
        else MessageBox.Show($"The folder doesn't exist yet:\n{path}", game.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenPath(string target)
    {
        try
        {
            var isUrl = target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (!isUrl) Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't open {Target}", target);
        }
    }

    // ================================================================== updates

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updates.CheckAsync();
            if (update is null)
            {
                MessageBox.Show($"You're up to date (Savepoint {_updates.CurrentVersion}).", "Savepoint",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            await InstallUpdateAsync(update);
        }
        catch (Savepoint.Core.Updates.UpdateException ex)
        {
            MessageBox.Show(ex.Message, "Savepoint", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task InstallUpdateAsync(Savepoint.Core.Updates.UpdateInfo update)
    {
        if (_updating) return;
        var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : "\n\n" + Truncate(update.Notes.Trim(), 600);
        if (!UpdateService.CanSelfUpdate)
        {
            var open = MessageBox.Show(
                $"Savepoint {update.Version} is available (you have {_updates.CurrentVersion}).\n\n" +
                "Savepoint can't replace itself in its current folder, so download it from the release page instead?" + notes,
                "Savepoint update", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (open == DialogResult.OK) OpenPath(update.ReleaseUrl);
            return;
        }
        var answer = MessageBox.Show(
            $"Update Savepoint from {_updates.CurrentVersion} to {update.Version}?\n\n" +
            "It downloads the new version, checks it, and restarts. Syncing pauses for a few seconds." + notes,
            "Savepoint update", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        _updating = true;
        try
        {
            _tray.ShowBalloonTip(3000, "Savepoint", $"Downloading Savepoint {update.Version}…", ToolTipIcon.Info);
            var file = await _updates.DownloadAsync(update);
            // Let a running sync finish rather than cutting it off mid-restore.
            for (var i = 0; i < 120 && _agent.Status == AgentStatus.Syncing; i++) await Task.Delay(500);
            _updates.ApplyAndRestart(file);
            Quit();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update failed");
            _updating = false;
            MessageBox.Show($"The update didn't install: {ex.Message}\n\nSavepoint {_updates.CurrentVersion} keeps running.",
                "Savepoint update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void Quit()
    {
        _tray.Visible = false;
        _ipc.Dispose();
        _agent.Dispose();
        Notifier.Cleanup();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _menu.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
