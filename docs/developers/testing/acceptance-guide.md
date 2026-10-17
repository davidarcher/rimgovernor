# Native acceptance

[Choose tests](choose-tests.md) · [Machine setup](../agent-runbook.md)

## Charter

Every case follows these rules. Each one can be checked from the case declaration or its body.

1. **One native claim.** `Scope` names one native read, operation or simulation outcome, and the case asserts
   it as observed in the game. An accepted order, a fixture's `success` or a planner choice is not a claim. Those
   belong in an xUnit test in `brain/tests`, and only when no existing test covers them.
2. **Stage, don't play.** The precondition comes from the typed stage spec. The behavior under test runs through
   production ops and production observation reads. No case advances the game to create its precondition.
3. **Tick budget.** The case declares its budget in game ticks: at most 30,000 for a lab or fixture start and
   60,000 for any other start. There is no waiver. A case that does not fit is re-staged with an injected
   precondition or deleted; long-horizon play belongs to the colony review (#2351). Wall time is only a hang
   guard: budget ÷ 300 ticks/s + 1 minute.
4. **Wait on the game.** Every wait ends on a tick, a progress signature or an event. No `Task.Delay`,
   `Thread.Sleep` or wall-clock polling.
5. **Typed reads.** Replies are read as `RimGovernor.Brain.Contracts` or `Fixtures.Contracts` records, never by
   string key. The compiler enforces it across the whole harness, cases and subcommands alike.
6. **Telemetry by kind constant.** OTLP rows are selected by a registered kind constant
   ([OTEL signals](../contracts/otel-signals.md#kinds)). No raw kind strings and no matching on log or message
   text.
7. **Shortcuts stop at the precondition.** Staging may spawn, inject and set state. The behavior under test is
   never shortcut. The defaults stay: needs frozen except the case's kept needs, a superpawn crew and the quiet
   storyteller. A case that departs from a default declares the departure and the claim that needs it.
8. **Tier on the case.** The case declares its tier: smoke, nightly or on demand ([tiers](#tiers)).

A case that cannot satisfy rules 1–3 is deleted, not given an exception.

## Writing a case

Every case uses the typed case API, and the compiler enforces the charter. A case is a `TypedCase` record
(`brain/src/RimGovernor.Brain.Acceptance/Cases/TypedCase.cs`), registered with `registry.Register(...)` in
`CaseCatalog`:

| Member | Contract |
| --- | --- |
| `Name` | Required. Stable `<area>/<case>` identifier. |
| `Scope` | Required. The one native claim (rule 1) and why a unit test cannot prove it. It also justifies any departure from a default (rule 7). |
| `Ticks` | Required. At most `StagedTickCap` (30,000) on a lab start or with a `Stage`, `TickCap` (60,000) otherwise (rule 3). Registration fails past the cap. `HangGuard` is `Ticks` ÷ 300 + 1 minute, measured from the body's start, plus `LaunchAllowance` (5 minutes) per game a `ClaimsStart` body launches. It cuts a hung body, and wall time never fails a passing claim. |
| `Tier` | Required. A `CaseTier`: `Smoke`, `Nightly`, `OnDemand` or `Off` (rule 8). |
| `Start` | `LabStart` (the default), `DebugStart`, `SaveStart` or `ScenarioStart`; `OwnedStart` only with `ClaimsStart`. |
| `ClaimsStart` | A `StartClaim`, set only when the start itself is the claim (a new colony from the main menu, a reused game's lifecycle). On an `OwnedStart` it declares `Launches`, the fresh games the body opens through `LaunchAsync` (at least 1), and implies `NoKeep`; the tick cap stays `TickCap`. On a `ScenarioStart` it declares `KeepsStoryteller`, so the runner keeps the scenario's storyteller instead of requiring the quiet one. Registration and the catalog test reject any other combination. |
| `Stage` | A `Fixtures.Contracts` `StageSpec` applied by `test/stage` on the opened start (rule 2). Its reply is `TypedSession.Staged`. |
| `Ops` | The fixture ops the body calls. |
| `Crew`, `Keep`, `QuietWorld`, `Expansions`, `Serve` | The defaults' departures (rule 7). |
| `NoKeep` | The body leaves the process on a game the next case did not open (an unload, a reload, unpatched process state), so the runner does not keep the game. An expansion profile implies it. |
| `Run`, `Postmortem` | The body, over an `TypedSession` (rules 4–6). |
| Variants | There is no member for variants. A data-driven case registers one `TypedCase` per row. A variant that duplicates another's claim is deleted. |

The body's `TypedSession` (`Runner/TypedSession.cs`) carries `Identity`, `Scope` (a read scope pinned to the
identity), `Staged` and `TicksLeft`. Its members:

- **Calls:** `CallAsync(label, NativeOps.<Op>, request)` takes a Contracts request record and returns the reply
  record. `FixtureAsync(label, FixtureOp<,>, request)` calls a bespoke op declared in `Ops`, and `StageAsync(label,
  spec)` re-stages mid-case.
- **Reads:** `BuildingsAsync`, `ZonesAsync` and `GrowingZonesAsync` run over `TypedReads`, and
  `TypedReads.Observed` unwraps a read's `observed` arm.
- **Waits:** `RunTicksAsync`, `RunUntilAsync` (a `Probe` signature) and `RunUntilEventAsync` (a telemetry row).
  Each spends from `Ticks`, and a wait past what is left fails before the game runs.
- **Native clock:** `RunClockUntilHeldAsync(label, ticks, runRequest)` runs the native clock under the request's
  watch policy (combat mode, armed stop events, health thresholds) until the clock holds itself, beating while it
  runs, and returns the held `Clock.Status` (reason, detail, combat event, tick, `HeldAtUnixMs`). It spends from
  `Ticks` like the other waits; a wait that ends otherwise holds the clock before it throws. Call it again with the
  same request to resume after a hold. `HoldRowAsync(label, held)` returns that hold's `clock_hold` row (the one whose
  `stop_at_ms` is `HeldAtUnixMs`; `ClockRows` reads its fields) for what the status does not carry: the thing, the
  detected and occurrence ticks. It spends no ticks. `combatlab/stops` is the worked example. The overload taking a
  `ClockRound` calls it once per round while the clock still runs, before the heartbeat, with that round's status:
  the hook acts mid-run (an injection once the run passes a tick) and returns true to end the wait without a hold,
  for a claim that the clock keeps running (an invalidation row read with `Telemetry`). It returns the held status, or
  the running status of the round that ended it; the clock is held either way. `ClockAccountingAsync(label)` reads the
  supervisor's declared hazard bounds and widest observed gaps. `hazard/*` is the worked example.
- **Telemetry:** `Telemetry(TelemetryKinds.<Kind>)` returns the game log ring's rows of that kind as `LogRow`s.
  A kind is added to `TelemetryKinds` (`brain/src/RimGovernor.Brain.Telemetry/TelemetryKinds.cs`) with its row in
  the kinds table.
- **Evidence:** `Record(key, value)` and `RecordWire(key, record)`.
- **Owned start:** `LaunchAsync(label)` opens a fresh main-menu game (a `GameReuse`) and routes the session's calls
  to it while it is open. `NewColonyAsync(label, request)` polls `lifecycle_new_colony` to its typed outcome
  (phases, then the completed reply or a failure code). `ReadSaveFileAsync(saveName)` and `ProfileSave(saveName)`
  read the profile's save file. The body retires every game it launches.
- **Served wait:** `ServeUntilAsync(label, ticks, probe)` hands the clock to the declared `Serve`. It starts the
  brain service, resumes automatic control under a kept authority, and observes while the brain runs the game until
  the probe is done. Then it stops the service and parks the brain. The budget is game ticks read from the game and
  spent from `Ticks` like the other waits; running past it fails, even when the probe finishes late. The service's
  exit and the stall budget end a run that stops moving, and the hang guard is the wall-clock backstop. A served
  case stages its precondition, and its probe reads the outcome through typed reads and telemetry. It may also
  call a declared fixture op mid-wait to inject the next phase's precondition. `ReviewAsync` (the brain's last
  persisted review, a `Store.Rounds`) and `NeedTransitionsAsync` (the journal's need_transitions rows) read the
  run's durable journal, the brain-side state a served probe or audit needs.

`lab/stage`, `lab/spawn` and `upkeep/auto-home-area` are the reference ports.

A new area adds its registration to `CaseCatalog.Register`. The gate checks every case without a game
([acceptance-case gates](../architecture/rules.md#acceptance-cases)): the catalog test checks each case's
cap, claim and tier. RS0030 fails any file in `RimGovernor.Brain.Acceptance` or `RimGovernor.Brain.Acceptance.Cases`
that calls `Task.Delay` or `Thread.Sleep` or reads a `JsonNode` by key or index; the banned list is
`brain/analyzers/BannedSymbols.Acceptance.txt`.

### Staging

The stage spec is a typed, declarative description of the precondition. It is built from a small set of
primitives (spawn pawns, items, plants and buildings, clear an area, set priorities and timetables, set needs, research,
world, incident and quest state). The primitives live in the separate `Fixtures.Contracts` assembly. That assembly
is compiled into fixture builds only, and production projects never reference it. A bespoke `test/*` op is
added only for something the primitives cannot express: a fault hook, a recorder, an equality or benchmark check,
or a content flow. Read-only probes are not test ops: a missing read is promoted into the
[observations contract](../contracts/README.md) and read through production.

The spec is `StageSpec` (`brain/src/RimGovernor.Fixtures.Contracts`). Its primitives are research (`StageResearch`:
finish a project, or set its progress), world (`StageWorld`: pinned outdoor temperature, clock hour), find site
(`StageSite`), clear area (`StageClear`), set cells (`StageCells`: terrain, roof, home, fog), build room
(`StageRoom`), place building (`StageBuilding`: complete, frame or blueprint, a finished one damaged or fuelled), add bill (`StageBill`: a repeat-count production bill on a staged work table), spawn thing (`StageThing`), spawn plant (`StagePlant`: a plant def at a growth), spawn or
set pawn (`StagePawn`: kind, faction, age, skill level, cleared traits, gear, hediffs, work, timetable, needs, mental
state, inspiration), zone (`StageZone`), the raid that drives staged hostiles (`StageRaid`) and fire incident
(`StageIncident`: an IncidentDef through its own worker, optionally with a trader kind). One fixture op, `test/stage`,
applies a spec and replies a `StageReply` keyed by the names in the spec: each entry's ids and cells and the incidents'
traders. A case stages through
`FixtureStage`: `FixtureStage.Start(spec)` as its start (the reply is `FixtureStage.Prepared(s)`), or
`FixtureStage.ApplyAsync` mid-case. The shared primitives on the mod side live in `scripts/fixtures/FixtureKit.cs`.
The native setup refuses a production Bridge that carries a contracts type or a `test/*` op.

A lab start is a pinned blank 100×100 map with a colonist count. Spawn at known offsets from its center. A
prepared save or scenario is appropriate when history, biome or season is the precondition.

A paused lab never advances the tick, and RimWorld's pathfinder gathers its map
data once per tick. A fixture that spawns or changes buildings, rock or terrain
and then reads routes without ticking must dirty that data first
(`PathFinderMapData.Notify_MapDirtied`);
otherwise `FindPathNow` paths over stale connectivity and finds nothing while
`CanReach` still answers true.

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
  explicitly with the case's expansions or `RIMGOVERNOR_ACCEPT_EXPANSIONS`.
- Keep needs the case tests unfrozen; freeze the rest. Use `QuietWorld` only when wild-map
  simulation is irrelevant, not for farm, hunting or husbandry claims.
- Service-driven waits use a progress signature that ends on service exit: `ServeUntilAsync`.
- Advance gameplay by ticks with `RunTicksAsync`, `RunUntilAsync` and `RunUntilEventAsync`. The speed matrix
  reports hold counts by reason, resume latency and effective speed instead of window counts
  ([measure-throughput](measure-throughput.md#across-clock-speeds)).
  Fail immediately on terminal refusals or service exit.

The baseline is generated from `BaselineStart` on first use and stamped
beside its save; a changed spec regenerates it. `RimGovernor.cmd acceptance
baseline -root <dir> [-game <id>] [-rendered]` regenerates it on demand, even
when the stamp is current. A retained seed/save/fixture hash
identifies the input actually exercised.

The lab and debug starts cache as `RimGovernor-lab-*` and `RimGovernor-debug-*`
saves in `profile/Saves`, named for the expansions the prepared profile
activates (all installed ones when nothing narrows them). A lab save carries a
`.stamp` with the installed package's hash, so a mod rebuild regenerates it.
Suite and sweep worker roots receive copies of these files, so workers load the
cache instead of generating a start.

### Harness helpers

Runner and case code use these helpers instead of raw JSON and fixed sleeps:

- **Host tools:** `Harness.SetTimeSpeedAsync(label, TimeSpeedRequest)`, `PauseClockAsync(label)` and
  `LoadGameReadyAsync(label, LoadGameRequest)` call `rimgovernor/set_time_speed` and `rimgovernor/load_game_ready`
  with the request and reply records in `RimGovernor.Contracts.Host` (`Brain.Contracts/HostTools.cs`). A load
  request without its own `TimeoutMs` waits `Harness.LoadWait`.
- **Service replies:** `ServiceJson.State`, `Health` and `Session` (`Service/ServiceReplies.cs`) read the service's
  `/api` replies into harness-side records (`ServiceState`, `ServiceHealth`, `PlayerSession`) and throw an
  `AcceptanceException` on a bad shape. The service's PlayerApi DTOs stay internal to the host.
- **Output documents:** `OutputJson` (`Harness/OutputJson.cs`) builds result.json blocks, summaries and digests
  without string indexers: `Array`, `Fields` (ordered key/value pairs), `AddIfPresent` (an optional field left out
  when absent) and `Node` (a typed record through its source-generated type info). A `new JsonObject { ... }`
  initializer works too.
- **Pacing:** `Wait.Pace(interval)` returns a `Pacer` over one `PeriodicTimer`. It is the harness's only pacing
  point: a poll loop holds one with `using var pace = Wait.Pace(...)` and awaits `pace.NextAsync(token)` between
  probes, instead of `Task.Delay`. Prefer a real event wait (a task, a process exit, a telemetry row) whenever one
  exists.
- **Telemetry rows:** readers select log rows by `TelemetryKinds` (`Brain.Telemetry/TelemetryKinds.cs`), never by a
  kind string. A kind a reader needs is added there with its emitter's constant.

## Tiers

- **smoke**: runner-proving cases over a kept debug game plus one short service-driven case (`smoke/identity`
  proves the in-proc launch and fixtures, `light/dark` the first service-driven case). Extend it with a case that
  proves a runner path the others miss, not one per area.
- **nightly**: whole-colony proofs, run against main every night. It is a signal, not a landing gate.
- **on demand**: every other case, run by name or by the remote workflow's cases tier.

`acceptance list -tier <name>` prints a tier's cases. Every case declares its tier as `Tier = CaseTier.<Tier>`.
`Off` covers fixture
generators and diagnostics. Tiers never run them, and neither does `acceptance suite -all`.
`brain/src/RimGovernor.Brain.Acceptance/Cost/Tiers.cs` builds each tier from these declarations.

## Run and diagnose

The runner is `RimGovernor.cmd acceptance <subcommand>` (`acceptance <subcommand> -h` lists flags). The harness core,
registry, setup, doctor, postmortem, run, suite and the other subcommands live in
`brain/src/RimGovernor.Brain.Acceptance`; cases live in `brain/src/RimGovernor.Brain.Acceptance.Cases/<Area>`. The
nightly and the remote workflows run this harness against the in-process C# brain. The harness drives the game over
the mod's local HTTP channel. There is no child process and no `games_stop`: a stop kills the recorded process tree.
Every game it launches (headless, rendered, warm) carries `-rimgovernor-inproc` and `RIMGOVERNOR_OBSERVE=1`: the mod
hosts the brain, and a case starts the brain through the player API instead of the host's auto-resume. There is no
opt-out. The launcher's Play passes the same flag.

On a prepared root (`RimGovernor.cmd acceptance setup` once, then one game at a time):

```text
RimGovernor.cmd acceptance list -tier smoke
RimGovernor.cmd acceptance suite -tier smoke   -root <abs root> -output <fresh dir>
RimGovernor.cmd acceptance suite -tier nightly -root <abs root> -output <fresh dir> -workers 1
RimGovernor.cmd acceptance run <area>/<case>   -root <abs root> -output <fresh dir>   # one failing case
RimGovernor.cmd acceptance why <output>/<area>/<case>
RimGovernor.cmd acceptance stop -root <abs root>
```

The remote workflow's `nightly` tier is the same set minus rendered cases. Run the smoke tier first, then the
nightly tier.

The runner owns package checks, profile preparation, game connection, discovery,
start, pause, frozen needs, reporting and cleanup. A case receives its fixture
reply from the runner.

Each case writes `result.json`, native-call/service evidence and its game-log
slice. Failures add `diagnosis.txt`; `acceptance why` reads the same digest.
The report's `world` records seed, save hash and fixture hash. Large replies
are capped by default; `-evidence full` retains them. Acknowledging an
interruption permits progress but does not establish that the fault is harmless.

Use `-repeat N` to reproduce a stochastic failure under one build and
`-seed <s>` for a recorded debug/scenario seed. A save start uses its saved world
and rejects a seed override.

Filing failures: one GitHub issue per distinct failure (check for an existing match first), labelled `area:tooling`
for harness/mod-host faults or the owning area label and `priority:P1`/`P2`, titled with the case name. Include the
case name, the commit, the `result.json` verdict, the `acceptance why` digest, the game-log slice and whether it
reproduces alone (`-repeat N`). Say whether the failure is the brain, the in-process host (bootstrap, channel,
`RIMGOVERNOR_*` environment), or the harness; a case that fails to start or a missing route belongs to the host.
A load-sensitive failure that passes alone is recorded, not rerun
as a suite.

## Reusing one game across acceptance cases

The runner keeps a fixture-enabled game by default. Declare `NoKeep` for a case
that terminates/replaces the process; an `Owned` start must do so. A kept game
retains its loaded mod configuration: stop it before changing expansions or
installing a new mod build. A fixture-less production build requires fresh boot.
The game starts detached and inherits only its own `game.log` handle, so a redirected
`acceptance run` returns when the case ends while the kept game stays up.

A run that cannot succeed fails at once with its cause as the `error`: the game process
exited (exit code and `game.log` tail), or two consecutive native calls got no reply
(`native unreachable`). A checkpoint whose native reads fail ends the run the same way.

`acceptance warm -root <root>` prepares the menu before a run.
Use the suite runner for worker isolation and parallel cases rather than
sharing one profile between independent runners.

## Colony generator

`acceptance colony -root <dir> [-output <dir>] [-ticks 300000] [-watch <d>]
[-every 1m] [-timeout <d>] [-game <id>]` plays the tribal8 baseline under the
governor for `-ticks` game ticks (five days by default) with a staffed crew and
writes a checkpoint ring to `<root>/checkpoints/sustained/colony`, passed or
failed. It has its own driver (`brain/src/RimGovernor.Brain.Acceptance/Colony`)
outside the case runner and the case catalog. At the window's end it audits the
keep-alive resumes, every colonist's malnutrition and the stockpile zones, and
writes `result.json` to `-output` (default `<root>/acceptance/colony`). Exit 0
is a pass, 1 a failed window and 2 a usage error. The fixture factory runs it
weekly ([remote workflow](remote-workflow.md#fixture-factory)); `acceptance
profile-capture` and `acceptance fetch-fixture` read its ring by default.

`acceptance colony-review` is the same kind of driver for the nightly
[colony review](colony-review.md): a season on a fresh pinned-seed scenario start with the storyteller
on, recording screenshots beside `result.json`. It gates nothing. `acceptance speedmatrix` is the clock-speed
measurement's ([measure throughput](measure-throughput.md#across-clock-speeds)).

## Combat lab sweeps

`acceptance sweep <fixture>... -root <dir> -output <fresh dir>
[-n 20 | -seeds 1,2,5] [-workers 1|2] [-against <sweep.json|dir>] [-timeout 10m]`
runs each combat fixture once per seed, 1..N by default. The sweep has its own
driver (`brain/src/RimGovernor.Brain.Acceptance/Sweep`): it does not go through
the case runner or the case catalog and keeps no checkpoints. Every seed opens
the game in process on its worker root, stages the fixture fresh through the
`Fixtures.Contracts` stage spec, reseeds the game RNG with the seed (the lab's
`test/lab_stage` action `reseed`, a harness-only test tool and not a model
action), serves the brain for the fixture's ticks and reads the outcome. Each
seed's `result.json` carries `run_seed` and `run_seed_draws`. The reseed fixes
the RNG start only: once two tactics diverge their draws diverge too, so paired
seeds compare starting dice, not identical dice; each seed's reseed reply must
echo its seed and carry draws. Seeds are dispatched with `CombatDispatcher.Dispatch` on 1 or 2
private clones of `-root` (more is refused); the result records the workers and
a RimWorld process count taken before dispatch, so a timing-dependent metric can
be read against box load. `-timeout` bounds one seed (10 minutes by default).

`<output>/<fixture>/sweep.json` is the sweep file (versioned): one row per seed (metrics, staging digest, reseed draws)
plus a summary. The printed report leads with the seeds that saw a colonist
death or down and the means of colonist deaths and downs with 95% t intervals,
then damage, enemy outcomes and contact-to-resolution. A second sweep with the
same seeds reads the same staging digest and reseed draws per seed. `-against`
pairs the seeds of an earlier sweep file and prints the paired deltas; it exits
1 when a seed gains a colonist death or the paired interval of colonist deaths
or downs lies wholly above zero.

Every seed also records the shot tap (`LabShotLedger` in
`scripts/fixtures/DebugStartFixture.cs`, harness only): each projectile launch
and impact, job starts, stances, draft changes and each pawn's first think
ticks, read with `test/lab_stage` action `shots` into `combat_shots.jsonl` in the
seed directory. `acceptance sweep <fixture> -variant <name>` sweeps a tactic variant of
the combat-lab variants in the real game: it launches the game with the test-only
`RIMGOVERNOR_COMBAT_TUNING` environment variable (a kept game launched with a
different value is relaunched), and with
`-record` writes `brain/src/RimGovernor.Brain.Acceptance/Sweep/calibration/variants/<fixture>/<variant>.json`
instead of the baseline (the `default` variant is the baseline and is refused).
The variable is a JSON `CombatTuning` (fields over `CombatTuningRules.DefaultCombatTuning`,
unknown fields and out-of-range values refused at start, unset means
inert); the brain host reads it, the defense planner seeds every new fight's
`CombatMemory.Tuning` with it (the ordinary admission and the sapper break), and
the policy layer itself never reads the environment. A variant moves a fight only where
its knob is read: `ScatterCells` and `GroupGap` act under a layout (a hold that
collapses, a raid split into groups), so a layout-less fixture measures the same
play under every variant.
`acceptance sweep <fixture> -shots <file>` concatenates the seeds' rows,
stamped with the seed and with the damage each impact dealt. The committed
`Sweep/calibration/shots/lab-ranged.jsonl` and its README (fields, how it
was recorded) are the real timing data for combat simulator calibration.

The combat baselines are sweep outputs: `Sweep/baselines/<fixture>.json` is
the sweep of seeds 1..20 recorded from main with the routine
defense planner running. A tactic is first compared on one seed (three at most);
a full sweep of the same seeds runs only when asked or by the nightly, with `-against
brain/src/RimGovernor.Brain.Acceptance/Sweep/baselines` and gets paired deltas row
by row. Re-record one with `acceptance sweep <fixture> -record`
(seeds 1..20 only; a failed seed, a seed with no contact or no orders, or a
summary that is not derived from the rows is refused). Re-record only when asked or by the nightly (not as a
default step of a fix), after a schema bump or an intended change to the
fixture or planner. lab-open
is smoke-only: recorded, but not a tactics signal.

`acceptance sweep hunting-grid-<cell>` runs a hunting qualification cell; see
[hunting prediction qualification](hunting-prediction-qualification.md).
