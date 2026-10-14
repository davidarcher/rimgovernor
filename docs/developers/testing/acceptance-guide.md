# Native acceptance

[Choose tests](choose-tests.md) · [Machine setup](../agent-runbook.md)

The runner is `RimGovernor.cmd acceptance <subcommand>` (`acceptance <subcommand> -h` lists flags). The harness core,
registry, setup, doctor, postmortem, run, suite and the other subcommands live in
`brain/src/RimGovernor.Brain.Acceptance`; cases live in `brain/src/RimGovernor.Brain.Acceptance.Cases/<Area>` and
register through `CaseCatalog.Register`. It is the harness the nightly and the remote workflows run, against the
in-process C# brain. The harness drives the game over the mod's local HTTP channel; there is no child process and no
`games_stop`, a stop kills the recorded process tree.
Every game it launches (headless, rendered, warm) carries `-rimgovernor-inproc` and `RIMGOVERNOR_OBSERVE=1`: the mod
hosts the brain, and a case starts the brain through the player API instead of the host's auto-resume. There is no
opt-out. The launcher's Play passes the same flag.

## The nightly-tier subset

To run the subset by hand on a
prepared root (`RimGovernor.cmd acceptance setup` once, then one game at a time):

```text
RimGovernor.cmd acceptance list -tier smoke
RimGovernor.cmd acceptance suite -tier smoke   -root <abs root> -output <fresh dir>
RimGovernor.cmd acceptance suite -tier nightly -root <abs root> -output <fresh dir> -workers 1
RimGovernor.cmd acceptance run <area>/<case>   -root <abs root> -output <fresh dir>   # one failing case
RimGovernor.cmd acceptance why <output>/<area>/<case>
```

The remote workflow's `nightly` tier is the same set (`Tiers.CasesOf("nightly")`, minus rendered cases). Run the smoke
tier first (`smoke/identity` proves the in-proc launch and fixtures, `light/dark` the first service-driven case), then
the nightly tier. The nightly-tier subset is `Tiers.EndToEnd` in `brain/src/RimGovernor.Brain.Acceptance/Cost/Tiers.cs`:

- combatlab/native-hold
- campaign/foothold, campaign/recovery, clearance/shrine-breach
- defense/perimeter, food/reserve, food/starving-tribal-recovery, production/ladder
- shelter/bunks-first, shelter/retirement, shelter/climate-mild, shelter/climate-cold, shelter/climate-hot, shelter/solo-cold
- startup/labor, solo/sea-ice-stock, solo/sea-ice-rhadamant
- sustained/colony-stable, sustained/winter, upkeep/campaign, sleeping/suites
- layout/ring, layout/rich-soil, layout/sea-ice-envelope, layout/storage-successor
- mood/headroom, mood/ledger, mood/gathering
- explain/every-concern

Filing failures: one GitHub issue per distinct failure (check for an existing match first), labelled `area:tooling`
for harness/mod-host faults or the owning area label and `priority:P1`/`P2`, titled with the case name. Include the
case name, the commit, the `result.json` verdict, the `acceptance why` digest, the game-log slice and whether it
reproduces alone (`-repeat N`). Say whether the failure is the brain, the in-process host (bootstrap, channel,
`RIMGOVERNOR_*` environment), or the harness; a case that fails to start or a missing route belongs to the host.
A load-sensitive failure that passes alone is recorded, not rerun
as a suite.

## Run and diagnose

```text
acceptance list
acceptance run <area>/<case> -root <absolute root> -output <fresh directory>
acceptance why <output>/<area>/<case>
acceptance stop -root <absolute root>
```

The runner owns package checks, profile preparation, game connection, discovery,
start, pause, frozen needs, reporting and cleanup. A case receives its fixture
reply through `s.Prepared()`.

Each case writes `result.json`, native-call/service evidence and its game-log
slice. Failures add `diagnosis.txt`; `acceptance why` reads the same digest.
The report's `world` records seed, save hash and fixture hash. Large replies
are capped by default; `-evidence full` retains them. Acknowledging an
interruption permits progress but does not establish that the fault is harmless.

Use `-repeat N` to reproduce a stochastic failure under one build and
`-seed <s>` for a recorded debug/scenario seed. A save start uses its saved world
and rejects a seed override.

## Adding a case

Register an `AcceptanceCase` (`brain/src/RimGovernor.Brain.Acceptance/Cases/AcceptanceCase.cs`) with
`registry.Register`; a new area adds its `<Area>Cases.Register` to `CaseCatalog.Register`.

| Field | Contract |
| --- | --- |
| `Name` | Stable `<area>/<case>` identifier. |
| `Scope` | Exact claim, including why a snapshot cannot prove it. |
| `Budget` | Minute-scale runtime; exceeding `AcceptanceCase.MaxBudget` needs a `Reason`. |
| `Start` | Prepared save, fixture, lab, scenario, or `Owned` lifecycle. |
| `RequiredOps` | Fixture operations called by the body or service hooks. |
| `Run` | Exercise the behavior and watch progress. |
| `Postmortem` | Read and assert native outcomes after services stop. |

For service-driven cases, declare `Serve = new ServeSpec { ... }` (the harness-side session: save, families,
environment entries). Store assertion inputs with the checkpoint state, `Session.Resumed` or
`Session.Prior` so postmortem can run independently.

### Stage the precondition, do not play into it

Start with the state the assertion needs. Native op/read cases normally use
`LabStart` with a colonist count: a pinned blank 100×100 map.
Spawn at known offsets from its center. Terrain-dependent cases need an explicit
`Reason`.

Use one `test/*_prepare` operation to spawn, assign, damage and settle the
fixture. Use production operations for the behavior under test. A prepared save
or scenario is appropriate when history, biome or season is the precondition;
do not grow a colony for twenty minutes to arrange a small assertion.

Try a fixture without writing a case:

```text
acceptance fixture <op> [key=value ...] -root <root>
acceptance fixture <op> [key=value ...] -root <root> -loaded
```

The first call loads the baseline; `-loaded` reuses the paused world.
JSON-looking values decode as JSON; other values are strings.

### Keep the game quiet and small

- Prefer Core-only unless the claim requires DLC. The runner's default activates
  installed expansions; a save start activates its saved `modIds`. Narrow
  explicitly with `AcceptanceCase.Expansions` or `RIMGOVERNOR_ACCEPT_EXPANSIONS`.
- Use a quiet storyteller. Needs are frozen except the case's `Keep` list;
  preserve the needs being tested. Use `QuietWorld` only when wild-map
  simulation is irrelevant, not for farm, hunting or husbandry claims.
- Use `WaitProgress` with a progress signature that ends on service exit
  for service-driven waits. Existing `WaitReview`, `WaitPlan` and `WaitRounds`
  helpers cover common cases. No sleep loops.
- Advance gameplay by ticks with `RunUntil`/tick waits.
  Wall time is a hang guard. Fail immediately on terminal refusals or service exit.
- Declare budgets in minutes. The default timeout is 20 minutes and cannot be
  less than budget plus five; it is separate from the case budget.
- `AcceptanceCase.Lint` checks the case before execution.
  Departures requiring `Reason` must explain the tested constraint.

The baseline is generated from `BaselineStart` on first use and stamped
beside its save; a changed spec regenerates it. A retained seed/save/fixture hash
identifies the input actually exercised.

## Reusing one game across acceptance cases

The runner keeps a fixture-enabled game by default. Declare `NoKeep` for a case
that terminates/replaces the process; an `Owned` start must do so. A kept game
retains its loaded mod configuration: stop it before changing expansions or
installing a new mod build. A fixture-less production build requires fresh boot.

`acceptance warm -root <root>` prepares the menu before a run.
Use the suite runner for worker isolation and parallel cases rather than
sharing one profile between independent runners.

## Checkpointing a slow precondition

A failed run normally resumes from its latest checkpoint on the next run in the
same root. `-fresh` starts over; `-rewind N` chooses an earlier checkpoint.
Keep the report's `resumed_from` with the result.

A resumable body must recognize `s.Resumed()`, restore recorded fixture state
and avoid repeating writes already represented by the bundle. Use
`RequestID(base)` for request IDs; resumed attempts get a distinct suffix.
Declare `NoCheckpoint` if the body cannot safely resume, or
a checkpoint cap with a reason after its last resumable point.

### Postmortem-only and dev iteration

`acceptance run <case> -postmortem-only [-from <label|directory>]` loads a retained
bundle and runs only `Postmortem`. It requires a fresh output directory and
cannot combine with fresh/repeat/seed options or a suite.

`acceptance dev <case> -root <root> [-from <label|directory>] [-watch]` rebuilds
the brain, reloads the bundle and runs `Run` plus `Postmortem`.
It waits for Enter, or a brain-file change with `-watch`, between iterations.
Outputs are separate; the checkpoint ring and metrics series are unchanged.

### Breakpoints

`acceptance run <case> -break stage=<name>|tick=<n>|minute=<m>
-headless=false` stops at the chosen boundary, bundles state and leaves the game
visible and paused. It prints `BREAK` and exits 3, not a pass.

Continue with `acceptance resume -root <root>`; discard with `acceptance stop`.
Breakpoints require checkpoint support and cannot combine with repeat,
postmortem-only or dev mode.

### Staging a slow Run body

Declare ordered `Stages` and wrap setup blocks in `s.Stage(ctx, name, fn)`.
Each block must stop its services before returning. The runner stores a bundle
and reuses the newest matching native package, start, expansions and stage key.

The stage key covers the area's brain files and stage names, not shared helpers. Use `-restage` when those affect the precondition.
`-fresh` retains stages; `-restage` discards them. A pending resume takes
precedence. `RIMGOVERNOR_ACCEPT_STAGES=0` disables caching.

A staged checkpoint is staged once, at seed 0. `-run-seed <n>` reseeds the
game RNG after the checkpoint loads (`combatlab/metrics-*`, through the lab's
`test/lab_stage` action `reseed`, a harness-only test tool and not a model
action), so seeds 1..N run from one bundle; the report carries `run_seed` and
`run_seed_draws`. The reseed fixes the RNG start only: once two tactics diverge
their draws diverge too, so paired seeds compare starting dice, not identical
dice. `combatlab/reseed` checks the action itself.

## Combat lab sweeps

`acceptance sweep <fixture>... -root <dir> -output <fresh dir>
[-n 20 | -seeds 1,2,5] [-workers 1|2] [-against <sweep.json|dir>] [-restage]`
runs `combatlab/metrics-<fixture>` once per seed, 1..N by default. The fixture
is staged once (a cached stage bundle is reused), every worker root gets its
own copy of that bundle, and seed `s` runs `-run-seed s` from it. Seeds are
dispatched with `CombatDispatcher.Dispatch` on 1 or 2 private clones of `-root`
(more is refused); the result records the workers and a RimWorld process count
taken before dispatch, so a timing-dependent metric can be read against box load.

`<output>/<fixture>/sweep.json` is the sweep file (versioned): one row per seed (metrics, staging digest, reseed draws)
plus a summary. The printed report leads with the seeds that saw a colonist
death or down and the means of colonist deaths and downs with 95% t intervals,
then damage, enemy outcomes and contact-to-resolution. A second sweep with the
same seeds reads the same staging digest and reseed draws per seed. `-against`
pairs the seeds of an earlier sweep file and prints the paired deltas; it exits
1 when a seed gains a colonist death or the paired interval of colonist deaths
or downs lies wholly above zero.

Every metrics run also records the shot tap (`LabShotLedger` in
`scripts/fixtures/DebugStartFixture.cs`, harness only): each projectile launch
and impact, job starts, stances, draft changes and each pawn's first think
ticks, read with `test/lab_stage` action `shots` into `combat_shots.jsonl` in the
bundle. `acceptance sweep <fixture> -variant <name>` sweeps a tactic variant of
the combat-lab variants in the real game: it sets the test-only
`RIMGOVERNOR_COMBAT_TUNING` environment variable on the child service, and with
`-record` writes `combatlab/calibration/variants/<fixture>/<variant>.json`
instead of the baseline (the `default` variant is the baseline and is refused).
The variable is a JSON `CombatTuning` (fields over `PolicyFunctions.DefaultCombatTuning`,
unknown fields and out-of-range values refused at start, unset means
inert); the brain host reads it, the defense planner seeds every new fight's
`CombatMemory.Tuning` with it (the ordinary admission and the sapper break), and
the policy layer itself never reads the environment. A variant moves a fight only where
its knob is read: `ScatterCells` and `GroupGap` act under a layout (a hold that
collapses, a raid split into groups), so a layout-less fixture measures the same
play under every variant.
`acceptance sweep <fixture> -shots <file>` concatenates the seeds' rows,
stamped with the seed and with the damage each impact dealt. The committed
`CombatLab/calibration/shots/lab-ranged.jsonl` and its README (fields, how it
was recorded) are the real timing data for combat simulator calibration.

The combat baselines are sweep outputs: `CombatLab/baselines/<fixture>.json` is
the sweep of seeds 1..20 recorded from main with the routine
defense planner running. A tactic is first compared on one seed (three at most) and widened in the sim;
a full sweep of the same seeds runs only when asked or by the nightly, with `-against
brain/src/RimGovernor.Brain.Acceptance.Cases/CombatLab/baselines` and gets paired deltas row
by row. Re-record one with `acceptance sweep <fixture> -record`
(seeds 1..20 only; a failed seed, a seed with no contact or no orders, or a
summary that is not derived from the rows is refused). Re-record only when asked or by the nightly (not as a
default step of a fix), after a schema bump or an intended change to the
fixture, planner or simulator. lab-open
is smoke-only: recorded, but not a tactics signal.

## Simulator calibration gate

`RimGovernor.cmd combatcalibrate [-record | -show report.json]` decides whether
combat simulator results may be trusted for the ranged slice (#2844). It plays each
fixture in the calibration and held-out fixture lists in the sim,
under each combat-lab variant tuning, over the baseline's seeds, and
compares the result with the real-game sweeps: the baselines for the default
tuning, and `CombatLab/calibration/variants/<fixture>/<variant>.json` (a
sweep file from `acceptance sweep`, seeds 1..20) for the others.
The thresholds are constants in the calibration gate: pooled pairwise rank
agreement >= 0.80 on held-out fixtures, zero survival-critical misrankings (the
sim prefers a variant whose real sweep has more colonist deaths), and colonist
deaths and downs within +/-25% of the real mean or +/-1 pawn. Variants are
ordered deaths first, then downs, then colonist damage.

The gate fails closed and exits 1: a fixture the sim cannot import (melee
pawns, turrets, staged conditions), a variant with no real sweep, a sweep with
defects or a different seed list, and a role with nothing measured are
failures. The report also carries native regret (the real outcome lost by
following the sim's pick) and false-safe variants (the sim sees no colonist
death, the real sweep does). `calibration/report.json` and `report.txt` are the
recorded output, stamped with the pinned assembly hash,
`RulesVersion`, the thresholds version and the sweep schema; a stale stamp or
output is a stale report. Bump `RulesVersion` with any port or approximation change that can move
an outcome, then re-record. A failing report is a valid record; the sim is
trusted only where a passing report of this build accepts it.
`lab-split-ranged` is the held-out fixture built so both variant knobs act: four
riflemen with a hand-written layout (firing line behind four stone chunks)
against eight rifle gunners in two clusters (four cells apart inside a cluster,
sixteen between the clusters). The line is outmatched, so `ScatterCells` applies
once it falls below half; the clusters are two raid groups at `GroupGap` 8,
singletons (below the group minimum) at 3 and one group at 20. Its
`CombatLab/calibration/variants/lab-split-ranged/` files are the four
non-default sweeps.

A held-out fixture joins the gate by becoming importable and gaining real
variant sweeps; the fixture lists and variants are fixed before measuring.

`acceptance suite -stages` schedules missing stages and their dependent tails
for iteration. The ordinary suite restages from scratch. Preserve
`staged_from` when reporting cached evidence; see
[result provenance](choose-tests.md#full-cached-and-resumed-runs).
