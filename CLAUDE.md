# CLAUDE.md

Guidance for Claude working in this repository.

## What this is

**HMM Revive**: an unofficial fan mod that brings back **Heavy Metal Machines** (Hoplon, servers shut down in 2022)
for private matches among friends, with no dependency on Hoplon's backend ("Swordfish", dead).

- A **headless match server** built at runtime from the game's own shipped server code (the server scene was never
  shipped, so the mod rebuilds it), with bots.
- **Clients connect directly** to it using the game's built-in dev mode (`SkipSwordfish` / `DirectMatch`), usually
  over Tailscale/ZeroTier/LAN.
- A **launcher** (`HMM-Revive.exe`) with a local web UI: host/discover/join lobbies, team/car/skin pick, draft,
  spectators, rematch.

Target game build: Steam `Release.15.00.250` — Unity 2018.4.24f1, Mono, **legacy .NET 3.5 runtime**.
An experimental second target is the **November 2017 build** (Unity 4.6.8, no Zenject) via `HmmRevive.Legacy`.

Read `docs/HOW-IT-WORKS.md` first for the reverse-engineering findings (config system, dev LAN mode, why the server
bootstrap is needed). `CHANGELOG.md` explains recent behavior.

## Legal hard rules (never break)

- **Never commit or ship Hoplon code or game files**: no decompiled code, no game DLLs/assets, no patched DLLs.
  `.gitignore` blocks `*.dll`, `*.exe`, `decompiled/`, `instance*/`, `build/` — don't work around it.
- Decompile with ILSpy (`ilspycmd`) into `decompiled/` (gitignored) for reference only. Don't paste large chunks of
  decompiled code into source files, comments, docs or commit messages.
- The patcher only ever works on a **copy** (`./instance`, hardlinked). The user's Steam install is never written to.
  Never write through a hardlink: delete the destination file, then copy (see `Patcher/Program.cs`).
- `tools/make_friend_kit.sh` runs `tools/check_public.py` (maintainer-only, not in the repo) as a leak check when present.

## Layout

| path | what |
|---|---|
| `mod/HmmRevive/` | The mod DLL (net35) loaded into the Steam build. Compiled against the game's own assemblies (`GameManaged`). |
| `mod/HmmRevive.Legacy/` | Mod for old builds (Unity 4.6.8). `#if Y2016` reserved for the 2016 build (planned). `balance-2017.txt` ships with it. |
| `tools/Patcher/` | net8.0 + **Mono.Cecil**. Hardlinks the game into an instance, copies mod DLLs, IL-patches `Assembly-CSharp-firstpass.dll`. `Legacy.cs` = patch table for old builds. |
| `tools/Launcher/` | net48 console exe `HMM-Revive.exe`: tiny HTTP server (`Http.cs`) serving `web/` (embedded resources) on 127.0.0.1:9690, plus the lobby on TCP/UDP 9697. |
| `tools/Launcher/web/` | Launcher UI: `index.html`, `app.js`, `style.css`, `i18n.js` (English + pt-BR). |
| `tools/friend-kit/` | Scripts/READMEs packaged in the release zip (`setup/host/play/stop .bat/.ps1`, `README.txt`, `LEIA-ME.txt`). |
| `launcher/` | Old .bat/.ps1 host/join menus for running from the repo; `skins.txt` = car/skin catalog (`carId TAB car TAB number TAB skin`). |
| `tools/*.sh`, `tools/run_*.ps1` | Build, smoke test, run server/client, log summary. |

## How the pieces fit

1. **Patcher** (`Patcher <gameDir> <instanceDir> <modDir>`): detects the build by sha256 prefix of the original
   `Assembly-CSharp-firstpass.dll` (`Builds`), hardlinks all files (copies if on another drive), copies mod DLLs into
   `HMM_Data/Managed`, then rewrites the firstpass DLL. First output line is `build: <id>`.
2. **Injection**: `Infra.PreStart.Awake` gets a call to `HmmRevive.Entry.Init()` first. Every other hook is a static
   IL edit in the **patch table** in `tools/Patcher/Program.cs` (`Prologue`, `SkipIf`, and direct call redirects).
   **Harmony/MonoMod do not work on this Mono 2.0 runtime** — don't suggest them.
3. **Mod** (`mod/HmmRevive`):
   - `Entry.cs` — parses `--hmmrevive-*` command-line flags (server mode, headless, car/skin/team, score, bots,
     repair, chaos…), installs test crypto keys, and `Log` (writes `hmmrevive-server-<port>.log` /
     `hmmrevive-client-<pid>.log` next to `HMM.exe`).
   - `Hooks.cs` — static entry points called from patched game methods.
   - `ServerBootstrap.cs` — turns the client `0StarterScene` into Hoplon's unshipped `MainServer` scene (DI context,
     `NetworkClient`→`NetworkServer`, server GameStates, server installers).
   - `CarChoice.cs` (car/skin/team/spectator via the login name `name#car#skin#team`), `CarSwap.cs` (mid-match
     `/car`, `/skin`, chat commands), `Variants.cs` (mod cars: a base car with gadgets from another, e.g. 101 "Stingray W" = Stingray + Wildfire's boost; listed in `launcher/skins.txt` with the base car's skins), `OutOfCombatRepair.cs`, `MatchEnd.cs`, `LocalPrefs.cs` (options saved locally
     instead of Swordfish), `Watchdog.cs`, plus test/diagnostic helpers (`HealScan.cs`, `TestShots.cs`, `UpgradeDump.cs`).
4. **Game config** is overridden on the command line, never by editing game files:
   `HMM.exe BeginConfig [Section] Key=Value ... EndConfig` (e.g. `[Debug] SkipSwordfish=true`, `[Server] IP= Port=`).
5. **Launcher**: `Program.cs` (local API `/api/*`), `Lobby.cs` (host-side lobby, `/lobby/*`, UDP discovery),
   `Session.cs` (client side of a lobby), `Game.cs` (paths, settings, catalog, starting `HMM.exe`, network
   addresses, firewall), `Draft.cs` (ban/pick draft), `Builds.cs` (supported game copies).

## Sync points (change one → change the other)

- Patch table in `tools/Patcher/Program.cs` ⇄ hook signatures in `mod/HmmRevive/Hooks.cs` (and the other mod classes
  it references by name: `CarChoice`, `CarSwap`, `LocalPrefs`). Hooks are looked up **by type and method name** as strings.
- Old builds: `tools/Patcher/Legacy.cs` ⇄ `mod/HmmRevive.Legacy/Hooks.cs`.
- Build hash table: `tools/Patcher/Legacy.cs` (`Builds.Known`) ⇄ `tools/Launcher/Builds.cs` (`Known`).
- Server/client command lines: `tools/run_server.ps1` / `run_client.ps1` ⇄ `tools/Launcher/Game.cs` ⇄ `Entry.cs` flag parsing
  ⇄ `tools/friend-kit/*.ps1`.
- Every user-facing launcher string goes in `web/i18n.js` in **both English and pt-BR**.
- User-facing docs come in pairs: `README.md` ⇄ `README.pt-BR.md`, `friend-kit/README.txt` ⇄ `friend-kit/LEIA-ME.txt`.
- `VERSION` (MAJOR.MINOR) is the single source of truth: baked into all three assemblies via the csproj files. The lobby
  **rejects players on a different version**, so protocol changes need a version bump and a `CHANGELOG.md` entry.

## Build & test

Windows only (Git Bash + PowerShell, .NET 8 SDK). The game must be installed; set `HMM_GAME_DIR` (scripts) /
`GameManaged` (mod csproj) if it isn't in the default Steam folder. Old build: `HMM_2017_DIR` (env or `tools/local.env`).

```bash
tools/build.sh                       # mod + patcher + launcher, refresh ./instance (kills HMM.exe running from it)
HMM_INSTANCE=instance-dev tools/build.sh   # second instance, leaves ./instance alone
tools/test_match.sh 1 150            # smoke test: headless server + 1 hidden/muted client, then prints logs
INSTANCE=instance-dev PORT=9797 tools/test_match.sh ...
tools/serverlog.sh                   # summary: mod log, game log, Unity exceptions
tools/make_friend_kit.sh [preview]   # build/HMM-Revive-friend-kit-<version>[-preview].zip
```

```powershell
tools/run_server.ps1 -Players 2 -SameTeam -RedBots 4 -BluBots 2
tools/run_client.ps1 -Name Player -Ip 100.x.y.z -Car stingray
```

There are no unit tests; verification = build succeeds + `test_match.sh` shows the server listening, clients
connecting and no new errors in `hmmrevive-*.log` / `server_unity_<port>.log`. The server takes ~20–30 s to listen.

Logs (in the instance folder): `hmmrevive-server-<port>.log`, `hmmrevive-client-<pid>.log`,
`server_unity_<port>.log`, `logs/log-*.txt` (game log).

## Code conventions

- Mod code targets **net35** with `LangVersion latest`: no APIs newer than .NET 3.5 (no `Task`/`async`,
  no `string.Join(string, IEnumerable<string>)`, no `Tuple`, etc.). Launcher is **net48** (no NuGet; `System.Web.Extensions`
  for JSON). Patcher is net8.0.
- Server-only behavior must be guarded with `Entry.ServerMode`; clients must stay unchanged otherwise.
- Hooks must never throw into game code: wrap in try/catch and `Log.Error`.
- Use `Log.Info/Error` (mod) and `Program.Log` (launcher), not `Debug.Log`.
- Comments explain *why* and cite the game type/method touched; keep them concise and in English.
- `PathMap` in csproj files keeps the builder's user path out of DLLs — keep it.

## Gotchas

- Paths with accented/non-ASCII letters crash the game's native logger: the kit must live in an ASCII path.
- `-nographics` crashes in `GetDedicatedVideoMemory`; it's skipped via `SkipWhenHeadless`.
- The client also needs the mod: Lidgren busy-waits on a server public key that only Swordfish login set — the mod
  installs Hoplon's test crypto key pair.
- The in-match shop is gone from the shipped build; cars keep base stats.
- Ports: game UDP 9696, lobby TCP+UDP 9697, launcher UI 9690 (localhost only). No real authentication — anyone who
  reaches the ports can join.
- `.claude/settings.local.json` is per-PC and gitignored.
