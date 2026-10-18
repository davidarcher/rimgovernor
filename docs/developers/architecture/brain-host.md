# Brain host

The brain host (`brain/src/RimGovernor.Brain.Host`, `BrainHost`) runs the C# brain inside the mod's process behind the
launch flag `-rimgovernor-inproc` (`InProcLaunch.Flag`). There is no separate process, transport, lease or reconnect machinery: the brain and the game
share a process, so a link cannot drop under it.

The optional [encounter predictor](../contracts/encounter-prediction.md) is an
isolated hypothetical worker behind a typed replaceable interface. Host owns its
bounded process lifecycle (`LocalEncounterPredictor`); it has no live colony
facade or order authority. This is not another brain/controller. When
`RIMGOVERNOR_ENCOUNTER_WORKER` names the built worker, `BrainBootstrap` starts a
`HuntEstimateService` over it and the food plan's
[hunt comparison](../contracts/forecast-contracts.md#coherent-coupled-observation-frame)
prices its rosters from the service's estimates; unset, rosters stay unestimated
and no formation hunt is offered.

## Lifecycle

`BrainHost.StartAsync(options, ct)` in order: validate options; start the OTLP file writer (a failure is reported and
the rows go nowhere, it never stops the brain); register the intent kinds (`IntentActionRules.RegisterIntentKinds`); open the state database (an incompatible one is moved aside); compose the session, the player and the
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
reported in `BrainHost.Status()`. A step cancelled by its call budget (`CallTimeout`) is a retry, not a fault: it does not count toward
`FaultLimit`, and the scheduler replans with a full step after it (#3435). `StepSupervisor.CancelLimit` (5) consecutive cancellations suspend authority like a fault; a success or a fault ends the run.
reported in `BrainHost.Status()`.

## Native client and composition (#3113)

`FacadeNativeClient` (`RimGovernor.Brain.BuildingRuntime/Client/`) is the planners' native client, with no transport: one class over `INativeFacade` that the planners take directly. Each `Client.ReadX` is one `ReadXAsync` core method
(request, facade call, outcome unwrap, the reply validators). A BuildingRuntime interface survives only where a test
fake or a second implementer satisfies it (the clock, boundary, colony-status and building-preview natives, the
defense source, the writers, the session and player seams); those are bound to the core methods in
`FacadeNativeClient.Interfaces*.cs`, with explicit implementations where the interface shape differs from the core method's. The PlayerApi seams (presentation, notifications, lifecycle) sit above BuildingRuntime, so Host binds them through the thin `FacadePlayerAdapter`. A failure or unavailable arm throws
`NativeFailureException` / `NativeUnavailableException`; a malformed reply throws the contract exception. Writes go
through `FacadeActionsWriter` (`IActionsWriter`) and `FacadeClockWriter` (`IClockWriter`). `CountingNativeFacade` wraps
the facade so every native round trip is counted into the attached `ReadTally` (`ReadTallyRules.CountNativeRead`, under
the brain's `rimgovernor/*` tool names), which is how a scheduler step reports its reads.

`ClockRunDriver` (#3178) is the hold state machine of the clock: after each planning step it keeps the clock Running at the target
speed under the watch policy the facts imply (`Run`), takes the brain's own holds (a coupled order, a review, an autosave),
keeps the heartbeat and consumes the colony events native pushes (`ColonyEventConsumer`). The composition builds it with `ClockRunDriver.Create` over a
`ClockCapabilities` session and an `IClockDriverNative` and returns it as
`PlannerParts.Clock`. The composition (`startServiceClock` and
`roundsCapabilities`) is `BrainComposition`; its family flags are all on by default and
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

Measured once (one in-game day, 2026-10-18, #3199); see [Live clock baseline](#live-clock-baseline).

## Live clock baseline

First measured 2026-10-18 (#3199) on one in-game day of a fresh Crashlanded colony (3 colonists, storyteller on) under
`acceptance colony-review -days 1`, revision 21a4a5196, brain session `service.instance.id`
`e59697faa0f3488d8fa177ba88d2916b`, on DAVID-PC (AMD Ryzen 9 9950X, 94 GB, NVIDIA GeForce RTX 4080 SUPER, Windows 11 Pro).
The recording procedure is in [Measure brain throughput](../testing/measure-throughput.md#clock-baseline-run). Rows marked
"not measured" need the combat, idle-heartbeat and autosave runs that #3199 still owes. The same constants are listed with
their meaning in the [control loop](control-loop.md#constants-that-are-not-measured).

The day took 102 s of wall time from `brain_started` to tick 60000 (85 s from the first Run): 55.9 s running at Ultrafast
and 29.6 s held. Holds: 5 (`ColonistNeed` 3, `NotificationBatch` 1, `SessionChanged` 1 at shutdown), none `Unavailable`;
12 `clock_step` rows; 7 `clock_run` rows, 5 resumed and 2 refused because a hold entered after the step's read (each refusal
cost one more step before the resume). Native observed the whole day at about 1100 ticks/s with the ceiling at 2100 to
4000 ticks/s: the game's own tick rate, not the ceiling, bounded the speed.

| Quantity | Measured | Current constant | Source row |
| --- | --- | --- | --- |
| Frame time at each speed (p50/p95/p99) | Ultrafast only: 1645 paced frames over 55.9 s running (34 ms mean), 61 over the 30 ms budget (3.7 %). No per-frame distribution; the per-run split was cumulative in this run (fixed after it). | 30 ms budget (`ClockCeiling.DefaultFrameBudgetMs`) | `clock_hold` `run_frames` and `run_frames_over_budget` per run (the frame account keeps no per-frame distribution) |
| Effective ticks/s at Ultrafast (boosted, up to 9000 ticks/s, with the one ceiling lowering it on over-budget frames) | `run_tps` 1067, 1110 and 1365 on the three long runs (29876, 19941 and 12155 ticks); 1110 over the day. `ceiling_tps` at the holds 2532 to 3956; `clock_step` `paced_tps` 2128 to 2891 with `pacing_reason` `frame_budget`; `effective_tps` between steps 860 to 1196. | 0.7 down, 1.25 up per 250 ms | `clock_hold` `run_tps` per run (`run_ticks` / `run_ms`), `ceiling_tps` at the hold; `clock_step` pacing fields (`pacing_reason`, `paced_tps`, `tps_ceiling`, `effective_tps`) while running |
| Critical planner wave wall time | p50 5.8 s, p95 7.8 s, max 13.5 s (12 steps) | none | `clock_step.critical_wave_ms` |
| Immediate review scope wall time | `clock_step` `dur_ms` p50 7.0 s, p95 10.0 s, max 14.0 s; rounds `planner_step` p50 4.0 s, p95 5.8 s, max 12.0 s; the slowest other planners economy p50 2.2 s and food p50 1.3 s | none | `rounds_review`, `planner_step` `dur_ms` |
| Hazard hold to resume latency | Hunger holds 5.4, 5.7, 8.2 and 10.3 s: every hold costs a full scheduler step before the Run, and a refused Run costs a second one | none | `clock_hold` to the next `clock_resume` |
| Combat hold to resume latency | not measured (no combat in the run) | 2 s budget (`ClockCombatStops.ResumeBudget`) | `clock_stop.resume_latency_ms`, `combat_summary` p50/p95 |
| Brain silence before the heartbeat lapses | not measured (no `heartbeat` pacing row; the ceiling never fell to Normal) | 1 s beat, 5 s tolerance | `clock_step` rows with `pacing_reason` `heartbeat`; `heartbeat` failure events |
| Autosave hold duration | not measured (no autosave in one day) | one in-game day cadence | `clock_run` rows with reason `autosave` and the next `resumed` |

| Recommended budget | Value | Status |
| --- | --- | --- |
| Native frame budget | 30 ms | measured once: 3.7 % of paced frames over budget at about 1100 ticks/s; keep |
| Brain heartbeat interval and tolerance | 1 s and 5 s | guess; derive from the p99 brain silence |
| Resume-latency budget for combat holds | 2 s | guess; derive from the p95 `resume_latency_ms` |
| Combat fallback speed | Normal | guess; the safest choice, not a measured one |
| Autosave cadence | one in-game day | guess; needs a measured save duration and a tolerable intent loss |

What the day also showed (#3556, #3557, #3558): the brain reviews at wall cadence while the clock runs at game speed, so a
7 to 14 s step is 8000 to 15000 ticks of game time, the dispatch of a selection made at tick 8858 was refused at tick
25224 as "Activity deadline already passed", and the colony saw 12 reviews in a day where the retired 600-tick activity
holds gave about 100.

### Step profile (#3556 item 1)

Measured 2026-10-18 on the same colony and machine, revision 52c4c8145 plus the stage instrumentation, brain session
`2f2cd8c7c4bf4f01a5f3c0c9a1e786f4`: 11 `clock_step` rows (5 settled, 4 wake, 1 full, 1 timer), 82 s of wall from the
first Run to the end-of-day hold at tick 62668 (51 s running at 1160 to 1280 ticks/s, 31 s held in reviews; the 110 s
before the first Run are startup, the world rebuild and the first step). Each row carries `stage_ms` and `stage_alloc_kb`
(the process allocation between laps, so a stage that overlaps parallel planners counts their allocation), `selection`
(why the planners ran) and each `planner_step` row `selected`; the rounds row carries its own stages and the Worker files a
`worker_step` row per cycle that dispatched.

| Step stage | p50 | p95 | max | Allocation p50 | What it is |
| --- | --- | --- | --- | --- | --- |
| `clock_step` dur | 7.2 s | 15.6 s | 15.6 s | 2.5 GB | the whole step, 87 s over 11 steps |
| gate | 351 ms | 1004 ms | 1004 ms | 28 MB | waiting for the player gate the Worker holds while it dispatches |
| read | 33 ms | 270 ms | 270 ms | 4.6 MB | the step read with clock status, scope and validity checks |
| select | 0 ms | 1 ms | 1 ms | 8 KB | the due-queue selection |
| refresh | 17 ms | 361 ms | 361 ms | 0.3 MB | entity sections and the zone census |
| rounds | 4645 ms | 13054 ms | 13054 ms | 2.0 GB (max 5.8 GB) | the rounds review, stages below |
| extent | 0 ms | 152 ms | 152 ms | 0.4 MB | establishing the planning extent |
| critical | 1500 ms | 2530 ms | 2530 ms | 332 MB | the critical planners (anomaly, shelter, admin, medical, people, disaster, defense) |
| optional | 788 ms | 2262 ms | 2262 ms | 57 MB | the optional planners after the critical wave returned |
| record | 2 ms | 22 ms | 22 ms | 3 MB | filing the wave on the due queue |
| `clock_run` dur | 115 ms | 1433 ms | 1433 ms | | the driver's decision and the Run (1.4 s at the first Run; 2 of 9 refused on the hold serial) |

| Rounds stage | p50 | p95 | max | Allocation p50 | What it is |
| --- | --- | --- | --- | --- | --- |
| acquisition | 1584 ms | 3956 ms | 3956 ms | 859 MB | `PlanFoodAcquisitionAsync` |
| derive | 1169 ms | 2897 ms | 2897 ms | 261 MB | the reads and derivations between the layout plan and acquisition (stockpiles, planned sites, shelter reads, clearance, ledger) |
| layout | 1005 ms | 9055 ms | 9055 ms | 387 MB (max 3.7 GB) | `ReviewLayoutPlanAsync`; 9 s once, at the step that planned the layout |
| review | 328 ms | 1921 ms | 1921 ms | 336 MB | `ReviewRoundsAsync`; the policy inspection inside it is `inspect_ms` 324 ms p50, and the coupled search hit its 16384-expansion bound in every review after the first (`coupled_search_expansions`) |
| observe | 29 ms | 420 ms | 420 ms | 28 MB | `ObserveRoundsAsync` |
| journal, admit, record | 17, 1, 2 ms | 55, 46, 30 ms | | | journal loads and claims; admission; snapshot and overlays |

What the profile says:

- **Where the 7 s goes.** Rounds 4.6 s, of which 3.8 s is acquisition, derive and layout re-derived from the same facts at
  every review and 0.3 s the review with the coupled search; then 2.3 s of planner waves and 0.35 s waiting for the
  Worker's gate. Read, select, refresh and record together are under 60 ms. The coupled search, assumed to be the cost
  of the rounds planner, is not: at the 16384-expansion bound it costs 0.3 s p50 (1.9 s once).
- **Every step ran every planner.** `selection` was `full=22`, `settled=22` (a settled step marks all), `all=22` (an
  authority wake), `dirty+due=22` or `due=22` (the timer step: `ReviewEvery` is 2500 ticks and the clock moved 8000 to
  18000 ticks between steps). Of 824 Need rows in 11 steps (75 per step), 602 (73 %) ended `nothing_to_do` or
  `disabled`. The planners' own wall summed to 9 to 33 s per step behind a 2.3 s wave: the width works, the work is
  mostly wasted. A Pawns-only step was not isolated in this run because every family entry's sections include the pawn
  sections, so a Pawns wake selects all 22; the dirty-only question is therefore which planners read what, not the queue.
- **Game-thread queueing.** `hop_wait_ms` p50 per run: admin 1182 ms, military 912, food 751, economy 702, shelter 656,
  home-upkeep 523; the planners that read nothing new wait 3 to 30 ms. The facade reads ran 0.8 to 3.0 s on the game
  thread per step (`hop_run_ms`), serialized, while the planners waited in parallel.
- **Dispatch is cheap.** 136 `dispatch` rows at 50 ms p50, 99 ms p95, 261 ms max; a Worker cycle dispatched 2 to 17
  actions in 68 to 916 ms. Its cost to the step is the gate: the Worker waited 5.2 to 14.5 s for the scheduler's step, and
  the scheduler waited up to 1 s for the Worker.
- **No-op floor.** No step had nothing dirty, so the floor is composed from the measured stages: gate 0 (Worker idle) to
  1 s, read 33 ms, select 0, record 2 ms, Run 115 ms: about 150 ms per review, 1.2 s when the Worker holds the gate.
- **Is 0.65 s per review reachable?** Not with today's review. At a 600-tick cadence the 11 reviews become about 100, and
  100 × 7.2 s is 720 s against the 65 s the 2-minute budget leaves. A review that re-runs rounds costs 3 to 6 s before
  any planner runs, so the common review must skip rounds entirely (a Pawns-only review: 150 ms floor plus the few
  planners whose facts changed), and the full review must run at a coarser cadence or incrementally. With that split the
  average is a weighted one: at 1 full review (4 to 6 s) per 10 reviews the average is about 0.7 s, right at the budget;
  the planner cuts below decide whether it lands under.
- **Three biggest cuts.** (1) Rounds derive, acquisition and layout: 3.8 s and 2.9 GB per review, re-derived from facts
  that rarely change between reviews; make them conditional on their inputs or run the full review on its own cadence.
  (2) Dirty-only planner waves: stop the settled step marking all, make `ReviewEvery` longer than the review cadence, and
  narrow the family entries' sections so a Pawns wake selects the planners that read pawns, not all 22; 73 % of Need rows
  do nothing today. (3) The game-thread reads: 0.8 to 3 s of serialized facade work per step with the heavy planners each
  waiting 0.7 to 1.2 s; fold the family reads into the step read, or read them once per step and share.

#### Rounds cuts (#3556, 2026-10-18)

The three rounds stages were cut in the order the profile ranked them and each cut was measured the same way on the same
colony (one colony-review day; brain sessions `4d1d320821a7440cb5cb864ebcdd5746` after the acquisition cut and
`2265830e199e476db78b7fb325b1a18c` after the derive cuts, 12 and 15 rounds reviews). The rounds row now laps the derive
chain (`d_owed`, `d_reads`, `d_stock`, `d_planned`, `d_file`, `d_work`, `d_clear`, then `derive` for the ledger and the
colony model), the `layout_replan_tiled` row names the inputs that drifted since the last replan (`inputs_changed`) and
the `clock_step` row carries `hop_run`, the game-thread run time per hop operation and, for the construction read, per
part of it (`facade/construction_activity/<part>`, not summed into `hop_run_ms`).

| Rounds stage | Before p50 | After p50 | What changed |
| --- | --- | --- | --- |
| acquisition | 1584 ms, 859 MB | 63 ms, 28 MB | the settlement loop asked every ThingDef three uncached catalog questions per settlement, one of them a stat read that threw per definition; the supply definitions are now built once per trader kind per catalog (`TradeAcquisitionMemory.SupplyDefinitions`) and the stat read asks `ShownStatValue` |
| derive, all laps | 1169 ms | 1070 ms (`d_planned` 684, `derive` 154, `d_stock` 150, `d_file` 47, the rest under 20) | the owed diff over every built room (`PlannedOwedOf`) is taken once per review instead of four times (pricing, planned sites, and twice in the store-room report); the firebreak's tile reads go out in one frame and overlap the planned-site reads; the stockpile review overlaps the consumption window; the surface-ore reads go out in one frame |
| layout | 1005 ms | 1029 ms at the hourly check (8 of 15 reviews), 5 ms otherwise | the cost is unchanged; the key now bands the expected mood (below) |
| review | 328 ms | 354 ms | unchanged |
| rounds | 4645 ms | 3105 ms (max 10.9 s at the step that planned the layout) | |

What the second profile says:

- **`d_planned` is native work, not queueing.** `hop_run` per step: `facade/construction_activity` 637 ms p50 (1.2 s max)
  for the three reads the lap issues (the planned sites, the routes between them, the shelters' thermal and room
  predictions; 15 MB of reply), `facade/observations_read_colony_facts` 1056 ms (3 reads per step, 72 MB); every other
  operation runs under 15 ms. Overlapping the hops around them gained 0.1 s; the 0.65 s left is the native estimate
  itself (the route probes path every free colonist to every site and the site answers estimate every piece), so the next
  derive cut is on the native side or in asking it once per step rather than once per review (the third cut, below).
  The ledger (`derive`) and the stock reads (`d_stock`) are 0.15 s each.
- **The hourly replan re-tiled the same rooms.** 6 of 8 replans ended same=13 moved=0 new=0 and `inputs_changed` named
  `expected_mood` in every one of them: alone 3 times, with `survey` 3 times, with `stock` once. The mean mood drifts
  every hour and only prices the comfort tiers, so the key now carries it in bands of 0.05
  (`StationFacts.ExpectedMoodBand`); `survey` (the cells change as the colony builds) and `stock` still replan, as they
  should. The next day (session `67361dc552c14f0ca972c8027354ee82`, 16 reviews, rounds 2974 ms p50) tested the
  band: every one of its 10 hourly checks still replanned, `survey` at 7 of them and a band edge crossed
  (`expected_mood` alone) at 2, so the band skipped none. While the colony builds, the survey cells change every hour;
  the hourly replan (0.7 to 0.9 s, the chain and the tiler) stays until the key reads what the chain reads of the
  survey rather than every cell.
- **The step.** `clock_step` p50 6.0 s before the derive cuts, 5.9 s after (7.2 s in the first profile): with rounds at
  3.1 s the planner waves (critical 1.7 s, optional 0.8 s) are the larger half, cuts (2) and (3) above.

The third cut, the path searches, measured on one colony-review day each of the same colony (sessions
`47201e9f4881475c9c23ece9c1514fe3` before, 23 rounds reviews, and `6aa045a4ac324a6ea3e3d0f4bd0f0cba` after, 15):

- **Where the hop went.** The parts of `facade/construction_activity` before: 956 ms of the 993 ms hop p50 were path
  searches, one synchronous `FindPathNow` per leg, each clearing a grid-sized calc grid on the game thread (709 ms under
  the planned sites, 289 ms under the route probes); blueprint legality, the thermal read and the room predictions were
  under 3 ms together. That day's `d_planned` was 1332 ms p50 (p95 2041), double the second profile's 684 ms: the sites
  grow as the colony builds.
- **What changed.** `NativePathLegs.Batched` runs the read twice: a collecting run in which every leg not yet searched
  is noted and answered 0, one batched search of the noted legs (`PathFinder.CreateRequest` and `PushRequest` in rounds,
  two `PathFinderTick` calls per round: the first schedules the round across the game's path job workers, the second
  completes and resolves it), then the real run against the searched legs. The finder runs the same `PathFinderJob`
  per request either way, so the legs are those the synchronous search found; a leg the real run asks that the
  collecting run did not is searched on its own. The round is the map's spawned pawns plus two (at least four): the
  finder emits a round's paths together, and the map's `PawnPathPool` reports a leak past twice the spawned pawns plus
  four paths alive at once.
- **What the first live day showed (2026-10-18).** In a live colony every batched request of a construction read
  resolved with no path (440 `Resolved path returned no nodes` warnings over four reads, legs between adjacent cells
  among them) while the same legs searched synchronously were found, and the empty legs were stored as "no path", so
  no construction row formed and the builders stood. The paused lab's batched requests resolve with their paths
  (`observations/construction-route-legs`, paused and while the game ticks); the live trigger is not pinned. The
  search no longer believes a batched request that resolves with no path: that leg is searched directly before it is
  stored, and a map whose batched round missed a path the direct search found is searched directly from then on, with
  one warning in the game's log naming the first such leg. A genuinely unreachable leg costs one direct search per
  full drop of the store.
- **After.** `d_planned` 343 ms p50 (p95 420, max 1145), `facade/construction_activity` 271 ms p50 (p95 327: paths
  233, rows 30, legality, thermal and predictions under 2 each); rounds 2532 ms p50 (3315 before), `clock_step`
  5324 ms p50 (5994). The 0.23 s left is the batched searches themselves; the next cut is keeping the legs across
  reviews while the sites, roster and cells they depend on are unchanged.

The fourth cut, the legs kept across reviews (session `b89e623bab264a5eb1a97f93899ac8ea`, 19 rounds reviews, one
passing colony-review day of the same colony):

- **What changed.** `NativePathLegs` keeps every searched leg in a per-map store with the cells it crosses, so a read
  finds the legs earlier reads searched. The store drops legs the way the finder's own map data refreshes
  (`PathFinderMapData`, the map events): a region, room or fog change and a game condition drop every leg; a building,
  a path-relevant thing, a terrain or path-cost change, a reservation or a haul en route drops the legs crossing its
  cells. Door open states are not watched: a leg through a door is priced at the door's state when it was searched, as
  every leg of one read already was. A colonist's own legs are keyed by its cell, so they are searched again once it
  has moved; the stack-to-site legs and the route probes, the bulk of a review's legs, are found.
- **Reuse.** Over the day, 195 construction reads (the rounds review's three hops and the colony-facts forecast's)
  asked 114 k legs and searched 21 k, 82 % kept. A planned-sites read asks about 3.6 k legs: the day's first searched
  all 2.5 k of its legs in 124 ms, the rest 500 to 1100 in 20 to 70 ms (77 % kept).
- **After.** `d_planned` 143 ms p50 (p95 465, max 471), `facade/construction_activity` 74 ms p50 (p95 164: paths 37,
  rows 40); rounds 2253 ms p50 (2532 after the third cut, 3315 before it), `clock_step` 4212 ms p50 (5324). The row
  building (both runs of the batched read, 3.6 k legs re-priced per colonist) now outweighs the searches; the next
  cut, if one is wanted, is there or in asking the three reads once per step.

The layout cut (#3565; session `663cb825094049a88ab29cfc8fd70f6e`, 34 rounds reviews, one passing colony-review day
of the same colony):

- **What changed.** The hourly check ran the station chain twice (once for the inputs key, once in the replan) and
  tiled on the hold path. It now reads the replan's inputs purely (`StationInputsOf`: the facts and their key, no
  chain) and, when they drifted, starts the replan on a worker (`Rounder.LayoutPending`: the catalog rows onto the
  facts, `ReplanFacts`, the chain once, the tiler over its advice). The review that finds the run complete adopts it
  when the loaded world and the held plan are still the ones it started from (`layout_replan_tiled` attr `adopted`,
  the ticks since the start), else drops it (`skipped/superseded`); no second replan starts while one is pending.
- **After.** `layout` 45 ms p50 over every review (before: 1029 ms at the hourly check, 8 of 15 reviews, 5 ms
  otherwise); the hourly checks and the adoptions take 25 to 160 ms. 10 replans were adopted, 0 to 6037 ticks after
  they started (median about 2400: the next review is usually the next hour's). Rounds 1559 ms p50 (2253 after the
  legs cut), `clock_step` 3493 ms p50 (4212).
- **What is left on the hold path.** The two adoptions that changed the perimeter (the first two, which added the
  bedrooms, kitchen and storage) took 1.3 and 1.6 s in `ColonyPlanCut.LogPerimeterCut`: the perimeter cut is computed
  to be logged, and belongs on the same worker. The eight that re-tiled the same twelve rooms (`same=12`) took under
  0.1 s. Every replan still records a plan (`changed=true`): the tiled plan differs from the held one even when no
  room moved, which is the next thing to read before the ~1 GB replan allocation is profiled.

The search cut (#3563; session `9a713eed0644434a85ce035d96f7c5e9`, 22 rounds reviews, one passing colony-review day
of the same colony):

- **What changed.** The coupled search ([portfolio selection](portfolio-selection.md)) ran to its 16,384-expansion
  bound in 33 of the 34 reviews of the layout-cut day: after the greedy extension and the confirmed leader, the
  cardinality sweep spent whatever was left (11,330 expansions mean over the 17 reviews that reached it) and chose
  the selection in none. The sweep now runs only when the budget left can finish it, else the search ends at the
  confirmed leader; with the budget a ceiling the converged search rarely reaches, the default rose to 65,536 (the
  seed pass, a quarter of it, screened about 64 of up to 99 roots before, every root now).
- **After.** 0 of 22 reviews reached the bound (max 34,932 expansions); 13,893 p50, 15,740 mean (16,384 and 15,902
  before), so the raised budget spent in the extension what the sweep used to burn: the extension 15,659 expansions
  mean (10,734) and every extension reached the confirm pass (6 of 20 before; the other 14 ran out of budget in the
  extension). The 10 reviews whose baseline won ended at 238 to 6,837 expansions in 35 to 378 ms of `inspect_ms`; the
  12 the extension or the warm start won at 13,893 to 34,932 in 279 to 1263 ms. `inspect_ms` 378 ms p50, 1174 p95,
  1263 max, 10.5 s over the day (647, 1570, 1697 and 23.1 s over 34 reviews). Rounds 1179 ms p50 (1559), `clock_step`
  3213 ms p50 (3493).
- **What is left.** The warm start (`warm_start`) found no previous selection to seed from in 12 of the 19 reviews
  after the first two (`missing`; 7 `seeded`, 1 selection taken from it): the key the previous selection is filed
  under misses more often than the alternatives change, which is worth reading before the extension is tuned. The
  rounds allocation is unchanged at 898 MB p50 per review (the ~1 GB above): the search was 10 to 30 MB of it.

The continuation that the inputs rule out (#3564; session `79a421bac6af4cc18eb7ea23acd7cc21`, 23 rounds reviews, one
passing colony-review day of the same colony):

- **What was measured.** #3564 proposed re-deriving only the laps whose inputs changed. Before any lap is skipped,
  the row says which could be: each lap through `derive` leaves its reading with `RoundsLapInputs`, and the
  `rounds_review` row names, per lap after `observe`, the properties of the reading it entered on (the one the lap
  before it left) that differ from the previous review's (`laps_changed`; `laps_same` the laps that entered on an
  identical reading; `laps_compare_ms` 1.6 ms p50, 105 ms at the first comparison, which reflects the properties).
- **What the day says.** In 22 of 22 compared reviews every lap entered on a changed reading, about 45 of the 300
  compared properties, in the three reviews 61 to 69 ticks apart (t=29872, 29933, 30002) as in the ones 2 to 8
  thousand ticks apart. Part of that is the comparison's blind spot: ten of the property types (`DeepResources`,
  `Policies`, `BiotechColony`, `AnomalyColony`, `DeliveryLedger`, `FoodChannels`, `PowerPlanning`, `FoodSupply`,
  `MoodLedger`, `FoodReserveReview`) are records whose members include plain lists or dictionaries, which compare by
  reference and read as changed whenever the review rebuilds them. The rest is the colony: `Identity` (the tick),
  `WorkPawns`, `Facts.MoodPawns`, `Window`, `Threat`, `Zones`, `Rooms`, `Acquisition` and `Environment` differ in
  every review, and every lap reads some of them. No lap could have been skipped on this colony at any cadence.
- **What follows.** A per-lap skip would need each lap's inputs declared by hand, with the tick, the pawn positions
  and the continuous facts (mood, temperature) left out or banded, and the laps that cost (the review's coupled
  search over the needs forecasts, the planned sites priced per colonist, the stock) are the ones that read exactly
  those. The issue's fallback, a full rounds review on its own cadence with the common review reading the last
  committed one, is the remaining shape; it is not taken here (#3556 ruled it the fallback). Rounds this day:
  1409 ms p50 (review 529, derive 155, d_planned 119, d_stock 80). One lead outside rounds: the colony facts read
  (`hop_run` `facade/observations_read_colony_facts`) costs 614 ms p50 of game-thread time per step, 12.5 s over
  the day, more than any rounds lap.

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
  The harness's journal reader (`CaseStore`) opens that file. The speed matrix and hazard cases call the mod's
  read-only `rimgovernor/acceptance_read_*` tools (`capture_profile` and `clock_accounting`; `AcceptanceReadTools`)
  through the generic native route. `capture_profile` times the brain's decode of a captured frame with
  `AcceptanceReads.FrameDecodeProfile` in the Host project; the tools return JSON in `payload`, record nothing and
  need no running brain.
- **Catalog.** `BrainCompositionOptions.CatalogFactory` is `NativeCatalogFactory.Create` and `StartingIdeoligion` is
  `NativeCatalogFactory.StartingIdeoligion`; both run on the brain's threads, never the game thread.
- **Clock native.** Nothing to build: `FacadeNativeClient` already implements `IClockDriverNative`.
- **Loading.** The default load context, with the brain assemblies beside the Bridge, not a collectible one. The
  package ships the Host's whole graph (Observation, Store, Executor, BuildingRuntime, PlayerApi,
  Simulation) and Store's native SQLite as a flat `e_sqlite3.dll` (win-x64 only) in `BridgeTools/RimGovernor`, because
  the runtime probes the requesting assembly's own directory, not `runtimes/<rid>/native`. Verified on a stock .NET 10
  runtime; not yet on the game's own CLR.

## Open decisions

What a suspended planner reports (today the whole brain is suspended, since `ClockScheduler` cannot drop one planner);
the shared-assembly binding assumption from #3080.
