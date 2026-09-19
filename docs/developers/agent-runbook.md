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
3. Before the first native acceptance run, `go run
   ./internal/nativeaccept/cmd/acceptance setup` from `go/` builds the
   private layout below (game copy, bridge root, fixture mod, binaries) and
   prints the first run command. After merging `main` a plain `acceptance
   run` heals a stale mod itself (below); `setup` again is only for a
   layout change. `acceptance doctor -root <root> [-rimgovernor <bin>]`
   checks the environment in a second or two (below). Then keep to the
   run pattern.

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

`acceptance setup` (from `go/`: `go run ./internal/nativeaccept/cmd/acceptance
setup`) makes all of this and is idempotent: it discovers the Steam
RimWorld install, the workshop Harmony and an installed `gabs.exe`
(`.rimgovernor/bridge/gabs/*/gabs.exe` in this worktree, its main
checkout, then its sibling worktrees)
(`-rimworld`, `-harmony`, `-gabs` or `RIMGOVERNOR_RIMWORLD_DIR`,
`RIMGOVERNOR_HARMONY_DLL`, `RIMGOVERNOR_GABS_EXE` override discovery),
skips the mod build when the installed manifest already matches the
worktree's native sources and fixture set, and refuses to install while a
game runs from the copy. `-fixture A,B` narrows the build (every class
`build_native_mod.ps1` accepts by default), `-production` builds without
fixtures (what the storage/food cases need), `-rebuild` forces a build,
`-skip-mod`/`-skip-binaries` leave those parts alone. Keep the worktree
under `.claude/worktrees/`: RimWorld cannot open its own Defs from a copy
whose path passes ~140 characters (`setup` refuses one).

What it produces:

- **The game copy**, `.rimgovernor/native-rimworld/`: the Steam install's
  loose files copied, `Data`, `RimWorldWin64_Data`, `MonoBleedingEdge` and
  `Mods/RimBridgeServer` as NTFS junctions to Steam, and `Mods/RimGovernor`
  from this worktree's own build.
- **The bridge root**, `.rimgovernor/bridge/` (or whatever `-root` you pass):
  `gabs/`, `config/config.json` whose DirectPath target is the private exe,
  `profile/Config/{ModsConfig,Prefs}.xml` (Prefs from the player's own
  RimWorld profile when there is one), `profile/Saves/` (Prepare stages
  the committed `scripts/fixtures/saves/RimGovernor-tribal8-baseline.rws`
  there itself, replacing an older copy).
- **The mod build**, `.rimgovernor/native-builds/<role>-<stamp>/`, from
  `scripts/build_native_mod.ps1`, installed over the copy's
  `Mods/RimGovernor`. Always go through `acceptance setup -rebuild
  [-fixture A,B | -production]`: it supplies the script's three Steam
  paths (`-RimWorldManagedDir`, `-HarmonyAssembly`, `-RimBridgeSdkDir`),
  calls it in-process (`pwsh -File` does not parse the fixture list), and
  refuses to install while a game of yours runs. Do not call the script
  from an agent session: the harness blocks any PowerShell command whose
  arguments contain `C:\Program Files`, which those Steam paths do. The
  build is copied in **only while no game of yours is running**. `Prepare` refuses a stale install before
  boot (`na.RequireCurrentPackage`) and its error names the rebuild
  command; `acceptance run` does the rebuild itself (the heal, below).
  Rebuild whenever `integrations/rimgovernor-native` or
  `scripts/fixtures` changed, including after merging `main`.
- **The controller binaries**, `.rimgovernor/bin/{acceptance,rimgovernor}.exe`
  (a serve-driven case takes `-rimgovernor <path>`). Never rebuild them,
  or the mod, while a case is running from them: the service restart reads
  EOF and the run dies.

## Running a case

- There is one runner, `go/internal/nativeaccept/cmd/acceptance`
  (`list`, `run <area>/<case>...`, `suite`, `stop`, `doctor`, `why`, `prune`). Build it to an exe
  and launch it detached: `Start-Process -WindowStyle Hidden -PassThru`
  with stdout/stderr redirected under `.rimgovernor/`.
- Run outputs are never cleaned up for you and a suite is hundreds of
  megabytes: a worktree with a few weeks of runs under `.rimgovernor/out/`
  holds gigabytes. `acceptance prune -output <abs .rimgovernor/out> [-keep 5]
  [-dry-run]` deletes the oldest run and suite outputs there (and the
  `<name>.log`/`.err` a detached launch wrote beside each), keeps the
  newest `-keep`, and leaves loose files and any directory without a
  `result.json` alone. Runs whose checkpoint ring you still mean to
  `-resume` live under `-root`, not there.
  The tool shell caps a command at ten minutes even in the background, and
  without `-WindowStyle Hidden` a console window opens on the user's
  desktop.
- `acceptance doctor -root <root> [-rimgovernor <bin> -output <dir>]` is
  the preflight (#277): one line per known pitfall with its fix -- the
  root and its game copy (path past ~140 characters), `gabs.exe`, the
  installed mod (present, stale against the worktree), the Core-only
  baseline save, the profile's `ModsConfig.xml` against what the kept
  process launched with, your own leftover game processes, the clock
  journal backlog under a kept process, a private `GOCACHE`, the runner
  and `rimgovernor` binaries against the worktree and `main`, and an
  occupied output directory. It exits non-zero only on a check that would
  certainly fail the run; `run` and `suite` run the same checks first,
  print only the failing ones and refuse on a failure (`-no-doctor` on
  `run` skips it), with one exception: a stale installed mod, or one
  lacking a fixture the run's cases call, is **healed** by `run` (#276)
  rather than refused. The heal stops this root's own kept game (GABS
  `games_stop`, then any leftover pid running from the worktree's private
  copy -- never by image name), rebuilds the mod through `setup` with the
  installed fixture set plus the cases' own, installs it and lets the run
  launch fresh; the checks run again and `result.json` lists the heal
  under `healed` (`stale_mod`, `missing_fixture`, `relaunched`) so a slow
  first run is explained. It only heals a root that launches the
  worktree's own `.rimgovernor/native-rimworld`. `-no-heal` restores the
  refusal; `suite`, the landing gate's form, never heals.
- Pass absolute paths (`-root`, `-output`, `-OutputRoot`): the PowerShell
  tool's working directory follows the last `cd` in the Bash tool.
- Expect 170-250 ticks/s of game time with peers running whatever speed you
  ask for (`RIMGOVERNOR_ACCEPT_CLOCK_SPEED=Ultrafast`); a harness whose
  fixture has to play into its precondition is the slow part, not the box.
- A kept process (`RIMGOVERNOR_ACCEPT_KEEP_GAME`, the default) keeps its
  `ModsConfig`: a save that needs DLC fails `save.missing_mods` on a
  Core-only kept process; `gamesstop` first. A production (fixture-less)
  build cannot be kept and always pays a fresh boot.
- `acceptance warm -root <root>` prepares the profile and boots the game
  to the menu ahead of the first run so it attaches (~0.3s) instead of
  launching (~11s headless); `-background` detaches the boot (log under
  `<root>/acceptance/warm/warm.log`) so a post-build step can fire it
  right after the mod is installed. It refuses a stale package like a
  run does and a mod rebuilt afterwards relaunches on the package check
  (#285).
- Before copying a rebuilt mod in by hand, look for your own leftover
  `RimWorldWin64.exe` from an earlier kept run (command-line filter on the
  worktree path) and stop it by pid; the heal does this for you.
- Read the digest first: a failed case writes `diagnosis` at the top of
  its `result.json` and `diagnosis.txt` beside it (`acceptance why
  <output>/<area>/<case>` reprints it, `-json` for the structure): the run
  binary's revision against `main`, the last native refusals, the routine
  review's refused development rows and selected goals with no method,
  unsuccessful stages, native job failures, authority generation flips
  and pooled-job mismatches, each line naming its file and row (#278).
  It reads `service.sqlite` raw, so an old schema does not stop it.
- Diagnosis before latency: a harness that stalls with policies refusing
  `not_ready`/`StaleFacts` usually has an action prepared under an older
  native generation (`transitions` in `service.sqlite`, read-only), not a
  transport problem; `[worker] ... bridge transport failure` lines repeat a
  handful of real failures (`native_error` rows in the flight recorder).
- A failed case resumes from its last checkpoint on the next `run` of it
  in the same root (first output line says so); pass `-fresh` to start
  over, `-rewind N` to step back. Report a resumed pass as such and land
  on a fresh one: `acceptance suite` and the `cmd/test` hint run fresh.
- A case that declares `Stages` opens on its newest cached stage bundle
  in the root (first output line says so; #329) and skips the staging
  blocks it covers; `-restage` stages again, and a landing suite always
  does. Say `staged_from` in a report the same way as `resumed_from`.
- Iterating on a case's asserts, not its scenario: `acceptance run
  <case> -postmortem-only [-from t+7m] -output <empty dir>` reloads the
  failed bundle on the kept process and runs only the case's
  `Postmortem` phase (#275), ~20 s; a case without one says so.
- Flake or regression: `result.json` `world` names the seed, save hash
  and fixture hash the run had, and `flake` its recent failure share.
  `acceptance run <case> -repeat N` measures the pass rate under one
  build; `-seed <s>` reruns a debug or scenario start on a recorded
  seed (#281). Say which in the issue instead of "reroll the world".
- Report evidence from `result.json`/`report.json` and the retained logs;
  name the cases you ran in the commit message.
- Iterating on a fixture op: `acceptance fixture <op> [k=v ...] -root
  <root>` runs it on the baseline (or `-loaded`, the world the last call
  left) and prints the reply and a world census; no case around it
  (choose-tests, "Stage the precondition").

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
