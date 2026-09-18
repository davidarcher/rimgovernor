# Agent runbook

[All docs](../README.md) · [Working agreement](../../AGENTS.md) · [Choosing checks](testing/choose-tests.md)

The machine-level facts a session needs before it touches the game or the
build: what is shared with peer sessions, what is private to a worktree, and
the commands that keep them apart. Several agent sessions run on this one
Windows machine at once, each in its own worktree, several with a headless
RimWorld running.

## Session start

1. `git merge main` once (the branch's `cmd/test` and `cmd/land` come from
   `main`; a branch that predates them has neither).
2. Nothing else until the task needs the game. Go work needs no game and no
   mod build; `go run ./cmd/test` is the loop.
3. Before the first native acceptance run, set up the private game copy and
   build the mod (below), then keep to the run pattern.

## Shared with peers: never touch these from a task

- **The Go build cache** (`%LOCALAPPDATA%\go-build`). Keep the default
  `GOCACHE`; never `go clean -cache` and never point `GOCACHE` at a private
  directory. The cache is what makes a repeated `cmd/test` report
  `(cached)` in seconds. If a build fails on a missing
  `go-build\..\<hash>-d` file, rerun the command: the entry repopulates. (A
  single `go clean -cache` on 2026-09-17 took ten sessions down at once and
  taught each of them the wrong lesson.)
- **The Steam RimWorld install** and the shared `.rimgovernor/isolated-rimworld`
  copy. Peers' games run from them; never replace a DLL under either.
- **Running games**. Never kill `RimWorldWin64.exe` or `gabs.exe` by image
  name (`taskkill /IM`, `Stop-Process -Name`): that ends every session's
  game (it shows up there as GABS's tool catalog going empty,
  `availableTotal: 0`). Stop your own with `gamesstop -root <root>`; for a
  stray, select only the pid whose command line contains your worktree path
  (`Get-CimInstance Win32_Process | Where-Object CommandLine -like '*<worktree>*'`).
- **The landing lock** (`.git/rimgovernor-land.lock`). `cmd/land` waits on
  it; remove it by hand only when its recorded pid is gone.
- **`main`'s checkout**. The lane commits there; never edit or integrate in
  it yourself.

## Private to the worktree

- **The game copy**, `.rimgovernor/native-rimworld/`: the Steam install's
  loose files copied, `Data`, `RimWorldWin64_Data`, `MonoBleedingEdge` and
  `Mods/RimBridgeServer` as NTFS junctions to Steam, and `Mods/RimGovernor`
  from this worktree's own build. Copy the layout from any peer worktree's
  `.rimgovernor/native-rimworld` (`robocopy /XJ` for the files, `New-Item
  -ItemType Junction` for the junctions).
- **The bridge root**, `.rimgovernor/bridge/` (or whatever `-root` you pass):
  `gabs/`, `config/config.json` whose DirectPath target is the private exe,
  `profile/Config/{ModsConfig,Prefs}.xml`, `profile/Saves/` (Prepare stages
  the committed `scripts/fixtures/saves/RimGovernor-tribal8-baseline.rws`
  there itself, replacing an older copy).
  Rewrite the paths in `config.json` after copying.
- **The mod build**, `.rimgovernor/native-builds/<tag>/`, from
  `scripts/build_native_mod.ps1` (called in-process from PowerShell:
  `& scripts/build_native_mod.ps1 -RimWorldManagedDir "<Steam RimWorld>\RimWorldWin64_Data\Managed"
  -HarmonyAssembly "<workshop>\2009463077\Current\Assemblies\0Harmony.dll"
  -RimBridgeSdkDir "<Steam RimWorld>\Mods\RimBridgeServer\1.6\Assemblies"
  -OutputRoot <absolute path> -Fixture @('UpkeepFixture','ShutdownFixture')`;
  `pwsh -File` does not parse the fixture list). Copy its `RimGovernor/`
  over the private copy's `Mods/RimGovernor` **only while no game of yours
  is running**; `Prepare` refuses a stale install before boot
  (`na.RequireCurrentPackage`) and its error names the rebuild command.
  Rebuild whenever `integrations/rimgovernor-native` or `scripts/fixtures`
  changed, including after merging `main`.
- **The controller binary** a serve-driven case drives (`-rimgovernor
  <path>`). Build it to `.rimgovernor/bin/` and never rebuild it, or the
  mod, while a case is running from it: the service restart reads EOF and
  the run dies.

## Running a case

- There is one runner, `go/internal/nativeaccept/cmd/acceptance`
  (`list`, `run <area>/<case>...`, `suite`, `stop`). Build it to an exe
  and launch it detached: `Start-Process -WindowStyle Hidden -PassThru`
  with stdout/stderr redirected under `.rimgovernor/`.
  The tool shell caps a command at ten minutes even in the background, and
  without `-WindowStyle Hidden` a console window opens on the user's
  desktop.
- Pass absolute paths (`-root`, `-output`, `-OutputRoot`): the PowerShell
  tool's working directory follows the last `cd` in the Bash tool.
- Expect 170-250 ticks/s of game time with peers running whatever speed you
  ask for (`RIMGOVERNOR_ACCEPT_CLOCK_SPEED=Ultrafast`); a harness whose
  fixture has to play into its precondition is the slow part, not the box.
- A kept process (`RIMGOVERNOR_ACCEPT_KEEP_GAME`, the default) keeps its
  `ModsConfig`: a save that needs DLC fails `save.missing_mods` on a
  Core-only kept process; `gamesstop` first. A production (fixture-less)
  build cannot be kept and always pays a fresh boot.
- Before copying a rebuilt mod in, look for your own leftover
  `RimWorldWin64.exe` from an earlier kept run (command-line filter on the
  worktree path) and stop it by pid.
- Diagnosis before latency: a harness that stalls with policies refusing
  `not_ready`/`StaleFacts` usually has an action prepared under an older
  native generation (`transitions` in `service.sqlite`, read-only), not a
  transport problem; `[worker] ... bridge transport failure` lines repeat a
  handful of real failures (`native_error` rows in the flight recorder).
- Report evidence from `result.json`/`report.json` and the retained logs;
  name the cases you ran in the commit message.

## Landing

`go run ./cmd/test` from `go/`, then `go run ./cmd/land -F msg.txt` from the
branch worktree. The lane merges `main`, squash-lands on the `main`
checkout, resets the branch and closes the issue named in the branch
(`claude/github-issue-128-…`; `-issue N` to name it, `-no-close` to skip).
Call it once. A conflict comes back as an error: `git merge main`, resolve,
commit, call it again. A conflict only on a generated protobuf file:
merge, regenerate (`generatego`/`generatecsharp`, absolute `--output`),
commit, land. `rerere.enabled` is on, so a resolution replays on later
merges.
