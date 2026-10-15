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
after a window. It idles unless the player is enabled with a known observation. Authority is the loaded world's:
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

`ClockWindowDriver` (#3108) is the window side of the clock: after each planning step it admits and starts the window,
settles owed epochs, re-takes the clock from the player and polls the native journal. The composition builds it with
`ClockWindowDriver.CreateAsync` over a `ClockCapabilities` session and an `IClockDriverNative`, wraps it in
`ClockWindowHostDriver` and returns it as `PlannerParts.Windows`. The composition (`startServiceClock` and
`roundsCapabilities`) is `IBrainComposition`; its family flags are all on by default and
`RIMGOVERNOR_ROUTINE_FAMILIES` narrows them.

The player providers (#3114) are the client's `IPresentationReader`, `IPresentationMediaWriter`, `INotificationReader`
and `ILifecycleWriter` (Save/Load/NewColony, with `StartingIdeoligion` as the seam for the `governor_ideoligion` choice),
plus `HostSnapshots` (the cached brain snapshot, polled every `RefreshInterval`), `HostColonyStatus` and
`RoundsDiagnostics`. `BrainHost` registers `PlayerRoutes`, `ReadRoutes`, `PresentationRoutes` and `LifecycleRoutes`
from `SessionParts.Player`. There is no Attention provider: the brain's acknowledgement had no facade method to port. The
host wraps the lifecycle in `FlushedLifecycle` (the governor state is flushed before every save) and builds the
`Autosaver` (one in-game day, five rotating `Autosave-N` slots, baseline on the first stop or a rewound tick), which
the composition passes to the clock windows as `ClockWindowConfig.Autosave`.

The catalog reads need `CatalogFactory` and the `governor_ideoligion` resolver `StartingIdeoligion`; the mod's
`BrainBootstrap` installs both (see below). Both live in the mod
(`NativeCatalogFactory`), because only the mod may read the Def databases (rule 7): the catalog is built over the game's
Def objects, the wire catalog's derived and race facts and `INativeFacade.Stats`, and the catalog reply carries no def
rows or game constants. Both run off the game thread, as the stat source blocks until the game thread drains it. Every
`InProcessNativeFacade` operation label must name a registered tool; `NativeFacadeLabelTests` (brain tests) checks it
from the sources.

## Watchdog thresholds

`BrainWatchdog.Standard()` is a chosen starting point (a stuck step otherwise holds `ClockWorker` until its step timeout):

| Threshold | Value | Why |
| --- | --- | --- |
| `StallAfter` | 90 s | A step may use its whole 1 min `StepTimeout` plus half again before it counts as a stall. |
| `AbandonAfter` | 30 s | A step that ignores its cancellation token this long after the stall is abandoned and authority suspended. |
| `FaultLimit` | 5 | Consecutive faulted or stalled steps before authority is suspended; five is a pattern, not a blip. |
| `FaultBackoff` | 5 s | Half the worker's maximum idle delay (10 s), so a fault does not spin and recovery stays prompt. |
| `PlannerMissLimit` | 3 | Consecutive missed wave cutoffs before a planner is reported stalled in `BrainHost.Status()`. |

They have not been measured against a live game; see [Live clock baseline](#live-clock-baseline).

## Live clock baseline

Not yet measured (#3169): no live game run has been recorded, so every value below is a guess taken from the
current constants, not an observation. The recording procedure is in
[Measure brain throughput](../testing/measure-throughput.md#clock-baseline-run). Fill the Measured column from one
run and name the machine and run under the tables.

| Quantity | Measured | Current constant | Source row |
| --- | --- | --- | --- |
| Frame time at each pace level (p50/p95/p99) | not measured | 30 ms budget (`SupervisedPlayPacing.DefaultFrameBudgetMs`) | native call `timing` / `native_frames` |
| Effective ticks/s from the 15x floor to the 150x ceiling | not measured | 0.7 down, 1.25 up per 250 ms | `pace`, `clock_step` pacing |
| Critical planner wave wall time | not measured | none | `clock_step.critical_wave_ms` |
| Immediate review scope wall time | not measured | none | `rounds_review`, `planner_step` `dur_ms` |
| Hazard stop to resume latency | not measured | none | `clock_step.stop_latency_ms`, `clock_stop` |
| Gap between brain activity (heartbeat) | not measured | none | gaps between consecutive `clock_step` / `planner_step` rows |

| Recommended budget | Value | Status |
| --- | --- | --- |
| Native frame budget | 30 ms | guess (the existing constant) |
| Brain heartbeat interval and tolerance | undecided | guess; derive from the p99 heartbeat gap |
| Resume-latency budget for combat holds | undecided | guess; derive from the p95 `stop_latency_ms` |

Machine: not recorded. Run: none.

## Hosting in the mod (#3115)

Decided (user, 2026-10-13):

- **Who starts it.** `BrainBootstrap` (Bridge, `Protocol/`) is a `[ModuleInitializer]` like `NativeFacadeBootstrap` and
  `HttpChannelBootstrap`: the Bridge has no game-side startup hook. Without `-rimgovernor-inproc`
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
- **Settings.** The mod has no CLI; `InProcEnvironment` (Host, game-free) resolves environment variables. Paths must be
  absolute; a bad value stops the start with a warning naming the variable.

  | Variable | Meaning | Default |
  | --- | --- | --- |
  | `RIMGOVERNOR_STATE` | state database | `<save-data>/RimGovernor/brain-state.db`; a stamped `brain-state-yyyyMMdd-HHmmss.db` when continue is off |
  | `RIMGOVERNOR_PROFILE` | shared game profile directory | the game's save-data folder |
  | `RIMGOVERNOR_OTLP_DIR`, `RIMGOVERNOR_RUN_ID` | the Bridge's existing variables | `<save-data>/RimGovernor/otlp`, a fresh GUID |
  | `RIMGOVERNOR_OBSERVE` | launcher Observe: no auto-resume, no layout overlay | off |
  | `RIMGOVERNOR_CONTINUE_STATE` | launcher Continue: reuse the default database | on |
  | `RIMGOVERNOR_LAYOUT_OVERLAY` | launcher Layout overlay | on |

  The launcher sets the last three from its saved settings. The default database is shared by every save, so an
  incompatible-schema reset affects them all. Launcher Telemetry is intentionally unmapped (not a bug, #3117): the
  host writes OTLP files and has no endpoint to send to, so the saved setting has nothing to drive, and
  `RIMGOVERNOR_PROFILE` stays unset by the harness. `-rimgovernor-test-acceleration` also sets `ClockTestAcceleration`.
- **Acceptance harness.** The acceptance harness launches every game with `-rimgovernor-inproc` and
  `RIMGOVERNOR_OBSERVE=1` (a case starts the brain through the player API; a `ServeSpec.Env` entry can override), and sets `RIMGOVERNOR_STATE=<Output>/service.sqlite` (plus a case's
  serve flags named like environment variables, `ServeSpec.Env`) in the launched game's environment and records the
  launch environment in the save-data folder (`RimGovernorLaunchedEnv.json`). The game reads its environment once, so a
  kept game launched without a case's flags is relaunched (reason `env`); the state path never forces a relaunch, and a
  kept game's readers follow the recorded path. `HostSeams.Install()` (called from `CaseCatalog.Register`) wires the
  store-backed case readers over that file. Readers that need the game's own data (`StarterSite.Planner`,
  `LayoutSiteHost.Surveyor`, `ColonyFactsSource.Reader`, the catalog and blight readers) call the mod's read-only
  `rimgovernor/acceptance_read_*` tools (`colony_facts`, `blight`, `starter_site`, `impressiveness`, `animal_races`;
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
