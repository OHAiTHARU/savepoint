# Savepoint (Windows client)

Tray app that syncs and backs up game saves through a
[savepoint-server](https://github.com/OHAiTHARU/savepoint-server). See
[`docs/DESIGN.md`](docs/DESIGN.md) for the full spec.

| Project | |
|---|---|
| `src/Savepoint.Core` | Cross-platform library: API client, path templates, exclude globs, scanning, local state (SQLite), sync engine |
| `src/Savepoint.Agent` | Windows tray app (WinForms): exe monitor, file watcher, head polling, toasts, dialogs, IPC pipe |
| `tests/Savepoint.Core.Tests` | xUnit tests, including the sync decision table and two-machine scenarios |
| `src/Savepoint.Playnite` | Playnite 10 extension: links games and pulls before launch, pushes after exit |
| `tests/Savepoint.Playnite.Tests` | Tests for the plugin's pipe client, launch decisions and auto-linking |

## Install

1. Download `Savepoint.exe` from the latest [release](https://github.com/OHAiTHARU/savepoint/releases). It's a single self-contained file; put it anywhere, e.g. `%LOCALAPPDATA%\Programs\Savepoint\`.
2. On the server's **Machines** page, create a key for this PC.
3. Run `Savepoint.exe`. Settings opens on first run: enter the server URL and the key, press **Test connection**, then **Save**.
4. Right-click the tray icon → **Add game…**, or add games in the web UI.

## What it does

- **On startup**, it checks every game: it restores newer saves from the server, and uploads changes made while it wasn't running (as `offline` versions).
- **When a game's exe exits**, it uploads a `session` version, then checks for a newer one.
- **When the save folder has been quiet for 3 minutes** (configurable), it uploads a `checkpoint`. A full rescan every 15 minutes catches anything the file watcher missed.
- **Every 60 seconds** it polls the server for new heads (cheap: unchanged polls are a 304). A new head from another PC or the web UI is restored in auto mode, or offered as a toast in manual mode.
- **Offline**, uploads are queued and retried with backoff. A toast appears if the server has been unreachable for over an hour.
- **Conflicts** (both PCs changed the save since the last sync): the local save is always uploaded first, so nothing is lost.
  - In auto mode, the newer side wins. Times are compared in UTC, corrected for this PC's measured clock offset.
  - In manual mode, a toast asks: keep this PC's, take the server's, or decide in the web UI.
- **Before every restore**, it zips the current saves to `%LOCALAPPDATA%\Savepoint\snapshots\` (it keeps the last 3 per game). If they were never synced, it also uploads them as a `pre_restore` version.

Restores never touch a running game. Files matching a game's excludes are never uploaded or deleted.

### Tray menu

- Status line; this PC's name.
- Per game:
  - Back up now
  - Restore latest
  - Open in web UI
  - Open save folder
  - Edit game…
  - Conflict choices, when a conflict is pending
- Add game… · Open web UI · Settings… · Pause sync · Open log folder · Quit.

Icon badge: none = up to date, blue = syncing, grey = offline, amber = needs attention.

### Files

All under `%LOCALAPPDATA%\Savepoint\`:

| Path | |
|---|---|
| `state.db` | Settings (API key encrypted with DPAPI), last-synced state per game, queued uploads |
| `snapshots\<game>\*.zip` | Pre-restore zips |
| `logs\agent-YYYYMMDD.log` | Logs, kept 14 days |

## Playnite extension

1. Download `Savepoint.Playnite.pext` from the same release and open it. Playnite asks to install it.
2. Link games to Savepoint games. Any of these works:
   - **Right-click a game → Savepoint → Link to Savepoint game.** The suggested match is listed first.
   - **Main menu → Extensions → Savepoint → Auto-link library.** This links every unambiguous match, by exe name, then by name.
   - **Add-ons → Extension settings → Savepoint.** This page lists the links; you can change each one with a dropdown or remove it.
3. Play from Playnite. Before a linked game starts, a progress dialog shows while the agent pulls the latest save (up to 45 seconds by default):
   - **In sync, or restored:** the game starts. A restore also shows a notification.
   - **Manual-mode games with a newer save on the server:** you're asked whether to restore it first.
   - **Conflicts:** you're asked which save to play with, or you can choose to decide later.
   - **The agent isn't running, or the server is unreachable:** the game still starts, and a notification warns you. Settings can make it ask you instead.
4. After the game exits, the session is backed up.

The game's right-click menu also has **Back up saves now**, **Restore latest save from server** and **Unlink**.

### IPC

The agent serves `\\.\pipe\savepoint`, using JSON lines, one request per line:

```json
{"cmd":"pull","game_id":"…"}
{"cmd":"push","game_id":"…","type":"session"}
{"cmd":"restore","game_id":"…"}
{"cmd":"resolve","game_id":"…","choice":"keep_local"}
{"cmd":"status"}
{"cmd":"list_games"}
```

`pull` blocks until the pull is done, or until the configured timeout (default 30 s) runs out. Responses look like `{"ok":true,"outcome":"restored","message":"…"}`.

## Develop

Requires the .NET 8 SDK. Core and its tests run on any OS. The tray app and the Playnite extension build anywhere: the tray app uses `EnableWindowsTargeting`, and the extension uses the .NET Framework reference-assemblies package. Both run only on Windows.

```bash
dotnet test tests/Savepoint.Core.Tests
dotnet test tests/Savepoint.Playnite.Tests
dotnet build Savepoint.sln
```

### End-to-end test against a real server

```bash
docker run --rm -p 8080:8080 -e SAVEPOINT_ADMIN_PASSWORD=devpassword -e SAVEPOINT_SECURE_COOKIES=false savepoint-server:local
```

Create two machine keys at http://localhost:8080/machines, then:

```bash
SAVEPOINT_E2E_URL=http://localhost:8080 SAVEPOINT_E2E_KEY_A=sp_… SAVEPOINT_E2E_KEY_B=sp_… dotnet test tests/Savepoint.Core.Tests --filter EndToEnd
```

### Release

Push a `v*` tag. The release workflow runs the tests, publishes a single compressed `Savepoint.exe` (win-x64, self-contained), packages `Savepoint.Playnite.pext`, and attaches both to a GitHub release.
