# Choose checks for a change

[Documentation](../../README.md) · [Development workflow](../development-process.md)

## Testing budget and evidence reuse

Follow the testing pyramid: many fast Go unit tests, fewer boundary integration
tests and a small set of targeted native acceptance cases verified against a
real headless RimWorld instance. Keep focused unit tests in the edit loop.
Before a slow check, identify the changed behavior or unresolved failure it
verifies and why cheaper checks cannot establish it.

Run native acceptance at relevant feature milestones, not after every edit.
After an acceptance failure, add a fast regression test where feasible and
rerun the affected case; expand coverage only when the changed behavior or
failure justifies it. Do not duplicate tests or reviews already supported by
applicable evidence.

Harness logic (the shared waits, `Harness.Call`/`Wire` decoding, the
authority ceremony, discovery and refusal handling) has a check below
acceptance: a run made with `RIMGOVERNOR_ACCEPT_RECORD=<dir>` writes every
bridge call and its raw receipt to `<dir>/transcript.jsonl`
(`bridge.Transcript`, the evidence label as each call's phase), and
`na.ReplayHarness` serves such a transcript back to a `Harness` under `go
test` in milliseconds (`bridge.Replay`). A call the recording never saw,
or one past its end, fails with the recorded row and the argument diff, so
a wait or parser that starts making a different call sequence fails in the
edit loop before a 15-minute case does (#282). The transcripts under
`go/internal/nativeaccept/testdata/transcripts/` are the harness's own
unit fixtures; the game stays the oracle for anything native.

## Which cases a change owes

`go run ./cmd/test` (and `cmd/affected`, which only prints) names the
case areas a change touches before it runs the Go tests: an area whose
own package or the shared runner (`cmd/acceptance`, through its own
imports, not through the areas it registers) imports a changed package,
an area hosting `rimgovernor serve` (a `Serve` spec or `Service` case, a
`ServiceLaunch`) when `cmd/rimgovernor` does, and every area when a shared
input changed (the native mod's build inputs, the same list
`RequireCurrentPackage` compares, and `go.mod`/`go.sum`). A bridge-only
area never runs the binary, so no binary change reaches it, and a
`_test.go` or non-embedded package `testdata` edit builds into no binary,
so it names only its owning package to test and
no area (#361). Embedded inputs use `go list` metadata: production embeds
select their owner, importers and acceptance areas, including deleted files;
test-only embeds select only their owner. A test fixture under `scripts/fixtures` is not shared
(#170): a `<Name>Fixture.cs` affects the areas whose Go sources name one
of its `[Tool("test/...")]` ops (and the fixtures it mentions by class
name), a committed save under `saves/` the area naming it, and the
fixture build files (`.csproj`, `Taskfile.yml`, lock file) every area;
`contracts/fixtures` feeds unit tests only. `na.HarnessInputs` lists a
case's inputs. Selection is per package, not
per symbol: any code edit to a package the runner imports (`cases`,
`clock`, ...) names every area, even an additive one whose zero value
keeps the old path, because nothing cheaper proves that. Two grains are
finer. The routine family (#361): an edit
confined to a family-owned planner file in `internal/buildingruntime`
(`routine_lighting.go`, `routine_flooring.go`, ...; the table is
`routineFamilyFiles` in `internal/affected`) names the serve-hosting areas
whose cases compose that family in their `Families` list, or compose every
family (no list, or `nil`), and no other. The scope widens through use: a
file whose declarations another family's planner uses names that family
too, and one a shared file uses (the scheduler's dispatch excepted) is
every area, as every clock, worker, scheduler, review and boundary file
is. A Go file whose edit changed only comments (compiler directives such
as `//go:build` count as code), or only the clock's debug trace (an `if
clockDebug()` block that traces and nothing else, a `clockSchedulerLog`
call), is not a change at all and names nothing. `cmd/affected -files`
prints, under each area, the changed file and the rule that reached it,
so an unexpected tier is explainable. The harness object (#348): an edit
to `go/internal/nativeaccept/*.go` (the harness package itself, not its
subpackages) is scoped by the declarations it edited against the merge
base, then by every harness declaration whose body uses one, transitively
(a named file given to `cmd/affected` by hand counts whole). An area whose
own sources use a tainted declaration runs whole ("the area uses
na.Session.Advance of wait.go"); an area the change reaches only through
the runner or a helper package every case shares (`cases`, `sustainedfood`,
`setup`...) is **sampled**: it is named, but the land tier runs one case
of it, the cheapest by budget with a bridge-only case before a serve-driven
one, and `-tier full` runs the rest. `cmd/test` lists the sampled areas
under `cases affected`, `acceptance list -tier land` names them on stderr
and the suite's `result.json` records them as `sampled`. A boot-path edit
(`headless.go`, `warm.go`) therefore costs one case per area plus the
smoke set, not the registry. Run the printed
`acceptance suite -tier land` command at the milestone and hand its output
to `cmd/land -results`. It covers the affected areas plus smoke; do not run
the areas separately first. Name the suite in the commit message; an area you judged unaffected and skipped
is "left unverified" below: land and say so in an issue. A run counts for the code it ran against: `main` moving under the
branch afterwards, a clean rebase or a cherry-pick does not invalidate it,
and nothing hashes or grades it. Never enter a second rerun-and-land cycle
for one milestone; land and file an issue for anything left unverified.

## Stage the precondition, do not play into it

A native acceptance case should open on a colony that is already in the
state the assertion needs and then advance only the ticks the assertion itself
consumes. Do not start from a baseline/foothold save and let the colony grow,
research, build or starve its way into the precondition: a run that spends
20-30 minutes of game time getting ready is a fixture problem, and it makes
the case too slow to rerun after a fix. Reach for, in order of preference:

- a prepared `.rws` save committed with the case's fixture set, opened
  directly by the case (`cases.Save`);
- a `test/*_prepare` op in the test fixture mod (see the existing
  `cleanliness_prepare`, `power_prepare`, `refrigeration_prepare`,
  `storage_haul_prepare`, `guarded_construction_prepare` families) that spawns
  the buildings, pawns, items and conditions the test needs in one call;
- the `tools/variantsavegen-<save>` cases / `ScenarioStartFixture` for a
  programmatic scenario start when the stressor is map- or start-level
  (seed, biome, season, scarcity). The checked-in artifact is the manifest
  (`cases/sustained/manifests/issue-1-matrix.json`, a JSON array of
  `variantgen.Variant`), the generated `.rws` under `profile/Saves` is the
  pre-generated world: a `tools/variantsavegen-<save>` case writes it once
  offline (about 5s a variant on a kept process), and the matching
  `sustained/matrix-<save>` case opens on that scenario start directly.
  A load takes about 3s; nothing regenerates a world per run.

Budget a targeted case at minutes. If the precondition is the slow part,
build the fixture before writing the assertion, and review the generated save
once so later runs can trust it.

Develop a fixture op without a case around it: `acceptance fixture
<op> [key=value ...] -root <root>` loads the tribal8 baseline (`-save
<name>` for another save in the profile or a committed checkpoint) into
the root's kept game, calls the op through the same harness the cases
use and prints native's reply as it came (a refusal included, exit 1)
with a census of the world after it (tick, pause, authority, owned
drafts, the non-zero stocks with their forbidden counts: the reset
check's sample). A value that parses as JSON is that value (`x=12`,
`roofed=true`, `cells=[[1,2]]`), anything else a string. The world stays
loaded and paused, so `-loaded` runs the next op on it in a few seconds
instead of reloading; the next `acceptance run` unloads it as it does any
leftover. Evidence lands under `<root>/acceptance/fixture/<op>-<time>`
(`-output`), `-json` prints one object.

## Adding a case

Every native acceptance is a registered `cases.Case` under
`go/internal/nativeaccept/cases/<area>/*.go` (#135), run by the one
binary `go/internal/nativeaccept/cmd/acceptance`: `go run
./internal/nativeaccept/cmd/acceptance list [<area>/...]` names the registry
(`-cost -baseline <suite result.json or metrics.jsonl>` adds each case's
baseline wall and boot time and the set's total, `untimed` for cases the
baseline never ran, so an agent choosing among the cases a change owes
can see that one costs 4 minutes and another 18, #283), and
`acceptance run <area>/<case>... -root <abs root> [-output <dir>]
[-rimgovernor <abs rimgovernor.exe>] [-budget <d> -stall <d> -timeout <d>]
[-fresh] [-rewind N] [-checkpoint-every <d>] [-restage] [-evidence capped|full]
[-repeat N] [-seed <s>] [-postmortem-only [-from <label|dir>]]`
runs cases on one kept process, writing each case's `result.json` under
`<output>/<area>/<case>` beside one evidence file per native call
(`NNNN-<label>.json`) and per service request (`service*/http-NNNN.json`,
numbered in the same run-wide sequence).
Evidence is capped by default (#302): a reply or response over 256 KiB
(the flight recorder's payload cap) is kept as `result_preview` (its
first 256 KiB), `result_bytes`, `result_sha256` and `truncated: true`;
`-evidence full` (or `RIMGOVERNOR_ACCEPT_EVIDENCE=full`) also writes
the untouched row to `<case>/full/`. `result.json` records the mode
under `evidence` and hashes the capped evidence files and every file the
report names under `artifacts` (never the flight ring, service database
or logs); `metrics.evidence_bytes` still sums everything under the case
directory. No case reads an evidence file back; assertions go through
the reply the harness returns. There is no other entry point: a new case is a
new file in an area package (or a new area, imported for its `init()` from
`cmd/acceptance/main.go`) that calls `cases.Register` with a `Name`
(`<area>/<case>`), a `Scope`, a `Start` (`cases.DebugStart{}`,
`cases.Save{Name}`, `cases.Fixture{Op, Args}` on either, `cases.Scenario`
or, for a case that drives the process lifecycle itself, `cases.Owned`),
a `Budget`, and a `Run(ctx, s cases.Session)` that is the assertion only.
The runner owns the preamble every retired per-harness binary used to
repeat: the stale-package check and profile preparation, `OpenGame` on
the root's kept process, discovery, the start, the pause, the frozen
needs, the identity, the fixture reply (`s.Prepared()`), the report
(`s.Report()`: `package_files`, `discovery`, `start`, `world`, `quiet`,
`prepared`, `frozen_needs`, `boot_ms`), the close and the startup-log
check. `world` (#281) is what decided the world the case ran on: the
`seed` (drawn by the runner for a debug start, the scenario's, or the
loaded save's own), the `save` loaded (a cached debug start, a `Save`
start, a resumed checkpoint) with its `save_sha256`, and the fixture op
with `fixture_hash` over its arguments; the native build's hashes stay
under `package_files`. A failure that depends on the roll (#185) is read
against it instead of rerun by hand: `-repeat N` runs each case N times
fresh on the kept process (the checkpoint ring off; attempts after the
first write under `<output>/repeat/<n>/`) and writes
`<output>/<area>/<case>.repeat.json` with the pass rate and each
attempt's world, printing `REPEAT <case> pass=k/N seeds=[...]`, so a
flake shows as a rate under one build; `-seed <s>` pins a debug or
scenario start to a recorded `world.seed` (the tile and the starting
pawns follow the seed natively, and the map follows the world and tile),
which reproduces that run's world fresh. A `Save` start carries its own
world and refuses `-seed`. A pinned debug start caches as its own
`RimGovernor-debug-...-seed-<s>` save, so a repeat under it loads. A serve-driven case declares `Serve: &cases.ServeSpec{...}` and
calls `s.Serve(ctx, s.Spec())` when its in-game setup is done (the
`dialog/pause`, `surgery/queue` and `light/*` cases are the reference
shapes); `s.Reattach(ctx)` takes the slot back for the postmortem reads.
A case that composes the serve lifecycle itself uses `s.Launch`. Every
service a run launches is profiled (#301): the runner passes `--pprof`,
starts a CPU profile for the case's budget at launch and, at the
service's stop, takes a heap snapshot and ends the profile, writing
`cpu.pprof` and `heap.pprof` beside the service's logs
(`<output>/<area>/<case>/service[-N]/`, `go tool pprof <file>`) and
their outcome under the launch's `service[_N].pprof` in `result.json`;
a service that exited first records the capture as skipped, never as a
failure. `RIMGOVERNOR_ACCEPT_PPROF=0` opts out.

The checklist below is what a case is held to. Each item names the runner
default that makes it true by construction or the lint rule
(`cases.Case.Lint`, walked over the whole registry by `go test
./internal/nativeaccept/cmd/acceptance` and applied again before every
run) that refuses a case that departs from it without a `Reason`. The
refactors behind #91 and #92 exist because earlier binaries enforced it
by review alone.

1. **Open on the precondition.** A committed `.rws` or a single
   `test/*_prepare` call (previous section). The run's first assertion-bearing
   tick should come within a minute of the game being ready. *Enforced:*
   `Start` is required (`Validate`), and lint refuses `Serve` on a bare
   `DebugStart`: a serve-driven case opens on a `Save` or a `Fixture`.
2. **Small map, tiny planet.** Take the default start (200x200, 5%
   planet); pass a larger `DebugStart{Size}` only when the assertion reasons
   about terrain beyond that, and never hardcode a map size in a fixture.
   *Enforced:* the zero `DebugStart{}` is the default size; a bigger one
   needs a `Reason` (lint). A construction-heavy case that never reasons
   about terrain sets `DebugStart{Size: na.DebugStart{Flat: true}}` (#272):
   the start settles a flat tile without rivers, roads or tile mutators
   when the planet offers one, cached as its own
   `RimGovernor-debug-...-flat` save.
3. **Core-only unless the test is about DLC.** *Enforced:* the runner's
   profile is Core-only; a `Save` start (and an `Owned` case's `Saves`)
   activates the save's own `<modIds>` through `cfg.UseSaveExpansions`.
   Only a DLC-content test sets `Config.Expansions` (or
   `RIMGOVERNOR_ACCEPT_EXPANSIONS`).
4. **Quiet by default.** *Enforced:* `Quiet` defaults to `QuietRequired`;
   `QuietIfAvailable` (a case that must also run on a production build) and
   `Loud` (an assertion about an interruption) need a `Reason` (lint).
   Every need is frozen before `Run` except the `Keep` list (`Food` for a
   cooking case, `Rest` for a sleeping one), and the report records
   `frozen_needs`. `s.Advance` passes the case's `Letters` as the expected
   interruption letters and the discovered names as `ScenarioRuntime.Tools`,
   so `AdvanceGame` dismisses the letters it acknowledges; a non-nil
   `Letters` (even empty) makes every window strict. A case whose
   assertion never watches the wild map also sets `QuietWorld` (#272):
   under the headless profiles' `-rimgovernor-test-acceleration` launch,
   `test/quiet_world` marks the game (persisted with its saves) so wild
   plants and animals outside the home area and any growing zone stop
   ticking and the wild spawners stop; the report records `quiet_world`.
   Farm, husbandry and hunting cases leave it off. The same launch gate
   also drops the autosaver tick; audio is already off headless.
5. **Every wait is stall-bounded.** Poll through `na.WaitProgress` with a
   signature over the thing that must move and `Terminal: service.Exited`
   when a serve subprocess is involved; use the shared `WaitReview`,
   `WaitPlan`, `WaitGoalMethod`, `WaitPlanTerminal` and `WaitRoutineReview`
   where they fit. No bare `for { ...; time.Sleep }` loops bounded only by
   the run timeout, and no per-phase ceilings measured in tens of minutes:
   a ceiling is the safety net, the stall budget is what ends a broken run.
   *Enforced:* the runner sets the shared stall budget (`-stall`, default
   `na.StallBudget()`) for every wait and records under `wait_stats` how
   many stalled; a wait that bypasses `WaitProgress` shows as a run that
   only ends on `-timeout`.
6. **Budget in minutes and say so.** *Enforced:* `Budget` is required and
   at most `cases.MaxBudget` (15 minutes) without a `Reason` (lint); past
   that the precondition is not staged well enough (item 1) or the
   assertion covers too much, so split it. The runner fails a run that
   passed but took longer than its budget (`budget_exceeded`, "run
   exceeded its budget"), while `-timeout` (default 20 minutes, never less
   than the budget plus 5) stays the safety net. Every `result.json`
   records `started_at`, `finished_at`, `wall_ms`, `budget_ms`, `boot_ms`,
   `ticks_advanced` (the game ticks the case saw pass through its native
   replies) and `wall_tps`, so a slower case shows in its own report and
   the suite's `-baseline` comparison, not in evidence-file mtimes. The
   same numbers, the wait statistics, the native round trips and the
   time-weighted `paused_fraction` (the share of the sampled wall time the
   game stood still between clock windows, #266) of every flight
   recording under the output directory and the evidence size are
   flattened into `metrics` (`na.MetricNames`, #297): the block every
   run appends, with the case, run id (the output directory's name),
   source revision, world seed and timestamp, to the append-only series
   at `-series` (default `<output>/../metrics.jsonl`, shared by the runs
   beside each other; `-no-series` skips it). A metric past its rule
   (`na.DriftRules`: a ratio and an absolute floor over the trailing
   median of the case's last ten earlier passes; `cache_hit_ratio`,
   `wall_tps` and `ticks_advanced` flag a drop) is listed under `drift`,
   never failing the run. The run's `flake` block (`na.FlakeOf`, #281) is
   the share of the case's last ten series runs that failed, whatever
   the reason: a rate strictly between 0 and 1 is a case that passes and
   fails on the same code.
   Every native call a case makes is one evidence row,
   `<output>/<area>/<case>/NNNN-<label>.json`, stamped with `sequence`
   (one stream per case output directory, continued across a reattach
   after a service), `observed_at`, `elapsed_ms` and `tick` when the
   reply carried the game tick. The slice of the game's own log
   (`HeadlessPlayer.log`/`Player.log` under the root, shared by every
   case on a kept process) the case wrote is copied to the case's
   `game.log`, and `result.json` records it under `game_log` (`path`,
   `bytes`, `exceptions`: lines naming an `Exception`, counted, not
   judged).
   A failed run also writes its postmortem digest (`diagnosis`, first in
   `result.json`, and `diagnosis.txt`; `acceptance why <case dir>`
   reprints it) so the diagnosis starts from the evidence, not from five
   open files (#278).
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
   and the wall ceiling a run that never finishes. *Enforced:* `s.Serve`
   always passes `na.ClockSpeedArgs` (`--clock-speed Ultrafast --clock-test-acceleration` by default since #265,
   override with
   `RIMGOVERNOR_ACCEPT_CLOCK_SPEED`; the clock wire admits Normal, Fast,
   Superfast and Ultrafast, and at Ultrafast the runner also passes
   `--clock-test-acceleration`, the native dev tick boost that only an
   acceptance `Prepare`/`PrepareRendered` launch admits; a player launch refuses the window). Their wall time is the controller's cadence, not the
   game's: `light/dark` spends ~6s of ~21s of supervised play ticking (two
   windows) and the rest in ~1s scheduler steps of native reads
   plus the worker's 1s-to-10s backoff, so Superfast passes but measures no
   faster (36s vs 31s). The refrigeration "held at tick 1225"
   failure once blamed on Superfast is a stock-in-transit deadlock (#66,
   item 6) that happens at Fast too.
8. **One fixture call, not a script.** Spawn, forbid, damage, assign and
   settle in one `test/*_prepare` op rather than a sequence of production ops
   each paying a bridge round trip; production ops are for the behavior under
   test, not for setup.
9. **Fail fast on terminal signals.** A `ScenarioInterrupted` hold, a plan in
   `Unsuccessful`/`Cancelled`, a serve exit or a missing fixture op ends the
   run at once with the evidence in the report; do not wait out the ceiling
   hoping it recovers. *Enforced:* the shared waits end on
   `service.Exited`, and the runner stops any service the case launched
   and checks the game's startup log after `Run` whatever it returned.
10. **Prefer reuse over boot.** *Enforced:* the runner keeps the process by
   default (`na.KeepGameEnv`): every run leaves it at the main menu and the
   next attaches to it ([below](#keeping-the-process-between-runs)), so
   several cases through one `acceptance run a b c` invocation
   ([below](#reusing-one-game-across-acceptance-cases)) or an
   `acceptance suite` boot RimWorld once per worker. A case that ends or
   replaces the process declares `NoKeep`; an `Owned` start must.
11. **Split the scenario from its reads.** A serve-driven case declares
   its reads and asserts as `Postmortem` (called after `Run` with the
   services stopped and the harness reattached), keeping `Run` to the
   prepare and the watch; what `Run` learned that the asserts need goes
   through `na.SetCheckpointState` (read back from `Session.Resumed`) or
   `Session.Prior`. That is what lets `-postmortem-only` rerun the asserts
   over the failed bundle in seconds (#275). `production/ladder` and
   `research/ladder` are the shape.

## Keep the game quiet and small

Acceptance profiles are Core-only: `nativeaccept.PrepareNativeModConfig`
drops every `ludeon.rimworld.*` expansion from the headless/rendered
`ModsConfig.xml` it generates, because each active expansion adds def loading
and per-tick systems no case needs unless it tests that DLC, and lists every
expansion the game copy ships in `knownExpansions`: RimWorld activates any
installed expansion it has not seen before at boot and rewrites the file
with it, whatever `activeMods` said (#332). A case that
does sets `Config.Expansions` (or the run sets
`RIMGOVERNOR_ACCEPT_EXPANSIONS=royalty,biotech`). A save refuses to load
(`save.missing_mods`) under a profile missing an expansion it was recorded
with, so a `Save` start activates the save's own expansions (`cfg.UseSaveExpansions`)
before `PrepareConfig`, which activates exactly the expansions in that
save's `<modIds>` header; regenerate saves Core-only (the
`tools/variantsavegen-*` cases do) rather than carrying DLC forward. Every committed save is Core-only
since #192, including the tribal8 baseline (`scripts/fixtures/saves/`,
Lost Tribe, eight colonists, `-seed rimgovernor-tribal-eight-e -biome TemperateForest -map-size 250
-planet-coverage 0.3 -world-temperature LittleBitColder -difficulty
Medium`, quiet); `Prepare`/`PrepareRendered` stage it into
`<root>/profile/Saves` and replace an older copy there, so no root needs a
peer's save. A committed save's planning colony facts must read under 768 KiB
(`na.CheckCommittedSaveHeadroom`): the routine review fails every step once
that read crosses the 1 MiB envelope, and a case that starts from the save
adds buildings and loot to it (#320). The checkpoint generators
(`tools/facility-checkpoint`, `tools/defense-checkpoint`) refuse to commit
past it, and `tools/saveheadroom-<save>` lints each committed save in ten
seconds, reporting the largest sections (`colony_facts` in result.json).

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
and map-gen insect hive removed from the map (#340); because the Custom difficulty is what the save
persists, a quiet save stays quiet after reload while a fixture build is
installed. Interruption harnesses (the `combat/*`, `movement/arrival` and
`authority/disconnect` and `defense/*` cases, `test/world_incident`
users) stay `Loud`; a registered case declares why in `Reason`.

Needs are frozen when the assertion is not about them. `na.FreezeNeeds(ctx,
h, names, keep...)` (`test/freeze_needs`, `FreezeNeedsFixture`, in every
fixture build) pins every free colonist need at maximum after each needs
interval except the NeedDefs in `keep` (`Food` for a cooking case, `Rest`
for a sleeping one, `Mood`/`Joy` for mood relief), for the current game
only, and the reply names what was frozen: record it on the report so a
pass cannot hide that nobody ever ate or slept. The `needs/freeze` case proves the
pin and the release. The construction cases freeze everything; a case
whose scenario needs a colonist to eat or break names that need in
`Keep`. Every serve-driven case freezes too (#131): the runner freezes
before `Run`, so the service never sees a live need the case did not
keep. Each reports `frozen_needs`; a serve-driven run therefore needs a
fixture build.

Letters are acknowledged, not fatal. `na.AdvanceGame` used to fail a window
on any pausing letter outside its expected list; it now acknowledges the
informational defs in `na.AcknowledgedLetterDefs` (Neutral/Positive/Negative
events, quests, joiners, rituals, births), dismisses them through
`test/dismiss_letter` (`LetterFixture`, in every fixture build) when the
case passes its discovered names as `ScenarioRuntime.Tools` (`s.Advance`
does), and records
the acknowledgement under the window's interruption. Threat letters still
need `WithExpectedLetters(pairs...)`, and that option also makes the window
strict (no informational acknowledgement), which is what interruption
cases want (`Letters`). Only letters whose def pauses under the profile's
`automaticPauseMode` (MajorThreat in the headless profile: ThreatBig only)
ever reach the loop; the `letter/pause` case covers both modes through
`test/letter_pause_mode` and `test/deliver_letter`. Under the serve process
a letter pause drops authority but only suspends routine goals (#65): the
next enabled review reactivates the same goal with its plans still open, so
a case following a routine plan sees the same plan resume, not a
successor.

Starts are small by default. `test/configure_debug_start` (in every fixture
build) arms the next quick start with a map size and planet coverage, and
`na.StartDebugGame` uses it whenever it is discoverable: 200x200 on a 5%
planet (`na.DefaultDebugStart`; `RIMGOVERNOR_ACCEPT_MAP_SIZE` and
`RIMGOVERNOR_ACCEPT_PLANET_COVERAGE` override a run), which took the quick
start from 11.5s to 4s. `test/configure_start` takes the same `mapSize` and
`planetCoverage` parameters, the variant manifest exposes them as
`mapSize` / `planetCoverage` fields (a variant that picks a biome or a
temperature band defaults to a 30% planet, since a 5% planet has no
guaranteed tundra or extreme-desert tile). A case that reasons about
surrounding terrain calls `na.StartDebugGameSized` with what it needs (150
is the floor, 400 the ceiling); fixtures read `map.Size` rather than
assuming 250.
A case whose assertion needs a particular kind of map sets
`na.DebugStart.Biomes` (a comma-separated `BiomeDef` preference; the
fixture's `biomes` parameter): the start settles a random valid tile of
the first biome the planet offers and fails when it offers none, and the
cached start is keyed on the preference. storage/food pins a berry-rich
biome this way rather than leaving food to the roll (#172).
Every starting colonist of a configured debug start can Construct and
Haul: the fixture rerolls an incapable pawn in place (#152), so a stage
that needs three such pawns (`test/throughput_prepare`) never depends on
the roll. A cached start saved before that guarantee keeps its pawns;
delete the `RimGovernor-debug-*` save when the stage refuses for it.

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
with `na.StallBudget()` (1 minute; a case whose passing runs hold a
signature longer declares `Case.Stall`, and `RIMGOVERNOR_ACCEPT_STALL` or
the runner's `-stall` overrides both). Across 406 passing rows the longest
quiet span was p90 6s, p99 39s (#353): a stall is a broken run, and a wait
that legitimately needs the game to do more than a minute of work is
bounded in ticks (`Wait.Ticks`, `RunUntil`), not by a longer stall.
`RunUntil` also reads `paused` with every tick probe: a game that stopped
under a running speed is resolved through `home/status` at once, letters
in `AcknowledgedLetterDefs` dismissed and the run resumed, anything else
(a force-pausing window, another letter, a pause with no visible cause)
failing the wait with a `*na.PauseCause` that names it. Every
`result.json` carries `wait_stats` (`waits`, `stalled`, `max_quiet_ms`
with the signature that held longest, `stall_budget_ms`); a passing run
whose `max_quiet_ms` approaches the budget is the evidence for a
`Case.Stall`, or for moving that wait onto a tick budget.
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
the case runs from (`na.RequireCurrentPackage`, under a second). A stale
build otherwise fails minutes later and obliquely: a fixture op missing from
discovery, an `INVALID_REQUEST` ProtoJSON refusal, a receipt the Go side no
longer decodes; several one-minute runs were burned on each of those before
anyone suspected the DLL. `scripts/build_native_mod.ps1` records
`sourceTree` in `native-manifest.json`, a hash over exactly the files it
copies into `build/source` (`na.SourceTreeHash` reproduces the list from
the same copy rules, so a dirty tree compares correctly); a build from
before that field falls back to `git diff --quiet <sourceRevision> --
<inputs>`. The refusal names the rebuild command with the `-Fixture` list a
rebuild for this run needs: the installed build's fixtures plus the ones
under `scripts/fixtures` registering the ops the case's `Fixture` start
calls (`Config.FixtureOps`, `inputs.FixtureClasses`), so following the
hint cannot drop the case's own fixture (#208). `acceptance run` follows
that hint itself: its preflight heals a stale build, or one lacking a
fixture the run's cases call, by stopping the root's own game, rebuilding
through `setup` and reinstalling before the run (`healed` in
`result.json`, #276); `-no-heal` refuses instead, as `suite` always does.
`RIMGOVERNOR_ACCEPT_ALLOW_STALE_MOD=1` runs against the stale build anyway
(bisecting the mod against newer Go code). The report records the check
under `installed_package` (`checked`, `method`, `fixtures`,
`source_revision`, or `skipped` with the reason: no manifest, no enclosing
checkout, no git). The fixture set is not part of the hash; a case that
needs an op the build lacks still says so at discovery.

## Reusing one game across acceptance cases

A RimWorld launch is the expensive part of a run (tens of seconds against
a few seconds to reload a paused save), so the runner boots once per
`acceptance run a b c` invocation and keeps the process between runs
(below). Inside one process, `nativeaccept.GameReuse`
([reuse.go](../../../go/internal/nativeaccept/reuse.go)) is the
lifecycle an `Owned` case that loops over saves drives itself: RimWorld
is launched once and each case begins with a reload of its save into the
same process; the `lifecycle/reuse` case
([cases/lifecycle/reuse.go](../../../go/internal/nativeaccept/cases/lifecycle/reuse.go))
is the acceptance for the lifecycle itself; it launches and stops a
controller per reload, so it takes `-rimgovernor` like every
service-hosting case.

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

### Keeping the process between runs

The runner opens every case's game through `na.OpenSession` (over
`na.OpenGame(ctx, cfg)`) and ends it with the session's `Close`. A
serve-driven case releases the session (`s.Serve` does) while
`rimgovernor serve` owns the sole GABP slot and reattaches
(`s.Reattach(ctx)`) for its postmortem reads; the close reattaches on its
own if the case did not. By default the close returns
the game to the main menu (`test/shutdown_unload`, `ShutdownFixture`, in
every fixture build) and leaves the process running; the next `OpenGame`
under the same root finds it (`games_status` `shared-running`), attaches
through `games_start`, unloads whatever is loaded and starts from the menu
like a fresh launch would. Measured on the headless profile: opening a
fresh process takes about 5s, an attach about 0.2s, and the case still
generates (or loads, below) its own colony. The report records
`game_reuse` (`reused`, `kept`, `openMs`, and `relaunched` when the
process was not reused). A process runs with the `ModsConfig.xml` it was
launched with, so a fresh launch snapshots the prepared file to the
profile's `RimGovernorLaunchedMods.xml` and `OpenGame` compares it to
what the current `PrepareConfig` wrote: a different load order (a
Core-only process facing a case whose `Save` start activated DLC, which
would fail `save.missing_mods` at once, or the reverse) stops the kept
process and launches fresh, `relaunched: "expansions"`; a process with no
snapshot (a hand launch) relaunches as `"unrecorded"` (#166). The same
launch snapshots the installed package's file hashes to
`RimGovernorLaunchedPackage.json`: a process serves the DLLs it loaded,
so a rebuilt `Mods/RimGovernor` installed under a kept process (new
fixtures, say) relaunches as `"package"` instead of failing discovery
against the old catalog (#209). `acceptance warm -root <root>`
(`-background` to detach) boots that kept process ahead of the first
run, recording the same snapshots, so the run attaches (#285). Stop a kept
game with `acceptance stop -root <root>` when you are done with the root
(a case that must not hand its process on declares `NoKeep` and the
runner stops it itself); a case that fails still leaves the process at
the menu, and a run that dies without reaching the close leaves a game
loaded, which the next `OpenGame` unloads.

`RIMGOVERNOR_ACCEPT_KEEP_GAME=0` opts out (launch and `games_stop` per
run). Do so for a case that asserts on mod static state, which is
process-scoped and survives the reuse (`contracts/native-static-state.md`),
or that must observe a first boot; `NoKeep` is the case's own way to say
so, and `acceptance suite` forces the keep on for its workers regardless.

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
the removed rows as lost, so a wipe costs a gap rather than the process. A
retained row whose file is present but no longer decodes (truncated or
overwritten on disk) reads the same way: one lost cursor on the page that
crosses it, listed under `home/runtime_health` `journal.corruptRows`, and
the journal keeps appending after it. `lifecycle/runtime-fault` injects
both faults (`RuntimeFaultFixture`: `test/runtime_fault_unpatch`,
`test/runtime_fault_corrupt_row`) and asserts the recovery.
`smoke/dispatch` (#227) is the transport regression: `home/runtime_health`
reports `extensionDispatch` installed with every discovered companion tool
rewrapped by `ExtensionDispatchPatch` (RimBridgeServer 2.1.1 registers
companion tools with Lib.GAB as synchronous handlers on the GABP reader,
so a held read blocked every later call, #115), and an identity read
issued under a 4s held `clock_read_events` returns in a round trip (60ms
measured; 3.8s on the unpatched host). About 15s on a kept process.
The `authority/warm` case (below) is the regression: its second phase
prepares the profile again the way a second run would, attaches to the
kept process, pages the journal from cursor 0 and holds a fresh Auto
grant.

### Loading the debug start instead of generating it

By default `StartDebugGame` loads a saved copy of the quick start
(`RimGovernor-debug-<size>-<coverage>[-<dlc>][-<biomes>]` in `profile/Saves`,
written by the first start that misses it) instead of generating a world
and map: ~2.7s against ~5.4s on a warm process, surgery/queue 14s to 10s
on a kept game. The loaded colony is the same one every run rather than
a new world, so a case that is about world generation or a first-load
identity opts out with `RIMGOVERNOR_ACCEPT_CACHED_START=0`, and the save
must be deleted to pick up a fixture or start change that alters the
colony.

### Running cases in parallel

`acceptance suite (-all | -cases a,b | -suite file.json | -tier
land|full|matrix|smoke) -root <root>
-output <out> -workers N [-baseline <result.json> -series <metrics.jsonl>
-rimgovernor <bin> -evidence capped|full]`
(`go/internal/nativeaccept/cmd/acceptance`) clones the root into N
worker roots (`na.IsolatedRoot`: own GABS state, config and profile, same
game installation), gives each worker a queue of cases chained on one kept
process, and stops every worker's game at the end. `-all` and `-cases`
name registry cases; a `-suite` file (`[{"name", "acceptance"}]`) lists
registry cases with the criterion each stands for; `-tier` names one of
the three tiers below (the report records `tier`). The queue puts
bridge-only cases first, cases that end or replace the process (`NoKeep`,
`Rendered`) next and serve-driven ones (`Serve` or `Service`) last, so no
bridge-only case inherits a process that hosted a service (#119); within
each tier it runs longest-first by the `-baseline` suite's wall times
(untimed cases first). The suite's `result.json` lists each case's
worker, exit, `wall_ms`, `boot_ms`,
`game_reuse`, `acceptance` label and error, the sum of case wall times
beside the baseline's, and `regressions`: every case whose run time
(`wall_ms` net of `boot_ms`, so which worker paid the game boot does not
count) is both 25% and 5s over its baseline row's (flagged, never failing
on its own; #176). Every row also carries its `metrics` block and
`drift` flags, and the suite report lists all flags under `drift` (the
rows append to the same series, `-series`, passed through to each run),
its `world` block and its `flake` record (#281). A suite used as
`-baseline` hands the flake record on: a regression row carries
`baseline_flake` and prints it beside the ratio (`a 1.50x (baseline
flake 30%)`), and a failed row whose record has failures prints as a
known flake, so neither is read as a regression without a look at the
seed. The suite passes only when every case did.
#### Tiers

The registry runs in three tiers (#273), so a landing runs a fraction of
it and the rest runs on its own cadence; `acceptance list -tier <name>`
prints a tier and `-cost -baseline <result.json|metrics.jsonl>` prices it:

- **land** (`suite -tier land [-base main]`): the case areas
  `cmd/affected` selects for the worktree's diff plus the smoke set, fresh,
  in the landing lane; an area a harness edit reaches through shared
  plumbing alone contributes one case (sampled, #348, above). `cmd/test`
  prints the command; `cmd/land -results
  <output>` reads the suite's `result.json` and refuses a suite that did
  not pass or whose rows resumed from a checkpoint (#308). A diff under
  the native mod sources (`na.HarnessInputRoots`) or
  `go/internal/buildingruntime` does not land without `-results`;
  `-unverified` lands it anyway, and the issue names what went unverified.
- **full** (`suite -tier full`): every case outside the matrix tier, the
  nightly loop against `main`, chained with `-baseline` for regression
  flagging.
- **matrix** (`suite -tier matrix`): the cases that declare
  `Case.Matrix` — `speedmatrix/`, `tickbudget/` and any DLC-save case — on
  demand and whenever the clock scheduler or the native tick path changes.
  Neither land nor full runs them.
- **smoke** (`suite -tier smoke`): the land tier's fixed half alone,
  `cmd/acceptance/suites/smoke.json`: runner-proving bridge-only cases over
  a kept debug game plus one short serve-driven case (`light/dark`, so the
  build carries `LightingFixture`); every row runs on any fixture build.
  `TestSmokeSuiteShape` holds it to that shape (one serve-driven row, no
  `NoKeep`, `Rendered` or matrix case, budgets within 5m); extend it with a
  case that proves a runner path the others miss, not one per area.

`cmd/acceptance/suites/issue-6-matrix.json` is issue #6's cross-slice
acceptance matrix: one row per criterion in the issue text (dark and
partially lit benches, protected fungus rooms, lighting repair after a
layout change (#161), filthy vs inherently dirty
rooms, kitchen/butcher separation, unreachable stores, disconnected
consumers, exhausted fuel and batteries, hot-weather freezer failure), each
mapped to the case that exercises it; `suite_test.go` fails
when a criterion loses its row. The mod build for it needs
`PowerFixture RefrigerationFixture CleanlinessFixture LightingFixture
FlooringFixture RoutesFixture`. Measured: six short cases on two
workers in 68s unordered, 58s ordered, against about 125s in sequence;
the same six plus `smoke/identity` through `acceptance suite` in 47s. The
installed mod build must carry every fixture the list needs, and each
case must fit the step budget with N-1 peer games running (#73 measured
three). A case that composes several routine families in one service
(`sustained/food` and `sustained/matrix-*` run EnsureFoodSupply's
whole pipeline by default) shares one 30s step across all of them, and
under three peer games that step admits nothing: pass `-families <family>`
to keep the budget for the family under test (`farm/select-*` declare
`field` alone), or let the default `-step-stall 90s` fail the run as soon as
the first window has not been admitted instead of watching an idle service
for twenty minutes (#103). The watch itself is a tick window, not a flat
wall-clock length (#133): the `sustained/matrix-*` cases watch 2500
ticks (one in-game hour) per variant so the ten-variant matrix is a
regression gate, the wall-clock ceiling (`RIMGOVERNOR_ACCEPT_WINDOW`, a Go
duration, default 8m) only ends a game that stops advancing, and
`sustained/food` watches the whole wall-clock window as the diagnostic
timeline. Each case's `result.json` records the observed window under
`window` and the ceiling under `window_ms`. Every timeline sample also
carries a `colony` block read from the service's `/api/player/colony`
census (food nutrition and runway days, colonists, downed, mood mean, the
roster), and `result.json` summarizes them under `colony_outcome` (minimum
food runway and the tick it was seen at, first and final colonist counts,
`colonists_lost`, worst downed count and mood mean), so a sustained run
that starved its colonists is judged from the result rather than
reconstructed from the flight recorder (#261).

Samples follow the game, not the wall clock (#267): the next one is taken
once the live tick has advanced `PollTicks` (default 600, a quarter of an
in-game hour) past the previous sample, at once when the service's flight
recorder appends a row `Wake` accepts (default a `worker_outcome` row, the
moment an action's stage changed), and no later than the `Poll` wall-clock
ceiling (default 5 s) so a paused game still shows in the timeline. The
tick and the journal are probed every `na.RunInterval` (250 ms), and
`result.json` counts what ended each pause under `cadence` (`wakes`,
`tick_polls`, `wall_polls`). The same rule holds outside the watch: a wait
on the game is a tick budget through `na.RunUntil`, never a sleep; a
wall-clock duration is only ever a ceiling.

The watch also fails fast on the journal instead of running out that
ceiling (#268, `sustainedfood.FailFast`, on by default): an action of the
watched goal's committed method ending `unsuccessful` for any reason but
`interrupted`/`cancelled`; the goal left active/deficit with no method
through five consecutive reviews that handed its planner the slot (the
development row's `Idle` flag -- a goal the review never selects, such as
`EnsureComfort` under `startup_survival`, is waiting, not refused); or the
service's latest `scheduler_step` line carrying the same native refusal in
`planner_failures` for six consecutive samples (#219's shape); or the
watched goal `suspended` while the review's development rows hold goals
back for an `emergency` and the live tick has not moved for twelve
consecutive samples (#319's park: a downed colonist no kept family can
tend, the clock refusing every window as `no_work`). The verdict
is the case's error and `result.json`'s `fail_fast` row, quoting the
journal text; the failed checkpoint bundle is still taken. A case where
one of these is an expected transient sets `FailFast{Disabled: true}`
(the `sustained/colony` diagnostics) or raises `NoMethodReviews` /
`RefusalSamples` / `ParkSamples`. A baseline-save case whose kept needs
can down a colonist keeps `tend` and `rescue` beside its families
(`supply/starting`, #201's startup cases) so an emergency is served
rather than parked on.

Process reuse carries the same static-state caveat as an `Owned` case's
`GameReuse` (next paragraph), and process-wide `Prefs` too: the
`letter/pause` case sets the pause mode it needs and restores the one it
found. Cases asserting on statics or prefs declare `NoKeep`.

Reuse does **not** reset mod static state: process-scoped statics such as
`OrderedWorkHistory`, `PlayerFrame`, the `Supervisor` journal and
`PawnConfigTool`'s letter maps survive a reload (see
[native-static-state.md](../../../contracts/native-static-state.md)). Any
case whose assertion depends on one of those, and any case run as static-
state or fresh-Go-session evidence, declares `NoKeep` and runs with
`RIMGOVERNOR_ACCEPT_KEEP_GAME=0`. Missing manifest variants are generated
by their `tools/variantsavegen-<save>` case before the `sustained/matrix-*`
case that opens on them.

## Checkpointing a slow precondition

Every run keeps a checkpoint ring of its own (#249): once a minute of run
phase, at the next natural pause (a bridge call, a poll interval; a
serve-driven run pauses its automating service to manual control, saves
through `/api/lifecycle/save` and resumes, about a second, once the
service has observed a tick, #309), the runner bundles the save, the
service's `service.sqlite` (an online-backup copy), the native clock
journal and a `checkpoint.json` sidecar (identity, tick, offset, serve
spec, source revision, native package hash, Start) into
`<root>/checkpoints/<area>/<case>/t+<offset>/`, keeps the last five and,
when the run fails, a final `failed/` bundle before teardown. The next
`acceptance run` of that case in the same root resumes from the ring's
newest entry, printing `resuming <case> from t+7m (rev abc123, failed at
t+8m); -fresh starts over` first; the resumed game is relaunched so the
restored journal is read from its start, and `result.json` records
`resumed_from` and `checkpoints[]`. A ring is discarded, with the reason
printed, when the installed native package, the case's `Start` or the
profile's expansions changed; Go-only changes keep it. `-rewind N`
resumes N entries earlier; a resumed run that fails at the same tick as
the last one rewinds one entry by itself (`no progress since t+7m;
rewinding to t+6m`) and starts fresh past the ring. `-fresh` clears the
ring; `-checkpoint-every 0` or a case's `NoCheckpoint` turns capture
off, and `speedmatrix/` and `tickbudget/` never capture. A resumed pass
is not a landing pass: `acceptance suite` runs every case fresh and fails
a row whose `result.json` carries `resumed_from`, and `cmd/land -results`
refuses such a suite (#308). The bundles are
disposable per-worktree state, never committed. Resume replays the case
body from the top against the restored world and store, so it suits
watch-shaped cases (a declarative `Serve` spec or an `Observe` loop).
A case whose reads and asserts are a separate `Postmortem` phase (see
below) reruns only that phase with `-postmortem-only` (#275): the ring's
`failed/` bundle (or `-from t+7m`, `-from failed`, `-from <bundle dir>`)
is staged and loaded on the kept process, its store copied to
`<output>/service.sqlite`, and `Postmortem` runs against the reattached
harness with no fixture op, no `Run` and no watch, about 20 s instead of
the case's wall time; the ring is left as it was for the next plain run.
`result.json` carries `postmortem_only: true` and `postmortem_from`, and
the suite and `cmd/land -results` refuse it like a resumed row. It takes
none of `-fresh`, `-rewind`, `-repeat`, `-seed`, and needs an empty
`-output` like any run.
A `Run` that submits a deterministic request id (a building plan whose
acceptance fills the arbitration slot, a work-preference override) takes
it from `s.RequestID(base)`: the base on a fresh run, the base suffixed
with the run id on a resumed one, since the restored store already holds
the fresh run's submission and the replay under the resumed world's load
identity is a different request the store answers `409 conflict` (#307).
The id holds still within a run, so a relaunch on the same journal still
replays idempotently. A body that stages its own fixture before the watched phase (a
band of rock, a construction site, a chosen coordinate) records what it
did with `na.SetCheckpointState(key, value)`; every later bundle's
sidecar carries that `state`, and on a resume the body reads it back
through `s.Resumed()` and skips the prep the save already holds instead
of laying it again over a world that has moved on (`defense/layout`,
#316). A body that reaches a point its later steps cannot resume after (a
staged raid whose sprung traps would fail the pre-raid audits a resume
replays) caps the ring there with `na.CapCheckpoints(reason)`: no entry is
taken past it, only the `failed/` bundle, so a later failure always resumes
from the last pre-raid entry and stages the raid again (`defense/layout`,
#330; the reason lands on the report as `checkpoint_capped`). A body whose
later steps assume the fresh run's progress has not
happened and records nothing should declare `NoCheckpoint`. A case that never
pauses on its own (bridge-only, no service) only gets the `failed/`
bundle.

A `sustainedfood.WatchConfig` may also name phase-boundary checkpoints
(`Checkpoints`, each a tick and save name): the watch saves the game
there through the service and captures the same bundle into the ring.

### Staging a slow Run body

The ring resumes a failed attempt; a stage bundle caches deterministic
setup (#329). A case whose minutes are spent in its Run body before the
assertion (a shell sited and roofed, research and a bench built, rooms
on the baseline) declares the stages in order (`Stages: []string{...}`)
and wraps each staging block in `s.Stage(ctx, name, fn)`. `fn` returns
with every service it launched stopped (a released slot with no service
is reattached) and the runner pauses the game and captures the same
bundle as the ring (save, `service.sqlite`, clock journal, sidecar) into
`<root>/stages/<area>/<case>/<name>/`, replacing that stage's earlier
bundle. The next `acceptance run` opens on the newest stage whose native
package, `Start`, expansions and `stage_key` still match (printed as
`opening <case> on stage <name> (captured ...); -restage stages again`),
the way a resume does: the save replaces the `Start`, the store and
journal are restored, `Prepared` comes from the sidecar and
`s.RequestID` and the request ids of every service `s.Serve` launches
are suffixed (#307); `Stage` then skips `fn` for that stage
and every earlier one, so the code after it cannot tell a hit from a
miss. Later stages run and capture as on a miss. `stage_key` is a hash
of the case's area package sources (`cases/<area>/*.go`) plus the
stage names: a change to the staging code invalidates the bundle, a
change to shared helpers does not (`-restage`), and neither the git
revision nor the `rimgovernor` binary is in it. A pending ring resume
wins over a stage (its bundles record the last stage completed under
`state.stage_completed`, so the replayed body skips those blocks too); a
stage hit starts the ring at the staging run's offset, and neither
clears the other. `-fresh` keeps the stages
(they are setup, not the failed attempt), `-restage` discards them and
`RIMGOVERNOR_ACCEPT_STAGES=0` turns the cache off for a harness whose
staging is itself under test. `result.json` carries `staged_from`,
`stages[]` (each stage's name, `hit`/`captured`/`uncached`/`failed`,
bundle path and wall time) and `stage_key`. A staged pass is not a
landing pass either: `acceptance suite` runs every row `-restage` and
fails one whose `result.json` carries `staged_from`, and `cmd/land
-results` refuses such a suite.

A case whose late scenario depends on minutes of earlier play (the
defense layout build before its raid) checkpoints the precondition as a
prepared save instead of replaying it: `tools/defense-checkpoint` saves the
game once the layout is built and audited, writing
`RimGovernor-defense-layout.rws` and `.checkpoint.json` (the layout record and site the
raid assertions need) to `root/profile/Saves` and to the committed
[scripts/fixtures/saves](../../../scripts/fixtures/saves/). The
`defense/raid`, `defense/raid-bypass`, `defense/predator`, `defense/hive`, `defense/shippart` and `defense/turrets` cases declare
`cases.Save{From: ...}` and the runner stages those files into the root
when it lacks them, loads the save, re-runs the cheap layout audits and goes
straight to the raid; the checkpoint is fixture-mod state, so rebuild it
after fixture or save-format changes.

The same shape serves a goal that ranks behind the whole startup ladder:
`tools/facility-checkpoint` plays the tribal8 baseline under the comfort
case's families until RankDevelopment first admits `EnsureComfort` (every
priority-0..2 goal served: shelter, campfire, storage, fields, work
assignments), saves through the service's lifecycle save and commits
`RimGovernor-facility-startup.rws`; `facility/comfort` opens on it so its
12-minute watch covers comfort's own planning and use instead of the
ladder (#201). Regenerate it when the startup ladder's goals, the
`comfortFamilies` composition or the save format change; until the
ladder can complete on the baseline (#217) the save is not committed and
`facility/comfort` fails at staging.
`facility/basic-comfort` needs no such save: its fixture (`UpkeepFixture` build
flag) stands a roofed wood hut with no furniture, seeds an AteWithoutTable
memory and, once the table and seat stand, re-seeds hunger one colonist at a
time (one seat). The watch ends at recovery; with no work left the served
families stop advancing the clock, so the audit drives the meals itself with
`Advance` and then asserts every colonist ate at the table and gained no
AteWithoutTable memory after it stood (#232). About 90 s on a quiet host.

## Available checks

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Go controller logic, contracts, persistence | From `go/`: `go test ./...`, `go vet ./...`, `go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor` (also run together, with staticcheck and the test-time budget, by `task go:build && task go:test` from the [root Taskfile](../../../Taskfile.yml)) | Pin Go via [go/.go-version](../../../go/.go-version); `CGO_ENABLED=0`. Linux race tests need CGO/GCC. Native control and fresh Go-session recovery have separate behavioral checks below. See [go/README.md](../../../go/README.md). |
| Dashboard behavior and build | `task dashboard:build` runs `pnpm run typecheck`, `pnpm run lint` and `pnpm run build`; `task dashboard:test` runs `pnpm test` (Vitest) | Local pnpm and dashboard dependencies; native UI acceptance is separate. |
| Shared Protobuf contracts | Official C#/Go generation `--check` for both languages | [Generation commands](../../../contracts/schema-generation.md); native adapters additionally need gameplay acceptance. |
| Completed pawn work, recovery or another live-game invariant | A registered case through the shared runner, `go run ./internal/nativeaccept/cmd/acceptance run <area>/<case> -root <abs root> -output <fresh dir>` from `go/` (`acceptance list` prints the registry: the synchronous typed-op cases `bed/assign`, `bills/census`, `caravan/control`, `caravan/departure`, `lifecycle/checkpoint`, `lifecycle/load`, `mapscope/isolation`, `pawn/reads`, `presentation/media` (needs `-headless=false`), `quest/accept`, `quest/fulfill`, `research/reads`, `rooms/reads`, `settlement/gift`, `supplies/reads`, `trade/open`; the Loud cases `combat/melee`, `combat/ranged`, `combat/explosive`, `movement/arrival`, `authority/disconnect`; the lifecycle cases `lifecycle/shutdown`, `lifecycle/runtime-fault`, `lifecycle/reuse`, `lifecycle/headless-soak`; the rendered `video/stream`, `video/feeds`, `video/matrix`, `video/source-spike`; the serve-driven `dialog/pause` (#156: a force-pausing choice dialog the game opens is answered and the clock runs again); every other area is listed there too, so trust `acceptance list` over this row) | Disposable prepared colony, matching native DLLs, GABS and a real headless RimWorld instance. Never replace installed DLLs while any RimWorld instance is running, including another worktree's tests. Isolated tests must restore temporarily swapped DLLs. Never kill `RimWorldWin64.exe`/`gabs.exe` by image name — that ends every concurrent worktree's game (seen there as GABS's catalog emptying, `availableTotal: 0`); stop your own via `games_stop` or kill only pids whose command line contains your `-root`. A receipt alone does not prove pawn work completed — verify the observed postcondition. |
| A wild predator hunting a colonist during supervised play is answered by squad defense instead of parking the clock on `predator_hunt` / `unsafe_colony` (#157): the hunt resolves under the service through combat windows, the drafts are released and colony windows resume; natively the predator is dead, downed, gone or no longer hunting a colonist (a predator that broke off to eat wildlife is a watched nearby predator, not the goal's threat, #214) and no colonist is dead or drafted | `go run ./internal/nativeaccept/cmd/acceptance run defense/predator -root <abs root> -output <fresh dir> -rimgovernor <path to rimgovernor.exe>` from `go/` (the case opens on the committed `RimGovernor-defense-layout` checkpoint and spawns a Cougar) against a `DefenseFixture,GuardedConstructionFixture` build; `result.json` carries `predator_incident`, `combat_method` (`squad-…`), `hunt_resolution`, `defenders_released`, `clock_resumed` and `inspect_after_hunt` | Serve-driven on the committed layout checkpoint; the fixture spawns the predator inside the band already on the game's own `PredatorHunt` job (no combat outcome injected). About 5 minutes. Rerun when `routine_defense.go`, `squad_defense.go`, `squad_facts.go` or the emergency threat census changes. |
| A wild alphabeaver pack (factionless, never hostile, ignored by the emergency census) is hunted down by `ClearPests` through the hunt method until none remain on the map (#247): the goal opens on the wild-animal census, plans one `pest-hunt-*` per beaver within the two-hunt budget, re-plans a beaver that wandered off its planned cell and recovers when the census counts none; natively every staged beaver is dead or gone, no wild alphabeaver remains and no colonist died | `acceptance run animals/alphabeavers -root <abs root> -output <fresh dir> -rimgovernor <abs exe>` from `go/` against a `PestFixture` build (`test/pest_prepare` spawns two Alphabeaver about 40 cells from the colonists on the tribal8 baseline, `test/pest_inspect` reads their native state and why the hunt census would not offer one); `result.json` carries `fixture` (the staged pack and the eligible hunters), the `ClearPests` timeline, `settle_steps` (native-only steps a beaver already designated when the watch ended was given) and `pest_inspect` | Serve-driven on the tribal8 baseline under `acquisition,supply,defense,tend,rescue` (the fixture arms the best shooter with a loose bow and gives them Hunting, since the baseline starts with the bows in the drop pile); a wounded beaver turns manhunter half the time, which the defense family answers before the survivors are offered again; beavers forage across the whole map, so a hunt is re-planned when its beaver strays from the planned cell for an hour. Up to 15 minutes. Rerun when `NativeHuntAcquisition.cs`, `policy/pest.go`, `routine_acquisition.go` or the wild-animal census changes. |
| A hostile building near the colony -- an insect hive (`defense/hive`) or a crashed ship part (`defense/shippart`) -- is answered by squad defense (#246): the census lists it under `hostileBuildings`, the planner targets the building itself with melee attacks (never ranged), the fight runs under combat windows, the ActiveCombat goal recovers, the drafts are released and colony windows resume; natively the building is destroyed, no pawn of its faction is on the map and no colonist is dead or drafted | `go run ./internal/nativeaccept/cmd/acceptance run defense/hive -root <abs root> -output <fresh dir> -rimgovernor <path to rimgovernor.exe>` (or `defense/shippart`) from `go/` against a `DefenseFixture,GuardedConstructionFixture` build; `result.json` carries `healed_before_building`, `hostile_building_incident`, `combat_method` (`squad-…`), `building_resolution`, `building_attacks` (`dispatched` > 0: a melee attack on the building ran natively, so the building did not merely decay), `defenders_released`, `clock_resumed` and `inspect_after_building` | Serve-driven on the committed layout checkpoint; the colony is healed first (the checkpoint carries a wound that creeps into a CriticalMedical hold), then the fixture spawns the building in the open with its pawn and child-hive spawning off and the hive's maintenance decay parked, so the building is the only threat and only the colonists' damage can destroy it (no combat outcome injected). About 5 minutes for the hive; the 1200-hit-point ship part takes longer under tribal melee. Rerun when `routine_defense.go`, `squad_defense.go`, the melee boundary, `NativeCombatOperations.cs` or the emergency threat census changes. |
| The first shelter as a native room (#7, #175): a Neolithic colony's hut, a medium or low oval where the strip between rock rows is too narrow for the circle, a concave L or a two-chamber connector where only such a clearing is open, or a grown irregular shell in a five-cell corridor; the shell adopted across a controller restart with exactly one cancelled wall reissued; the plan holding through a wood shortage without a second shell or order and completing once wood is back; beds inside the room off the entrance aisle | `acceptance run shelter/hut`, `shelter/hut-corridor`, `shelter/hut-oval`, `shelter/hut-low-oval`, `shelter/hut-concave`, `shelter/hut-connector`, `shelter/hut-shortage` (with `-rimgovernor`) against a `CorridorTerrainFixture` build (`HutShellFixture` comes with it); `result.json` carries `shell` (`shape` names the template, `hut-template-N`, `concave-l-*`, `connector-*` or `irregular`), `corridor_terrain` (the fixture's `layout`, `rows` or `open` clearing), `run2_shell`/`run2_shortage`, `wood_take`/`wood_wood` and the native room census under `verify` | Serve-driven on the tribal8 baseline, staged (#174): run 0 sites the shell, the ring is spawned but for three bearing gaps plus wood, and the case then edits mid-build. Corridor and pocket layouts raise granite through `test/corridor_terrain_fixture` (`layout` rows with a `period`, `pocket-l`, `pocket-connector`); the fixtures vanish stone chunks rather than pile them into the clearing and end the job of every pawn they teleport, since the baseline has one colonist with Construction active and a pawn left on a stale path never builds. 7–15 minutes each on a loaded machine; `shelter/hut-corridor` is flaky on the canonical baseline (#215). Rerun when `starter_layout.go`, `room_footprint.go`, `routine_shelter.go`, admission's stock refusal or the fixtures change. |
| Sustained colony upkeep on one colony (#99): a starving confined pet, a filthy kitchen, a medicine shortage and a cold sleeping room staged in turn on one kept tribal8 world and one durable journal, each recovered by the routine families composed so far and audited natively, with no goal an earlier stage recovered reopening without recovery (a new method epoch is recorded and must recover again within 90k ticks and before the stage ends), rebound or invalidated while the later ones are handled | `acceptance run upkeep/campaign -root <abs root> -output <fresh dir> -rimgovernor <abs exe>` from `go/` against an `UpkeepFixture,ForecastFixture,RoutineSleepingFixture,CleanlinessFixture` build; `result.json` carries `timeline` (per stage: ticks, `wall_ms`, the recovered goal and its epoch), `stage_<name>` (the stage's own watch fields, `upkeep_before`/`upkeep_after`, `reopened`) and `closed_goals` | Serve-driven, one service launch per stage over the live map (never a reload, so the earlier goals survive on the journal); the calendar is not the property, a season being over an hour of Ultrafast wall time. 10-15 minutes. Rerun when `routine_upkeep.go`, the cleanliness, animal-feed, medical-reserve or temperature reviews, `domain.ReviewGoal`'s epoch rule or the four fixtures change. |
| Home coverage over a controller-built facility (#292): MaintainSleeping builds one bed the journal's construction lineage then owns, the fixture strips Home from the bed's footprint (bed plus its enclosed roofed room), and MaintainHomeCoverage opens on the deficit, dispatches one typed `ExtendHome` for the bed under the fixture's shape token and recovers on the observed Home cells; natively every footprint cell is Home again and the revision advanced. Then a player removal (#314): `test/home_player_remove` takes one covered cell out, a third service reopens the deficit as an excluded target (`excluded_cells` 1, blocker) and commits no method, and the cell stays out of Home | `acceptance run upkeep/home-coverage -root <abs root> -output <fresh dir> -rimgovernor <abs exe>` from `go/` against an `UpkeepFixture,ForecastFixture,RoutineSleepingFixture` build; `result.json` carries `stage_bed` (`bed_*` method fields, `beds_built`), `stage_home` (`prepared` shape/revision/count, `home_*` method fields, `home_before`/`home_after` covered/total/revision) and `stage_exclusion` (`removed`, `home_after`, `census_row`) | Serve-driven, three service launches over the live map (the bed must be the controller's own order, or nothing owns it). About 6 minutes when the bed goes up on the first window, 9 when the builder is elsewhere (the exclusion stage holds a 3-minute window). Stockpile ownership by receipt identity (#315) is proved by `upkeep/storage-missing` instead (`zone_id`, `stockpile_home_after`). Rerun when `NativeHomeCoverageOperations.cs`, `HomeCoverageTool.cs` scope/shape rules, the `home_coverage` census rows, `routine_home_coverage.go`, `home_coverage_boundary.go` or `policy.ReviewHomeCoverage` change. |
| The powered turret tier (#61): with turret research, a fuelled network outside connector reach and steel observed, the layout planner adds turrets behind the firing line with their own conduit chain to the committed layout and builds them natively; a powered turret takes aim at an edge raid entering the kill zone; a tier conduit that vanished and a turret at half hit points are restored by routine upkeep | `go run ./internal/nativeaccept/cmd/acceptance run defense/turrets -root <abs root> -output <fresh dir> -rimgovernor <path to rimgovernor.exe>` from `go/` against a `DefenseFixture,GuardedConstructionFixture` build; `result.json` carries `power`, `turret_tier` (cells, `built`), `inspect_after_turrets` (turret rows with `powered`), `turret_fire` (`last_attack_tick` past `raid_tick`), `depower`, `damage_turret`, `turret_restore` (repairs deficit seen and recovered, layout standing) and `inspect_after_restore` | Serve-driven on the committed layout checkpoint with the `repair` and `power` families added; the fixture finishes research natively and spawns the generator, conduit stub and stock, never a turret, and starts the raid at the map edge nearest the corridor entry. About 5 minutes on an idle machine. Rerun when `defense_turrets.go`, `routine_defense_layout.go`, the turret deficit in `routine_development.go`, `NativeUpkeepFacts.cs` structure rows or the power topology census changes. |
| Reactive clock delivery: a watched construction attempt stops its window at completion (`STOP_REASON_WATCH_LATCHED`) with the outcome and stop on one long-polled page, a baseline window still plays to `STOP_REASON_TICK_BUDGET`, an idle `wait_ms` read is held, and an authority change outside any epoch arrives as an owner-less row | `go run ./internal/nativeaccept/cmd/acceptance run reactivewatch/construction -root <abs root>` from `go/` (every window at Superfast) against a `GuardedConstructionFixture` build; reports wake latency, latched tick, ticks saved and window counts in `result.json` | Bridge-driven (no `serve`), Core-only debug start, frozen needs, quiet storyteller; about 2 minutes. Bounds: wake latency (page return minus the stop's `observed_at_unix_ms`) at most 250 ms, stop within one tick of the latched tick and before the deadline. Rerun with `acceptance run tickbudget/boundaries` and `guardedconstructionaccept` when the stop-reason or classification surface changes. |
| Kept process between runs: a process that hosted a controller killed with its Auto grant and typed clock epoch still active (session dropped, then unload to the menu) must, for the next controller that prepares the profile again and attaches, still page its clock journal from cursor 0 with no gap and hold a fresh Auto grant for the whole 15s hold window (#119) | `go run ./internal/nativeaccept/cmd/acceptance run authority/warm -root <abs root> -output <fresh dir>` from `go/` against any fixture build; `result.json` records both phases, `clock_journal` (`newestCursor`, `lostCount`, `gap`) and `held_reads` | An `Owned`, `NoKeep` case: two sessions on one process it launches and retires itself; about a minute after the first start is cached. Rerun when `nativeaccept` profile preparation, `OpenGame`/`OpenSession`, `ClockEventJournal.cs` or the authority hooks change. |
| Whether pawn outcomes and controller throughput hold across clock speeds (Normal, Fast, Superfast, Ultrafast, uncapped = Ultrafast with the acceptance test acceleration, #109) | `go run ./internal/nativeaccept/cmd/acceptance run speedmatrix/plain -root <abs root> -rimgovernor <abs path to rimgovernor.exe>` from `go/` against a `ThroughputFixture` build (every speed of `na.DefaultSpeedMatrix`, a 6000-tick budget per speed, or until the stage runs out of work; rendered runs admit uncapped too, at a lower wall TPS). One staged colony (`test/throughput_prepare`: three colonists on Construct/Haul, loose Steel with a stockpile, a 6-segment wall run) saved once and reloaded per speed through a plain hold (the GameReuse reset contract is `reuseaccept`'s subject); one `serve` process per speed with the `haul` and `work` families. `report.json` carries per speed wall TPS, paused fraction, steps, reads/step, parent hits, the wall-sized colony window (mean/max ticks, #126), stop latency, the stop-to-readmit pause per admission (mean/max, #162), budget-vs-reactive stops and budget stops per 6000 ticks, plus the outcome row (stored units, walls built, healthy colonists, unsuccessful plan stages) | Serve-driven, frozen needs, quiet storyteller, stall-bounded waits; about 2 minutes per speed. Fails (non-zero exit) when any outcome differs by more than 1 across speeds, when a case records an unsuccessful plan stage, or when nothing was hauled or built; the #126 paused-fraction and Ultrafast-vs-Fast TPS thresholds are reported, not enforced. Rerun when the clock scheduler, step cache, or the Ultrafast/acceleration path changes. The fast check first: `go test ./internal/buildingruntime -run TestClockSpeedMatrix` drives the scheduler and worker against a wall-clock fake native at tick multipliers 1-150 and asserts the same window decisions per game tick and a stop-to-step latency under one step interval (#112). [measure-throughput.md](measure-throughput.md) explains the report fields. |
| Native colony/routine read parity against a retained capture | `go test ./internal/observation -run <TestName> -v` with the matching `RIMGOVERNOR_NATIVE_*_CAPTURE`/`RIMGOVERNOR_NATIVE_*_REFERENCE` environment variable (colony, food forecast, production, naming, power methods, temperature methods, mood — see [go/README.md](../../../go/README.md) for the exact test name and variable per family) | Retained native capture files; verifies decoding/replay parity, not a live game session. |
| Whether the cross-step fact cache is doing its job for a routine family | `acceptance run routinehaul/storage` or `light/dark`: every launch records a flight timeline and `result.json` summarizes it under `metrics` (`reads_per_step_mean`, `cache_hit_ratio`); the pawn outcome assertions are unchanged, so a run that passes with a lower reads/step than a run without the parent cache is the acceptance | Read-only diagnostics on top of the ordinary case; no extra game time. |
| Where bridge call time goes (gate wait, GABS round trip, native main-thread queue wait, native tool execution, receipt decode, ProtoJSON decode) and wall TPS over a session | Run `serve` with `--flight-recorder <abs path>`, then `rimgovernor phases [--json] <abs path>` from `go/` (`go run ./cmd/rimgovernor phases ...`). Rows carry per-call `timing` phases and `native_decode` rows; the sampler aggregates per native tool, with describe (`games_tool_detail`) round trips listed separately (paid once per method per GABS session, not per call); the `cached` column counts reads the scheduler's per-step read cache served without a round trip (`native_cache_hit` rows). Each `ClockScheduler.Step` also publishes a `clock_step` row tallying the round trips it still issued by tool, which the report shows as reads/step (mean, max, per tool) with the step cache's hits and the cross-step `FactCache` parent hits per step; `serve --debug` (every acceptance launch) prints the same tally with the cache's hit/miss/parent-hit counts per step on stderr (`[clock-scheduler] step reads: ...`) | Read-only over the recorder's retained segments; no game or authority access. The companion reports its own split beside the payload (`{"payload": ..., "timing": {"queueMs", "executeMs"}}`) for every `rimgovernor/*` tool whose single main-thread hop goes through `ProtoBoundary.OnMainThread`; the `queue ms`/`exec ms` columns are means over the calls that carried it and read `-` (absent, not zero) for an older companion or a multi-hop media capture. Sub-millisecond phases can read 0 on Windows' coarse monotonic clock. Wall TPS comes from reply ticks and includes paused time; tick decreases (load, rewind) are excluded as resets. Field meanings and how to read a report: [measure-throughput.md](measure-throughput.md). |
| Harness waits, decoding, the authority ceremony or discovery against a recorded call sequence, without a game | `go test ./internal/nativeaccept -run TestReplay` from `go/` over the transcripts under `internal/nativeaccept/testdata/transcripts/`; record a fresh one from any case with `RIMGOVERNOR_ACCEPT_RECORD=<abs dir>` (`<dir>/transcript.jsonl`, one sequence across every session the run opens) and open it with `na.ReplayHarness(ctx, path, output)` | Replays receipts as GABS returned them (refusals included) and fails on the first call the recording did not make, with the diff (`bridge.Replay.Err`). Establishes harness behaviour only, never a native one. |
| Docker packaging of the Go controller | `docker build -f containers/Dockerfile --target go-controller`, `docker run --rm rimgovernor-go:local version`/`help` | Confirms the image builds and the binary starts without a licensed game; not gameplay evidence. See [go/README.md](../../../go/README.md#go-launch-and-packaging-the-production-default-g0111-g0112). |
| The Go controller as a Linux worker container reaching a connected native session, and the worker storage contract (private volume, export after stop, database integrity, verified cleanup) | `go run ./internal/nativeaccept/docker/cmd/dockerworkeraccept` from `go/` with the Linux game/mods/profile/GABS inputs (`-storage bind` for the host-bind-mount comparison); `go test ./internal/nativeaccept/docker/` covers the stop/export/retain/release branches against a fake `docker` without an engine | Linux Docker engine, licensed Linux game build and prepared profile; one worker, no pawn work. A retained volume in `storage_evidence` is recovery evidence from a failed export, not a leak to sweep. |

For agents: `go run ./cmd/affected` from `go/` prints the checks a change
needs, one command per line: the `go test` line for the packages holding the
changed Go files plus every in-module package importing them (`./...` when
`go.mod`/`go.sum` changed), and one `go run ./internal/nativeaccept/cmd/acceptance
run <area>/...` line per case area whose inputs the change touched (the area
or the runner imports a changed package, the `rimgovernor` binary does and
the area hosts it, or a changed routine family is one the area composes;
every area when the native sources, fixtures or `go.mod` changed). It diffs
the working tree, including uncommitted and untracked files, against the
merge base with `main` (`-base` for another revision); pass paths to ask
about a hypothetical change. Every changed Go file stays in compilation and
static checks. Only acceptance selection may ignore ordinary comments or
literal-only clock log calls; control flow and expressions with unknown
effects keep their acceptance coverage. The land tier uses the full changed
file list and may conservatively include these diagnostic inputs as well.
`-files` lists the files considered and, under
each area, why it was selected; and a `task probes:build` line when the change touches the native
contract probes build (a source under `integrations/rimgovernor-native/src`,
`contracts/tests` or the generated C# protocol classes): the probes compile
production clock and authority sources against hand-written stubs, so a
native addition can break them while the mod build stays green (#123).
`-baseline <result.json|metrics.jsonl>` appends the affected case set's
price: the total wall and boot time of the baseline's rows in the affected
areas (cases the baseline never timed are not counted; `acceptance list
-cost <area>/...` names them).
`go run ./cmd/test` runs the `go test` line and the probes build (the
landing lane does not), after gofmt on the changed Go files and `go vet`
plus staticcheck on the affected packages, the gates `task go:build`
applies to the whole module (#334). The individual acceptance lines from
`cmd/affected` explain selection; use the single land-tier command printed
by `cmd/test` for milestone validation. Run `cmd/test` once before landing;
do not follow it with `go test ./...`. Changes touching dashboard, C# or
shared Protobuf contracts use `task build && task test` for their project
gates and generation checks. Reuse a successful run when
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
gaps, and `acceptance list` (the packages under
`go/internal/nativeaccept/cases/`) for the actual current set of cases (issue
#38's own description can lag newly landed families — trust the registry over
the issue body when they disagree).
