using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace Savepoint.Playnite
{
    /// <summary>
    /// Links Playnite games to Savepoint games and talks to the Savepoint agent over its
    /// named pipe: pulls before a game starts (blocking, with a timeout) and pushes a
    /// session after it stops (DESIGN §6 "Playnite plugin").
    /// </summary>
    public class SavepointPlugin : GenericPlugin
    {
        private static readonly ILogger Log = LogManager.GetLogger();
        private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(10);
        private const string MenuSection = "Savepoint";

        private readonly SavepointSettingsViewModel _settings;
        private readonly AgentClient _agent = new AgentClient();
        private readonly object _cacheLock = new object();
        private List<AgentGame> _gamesCache;
        private DateTime _gamesCachedAt;

        public override Guid Id { get; } = Guid.Parse("5f3c2b8e-6a51-4c8d-9b8e-2f1d7c4a9e10");

        public SavepointPlugin(IPlayniteAPI api) : base(api)
        {
            _settings = new SavepointSettingsViewModel(this);
            Properties = new GenericPluginProperties { HasSettings = true };
        }

        internal SavepointSettings Settings => _settings.Settings;
        internal IPlayniteAPI Api => PlayniteApi;

        public override ISettings GetSettings(bool firstRunSettings) => _settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new SettingsView(this, _settings);

        // ================================================================== Savepoint games

        /// <summary>Games known to the agent, cached briefly. Null if the agent isn't reachable.</summary>
        internal List<AgentGame> GetSavepointGames(bool refresh = false)
        {
            lock (_cacheLock)
            {
                if (!refresh && _gamesCache != null && DateTime.UtcNow - _gamesCachedAt < TimeSpan.FromSeconds(30))
                    return _gamesCache;
            }
            try
            {
                var games = _agent.ListGames(ListTimeout).OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                lock (_cacheLock)
                {
                    _gamesCache = games;
                    _gamesCachedAt = DateTime.UtcNow;
                }
                return games;
            }
            catch (AgentUnavailableException ex)
            {
                Log.Info("Savepoint agent unavailable: " + ex.Message);
                return null;
            }
        }

        internal static IEnumerable<string> ExePaths(Game game) =>
            (game.GameActions ?? Enumerable.Empty<GameAction>())
            .Where(a => a.Type == GameActionType.File && !string.IsNullOrWhiteSpace(a.Path))
            .Select(a => a.Path);

        // ================================================================== launch hooks

        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            var game = args.Game;
            var link = Settings.LinkFor(game.Id);
            if (link == null) return;

            var decision = PullWithProgress(game, link);
            Log.Info($"Pre-launch pull for {game.Name}: {decision.Action} ({decision.Message})");

            switch (decision.Action)
            {
                case LaunchAction.Proceed:
                    return;

                case LaunchAction.ProceedWithNotice:
                    if (Settings.NotifyOnRestore) Notify(game, decision.Message, NotificationType.Info);
                    return;

                case LaunchAction.Unsynced:
                    if (Settings.AskWhenUnsynced)
                    {
                        var answer = PlayniteApi.Dialogs.ShowMessage(decision.Message + "\n\nLaunch anyway?",
                            "Savepoint", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (answer != MessageBoxResult.Yes) args.CancelStartup = true;
                    }
                    else
                    {
                        Notify(game, decision.Message, NotificationType.Error);
                    }
                    return;

                case LaunchAction.AskRestore:
                {
                    var restore = new MessageBoxOption("Restore and play", true, false);
                    var local = new MessageBoxOption("Play with this PC's save", false, false);
                    var cancel = new MessageBoxOption("Cancel", false, true);
                    var choice = PlayniteApi.Dialogs.ShowMessage(decision.Message, "Savepoint",
                        MessageBoxImage.Question, new List<MessageBoxOption> { restore, local, cancel });
                    if (choice == restore) RunAgentAction(game, $"Restoring {game.Name} save…", () => _agent.Restore(link.SavepointGameId, ActionTimeout), args);
                    else if (choice != local) args.CancelStartup = true;
                    return;
                }

                case LaunchAction.AskConflict:
                {
                    var keep = new MessageBoxOption("Keep this PC's save", false, false);
                    var take = new MessageBoxOption("Use the other PC's save", false, false);
                    var later = new MessageBoxOption("Play now, decide later", true, false);
                    var cancel = new MessageBoxOption("Cancel", false, true);
                    var choice = PlayniteApi.Dialogs.ShowMessage(decision.Message, "Savepoint: save conflict",
                        MessageBoxImage.Warning, new List<MessageBoxOption> { keep, take, later, cancel });
                    if (choice == keep) RunAgentAction(game, "Keeping this PC's save…", () => _agent.Resolve(link.SavepointGameId, "keep_local", ActionTimeout), args);
                    else if (choice == take) RunAgentAction(game, "Restoring the other PC's save…", () => _agent.Resolve(link.SavepointGameId, "take_server", ActionTimeout), args);
                    else if (choice != later) args.CancelStartup = true;
                    return;
                }
            }
        }

        private LaunchDecision PullWithProgress(Game game, GameLink link)
        {
            AgentResponse response = null;
            string unavailable = null;
            var wait = TimeSpan.FromSeconds(Math.Max(5, Settings.MaxWaitSeconds));
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                try { response = _agent.Pull(link.SavepointGameId, wait); }
                catch (AgentUnavailableException ex) { unavailable = ex.Message; }
                catch (Exception ex)
                {
                    Log.Error(ex, "Pre-launch pull failed");
                    unavailable = ex.Message;
                }
            }, new GlobalProgressOptions($"Savepoint: checking {game.Name} saves…", false) { IsIndeterminate = true });
            return response == null ? LaunchGate.AgentUnavailable(unavailable) : LaunchGate.Decide(response, game.Name);
        }

        /// <summary>Run a blocking agent call with a progress dialog. If it fails, ask whether
        /// to launch anyway (when <paramref name="launch"/> is given) or show the error.</summary>
        private void RunAgentAction(Game game, string text, Func<AgentResponse> call, OnGameStartingEventArgs launch = null)
        {
            AgentResponse response = null;
            string error = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                try
                {
                    response = call();
                    if (!response.Ok) error = response.Message ?? response.Error;
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Savepoint action failed");
                    error = ex.Message;
                }
            }, new GlobalProgressOptions("Savepoint: " + text, false) { IsIndeterminate = true });

            if (error == null)
            {
                if (launch == null)
                    PlayniteApi.Dialogs.ShowMessage(response?.Message ?? "Done.", "Savepoint");
                return;
            }
            if (launch != null)
            {
                var answer = PlayniteApi.Dialogs.ShowMessage($"Savepoint couldn't finish: {error}\n\nLaunch {game.Name} anyway?",
                    "Savepoint", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) launch.CancelStartup = true;
            }
            else
            {
                PlayniteApi.Dialogs.ShowErrorMessage(error, "Savepoint");
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            var link = Settings.LinkFor(args.Game.Id);
            if (link == null) return;
            var name = args.Game.Name;
            // The agent also pushes when it sees the exe exit; a second push is a cheap no-op.
            Task.Run(() =>
            {
                try
                {
                    var r = _agent.Push(link.SavepointGameId, "session", ActionTimeout);
                    Log.Info($"Post-exit push for {name}: {r.Outcome} {r.Message}");
                }
                catch (AgentUnavailableException ex)
                {
                    Log.Warn($"Post-exit push for {name} skipped: {ex.Message}");
                    Notify(args.Game, "The Savepoint agent isn't running, so this session wasn't backed up.", NotificationType.Error);
                }
            });
        }

        private void Notify(Game game, string text, NotificationType type) =>
            PlayniteApi.Notifications.Add(new NotificationMessage($"savepoint-{game.Id}", "Savepoint: " + text, type));

        // ================================================================== menus

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games.Count > 1)
            {
                yield return new GameMenuItem
                {
                    MenuSection = MenuSection,
                    Description = "Auto-link selected games",
                    Action = a => AutoLink(a.Games, showSummary: true),
                };
                yield break;
            }

            var game = args.Games.FirstOrDefault();
            if (game == null) yield break;
            var link = Settings.LinkFor(game.Id);

            if (link != null)
            {
                yield return new GameMenuItem
                {
                    MenuSection = MenuSection,
                    Description = $"Linked to \"{link.SavepointGameName}\": back up saves now",
                    Action = _ => RunAgentAction(game, $"Backing up {game.Name}…", () => _agent.Push(link.SavepointGameId, "manual", ActionTimeout)),
                };
                yield return new GameMenuItem
                {
                    MenuSection = MenuSection,
                    Description = "Restore latest save from server",
                    Action = _ =>
                    {
                        var ok = PlayniteApi.Dialogs.ShowMessage(
                            $"Replace {game.Name}'s saves on this PC with the latest version from the server?\n\n" +
                            "Your current saves are zipped locally first.",
                            "Savepoint", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                        if (ok == MessageBoxResult.OK)
                            RunAgentAction(game, $"Restoring {game.Name}…", () => _agent.Restore(link.SavepointGameId, ActionTimeout));
                    },
                };
                yield return new GameMenuItem
                {
                    MenuSection = MenuSection,
                    Description = "Unlink",
                    Action = _ => SetLink(game, null),
                };
            }

            var games = GetSavepointGames();
            var linkSection = MenuSection + "|" + (link == null ? "Link to Savepoint game" : "Change linked game");
            if (games == null)
            {
                yield return new GameMenuItem
                {
                    MenuSection = linkSection,
                    Description = "Savepoint agent isn't running",
                    Action = _ => PlayniteApi.Dialogs.ShowMessage(
                        "Start the Savepoint tray app, then try again.", "Savepoint"),
                };
                yield break;
            }
            if (games.Count == 0)
            {
                yield return new GameMenuItem
                {
                    MenuSection = linkSection,
                    Description = "No games in Savepoint yet",
                    Action = _ => PlayniteApi.Dialogs.ShowMessage(
                        "Add the game from the Savepoint tray menu (Add game…) or the web UI first.", "Savepoint"),
                };
                yield break;
            }

            var suggested = LinkMatcher.Suggest(game.Name, ExePaths(game), games);
            if (suggested != null && suggested.Id != link?.SavepointGameId)
            {
                yield return new GameMenuItem
                {
                    MenuSection = linkSection,
                    Description = $"★ {suggested.Name} (suggested)",
                    Action = _ => SetLink(game, suggested),
                };
            }
            foreach (var g in games)
            {
                var target = g;
                yield return new GameMenuItem
                {
                    MenuSection = linkSection,
                    Description = target.Id == link?.SavepointGameId ? $"✓ {target.Name}" : target.Name,
                    Action = _ => SetLink(game, target),
                };
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@Savepoint",
                Description = "Auto-link library to Savepoint games",
                Action = _ => AutoLink(PlayniteApi.Database.Games.ToList(), showSummary: true),
            };
            yield return new MainMenuItem
            {
                MenuSection = "@Savepoint",
                Description = "Settings",
                Action = _ => OpenSettingsView(),
            };
        }

        // ================================================================== linking

        internal void SetLink(Game game, AgentGame target)
        {
            var existing = Settings.LinkFor(game.Id);
            if (existing != null) Settings.Links.Remove(existing);
            if (target != null)
            {
                Settings.Links.Add(new GameLink
                {
                    PlayniteGameId = game.Id,
                    PlayniteGameName = game.Name,
                    SavepointGameId = target.Id,
                    SavepointGameName = target.Name,
                });
            }
            _settings.Save();
        }

        /// <summary>Link every not-yet-linked game that has a confident match. Returns how many were linked.</summary>
        internal int AutoLink(IEnumerable<Game> games, bool showSummary)
        {
            var savepointGames = GetSavepointGames(refresh: true);
            if (savepointGames == null)
            {
                if (showSummary) PlayniteApi.Dialogs.ShowMessage("The Savepoint agent isn't running. Start the tray app and try again.", "Savepoint");
                return 0;
            }
            var linked = new List<string>();
            foreach (var game in games)
            {
                if (Settings.LinkFor(game.Id) != null) continue;
                var match = LinkMatcher.Suggest(game.Name, ExePaths(game), savepointGames);
                if (match == null) continue;
                Settings.Links.Add(new GameLink
                {
                    PlayniteGameId = game.Id,
                    PlayniteGameName = game.Name,
                    SavepointGameId = match.Id,
                    SavepointGameName = match.Name,
                });
                linked.Add($"{game.Name} → {match.Name}");
            }
            if (linked.Count > 0) _settings.Save();
            if (showSummary)
            {
                var text = linked.Count == 0
                    ? "No new matches. Link games by hand from their right-click menu (Savepoint → Link to Savepoint game)."
                    : $"Linked {linked.Count} game(s):\n\n" + string.Join("\n", linked.Take(40)) + (linked.Count > 40 ? "\n…" : "");
                PlayniteApi.Dialogs.ShowMessage(text, "Savepoint");
            }
            return linked.Count;
        }
    }
}
