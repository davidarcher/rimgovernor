# Choose checks for a change

[Documentation](../../README.md) · [Development workflow](../development-process.md)

## Testing budget and evidence reuse

Follow the testing pyramid: many fast Go unit tests, fewer boundary integration
tests and a small set of targeted native acceptance harnesses verified against a
real headless RimWorld instance. Keep focused unit tests in the edit loop.
Before a slow check, identify the changed behavior or unresolved failure it
verifies and why cheaper checks cannot establish it.

Run native acceptance at relevant feature milestones, not after every edit.
After an acceptance failure, add a fast regression test where feasible and
rerun the affected harness; expand coverage only when the changed behavior or
failure justifies it. Do not duplicate tests or reviews already supported by
applicable evidence.

## Record a harness run in the commit: the `Verified:` trailer

Whether a passing harness run still applies after a merge is a hash
comparison, not a judgment call. After a harness passes, stamp the commit
that carries the verified change:

```bash
go run ./internal/nativeaccept/cmd/verified trailer upkeepaccept
```

prints `Verified: upkeepaccept inputs=<hash>`; paste that line at the end of
the commit message (one line per harness). The hash covers the working-tree
contents of everything checked in that the run depended on
(`na.HarnessInputs`): every non-test file of each in-module Go package the
harness and `cmd/rimgovernor` import, the native mod's build inputs (the
same list `RequireCurrentPackage` compares), `go.mod`/`go.sum`,
`scripts/fixtures` and `contracts/fixtures`. `verified inputs <harness>`
lists them. The game, GABS and the machine are environment, not inputs.

The landing lane (`go run ./cmd/land`) prints the same comparison before it
squashes the branch, and keeps the newest trailer per harness in the squash
commit. To check by hand:

```bash
go run ./internal/nativeaccept/cmd/verified check
```

reads the trailers in `main..HEAD` (`-range` for another range; name
harnesses to check only those) and reports each harness `ok` when the
current tree's hash matches its newest trailer, otherwise `stale` or
`unrecorded` with exit 1. A merge of `main` that only moved files outside
the inputs keeps every trailer `ok`; that run counts as done and is not
repeated. A `stale` harness is rerun and restamped in the commit that
changed its inputs.

## Stage the precondition, do not play into it

A native acceptance harness should open on a colony that is already in the
state the assertion needs and then advance only the ticks the assertion itself
consumes. Do not start from a baseline/foothold save and let the colony grow,
research, build or starve its way into the precondition: a run that spends
20-30 minutes of game time getting ready is a fixture problem, and it makes
the harness too slow to rerun after a fix. Reach for, in order of preference:

- a prepared `.rws` save committed with the harness's fixture set, opened
  directly by the harness;
- a `test/*_prepare` op in the test fixture mod (see the existing
  `cleanliness_prepare`, `power_prepare`, `refrigeration_prepare`,
  `storage_haul_prepare`, `guarded_construction_prepare` families) that spawns
  the buildings, pawns, items and conditions the test needs in one call;
- `variantsavegen` / `ScenarioStartFixture` for a programmatic scenario start
  when the stressor is map- or start-level (seed, biome, season, scarcity).
  The checked-in artifact is the manifest (a JSON array of
  `variantgen.Variant`), the generated `.rws` under `profile/Saves` is the
  pre-generated world: `variantsavegen -manifest` writes it once offline
  (about 5s a variant on a kept process, the default),
  and `sustainedmatrixaccept -manifest` loads whatever already exists,
  generating only what is missing before any variant runs (`-regenerate`
  forces it). A load takes about 3s; nothing regenerates a world per run.

Budget a targeted harness at minutes. If the precondition is the slow part,
build the fixture before writing the assertion, and review the generated save
once so later runs can trust it.

## Performance guidelines for a new harness

Every harness under `go/internal/nativeaccept/cmd/*` is written against
this checklist; the refactors behind #91 and #92 exist because earlier ones
were not. A reviewer holds a new harness to it.

Items 1-4 are what `na.OpenSession(ctx, cfg, report, start, quiet, keep...)`
does by construction (#137): the stale-package check and profile
preparation, `OpenGame`, discovery, the start (`na.DebugStart{}`,
`na.Save{Name}` or `na.Fixture{Op, Args}` on either), the pause, every
need frozen except `keep`, and the initial identity. The `Session` carries
`Harness`, `Names`, `Identity`, `Prepared` (the fixture reply) and `Report`
(`package_files`, `discovery`, `start`, `quiet`, `prepared`, `frozen_needs`,
`boot_ms`); `Close()` ends the hold and a serve-driven harness uses
`Release()`/`Reattach(ctx)` around the service (`surgeryaccept`,
`wasteaccept` and `lightaccept` are the reference conversions). A new
bridge-only harness starts there and writes only its assertions.

1. **Open on the precondition.** A committed `.rws` or a single
   `test/*_prepare` call (previous section). The run's first assertion-bearing
   tick should come within a minute of the game being ready.
2. **Small map, tiny planet.** Take the default start (200x200, 5%
   planet); pass a larger `na.DebugStart` only when the assertion reasons
   about terrain beyond that, and never hardcode a map size in a fixture.
3. **Core-only unless the test is about DLC.** Do nothing and the profile is
   Core-only; a save-loading harness calls `cfg.UseSaveExpansions(save)` so a
   save's own `<modIds>` decide. Only a DLC-content test sets
   `Config.Expansions` (or `RIMGOVERNOR_ACCEPT_EXPANSIONS`).
4. **Quiet by default.** Start the debug colony with `na.StartDebugGame`
   and pick the mode deliberately: `QuietRequired` when the harness needs a
   fixture build anyway, `QuietIfAvailable` when it must also run on a
   production build, `Loud` only when the assertion is about an interruption.
   `test/configure_start` is quiet unless told otherwise. Freeze the needs
   the scenario is not about with `na.FreezeNeeds`. Pass the discovered
   names as `ScenarioRuntime.Tools` so `AdvanceGame` dismisses the letters it
   acknowledges; reserve `WithExpectedLetters` for interruption windows.
5. **Every wait is stall-bounded.** Poll through `na.WaitProgress` with a
   signature over the thing that must move and `Terminal: service.Exited`
   when a serve subprocess is involved; use the shared `WaitReview`,
   `WaitPlan`, `WaitGoalMethod`, `WaitPlanTerminal` and `WaitRoutineReview`
   where they fit. No bare
   `for { ...; time.Sleep }` loops bounded only by the run timeout, and no
   per-phase ceilings measured in tens of minutes: a ceiling is the safety
   net, the stall budget (`-stall`, default `na.StallBudget()`) is what ends a
   broken run.
6. **Budget in minutes and say so.** `-timeout` defaults reflect a healthy
   run plus margin, not the worst run seen. If the honest default exceeds
   ~15 minutes, the precondition is not staged well enough (item 1) or the
   assertion covers too much; split it.
7. **Advance by ticks, at speed.** A wait for something the game itself
   must do (a haul, a surgery, a pen, a capture) is bounded in ticks, not
   wall clock: `na.RunUntil` runs at `na.RunSpeed` with `na.RunBoost`
   (Ultrafast plus RimWorld's dev tick boost, ~7000 ticks/s measured on the
   debug colony against 348 at Superfast and 168 at Fast; no `devMode`
   pref needed, and that pref adds a 35s def check to every boot), polls
   under a `na.Wait{Ticks: 2*na.TicksPerDay}` budget every 250ms
   (`na.RunInterval`) and pauses again; `na.ObserveCompleted` is the
   receipt-observing form (`receipts_observe_progress` until Completed). A
   tick budget means the same at every speed and on every machine; the
   stall budget still catches a game that stops ticking (a pausing letter)
   and the wall ceiling a run that never finishes. Serve-driven harnesses
   default to `--clock-speed Fast` (`na.ClockSpeedArgs`, override with
   `RIMGOVERNOR_ACCEPT_CLOCK_SPEED`; the clock wire admits Normal, Fast,
   Superfast and Ultrafast, and at Ultrafast the harness also passes
   `--clock-test-acceleration`, the native dev tick boost that only a
   headless `Prepare` launch admits; a rendered profile refuses the window). Their wall time is the controller's cadence, not the
   game's: lightaccept spends ~6s of ~21s of supervised play ticking (two
   windows) and the rest in ~1s scheduler steps of native reads
   plus the worker's 1s-to-10s backoff, so Superfast passes but measures no
   faster (36s vs 31s). The refrigerationaccept "held at tick 1225"
   failure once blamed on Superfast is a stock-in-transit deadlock (#66,
   item 6) that happens at Fast too.
8. **One fixture call, not a script.** Spawn, forbid, damage, assign and
   settle in one `test/*_prepare` op rather than a sequence of production ops
   each paying a bridge round trip; production ops are for the behavior under
   test, not for setup.
9. **Fail fast on terminal signals.** A `ScenarioInterrupted` hold, a plan in
   `Unsuccessful`/`Cancelled`, a serve exit or a missing fixture op ends the
   run at once with the evidence in the report; do not wait out the ceiling
   hoping it recovers.
10. **Prefer reuse over boot.** When several cases share a save, run them
   through `sustainedmatrixaccept -reuse-game` ([below](#reusing-one-game-across-acceptance-cases)) rather
   than booting RimWorld per case. Between harness binaries the process is
   kept by default: each leaves it at the main menu and the next attaches
   to it ([below](#keeping-the-process-between-harnesses)).

## Keep the game quiet and small

Acceptance profiles are Core-only: `nativeaccept.PrepareNativeModConfig`
drops every `ludeon.rimworld.*` expansion from the headless/rendered
`ModsConfig.xml` it generates, because each active expansion adds def loading
and per-tick systems no harness needs unless it tests that DLC. A harness that
does sets `Config.Expansions` (or the run sets
`RIMGOVERNOR_ACCEPT_EXPANSIONS=royalty,biotech`). A save refuses to load
(`save.missing_mods`) under a profile missing an expansion it was recorded
with, so a harness that loads a save calls `cfg.UseSaveExpansions(save)`
before `PrepareConfig`, which activates exactly the expansions in that
save's `<modIds>` header; regenerate saves Core-only (`variantsavegen` now
does) rather than carrying DLC forward.

Fixture games are also quiet by default: `test/configure_start` applies
`test/quiet_storyteller` once the colony exists (pass `quiet=false` to keep
the ordinary storyteller), and harnesses start their debug colony through
`na.StartDebugGame(ctx, h, names, mode)`, which applies the same op per
mode: `QuietRequired` for fixture-dependent harnesses (a missing op is a
stale-mod error; every fixture build carries it), `QuietIfAvailable` for
harnesses that also run against a production build, and `Loud` for
interruption harnesses. Quiet means a Custom difficulty at
zero threat scale with no big/intro threats, violent quests or humanlike
hunting, no queued incidents, no storyteller ticks, and every non-colony pawn
removed from the map; because the Custom difficulty is what the save
persists, a quiet save stays quiet after reload while a fixture build is
installed. Interruption harnesses (`combataccept`, `defenselayoutaccept`,
`movementaccept`, `disconnectaccept`, `test/world_incident` users) stay
`Loud`.

Needs are frozen when the assertion is not about them. `na.FreezeNeeds(ctx,
h, names, keep...)` (`test/freeze_needs`, `FreezeNeedsFixture`, in every
fixture build) pins every free colonist need at maximum after each needs
interval except the NeedDefs in `keep` (`Food` for a cooking harness, `Rest`
for a sleeping one, `Mood`/`Joy` for mood relief), for the current game
only, and the reply names what was frozen: record it on the report so a
pass cannot hide that nobody ever ate or slept. `needsaccept` proves the
pin and the release. The construction harnesses freeze everything; a
harness whose scenario needs a colonist to eat or break keeps that need.

Letters are acknowledged, not fatal. `na.AdvanceGame` used to fail a window
on any pausing letter outside its expected list; it now acknowledges the
informational defs in `na.AcknowledgedLetterDefs` (Neutral/Positive/Negative
events, quests, joiners, rituals, births), dismisses them through
`test/dismiss_letter` (`LetterFixture`, in every fixture build) when the
harness passes its discovered names as `ScenarioRuntime.Tools`, and records
the acknowledgement under the window's interruption. Threat letters still
need `WithExpectedLetters(pairs...)`, and that option also makes the window
strict (no informational acknowledgement), which is what interruption
harnesses want. Only letters whose def pauses under the profile's
`automaticPauseMode` (MajorThreat in the headless profile: ThreatBig only)
ever reach the loop; `letteraccept` covers both modes through
`test/letter_pause_mode` and `test/deliver_letter`. Under the serve process
a letter pause drops authority but only suspends routine goals (#65): the
next enabled review reactivates the same goal with its plans still open, so
a harness following a routine plan sees the same plan resume, not a
successor.

Starts are small by default. `test/configure_debug_start` (in every fixture
build) arms the next quick start with a map size and planet coverage, and
`na.StartDebugGame` uses it whenever it is discoverable: 200x200 on a 5%
planet (`na.DefaultDebugStart`; `RIMGOVERNOR_ACCEPT_MAP_SIZE` and
`RIMGOVERNOR_ACCEPT_PLANET_COVERAGE` override a run), which took the quick
start from 11.5s to 4s. `test/configure_start` takes the same `mapSize` and
`planetCoverage` parameters, `variantsavegen` exposes them as `-map-size` /
`-planet-coverage` and manifest fields (a variant that picks a biome or a
temperature band defaults to a 30% planet, since a 5% planet has no
guaranteed tundra or extreme-desert tile). A harness that reasons about
surrounding terrain calls `na.StartDebugGameSized` with what it needs (150
is the floor, 400 the ceiling); fixtures read `map.Size` rather than
assuming 250.

Bound waits by stall, not only by ceiling. A broken run stops changing long
before its wall-clock budget runs out, so a poll loop goes through
`na.WaitProgress(ctx, na.Wait{Ceiling, Stall, Terminal}, probe)`: the probe
returns a progress signature (`na.Signature(...)` over whatever must move:
plan stages, a goal binding, a method count) and the wait fails once it has
not changed for the stall budget. Leave the game tick out of the signature
unless the wait tolerates a plan that is not moving while the game runs.
`Terminal` fails fast on a signal that nothing can recover from, typically
the serve subprocess having exited (`service.Exited`). The shared
`WaitReview` (a latch or binding on the routine review), `WaitPlan` (a
plan's stages, with `PlanSignature`), `WaitGoalMethod`, `WaitPlanTerminal`
and `WaitRoutineReview` already do this
with `na.StallBudget()` (3 minutes, `RIMGOVERNOR_ACCEPT_STALL` overrides);
harnesses with their own loops take a `-stall` flag defaulting to the same.
The budget was 10 minutes until a sweep of failed runs showed every one of
them spending those ten minutes on an unchanged signature (a 14-minute
defense run held its last signature for 10m1s; a 20-minute hut run for
10m0s): a stall is a broken run, and a wait that legitimately needs the
game to do more than a few minutes of work is bounded in ticks (`Wait.Ticks`,
`RunUntil`), not by a longer stall. Every `result.json` carries
`wait_stats` (`waits`, `stalled`, `max_quiet_ms` with the signature that
held longest, `stall_budget_ms`); a passing run whose `max_quiet_ms`
approaches the budget is the evidence for raising it, or for moving that
wait onto a tick budget.
The headless profile's `Prefs.xml` is the player's copy trimmed by
`na.TrimPrefs` (`HeadlessPrefs`): autosaves effectively off (1000 days;
the interval must stay under ~35791 days or the autosaver's int threshold
overflows and it saves every tick), run in background, the smallest
window, no eye candy, and no ModsConfig reset on crash. Pause preferences
are left alone. That is the last of the #91 speed work.

Passing evidence follows relevant code, dependencies, inputs and environment,
not the main HEAD hash. Unrelated main commits, clean cherry-picks and rebases
do not invalidate it. Inspect the relevant diff and reuse applicable results
across agents; conflict resolution or dependency changes require only the
checks they affect. "The full affected suite" means the applicable automated
suite, not every gameplay scenario. Finish when agreed completion criteria and
relevant checks pass; put unrelated discoveries in the backlog.

## The installed build must match the worktree

`Prepare` and `PrepareRendered` refuse, before GABS or RimWorld start, an
installed `Mods/RimGovernor` whose native sources differ from the worktree
the harness runs from (`na.RequireCurrentPackage`, under a second). A stale
build otherwise fails minutes later and obliquely: a fixture op missing from
discovery, an `INVALID_REQUEST` ProtoJSON refusal, a receipt the Go side no
longer decodes; several one-minute runs were burned on each of those before
anyone suspected the DLL. `scripts/build_native_mod.ps1` records
`sourceTree` in `native-manifest.json`, a hash over exactly the files it
copies into `build/source` (`na.SourceTreeHash` reproduces the list from
the same copy rules, so a dirty tree compares correctly); a build from
before that field falls back to `git diff --quiet <sourceRevision> --
<inputs>`. The refusal names the build's fixtures and the rebuild command;
`RIMGOVERNOR_ACCEPT_ALLOW_STALE_MOD=1` runs against the stale build anyway
(bisecting the mod against newer Go code). The report records the check
under `installed_package` (`checked`, `method`, `fixtures`,
`source_revision`, or `skipped` with the reason: no manifest, no enclosing
checkout, no git). The fixture set is not part of the hash; a harness that
needs an op the build lacks still says so at discovery.

## Reusing one game across acceptance cases

Native acceptance binaries are fresh-process by default: one `games_start`,
the cases, one `games_stop`. That launch is the expensive part (tens of
seconds against a few seconds to reload a paused save), so a binary that
runs several cases in a loop may opt into `nativeaccept.GameReuse`
([reuse.go](../../../go/internal/nativeaccept/reuse.go)): RimWorld is
launched once and each case begins with a reload of its save into the same
process. `sustainedmatrixaccept -reuse-game -saves a,b,c` is the first
consumer; [reuseaccept](../../../go/internal/nativeaccept/cmd/reuseaccept/main.go)
is the acceptance for the lifecycle itself.

Reuse is only valid because every reload is checked against a reset
contract before the case starts (`CheckReset`): a load token never issued
before, the game paused at the baseline tick, no active authority, no owned
draft claim, and the sampled resource census equal to the first load's. The
colony id is not compared -- a fixture save the mod never wrote has no
persisted id, so native mints one per load; for the same reason reloads go
through `rimworld/load_game_ready`, not `lifecycle_load`. A case
that fails, or that ends with authority or a draft still held, retires the
game (`games_stop`) rather than handing it on. Each case gets its own output
directory and, when it launches `rimgovernor serve`, its own SQLite state.

### Keeping the process between harnesses

Every harness opens its game through `na.OpenGame(ctx, cfg)` and ends it
with `held.Close(report)`. A serve-driven harness releases the session
(`held.Release()`) while `rimgovernor serve` owns the sole GABP slot and
reattaches (`held.Reattach(ctx)`) for its postmortem reads; `Close`
reattaches on its own if the harness did not. By default `Close` returns
the game to the main menu (`test/shutdown_unload`, `ShutdownFixture`, in
every fixture build) and leaves the process running; the next `OpenGame`
under the same root finds it (`games_status` `shared-running`), attaches
through `games_start`, unloads whatever is loaded and starts from the menu
like a fresh launch would. Measured on the headless profile: opening a
fresh process takes about 5s, an attach about 0.2s, and the harness still
generates (or loads, below) its own colony. The report records
`game_reuse` (`reused`, `kept`, `openMs`). Stop a kept game with
`gamesstop -root <root>` when you are done with the root; a harness that
fails still leaves the process at the menu, and a harness that dies
without reaching `Close` leaves a game loaded, which the next `OpenGame`
unloads.

`RIMGOVERNOR_ACCEPT_KEEP_GAME=0` opts out (launch and `games_stop` per
harness). Do so for a harness that asserts on mod static state, which is
process-scoped and survives the reuse (`contracts/native-static-state.md`),
or that must observe a first boot; `suiteaccept` forces the keep on for its
workers regardless.

The native clock journal (`ClockEventJournal.cs`, one XML row per event
under the profile's `RimGovernorClockEvents`) belongs to the running
process: its cursor is static state, so `OpenGame` (and `OpenSessionWith`)
clear the journal only right before a fresh `games_start`, never under a
kept process. Rows therefore accumulate for the life of a kept process; a
service attaching later pages them from cursor 1. Wiping the directory
under a live process is what #119 was: every `clock_read_events` refused
cursor continuity, the service's clock inbox died and took its authority
refresh with it (automate mode lost within 2s of every resume). The
native journal now re-creates its directory on the next append and reads
the removed rows as lost, so a wipe costs a gap rather than the process.
`warmauthorityaccept` (below) is the regression: its second phase prepares
the profile again the way a second harness binary would, attaches to the
kept process, pages the journal from cursor 0 and holds a fresh Auto
grant.

### Loading the debug start instead of generating it

By default `StartDebugGame` loads a saved copy of the quick start
(`RimGovernor-debug-<size>-<coverage>[-<dlc>]` in `profile/Saves`,
written by the first start that misses it) instead of generating a world
and map: ~2.7s against ~5.4s on a warm process, surgeryaccept 14s to 10s
on a kept game. The loaded colony is the same one every run rather than
a new world, so a harness that is about world generation or a first-load
identity opts out with `RIMGOVERNOR_ACCEPT_CACHED_START=0`, and the save
must be deleted to pick up a fixture or start change that alters the
colony.

### Running harnesses in parallel

`suiteaccept -root <root> -output <out> -bin <bin> -workers N -harnesses a,b,c`
(or `-suite file.json` with `[{"name", "binary", "args", "acceptance"}]`,
a relative `binary` resolving under `-bin` and `{rimgovernor}` in `args`
replaced by `-rimgovernor`) clones the root into N worker roots
(`na.IsolatedRoot`: own GABS state, config and profile, same game
installation), gives each worker a queue of harnesses chained on one kept
process, and stops every worker's game at the end. The suite's
`result.json` lists each harness's exit, wall time, `game_reuse`,
`acceptance` label and error; it passes only when every harness did.
`cmd/suiteaccept/suites/issue-6-matrix.json` is issue #6's cross-slice
acceptance matrix: one row per criterion in the issue text (dark and
partially lit benches, protected fungus rooms, filthy vs inherently dirty
rooms, kitchen/butcher separation, unreachable stores, disconnected
consumers, exhausted fuel and batteries, hot-weather freezer failure), each
mapped to the harness scenario that exercises it; `suite_test.go` fails
when a criterion loses its row. The mod build for it needs
`PowerFixture RefrigerationFixture CleanlinessFixture LightingFixture
FlooringFixture RoutesFixture`. `-order <earlier result.json>`
starts harnesses longest-first by that run's wall times, so a slow one
does not land last. Measured: six short harnesses on two workers in 68s
unordered, 58s ordered, against about 125s in sequence. The installed mod build
must carry every fixture the list needs, and each harness must fit the
step budget with N-1 peer games running (#73 measured three). A harness
that composes several routine families in one service (`sustainedfoodaccept`
and `sustainedmatrixaccept` run EnsureFoodSupply's whole pipeline by
default) shares one 30s step across all of them, and under three peer games
that step admits nothing: pass `-families <family>` to keep the budget for
the family under test (`farmselectaccept` defaults to `field`), or let the
default `-step-stall 90s` fail the run as soon as the first window has not
been admitted instead of watching an idle service for twenty minutes (#103).

Process reuse carries the same static-state caveat as `-reuse-game`
(next paragraph), and process-wide `Prefs` too: `letteraccept` sets the
pause mode it needs and restores the one it found. Harnesses asserting on
statics or prefs run without the variable.

Reuse does **not** reset mod static state: process-scoped statics such as
`OrderedWorkHistory`, `PlayerFrame`, the `Supervisor` journal and
`PawnConfigTool`'s letter maps survive a reload (see
[native-static-state.md](../../../contracts/native-static-state.md)). Any
case whose assertion depends on one of those, and any case run as static-
state or fresh-Go-session evidence, stays in fresh-process mode. Manifest
variants (`-manifest`) that are missing are generated before the reused game
opens, so `-reuse-game` works in both modes.

## Checkpointing a slow precondition

A harness whose late scenario depends on minutes of earlier play (the
defense layout build before its raid) checkpoints the precondition as a
prepared save instead of replaying it: `defenselayoutaccept -checkpoint
<name>` saves the game once the layout is built and audited, writing
`<name>.rws` and `<name>.checkpoint.json` (the layout record and site the
raid assertions need) to `root/profile/Saves` and to the committed
[scripts/fixtures/saves](../../../scripts/fixtures/saves/). A later
`-from-checkpoint <name>` run stages those files into the root when it
lacks them, loads the save, re-runs the cheap layout audits and goes
straight to the raid; the checkpoint is fixture-mod state, so rebuild it
after fixture or save-format changes.

## Available checks

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Go controller logic, contracts, persistence | From `go/`: `go test ./...`, `go vet ./...`, `go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor` (also run together, with staticcheck and the test-time budget, by `task go:build && task go:test` from the [root Taskfile](../../../Taskfile.yml)) | Pin Go via [go/.go-version](../../../go/.go-version); `CGO_ENABLED=0`. Linux race tests need CGO/GCC. Native control and fresh Go-session recovery have separate behavioral checks below. See [go/README.md](../../../go/README.md). |
| Dashboard behavior and build | `task dashboard:build` runs `pnpm run typecheck`, `pnpm run lint` and `pnpm run build`; `task dashboard:test` runs `pnpm test` (Vitest) | Local pnpm and dashboard dependencies; native UI acceptance is separate. |
| Shared Protobuf contracts | Official C#/Go generation `--check` for both languages | [Generation commands](../../../contracts/schema-generation.md); native adapters additionally need gameplay acceptance. |
| Completed pawn work, recovery or another live-game invariant | A targeted `go/internal/nativeaccept/cmd/*accept` harness (`bedassignaccept`, `billsaccept`, `caravancontrolaccept`, `caravandepartureaccept`, `combataccept`, `constructionaccept`, `developmentaccept`, `draftaccept`, `excavationaccept`, `facilityaccept`, `hospitalaccept`, `husbandryaccept`, `movementaccept`, `questacceptaccept`, `questfulfillaccept`, `recoveryareaaccept`, `recoveryserviceaccept`, `researchaccept`, `roomsaccept`, `settlementgiftaccept`, `sustainedfoodaccept`, `tradeaccept`, `upkeepaccept`, `wallremovalaccept`, `wallupgradeaccept`, `workshopaccept`, `zoneaccept` and others — see the current set under `go/internal/nativeaccept/cmd/`) | Disposable prepared colony, matching native DLLs, GABS and a real headless RimWorld instance. Never replace installed DLLs while any RimWorld instance is running, including another worktree's tests. Isolated tests must restore temporarily swapped DLLs. Never kill `RimWorldWin64.exe`/`gabs.exe` by image name — that ends every concurrent worktree's game (seen there as GABS's catalog emptying, `availableTotal: 0`); stop your own via `games_stop` or kill only pids whose command line contains your `-root`. A receipt alone does not prove pawn work completed — verify the observed postcondition. |
| Reactive clock delivery: a watched construction attempt stops its window at completion (`STOP_REASON_WATCH_LATCHED`) with the outcome and stop on one long-polled page, a baseline window still plays to `STOP_REASON_TICK_BUDGET`, an idle `wait_ms` read is held, and an authority change outside any epoch arrives as an owner-less row | `go run ./internal/nativeaccept/cmd/reactivewatchaccept -root <abs root> -output <fresh dir>` from `go/` (`-speed Normal|Fast|Superfast`, default Superfast) against a `GuardedConstructionFixture` build; reports wake latency, latched tick, ticks saved and window counts in `result.json` | Bridge-driven (no `serve`), Core-only debug start, frozen needs, quiet storyteller; about 2 minutes. Bounds: wake latency (page return minus the stop's `observed_at_unix_ms`) at most 250 ms, stop within one tick of the latched tick and before the deadline. Rerun with `tickbudgetaccept` and `guardedconstructionaccept` when the stop-reason or classification surface changes. |
| Kept process between harnesses: a process that hosted a controller killed with its Auto grant and typed clock epoch still active (session dropped, then unload to the menu) must, for the next controller that prepares the profile again and attaches, still page its clock journal from cursor 0 with no gap and hold a fresh Auto grant for the whole hold window (#119) | `go run ./internal/nativeaccept/cmd/warmauthorityaccept -root <abs root> -output <fresh dir>` from `go/` (`-hold`, default 15s; `-skip-phase1` attaches to a process another harness left at the menu) against any fixture build; `result.json` records both phases, `clock_journal` (`newestCursor`, `lostCount`, `gap`) and `held_reads` | Bridge-driven, two sessions on one process; about a minute after the first start is cached. Rerun when `nativeaccept` profile preparation, `OpenGame`/`OpenSessionWith`, `ClockEventJournal.cs` or the authority hooks change. |
| Whether pawn outcomes and controller throughput hold across clock speeds (Normal, Fast, Superfast, Ultrafast, uncapped = Ultrafast with the headless test acceleration, #109) | `go run ./internal/nativeaccept/cmd/speedmatrixaccept -root <abs root> -output <fresh dir> -rimgovernor <path to rimgovernor.exe>` from `go/` against a `ThroughputFixture` build (`-speeds` to subset, `-ticks` for the per-speed game-tick budget, default 6000; `-rendered` refuses uncapped). One staged colony (`test/throughput_prepare`: three colonists on Construct/Haul, loose Steel with a stockpile, a 6-segment wall run) saved once and reloaded per speed with `-reuse-game` (default on); one `serve` process per speed with the `haul` and `work` families. `report.json` carries per speed wall TPS, paused fraction, steps, reads/step, parent hits, the wall-sized colony window (mean/max ticks, #126), stop latency, budget-vs-reactive stops and budget stops per 6000 ticks, plus the outcome row (stored units, walls built, healthy colonists, unsuccessful plan stages) | Serve-driven, frozen needs, quiet storyteller, stall-bounded waits; about 2 minutes per speed. Fails (non-zero exit) when any outcome differs by more than `-tolerance` (default 1) across speeds, when a case records an unsuccessful plan stage, when nothing was hauled or built, or when `-max-paused-fraction` (Superfast and faster) or `-min-ultrafast-tps-ratio` (Ultrafast vs Fast wall TPS) is set and missed (#126 asks for 0.5 and 2). Rerun when the clock scheduler, step cache, or the Ultrafast/acceleration path changes. |
| Native colony/routine read parity against a retained capture | `go test ./internal/observation -run <TestName> -v` with the matching `RIMGOVERNOR_NATIVE_*_CAPTURE`/`RIMGOVERNOR_NATIVE_*_REFERENCE` environment variable (colony, food forecast, production, naming, power methods, temperature methods, mood — see [go/README.md](../../../go/README.md) for the exact test name and variable per family) | Retained native capture files; verifies decoding/replay parity, not a live game session. |
| Whether the cross-step fact cache is doing its job for a routine family | `routinehaulaccept` or `lightaccept` with `-flight-recorder`: the harness passes `--flight-recorder` to `serve` and writes the phase summary into `result.json` under `phases` (`steps.reads`, `steps.cache_hits`, `steps.parent_hits`); the pawn outcome assertions are unchanged, so a run that passes with `parent_hits > 0` and a lower reads/step than a run without the parent is the acceptance | Read-only diagnostics on top of the ordinary harness; no extra game time. |
| Where bridge call time goes (gate wait, GABS round trip, native main-thread queue wait, native tool execution, receipt decode, ProtoJSON decode) and wall TPS over a session | Run `serve` with `--flight-recorder <abs path>`, then `rimgovernor phases [--json] <abs path>` from `go/` (`go run ./cmd/rimgovernor phases ...`). Rows carry per-call `timing` phases and `native_decode` rows; the sampler aggregates per native tool, with describe (`games_tool_detail`) round trips listed separately (paid once per method per GABS session, not per call); the `cached` column counts reads the scheduler's per-step read cache served without a round trip (`native_cache_hit` rows). Each `ClockScheduler.Step` also publishes a `clock_step` row tallying the round trips it still issued by tool, which the report shows as reads/step (mean, max, per tool) with the step cache's hits and the cross-step `FactCache` parent hits per step; `RIMGOVERNOR_CLOCK_DEBUG=1` prints the same tally with the cache's hit/miss/parent-hit counts per step on stderr (`[clock-scheduler] step reads: ...`) | Read-only over the recorder's retained segments; no game or authority access. The companion reports its own split beside the payload (`{"payload": ..., "timing": {"queueMs", "executeMs"}}`) for every `rimgovernor/*` tool whose single main-thread hop goes through `ProtoBoundary.OnMainThread`; the `queue ms`/`exec ms` columns are means over the calls that carried it and read `-` (absent, not zero) for an older companion or a multi-hop media capture. Sub-millisecond phases can read 0 on Windows' coarse monotonic clock. Wall TPS comes from reply ticks and includes paused time; tick decreases (load, rewind) are excluded as resets. |
| Docker packaging of the Go controller | `docker build -f containers/Dockerfile --target go-controller`, `docker run --rm rimgovernor-go:local version`/`help` | Confirms the image builds and the binary starts without a licensed game; not gameplay evidence. See [go/README.md](../../../go/README.md#go-launch-and-packaging-the-production-default-g0111-g0112). |
| The Go controller as a Linux worker container reaching a connected native session, and the worker storage contract (private volume, export after stop, database integrity, verified cleanup) | `go run ./internal/nativeaccept/docker/cmd/dockerworkeraccept` from `go/` with the Linux game/mods/profile/GABS inputs (`-storage bind` for the host-bind-mount comparison); `go test ./internal/nativeaccept/docker/` covers the stop/export/retain/release branches against a fake `docker` without an engine | Linux Docker engine, licensed Linux game build and prepared profile; one worker, no pawn work. A retained volume in `storage_evidence` is recovery evidence from a failed export, not a leak to sweep. |

For agents: `go run ./cmd/affected` from `go/` prints the checks a change
needs, one command per line: the `go test` line for the packages holding the
changed Go files plus every in-module package importing them (`./...` when
`go.mod`/`go.sum` changed), and one `go run ./internal/nativeaccept/cmd/<harness>`
line per harness whose inputs the change touched (a harness or the `rimgovernor`
binary imports a changed package; every harness when the native sources, fixtures
or `go.mod` changed). It diffs the working tree, including uncommitted and
untracked files, against the merge base with `main` (`-base` for another
revision); pass paths to ask about a hypothetical change. The landing lane runs
the `go test` line itself. Run the harness lines at the milestone, then stamp
the `Verified:` trailer. Run the full affected suite once before handoff. Go-only changes need the full Go
suite; dashboard-only changes need typecheck, Vitest and build. Changes to shared
Protobuf contracts need both plus generation checks. Reuse a successful run when
relevant code, dependencies, inputs and environment are unchanged, even if main has
advanced; do not repeat full suites after documentation-only follow-ups. Report
commands, exit status, skips, artifact locations and what remains unverified. Keep
failed trials. A documentation-only edit normally needs command/flag and link
verification, not a new game session. Do not mark acceptance work complete from
unit tests, compilation or native receipts alone — verify the observed outcome.

Use an isolated task worktree when peers may be active. A worktree does not inherit
the main checkout's build artifacts; run `go test`/`go build` and `pnpm install`
there for local checks.

Native acceptance tooling is Go-only; see
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38) for coverage
gaps, and `go/internal/nativeaccept/cmd/` for the actual current set of
harnesses (issue #38's own description can lag newly landed families — trust the
directory listing over the issue body when they disagree).
