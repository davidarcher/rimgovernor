# Brain host

The brain host (`brain/src/RimGovernor.Brain.Host`, `BrainHost`) runs the C# brain inside the mod's process behind the
launch flag `-rimgovernor-inproc` (`InProcLaunch.Flag`). There is no separate process, transport, lease or reconnect machinery: the brain and the game
share a process, so a link cannot drop under it.

The optional [encounter predictor](../contracts/encounter-prediction.md) is an
isolated hypothetical worker behind a typed replaceable interface. Host owns its
bounded process lifecycle; it has no live colony facade or order authority. This
is not another brain/controller. The current service is research-only and is not
composed into live hunting selection until qualification and #3130 integration.

## Lifecycle

`BrainHost.StartAsync(options, ct)` in order: validate options; start the OTLP file writer (a failure is reported and
the rows go nowhere, it never stops the brain); register the intent kinds (`BridgeFunctions.RegisterIntentKinds`); open the state database (an incompatible one is moved aside); compose the session, the player and the
planners; create the Worker; register the player routes; start the save-signal loop, the auto-resumer and the
scheduler thread. A failure unwinds what was built and throws. `StopAsync` is idempotent and ends the loops, drains
the Worker (closing player and session, bounded retries), closes the database and flushes telemetry; errors are
collected and thrown together after everything else ran.

## Scheduler thread and authority

One dedicated thread (`rimgovernor-brain-scheduler`) steps the `ClockScheduler` with the `ClockWorker.stepLoop`
cadence: first step Full, delay doubling to the maximum on an unchanged step key, evidence resets it, a settled step
after the run driver held or resumed the clock. It idles unless the player is enabled with a known observation. Authority is the loaded world's:
`FacadeAuthority` implements `INativeAuthority` over the facade, and `AutoResumer` offers each fresh world one resume
through the same control intent the resume route submits.

## Fault isolation

Each step runs on the pool behind a guard: past `StallAfter` it is recorded as a stall, past `AbandonAfter` the thread
stops waiting and the brain is wedged. `FaultLimit` consecutive faults or any stall/wedge suspends authority
(`Session.Disable`), logs it, and keeps the game running; only the player's resume clears the suspension. Hazard stops
stay native and never wait on a planner. Planners that miss the wave cutoff `PlannerMissLimit` times in a row are
reported in `BrainHost.Status()`.

## Native client and composition (#3113)

`FacadeNativeClient` (`Client/`) is the planners' native client, with no transport: one class over `INativeFacade` that implements the ~70 Native/Source interfaces the
planners are written against. Each `Client.ReadX` is one `ReadXAsync` core method (request, facade call, outcome
unwrap, the reply validators); the interfaces are bound to them in `FacadeNativeClient.Interfaces*.cs`, with
explicit implementations where the interface shape differs from the core method's. A failure or unavailable arm throws
`NativeFailureException` / `NativeUnavailableException`; a malformed reply throws the contract exception. Writes go
through `FacadeActionsWriter` (`IActionsWriter`) and `FacadeClockWriter` (`IClockWriter`). `CountingNativeFacade` wraps
the facade so every native round trip is counted into the attached `ReadTally` (`BridgeFunctions.CountNativeRead`, under
the brain's `rimgovernor/*` tool names), which is how a scheduler step reports its reads.

`ClockRunDriver` (#3178) is the hold state machine of the clock: after each planning step it keeps the clock Running at the target
speed under the watch policy the facts imply (`Run`), takes the brain's own holds (a coupled order, a review, an autosave),
keeps the heartbeat and consumes the colony events native pushes (`ColonyEventConsumer`). The composition builds it with `ClockRunDriver.Create` over a
`ClockCapabilities` session and an `IClockDriverNative`, wraps it in `ClockRunHostDriver` and returns it as
`PlannerParts.Clock`. The composition (`startServiceClock` and
`roundsCapabilities`) is `IBrainComposition`; its family flags are all on by default and
`RIMGOVERNOR_ROUTINE_FAMILIES` narrows them.

The player providers (#3114) are the client's `IPresentationReader`, `IPresentationMediaWriter`, `INotificationReader`
and `ILifecycleWriter` (Save/Load/NewColony, with `StartingIdeoligion` as the seam for the `governor_ideoligion` choice),
plus `HostSnapshots` (the cached brain snapshot, polled every `RefreshInterval`), `HostColonyStatus` and
`RoundsDiagnostics`. `BrainHost` registers `PlayerRoutes`, `ReadRoutes`, `PresentationRoutes` and `LifecycleRoutes`
from `SessionParts.Player`. There is no Attention provider: the brain's acknowledgement had no facade method to port. The
host wraps the lifecycle in `FlushedLifecycle` (the governor state is flushed before every save) and builds the
`Autosaver` (five rotating `Autosave-N` slots; no cadence of its own), which the composition passes to the clock run
driver as `ClockRunConfig.Autosave`. The driver owns the cadence: `ClockRunConfig.AutosaveEveryTicks` game ticks after
the last autosave hold (counted from the first running step of a process or a rewound tick) it takes a `Hold(Autosave)`,
saves at the first step that finds the clock held and paused, and resumes with `Run` once that step has planned. No
save is made while Running. The default is one in-game day (`DefaultAutosaveEveryTicks`, 60,000), a conservative guess
that is unmeasured; a measured save duration and tolerable intent loss should replace it.

The catalog reads need `CatalogFactory` and the `governor_ideoligion` resolver `StartingIdeoligion`; the mod's
`BrainBootstrap` installs both (see below). Both live in the mod
(`NativeCatalogFactory`), because only the mod may read the Def databases (rule 7): the catalog is built over the game's
Def objects, the wire catalog's derived and race facts and the load's def stat table (`NativeStatTable`, built on the game
thread by the catalog read), and the catalog reply carries no def rows or game constants. Both run off the game thread. Every
`InProcessNativeFacade` operation is labelled by its `NativeOps` entry, so the label is compile-checked; `NativeOpsTests`
(brain tests) checks that every facade method is bound by exactly one op.

## Watchdog thresholds

`BrainWatchdog.Standard()` is a chosen starting point. The watchdog guards the brain's own step; it does not guard the game. A stuck brain is the clock ceiling's to handle: the heartbeat lapses and native drops the rate to Normal (see [control loop](control-loop.md#the-one-ceiling-and-the-heartbeat)).

The thresholds are unmeasured guesses:

| Threshold | Value | Why |
| --- | --- | --- |
| `StallAfter` | 90 s | A step may use its whole 1 min `StepTimeout` plus half again before it counts as a stall. |
| `AbandonAfter` | 30 s | A step that ignores its cancellation token this long after the stall is abandoned and authority suspended. |
| `FaultLimit` | 5 | Consecutive faulted or stalled steps before authority is suspended; five is a pattern, not a blip. |
| `FaultBackoff` | 5 s | Half the worker's maximum idle delay (10 s), so a fault does not spin and recovery stays prompt. |
| `PlannerMissLimit` | 3 | Consecutive missed wave cutoffs before a planner is reported stalled in `BrainHost.Status()`. |

None has been measured against a live game (#3199); see [Live clock baseline](#live-clock-baseline).

## Live clock baseline

Not measured (#3199): no live game run has been recorded, so every value below is a guess taken from the current
constants, not an observation. The recording procedure is in
[Measure brain throughput](../testing/measure-throughput.md#clock-baseline-run). When #3199 records a run, fill the
Measured column from it and name the machine and run under the tables. The same constants are listed with their
meaning in the [control loop](control-loop.md#constants-that-are-not-measured).

| Quantity | Measured | Current constant | Source row |
| --- | --- | --- | --- |
| Frame time at each speed (p50/p95/p99) | not measured | 30 ms budget (`ClockCeiling.DefaultFrameBudgetMs`) | none (native keeps no frame-interval account) |
| Effective ticks/s at Ultrafast (boosted, up to 9000 ticks/s, with the one ceiling lowering it on over-budget frames) | not measured | 0.7 down, 1.25 up per 250 ms | `clock_step` pacing fields (`pacing_reason`, `paced_tps`, `tps_ceiling`, `effective_tps`) |
| Critical planner wave wall time | not measured | none | `clock_step.critical_wave_ms` |
| Immediate review scope wall time | not measured | none | `rounds_review`, `planner_step` `dur_ms` |
| Combat hold to resume latency | not measured | 2 s budget (`ClockCombatStops.ResumeBudget`) | `clock_stop.resume_latency_ms`, `combat_summary` p50/p95 |
| Brain silence before the heartbeat lapses | not measured | 1 s beat, 5 s tolerance | `clock_step` rows with `pacing_reason` `heartbeat`; `heartbeat` failure events |
| Autosave hold duration | not measured | one in-game day cadence | `clock_run` rows with reason `autosave` and the next `resumed` |

| Recommended budget | Value | Status |
| --- | --- | --- |
| Native frame budget | 30 ms | guess (the existing constant) |
| Brain heartbeat interval and tolerance | 1 s and 5 s | guess; derive from the p99 brain silence |
| Resume-latency budget for combat holds | 2 s | guess; derive from the p95 `resume_latency_ms` |
| Combat fallback speed | Normal | guess; the safest choice, not a measured one |
| Autosave cadence | one in-game day | guess; needs a measured save duration and a tolerable intent loss |

Machine: not recorded. Run: none.

## Hosting in the mod (#3115)

Decided (user, 2026-10-13):

- **Who starts it.** `BrainBootstrap.Start` (Bridge, `Protocol/`), called once by the mod's one startup,
  `NativeStartup` ([native facade](native-facade.md)). Without `-rimgovernor-inproc`
  (`InProcLaunch.Requested(Environment.GetCommandLineArgs())`) it does nothing: no thread, database or file. With it,
  one task polls the game thread (a facade hop, so it is also "not yet" until the host has set up game-thread access)
  until `PlayDataLoader.Loaded` and the Defs exist, for up to 20 minutes; resolves the environment; takes the route
  table; and calls `BrainHost.StartAsync`. Every failure is logged and none reaches the loading host. The brain lives
  per process, not per save. `ProcessExit` stops it within 15 s (one drain attempt of 8 s instead of the host's
  3 x 10 s, which would hold the game's exit hostage).
- **Routes.** `HttpChannelBootstrap.Routes` completes with the one `RouteTable` the running channel serves (null when
  it will not run: `RIMGOVERNOR_OTLP_DIR` unset, or the start failed). The brain passes it as
  `BrainHostOptions.Routes`, so `/player/*` shares the endpoint file and request guard with `/native/*`. With no
  channel (or none within 30 s) the brain runs with `Routes = null` and logs one warning.
- **Start time (#3290).** One headless boot with all DLCs took ~2.8 s from bridge start to brain started:

  | Step | Time | Note |
  | --- | --- | --- |
  | `capabilities.initialize` (game thread) | 2.07 s | the defs check passes on its first try, but its hop is queued behind this |
  | routes | ~34 ms | the routes task is awaited directly, with no polling |
  | `brain.host_start` | ~0.64 s | includes ~160 ms of game-thread observation snapshot read |

  Overlapping the brain start with `capabilities.initialize` would save ~0.4–0.5 s, but it needs a `BrainHost` API
  change and a readiness gate, so it was rejected. The real cost belongs to #3289 and #3291.
- **Settings.** The mod has no CLI; `InProcEnvironment` (Host, game-free) resolves environment variables. Paths must be
  absolute; a bad value stops the start with a warning naming the variable.

  | Variable | Meaning | Default |
  | --- | --- | --- |
  | `RIMGOVERNOR_STATE` | state database | `<save-data>/RimGovernor/brain-state.db`; a stamped `brain-state-yyyyMMdd-HHmmss.db` when continue is off |
  | `RIMGOVERNOR_OTLP_DIR`, `RIMGOVERNOR_RUN_ID` | the Bridge's existing variables | `<save-data>/RimGovernor/otlp`, a fresh GUID |
  | `RIMGOVERNOR_OBSERVE` | launcher Observe: no auto-resume, no layout overlay | off |
  | `RIMGOVERNOR_CONTINUE_STATE` | launcher Continue: reuse the default database | on |
  | `RIMGOVERNOR_LAYOUT_OVERLAY` | launcher Layout overlay | on |
  | `RIMGOVERNOR_ENCOUNTER_WORKER`, `RIMGOVERNOR_ENCOUNTER_DOTNET` | the built prediction worker (`RimGovernor.RealCombat.Host.dll`) and the dotnet that runs it, for hunt roster estimates | unset (no estimates), `dotnet` |

  The launcher sets the last three from its saved settings. The default database is shared by every save, so an
  incompatible-schema reset affects them all. Launcher Telemetry is intentionally unmapped (not a bug, #3117): the
  host writes OTLP files and has no endpoint to send to, so the saved setting has nothing to drive.
  `-rimgovernor-test-acceleration` also sets `ClockTestAcceleration`.
  `BrainCompositionOptions.TargetSpeed` (default Ultrafast) is the speed every `Run` asks for; the harness changes
  it live between launches and speed-matrix rows with `POST /api/player/clock-speed`, with no relaunch.
- **Acceptance harness.** The acceptance harness launches every game with `-rimgovernor-inproc` and
  `RIMGOVERNOR_OBSERVE=1` (a case starts the brain through the player API; a `ServeSpec.Env` entry can override), and sets `RIMGOVERNOR_STATE=<Output>/service.sqlite` (plus a case's
  serve flags named like environment variables, `ServeSpec.Env`) in the launched game's environment and records the
  launch environment in the save-data folder (`RimGovernorLaunchedEnv.json`). The game reads its environment once, so a
  kept game launched without a case's flags is relaunched (reason `env`); the state path never forces a relaunch, and a
  kept game's readers follow the recorded path. The brain runs from the assemblies the game loaded at launch, so a kept
  game is also relaunched (reason `package`) when any file under the installed mod's `Assemblies/` or
  `BridgeTools/RimGovernor/` (or `About.xml`) changed since its launch (`Harness.RelaunchFiles`); the lab start-cache
  stamp and report `package_files` stay on the narrower `Harness.StampFiles` (About.xml, Runtime.dll, Bridge.dll).
  `HostSeams.Install()` (called from `CaseCatalog.Register`) wires the
  store-backed case readers over that file. Readers that need the game's own data (`StarterSite.Planner`,
  `LayoutSiteHost.Surveyor`, `ColonyFactsSource.Reader`, the catalog and blight readers) call the mod's read-only
  `rimgovernor/acceptance_read_*` tools (`colony_facts`, `blight`, `starter_site`, `impressiveness`, `animal_races`, `floor_terrains`, and `clock_accounting` for the speed matrix and hazard cases;
  `AcceptanceReadTools`) through the generic native route. The tools run `AcceptanceReads` in the Host project: the
  observation layer's colony read with a standalone planning window and the load's definition catalog over the published
  facade, returning JSON in `payload`; they record nothing and need no running brain. The first layout plan is derived
  as the starter site did (no ledger, no stock) and returned in the store codec, so it can differ from the plan a
  running service records.
- **Catalog.** `BrainCompositionOptions.CatalogFactory` is `NativeCatalogFactory.Create` and `StartingIdeoligion` is
  `NativeCatalogFactory.StartingIdeoligion`; both run on the brain's threads, never the game thread.
- **Clock native.** Nothing to build: `FacadeNativeClient` already implements `IClockDriverNative`.
- **Loading.** The default load context, with the brain assemblies beside the Bridge, not a collectible one. The
  package ships the Host's whole graph (Snapshot, Observation, Store, Executor, BuildingRuntime, PlayerApi,
  Simulation) and Store's native SQLite as a flat `e_sqlite3.dll` (win-x64 only) in `BridgeTools/RimGovernor`, because
  the runtime probes the requesting assembly's own directory, not `runtimes/<rid>/native`. Verified on a stock .NET 10
  runtime; not yet on the game's own CLR.

## Open decisions

What a suspended planner reports (today the whole brain is suspended, since `ClockScheduler` cannot drop one planner);
the shared-assembly binding assumption from #3080.
