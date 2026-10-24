# Brain host

The brain host (`brain/src/RimGovernor.Brain.Host`, `BrainHost`) runs the C# brain inside the mod's process behind the
launch flag `-rimgovernor-inproc` (`InProcLaunch.Flag`). There is no separate process, transport, lease or reconnect machinery: the brain and the game
share a process, so a link cannot drop under it.

The optional [encounter predictor](../contracts/encounter-prediction.md) is an
isolated hypothetical worker behind a typed replaceable interface. Host owns its
bounded process lifecycle (`LocalEncounterPredictor`); it has no live colony
facade or order authority. This is not another brain/controller. The served brain
does not run it: the food plan's
[hunt comparison](../contracts/forecast-contracts.md#coherent-coupled-observation-frame)
estimates its rosters analytically (`HuntEstimator`, #3533), and the predictor is
the offline check the hunting sweep runs.

## Lifecycle

`BrainHost.StartAsync(options, ct)` in order: validate options; start the OTLP file writer (a failure is reported and
the rows go nowhere, it never stops the brain); register the intent kinds (`IntentActionRules.RegisterIntentKinds`); open the state database (an incompatible one is moved aside); compose the session, the player and the
planners; create the Worker; register the player routes; start the save-signal loop, the auto-resumer and the
scheduler thread. A failure unwinds what was built and throws. `StopAsync` is idempotent and ends the loops, drains
the Worker (closing player and session, bounded retries), closes the database and flushes telemetry; errors are
collected and thrown together after everything else ran.

## Scheduler thread and authority

One dedicated thread (`rimgovernor-brain-scheduler`) steps the `ClockScheduler` on the clock's cadence (#3556): the
first step is Full, and every later step starts from a hold. Each `Run` names the review tick native holds at
(`ClockRunConfig.ReviewEveryTicks`, 600); a hazard, a letter, a game pause or a brain hold is an early review; an
authority change or a loss on the event queue wakes a step while the clock runs. A step that did not complete, or
left the clock held, retries after `FaultBackoff` or at once. Invalidations only mark planners for the next review.
There is no wall-time step interval. It idles unless the player is enabled with a known observation. Authority is the loaded world's:
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
| `FaultBackoff` | 5 s | The wait before a step that faulted, stalled, deferred or was cancelled retries, and the idle loop's poll; a hold wakes the retry sooner. Half the Worker's maximum idle delay (10 s), so a fault does not spin and recovery stays prompt. |
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

Re-measured 2026-10-18 (#3556 item 4) after the rounds cuts below, on the same colony and machine, revision 03433bfcd, brain
session `4a7c9e93e3ab45adb3a48cdf84781d5f`: 100 s of wall time from `brain_started` to the end-of-day autosave hold at tick
62127 (81 s from the first Run): 68.9 s running at Ultrafast, the rest held in reviews. Holds: 5 (`ColonistNeed` 3,
`NotificationBatch` 1, `Autosave` 1 at the day's end); 24 `clock_step` rows (the last the failed step at the stop); 8
`clock_run` rows, 5 resumed, 2 refused because a hold entered after the step's read, 1 the autosave. The third column of
the table is that day; the fourth the constants as they stand.

| Quantity | Measured (#3199, 21a4a5196) | Re-measured (#3556 item 4, 03433bfcd) | Current constant | Source row |
| --- | --- | --- | --- | --- |
| Frame time at each speed (p50/p95/p99) | Ultrafast only: 1645 paced frames over 55.9 s running (34 ms mean), 61 over the 30 ms budget (3.7 %). No per-frame distribution; the per-run split was cumulative in this run (fixed after it). | Ultrafast only: 2085 paced frames over 68.9 s running (33 ms mean), 75 over the 30 ms budget (3.6 %): 54 of 1370, 14 of 462 and 7 of 253 on the three long runs | 30 ms budget (`ClockCeiling.DefaultFrameBudgetMs`) | `clock_hold` `run_frames` and `run_frames_over_budget` per run (the frame account keeps no per-frame distribution) |
| Effective ticks/s at Ultrafast (then boosted, up to 9000 ticks/s, with the one ceiling lowering it on over-budget frames; the boost is Hyperfast's since #3604, capped at 1800) | `run_tps` 1067, 1110 and 1365 on the three long runs (29876, 19941 and 12155 ticks); 1110 over the day. `ceiling_tps` at the holds 2532 to 3956; `clock_step` `paced_tps` 2128 to 2891 with `pacing_reason` `frame_budget`; `effective_tps` between steps 860 to 1196. | `run_tps` 693, 1216 and 1317 on the three long runs (29861, 20107 and 12033 ticks; the first ran from tick 0 under the first steps' reviews); 902 over the day's 68.9 s of running. `ceiling_tps` at the holds 1908 to 2613; `clock_step` `paced_tps` 372 to 4658 with `pacing_reason` `frame_budget`; `effective_tps` between steps 29 to 1352 | 0.7 down, 1.25 up per 250 ms | `clock_hold` `run_tps` per run (`run_ticks` / `run_ms`), `ceiling_tps` at the hold; `clock_step` pacing fields (`pacing_reason`, `paced_tps`, `tps_ceiling`, `effective_tps`) while running |
| Critical planner wave wall time | p50 5.8 s, p95 7.8 s, max 13.5 s (12 steps) | p50 1.8 s, p95 4.0 s, max 5.7 s (23 steps) | none | `clock_step.critical_wave_ms` |
| Immediate review scope wall time | `clock_step` `dur_ms` p50 7.0 s, p95 10.0 s, max 14.0 s; rounds `planner_step` p50 4.0 s, p95 5.8 s, max 12.0 s; the slowest other planners economy p50 2.2 s and food p50 1.3 s | `clock_step` `dur_ms` p50 3.3 s, p95 5.1 s, max 6.2 s (24 steps); rounds `planner_step` p50 1.3 s, p95 3.0 s, max 4.6 s; the slowest other planners economy p50 1.9 s and food p50 0.7 s | none | `rounds_review`, `planner_step` `dur_ms` |
| Hazard hold to resume latency | Hunger holds 5.4, 5.7, 8.2 and 10.3 s: every hold costs a full scheduler step before the Run, and a refused Run costs a second one | Hunger holds 4.3, 2.7 and 2.1 s (the first refused a Run and cost a second step); the notification-batch hold 3.5 s (also refused once) | none | `clock_hold` to the next `clock_resume` |
| Combat hold to resume latency | not measured (no combat in the run) | not measured (no combat in the run) | 2 s budget (`ClockCombatStops.ResumeBudget`) | `clock_stop.resume_latency_ms`, `combat_summary` p50/p95 |
| Brain silence before the heartbeat lapses | not measured (no `heartbeat` pacing row; the ceiling never fell to Normal) | not measured (the one `heartbeat` row is the shutdown's; the ceiling never fell to Normal) | 1 s beat, 5 s tolerance | `clock_step` rows with `pacing_reason` `heartbeat`; `heartbeat` failure events |
| Autosave hold duration | not measured (no autosave in one day) | not measured (the autosave hold at tick 62127 was the day's end; the harness stopped the game) | one in-game day cadence | `clock_run` rows with reason `autosave` and the next `resumed` |

| Recommended budget | Value | Status |
| --- | --- | --- |
| Native frame budget | 30 ms | measured twice: 3.7 % and 3.6 % of paced frames over budget at 900 to 1300 ticks/s; keep |
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
the `clock_step` row carries `hop_run`, the game-thread run time per hop operation and, for the construction and the colony-facts
reads, per part of it (`facade/construction_activity/<part>`, `facade/observations_read_colony_facts/cf.<section>`, not
summed into `hop_run_ms`).

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
  (a band since deleted, below); `survey` (the cells change as the colony builds) and `stock` still replan, as they
  should. The next day (session `67361dc552c14f0ca972c8027354ee82`, 16 reviews, rounds 2974 ms p50) tested the
  band: every one of its 10 hourly checks still replanned, `survey` at 7 of them and a band edge crossed
  (`expected_mood` alone) at 2, so the band skipped none. While the colony builds, the survey cells change every hour;
  the hourly replan (0.7 to 0.9 s, the chain and the tiler) stayed until the key read what the chain reads of the
  survey rather than every cell. #3739 did that: the survey compares by terrain only, the mood band and the stock left
  the key (the band is deleted), and a daily floor (`LayoutPlanning.ReplanDaily`) carries the facts that left it.
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
  the ticks since the start), else drops it (`skipped/superseded`), and drops it too when an open journal plan has
  since taken up a room the run did not hold (`skipped/room_held`, #3537: a shell admitted by the review that started
  the run would otherwise be sited again elsewhere); no second replan starts while one is pending.
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
  seed pass, a quarter of it, screened about 64 of up to 99 roots before, every root now). Since #3722 the allowance
  counts outcome evaluations (8,192 by default) and the 65,536 is the traversal bound on expansions
  ([portfolio selection](portfolio-selection.md)); the counts below are expansions.
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
  committed one, is the remaining shape; it is not taken here (#3556 ruled it the fallback) and was taken once the
  600-tick cadence had landed ([rounds cadence](#rounds-cadence-3556)). Rounds this day:
  1409 ms p50 (review 529, derive 155, d_planned 119, d_stock 80). One lead outside rounds: the colony facts read
  (`hop_run` `facade/observations_read_colony_facts`) costs 614 ms p50 of game-thread time per step, 12.5 s over
  the day, more than any rounds lap.

The derive and stock cuts (#3566; two colony-review days of the same colony, cuts `ca2768dee` and `03433bfcd`).

The first cut (session `bb3d35cf81fb4e9887a452cc22484139`, 26 rounds reviews; the game exited at tick 59,085 of 60,000
after a run of 7 s game-thread stalls, so the harness marked the day failed with its measurement complete):

- **What changed.** The `d_stock` lap read the journal's zone claims and shelf plans twice (the zone census, whose
  result the review then dropped, and the stockpile request); they are read once and shared, and the census lands,
  so `Facts.StockpileZones` is known once the claims are. The `derive` lap rewrote the `ledger_rows` table after every
  review for a launcher tab removed long ago; the table and the dashboard view it flattened are deleted through every
  layer (the station planner keeps the status and the wanted orders). The rounds row gained `fine_ms` /
  `fine_alloc_kb`, `d_stock` and `derive` split further.
- **After.** Rounds 1155 ms p50 (1409 the day before), `clock_step` 2828 ms p50 (3213). `derive` 158 ms p50
  (`derive/ledger` 129, p95 444, max 505, 4.3 s over the day; `derive/incumbents` 24; `derive/model` 5; `derive/bills`
  0), `d_stock` 87 (`d_stock/stockpile` 83, p95 309; `d_stock/zones` 3.6; meals and ore under 1), `review` 146 (p95
  1096), `d_planned` 136 (p95 742), `acquisition` 68, `d_file` 55, `layout` 33 (max 2148, one replan adopted). The
  rounds allocation fell from 898 MB p50 per review to 512 MB: the rows (one JSON document per order, rewritten every
  review) and the view were most of the difference; `derive/ledger` still allocated 32 MB p50 (272 MB p95) and the
  stockpile review 57 MB (206 MB p95).
- **What the hops say.** `hop_run` per operation over the day: the colony facts read 11.4 s (26 reads, 1.5 GB), the
  construction read 1.8 s, the clearance targets 0.6 s; every operation the ledger and the stockpile review issue (the
  trade requests, the world progression, the settlements, the supply stock, the resource sources, the storage
  occupancy) runs under 2 ms a hop. Their cost is the queue: a hop in the review chain waits its turn for the game
  thread, about 15 to 20 ms a hop in sequence (the ledger's 300 ms spread between a review whose export declarer read
  nothing and one whose silver gap was open, over its eighteen reads). The next cut is in the number of frames the
  chain waits for, not in the game-thread work.

The second cut (session `4a7c9e93e3ab45adb3a48cdf84781d5f`, 22 rounds reviews, one passing colony-review day):

- **What changed.** The ledger's twelve declarers ran one after another, each waiting its own frames; they now start
  together from the same reading (each writes only its own memory) and are awaited in composition order, so the first
  to fail in that order still fails the review. The trade-export declarer starts its supply, source and packed-art
  reads together and its trade-acquisition read beside the settlements read, with the home tile cached per snapshot.
  Finer laps: `d_stock/stockpile/census|build|shelves|occupancy|plan`, `d_planned/sites|shelter|firebreak`,
  `derive/ledger/benches|declare|<declarer>|reconcile`.
- **After.** `derive` 87 ms p50 (158): `derive/ledger` 53 (129), p95 119 (444), max 132 (505), 1.3 s over the day
  (4.3). `derive/ledger/armory` 48 ms p50 (p95 111, 1.2 s over the day) is the whole `declare` lap of 53: the other
  declarers finish under it (`tradeexport` 18 p50, `gear` 1.8; the rest, the bench read and the reconcile under 1 ms).
  The ledger allocates 14 MB p50 (32), 43 MB p95 (272). `derive/incumbents` 27 (p95 133), `derive/model` 5. `d_stock`
  83 (87) did not move: `d_stock/stockpile` 78 p50, of which `plan` 43 (47 MB p50), `occupancy` 12, `census` 11 p50
  but 271 p95 and 293 max (1.0 s over the day: the bench mirror refreshes with a fresh bills read, then one recipe
  hop per bench), `build` 5, `shelves` 2. `d_planned` 156 p50: `sites` 94 (p95 446; 43 MB p50, 288 MB p95),
  `shelter` 19, `firebreak` 0. Rounds 1332 ms p50 (1155) and `clock_step` 3313 (2828): the difference is `review` at
  351 p50 (146; p95 1337, 11.1 s over the day) and `layout` at p95 811 with 7 replans adopted, the day's colony rather
  than the cut; the rounds allocation 908 MB p50 (512) is the sites and the replans. The hops read as on the first
  day: colony facts 12.0 s over 22 reads (1.45 GB), construction 1.9 s, every ledger and stockpile operation under
  2 ms a hop.
- **What is left.** The issue's mark (`derive` and `d_stock` together under 0.1 s p50) is not reached: 87 + 83 ms.
  The armory declarer issues five hops in sequence (the frame things, the combat pawns, the map bounds, the equip
  weapons, then the mortar shell targets, which `d_reads` already read for `ShellsShort`): overlapping them, or
  sharing the shell targets, is most of the ledger's remaining 48 ms. The stockpile plan (43 ms, 47 MB per review)
  and the census refresh spikes are the stock lap's remainder; the planned sites (94 ms, 288 MB p95) are
  `d_planned`'s.

The third cut, the armory declarer (session `abf485a16823439d917e6be65f1991ee`, 31 rounds reviews, one passing
colony-review day of the same colony):

- **What changed.** The declarer's weapon demand starts the frame-things, combat-pawns and map-bounds reads together
  and issues the equip-weapons read as soon as the bounds answer, while the pawn facts are built; the replies are read
  in the order the sequential reads had, so the first failure in that order still abstains the same way
  (`SharedReads.Attempt` / `Reply`, the trade-export declarer's capture helper made common). The mortar shell targets
  the `d_reads` lap reads for `ShellsShort` ride on the reading as `Facts.ShellTargets`, so the declarer reads them
  from the projection instead of loading the defense layout and the catalog again.
- **After.** `derive/ledger/armory` 24.8 ms p50 (48.3), p95 70 (111), max 72 (125), 0.87 s over the day (1.2 s over
  22 reviews). `derive/ledger` 28 p50 (53), p95 79 (119), 1.2 s over 31 reviews (1.3 s over 22); `derive/ledger/declare`
  28 p50, and the lap is no longer the armory's alone: `tradeexport` 18 p50 (p95 63, max 194, the lap's max of 205)
  runs the length of it on the reviews whose silver gap is open; `gear` 3, the rest under 1. `derive` 66 ms p50 (87),
  p95 147. Rounds 1291 ms p50 (1332), p95 2970; `d_stock` 101 (83) and `d_planned` 159 (156) are the day's colony.
- **What is left.** The issue's mark is still not reached: `derive` 66 + `d_stock` 101 ms. The armory's 25 ms is two
  frame waits (the three reads together, then the weapons read the bounds gate) plus the pawn facts; the map bounds
  do not change within a load, so reading them once per load would collapse the two waits into one. The stock lap is
  now the larger half of the mark.

The fourth cut, the stock lap and the armory's bounds (session `1e95d65fbaec4f0f992bc5307d59b742`, 35 rounds reviews,
one passing colony-review day of the same colony):

- **What changed.** The stockpile plan (`d_stock/stockpile/plan`, pure CPU) indexed the planning cells by cell once
  per stage and once per store: `NewStockpileOpen`, the storage options' census and a fresh open per store for zone
  growth, the shelf candidates' census and one more per zone in `ShelfSites`, and the runtime's `CensusByCell` twice;
  on a 250 x 250 map that is 62,500 inserts each. The plan now builds the index once (the open ground's `Cells`) and
  every stage reads it; zone growth is priced over the same open with no ground taken. The rectangle search for an
  unserved store sorted every open cell of the map per call and ran twice per site (`Rectangles`, then `Cells`); it
  tries only the site's own cells (the `Only` set, whose outside was never open) and the cover reads the rectangles
  already found. A synthetic 250 x 250 request with six zones and two unserved stores went from 110 ms to 11 ms warm
  (Debug). In the runtime the occupancy read's groups (the owned zones and their built shelves) are known before the
  bench census, so the hop is issued first and shares frames with it, and the bench census refresh issues its one
  recipe hop per bench together instead of in sequence (replies read in bench order; the first failure throws). The
  armory declarer's weapon demand reads the map bounds from the reading (`Facts.MapBounds`) instead of its own hop,
  so the loose-weapons read joins the frame-things and combat-pawns reads in one frame.
- **After.** `d_stock` 77 ms p50 (101), p95 123 (245), 3.0 s over the day (3.1 s over 31 reviews);
  `d_stock/stockpile/plan` 45 p50 (65), p95 100 (95); `occupancy` 0 (10); `census` 14 p50 (14), p95 57 (158), one
  278 ms refresh. `derive/ledger/armory` 17 p50 (25), p95 42 (70); `derive/ledger` 21 (28), p95 51 (79);
  `tradeexport` 17 p50, max 70 (194). `derive` 60 p50 (66), p95 221 (147) on `derive/incumbents` p95 136 reviews.
  Rounds 1275 ms p50 (1291), p95 3047 (2970).
- **What is left.** The mark (`derive` + `d_stock` under 100 ms p50) is at 137 ms. The plan's 45 ms is now the one
  index build plus the departments' edits over the real colony's zones; the census refresh still costs a frame for
  the bills and one for the recipes when the bench table is stale. The trade export's gap-open reviews are two frame
  waves (trader kinds, then supply and sources) and the ingredient list depends on the first, so nothing further
  overlaps there.

The colony-facts read cut (#3556; one colony-review day each of the same colony, every day passing: sessions
`1d21f33a62744b889b1b2f6ef8c8bcff` before, 39 reads; `79279ffd70fa4ba9be53c5a89ed1df1f` after the routes cut, 33;
`1085b0366f4d4c03929e75d78b2171f3` after the food cut, 29):

- **Where the hop went.** The read's sections ride on `hop_run` as `facade/observations_read_colony_facts/cf.<section>`
  (every `ObservationWork.Detail` span inside a hop is a part of that hop; the coupled forecast's sub-reads are
  `cf.fc.<name>`, with `cf.fc.<name>.paths` when the sub-read searched paths). Before: 763 ms p50 (p95 1270, max 1458)
  and 74 MB per read, the coupled forecast three quarters of it: `cf.fc.routes` 343 ms (every free colonist's route
  between every pair of the forecast's locations, one store lookup or synchronous search per colonist per pair),
  `cf.fc.food` 116 (synchronous colonist-to-plant and plant-to-store legs), `cf.fc.needs` 50, `cf.fc.hunt` 32,
  `cf.eventLoot` 28, `cf.acquisition` 25, `cf.deep` 17, `cf.upkeep` 16, `cf.fc.construction` 9; the other twenty
  sections under 8 ms each.
- **What changed.** A route between two cells is one leg: found once (`NativePathLegs.Find`) and priced per colonist
  from its cardinal and diagonal steps at that colonist's move ticks (`Found.For`, the per-step term `PathFinder` adds),
  not a lookup per colonist; and the routes are built batched (a collecting run, one batched search, a pricing run).
  The food read runs batched too (`NativePathLegs.Batched` is re-entrant: a batched read inside a collecting run
  collects for the outer one). The rows are unchanged: the same routes in the same order with the same dedup and
  costs. The needs forecast was batched and reverted: its cost is the per-target checks, not the paths (83 ms batched
  against 36 to 50 unbatched).
- **After.** Hop 314 ms p50 (p95 436, max 520) and 76 MB per read: `cf.fc.routes` 69 ms (its `.paths` 68: the loop
  itself is under 2 ms, the rest is the batched search of the legs from where the colonists stand, searched anew once
  a colonist moves while the pair legs are kept), `cf.fc.food` 31 (paths 24), `cf.fc.needs` 36, `cf.fc.hunt` 27,
  `cf.eventLoot` 22, `cf.deep` 14, `cf.upkeep` 14, `cf.acquisition` 14, `cf.fc.construction` 7. `hop_run_ms` 434 p50
  (722 before), `clock_step` 2736 (3242); the read cost 8.1 s of game thread over the day's 29 reads.
- **What is left.** The routes' from-start searches, the hunt, event-loot, deep and upkeep sections at 14 to 27 ms
  each (per-item hauling-safety routes, per-plant eligibility, the deep-resource flood fill over every cell), and
  the 76 MB of reply. The first day after the food cut (session `c73064fb06e74c0c9bdd2c7ac6008a6a`) ended at tick
  58,582 with the game thread never returning from the routes block (the watchdog's breadcrumb stayed at `snapshot
  capture after cf.fc.locations` for 275 s; a test-acceleration launch writes no stall dump); the rerun passed with no
  stall, and the leg searches now mark the breadcrumb per batched round, per direct search and per synchronous search,
  so a repeat names its phase.

The route matrix cut (#3683, #3685): a colony review run held the game thread about 9 s every 25 s in the routes'
batched search (`legs batched search: round from leg 21840 of 44659`), one A* search per ordered pair of about 210
locations whenever a region change dropped the store.

- **What changed.** `NativePathLegs.Floods` prices the matrix by one Dijkstra flood per origin (each location and
  each colonist's start, about 216 instead of about 45 k searches): the game's `PathFinderJob` with heuristic 0 and a
  destination off the map, all floods scheduled together on the job workers inside the capture, each worker's chain
  reusing one calc grid and frontier, and each wanted leg read off the flood's calc grid on the worker. The rows are
  built from the store as before; the other sub-reads keep the batched search. The floods mark the breadcrumb at
  scheduling and completion, and `cf.fc.routes.floods` carries their time and count.
- **Lab.** `lab/route-floods` (100x100 lab, three wall lines): 200 floods each to the other 199 cells in 167 ms of game
  thread; on 250 sampled pairs the same reachability as A*, never dearer, at most 10 ticks under (mean 0.2), equal
  on the 57 pairs with no diagonal step.

### Clock cadence (#3556 item 3)

Measured 2026-10-18 on the same colony and machine, revision b9ce35c15 (the 600-tick review hold, the wall cadence
gone), brain session `32c2e3bbd6104b4c8d62d5797eb07a7a`, one passing colony-review day of 60240 ticks: 268 s of wall
from the first step to the last, against 82 s for 11 steps on the wall-cadence day. The budget the item serves is under
2 wall minutes of brain time per in-game day; the day is not under it.

| What | Value |
| --- | --- |
| `clock_step` rows | 105: 97 `review`, 5 `wake` (3 `ColonistNeed`, 1 `NotificationBatch`, 1 `SessionChanged`), 3 `full`; every one `planned` |
| `clock_run` rows | 105: 104 `run`/`resumed`, every one with `hold_at_tick` 600 past its tick; 1 refused on the hold serial at the first review (the step read serial 0, the review hold made it 1; the retry ran) |
| Native holds | 99 `Review`, 3 `ColonistNeed`, 1 `NotificationBatch`, 1 `SessionChanged` |
| Review step wall | p50 94 ms, p95 3134 ms, max 3737 ms, 112.7 s over 97 |
| Reviews that ran no planner | 50, p50 84 ms (gate 51 + read 33), max 135 ms: the no-op floor |
| Reviews that ran planners | 47 at about 2.4 s: rounds 1184 ms p50 (5592 max, 75.9 s over 55 steps), critical wave 356 ms, optional wave 679 ms |
| `stop_latency_ms` (hold stamp to step start) | p50 397 ms, p95 572 ms, max 3021 ms, 44.6 s over 103 |
| `clock_run` wall | p50 112 ms, p95 403 ms, 17.0 s over 105 |
| Brain-held wall | steps 139.9 s + Runs 17.0 s + hold-to-step 44.6 s = 201 s of the 268 s; about 67 s running, about 900 ticks/s |
| `selection` words | `dirty` 567, `due` 284, `dirty+due` 5, `full` 66, `all` 22 |

What the day says:

- **The cadence holds.** About 100 reviews a day as designed, every Run naming the next review 600 ticks out, five
  hazard holds as early reviews, no brain hold all day (the autosave fell due inside a review hold and saved there).
  The 12 reviews of the wall-cadence day are 97; a selection is dispatched within 600 ticks of its read.
- **Half the reviews are no-ops at the floor.** 50 of 97 found nothing dirty or due and cost 84 ms: the gate and the
  step read. The other 47 ran rounds (1.2 s) and the planner waves (1.0 s): the family entries were dirty in 32 reviews
  (pawn-section invalidations select every family entry, as the step profile found) and due in 12 (`ReviewEvery` 2500
  ticks is every fourth review).
- **Where the 201 s goes.** Rounds 76 s (38 %), the planner waves 50 s, hold-to-step 45 s, Runs 17 s, the floor 4 s.
  Per review that is 1.75 s against the 0.65 s the budget allows: 1.16 s of step, 0.16 s of Run, 0.43 s from the hold
  to the step's start.
- **Hold-to-step is a new cost.** 400 ms from native's hold stamp to the step's first lap, 45 s a day, and unexplained:
  the hold event crosses the bounded queue, the consumer's context read and the wake. On the wall-cadence day the
  settled step hid it. The steps' own game-thread hops wait 231 ms p50 (`hop_wait_ms`), so the game thread is busy
  around a hold.
- **The tick rate while running fell.** About 900 ticks/s against 1160 to 1280 on the wall-cadence day; the ceiling at
  the holds ranged 317 to 3563 ticks/s. A hundred holds a day is a hundred ramps.
- **Next cuts, in order.** (1) The full rounds review on its own cadence (deferred until this item landed): rounds runs
  at every review that selects a planner, 76 s a day, re-derived from facts that rarely change in 600 ticks. (2) The
  hold-to-step latency, 45 s. (3) The family entries' sections, so a pawn invalidation selects the planners that read
  pawns and not all 22: the waves are 50 s. With rounds and the latency cut the day is about 80 s, under the budget.

### Rounds cadence (#3556)

Measured 2026-10-18 on the same colony and machine, revision 2ea666926 (the rounds review on its own cadence,
`ClockSchedulerConfig.RoundsEveryTicks` 2500), brain session `d3ad27f717294951bfac61f9ce8b9c1f`, one passing
colony-review day of 60530 ticks: 160.6 s of wall from the first step to the last, against 268 s on the 600-tick day
above. The brain-held wall is 112 s: the day is under the 2-minute budget.

| What | Value |
| --- | --- |
| `clock_step` rows | 105: 98 `review`, 5 `wake` (3 `ColonistNeed`, 1 `NotificationBatch`, 1 `SessionChanged`), 2 `full`; every one `planned` |
| `clock_run` rows | 105: 104 `run`/`resumed` with `hold_at_tick` 600 past its tick; the same one refusal on the hold serial at the first review |
| Steps that ran no planner | 59, p50 84 ms |
| Steps that ran the rounder (`rounds` `due` 18, `wake` 4, `all` 3) | 25, p50 2114 ms: rounds 1126 ms p50 (3806 max, 34.4 s), then the waves |
| Steps on the last committed review (`rounds` `last`) | 21, p50 1078 ms, max 1589 ms, 20.7 s: critical wave 296 ms, optional wave 607 ms |
| Review step wall | p50 86 ms, p95 2199 ms, max 3517 ms, 58.3 s over 98 |
| `stop_latency_ms` (hold stamp to step start) | p50 141 ms, p95 220 ms, max 2534 ms, 17.6 s over 103 (was 397 ms p50, 44.6 s) |
| `clock_run` wall | p50 67 ms, p95 356 ms, 15.4 s over 105 |
| Brain-held wall | steps 79.4 s + Runs 15.4 s + hold-to-step 17.6 s = 112 s of the 160.6 s; about 48 s running, about 1260 ticks/s |
| `selection` words | `dirty` 302, `due` 285, `dirty+due` 32, `full` 44, `all` 22 |

What the day says:

- **The rounder ran 25 times, not 55.** Every fifth review is a `due` one: the first review tick at or past 2500 ticks
  from the last rounds review is 3000 ticks on, so the effective cadence is 3000 ticks, about 20 a day, plus the early
  reviews (the four hazard holds) and the two full steps. Rounds itself is unchanged at 1.1 s p50 (34 s a day, was
  76 s). The 21 steps between due ticks that had dirty or due planners ran them against the last review in 1.1 s.
- **Where the 112 s goes.** The planner waves 46 s (critical 12.6 s, optional 21.3 s, refresh and record 1 s, in 46
  steps), rounds 34 s, hold-to-step 18 s, Runs 15 s, the floor 5 s. The waves are now the largest cost: the family
  entries are dirty in most reviews (`dirty` 302 selections over 46 steps) because the pawn sections invalidate at every
  review tick.
- **Hold-to-step fell with the work.** 141 ms p50 from native's hold stamp to the step's first lap, 18 s a day (was
  397 ms, 45 s), and the steps' own game-thread hops wait 33 ms p50 (`hop_wait_ms`, was 231 ms). The latency moved
  with the rounds work and not with the hold path, which did not change; the remaining 141 ms is not explained.
- **The tick rate while running recovered.** About 1260 ticks/s against about 900 on the 600-tick day (1160 to 1280 on
  the wall-cadence day); the ceiling at the holds ranged 273 to 5695 ticks/s.
- **What a need waits.** A need that first appears between two rounds reviews is ranked at the next `due` review, up to
  3000 ticks (a little over an in-game hour) later, or at the next hazard hold, whichever comes first; the planners that
  already hold a ranking act on it every 600 ticks. No case in the gate depends on a rounds review per step.
- **Next cuts, in order.** (1) The family entries' sections (#3556 item 3's third cut): a pawn invalidation selects
  every family entry, 46 s of waves a day; selecting the entries that read the dirtied section would leave the due
  ones. (2) The hold-to-step latency that remains, 18 s. The day is under the budget without either. The first is the
  next section.

### Family sections (#3556)

The third cut of item 3: which sections the invalidations on the rounds-cadence day named, which sections each
family entry's read consumes, and why the two could not be narrowed against each other.

**What the invalidations named.** The measured day above (session `d3ad27f7`) had 26 `clock_invalidation` rows:
16 `pawn_activity:coupled-<id>:completed|failed` (an order the review assigned finishing), 8
`pawn_activity:unassigned_voluntary_pawn` (a controlled colonist offered no job before standby was assigned; standby retired with #3669) and 2
`stockpile fill` (Colony, zone ids). The pawn rows came in pairs at the review tick after each `due` rounds step
(600, 3600, 6600, 9600, 12600, 18600). Each named the Pawns family and a pawn id; no pawn section is an entity section
(`Section.Entities()` is PlanningCells, Zones, Buildings, Bills), so `Invalidation.Sections` dirtied both Population
and Pawns, and `PlannerQueue.Wake` marked 17 of the 22 entries: the 16 families whose `IFamily.Reads` or
`SeriousHarmReads` include Pawns (every family but People, Power, Layout, HomeUpkeep and FoodStores) and `defense`
(`SectionsThreat`). Those marks are the 12 `last` steps with `dirty` 17 at 600, 1200, 3600, 4200 and on, and, since
`Ran` sets an entry's next review 2500 ticks on, they moved the entries' due phase off the rounder's, which gave 9
further due-only `last` steps (22200 to 42597) until the next `full` step realigned them.

**What a family read consumes.** Every `Rounds*.ReadAsync` loads the committed rounds review and then observes
through the shared `Rounder` (`ObserveRoomsAsync` / `ObserveOwnedAsync`, the generation-keyed `Census.Lookup`
or a fresh `ObserveRoundsAsync`): one census, every section of every family the review files. The entry's declared
sections (`FactStore.FamilySections` over `Reads` ∪ `SeriousHarmReads`) are therefore already what the read consumes;
splitting Pawns from Population, or declaring fewer families, would mark fewer entries without changing what any of
them reads, and `ReadNoteRules.NoteRead` (the `Undeclared` audit) sees only the held PlanningCells and Zones reads,
not the census. There was nothing to narrow on the brain side.

**What changed.** The pawn-activity invalidation is gone from native (`Supervisor.ActivityChanged`, its `Reported`
set and the three call sites). An order completing or failing is the brain's own attempt outcome, which the next
review reads from the execution rows; a controlled colonist without an order runs vanilla work (#3669) and is in the census
the next rounds review reads. Neither is an observed fact change. The medical invalidations
(`InvalidateMedicalFacts`, Pawns and Emergency, rate-limited) and the Colony invalidations (zones, harvest, stockpile
fill, game conditions) stay.

Measured 2026-10-18, same colony and machine, brain session `62f7490da4cf41fda31ef1f5ae3e9b5e`, one passing
colony-review day of 61074 ticks, against the rounds-cadence day above:

| What | Rounds-cadence day | This day |
| --- | --- | --- |
| `clock_invalidation` rows | 26: 24 `pawn_activity`, 2 `stockpile fill` | 4: `stockpile fill` (8341, 16681, 30362, 33422) |
| `clock_step` rows | 105 | 106: 98 `review`, 5 `wake`, 2 `full`; every one `planned` |
| `selection` words | `dirty` 302, `due` 285, `dirty+due` 32, `full` 44, `all` 22 | `dirty` 150, `due` 354, `dirty+due` 22, `full` 44, `all` 22 |
| Steps that ran a wave | 46 | 33 |
| Wave wall (`stage_ms` `critical` + `optional`) | 12.6 s + 21.3 s = 33.9 s | 15.8 s + 14.1 s = 29.9 s |
| Steps on the last committed review (`rounds` `last`) | 21, 20.7 s | 13, 14.1 s |
| Rounder runs (`due` / `wake` / `all`), rounds wall | 25 (18 / 4 / 3), 34.4 s | 25 (18 / 4 / 3), 38.9 s |
| Step wall sum | 79.4 s | 79.7 s |
| `clock_run` wall | 15.4 s | 14.4 s |
| Hold-to-step (`stop_latency_ms`) | p50 141 ms, 17.6 s over 103 | p50 167 ms, p95 336 ms, 21.6 s over 104 |
| Brain-held wall / first to last step | 112 s / 160.6 s | 116 s / 168.2 s |

What the day says:

- **The pawn marks are gone, the waves are not.** `dirty` fell from 302 to 150 and the wave steps from 46 to 33, but the
  step wall is the same 80 s: a `due` step runs all 22 entries whether or not an invalidation also marked them, the
  rounds wall rose 4.5 s with the day's own variance, and the day's four stockpile fills each marked 21 entries (every
  family declares Colony, and a zone id narrows to Zones, Buildings and Bills, which are Colony sections).
- **The 13 `last` steps left are a cadence phase, not an invalidation.** An entry runs 2500 ticks after it last ran
  (`ReviewEveryDefault`, one in-game hour) while the rounder runs at the first review tick 2500 past its last review,
  3000 effective. Once a dirty step (8400, the first stockpile fill) ran the entries off the rounder's tick, they came
  due at 11400, 14400, 19800, 22800, 25800, 28800 and on, each a 21-entry wave on the last review (1.1 to 1.3 s), while
  the rounder's `due` steps ran 1 entry; the `full` step at 44401 realigned them and the rest of the day had no `last`
  step. The 150 `dirty` are the 4 stockpile fills (84) and the 4 hazard wakes (66).
- **Next cuts, in order.** (1) Run the family entries on the rounder's cadence (due when a new rounds review is
  committed, not 2500 ticks after their own run): removes the `last` steps, 14 s, and the off-phase waves. (2) A
  stockpile fill marks 21 entries; declaring the zone and bill sections only on the families that read them (Food,
  FoodStores, Economy, Layout) would mark 4. (3) The hold-to-step latency, 22 s. The day is under the budget without
  any of them. Cuts 1 and 2 landed as 976dda3b1, measured in the next section.

### Family entries on the rounder's cadence (#3556)

The fourth cut of item 3, the first two next cuts of the family-sections day: the family entries are due when the
rounder is (`PlannerEntry.RoundsCadence`), and an invalidation narrowed to zone ids marks only the families whose
schedule names the zone census (`FamilySchedule.Sections`, `Section.Keyed`). Landed 976dda3b1.

**What the day before had hidden.** The section narrowing had never reached a family entry: `FamilyEntry` listed every
family the entry reads in `PlannerEntry.Families`, which `PlannerQueue.Wake` matches by name, so a Colony invalidation
marked all 21 entries whatever sections it named, and `Invalidation.Sections` could not tell a zone id from a building
id, so it dirtied Zones, Buildings and Bills together. Now an entry lists only its sectionless families by name (world),
an id names the section it is a row of by vanilla's load-id shape (`Zone_` the zone census, `Thing_` the buildings; an
id of no known shape dirties every entity section as before), and five families declare the zone census: admin (the
stockpile Need), food (fields, blight), food stores (storage upkeep), economy (resources, trade) and layout (the
defensive layout's admission). Bills stay declared with Colony since no invalidation names a bill row.

Measured 2026-10-18, same colony and machine, brain session `8fa5204cbb4346e7be43caf7854b2e22`, one passing
colony-review day of 60580 ticks, against the family-sections day above:

| What | Family-sections day | This day |
| --- | --- | --- |
| `clock_invalidation` rows | 4 `stockpile fill` | 2 `stockpile fill` (22741 `Zone_4`, 30362 `Zone_1`) |
| Entries a stockpile fill marked | 21 | 5 |
| `clock_step` rows | 106: 98 `review`, 5 `wake`, 2 `full` | 105: 98 `review`, 5 `wake`, 2 `full`; every one `planned` |
| `selection` words | `dirty` 150, `due` 354, `dirty+due` 22, `full` 44, `all` 22 | `dirty` 76, `due` 396, `dirty+due` 22, `full` 44, `all` 22 |
| Steps that ran a wave | 33 | 27: the 25 rounder steps and the 2 `last` steps |
| Entries a `due` rounder step ran | 1 | 22 |
| Wave wall (`stage_ms` `critical` + `optional`) | 15.8 s + 14.1 s = 29.9 s | 15.6 s + 15.1 s = 30.7 s |
| Steps on the last committed review (`rounds` `last`) | 13, 14.1 s | 2, 1.4 s (the two stockpile fills, 5 entries each) |
| Rounder runs (`due` / `wake` / `all`), rounds wall | 25 (18 / 4 / 3), 38.9 s | 25 (18 / 4 / 3), 37.3 s |
| Step wall sum | 79.7 s | 78.4 s |
| `clock_run` wall | 14.4 s | 14.0 s |
| Hold-to-step (`stop_latency_ms`) | p50 167 ms, 21.6 s over 104 | p50 166 ms, p95 347 ms, 22.7 s over 103 |
| Brain-held wall / first to last step | 116 s / 168.2 s | 115 s / 168.9 s |

What the day says:

- **The phase is gone, the waves moved.** Every `due` rounder step (3000, 6000, 9000, ... 60580) ran all 22 entries, and
  no entry came due between two reviews: the 13 `last` steps became 2, each a stockpile fill's 5 marked entries. But a
  wave is the 22 entries' derivation, about 1.2 s whatever step carries it, so the 25 rounder steps now hold the 30 s
  the `last` steps and the 1-entry `due` steps shared before. The step wall fell 1.3 s.
- **The `dirty` count is now the hazards.** 76 marks: the 4 hazard wakes (29856, 29941, 30001, 48580: 66, every entry,
  as a hold other than the review tick must) and the 2 stockpile fills (10). A fill no longer wakes the 16 families
  that never read a zone.
- **Two Runs failed, neither new.** The Run at 600 was refused on the hold serial (the hold entered after the step's
  read, as on every day since the 600-tick cadence); the Run at 60580 found no map loaded because the harness had
  ended the day under it.
- **Next cuts, in order.** (1) The waves, 31 s: 25 times a day every entry re-derives its Needs from facts that for
  most families did not change since the last review; the facts records are equatable, so an entry whose read equals
  its last run's could keep its standings and skip the derivation and the work. (2) The hold-to-step latency, 23 s,
  cause still unconfirmed. The rounds stage, 37 s, is the review itself. The day is under the budget without either. Cut 1 landed as 9adcf3d36, measured in the next section.

### A cadence run keeps the waits (#3556)

The first next cut of the entries-on-the-rounder day, redesigned on its measurement: derivation cost 0 ms everywhere
(`family.derive` spans), so skipping it would have saved nothing. The waves were the work hop: on the 18 `due` runs every
heavy family's ready Needs returned the same wait or refusal each time (economy `held:no_source` x16, research x17,
trade `export_bills` x15, admin `construction_labor` x17 and x15, food x17 and x17, rooms `no_space` x17, military x18),
and only admin/work-assignments (admitted 11 of 17) and shelter/starter (4 of 17) ever did anything. So an entry
selected `due` alone, on a step whose rounds word is `last` or `due`, runs for its cadence (`FamilyStep.Cadence`): a
ready Need whose last ordinary run ended in a wait or a refusal keeps that verdict and Method and skips propose, commit
and work. Open work, a held claim, an unread field and an admission are never kept; a `dirty`, `wake`, `full` or `all`
selection, a Need that was not ready last time or derives differently now (a live read changed: clearance's census once
the pawns finish a designated step, #3681), the immediate pass and another world run the work again. Kept Needs
are counted on the `clock_step` row (`kept`) and marked on their `planner_step` rows (`kept`). Landed 9adcf3d36.

Measured 2026-10-18, same colony and machine, brain session `949d4fe485dc46faabaab1e5b3898289`, one passing
colony-review day of 60000 ticks, against the entries-on-the-rounder day above:

| What | Entries-on-the-rounder day | This day |
| --- | --- | --- |
| `clock_step` rows | 105: 98 `review`, 5 `wake`, 2 `full`; every one `planned` | 104: 98 `review`, 4 `wake`, 2 `full`; 103 `planned`, the last (60006) failed under the harness's end |
| `selection` words | `dirty` 76, `due` 396, `dirty+due` 22, `full` 44, `all` 22 | `dirty` 54, `due` 396, `dirty+due` 22, `full` 44, `all` 22 |
| Steps that ran a wave | 27: 25 rounder steps, 2 `last` | 26: 24 rounder steps, 2 `last` |
| Needs kept on the 18 `due` rounder steps | 0 | 264: 11 at tick 3000 rising to 18 from 39006 on; 0 on every `wake`, `full`, `all` and `last` step |
| Due-step `planner_ms` sum, 22 entries x 18 steps | 90 s | 48.6 s |
| Due-run mean per heavy entry (economy, admin, food, shelter, rooms, medical) | 977, 566, 324, 286, 192, 161 ms | 191, 194, 121, 217, 223, 111 ms |
| Wave wall (`stage_ms` `critical` + `optional`) | 15.6 s + 15.1 s = 30.7 s | 7.5 s + 6.0 s = 13.5 s |
| Steps on the last committed review (`rounds` `last`) | 2, 1.4 s | 2, 1.9 s (stockpile fills at 30307 and 51067, 5 entries each, nothing kept) |
| Rounder runs (`due` / `wake` / `all`), rounds wall | 25 (18 / 4 / 3), 37.3 s | 24 (18 / 3 / 3), 44.2 s |
| Step wall sum | 78.4 s | 68.3 s |
| `clock_run` wall | 14.0 s | 13.3 s |
| Hold-to-step (`stop_latency_ms`) | p50 166 ms, p95 347 ms, 22.7 s over 103 | p50 138 ms, p95 211 ms, 17.4 s over 102 |
| Brain-held wall / first to last step | 115 s / 168.9 s | 99.0 s / 139.4 s |

What the day says:

- **The wave halved and is now its reads.** The `due` steps' planner time fell from 90 s to 48.6 s and the wave wall
  from 30.7 s to 13.5 s. On a `due` step a family entry now costs 88 to 223 ms (anomaly and disaster about 30, defense 3), and that is `family.read` with its hop
  wait (per entry 10 to 93 ms of hop wait on the read); `family.work` ran 25 times for economy over the whole day
  instead of for its three or four waiting Needs on every wave. The step wall fell 10 s and the brain-held wall 16 s.
- **What it kept, and what it kept that time could have changed.** Per concern over the 18 steps: admin/burial 17,
  animals/herd 17, anomaly/monolith 17, economy/research 17, food-stores/storage 17, home-upkeep/clearance 17,
  layout/defense 17, military/defense 17, rooms/comfort 17, economy/resource 16, food/supply 16, food/cooking 17,
  admin/stockpiles 14, economy/trade 14, shelter/temperature 11, people/mood 23 over three pawns. Every one is a wait
  or refusal on the committed review's facts, except people/mood's `method_already_used`, which a cooldown in the
  journal ends: a kept verdict whose cause is time alone refreshes only on evidence or on the `full` step
  (`FullStepEvery`, every 2 real minutes). The economy/resource verdict did change during the day, from
  `awaiting_plan research` (9 due runs) to `held:no_source` (8), at the hazard wakes around tick 30000; without a
  wake the cadence would have kept the old answer until the `full` step at 54006. Nothing was kept that then
  admitted: admin/work-assignments (admitted 12, `field_unavailable` 5), shelter/starter (admitted 3, open work 14)
  and shelter/safe-area (admitted 2) were worked on every run, as the rule requires.
- **The rounds stage grew 7 s.** 44.2 s over 23 reviews against 37.3 s, p50 1981 ms, one review of 3841 ms at tick
  9000 (step wall 5037 ms). It is the review itself, not the waves; whether it is the day's noise or the colony's
  growth needs a second day before it is a cut.
- **Next cuts, in order.** (1) The reads, 13.5 s: 22 entries each take the colony facts through the game-thread hop on
  every wave; a read shared across the wave, or taken from the census the review just committed, would leave the
  wave its work alone. (2) The rounds stage, 44 s. (3) The hold-to-step latency, 17 s, cause still unconfirmed. Cut 1 landed as 6564adcd8, measured in the next section.

### The wave's reads share the step's scope (#3556)

The first next cut of the kept-waits day: on a `due` step each of the 22 family entries cost 88 to 223 ms, almost all
of it one `lifecycle_read_tick` hop to the game thread (10 to 93 ms queued behind the other entries' hops, 23 to 25
`ReadTickAsync` per entry per day) to fetch the observation scope its read validates against, while the clock step had
already read that scope with its clock status. The step now decodes its clock-status context once
(`ClockScheduler.StepIdentity`) and the catalog hands it to every family step (`FamilyStep.Scope`); the read scope
carries it (`FamilyReadScope.Scope`) and `StepScope.StepScopeAsync(scope, native, ct)` returns it instead of hopping.
The game is held for the step, so the scope is fixed and the read sees the tick the hop would have returned. Outside a
clock step the scope is null and a read hops as before; a family's work keeps its own scope reads. Landed 6564adcd8.

Measured 2026-10-18, same colony and machine, brain session `6c1562c3f89740ec83f022160fbcbefb`, one passing
colony-review day of 60000 ticks, against the kept-waits day above:

| What | Kept-waits day | This day |
| --- | --- | --- |
| `clock_step` rows | 104: 98 `review`, 4 `wake`, 2 `full`; 103 `planned` | 105: 99 `review`, 4 `wake`, 2 `full`; every one `planned`; the first `clock_run` (tick 600) failed, the harness's review hold entered after the step's read |
| `selection` words | `dirty` 54, `due` 396, `dirty+due` 22, `full` 44, `all` 22 | `dirty` 54, `due` 413, `dirty+due` 27, `full` 44, `all` 22 |
| Steps that ran a wave | 26: 24 rounder steps, 2 `last` | 27: 25 rounder steps, 2 `last` |
| Needs kept on the `due` rounder steps | 264 over 18 | 292 over 19: 11 at tick 3000 rising to 18 from 41992 on |
| Due-step `planner_ms` sum, 22 entries | 48.6 s over 18 steps | 22.0 s over 19 steps |
| Due-run mean per heavy entry (admin, shelter, economy, military, people, medical) | 194, 217, 191, 103, 88, 111 ms | 191, 173, 167, 122, 109, 83 ms |
| Due-run mean per light entry (rooms, layout, goals, power, ideology, anomaly, disaster) | 223, 216, 169, 167, 166, 30, 26 ms | 57, 50, 12, 9, 10, 3, 3 ms |
| `ReadTickAsync` per entry per day | rooms 23, shelter 23, layout 25; economy 49, admin 66, food 45; military 25 | 0, 0, 0; economy 26, admin 45, food 22 (their work's own scope reads); military 25 (its census observe) |
| `family.read` hop wait, per-run mean | home-upkeep 93, military 92, food 60, food-stores 62, economy 48, power 40, goals 36 ms | home-upkeep 92 (clearance targets), military 111 (census observe), economy 51, people 22 ms; every other entry 0 |
| Wave wall (`stage_ms` `critical` + `optional`) | 7.5 s + 6.0 s = 13.5 s | 7.6 s + 5.4 s = 13.0 s |
| Steps on the last committed review (`rounds` `last`) | 2, 1.9 s | 2, 1.4 s |
| Rounder runs (`due` / `wake` / `all`), rounds wall | 24 (18 / 3 / 3), 44.2 s | 25 (19 / 3 / 3), 44.5 s, p50 1699 ms, p95 3738 ms |
| Step wall sum | 68.3 s | 68.1 s |
| `clock_run` wall | 13.3 s | 13.7 s |
| Hold-to-step (`stop_latency_ms`) | p50 138 ms, p95 211 ms, 17.4 s over 102 | p50 143 ms, p95 201 ms, 17.2 s over 103 |
| Brain-held wall / first to last step | 99.0 s / 139.4 s | 99.0 s / 147.9 s |

What the day says:

- **The wave's cost halved again; its wall did not move.** The `due` steps' summed planner time fell from 48.6 s to
  22.0 s and the light entries from about 170 ms to under 12 ms each (anomaly, disaster, defense 2 to 3 ms); some 420
  tick hops a day left the game thread's queue. But the wave runs its entries concurrently, so its wall is its
  slowest entry, and on a `due` step those are the entries that work through the hop every run: admin 191 ms
  (work-assignments admits every run; `family.work` 242 ms mean, 213 of it hop wait), shelter 173 ms (starter and
  safe-area), economy 167 ms, military 122 ms (its census observe is a 111 ms game read). The wave wall stayed at 13 s,
  the step wall at 68 s and the brain-held wall at 99.0 s. The cut pays off once the heavy entries' work leaves the
  wave; it does not pay now.
- **The rounds stage is 44 s on a second day.** 44.5 s over 25 reviews against 44.2 s over 24; p50 1699 ms, p95 3738 ms.
  Against the entries-on-the-rounder day's 37.3 s the growth holds, so it is the colony's growth, not noise. It is now
  65% of the step wall and the largest cut left.
- **The empty steps cost about 23 s.** 78 of the 105 steps selected no planner (84 ms step wall each, 7.1 s), yet each
  still paid its hold-to-step latency (p50 143 ms) and its `clock_run` (p50 67 ms): about 23 s of the 99 s the brain
  holds the game, on steps that did nothing. Hold-to-step is 17 s for the third day running at the same p50, so it is
  the hold's own cost, not load.
- **Next cuts, in order.** (1) The rounds stage, 44.5 s: the review itself. (2) The empty review steps, about 23 s:
  the review hold every 600 ticks when nothing is dirty or due. (3) The heavy entries' work on every `due` wave
  (admin, shelter, economy), which now sets the wave's wall.

### The review's search, profiled; the warm start survives renumbering (#3556)

The first cut at the rounds stage (44.5 s a day over 25 reviews on the shared-scope day), in two steps. First the
profile: the stage's `review` lap (26 to 28 s a day, p50 900 to 1000 ms) is the policy inspection, and the
inspection is the coupled portfolio search (`inspect_ms`); the other laps are `layout` 5 to 6 s, `d_planned` 3.5 to
4 s (its planned-site reads), `derive`, `acquisition` and `d_stock` 1.5 to 2.5 s each. The `rounds_review` row now
carries `search_profile`, per search phase: its wall, the schedule searches it ran and their wall, the outcome
evaluations it started (#3722), the subset evaluations that ended infeasible, without supported relief, as a swap,
or as a new leader, and the subsets a proven bound skipped or no bound covered (#3724). On the profile day (session
`a53f4706ced4430998dc04d97f243679`, 26 reviews, review lap 27.6 s, search 26.0 s):

| Phase | Reviews | Expansions | Wall | Schedule searches | Infeasible | New leader |
| --- | --- | --- | --- | --- | --- | --- |
| `seed` (every root alone, screened at 64) | 26 | 167,959 | 12.8 s | 3,454 | 1,155 | 68 |
| `extend` (the leader plus each root, to a fixpoint) | 16 | 276,336 | 10.9 s | 9,496 | 6,650 | 169 |
| `confirm` (the leader at 4,096) | 15 | 56,415 | 2.1 s | 15 | 0 | 0 |
| `warm`, `unserved`, `baseline` | 9, 10, 26 | 1,781 | 0.1 s | 53 | 35 | 6 |

What the profile says. The seed pass is a fixed cost: 147 single-root screens every review, 7,437 expansions, 0.3
to 1.5 s, the same whether or not a warm leader stands, and in the last seven reviews of the day every one of the 147
came back infeasible (`no_feasible_need_choice` for food, joy and rest at night) while the search still screened them
all. The extension is the variable cost and it scales with the roots times the levels: about 25,000 expansions and 1
to 1.5 s when the previous selection was missing, 2,300 to 7,000 when it stood, and it was missing in 12 of 25 reviews
because a tiled replan (10 a day) hands an unbuilt planned room a new id (room5, room35, room65: never reused, no
`Replaces` link), so `planned/room5/door/Steel/composition/15` named nothing. The confirm always ran its full bound
(4,097 expansions, truncated) and never changed the leader. Per expansion the search costs 27 µs and per schedule
alternative evaluated about 0.2 ms; a single-root screen evaluates about 11 alternatives, almost all of them then
rejected as construction without supported relief.

The cut: the rounder remembers the planned rooms with the selected ids, and `RoundsRules.WarmStartOf` points a
previous id whose room left the plan at the current room of the same role whose floor overlaps the remembered one, the
lowest id when several do, before the search (`warm_translated` on the row counts the ids moved). The search's clock
is injected (`clockMs`), since Policy reads no clock. Landed 891337f0b.

Measured 2026-10-18, same colony and machine, brain session `450b39dc04eb42848620cbb8f88eff79`, one passing
colony-review day of 60000 ticks, against the shared-scope day and the profile day above:

| What | Shared-scope day | Profile day | This day |
| --- | --- | --- | --- |
| Rounder runs, rounds wall | 25, 44.5 s, p50 1699 ms, p95 3738 ms | 26, 47.5 s, p50 1622 ms, p95 3237 ms | 25, 44.1 s, p50 1393 ms, p95 4377 ms |
| `review` lap | 26.1 s, p50 900 ms | 27.6 s, p50 1014 ms | 25.8 s, p50 821 ms |
| `warm_start` after the first two reviews | 11 `seeded`, 12 `missing` | 9 `seeded`, 15 `missing` | 23 `seeded`, 0 `missing`; 9 reviews moved 1 to 8 ids |
| Warm leader screened / stood | 9 / 6 | 9 / 6 | 20 / 9 (11 infeasible: the remembered work is done or its pawn moved on) |
| Selection taken from the warm leader | 3 | 3 | 4 |
| `extend` expansions, wall | 300,000 (est.) | 276,336, 10.9 s | 229,347, 11.5 s |
| `seed` expansions, wall | | 167,959, 12.8 s | 158,361, 10.5 s |
| Search expansions a day | | 503,000 | 446,000 |
| Reviews over 20,000 extension expansions | | 9 | 6 |
| Wave wall (`critical` + `optional`) | 13.0 s | 8.5 s + 7.3 s = 15.8 s | 7.9 s + 5.3 s = 13.2 s |
| Step wall sum | 68.1 s | 74.3 s | 68.6 s |
| `clock_run` wall | 13.7 s | 15.2 s | 14.0 s |
| Hold-to-step (`stop_latency_ms`) | p50 143 ms, 17.2 s over 103 | p50 142 ms, 18.1 s over 106 | p50 144 ms, 18.8 s over 105 |
| Brain-held wall / first to last step | 99.0 s / 147.9 s | 107.6 s / 175.1 s | 101.4 s / 150.1 s |

What the day says:

- **The translated seed stands where the remembered work is still open, and then the extension is a fifth of the
  cost.** In the reviews whose ids were moved and whose seed was feasible, the extension fell from 26,000 to 29,000
  expansions on the profile day to 4,753 (tick 36013), 10,720 (15000) and 15,377 (21000); where the moved seed was
  infeasible (27000, 42013) the search ran exactly as before (28,018 and 14,696). Over the day the extension's
  expansions fell 17% and the search's 11%; the rounds wall is 44.1 s against 44.5 s and 47.5 s, p50 1393 ms against
  1699 and 1622. The per-expansion wall varies 1.8x between days for the same search (tick 18000: 55,915 expansions in
  1528 ms then 2838 ms), so the expansions are the measure, not the milliseconds.
- **The seed pass is the fixed cost left: 10 to 13 s a day.** 147 screens per review at 2.6 to 10 ms each, every
  review, and all 147 infeasible in the night reviews. It does not read the warm start.
- **A warm leader stands in under half the reviews.** 9 of 20 screened; the other 11 were infeasible because the
  selected work completed or its pawn's needs moved on, and the search then starts from the seed pass as before.
- **Next cuts, in order.** (1) The seed pass, 10 to 13 s: skip or shorten the single-root screens whose root cannot
  be feasible at this tick (the night reviews screen 147 roots that all fail the same need choice), or screen a root's
  composition variants once. (2) The extension when no warm leader stands, 6 to 11 s. (3) The empty review steps,
  about 23 s, and hold-to-step at 18 s, unchanged.

## Hosting in the mod (#3115)

Decided (user, 2026-10-13):

- **Who starts it.** `BrainBootstrap.Start` (Bridge, `Protocol/`), called once by the mod's one startup,
  `NativeStartup` ([native facade](native-facade.md)). Without `-rimgovernor-inproc`
  (`InProcLaunch.Requested(Environment.GetCommandLineArgs())`) it creates no brain thread, database or file; the engine-quit cleanup hook still runs. With it,
  one task polls the game thread (a facade hop, so it is also "not yet" until the host has set up game-thread access)
  until `PlayDataLoader.Loaded` and the Defs exist, for up to 20 minutes; resolves the environment; takes the route
  table; and calls `BrainHost.StartAsync`. Every failure is logged and none reaches the loading host. The brain lives
  per process, not per save. The engine's `ShuttingDown` callback cancels and joins startup, then stops the brain
  off-thread while the game thread drains the existing native request queue (15 s hang guard, one 8 s drain attempt).
  Only after a successful stop does it call `Game.Dispose` and clear `Current.Game`, before engine jobs and native
  allocations are torn down. Menu/load disposal remains the game's normal path and does not stop the process-wide brain.
  `lifecycle/shutdown-cleanup` invokes the installed engine quit delegates on a loaded lab and verifies the brain is
  stopped, the map is disposed, `Current.Game` is cleared, and native allocation bytes fall before the engine sweep.
  A second notification verifies idempotency; `native-shutdown` covers the final-hop deadlock and failed/hung stops.
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
  | `RIMGOVERNOR_UNPIN_TEST_SPEED` | acceptance case override: a `-rimgovernor-test-acceleration` launch keeps the player's speed instead of pinning Hyperfast | off |

  The launcher sets the last three from its saved settings. The default database is shared by every save, so an
  incompatible-schema reset affects them all. Launcher Telemetry is intentionally unmapped (not a bug, #3117): the
  host writes OTLP files and has no endpoint to send to, so the saved setting has nothing to drive.
  `-rimgovernor-test-acceleration` also sets `ClockTestAcceleration` (the Hyperfast pin), unless `RIMGOVERNOR_UNPIN_TEST_SPEED`
  is on: a case sets it through `ServeSpec.Env` to observe the player's speed under the brain, and only the pin goes;
  native's test acceleration (`AcceptanceWorld`, the tick profile) stays on.
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
