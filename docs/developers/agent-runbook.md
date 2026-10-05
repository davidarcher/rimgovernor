# Agent runbook

[All docs](../README.md) · [Working agreement](../../AGENTS.md) · [Choosing checks](testing/choose-tests.md)

Machine-level facts a session needs before it touches the game or the build:
what is shared with peer sessions, what is private to a worktree, and the
commands that keep them apart. Several agent sessions share one Windows machine,
each in its own worktree, several with a headless RimWorld running.

Remote runs follow the [maintainer-to-agent handoff](testing/remote-handoff.md)
(publication, dispatch, diagnostics, import, cancellation, bundle rotation).
Clean remote Windows runners use [encrypted bundles and bootstrap](remote-bundles.md):
job-local dependencies, no discovery of Steam, peer worktrees or player
preferences.

## Session start

1. `git fetch origin main && git merge origin/main` once.
2. Go work needs no game and no mod build; `go run ./cmd/test` from `go/` is the
   loop (`-full` once at the end of an epic).
3. Before the first native acceptance run, `go run
   ./internal/nativeaccept/cmd/acceptance setup` from `go/` builds the private
   layout below and prints the first run command. A plain `acceptance run`
   heals a stale mod itself after merging `main`; rerun `setup` only for a
   layout change. `acceptance doctor -root <root> [-rimgovernor <bin>]` checks
   the environment in seconds.

## Shared with peers: never touch these from a task

- **The Go build cache** (`%LOCALAPPDATA%\go-build`). Keep the default
  `GOCACHE`; never `go clean -cache` or set a private one. If a build fails on a
  missing `go-build\..\<hash>-d` file, rerun the command.
- **The Steam RimWorld install** and the shared `.rimgovernor/isolated-rimworld`
  copy. Peers' games run from them; never replace a DLL under either.
- **Running games.** Never kill `RimWorldWin64.exe` by image name (`taskkill
  /IM`, `Stop-Process -Name`); that ends every session's game. Stop your own
  with `acceptance stop -root <root>`; for a stray, select only the pid whose
  command line contains your worktree path
  (`Get-CimInstance Win32_Process | Where-Object CommandLine -like '*<worktree>*'`).
  Removing a worktree without `acceptance stop -root` first orphans its game and
  `rimgovernor.exe`; `acceptance doctor -root <root> -heal` stops every such
  orphan by pid, and a `run`'s preflight does the same unless `-no-heal`.
- **The landing lock** (`.git/rimgovernor-land.lock`). `cmd/land` waits on it;
  remove it by hand only when its recorded pid is gone.
- **`main`'s checkout.** The lane commits there; never edit or integrate in it.

## Private to the worktree

`acceptance setup` (from `go/`) makes all of this and is idempotent. It
discovers the Steam RimWorld install and workshop Harmony (`-rimworld`,
`-harmony`, `RIMGOVERNOR_RIMWORLD_DIR`, `RIMGOVERNOR_HARMONY_DLL` override),
skips the mod build when the installed manifest matches the worktree's native
sources and fixture set, and refuses to install while a game runs from the copy.
`-fixture A,B` narrows the build, `-rebuild` forces it, `-skip-mod` /
`-skip-binaries` leave those parts alone. Keep the worktree under
`.claude/worktrees/`: RimWorld cannot open its Defs from a copy whose path
passes ~140 characters (`setup` refuses one).

- **Game copy**, `.rimgovernor/native-rimworld/`: the Steam install's loose
  files copied; `Data`, `RimWorldWin64_Data` and `MonoBleedingEdge` are NTFS
  junctions to Steam; `Mods/RimGovernor` is this worktree's build (it carries
  the GABP host; no RimBridgeServer install is needed or wanted).
- **Bridge root**, `.rimgovernor/bridge/` (or `-root`): `config/config.json`
  (`games.<id>.target` is the private exe; the running game is recorded in
  `config/<id>/endpoint.json`), `profile/Config/{ModsConfig,Prefs}.xml` (Prefs
  from the player's own profile when present), `profile/Saves/` (the harness
  generates the tribal8 baseline there on first use).
- **Mod build**, `.rimgovernor/native-builds/<role>-<stamp>/`, from
  `scripts/build_native_mod.ps1`, installed over the copy's `Mods/RimGovernor`
  only while no game of yours runs. Always build through `acceptance setup
  -rebuild [-fixture A,B]`: it supplies the script's three Steam paths and
  calls it in-process (`pwsh -File` does not parse the fixture list). Never call
  the script from an agent session: the harness blocks PowerShell commands whose
  arguments contain `C:\Program Files`. `Prepare` refuses a stale install
  (`na.RequireCurrentPackage`) and names the rebuild command; `acceptance run`
  rebuilds itself (the heal). Rebuild whenever `integrations/rimgovernor-native`
  or `scripts/fixtures` changed, including after merging `main`.
- **Controller binaries**, `.rimgovernor/bin/{acceptance,rimgovernor}.exe` (a
  serve-driven case takes `-rimgovernor <path>`). Never rebuild them, or the
  mod, while a case runs from them: the service restart reads EOF and the run
  dies.

## Running a case

- One runner, `go/internal/nativeaccept/cmd/acceptance` (`list`, `run
  <area>/<case>...`, `suite`, `stop`, `doctor`, `why`, `prune`). Build it to an
  exe and launch it detached: `Start-Process -WindowStyle Hidden -PassThru`
  with stdout/stderr redirected under `.rimgovernor/`. The tool shell caps a
  command at ten minutes even in the background, and without `-WindowStyle
  Hidden` a console window opens on the user's desktop. A suite of more than a
  handful of cases (a land tier past ~10 rows, any full tier) only runs detached:
  a killed in-shell attempt orphans its worker games under
  `<output>/workers/<n>`, to be stopped by pid.
- Outputs are never cleaned up for you (a suite is hundreds of MB).
  `acceptance prune -output <abs .rimgovernor/out> [-keep 5] [-dry-run]` deletes
  the oldest run and suite outputs there (and their detached `.log`/`.err`),
  keeping the newest `-keep`; loose files and directories without a
  `result.json` are left alone. Checkpoint rings for `-resume` live under
  `-root`, not there.
- `acceptance doctor -root <root> [-rimgovernor <bin> -output <dir>]` is the
  preflight: one line per known pitfall with its fix (root and game-copy path
  length, installed mod present/stale, baseline save's expansions, `ModsConfig`
  against the kept process, your leftover game processes, clock journal backlog,
  private `GOCACHE`, binaries against the worktree and `main`, occupied output
  directory, orphans, and a boot that never finished: working set under 250 MB
  with a core pegged over five minutes). It exits non-zero only on a check that
  would certainly fail the run. `run` and `suite` run the same checks, print
  only failures and refuse on one (`-no-doctor` on `run` skips). Exception: `run`
  heals a stale installed mod or one lacking a fixture the cases call. It stops
  this root's own kept game (by endpoint pid, then leftover pids from the
  worktree's private copy, never by image name), rebuilds through `setup` with
  the installed fixtures plus the cases' own, installs, and launches fresh;
  `result.json` lists it under `healed` (`stale_mod`, `missing_fixture`,
  `relaunched`, `orphans`). It heals only a root launching the worktree's own
  `.rimgovernor/native-rimworld`. `-no-heal` restores the refusal; `suite`, the
  landing gate's form, never heals.
- Pass absolute paths (`-root`, `-output`, `-OutputRoot`): the PowerShell tool's
  working directory follows the last `cd` in the Bash tool.
- Expect 170-250 ticks/s of game time with peers running
  (`RIMGOVERNOR_ACCEPT_CLOCK_SPEED=Ultrafast`); a fixture that must play into its
  precondition is the slow part, not the box.
- A kept process (`RIMGOVERNOR_ACCEPT_KEEP_GAME`, the default) keeps its
  `ModsConfig`: a DLC save fails `save.missing_mods` on a Core-only kept process;
  `acceptance stop` first. A production (fixture-less) build cannot be kept and
  always pays a fresh boot.
- `acceptance warm -root <root>` prepares the profile and boots to the menu so
  the first run attaches (~0.3 s) instead of launching (~11 s headless);
  `-background` detaches the boot (log `<root>/acceptance/warm/warm.log`). A mod
  rebuilt afterwards relaunches on the package check.
- Before copying a rebuilt mod in by hand, stop your own leftover
  `RimWorldWin64.exe` by pid; the heal does this for you.
- Read the digest first: a failed case writes `diagnosis` at the top of
  `result.json` and `diagnosis.txt` beside it (`acceptance why
  <output>/<area>/<case>`, `-json` for structure): the run binary's revision
  against `main`, last native refusals, refused development rows and selected
  concerns with no method, unsuccessful stages, failed dispatches (native job
  failures, pooled-job mismatches) and authority generation flips and rows,
  each naming its file and row; flight rows are cited `flight.jsonl#<sequence>`
  (`rimgovernor log` prints the same numbers). A launch's `service/stderr.log`
  keeps only the startup banner, fatals and panics: diagnose from the flight
  segments, not from it.
  Failed step receipts name the native tool and separate fixture exceptions,
  blocking attentions (IDs, sample messages) and refusals. It reads
  `service.sqlite` raw, so an old schema does not stop it.
- Diagnose before blaming latency: policies refusing `not_ready`/`StaleFacts`
  usually mean an action prepared under an older native generation
  (`transitions` in `service.sqlite`, read-only), not transport;
  `[worker] ... bridge transport failure` lines repeat a few real failures
  (`native_call` rows with an `error` in the flight recorder).
- A failed case resumes from its last checkpoint on the next `run` in the same
  root (first output line says so); `-fresh` starts over, `-rewind N` steps
  back. Report a resumed pass as such and land on a fresh one: `acceptance
  suite` and the `cmd/test` hint run fresh.
- A case that declares `Stages` opens on its newest cached stage bundle in the
  root (first output line says so) and skips the staging blocks it covers;
  `-restage` stages again and a landing suite always does. Report `staged_from`
  like `resumed_from`. `acceptance suite -cases a,b -stages` schedules each
  case's missing stages as work items (`run -through <stage>`) from bundles
  cached in `-root`, publishes new bundles back and runs the tails in parallel;
  refused with `-tier land` and by `cmd/land -results`.
- Iterating on asserts, not the scenario: `acceptance run <case>
  -postmortem-only [-from t+7m] -output <empty dir>` reloads the failed bundle
  on the kept process and runs only `Postmortem` (~20 s).
- Iterating on planner or policy code a serve-driven case exercises late:
  `acceptance dev <area>/<case> -root <root> [-from t+7m] [-watch]` builds
  `rimgovernor`, reloads the bundle (the ring's next entry by default) on the
  kept process, runs `Run` and `Postmortem` as a resumed run, then waits for
  Enter (or a `.go` change with `-watch`) and repeats. Each iteration writes
  `<output>/dev/<n>/<case>`; the ring, stage cache and metrics series are
  untouched, and a `dev` pass is never a landing pass.
- To look at the colony: `acceptance run <case> -root <root> -break
  stage=<name>|tick=<n>|minute=<m> -headless=false` stops there, bundles into the
  ring and leaves the game loaded, paused and visible (`BREAK`, exit 3).
  `acceptance resume -root <root>` continues; `acceptance stop -root <root>`
  discards. Stage names are the case's declared `Stages`; tick and minute work
  for any case that checkpoints.
- Flake or regression: `result.json` `world` names the seed, save hash and
  fixture hash, and `flake` its recent failure share. `acceptance run <case>
  -repeat N` measures the pass rate under one build; `-seed <s>` reruns a debug
  or scenario start on a recorded seed.
- Report evidence from `result.json`/`report.json` and retained logs; name the
  cases you ran in the commit message.
- `acceptance fixture <op> [k=v ...] -root <root>` runs a fixture op on the
  baseline (or `-loaded`, the world the last call left) and prints the reply and
  a world census (choose-tests, "Stage the precondition").

## Landing

`go run ./cmd/test` from `go/`, then `go run ./cmd/land` from the branch
worktree (`-issue N` names the issue, `-no-close` skips closing), once; then
`git fetch origin main && git push origin main`. On a conflict: `git merge
origin/main`, resolve, commit, land again. A conflict only in a generated
protobuf file: merge, regenerate (`generatego`/`generatecsharp`, absolute
`--output`), commit, land. `rerere.enabled` replays resolutions.

## Vocabulary glossary (epic #1964)

"Goal" meant three things (a kind id, a Standard row, any owner of Methods), so
epic [#1964](https://github.com/davidarcher/rimgovernor/issues/1964) renames it
everywhere. This table is the one source of truth: grep an old word to find the
new one. The rename is complete: code, storage, logs and docs use the new words,
and the "In code" column records which child landed each row.

The governor makes **Rounds**, running an **Inspection** on each **Concern** in
its **Department**. A Concern takes one **Type**: a **Standard** (kept up), a
**Project** (built once) or an **Incident** (handled when it happens).
**Safeguards** veto unsafe Plans, and the chosen **Method** produces a **Plan**.

| Old word | New word | Meaning | In code |
| --- | --- | --- | --- |
| `GoalID` (e.g. `EnsureFoodSupply`) | Concern | A kind the governor watches. The id strings do not change. | done |
| `GoalDetector` | Inspection | Checks one Concern. | done |
| `GoalConcept`, "concept" | Type | Standard, Project or Incident. | done |
| `Domain`, `GoalDomain` | Department | The colony area a Concern serves; groups panels only. | done |
| `RoutineReview`, `RoutineReviewer`, `RoutineReviewResult`, `RoutineNeeds` | Rounds, Rounder, RoundsResult, RoundsFindings | The routine review and its runner, result and findings; the whole `Routine*` family is `Rounds*` (`config.Rounds`, C# `Rounds*Fixture`). The `rounds ran` flight-recorder row and clock event are `rounds_review` (formerly `routine_review`); the SQLite table is `rounds`. | done |
| `Goal` row, `goals` table | Standard | `Open / Settled / Voided`. Table `standards`, blob key `standard/<id>`. | done |
| `Epoch` | Episode | Count of times a Standard went unmet again after settling (0 is the first); Methods are keyed by it. Projects have none. Column and JSON key `episode`. | done |
| `Project` | Project | `Open / Completed / Voided`. | done |
| `Response` concept, `Incident` row | Incident | Both the Type and the row: opens on Active, closes on Clear. "Response" is prose only, for the Methods and Plan chosen for an Incident. | done |
| `Rule` | Safeguard | An admission veto; not a Type. | done |
| `NeedState` (unknown / deficit / recovered) | Finding, Situation | Finding for Standards and Projects: Met / Unmet / Unclear. Situation for Incidents: Active / Clear / Unclear. JSON keys `Finding` / `Situation`. | done |
| `GoalMethod`, `goal_methods` | Method, `methods` | One Method with a single owner (`domain.Method`, field `Owner`); one table per owner (`standard_methods`, `project_methods`, `incident_methods`) plus `plan_owner` and the `plan_methods` view. | done |
| `Goal*` types in `httpapi`, `interpreter`, `spectator`, `colonyreview`, launcher, native panel, player docs | the new words | Player-visible surfaces take the same words (JSON `concern`, `concerns`). | Go JSON and player docs done; native panel keys left to #1983 |

Unchanged: Plan, Method id, Priority, the Concern id strings and the log format
(only message words change). No GABP wire field changes. A guard against the
retired words is tracked in #1981.
