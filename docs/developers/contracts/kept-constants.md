# Kept constants

[Documentation](../../README.md) · [Contracts](README.md)

Epic #2533 removed the caps and refusals that pre-vetoed what RimWorld or the brain
should decide. What remains is listed here once, each with the reason it stays
or the follow-up issue that owns it. A code comment at each constant points
back to this page. A cap that survives and can cut data tells the reader (see
[event detail](#visible-truncation)); a cap that does not belong gets an issue,
not a silent exemption.

## Kept, with the reason

| Constant | Where | Value | Why it stays |
| --- | --- | --- | --- |
| `ScheduleHours` | `WorkAssignment.cs`, `NativeWorkSettings.cs` | 24 | A game day is 24 hours; the timetable has one `TimeAssignmentDef` per hour. See [work assignment](work-assignment.md). |
| `domain.MaxPriority` | `WorkAssignment.cs` | 4 | The one range constant for priorities 0-4: RimWorld's manual work priorities are 1 (first) to 4 (last) with 0 disabled, and Standard/Incident priorities share the scale so a plan ranks against the work matrix. Domain validation, the work and apparel-policy readers and the gear loadout use it; the Concern-specific values (`artPriority`, `burialPriority`, `babyFeedingPriority`) are choices inside the range, not bounds. |
| `raidGiveUpTicks` | `CombatWait.cs` | 38000 | Humanoid raiders give up 26k-38k ticks after arriving (sappers 33k-38k); a raid still present past the window's end is not leaving and the fight re-forms. The vanilla window, not a tuning knob. |
| `CombatMirror.RingSize` | `CombatMirror.cs` | 1024 | The combat event ring; every frame carries it whole, and a reader works from the watermark. See [hazard detection bounds](../architecture/hazard-detection-bounds.md#combat-event-ring). |
| `CombatMirror.MaxDetailChars` | `CombatMirror.cs` | 256 | An event row's free-text `Detail`; a cut value ends in `...[truncated]` ([below](#visible-truncation)). |
| `InvalidationEntitiesMax` | `NativeClockRuntime.cs` | 64 | The narrowing of an observation invalidation: a larger entity list is an invalid scope, and the family-wide form is the way to name more. A message bound, not a game rule. |
| `ColonyEventChannel.Capacity` | `ColonyEventChannel.cs` | 1024 | The colony events native queues for the brain; a write past it is dropped and counted, and the brain plans Full. A memory bound, not a game rule. |
| Overlay bounds `MaxCells`, `MaxLabels`, `MaxLayerId` | `ProtoOverlayTools.cs` | 250000, 4096, 64 | Input bounds on an output-only draw request. |
| Placement candidates | `PlacementProtocol.cs` | 1..64 | Input bound on one placement preview/apply request, a main-thread budget. |
| Request id lists | room and supplies observation tools (256), zone observation `MaxIds` (16), quest shuttle explicit pawns (256) | 256 / 16 | The caller names exact ids; an input bound; larger sets are several requests. |
| `CombatGeometryTools` `MaxCells`, `MaxHostiles`, `MaxRadius` | `NativeCombatGeometryTools.cs` | 64, 16, 12 | Measured on `lab-ranged`: 64 cells x 8 pawns took 1.3-6.6 ms, about 13 ms at the caps. |
| Door and mortar frame caps | `SnapshotFrames.cs` | 64, 16 | Transport caps on a frame section; census #2641 kept them. |

## Forecast native reads: no cap, measured (#3341)

The forecast's native reads (planned sites, planned routes, needs, and the coupled read with its construction,
food, cooking, recipe, thermal, support, practice and hunt sub-reads) cap nothing: every colonist, candidate and
location pair is read. Each read searches a path once (`NativePathLegs`) and re-prices it per colonist. The
main-thread time on a lab measurement (32 free colonists, five planned rooms, twenty wood stacks,
eight meal stacks, eight beds; one lab map, 2026-10-16) is a measured fact, not a bound:

| Read (one call a round) | Cold ms | Warm ms | Size |
| --- | --- | --- | --- |
| Planned sites (`NativeConstructionActivity`, `Planned`) | 288 | 269 | 20 sites (largest 144 cells), 4420 rows, 1275 path searches |
| Planned routes (`NativeConstructionActivity`, `Routes`) | 98 | 92 | 311 probes, 9952 routes, 451 path searches |
| Needs (`NativeNeedsForecast`) | 55 | 45 | 32 colonists, 240 targets |
| Coupled (`NativeCoupledForecast`, needs passed in) | 104 | 99 | all sub-reads, 16 locations, 8192 routes |
| Round total | 546 | 506 | |

The round stalls the main thread about half a second on this colony. No limit is set from it; spreading a read
across frames is the remedy if the stall matters.

## Mirrored game literals (#3339)

The roof and room consequences use game literals that RimWorld 1.7 keeps in method
bodies or private fields, where no read can reach them. Each is mirrored once, and
its code comment cites the 1.7 member; no fixture check compares them. A game update
that changes one changes the forecast silently until the comment's member is
re-read. The brain's are in `ForecastRoofConsequences.cs`, the
native ones `NativeRoomPrediction.cs`.

| Constant | Value | 1.7 member |
| --- | --- | --- |
| `PsychologicallyOutdoorsOpenRoofCells`, native `PsychOutdoorsOpenRoof` | 300 | `Room.PsychologicallyOutdoors` (`OpenRoofCountStopAt(300)`) |
| `PsychologicallyOutdoorsEdgeOpenFraction`, native `PsychOutdoorsEdgeFraction` | 0.5 | `Room.PsychologicallyOutdoors` (map edge, open/cells) |
| `OutdoorTemperatureOpenFraction` | 0.25 | `Room.UsesOutdoorTemperature` |
| `BedThoughtsEarliestTicks`, `BedThoughtsLatestTicks` | 2500 (`GameTime.TicksPerHour`), 10000 | `Toils_LayDown.LayDown` (`Rand.Range(2500, 10000)` before `ApplyBedThoughts`) |
| `NeedIntervalTicks` | 150 | `Pawn_NeedsTracker.NeedsTrackerTick` |
| `OutdoorsDeltaScale` | 0.0025 | `Need_Outdoors.NeedInterval` |
| `Outdoors*Roof` deltas | -0.45, -0.4, -0.32, 1, 5, 8 | `Need_Outdoors.Delta_{Indoors,Outdoors}{ThickRoof,ThinRoof,NoRoof}` |
| `OutdoorsFallFloor`, `OutdoorsInBedFactor` | 0.2, 0.2 | `Need_Outdoors.Minimum_IndoorsThinRoof`, `DeltaFactor_InBed` |
| `OutdoorsFree` .. `OutdoorsTrapped` | 0.8, 0.6, 0.4, 0.2, 0.05 | `Need_Outdoors.CurCategory` |
| `SteadyCellsFractionPerTick` | 0.0006 | `SteadyEnvironmentEffects.MapFractionCheckPerTick` |
| `DeteriorationMinimumRate`, `DeteriorationChanceDivisor` | 0.001, 36 | `SteadyEnvironmentEffects.TryDoDeteriorate` |
| `DeteriorationRainLow`, `DeteriorationRainHigh` | 1, 5 | `StatPart_EnvironmentalEffects.TransformValue` (rain lerp) |
| `WorkTableOutdoorsFactor` | 0.8 | `StatPart_WorkTableOutdoors.WorkRateFactor` |
| native `MaxRegionsForRole` | 60 | `Room.UpdateRoomStatsAndRole` |
| native `SpaceStandable`, `SpaceWalkable`, `SpaceCap` | 1.4, 0.5, 350 | `RoomStatWorker_Space.GetScore` |
| native `RoofedFilthBeautyFactor`, `PollutedTerrainBeauty` | 0.3, -1 | `BeautyUtility.CellBeauty` |

The weather thought's 120-tick interval (`Pawn_MindState.MindStateTick`) is not
used as a number: the thought is applied for whole spans.

## Mirrored game literals: capacity penalties (#3519)

The capacity penalty tables ([forecast contracts](forecast-contracts.md#capacity-penalty-tables-and-per-pawn-capacities))
carry what the malnutrition and food poisoning defs state. These are the
method-body literals that move their severity, the downing thresholds, and the
need categories that carry an effect. The capability-days ledger
(`ForecastValue`, #3520) mirrors the malnutrition rate and the awake threshold as
constants whose comment cites the member below; the other rows document the
mechanics the thermal and rest pricing read (`ForecastValueThermal.cs`, #3521: the giver steps every 60 ticks, the ledger integrates its continuous limit over each span). All 1.7 members, read with
ilspycmd.

| Literal | Value | 1.7 member |
| --- | --- | --- |
| Malnutrition severity gain while starving, per 150-tick need interval | 0.0011325 (0.453 a day), × `Lerp(0.8, 1.2, Rand.ValueSeeded(thingIDNumber ^ 0x26EF7A))` per pawn | `Need_Food.BaseMalnutritionSeverityPerInterval`, `MalnutritionSeverityPerInterval` |
| Malnutrition severity loss while not starving | the same amount per interval | `Need_Food.NeedInterval` |
| A pawn is downed unless consciousness is at least 0.3 and Moving is above its `minForCapable` (a def value, in the tables) | 0.3 | `PawnCapacitiesHandler.CanBeAwake`, `CapableOf`, `Pawn_HealthTracker.ShouldBeDowned` |
| A capacity's level under a hediff | `min((base + Σ offset) × Π postFactor, min setMax)` | `PawnCapacityUtility.CalculateCapacityLevel` |
| Starving, urgently hungry, hungry | food level 0 or below; below 0.4 and below 0.8 of `RaceProperties.FoodLevelPercentageWantEat` | `Need_Food.CurCategory`, `PercentageThreshUrgentlyHungry`, `PercentageThreshHungry` |
| Hunger fall by category (Fed, Hungry, UrgentlyHungry, Starving) | × 1, 0.5, 0.25, 0 | `HungerLevelUtility.HungerMultiplier` |
| Exhausted, very tired, tired | rest level below 0.01, 0.14, 0.28 | `Need_Rest.CurCategory` (`ThreshVeryTired`, `ThreshTired`) |
| Rest fall by category (Rested, Tired, VeryTired, Exhausted) | × 1, 0.7, 0.3, 0.6 of 1.5833333E-05 a tick | `Need_Rest.RestFallPerTick` |
| Rest gain while resting, per need interval | 0.005714286 × rest effectiveness × `RestRateMultiplier` | `Need_Rest.NeedInterval` |
| Involuntary sleep MTB once rest has been 0 for over 1000 ticks | 0.25 days (under 15000 ticks), 0.125 (30000), 1/12 (45000), 0.0625 | `Need_Rest.ShouldInvoluntarySleepFromMTB` |
| Hypothermia severity gain, every 60 ticks below the safe minimum | `max(\|ambient − safeMin\| × 6.45E-05, 0.00075)` | `HediffGiver_Hypothermia.OnIntervalPassed` |
| Hypothermia and heatstroke recovery, every 60 ticks inside the comfort range | `Clamp(severity × 0.027, 0.0015, 0.015)` off | `HediffGiver_Hypothermia`, `HediffGiver_Heat.OnIntervalPassed` (only while the ambient is above the comfort minimum, respectively below the comfort maximum; between comfort and safe the severity holds) |
| Safe temperature range | the comfortable range widened by 10 each side | `GenTemperature.SafeTemperatureRange` |
| Heatstroke severity gain, every 60 ticks above the safe maximum | `max(curve(ambient − safeMax) × 6.45E-05, 0.000375)`, curve (0,0) (25,25) (50,40) (100,60) (200,80) (400,100) (4000,1000) | `HediffGiver_Heat.OnIntervalPassed`, `TemperatureOverageAdjustmentCurve` |

Which categories carry an effect. Hunger and rest categories change no capacity
and no work speed. Starving gives `Malnutrition`, whose stages carry the capacity
penalty. The `StatPart_Food` and `StatPart_Rest` parts on `ImmunityGainSpeed`
(factors 0.9 and 0.7 for urgently hungry and starving, 0.96, 0.92 and 0.8 for
tired, very tired and exhausted) are the only stat parts the game reads a need
category through, and slow immunity only. Every other category effect is a mood
thought (`ThoughtWorker_NeedFood`, `ThoughtWorker_NeedRest`, already in the mood
model) or a rate above. The game's `HediffGiver_Heatstroke` is `HediffGiver_Heat`.

The ledger prices a hediff's pain (`painOffset`, which lowers consciousness by up
to 0.4 in `PawnCapacityWorker_Consciousness`) as unpriced: the pawn's current pain
is not observed, so a priced stage with pain reports `hediff_pain_unpriced`.
Malnutrition's per-pawn rate factor (`Lerp(0.8, 1.2, ...)` seeded by the thing id
number) is not observed either: the mean 1 is used and `malnutrition_rate_pawn_factor_unobserved`
is reported.

## Mirrored game literals: plant resting (#3523)

`PlantResting` (`brain/src/RimGovernor.Brain.Policy/ColonyModel/PlantResting.cs`) mirrors a method-body literal no def carries.
The tier-2 colony model integrates crop growth per twelfth and needs the fraction of a day a plant grows. Read with ilspycmd (1.7).

| Literal | Value | 1.7 member |
| --- | --- | --- |
| A plant does not grow while the local day fraction is under 0.25 or over 0.8 | resting 0.45 of a day, growing 0.55 (22.5 grow days take about 41 calendar days) | `Plant.Resting`, used by `Plant.GrowthPerTick` |

## Policy constants (#3469)

`ForecastValuePolicy` (`brain/src/RimGovernor.Brain.Policy/ForecastValue.cs`)
carries the constants of the capability-days currency
([forecast value](../architecture/forecast-value.md#capability-days)). They are
choices, not game numbers; the defaults are the accepted starting values and
every `coupled_selection` decision reports the policy it used. Tests inject
them through `ForecastCoupledContext.Value`; production uses the defaults.

| Constant | Default | Unit | Why it is a choice |
| --- | --- | --- | --- |
| `DeathTerminalValue` | 60 | capability-days | What a colonist is worth beyond the horizon: about two game months of full capability. Read by the harm term (#3471) and, on a lethal thermal severity, the thermal term (#3521). |
| `WellbeingWeight` | 0.1 | capability-days per mood-day | Wellbeing valued for itself, above the break risk the mood term already counts. The game states no exchange rate. Priced today. |
| `HarmRiskAlpha` | 0.9 | fraction | The CVaR tail of raid harm: the mean of the worst 10% of outcomes. Read by the harm term (#3471). |
| `HarmRiskWeight` | 0.5 | fraction | How much of raid harm is priced at that tail against the mean. Read by the harm term (#3471). |
| `DeathRiskFloor` | 0.01 | expected deaths per day above baseline | The most a portfolio may add before it is infeasible. Read by `ForecastValue.Explain`; the harm model that feeds it is #3471, so the floor is reported unassessed (`death_floor_unassessed`) until then. |
| `BreakRiskCap` | 0.5 | expected breaks per pawn per day | The most break risk one pawn may carry, as expected breaks per pawn-day. Read by `ForecastValue.Explain` (#3522). |
| `TieResolution` | 0.01 | capability-days | A summed loss difference within this is a tie; the ranking then falls through to the unpriced terms, the incumbent and labor. Read by `CompareConsequences` (#3522). |
| `GoalImportanceValue` (Low, Normal, High) | 0.5, 2, 8 | capability-days per whole goal | What completing a goal is worth at each importance; a schedule earns the fraction it progresses. Low is five mood-days at the default `WellbeingWeight`, each level four times the one below. Read by the ledger's `Goals` term (#3503). |
| `GoalDeadlineDecay` | 0.8 | multiplier per day past the deadline | A goal's worth after its deadline. Read by `ForecastValuePolicy.GoalWorth` (#3503). |
| `GoalDeadlineFloor` | 0.1 | fraction of the worth | The least a late goal decays to, so a missed deadline never makes the goal worthless. Read by `GoalWorth` (#3503). |

The player's risk profile and pace choose the rows below (`ForecastValuePolicy.RiskRows`, `PaceWellbeingWeight`; `ForecastValuePolicy.For`),
replacing the constants above for that colony. Normal and Balanced are the defaults in the table above. Cautious has the stricter (smaller) death floor and the larger tail weight; Bold the looser.

| Risk profile | `HarmRiskAlpha` | `HarmRiskWeight` | `DeathRiskFloor` |
| --- | --- | --- | --- |
| Cautious | 0.95 | 0.8 | 0.005 |
| Normal | 0.9 | 0.5 | 0.01 |
| Bold | 0.8 | 0.2 | 0.02 |

| Pace | `WellbeingWeight` |
| --- | --- |
| Wellbeing | 0.2 |
| Balanced | 0.1 |
| Development | 0.05 |

## Tracked in follow-up issues

| Constant | Where | Value | Issue |
| --- | --- | --- | --- |
| `HiveBoundaryCells` | `NativeObservationTools.cs` | 10 | #2652 (reads the game's constant) |

The native attempt ledger, pawn-control table and production bill tracking
have no count cap (#2662): each grows with the live game's pawns, bills or
admitted clock attempts and is dropped with the game.

## Visible truncation

A combat event row's `Detail` is cut to 256 characters. A cut value is the
first 242 characters followed by the marker `...[truncated]`, so the text is
exactly 256 characters and a reader recognises a cut value by the suffix. The
marker is in the text, not a field: no proto change.
