# Choose checks for a change

[Documentation](../../README.md) · [Development workflow](../development-process.md)

## Testing budget and evidence reuse

Pyramid: many fast Go unit tests, fewer boundary integration tests, a small set
of targeted native acceptance cases against a real headless RimWorld. Keep
focused unit tests in the edit loop. Before a slow check, name the changed
behavior or unresolved failure it verifies and why a cheaper check cannot.

- Run native acceptance at feature milestones, not after every edit.
- After an acceptance failure, add a fast regression test where feasible and
  rerun the affected case only.
- Passing evidence follows relevant code, dependencies, inputs and environment,
  not the `main` HEAD hash. Unrelated `main` commits, clean rebases and
  cherry-picks do not invalidate it. Do not repeat suites after
  documentation-only follow-ups.
- "The full affected suite" means the applicable automated suite, not every
  gameplay scenario. Put unrelated discoveries in the backlog.

Harness logic (shared waits, `Harness.Call`/`Wire` decoding, authority
ceremony, discovery, refusals) is checked below acceptance: a run with
`RIMGOVERNOR_ACCEPT_RECORD=<dir>` writes every bridge call and raw receipt to
`<dir>/transcript.jsonl`, and `na.ReplayHarness` serves it back under `go test`.
A call the recording never saw fails with the argument diff. Transcripts live in
`go/internal/nativeaccept/testdata/transcripts/`; the game stays the oracle for
anything native.

## What a result proves

A result must show what behavior is proved, the cheapest credible boundary for
it, which real-game assumption still needs native evidence, and how a failure
identifies the broken contract. Levels are purposes, not a speed hierarchy:

- **Policy and component tests**: planner/adviser logic from constructed
  inputs. An exact planner decision (which shell, which bill) belongs here; a
  case asserting on it fails on unrelated map variation.
- **Boundary integration tests**: a contract (bridge client, transport, store,
  clock scheduler) with the far side controllable. A *scheduling* outcome
  (overlapping calls, a wait released by an event) belongs here, synchronized
  instead of timed.
- **Focused product acceptance** (`nativeaccept` cases): one gameplay outcome on
  a real headless RimWorld from a staged precondition.
- **Cross-policy campaign acceptance**: a whole chain over one preserved colony
  and journal. Do not split a campaign into fresh fixtures or assert only that
  journal statuses closed.

[shelter-coverage.md](shelter-coverage.md) is the worked example of mapping
planner branches to Go tests and native cases to what only a real room can fail
at. A case's own claim stays in its `Scope`.

Moving a case between tiers must state which proof moved and where it is still
obtained.

### Setup, behavior, verification

- Setup may create the shortage, obstacle or half-built structure; it must not
  supply the outcome. A declared mid-run intervention is part of the scenario
  and says so.
- Validate the precondition directly (a shortage fixture that leaves another
  usable stock tests nothing; a construction case that opens with its structure
  standing passes for free).
- Verify the outcome from authoritative native state. A receipt is not completed
  pawn work; controller reports and journal rows explain *why*.
- Add a few targeted negative controls (accepted-but-unfinished work must not
  satisfy a completion assertion), not a mutation suite.

### Full, cached and resumed runs

Every `result.json` carries a `provenance` block (`cases.Provenance`):
`execution`, `proves`, `native_ops`, `isolation`, `routine_families`.

| `execution` | Opened on | Proves |
| --- | --- | --- |
| `full` | the declared `Start` | the whole case from its precondition |
| `cached-precondition` | a stage bundle | behavior after that stage only |
| `resumed-suffix` | a ring checkpoint | behavior past the checkpoint only |
| `postmortem-only` / `dev-iteration` | a failed or pinned bundle | the reads, or nothing (an edit loop) |

- A cached fixture may supply the precondition but never proves the behavior
  that produced it.
- Reuse is invalidated when the installed native package, the case's `Start` or
  the profile's expansions change (`na.Fingerprint`). A controller change
  *before* the pickup point is not caught: run fresh (`-fresh`, or the suite).
- A map reload is not a process reset: a case depending on native static state
  declares `NoKeep` (see [Keeping the process](#keeping-the-process-between-runs)).

### Deadlines, ordering and latency

- **Gameplay deadlines** are game ticks. **Wall clock** is only for a dead game,
  process or transport, and hang guards; widen a guard that trips under load,
  never tighten one per test.
- **Latency** is asserted only as an intentional performance requirement
  ([measure-throughput](measure-throughput.md)).
- An *ordering* claim needs synchronization, not a bound.
  `internal/bridge` `TestIndependentCallAnsweredWhileLongPollHeld` is the
  controllable version; the `gab-dispatch` native probe covers the host.

## Remote checks

Use the [maintainer-to-agent handoff](remote-handoff.md) to prepare a remote
selection, request dispatch and import its verdict. Remote execution does not
authorize agent pushes or omitted cases.

## Which cases a change owes

`go run ./cmd/test` (and `cmd/affected`, which only prints) names the case areas
a change touches. Selection rules:

- **Per package, not per symbol.** An area is named when its own package or the
  shared runner (`cmd/acceptance`, through its imports) imports a changed
  package. Any edit to a package the runner imports (`cases`, `clock`, ...)
  names every area.
- **Binary.** An area hosting `rimgovernor serve` (a `Serve` spec, `Service`
  case or `ServiceLaunch`) is named when `cmd/rimgovernor` changes.
- **Shared inputs** name every area: the native mod's build inputs (the list
  `RequireCurrentPackage` compares), `go.mod`/`go.sum`, fixture build files
  (`.csproj`, `Taskfile.yml`, lock file).
- **Fixtures.** A `<Name>Fixture.cs` under `scripts/fixtures` names the areas
  whose Go sources name one of its `[Tool("test/...")]` ops; a committed save
  under `saves/` names the area naming it. `contracts/fixtures` feeds unit tests
  only.
- **No build effect.** A `_test.go` or non-embedded `testdata` edit names only
  its owning package. Production embeds select owner, importers and acceptance
  areas; test-only embeds select only the owner.
- **Routine family.** An edit confined to a family-owned planner file in
  `internal/buildingruntime` (`roundsFamilyFiles` in `internal/affected`) names
  the serve-hosting areas whose cases compose that family. Shared files, and
  every clock, worker, scheduler, review and boundary file, name every area.
- **Comments and trace.** A Go edit that changed only comments (`//go:build`
  counts as code) or only the clock's debug trace names nothing.
- **Harness object.** An edit to `go/internal/nativeaccept/*.go` (not
  subpackages) is scoped by the declarations edited and every harness
  declaration that transitively uses one. An area whose own sources use a
  tainted declaration runs whole; an area reached only through the runner or a
  shared helper package (`cases`, `sustainedfood`, `setup`...) is **sampled**:
  the land tier runs one case (cheapest, bridge-only first) and `-tier full`
  runs the rest. `cmd/test` lists them under `cases affected`.

`cmd/affected -files` prints, under each area, the changed file and the rule
that reached it.

Landing needs no acceptance run; the full tier proves affected areas.
`-tier smoke` or `-tier land` proves them before landing when the change
warrants it (hand the output to `cmd/land -results`). Do not run areas
separately first. Name the suite in the commit message; an area you judged
unaffected and skipped is "left unverified": land and say so in the commit body.
Never enter a second rerun-and-land cycle for one milestone.

Scenario-clock cases may set `ScenarioClock.TestAcceleration` (Ultrafast with
the native test tick boost; needs the acceptance launch flag). Other
scenario-clock cases default to Superfast.

## Stage the precondition, do not play into it

A case opens on a colony already in the state the assertion needs and advances
only the ticks the assertion consumes. Never start from a baseline save and let
the colony grow into the precondition. In order of preference:

- a prepared `.rws` save committed with the case's fixture set (`cases.Save`);
- a `test/*_prepare` op in the fixture mod that spawns buildings, pawns, items
  and conditions in one call;
- `acceptance setup generate variantsave-<save>` (`tools/variantsavegen-<save>`)
  / `na.ScenarioStart` when the stressor is map- or start-level (seed,
  biome, season, scarcity). The checked-in artifact is the manifest
  (`cases/sustained/manifests/issue-1-matrix.json`); the generated `.rws` under
  `profile/Saves` is written once offline.

Develop a fixture op without a case: `acceptance fixture <op> [key=value ...]
-root <root>` loads the tribal8 baseline (`-save <name>` for another) into the
root's kept game, calls the op and prints native's reply (a refusal included,
exit 1) plus a census of the world. A value that parses as JSON is that value
(`x=12`, `cells=[[1,2]]`), anything else a string. The world stays loaded and
paused, so `-loaded` runs the next op in seconds; the next `acceptance run`
unloads it. `-output` and `-json` apply.

## Campaigns, food and clearance cases

- `campaign/*` proves autonomy and runs outside the land tier (`nightlyOnly` in
  `cmd/acceptance/tier.go`). The harness acts only during setup; every later
  hand lands under `interventions`, which campaigns require to be zero. Gates
  are native end state plus advancing concern progress, never a plan count.
  `RIMGOVERNOR_ACCEPT_CAMPAIGN_TICKS` overrides `campaign/foothold`'s length.
- `food/` owns nutrition-channel acceptance (`food/empty-channels` over
  `food.EmptyChannels`). Food planner decisions are Go tests (snapshot replay in
  `internal/snapshot`; hunt selection, meal tiers, butchery are policy tests).
- `clearance/shrine-breach` and `clearance/shrine-claim` need
  `acceptance setup -rebuild -fixture ShrineFixture`. No acceptance case covers
  native casket opening; that replays as a colony snapshot.
- Single-failure faults are Go tests against fakes, not cases:
  `buildingruntime/faults_test.go` and `httpapi` viewer tests.

Details per case live in its `Scope` under
`go/internal/nativeaccept/cases/<area>/`.

## Adding a case

Every native acceptance is a `cases.Case` registered from
`go/internal/nativeaccept/cases/<area>/*.go` and run by the one binary
`go/internal/nativeaccept/cmd/acceptance`. A new case is a new file in an area
package (or a new area, imported for its `init()` from `cmd/acceptance/main.go`)
calling `cases.Register` with:

- `Name` (`<area>/<case>`), `Scope`, `Budget`;
- `Start`: `cases.DebugStart{}`, `cases.Save{Name}`, `cases.Fixture{Op, Args}`
  on either, `cases.Scenario`, `cases.Lab{Colonists: n}`, or `cases.Owned` for a
  case driving the process lifecycle itself;
- `Run(ctx, s cases.Session)`, the assertion only. Fixture ops called inside
  `Run` or service hooks go in `RequiredOps`.

Commands (from `go/`):

```
go run ./internal/nativeaccept/cmd/acceptance list [<area>/...] [-cost -baseline <result.json|metrics.jsonl>]
acceptance run <area>/<case>... -root <abs root> [-output <dir>] [-rimgovernor <abs exe>]
    [-budget <d> -stall <d> -timeout <d>] [-fresh] [-rewind N] [-checkpoint-every <d>]
    [-restage] [-through <stage>] [-evidence capped|full] [-repeat N] [-seed <s>]
    [-postmortem-only [-from <label|dir>]] [-break stage=<name>|tick=<n>|minute=<m>]
acceptance dev <area>/<case> -root <abs root> [-from <label|dir>] [-watch]
```

Other subcommands: `suite`, `resume`, `warm`, `stop`, `why`, `fixture`, `setup`,
`doctor`, `plan`, `prune` (`acceptance <sub> -h`). `run` executes several cases
on one kept process.

Evidence: each case writes `result.json` under `<output>/<area>/<case>` plus one
file per native call and service request. Replies over 256 KiB are capped
(`-evidence full` or `RIMGOVERNOR_ACCEPT_EVIDENCE=full` keeps the whole row).
`game.log` holds the case's slice of the game log. A failed run writes a
`diagnosis` digest; `acceptance why <case dir>` reprints it. Harness calls
acknowledge bridge attention and retry a refused call once; `attentions`
records them. Acknowledgement permits progress; it does not classify a game
fault as harmless.

The runner owns the preamble (stale-package check, profile, `OpenGame`,
discovery, start, pause, frozen needs, fixture reply `s.Prepared()`) and the
report (`s.Report()`), close and startup-log check. `world` in the report
records the `seed`, the `save` with its hash and the fixture op hash.

Reproducing a roll-dependent failure: `-repeat N` runs the case N times fresh
and writes `<case>.repeat.json` with the pass rate and seeds; `-seed <s>` pins a
debug or scenario start to a recorded `world.seed` (a `Save` start refuses it).

Serve-driven cases declare `Serve: &cases.ServeSpec{...}` and call
`s.Serve(ctx, s.Spec())` when in-game setup is done (`dialog/pause` and
`light/*` are the reference shapes); `s.Reattach(ctx)` takes the slot back for
postmortem reads; `s.Launch` composes the lifecycle yourself. Every launched
service is profiled (`cpu.pprof`, `heap.pprof` in the service directory);
`RIMGOVERNOR_ACCEPT_PPROF=0` opts out.

A case is held to this checklist. Each item is enforced by a runner default or
by `cases.Case.Lint` (run by `go test ./internal/nativeaccept/cmd/acceptance`
and before every run), which refuses a departure without a `Reason`.

1. **Open on the precondition.** A committed `.rws` or a single `test/*_prepare`
   call; the first assertion-bearing tick within a minute of the game being
   ready. Lint refuses `Serve` on a bare `DebugStart`.
   - A case needing one op or read on a known structure with no colony history
     opens on the lab (`cases.Lab{Colonists: n}`: 100x100 map wiped to Soil, `n`
     fixture colonists, `na.LabSpawn` per building/item/pawn, `cases.LabBudget`;
     `lab/spawn` is the pattern). An `Owned` case loads it with `na.StartLab`.
     Every op/read contract case opens on the lab. A lab op that uses existing
     starting resources takes `ArgsFrom: cases.LabWood(n)`.
2. **Small map, tiny planet.** Default 200x200 on a 5% planet; a larger
   `DebugStart{Size}` needs a `Reason`, and never hardcode a map size in a
   fixture. A construction-heavy case that ignores terrain sets
   `DebugStart{Flat: true}`; a case needing only open ground starts on
   `cases.LabStart()` (cached as `RimGovernor-lab-100`).
3. **Every installed DLC.** The profile activates every shipped expansion; a
   `Save` start activates the save's own `<modIds>` (`cfg.UseSaveExpansions`).
   `Config.Expansions` or `RIMGOVERNOR_ACCEPT_EXPANSIONS` narrows it.
4. **Quiet by default.** `Quiet` defaults to `QuietRequired`; `QuietIfAvailable`
   and `Loud` need a `Reason`. Every need is frozen before `Run` except the
   `Keep` list (`frozen_needs` in the report). `s.Advance` passes the case's
   `Letters` as expected interruption letters; a non-nil `Letters` (even empty)
   makes every window strict. A case that never watches the wild map also sets
   `QuietWorld` (needs `-rimgovernor-test-acceleration`; farm, husbandry and
   hunting cases leave it off).
5. **Every wait is stall-bounded.** Poll through `na.WaitProgress` with a
   signature over what must move and `Terminal: service.Exited` when a serve
   subprocess is involved; use `WaitReview`, `WaitPlan`, `WaitMethod`,
   `WaitPlanTerminal`, `WaitRounds` where they fit. No bare `for { ...;
   time.Sleep }` loops and no per-phase ceilings in tens of minutes.
6. **Budget in minutes and say so.** `Budget` is required and at most
   `cases.MaxBudget` (15 minutes) without a `Reason`; past that, stage the
   precondition better or split the case. A run that passes over budget fails
   (`budget_exceeded`); `-timeout` (default 20 minutes, never less than budget
   plus 5) is the safety net. Timing metrics append to the series at `-series`
   (default `<output>/../metrics.jsonl`; `-no-series` skips it); drift
   (`na.DriftRules`) and the `flake` block are reported, never failing a run.
7. **Advance by ticks, at speed.** A wait for something the game must do is
   bounded in ticks: `na.RunUntil` at `na.RunSpeed` with `na.RunBoost`, polling
   under a `na.Wait{Ticks: ...}` budget; no `devMode` pref (adds a 35 s def
   check to every boot). `s.Serve` always passes `na.ClockSpeedArgs`; override
   with `RIMGOVERNOR_ACCEPT_CLOCK_SPEED` (Normal, Fast, Superfast, Ultrafast).
   Serve-driven wall time is the controller's cadence, not the game's.
8. **One fixture call, not a script.** Spawn, forbid, damage, assign and settle
   in one `test/*_prepare` op; production ops are for the behavior under test.
9. **Fail fast on terminal signals.** A `ScenarioInterrupted` hold, a plan in
   `Unsuccessful`/`Cancelled`, a serve exit or a missing fixture op ends the run
   at once with evidence.
10. **Prefer reuse over boot.** The runner keeps the process by default
    (`na.KeepGameEnv`). A case that ends or replaces the process declares
    `NoKeep`; an `Owned` start must.
11. **Split the scenario from its reads.** A serve-driven case declares reads and
    asserts as `Postmortem` (after `Run`, services stopped, harness reattached),
    keeping `Run` to prepare and watch. State the asserts need goes through
    `na.SetCheckpointState` (read back from `Session.Resumed`) or
    `Session.Prior`, so `-postmortem-only` can rerun the asserts over a failed
    bundle. `production/ladder` is the shape.

## Keep the game quiet and small

### DLC and the baseline save

`nativeaccept.PrepareNativeModConfig` writes the requested expansions into the
headless/rendered `ModsConfig.xml` (every shipped one when a run names none) and
lists every shipped expansion in `knownExpansions` (RimWorld activates any
installed expansion it has not seen at boot). A save refuses to load
(`save.missing_mods`) under a profile missing an expansion it was recorded with,
so a `Save` start calls `cfg.UseSaveExpansions` before `PrepareConfig`.

The tribal8 baseline (Lost Tribe, eight colonists, quiet, Core-only) is a spec,
`na.BaselineStart`, not a committed save: the first `Save{Name: BaselineSave}`
start on a root generates it through the new-colony op into `profile/Saves`
(`Config.EnsureSave`) and stamps it with a `<name>.spec.json` beside it; a
missing save or a stamp that differs from the spec regenerates it.
`acceptance setup generate baseline` (`tools/baselinegen`) regenerates on demand.

#### Start cost and reproducibility (#2029)

`acceptance run tools/startcost` (off-tier, ~10 min, one game launch per row)
measures the new-colony op and reports it under `start_cost` in `result.json`.
Measured on the maintainer's box with no peer RimWorld running, all DLC, eight
LostTribe colonists, quiet; seconds from the op's start to the saved colony
(game boot excluded, ~25 s headless):

| Planet coverage | 150 map | 250 map | 350 map |
|---|---|---|---|
| 5% (dev only) | 5.6 | 7.7 | 13.1 |
| 30% | 10.1 | 13.8 | 21.4 |
| 50% | 16.0 | 18.6 | 23.7 |
| 100% | 41.4 | 44.6 | 55.9 |

Coverage dominates (world generation); map size adds roughly 5-12 s from 150 to
350. The launcher's coverage labels carry these figures. Colonists: 5-19 rerolls
accepted a team across 20 starts (3-10 colonists, nine seeds); a reroll costs
about 20-90 ms (the figure includes the roll's share of pawn generation and is
quantised by the 250 ms poll), so a team takes well under two seconds. At that
cost 10,000 rerolls would hold the game for 3-15 minutes before the hard error,
so the budget is 1,000 (about a minute at worst; 50 times the largest need seen).
`colonist_count` is capped at 10 natively.

Reproducibility (#2034): the same spec twice on one install, each on a fresh
game, gives the same tile, colonists (names, traits, skills, reroll count) and
map digest (`na.SaveMapHash`: terrain grid plus every thing's def and position).
`tools/startcost` checks three seeds (the tribal-8 seed and two more, 250 map,
0.3 coverage) on a Core-only profile and on all DLC, and passes: identical
digests in all six pairs. Four native causes were removed:

- `new Game()` draws `UniqueIDsManager.nextThingID = Rand.Range(0, 1000)` from
  the unseeded stream, so thing ids (which seed pawn relations and more) shifted
  every start. The Game, scenario and storyteller are now built under the
  spec seed's `Rand` scope.
- Vanilla's `Map.NextGenSeed` (BaseGen shrines, ruins and scatter groups) and
  `LayoutWorker.FillAllRooms` seed with `System.HashCode.Combine`, which is
  salted per process. `DeterministicGenSeed` swaps those calls for
  `Gen.HashCombineInt` (a Harmony transpiler installed at the start of a new
  colony); diagnose such a divergence by logging `UniqueIDsManager.nextThingID`
  and the thing count around each `GenStep.Generate`: the first step whose
  counts differ with equal `Rand` iterations is drawing from another source.
- A new game starts at normal speed until the poll pauses it, so animals walked
  a poll-timing-dependent distance; the start now begins paused.
- The case's earlier "core" rows never switched the profile and ran all DLC;
  each profile now writes its own `ModsConfig` before its rows.

The earlier reroll divergence (17 against 11) was the first cause. World
factions and ideoligions matched in the logged runs once thing ids were seeded.
A case that asserts a map feature on the generated baseline is now asserting a
feature that is stable per install, not per root; it can still differ across
installs.

Baseline-pinned cases run for #2029 (six of ~28, one root each; the rest are the
nightly bulk tier's): `startup/labor` passed on the generated baseline. On the
generated and the earlier committed baseline alike, `farm/select-hydroponics` (no basin
sows Plant_Potato), `defense/threat` (wealthItems did not grow with the stocked
supplies) and `shelter/bunks-first` (MaintainHousing admits no shelter plan)
fail, so those failures are not the generated colony's: they predate it and are
open for the full tier. `layout/rich-soil` and `layout/ring` failed on both
with "no definition catalog was loaded" until `startersite.Survey` loaded the
catalog (fixed here); after it, on the generated baseline `layout/rich-soil`
fails "no crop zone was laid" (the generated world has no rich-soil patch near
the centre; the case asserts the committed map) and `layout/ring` fails "no
stone ring stands". Neither assertion is relaxed here: with a map that differs
per generation, the terrain-pinned cases need either deterministic generation
or a spec-level terrain requirement first.

### Quiet storyteller, frozen needs, letters

A scenario start (`na.ScenarioStart`, the production new-colony op) picks the `RimGovernorQuiet` storyteller; a save start applies `test/quiet_storyteller` once the colony exists
(`quiet=false` keeps the ordinary one). Harnesses start through
`na.StartDebugGame(ctx, h, names, mode)`: `QuietRequired` (fixture-dependent),
`QuietIfAvailable` (also runs on a production build), `Loud` (interruption
harnesses such as `combat/*`, `defense/*`; `Reason` required). Quiet means Custom
difficulty at zero threat scale, no incidents or storyteller ticks, and every
non-colony pawn and map-gen insect hive removed.

`na.FreezeNeeds(ctx, h, names, keep...)` pins every free colonist need at
maximum except the NeedDefs in `keep` (`Food` for cooking, `Rest` for sleeping,
`Mood`/`Joy` for mood relief). Record the reply on the report. Construction
cases freeze everything; a case needing a colonist to eat or break names that
need in `Keep`.

Letters are acknowledged, not fatal: `na.AdvanceGame` dismisses the informational
defs in `na.AcknowledgedLetterDefs` when the case passes its discovered names as
`ScenarioRuntime.Tools` (`s.Advance` does) and records them under the window's
interruption.

- Threat letters need `WithExpectedLetters(pairs...)`, which also makes the
  window strict: that is what interruption cases want (`Letters`).
- Under the serve process a threat letter's pause drops authority but only
  suspends routine concerns: the next review reactivates the same concern with its
  plans open and held drafts still owned. An informational letter's pause holds
  nothing.

### Small starts

`na.StartDebugGame` arms a 200x200 map on a 5% planet (`na.DefaultDebugStart`;
override a run with `RIMGOVERNOR_ACCEPT_MAP_SIZE` and
`RIMGOVERNOR_ACCEPT_PLANET_COVERAGE`).

- A case reasoning about surrounding terrain calls `na.StartDebugGameSized` (150
  floor, 400 ceiling); fixtures read `map.Size` rather than assuming 250.
- `na.DebugStart.Biomes` (comma-separated `BiomeDef` preference) settles the
  first biome the planet offers and fails when it offers none; the cached start
  is keyed on it.
- Delete a stale `RimGovernor-debug-*` save when a stage refuses for it.
- `StartDebugGame` loads a cached copy of the quick start
  (`RimGovernor-debug-<size>-<coverage>[-<dlc>][-<biomes>][-flat][-seed-<seed>]-quiet|loud` in `profile/Saves`).
  A case about world generation or first-load identity sets
  `RIMGOVERNOR_ACCEPT_CACHED_START=0`; delete the save to pick up a fixture or
  start change that alters the colony.

### Stall-bounded waits

`na.WaitProgress(ctx, na.Wait{Ceiling, Stall, Terminal}, probe)`: the probe
returns a signature (`na.Signature(...)`) and the wait fails once it has not
changed for the stall budget.

- Leave the game tick out of the signature unless the wait tolerates a plan that
  is not moving while the game runs.
- The stall budget is `na.StallBudget()` (1 minute); a case needing longer
  declares `Case.Stall`; `RIMGOVERNOR_ACCEPT_STALL` or `-stall` overrides both.
  A wait needing more than a minute of game work is bounded in ticks
  (`Wait.Ticks`, `RunUntil`), not by a longer stall.
- `RunUntil` resolves a game that stopped under a running speed through the typed
  status read (dismissing `AcknowledgedLetterDefs`); anything else fails the wait
  with a `*na.PauseCause`.
- `wait_stats` in `result.json` (`max_quiet_ms` near the budget) is the evidence
  for a `Case.Stall` or a tick budget.

### Headless preferences

The headless `Prefs.xml` is the player's copy trimmed by `na.TrimPrefs`:
autosaves off (the interval must stay under ~35791 days or the autosaver's int
threshold overflows and saves every tick), run in background, smallest window.
The rendered profile (`PrepareRendered`, `-headless=false`) gets only
`na.RenderedPrefs`.

## The installed build must match the worktree

`Prepare` and `PrepareRendered` refuse, before RimWorld starts, an installed
`Mods/RimGovernor` whose native sources differ from the worktree
(`na.RequireCurrentPackage`). A stale build otherwise fails minutes later and
obliquely (a fixture op missing from discovery, an `INVALID_REQUEST` refusal).

- `scripts/build_native_mod.ps1` records `sourceTree` in `native-manifest.json`
  (`na.SourceTreeHash` reproduces it, so a dirty tree compares correctly).
- The refusal names the rebuild command with the `-Fixture` list this run needs.
- `acceptance run` heals a stale build, or one lacking a needed fixture, by
  stopping the root's own game, rebuilding through `setup` and reinstalling
  (`healed` in `result.json`). `-no-heal` refuses instead, as `suite` always does.
- `RIMGOVERNOR_ACCEPT_ALLOW_STALE_MOD=1` runs against the stale build anyway
  (bisecting the mod against newer Go code).

## Reusing one game across acceptance cases

The runner boots RimWorld once per `acceptance run a b c` and keeps the process
between runs. `nativeaccept.GameReuse`
([reuse.go](../../../go/internal/nativeaccept/reuse.go)) is the lifecycle an
`Owned` case looping over saves drives itself;
[lifecycle/reuse](../../../go/internal/nativeaccept/cases/lifecycle/reuse.go) is
its acceptance and takes `-rimgovernor`. Each reload is checked against a reset
contract (`CheckReset`); a case that fails or ends holding authority or a draft
retires the game rather than handing it on.

### Keeping the process between runs

The runner opens every case's game through `na.OpenSession` and ends it with the
session's `Close`. A serve-driven case releases the session (`s.Serve` does)
while `rimgovernor serve` owns the sole GABP slot and reattaches (`s.Reattach`)
for postmortem reads.

By default `Close` returns the game to the main menu (`test/shutdown_unload`) and
leaves the process running; the next `OpenGame` under the same root attaches and
starts from the menu. The report records `game_reuse` (`reused`, `kept`,
`openMs`, `relaunched`). A kept process is stopped and relaunched when:

| `relaunched` | Cause |
| --- | --- |
| `"expansions"` | a different load order (Core-only process facing a DLC `Save` start, or the reverse) |
| `"unrecorded"` | no launch snapshot (a hand launch) |
| `"package"` | a rebuilt `Mods/RimGovernor` installed under the kept process |

`acceptance warm -root <root>` (`-background` to detach) boots the kept process
ahead of the first run. Stop a kept game with `acceptance stop -root <root>` when
done with the root. `RIMGOVERNOR_ACCEPT_KEEP_GAME=0` opts out; use it, or
`NoKeep`, for a case that asserts on mod static state (process-scoped, survives
reuse; [native-static-state.md](../../../contracts/native-static-state.md)) or
must observe a first boot. `acceptance suite` forces the keep on for its workers.

Reuse does **not** reset process-scoped statics (`OrderedWorkHistory`,
`PlayerUiRevision`, the `Supervisor` journal) or process-wide `Prefs`. A case
asserting on those, or run as static-state or fresh-Go-session evidence,
declares `NoKeep`. Regressions for the kept-process path: `authority/warm`,
`lifecycle/runtime-fault`.

### Running cases in parallel

```
acceptance suite (-all | -cases a,b | -suite file.json | -tier land|full|nightly|matrix|smoke)
    -root <root> -output <out> -workers N
    [-baseline <result.json> -series <metrics.jsonl> -rimgovernor <bin> -evidence capped|full]
```

The suite clones the root into N worker roots (`na.IsolatedRoot`: own endpoint,
config and profile, same game installation) and gives each worker a queue
chained on one kept process. A `-suite` file (`[{"name", "acceptance"}]`) lists
registry cases with the criterion each stands for.

- Queue order: bridge-only first, then process-ending (`NoKeep`, `Rendered`),
  then serve-driven; longest-first by `-baseline` wall times within a group.
- Under `-headless` a `Rendered` case stops a kept headless process and
  vice versa.
- `result.json` lists each case's worker, exit, `wall_ms`, `boot_ms`,
  `game_reuse` and error, plus `regressions` (run time 25% and 5 s over the
  baseline row; flagged, never failing alone), `drift`, `world` and `flake`.
- The suite passes only when every case did.

`cmd/acceptance/suites/issue-6-matrix.json` maps each cross-slice criterion to
its case; `suite_test.go` fails when a criterion loses its row. Its mod build
needs `PowerFixture RefrigerationFixture CleanlinessFixture LightingFixture
FlooringFixture RoutesFixture`; the installed build must carry every fixture a
suite lists.

#### Tiers

`acceptance list -tier <name>` prints a tier; `-cost -baseline
<result.json|metrics.jsonl>` prices it.

| Tier | Command | Contents and use |
| --- | --- | --- |
| **land** | `suite -tier land [-base main]` | The case areas `cmd/affected` selects for the diff plus the smoke set, fresh, on demand before a landing the author wants proven. Shared-plumbing areas contribute one sampled case. `cmd/test` prints the command; `cmd/land -results <output>` reads the suite's `result.json` and refuses a suite that failed or whose rows resumed from a checkpoint. `-results` is optional. |
| **nightly** | `suite -tier nightly` | The end-to-end cases (`endToEnd` in `cmd/acceptance/tier.go`) plus `campaign/*` on CI; a signal, not a gate. |
| **full** | `suite -tier full` | Every other tiered case outside the matrix tier, dispatched on demand on CI (`tier: full`) with `-baseline` for regression flagging; a red row opens an issue. |
| **matrix** | `suite -tier matrix` | Cases declaring `Case.Matrix` (`tickbudget/`, any DLC-save case); run on demand and whenever the clock scheduler or native tick path changes. Neither land nor full runs them. |
| **off-tier** | `acceptance run` by hand | Fixture generators and diagnostics no tier runs (`offTier` in `cmd/acceptance/tier.go`); generators via `acceptance setup generate <variantsave-<save>\|variantsave-all\|baseline>`. |
| **smoke** | `suite -tier smoke` | The land tier's fixed half (`cmd/acceptance/suites/smoke.json`): runner-proving bridge-only cases plus one short serve-driven case (`light/dark`). `TestSmokeSuiteShape` enforces its shape. Extend it with a case proving a runner path the others miss, not one per area. |

Multi-family serve cases (`sustained/food`, `sustained/matrix-*`): the watch is a
tick window, not a wall-clock length; `RIMGOVERNOR_ACCEPT_WINDOW` (Go duration,
default 8m) only ends a game that stops advancing. `result.json` summarizes
`colony_outcome`, `window` and `cadence`; judge a run that starved its colonists
from those, not the flight recorder. A wait on the game outside the watch is a
tick budget through `na.RunUntil`, never a sleep.

The watch fails fast on the journal (`sustainedfood.FailFast`, on by default;
the verdict is the case's error and `fail_fast` quotes the journal text) when:

- an action of the watched concern's committed method ends `unsuccessful` for any
  reason but `interrupted`/`cancelled`;
- the concern stays active/deficit with no method through five consecutive reviews
  that handed its planner the slot;
- the latest worker-step `planner_step` row carries the same native refusal in
  `planner_failures` for six consecutive samples;
- the watched concern is vetoed by a Safeguard while the review names emergency
  needs and the live tick has not moved for twelve consecutive samples.

A case where one is an expected transient sets `FailFast{Disabled: true}` or
raises `NoMethodReviews` / `RefusalSamples` / `ParkSamples`. A baseline-save case
whose kept needs can down a colonist keeps `tend` and `rescue` beside its
families. Generate missing manifest variants with `acceptance setup generate
variantsave-<save>` before the `sustained/matrix-*` case that opens on them.

## Checkpointing a slow precondition

### The checkpoint ring

Every run keeps a checkpoint ring: once a minute of run phase, at the next
natural pause, the runner bundles the save, the service's `service.sqlite`, the
native clock journal and a `checkpoint.json` sidecar into
`<root>/checkpoints/<area>/<case>/t+<offset>/`. It keeps the last five and, on
failure, a final `failed/` bundle. Bundles are disposable per-worktree state,
never committed.

- The next `acceptance run` of that case in the same root resumes from the newest
  entry (`resuming <case> from t+7m ...; -fresh starts over`); `result.json`
  records `resumed_from` and `checkpoints[]`.
- A ring is discarded when the installed native package, the case's `Start` or
  the profile's expansions changed; Go-only changes keep it.
- `-rewind N` resumes N entries earlier; a resumed run failing at the same tick
  rewinds one entry by itself, then starts fresh. `-fresh` clears the ring.
- `-checkpoint-every 0` or a case's `NoCheckpoint` turns capture off;
  `speedmatrix/` and `tickbudget/` never capture. A case that never pauses on its
  own gets only the `failed/` bundle.
- A resumed pass is not a landing pass: `acceptance suite` runs every case fresh
  and fails a row carrying `resumed_from`.

Resume replays the case body from the top against the restored world and store,
so it suits watch-shaped cases (a declarative `Serve` spec or an `Observe` loop).

- A deterministic request id comes from `s.RequestID(base)` (suffixed on a
  resumed run, else the restored store answers `409 conflict`).
- A body that stages its own fixture before the watched phase records it with
  `na.SetCheckpointState(key, value)` and reads it back through `s.Resumed()`
  (`defense/perimeter`).
- A body that reaches a point its later steps cannot resume after caps the ring
  with `na.CapCheckpoints(reason)` (`defense/perimeter`).
- A body whose later steps assume progress a resume has not made, and records
  nothing, declares `NoCheckpoint`.
- A `sustainedfood.WatchConfig` may name phase-boundary `Checkpoints` (tick and
  save name).

### Postmortem-only and dev iteration

A case with a separate `Postmortem` phase reruns only that phase with
`-postmortem-only`: the ring's `failed/` bundle (or `-from t+7m`, `-from failed`,
`-from <bundle dir>`) is loaded on the kept process and `Postmortem` runs against
the reattached harness. It takes none of `-fresh`, `-rewind`, `-repeat`,
`-seed`, needs an empty `-output`, and is refused by the suite and
`cmd/land -results`.

`acceptance dev <area>/<case> -root <root> [-from <label|dir>] [-watch]` iterates
on the code a case's late stage exercises: each iteration builds
`./cmd/rimgovernor` into `<root>/dev/`, stages the bundle like a resume, runs
`Run` and `Postmortem`, then waits for Enter (`q` quits) or, with `-watch`, a
`.go` change. The `Run` body must skip work the bundle carries
(`Session.Resumed`/`Session.Stage`). Output goes to `<output>/dev/<n>/<case>`;
the ring is read, never written; no series row. A `dev` pass is never a landing
pass.

### Breakpoints

`acceptance run <case> -root <root> -break stage=<name>|tick=<n>|minute=<m>
[-headless=false]` cuts the run once the named `Stages` name is done, at the first
natural pause after tick `n`, or at run-phase offset `m` minutes. The case's
services are stopped, a `break/` bundle is taken, and the game is left loaded and
paused on the kept process. The run prints `BREAK` and exits 3 (`result.json`
carries `break`, no `passed`).

- `acceptance resume -root <root> [<case>]` (or a plain `acceptance run`)
  continues from the bundle; `-break` again stops later. `acceptance stop -root
  <root>` discards the breakpoint.
- It needs the ring (not `-checkpoint-every 0`, `NoCheckpoint` or `Owned`) and a
  plain run (none of `-repeat`, `-postmortem-only`, `dev`).

### Staging a slow Run body

A case whose minutes go to its Run body before the assertion declares
`Stages: []string{...}` in order and wraps each staging block in
`s.Stage(ctx, name, fn)`. `fn` returns with every service it launched stopped;
the runner captures a bundle into `<root>/stages/<area>/<case>/<name>/`.

- The next `acceptance run` opens on the newest stage whose native package,
  `Start`, expansions and `stage_key` still match; `Stage` skips `fn` for that
  stage and every earlier one, so code after it cannot tell a hit from a miss.
- `stage_key` hashes `cases/<area>/*.go` plus the stage names: a change to the
  staging code invalidates the bundle, a change to shared helpers does not (use
  `-restage`). Neither the git revision nor the `rimgovernor` binary is in it.
- A pending ring resume wins over a stage.
- `-fresh` keeps stages, `-restage` discards them, `RIMGOVERNOR_ACCEPT_STAGES=0`
  turns the cache off.
- `result.json` carries `staged_from`, `stages[]` and `stage_key`. A staged pass
  is not a landing pass: `acceptance suite` runs every row `-restage` and fails
  one carrying `staged_from`.

`acceptance suite -stages` is the iteration mode over staged cases: each staged
row expands to one work item per missing stage (`acceptance run <case> -through
<stage>`) plus the tail that runs the case to its verdict, chained by dependency;
finished items publish their bundles back into `-root`. It is refused with
`-tier land` and `-resume`, and `cmd/land -results` refuses its report.
`acceptance why` prints a case's stage graph.

## Available checks

| What changed / what you need to establish | Available support | Requirements and limits |
| --- | --- | --- |
| Go controller logic, contracts, persistence | From `go/`: `go run ./cmd/test` (`-full` at the end of an epic; not `go test ./...`), `go build -o ../.rimgovernor/go/rimgovernor.exe ./cmd/rimgovernor` (`task go:build && task go:test` from the [root Taskfile](../../../Taskfile.yml) adds staticcheck and the test-time budget) | Pin Go via [go/.go-version](../../../go/.go-version); `CGO_ENABLED=0`. Linux race tests need CGO/GCC. See [go/README.md](../../../go/README.md). |
| Supply flows (food or resource sources, shocks, runway, starvation) | `go test ./internal/supplysim` from `go/` (`-update` rewrites `testdata/golden.json`) | Fast and offline: a Go test tier that runs in `cmd/test`; simulator matrix scenarios that are slow call `slowtest.Skip`. Assertions are direction and ordering within tolerances, never exact tick equality. |
| Launcher behavior | `go test ./cmd/launcher` from `go/` (Windows-only files build only on Windows) | No game needed; the WebView2 page has no automated check. |
| Shared Protobuf contracts | `task protobuf:build` (C#/Go generation `--check`) | [Generation commands](../../../contracts/schema-generation.md); native adapters additionally need gameplay acceptance. The drift check is not in the landing loop; the nightly `race` job runs it. `task protobuf:test` stays manual. |
| Completed pawn work, recovery or another live-game invariant | A registered case: `go run ./internal/nativeaccept/cmd/acceptance run <area>/<case> -root <abs root> -output <fresh dir>` from `go/`. `acceptance list` is authoritative for the case set | Disposable prepared colony, matching native DLLs and a real headless RimWorld. Never replace installed DLLs while any RimWorld instance is running, including another worktree's. Never kill `RimWorldWin64.exe` by image name (it ends every worktree's game); stop your own via `acceptance stop -root <root>` or kill only pids whose command line contains your `-root`. A receipt alone does not prove pawn work completed. |
| Defense planner threat response or layout (sapper bypass, breach, siege, hive, turret tier, raider cover) | `go test ./internal/buildingruntime -run TestDefenseReplay` from `go/` (snapshot replays) | Fast and offline. Native end-to-end perimeter, raid and repair stay `defense/perimeter` (nightly); also `defense/siege-mortar`, `defense/ied-lane`, `defense/tier-upgrade`. |
| Initial shelter order (spots, beds, then ring) | `acceptance run shelter/bunks-first` (with `-rimgovernor`) against a `HutShellFixture` build | Serve-driven on the tribal8 baseline. Rerun when `shelter_bunks.go`, `rounds_shelter_bunks.go` or the shell search change. |
| Sustained colony upkeep across stages on one journal | `acceptance run upkeep/campaign -root <abs root> -output <fresh dir> -rimgovernor <abs exe>` against an `UpkeepFixture,ForecastFixture,RoundsSleepingFixture,CleanlinessFixture` build | Serve-driven, 10-15 minutes. Rerun when `rounds_upkeep.go`, the cleanliness, animal-feed, medical-reserve or temperature reviews, `domain.ReviewStandard`'s epoch rule or the four fixtures change. |
| Colony extent and explicit expansion | `acceptance run upkeep/colony-extent ... -rimgovernor <abs exe>` against `SleepingFixture,HomeCoverageFixture,UpkeepFixture,ForecastFixture,RoundsSleepingFixture` | Exercises the expansion API across controller restarts; `result.json` records `home_mask_byte_identical`. |
| Connected autonomous Home | `acceptance run upkeep/home-coverage ... -rimgovernor <abs exe>` against `UpkeepFixture,ForecastFixture,RoundsSleepingFixture` | Go tests cover ownership, recurrence and selection after blocked targets. |
| Kept process between runs (clock journal cursor 0 with no gap, fresh Auto grant) | `acceptance run authority/warm -root <abs root> -output <fresh dir>` against any fixture build | `Owned`, `NoKeep`. Rerun when `nativeaccept` profile preparation, `OpenGame`/`OpenSession`, `ClockEventJournal.cs` or the authority hooks change. |
| Pawn outcomes and controller throughput across clock speeds | `acceptance run speedmatrix/plain -root <abs root> -rimgovernor <abs exe>` against a `ThroughputFixture` build | Serve-driven, about 2 minutes per speed; fails when an outcome differs by more than 1 across speeds. Rerun when the clock scheduler, step cache or Ultrafast/acceleration path changes. Fast check first: `go test ./internal/buildingruntime -run TestClockSpeedMatrix`. Report fields: [measure-throughput.md](measure-throughput.md). |
| Native colony/routine read parity against a retained capture | `go test ./internal/observation -run <TestName> -v` with the matching `RIMGOVERNOR_NATIVE_*_CAPTURE`/`RIMGOVERNOR_NATIVE_*_REFERENCE` variable (names per family in [go/README.md](../../../go/README.md)) | Decoding/replay parity only, not a live game. |
| Cross-step fact cache effectiveness | `acceptance run light/dark`: `result.json` `metrics` has `reads_per_step_mean`, `cache_hit_ratio` | Read-only diagnostics on the ordinary case. |
| Where bridge call time goes, wall TPS over a session | Run `serve` with `--flight-recorder <abs path>`, then `go run ./cmd/rimgovernor phases [--json] <abs path>` from `go/` | Read-only over the recorder's segments. `-` means absent, not zero. Field meanings: [measure-throughput.md](measure-throughput.md). |
| Harness waits, decoding, authority ceremony or discovery without a game | `go test ./internal/nativeaccept -run TestReplay` from `go/`; record a transcript with `RIMGOVERNOR_ACCEPT_RECORD=<abs dir>` and open it with `na.ReplayHarness(ctx, path, output)` | Establishes harness behaviour only, never a native one. |

### Choosing checks with the tools

`go run ./cmd/affected` from `go/` prints the checks a change needs, one command
per line:

- the `go test` line for the packages holding the changed Go files plus every
  in-module package importing them (`./...` when `go.mod`/`go.sum` changed);
- one `acceptance run <area>/...` line per case area whose inputs the change
  touched (rules in [Which cases a change owes](#which-cases-a-change-owes));
- a `task probes:build` line when the change touches the native contract probes
  build (a source under `integrations/rimgovernor-native/src`, `contracts/tests`
  or the generated C# protocol classes).

It diffs the working tree (untracked included) against the merge base with
`main` (`-base` for another revision); pass paths to ask about a hypothetical
change. `-files` lists the files considered and why each area was selected.
`-baseline <result.json|metrics.jsonl>` appends the affected set's price
(`acceptance list -cost <area>/...` names untimed cases).

`go run ./cmd/test` runs `go test -short` on the affected packages (slow tests
skip) and the probes build (the landing lane skips it unless `-test`), after
gofmt on changed Go files and `go vet` plus staticcheck on the affected
packages. `-full` runs every test once at the end of an epic, where the
`implement` skill files an issue per failure. Use the single land-tier command
printed by `cmd/test` for milestone validation.

- Run `cmd/test` once before landing; do not follow it with `go test ./...`.
- Changes touching C# or shared Protobuf contracts use `task build && task test`
  for project gates and generation checks.
- Report commands, exit status, skips, artifact locations and what remains
  unverified. Keep failed trials.
- A documentation-only edit needs command/flag and link verification, not a game
  session.
- Do not mark acceptance work complete from unit tests, compilation or native
  receipts alone; verify the observed outcome.
- Use an isolated task worktree when peers may be active. A worktree does not
  inherit the main checkout's build artifacts; run `go test`/`go build` there.

`acceptance list` (the packages under `go/internal/nativeaccept/cases/`) is the
authoritative case set. Coverage gaps are tracked in
[issue #38](https://github.com/davidarcher/rimgovernor/issues/38); trust the
registry when they disagree.
