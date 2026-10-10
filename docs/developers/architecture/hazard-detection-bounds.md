# Hazard detection bounds

The native supervisor (`integrations/rimgovernor-native/src/Bridge/SupervisedPlayTool.cs`)
guards a running clock window with a hazard probe (`Probe`) and reports the
facts that changed under it with a digest pass (`PublishFactChanges`). The two are separate: the probe is paced in game ticks so its detection
gap holds at every production `TimeSpeed`, and the digests never run inside
it. `SupervisedPlayHazards.cs` declares the constants and the per-class
table below; the native contract probe (`native-clock`) drives the pure
cadence functions through every speed's tick pattern.

## Probe cadence

| Gate | Where it runs | Interval |
| --- | --- | --- |
| Tick-paced | `DoSingleTick` postfix (`OnTick`), every active epoch | `ProbeIntervalTicks` = 30 ticks |
| Wall-paced | `TickManagerUpdate` postfix (`OnFrame`), once per frame | `ProbeIntervalMs` = 100 ms |
| Hook-requested | the next tick boundary after a direct game hook fires | 1 tick |

A probe runs when any gate is due. The tick gate is the bound: however many
ticks a frame carries (Ultrafast with the ultra-speed boost runs hundreds
per frame), consecutive probes are at most 30 ticks apart. The wall gate
only tightens it at slow speeds: at Normal (60 ticks/s) it fires every 6
ticks, at Fast every 18, at Superfast the tick gate fires first. Under the
acceptance test acceleration (`testAcceleration`) the tick hook calls
the frame path itself, so the same gates apply.

`max_probe_tick_gap` on the clock status (per epoch) and
`session_max_probe_tick_gap` (cumulative for the loaded game) report the
widest gap observed; `hazard_gaps` reports it per class beside the declared
bound. The speedmatrix row carries them as `max_probe_tick_gap` and
`hazard_gaps`.

## Declared bound per hazard class

The bound is the most game ticks a hazard of that class can exist before a
probe evaluates it, at every production speed (Normal, Fast, Superfast,
Ultrafast) and under test acceleration. `detect_ticks` on a stop
(`detected_tick` less `occurrence_tick`) is the measured gap; the
`hazard/*` acceptance cases (`go/internal/nativeaccept/cases/hazard`,
injecting through `scripts/fixtures/HazardFixture.cs` and the letter
fixture) run a window at Ultrafast, inject the class mid-window and assert
both `detect_ticks` and the injection-to-detection gap within the bound;
a hooked class must also land within the hooked bound. The hooks live in
`SupervisedPlayHooks.cs`; when Harmony cannot install them the status
reports every class unhooked and the polled interval bound still holds.

| Stop kind | Hazard | Detection | Bound (ticks) | Occurrence tick on the stop | Acceptance case |
| --- | --- | --- | --- | --- | --- |
| `notification_batch` | a stopping letter or transient message | letters: `LetterStack.ReceiveLetter` hook requests the next-tick probe; messages: polled | 30 (a letter lands in 1) | the newest letter's `arrivalTick` or message's `startingTick` | `hazard/letter` |
| `hostile` | a hostile pawn within `hostileWithin` of a colonist | polled; `Pawn.SpawnSetup` hook requests the next-tick probe when a hostile spawns | 30 (a spawn lands in 1) | the pawn's spawn tick when it spawned under this epoch, else absent | `hazard/hostile` |
| `colonist_downed` | a colonist downed or dead | `Pawn_HealthTracker.MakeDowned` and `Pawn.Kill` hooks request the next-tick probe | 1 | the tick the hook fired; a corpse's `timeOfDeath` | `hazard/downed` |
| `predator_hunt` | a predator hunting within 40 cells of a colonist | polled | 30 | the predator's `PredatorHunt` job `startTick` | `hazard/predator` |
| `hunting_route_unsafe` | a colonist hunting prey along an unsafe route | polled | 30 | the hunter's `Hunt` job `startTick` | (hunting/* areas) |
| `colonist_injury` | a new wound past the severity floor (colony mode): a life-threatening hediff stage, or blood loss killing the pawn within the policy's `injury_severity_floor_ticks` (Go sends 5000) | polled | 30 | the newest wound's age (`ageTicks`) | (a lighter wound is demoted; `hazard/injury` proves the demotion) |
| `colonist_health` | a combat health threshold crossed | polled | 30 | the newest wound's age | (defense/* areas) |

Non-stopping observations (`alert_new`, `hostiles_cleared`,
`injury_observed`) ride the same probe and carry the same 30-tick bound.

## Go-authored thresholds

Native holds no literal for a hazard threshold. `WatchPolicy` carries each one
(values from `policy/hazard_thresholds.go`, attached by
`bridge.WithHazardThresholds`); `NativeClockRuntime.ValidPolicy` and the Go
`clockPolicy()` both refuse a policy missing any or holding one that is not
finite and positive (`injury_severity_floor_ticks` is 1 to int32 max). Only
`hostile_within` (1 to 250) and the cooldown (0 to 1800000 ms) keep proto
bounds, as real limits. The probe, digest and wake intervals above stay
constants.

| Field | Go sends | Used for |
| --- | --- | --- |
| `serious_single_hit_damage` | 20 | one blow at least this heavy is a serious injury (combat mode) |
| `serious_summary_health_floor` | 0.5 | summary health crossing under it is a serious injury; the same value is the floor a resting patient must stay over (`MedicalRestSafety.Eligible`) |
| `serious_bleed_rate_floor` | 1.0 | total bleed rate crossing over it is a serious injury |
| `serious_vital_part_floor` | 0.5 | a vital part hit under this fraction of its health is a serious injury |
| `explosive_near_margin_cells` | 3 | an explosive within blast radius plus this of a colonist is launched near them |
| `melee_reach_cells` | 1.5 | range assumed for a pawn without a ranged weapon |
| `injury_severity_floor_ticks` | 5000 | a new wound bleeding out inside this many ticks keeps the stop |
| `predator_margin_cells` | 25 | a hunt route within this many cells of a wild predator is unsafe (`HuntingSafety.RouteSafe`) |

`RouteSafe` takes the margin from its caller: the policy (the supervisor's
hunt withdrawal), `ColonyFactsRequest` / `SnapshotStreamRequest`
`hunt_predator_margin_cells` (the hunt census; absent, every pair reports
`skipped`), and `Rule.predator_margin_cells` (native rules). Damage and
explosive classification read the last epoch's policy, so before Go has
started an epoch the combat log records those rows unclassified.

A wound under the severity floor, and a resting patient who lost their
eligibility, are demoted tiers, not stops: the probe journals the observation
and an `observation_invalidated` row over the `pawns` and `emergency`
families (a medical review without a stop) and the window runs on, so
neither costs a stop-to-readmit pause. A pawn's demoted injury invalidates
again at most every `MedicalWakeIntervalTicks` (600), so a brawl cannot
replan the colony every probe; the wake raised for a discharged rest watch
is never throttled. `hazard/injury` asserts the wake lands inside the
30-tick bound with the window still running, and the native contract probe
pins the floor's boundary.
Fire is not a hazard class: the supervisor stops for it through the
`ThreatSmall` letter vanilla raises, under `notification_batch`.

## Digest cadence

`PublishFactChanges` recomputes the research, world (faction relations),
game-condition and zone digests and journals an `observation_invalidated`
row when one changed. It runs at the epoch's start (baseline) and then:

| Trigger | Source |
| --- | --- |
| Zone dirty | `ZoneManager.RegisterZone` / `DeregisterZone`, `Zone.AddCell` / `RemoveCell` postfixes |
| Research dirty | `ResearchManager.FinishProject` / `SetCurrentProject` / `StopProject` postfixes |
| Condition dirty | `GameConditionManager.RegisterCondition` / `OnConditionEnd` postfixes |
| World dirty | `Faction.TryAffectGoodwillWith` / `SetRelationDirect` postfixes |
| Interval | `DigestIntervalTicks` = 600 ticks since the last pass, the safety net for a path no hook covers (a faction defeated, a debug command) |

Stockpile fill has its own pass, `RunStockpileFillIfDue`, every
`StockpileFillIntervalTicks` = 60 ticks: it counts each stockpile's used
cells and, when one moved, journals an `observation_invalidated` colony row
narrowed to those zone ids (`stockpile fill: ...`). It reports the change
only; the grow and shrink thresholds stay in the controller's policy, which
re-reads fresh fill on the wake.

The digest pass runs from the frame path after the probe gate, never from
`Probe`; the contract probe asserts the cadence functions are independent
and the epoch timing summary (`probeMs`, `digestMs`) and the clock status
(`probe_elapsed_ms`, `digest_elapsed_ms`, `probe_total`, `digest_total`)
carry the two counters apart. The [throughput
measure](../testing/measure-throughput.md) reports `digest_share` per
speedmatrix row.

## Armed combat stops

A `WATCH_MODE_COMBAT` epoch also stops on the armed events in
`WatchPolicy.combat_stop_events` with a bound of **0 ticks**: the stop
lands on the boundary of the tick the event happened on
(`STOP_REASON_COMBAT_EVENT`, `occurrence_tick` = the stop tick). A game hook
records the event and `TickBody` stops right after the watch checks
(`SupervisedPlayCombatStops.cs`):

| Event | Source |
| --- | --- |
| Downed | `MakeDowned` / `Pawn.Kill` postfixes (colonists) |
| Serious injury | `Pawn_HealthTracker.PostApplyDamage` prefix + postfix |
| Shield broken | `CompShield.Break` postfix |
| Entered range | tick-boundary scan: a hostile's weapon reaching a colonist or turret, or theirs reaching it; each direction once per hostile per combat |
| Melee contact | tick-boundary scan: a new `AttackMelee` pair, once per pair per combat |
| Explosive launched | `Projectile.Launch` postfix |
| Raid phase | `Lord.GotoToil` prefix + postfix (hostile lords) |
| Hostile arrived | `Pawn.SpawnSetup` postfix |
| Breach | `Thing.Destroy` prefix (`KillFinalize` of a player wall, door or turret) |
| Mental break | `MentalStateHandler.TryStartMentalState` postfix |

A combat window with no armed event stops on its tick budget, the
controller's `combatBackstopTicks` (300). The acceptance case
`combatlab/stops` proves the exact tick on `lab-open`.

## Combat event ring

The combat mirror keeps the last 1024 event rows (`CombatMirror.RingSize`) and
every frame carries the ring whole; a reader works from its watermark. A row's
free-text `Detail` is cut to 256 characters and a cut value ends in
`...[truncated]`. Both bounds are kept: see [kept constants](../contracts/kept-constants.md).
