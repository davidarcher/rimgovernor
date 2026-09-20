# Hazard detection bounds

The native supervisor (`integrations/rimgovernor-native/src/Bridge/SupervisedPlayTool.cs`)
guards a running clock window with a hazard probe (`Probe`) and reports the
facts that changed under it with a digest pass (`PublishFactChanges`). Since
#626 the two are separate: the probe is paced in game ticks so its detection
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
acceptance test acceleration (`testAcceleration`, #109) the tick hook calls
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
(`detected_tick` less `occurrence_tick`, #621) is the measured gap; the
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
| `colonist_injury` | a new wound or new bleeding (colony mode) | polled | 30 | the newest wound's age (`ageTicks`) | `hazard/injury` |
| `colonist_health` | a combat health threshold crossed | polled | 30 | the newest wound's age | (defense/* areas) |
| `medical_rest_changed` | a resting patient no longer eligible | polled | 30 | absent | (medical/* areas) |

Non-stopping observations (`alert_new`, `hostiles_cleared`,
`injury_observed`) ride the same probe and carry the same 30-tick bound.
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

The digest pass runs from the frame path after the probe gate, never from
`Probe`; the contract probe asserts the cadence functions are independent
and the epoch timing summary (`probeMs`, `digestMs`) and the clock status
(`probe_elapsed_ms`, `digest_elapsed_ms`, `probe_total`, `digest_total`)
carry the two counters apart. The [throughput
measure](../testing/measure-throughput.md) reports `digest_share` per
speedmatrix row.
