# Native forecast contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

Normal production consumes these read-only facts through the
[shared portfolio selector and dependency composer](../architecture/portfolio-selection.md).
The needs census includes native conditional-thought windows, unclamped mood
target, actual seeker response/freeze mechanics and physiological sleep gates.
Known empty conditional effects differ from unknown support. Exact partial
recipe sources retain total recipe requirement and available source quantity;
one source need not independently fund the recipe. These facts neither apply
thoughts nor authorize execution. Selected input provenance extends only the
existing disposable Method task payload; no save preference or progress owner
is added.

Forecasts are read-only and use native definitions and rates. One forecast
implementation serves deterministic facts and player fact inspection.

[Encounter prediction](encounter-prediction.md) is a separate hypothetical input
to ColonyForecast. Its replaceable service returns encounter consequences and
explicit qualification/coverage, never orders or available colony stock. Shared
forecasting owns downstream hauling, butchery, treatment and displaced work once.

The pure C# [ColonyForecast](../architecture/colony-forecast.md) evaluates
supplied commitment deltas separately from these observed diagnostics.
Its requested plan horizon does not change the existing maintenance window.
Construction shortfalls are exact-resource units, not completion days.
Timed inventory effects are conditional estimates, never observed stock
or proof of native work completion. The [construction forecast slice](../architecture/colony-forecast.md#construction-commitments-and-shared-capacity) retains native remaining work and exact outstanding materials, then evaluates declared construction commitments against shared pawn/material capacity. Its route, rate and availability estimates remain explicit inputs.

[Declared material promises and exclusive equipment](../architecture/colony-forecast.md#material-promises-and-exclusive-equipment)
share that forecast. Native ingredient observations retain bill IDs for reconciling
a declared promise with the same bill's hold. Equipment availability remains a
supplied estimate; forecast claims do not acquire native reservations.
[Live fixed batches](../architecture/colony-forecast.md#live-fixed-batch-material-promises)
project committed Method actions and current native remaining counts into the
shared demand pipeline. Exact single-choice ingredients reconcile with native
holds by bill ID; alternatives stay unknown. [Fixed-batch admission](../architecture/colony-forecast.md#fixed-batch-material-admission)
rechecks journal claims atomically with Method commit against the last full
review's material evidence. This is not a native resource lock; indefinite
production and sale admission remain outside that check.

The work ledger now performs [joint production material selection](../architecture/colony-forecast.md#joint-production-material-choices)
before fixed-batch admission: bounded generation retains allowed ingredient
alternatives, bounded comparison chooses an affordable combination, and only then
are filters pinned. The live objective gives [observed clothing requirements](equipment-upkeep.md#live-clothing-requirement-selection) explicit priority before unclassified batch count, with postponement, unknowns and truncation; quality and colony utility are not calibrated.
The existing journal transaction remains the final shared-material admission owner.
The [need-based valuation proof](../architecture/colony-forecast.md#need-based-production-valuation-proof)
accepts explicit whole-batch relief, material and labor utility costs in the same
selector, caps shared need relief and retains postponement. Live declarers do not
yet supply these estimates; unknown valuation inputs never fall back to batch count.

[Unfinished work](../architecture/colony-forecast.md#ingredients-inside-unfinished-work)
is observed by exact bound item ID and embedded quantities. One initialized bound
item funds one remaining iteration; its ingredients are never loose stock. Unknown
binding gives no credit, unbound-item adoption is not predicted, and cancellation
refunds count only when observed in stock. Remaining counts are mutable progress;
the native managed-configuration check excludes them while CAS snapshots include them.

| Projection | Observed inputs | Bound |
| --- | --- | --- |
| Food and animal feed | Fed food fall, nutrition per eligible eater, food policy, safe reachability, holder and rot deadline | Demand-weighted allocation at current temperature/access; no grazing, future hauling or harvest credit. Downed pawns need assistance, which is not presumed. |
| Harvest and labor | Growing-zone crop, fertility, growth stalls, native yield, sow/harvest work and pending construction work remaining | Native work units; no pawn-hour conversion, season guarantee or completion date. Filtered construction censuses remain unknown. |
| Sowing work | Per pawn and growing zone with a crop set: the cells the grower sow giver accepts now, the pawn's plant work speed, the crop's sow work, route to the nearest cell | Candidate tasks only ([live sowing](pawn-activities.md#live-sowing)); nothing values them yet, no crop choice, and cells past the horizon are cut with a finding. |
| Medical | Native total bleeding and untreated blood-loss death estimate | Current untreated estimate; no inferred disease prognosis or treatment completion. |
| Mood | Current mood, thought target and pawn-specific native break thresholds | Current risk band and target pressure; no probability or time-to-break. |
| Power | Per-network observed watts and stored watt-days | Reserve duration at current deficit; no assumed future generation or grid connectivity. Failed readings remain unavailable. |
| [Materials](mining-contracts.md#material-runway-and-fabrication) | Journaled build/bill consumption, current stock, reserve and safe surface ore | Five-day maintenance target from one to fifteen days of history; prospective ore is not spendable stock and zero-rate days remain unknown. |

Food stock is apportioned only among eaters permitted by native diet, policy and
safe access; kibble is apportioned among animals only, since a colonist eats it
only when nothing better is reachable. Held food belongs to its observed
holder. Earliest-expiry allocation
uses native rot deadlines at the current temperature. The food gate uses the
lowest colonist runway after reserving competing animal shares; unknown combined
demand cannot certify a safe runway. Future harvesting, changing temperature,
feeding jobs and food sharing are not guaranteed. Harvest lead is computed in policy from
raw growth facts, the temperature and the growing calendar (a frost window
lengthens it; a missing fact leaves it unknown), then an observed growth pause
(eclipse, volcanic winter, cold snap) adds its remaining days, and crop work does not reserve future production. A field not yet sown is a plan candidate whose lead is its full grow days across the calendar ([supply model](../architecture/supply-model.md#crop-candidates)).

Fresh animal corpses carry their native meat amount times meat nutrition,
body size, forbid state and a one-tile footprint. Unforbidden corpses count
as pending-butcher stock for eaters eligible for their meat; forbidden
corpses remain observable but contribute no runway. Live designated prey
remains pending.
The [corpse larder](upkeep-contracts.md#corpse-larder) owns reserve release.

A corpse is humanlike when its source race row is (`DefinitionCatalog.HumanlikeCorpse`);
a stock is perishable exactly while it carries `rot_ticks`; raw class and
vegetable come from the def row. Raw meat and meals containing it carry
`is_human_meat`. Each consumer exposes native trait/precept eating disposition.
Unknown or unacceptable disposition excludes that stock from its forecast,
including private inventory. The ordinary corpse larder never releases human
corpses. A pure gate selects an assigned, reachable Psychopath, Bloodlust or
Cannibal butcher, or a pawn whose active Ideology precepts permit butchery.
Native admission rechecks the exact worker and pins a separate humanlike-only
`ButcherCorpseFlesh` bill. A roofed corpse stockpile outside colonists' current
sight lines precedes the bill. Other colonists retain normal butchery memories.

The shared FoodPlan records finite human nutrition without adding a recurring
production rate. Allocation prioritizes a short herd, then survival-meal or raw
trade surplus, then eligible diners' reserved demand. Feed and eligible shared
meal bills use explicit ingredients; shared meals require all diners to accept
human meat. Human survival-meal output is forbidden sale stock, excluded from
ordinary reserve release.

Cooking recipes expose their product's base taste mood offset, nutrition output
per nutrition input and native work per output nutrition (batch counts included).
Ingredient slots are conjunctive; each slot's classes are alternatives, so a fine
meal accepts meat or animal products in its protein slot and requires vegetables
in a second slot. Classification intersects the slot, fixed and default filters.
Unknown or unclassified ingredients remain unavailable rather than becoming an
unrestricted source. Needs-power describes the bench definition, separately from
current usability. Mood is a definition fact, not a promise about a pawn's traits
or ideology. Missing numeric and ingredient facts stay unknown.

Crop and construction work remain separate totals. Native recipe work/capacity
observations remain available for project planning; bill counts are not labor hours.

Strategic mood signals use each pawn's observed native minor-break threshold.
A ten-point recovery margin prevents repeated signals near that threshold; it is
brain hysteresis, not a forecast. Missing thresholds retain established risk.

## Coupled mood break risk

The coupled comparator prices forecast mood as expected mental breaks (#3221), and since #3522 as capability-days.
`MoodBreakModel.BreaksPerDay` (`LayoutMood.cs`, the one owner of the break math)
gives a level's tier one break per renewal cycle of the tier's MTB (4, 0.8 or 0.5
days) plus the 15,000-tick post-recovery cooldown. The pawn's observed minor-break
threshold sets the tier levels. `ForecastMoodResult.BreakRisk` integrates that
rate over the piecewise-linear target trajectory, cut at the tier thresholds. The
2,000-tick wait below a threshold before a break can fire and the break's own
duration are not modelled. Actual mood and layout's work-day/watt price are not used.

`ForecastConsequenceVector.BreakRisk` sums the pawns' risk and is known only when
every pawn's threshold and mood are assessed; otherwise the vector keeps
`mental_break_probability_unassessed`. The ranking no longer reads it, nor the raw
target exposure and pressure, directly. The ledger's `MoodBreaks` term prices the risk
in capability-days (expected breaks times the days a break costs), so mood far above
every threshold has no comparator value, and a pawn whose threshold is unobserved
leaves the term unassessed: two outcomes are compared on the terms both assess. The
break-risk floor reads the same risk per pawn-day against `BreakRiskCap`; when no
threshold supports it the review reports `break_floor_unassessed`. There is no
mood-to-labor rate. Power reserve risk likewise persists across unavailable
reads and serialization; recovery requires readable current networks.

Forecast validation and pawn outcomes are different evidence levels.

### Commitment mood and comfort inputs

A commitment's in-memory forecast inputs (none is a wire or save field) take
effect when all its activities complete inside the horizon, or at the forecast
start when it has no activities (#3282). Failed or later work changes nothing;
unknown completion leaves that pawn's mood, or thermal exposure, unassessed.

- `MoodMemories` add a memory to the pawn's mood timeline from that tick.
- `SituationalChanges` add `OffsetDelta` to the pawn's mood target from that
  tick; removing an active -11 thought is +11. Every other situational thought
  but the banded ones (temperature, roof, food and rest, below)
  stays constant inside `NeedMood.UnclampedTarget`, so the
  `unmodeled_situational_thoughts_constant` finding remains. Changing one
  thought twice for a pawn is `unsupported_situational_change_stacking`.
  `BreakRisk` values the change like any other target movement.
- `ComfortShifts` add `MinDelta`/`MaxDelta` to the pawn's observed
  `ComfortableMin`/`ComfortableMax` in the stationary thermal exposure. The
  observed range already includes the current outfit, so a shift is the
  insulation difference between the predicted outfit and the current one.
  Shifts for one pawn add. They change the pawn's own exposure, not
  `StationaryThermalRelief`, which stays outfit-neutral shelter relief.
- `OutfitChanges` (`ForecastOutfitChange(Pawn, Items)`, #3546) name the whole outfit (apparel and primary weapon) a pawn wears
  from the commitment's effective tick; a pawn with none wears `ForecastCoupledContext.Outfits[pawn]` (the gear census's
  `GearLoadoutInput.Worn`). The effect-source generator sets it from each candidate's `EffectSourceOutfit.Items`. A change with
  `Overlay` (a crafted garment, #3621) instead replaces only the items of the outfit then worn that its items conflict with
  (`GearConflicts`). The effective tick is the start for a commitment without activities; otherwise the evaluation reports it in
  `ForecastCoupledOutcome.OutfitTicks` (`ForecastEffectiveTick`, the same rule as the comfort shifts: positive infinity when the
  work does not complete inside the horizon, unknown when a completion is). The harm term prices its armor; nothing else reads it.

### Food and rest thoughts (#3598)

The `NeedFood` and `NeedRest` situational thoughts follow the needs timeline of
the pawn's choices instead of staying at their observed stage.

- `ForecastNeedMetric.StageSpans` is the need walk's output for food and rest: contiguous
  spans from the review to the horizon, each with the thought's stage (-1 none).
  Food is hungry (stage 0) below the row's observed `Threshold`
  (`PercentageThreshHungry`), urgently hungry (1) below half of it and starving
  (2) at 0. Rest is tired (0) below its `Threshold` (`ThreshTired`), very
  tired (1) below 0.14 and exhausted (2) below 0.01. The first span's stage is the
  observed one.
- `ForecastMood.NeedThoughts` turns them into banded rows covering the horizon:
  each span adds its stage's mood less the observed stage's, which
  `NeedMood.UnclampedTarget` already holds. The stage moods are the defs' `baseMoodEffect`
  ([kept constants](kept-constants.md#mirrored-game-literals-need-thoughts-3598)).
  `ForecastNeedsTimeline` and the coupled evaluation add them to the caller's
  banded thoughts, so `BreakRisk`, `BreakTierTicks` and `PressureTicks` carry
  them and the ledger prices them as `MoodBreaks` and `Wellbeing`.
- Not modelled: a nullified thought (trait, hediff, precept or gene), and the
  malnutrition stages past the first while starving (the thought stays at
  stage 2, -20). A need with an unknown timeline has no spans and its thought
  stays at the observed constant.

### Repeated and stacked memories (#3598)

`NeedMemory.StackLimit` is the def's native `stackLimit` (0 when not read).

- A planned memory (a completed meal's thought or a `MoodMemories` row) of a
  stackLimit-1 def renews the memory of that def live before it, as
  `Thought_Memory.TryMergeWithExistingMemory` does: the observed or earlier planned
  memory stops at the new one's start. Any other stack limit sharing a def leaves the
  pawn's mood unassessed with `unsupported_memory_renewal_or_stacking` (and the
  horizon thought offset with `unsupported_meal_memory_overlap`).
- Observed memories sharing a def (a stack, or social memories about different
  pawns) each expire on their own timer and are summed. Native values a stack as its
  mean offset times the stacked multipliers (`ThoughtHandler.MoodOffsetOfGroup`), so
  a stack member's expiry is overstated; `stacked_memory_expiry_additive` names it.
  Zero-offset duplicates do not raise it.
- A produced or delivered meal (`CoupledFoodMethodCandidates.AddEating`) carries
  `CoupledFoodUse.Thoughts`: native's `FoodUtility.ThoughtsFromIngesting` on one
  unspawned fresh unit of the resource, kept per def. A resource with ingredients
  (a cooked meal) leaves them unknown, and a plan eating it leaves the pawn's mood
  unassessed with `future_meal_thoughts_unassessed`, as before.

### Temperature mood from banded cards

Temperature mood is priced from the Live stage-band cards whose input reads the
ambient temperature (`EnvironmentCold`, `EnvironmentHot`; #3327). There are no
hand-written temperature-mood constants, and mood is valued only as `BreakRisk`.

- `ForecastThermalExposure.Spans` is the exposure integrator's per-span output:
  start, end, initial and final temperature, and the comfort range with the
  pawn's comfort shifts applied. The degree-ticks are integrated from the same
  spans. A stationary allocation at a read cell is located; every other tick
  (idle, transit, an unread cell) takes the last located temperature, else the
  read's `Outdoors` (#3673), and stays in `UnknownTicks` only with neither known.
- `MechanicCardGuards.CardStageOffset` prices a stage card at one moment from the
  comfort range, the ambient temperature, Ideology's state and the pawn's ideo
  precepts. A nullifier gives 0, as for count cards. An unknown fact the
  evaluation needs gives unknown, never 0: for example the precepts while
  Ideology is active, or whether Ideology is active when the precepts are
  unknown.
- Along each assessed span, the card is cut where its input crosses a literal it
  compares against and evaluated on each piece. Its offset replaces the
  thought's observed contribution (`MoodPawn.Thoughts`) as a delta inside the
  span. Outside assessed spans the observed constant stays, and
  `situational_thought_constant_outside_exposure:<thought>` names it. Unknown
  card facts, observed thoughts or offsets leave the whole thought constant.
- A thought changed both by a card and by a `SituationalChanges` row is
  `unsupported_situational_change_stacking`. Hypothermia and heatstroke are
  priced by the capability-days thermal term (#3521), not here; the exposure degree-ticks consequence is unchanged.

### Roof, room and deterioration reads

The needs read carries the facts the roof and room consequences use (#3339; the
model is in [ColonyForecast](../architecture/colony-forecast.md#roof-and-room-consequences)).
Nothing is capped. Every member of the needs read is required and plain (#3781): a
number the game gives as not finite, or a colonist without the tracker a member
reads (health, mood breaker, immunity), refuses the read rather than sending an
unknown. What the game does not have is a case, never an unknown:

- `NeedsForecastPawn.Mood` (`NeedMood`) is null for a pawn without a mood need; it
  carries the level, target, unclamped target, rise and fall rates a tick, whether
  sleep freezes it, the minor break threshold, the memories and `Conditional`:
  `Modeled` with the conditional thoughts the forecast models (the NightOwl
  worker), or `Unmodeled` naming the defs it cannot (another worker, a def it
  cannot price), which leaves the mood timeline unknown with one
  `unknown_conditional_mood_mechanics:<def>` finding per named def.
- `NeedMemory.Life` is `Permanent` or `Expires` with the ticks left;
  `NeedThoughtStage.Lasting` is a `Memory` (duration, whether it fades, stack
  limit) or `Situational`.
- `NeedTarget.Recovery` is `Ingest` (service ticks, gain a unit: a meal) or `Rate`
  (gain a tick: a bed, a recreation building); `TravelTicks` is the route cost.
- `NeedsForecastPawn.Outdoors` is `NeedLevel.Has` or `Lacks` (no outdoors need).
- `LocalHour` is the pawn's local hour; `SleepBlocked` is a pawn that cannot fall
  asleep voluntarily or has a hediff that blocks sleep, `SleepBlockedUntilTicks`
  the disturbance wait, 0 when none.

`NeedTarget.StockCount`, `MaxPawns` and `FreshTicks` are still facts (#3783).

- `NeedsForecastRead`: the current weather's `WeatherThought` and its stage count,
  `RainRate`, `MapArea`, and `Deterioration`, the DeteriorationRate stat's
  `StatPart_EnvironmentalEffects` def data (unroofed and outdoors offsets, edifice
  protection factor).
- `NeedsForecastPawn`: `Outdoors` and `OutdoorsDisabled` (Need\_Outdoors),
  `OwnedBed` and `Ascetic` (the two pawn facts `ApplyBedThoughts` reads), and
  `ThoughtStages`, one `NeedThoughtStage` per thought stage the pawn can get, priced
  by the game's `MoodOffset` for this pawn, with its duration. `Role` names why the
  row was read: `slept_outside`, `slept_in_bedroom`, `slept_in_barracks`, `weather`,
  `need_outdoors`, `undergrounder_indoors`, `undergrounder_underground`,
  `undergrounder_outdoors`, and `ascetic` (#3655): the thought `ThoughtWorker_Ascetic`
  gives in a room the pawn owns alone, staged by the room's Impressiveness, read
  from every def with that worker. Its null stages, where the worker leaves the
  thought inactive, are rows at 0. A stage the game cannot price is left out. The
  title room thoughts (#3807) and the trait bedroom thoughts a suite relieves (#3826):
  `greedy` (`ThoughtWorker_Greedy`, staged by the owned room's Impressiveness stage
  + 1, 0 without one; null stages at 0) and `jealous` (`ThoughtWorker_BedroomJealous`).
- `NeedsForecastPawn.Sleep` (#3840, `NeedSleep`): where the pawn sleeps now, as
  `JobGiver_GetRest` picks it: the bed `RestUtility.FindBedFor` gives (its load id),
  else the ground at the pawn's cell (""); whether that cell's room is
  PsychologicallyOutdoors (`slept_outside`), whether it is the ground or a costless
  bed (`slept_on_ground`, `CostListAdjusted` empty), the rest gained a tick there
  (`Need_Rest.BaseRestGainPerTick` × `RestEffectiveness` × RestRateMultiplier), the
  bed's Comfort (0 on the ground) and the cell's temperature. Null without a rest
  need, for a wild man or unspawned. `SleepComfortableMin/Max` are the pawn's
  ComfyTemperatureMin/Max as `ApplyBedThoughts` compares them (`slept_in_cold`,
  `slept_in_heat`); the rest `NeedRate.GainPerTickPerEffectiveness` is the gain a tick
  of a bed of rest effectiveness 1 (0 for other needs). `ThoughtStages` roles
  `slept_on_ground`, `slept_in_cold`, `slept_in_heat` and `need_comfort`
  (`ThoughtWorker_NeedComfort`, only for a pawn with the comfort need) price them.
- `NeedsForecastPawn.RivalRooms`: the Impressiveness of each room another spawned
  player-faction pawn without a host faction owns (`OwnedRoom`), each once, ascending:
  what `ThoughtWorker_BedroomJealous` compares the pawn's own room against (#3826).
- `NeedTarget.Cell`: the target cell's roof (roofed, thick, natural) and its room's
  UsesOutdoorTemperature and PsychologicallyOutdoors (no room reads as outdoors).
- `NeedTarget.Bed`: a rest target's room role, impressiveness stage, ForPrisoners and
  whether it is slept on the ground (empty cost list).
- `NeedTarget.Deterioration`: a food stack's hit points, its
  `SteadyEnvironmentEffects.FinalDeteriorationRate` now (every DeteriorationRate part
  applied), whether the environmental part applies, the terrain's
  extraDeteriorationFactor and edifice protection.

The brain mirrors the game literals these mechanics use; each constant cites its
1.7 member ([kept constants](kept-constants.md#mirrored-game-literals-3339)).

### Capacity penalty tables and per-pawn capacities

The capability-days ledger ([forecast value](../architecture/forecast-value.md#capability-days),
`ForecastValue.Of`) prices what malnutrition, food poisoning, hypothermia and heatstroke take from a pawn
with the game's own tables (#3519, #3520, #3521). Hypothermia and heatstroke are priced from the
pawn's `ForecastThermalExposure.Spans`: exact piecewise integration of the givers' severity (quadratic gain under a linear temperature, exponential recovery), the stage path priced as the other terms are, and a severity reaching `LethalSeverity` charging the remaining horizon plus `DeathTerminalValue`. A pawn with unknown occupancy has the term unassessed, with `unassessed_thermal_exposure`. Exhaustion collapse (#3598) reads the rest zero spans of the pawn's timeline and its observed `WakeThreshold` and rest `NeedRate.UnservedGainPerTick` (the ground sleep a collapse takes).

- `RoundsFacts.CapacityPenalties` (`CapacityPenaltyTables`): one
  `HediffPenaltyTable` each for `Malnutrition` and `FoodPoisoning`, decoded by
  `Catalog/CapacityPenalties.cs` from the `HediffDef` rows, and
  `MovingMinForCapable` (the Moving `PawnCapacityDef`'s `minForCapable`). A table
  has its stages ordered by `minSeverity`, each with the capacity modifiers
  (`Offset`, `SetMax` or null, `PostFactor`) and `PainOffset`, plus
  `LethalSeverity` (null when the def has none), `InitialSeverity`, and the def's
  `SeverityPerDay` and `Disappears` comps (null without them). A hediff the
  catalog lacks is an unknown table (`Known` false), never an empty one;
  `Valid()` is true for a known, ordered, finite table. Modifiers on a capacity
  outside the five a pawn-day is counted in (food poisoning's Eating) are left
  out.
- `RoundsFacts.MoodBreak` (`MoodBreakModel`): the catalog's break curve, whose
  tier mixes give the mean days a break of each tier lasts.
- `NeedsForecastPawn`: `Consciousness`, `Moving`, `Manipulation`, `Sight` and
  `Talking` (`health.capacities.GetLevel`), and `MalnutritionSeverity`, `HypothermiaSeverity`, `HeatstrokeSeverity` and
  `FoodPoisoningSeverity` (0 when absent).
  Each capacity ratio of the ledger is the level a stage leaves against the
  observed one.
- The ledger reads them through `ForecastCoupledContext` (`CapacityPenalties`,
  `MoodBreaks`, `Value`); `ForecastNeedMetric.ZeroSpans` gives the spans a need
  is zero (food: starving) and `ForecastMoodResult.BreakTierTicks` the ticks in
  each break tier. `coupled_selection` reports the ledger of the baseline and the
  selection (`value`); nothing ranks on it yet.

The severity rates that are method-body literals, and which need categories carry
an effect, are mirrored in
[kept constants](kept-constants.md#mirrored-game-literals-capacity-penalties-3519).

### Raid health consequences (#3544)

The harm model's fifth step ([harm model](../architecture/forecast-value.md#harm-model)).

- `NeedsForecastPawn.PainShockThreshold`, `HealthScale`, `IncomingDamageFactor` and `InjuryHealingFactor` (`StatDefOf` stats and
  `pawn.HealthScale`); a colonist without a health tracker refuses the needs read.
- `CapacityPenaltyTables.BloodLoss` and `WoundInfection` are decoded like the other tables; `HediffPenaltyTable.Immunizable`
  (`HediffCompProperties_Immunizable`) and `SeverityPerDayTended` (`HediffCompProperties_TendDuration`) carry the infection's course.
  They are not part of `CapacityPenaltyTables.Valid()`; an unknown one leaves its part unassessed.
- `HarmConsequences.Of(hits, bins, response, pawn, tables, support, policy, horizon)` is pure. It returns a `HarmDistribution`:
  `Points` (capability-days lost, probability) summing to 1, `DownProbability`, `DeathProbability`, `PatientDays` (the tended wounds'
  expected days from first care to healed, 0 untended; #3804), `Findings`. Deaths carry the remaining
  horizon plus `DeathTerminalValue`. Tended means the support serves a care (`CareServices`) with an observed `TendQuality` (quality and
  room factor are the mean over services); first care is `FirstCareTicks`; otherwise the pawn is untended (`harm_untended`). An internal
  overload takes the care as a plain `HarmConsequences.Care` (the hospital credit's, #3804), and `HarmConsequences.Wounds` prices a
  surgery failure's damage dealt at one wound severity the same way.
  A health scale that is not positive or an unobserved weapon mix makes the distribution unknown
  (`capacities_unobserved:<pawn>`, `weapon_mix_unobserved`). Unassessed parts are findings: `adaptation_feedback_unassessed`,
  `rescue_labor_unassessed`, `injury_part_effects_unassessed`, `bleed_part_factors_unobserved`,
  `capacity_penalty_table_unknown:BloodLoss` or `:WoundInfection`, and for infection `infection_immunity_gain_speed_unobserved`,
  `infection_difficulty_factor_unobserved`, `infection_onset_delay_unmodelled`, `infection_room_factor_unobserved`.
  Mirrored rules: [kept constants](kept-constants.md#mirrored-game-rules-raid-health-consequences-3544).

### Harm term and death floor (#3546)

The ledger's `Harm` term and the `Death` floor read `ForecastCoupledContext.Harm` (`HarmInputs`, assembled per review by
`HarmInputs.Of(RoundsFacts)`) and `Outfits`:

- `HarmInputs`: `Schedule` (`ThreatSchedule.Of` at the observed storyteller, days passed and raid points), `Composition`
  (`RaidCompositionRules.Of` at the observed `RaidPoints` and `HostileFactions`, over `RoundsFacts.HarmCatalog.Raid`), `RaidPoints`,
  `DefenseCapacity`, `Exposure` (the embedded `exposure.json` unless injected), `Coverage` and `Natural` (`HarmCatalog`, from the
  catalog), and `Illness` (`IllnessSchedule.Of` over `RoundsFacts.DiseaseTable` and `RoundsFacts.Disease`; see
  [illness term](#illness-term-3824)), and `Containment` (`ContainmentPlanning.Forecast`; see [escape term](#escape-term-3811)).
  Each defaults to unknown, `Containment` to no entity. The hediff tables are `CapacityPenalties`.
- `Harm` = raids per day (major threats per day × raid fraction) × horizon days × Σ over the outcome's pawns of
  (1 − `HarmRiskWeight`) × E + `HarmRiskWeight` × CVaR`HarmRiskAlpha` of `HarmConsequences.Of` for the pawn. Its inputs are
  `RaidExposure.HitsPerColonist(points ÷ DefenseCapacity, role)` and `ArmorResponseRules.OfOutfit` of the pawn's outfit. The role
  comes from the worn primary weapon (ranged: `gunner`, melee: `melee`, none: `noncombatant`). The pawn's outfits are the worn one
  from the start, then each `OutfitChanges` row for it from its effective tick in tick order (`HarmLedger.OutfitsOf`); each is
  priced for its share of the horizon, the raids of that share meeting it, so a crafted garment's armor counts only from its
  delivery. A change whose tick is unknown leaves the term unknown with `outfit_change_timing_unknown:<pawn>`. The worn primary is a `GearSlot.Primary` option in `GearLoadoutInput.Worn`
  that `ColonyGearReads.GearLoadoutModelFacts` builds from the gear census's `PawnEquipment` (`PrimaryId` and the equipped item
  resolved in the frame's things table), with `Ranged` from the catalog's weapon rows (#3562). A schedule that expects no raids is a known 0. The distribution of a pawn
  is memoized per context by (pawn, role, outfit, care facts, hits).
- Unknown, with the specific finding and no `harm_capability_unassessed`, when an input is: `threat_schedule_unknown` (with the
  schedule's own finding), `raid_composition_unknown` (with the composition's), `raid_points_unobserved`,
  `defense_capacity_unobserved`, `exposure_cell_uncalibrated:<bin>/<role>`, `outfit_unobserved:<pawn>`,
  `capacities_unobserved:<pawn>`, and `HarmConsequences`' own. A known term adds the illness term's loss and still reports
  `other_threats_unassessed` (the threats other than raids and disease),
  `adaptation_feedback_unassessed`, `fire_unassessed` and `exposure_layout_unassessed`. An unknown `Harm` makes `Total` unknown;
  a comparison still uses the terms both sides assess (`LossAgainst`).
- `ForecastValueExplanation.Death` is assessed when the outcome's and the reference's `Harm` are: its excess is the expected deaths per
  day (`HarmDetail.DeathsPerDay`: raids per day × Σ death probability, plus the illness and escape terms' deaths per day) above the reference's, minus `DeathRiskFloor`, floored at 0. The reference wears the
  worn outfits. `CompareConsequences` ranks the floors urgent zero, death, break risk, in that order, before the ledger.
- The search evaluates an outcome in that order (#3723): `ForecastValue.Stage` prices the urgent-zero time, the harm term and death
  excess, the break-risk excess and the goal terms at evaluation, and the remaining ledger terms on the first read of the
  explanation, which a comparison forces only when it reaches the ledger (`LossAgainst` over the terms both sides assess, with
  `TieResolution`) and a consumer forces when it needs the complete vector. The reference's harm (`ForecastValue.ReferenceHarm`)
  is priced once per search and shared by every outcome's death floor. A missing term or an unknown floor is never a failed floor
  or a zero loss, and a comparison that needs a term evaluates it; a skipped term never shrinks the assessed set. Rejected-subset
  evidence (`ForecastPortfolioEvidence.Reason`) is `infeasible_or_unassessed`, `construction_without_supported_service_relief:<ids>`
  or `lost_to_leader:<stage>`; `StageOf` reads the stage (`urgent_zero`, `death_risk_floor`, `break_risk_cap`, `ledger`, an
  unconverted term, `incumbent`, `labor`), and a subset decided at a floor has no `Consequences` (`Assessed` is still true).
- The search skips a subset without evaluating it only on a proven bound (#3724): the urgent-zero unserved-pawn bound
  (`ForecastCoupledSelection.UrgentZeroUnservedBound`), sound because a pawn none of whose need tasks lies in the subset's task
  closure keeps its baseline `ZeroTicks` in every plan (`ForecastNeeds.NeedsCoreOf` reads the pawn's own allocated need choices
  only) and `CompareConsequences` decides at the urgent-zero stage whenever the excesses differ by `UrgentZeroResolutionTicks`.
  The evidence reason is `pruned:urgent_zero_unserved:<pawn>` for a subset and `pruned_branch:urgent_zero_unserved:<pawn>` for a
  cardinality-sweep prefix with every completion; `BoundOf` reads the bound, `Consequences` is null and `Assessed` true. Where
  the bound is unavailable (a rest need task in the closure, an unknown baseline zero time, an infeasible or unassessed leader,
  goal values in the context, the relief gate's collection order) the subset is evaluated, `ForecastSearchPhase.Unbounded`
  counts it and `ForecastCoupledSelection.BoundsUnavailable` names the reason as `urgent_zero_unserved:<reason>`;
  `ForecastSearchPhase.Pruned` counts the skipped subsets. A partial portfolio's own value is never a bound on its completions,
  and no bound exists for the death and break-risk floors, the ledger or the tiebreaks. The unpruned search is the test control
  `CompareActivityOutcomesUnpruned`; it ranks the same selection, vectors and findings.
- `ForecastValueResult.HarmDetail` (`raids_per_day`, `exposure_range`, `down_probability`, `death_probability`, `cvar`, `raid_loss`,
  `illness`, `escape`) is reported as `harm_detail` ([otel-signals](otel-signals.md)). Until the exposure table is calibrated (#3543) every cell is uncalibrated, so
  a served review reports `exposure_cell_uncalibrated:…` and `harm` null.

### Illness term (#3824)

The harm term's disease part: how many `DiseaseHuman` incidents a day the storyteller sends, which disease each is, and what its course
costs each pawn.

- Native reads, read-only (`NativeDiseaseFacts`; never `IncidentWorker.CanFireNow` or `BiomeDef.CommonalityOfDisease`):
  `ThreatFacts.Disease` (`DiseaseFacts`: storyteller and the map tile's primary biome by def name, `DebugSettings.enableRandomDiseases`,
  the difficulty's `diseaseIntervalFactor`, `GenDate.DaysPassedSinceSettleFloat`, the victim candidates (`FreeColonistsAndPrisoners`
  outside a biosculpter pod), the free colonists the `minPopulation` gate counts, and per `DiseaseHuman` incident its potential victims
  (`IncidentWorker_Disease.PotentialVictims`) and `Blocked` (`scenario`, `difficulty`, `planet_layer`)), and per needs pawn
  `NeedsForecastPawn.Disease` (`PawnDiseaseFacts`: `ImmunityGainSpeed` and, per incident, `DiseaseContractChanceFactor` of its hediff,
  0 for a pawn that is no potential victim; its BloodPumping, Breathing and BloodFiltration levels; per body part of a def some incident affects
  its `DiseasePartRead` (`coverageAbs`, `CalculatePartEfficiency` and the incidents `HediffGiverUtility.TryApply` could still pick it
  for: not missing, no added part on it or an ancestor, the hediff not already on it); and per `DestroyOrgan` part def each install
  recipe the autopilot could queue (`AvailableNow`, an install-part worker, `AvailableOnNow`) as an `OrganReplacementRead`: its part and
  medicine on the map, eligible doctors, the best-bed and ideal success chances, the violation and lethal flags, and the surgery
  outcome model #3804 reads for a queued bill (#3830)). The colony decode carries `ThreatFacts.Disease` to `RoundsFacts.Disease`.
- `RoundsFacts.DiseaseTable` (`DiseaseTable`, `Catalog/Disease.cs`): per storyteller its home-map `StorytellerCompProperties_Disease`
  comps of `DiseaseHuman` (their `minDaysPassed`; any other comp that can send the category is unmodelled), per biome its
  `diseaseMtbDays` and commonalities (its own records, then the incidents' `diseaseBiomeRecords`), and per incident its victim rules,
  gates and its hediff's course (`DiseaseCourse`: the penalty table, severity clamp, `SeverityPerDay` range, not-immune random factor,
  `TendDuration` timing and cure, `DestroyOrgan`, and per Human body part of its defs a `DiseasePart`: threatened by `onlyLifeThreateningTo`,
  its organ tag's capacity (a part whose single tag feeds Breathing, BloodPumping or BloodFiltration and no unaffected part carries;
  otherwise none, named `disease_part_capacity_unmodelled:<hediff>:<part>` when the part is destroyed or loses efficiency), whether
  that capacity is `lethalFlesh` and its `minForCapable`, and the install recipes for it by tier). A hediff comp, stage
  death MTB, hediff giver or reversing severity the course does not model makes it unmodelled.
- `IllnessSchedule.Of(table, facts)`: each comp's rate 1 ÷ (biome MTB × interval factor) a day from its `minDaysPassed` (none with
  random diseases off or the factor at the 100 cutoff); the usable incidents (home map, `earliestDay` on whole days since settling,
  biome lists, `minPopulation`, not `Blocked`, a potential victim, commonality > 0), each with its share of the commonality and
  `VictimChance` (the drawn count uniform on `RoundToInt` of candidates × the fraction range, clamped to [1, `diseaseMaxVictims`],
  over the potential victims). Unassessed with `disease_facts_unobserved`, `disease_storyteller_unknown:<def>`,
  `disease_biome_unknown:<def>`, `disease_rate_invalid`, `disease_incident_unknown:<def>`,
  `disease_victims_unobserved:<def>`, `disease_course_unmodelled:<hediff>:<what>` or `disease_course_unknown:<def>`. A storyteller
  comp the decoder does not model (a mod's comp class, or a vanilla comp that draws `DiseaseHuman` other than by the disease comp)
  sends no incidents and is named on the assessed schedule, `disease_comp_unmodelled:<comps>` (#3859); the harm term carries it.
- `IllnessForecast.Of(schedule, pawn, …)`: a `HarmDistribution` of one incident for the pawn, each disease weighted by share ×
  victim chance × contract chance and the rest a point at 0. Immunity gains at the pawn's `ImmunityGainSpeed` read without its bed
  (native divides `StatPart_BedStat`'s current-bed `ImmunityGainSpeedFactor` out) times the factor of the bed it is cared in: the
  harm term's is its owned bed's (`NeedsForecastPawn.OwnedBedRoom`, 1 without one), the hospital credit's the planned hospital
  bed's (#3804). The course walks severity from its initial value under the
  Immunizable, SeverityPerDay and tended rates (tended from the support's first care at its tend quality, retended as the
  TendDuration comp allows), ending at 0, the Disappears time, the cure tend quality, the lethal severity on a threatened part, or the
  max severity with `DestroyOrgan`. The struck part is drawn by coverage among the pawn's open parts (#3830); a stage's
  `partEfficiencyOffset` and a destroyed organ cut its capacity (the mean of the tag's parts) and through it Consciousness, Moving,
  Manipulation and Talking by the PawnCapacityWorker weights, and a lethal capacity at or under `minForCapable` kills. A destroyed
  organ whose best-tier recipe is stocked and passes `SurgeryRules` at the restore failure cap is a booked replacement
  (`HarmDistribution.Replacements`, no further loss; the hospital's surgeries a day = incidents a day × its probability); otherwise
  it is a named `SurgeryWantReason` (`PartShort`, `NoDoctor`, `BedShort`) and the loss lasts to the horizon. Uniform draws are a 4-point midpoint grid; the death chance in the immunity factor is exact.
  Deaths cost the horizon's rest plus `DeathTerminalValue`; survivors their capability-days. `PatientDays` (days from first care to
  the end) and `Treatments` (tends) are its expectations. Unknown with `capacities_unobserved:<pawn>` (a pawn without a needs row),
  `capacity_penalty_table_unknown:MovingMinForCapable` or `disease_contract_unobserved:<pawn>:<def>`. Findings of what is left
  out: `illness_untended`, `illness_prior_immunity_unassessed` (an earlier immunity record),
  `disease_severity_factors_unpriced:<hediff>`, `disease_part_stage_death_unmodelled:<hediff>` (a stage offset that kills is priced
  downed), `illness_organ_replacement_wait_unmodelled` (the days to the replacement surgery). A pawn without part rows for a
  part-striking incident throws `RequiredFactException` (`disease_parts:<pawn>:<def>`).
- `HarmDetail.Illness` (`IllnessTerm`: incidents a day, its loss — incidents a day × horizon days × Σ over pawns of the risk transform
  — and per pawn `IllnessPawn` with `PatientDaysPerDay`, `TendsPerDay`, `DeathsPerDay`) is null when the disease table does not
  model the colony (its storyteller, biome, incident or course); the term's loss is then `HarmDetail.RaidLoss` alone with
  `illness_unmodelled` and the reason. An unread schedule fact (`disease_facts_unobserved`, `disease_rate_invalid`,
  `disease_victims_unobserved:<incident>`) or pawn fact throws `RequiredFactException` (#3835); a pawn with no immunity tracker
  catches nothing (a point at 0). `ColonyValue` prices the doctor and the medicine by the
  whole term (raid plus illness loss at each tend quality, raid treatments plus `TendsPerDay`, #3829) and throws a
  `RequiredFactException` when the term holds no illness. Mirrored literals:
  [kept constants](kept-constants.md#mirrored-game-literals-illness-3824).

### Escape term (#3811)

The harm term's held-entity part: how often an entity held on a holding platform escapes, and what the fight costs.

- Native reads, read-only (`NativeAnomalyFacts`), inside the pawn's held block (a failed read drops the block with a `held`
  `ReadIssue`): `HeldState.Escape` (`EntityEscapeState`: the comp's `baseEscapeIntervalMtbDays`, 1 / Clamp(Moving, 0.01, 1),
  `entityTicksInCaptivity` in days, `ContainmentUtility.TryGetColdContainmentBonus`'s modifier or 1, the kind's `combatPower`, and
  `ContainmentUtility.CanParticipateInEscape`); and in the study block `StudyState.Yield` (`StudyYieldState`: `AnomalyKnowledge`, the
  `KnowledgeCategory` defName, `Props.frequencyTicks`). The catalog reads `ContainmentUtility`'s three private curves by name
  (`DefinitionCatalog.ContainmentCurves`: escape interval and study knowledge over the strength margin, escape factor over days held).
- `RoundsContainmentReads.ContainmentForecastOf` lifts the pawn rows into `ContainmentForecast.Entities` (`ContainedEntity`): each
  living entity held on a platform, with the building row whose holder holds it (its cell and `ContainmentStrength`), and each one the
  game lets the colony capture that no platform holds. `Unread` names the pawn when its held block failed or a fact it needs is absent;
  either reader then raises `RequiredFactException("containment entities: …")`, as unread curves raise `containment curves`.
- Escapes a day per held entity: 1 / (base MTB × moving × captivity curve(days) × escape curve(platform strength − need) × cold), 0 for
  one that cannot escape (`ForecastContainment.EscapesPerDay`). Each escape is one fight at the entity's combat power:
  `HarmLedger.FightLoss` prices it as a raid of those points against `DefenseCapacity`, with the raid's weapon bins and the exposure
  table, Σ over the pawns of the risk transform (`containment_escape_raid_calibration_assumed`). No defender prices the fight at 0;
  a fight fact left unread (the defense ratio, weapon bins, a defender's outfit or capacities, or an exposure cell the table does
  not calibrate) throws `RequiredFactException` with its findings (#3835).
- `HarmDetail.Escape` (`EscapeTerm`: escapes a day, the loss — escapes a day × horizon days × one fight — and deaths a day) is zero
  without a held entity; its loss and deaths join the term's.

### Threat schedule (#3539)

The harm model's first step ([harm model](../architecture/forecast-value.md#harm-model)) reads the storyteller the game is running.

- `ThreatFacts.Storyteller` (`Find.Storyteller.def.defName`), `DaysPassed` (`GenDate.DaysPassedFloat`) are read beside `RaidPoints` and decoded onto `RoundsFacts.Storyteller` and `DaysPassed`; the section is required, and a threat read the native cannot complete fails the census. Raid points stay the observed `RaidPoints`
  (adaptation and difficulty are inside it); the schedule does not model their growth.
- `RoundsFacts.ThreatTable` (`ThreatScheduleTable`): per `StorytellerDef`, its home-map threat comps decoded by comp class in
  `Catalog/Storyteller.cs` (#3859): on-off cycles of `ThreatBig` with their accept curves by days passed and threat points, and MTB
  comps (random mains with their `ThreatBig` weight share, `ThreatBig` category MTBs with their days-passed factor, single MTBs of a
  `ThreatBig` incident), and the comps it does not model. Empty without a catalog.
- `ThreatSchedule.Of(table, storyteller, daysPassed, threatPoints)`: `MajorThreatsPerDay`, the sum of the modelled comps' expected
  incidents a day at that day (a threat-points curve at the observed `RaidPoints`), and `RaidFraction` (1 before day 20, 0.5 after).
  A comp it does not model (a mod's comp class, an on-off cycle scaled by progress score or swapping its days without a
  tree-connection meme, a home-map `ThreatsGenerator` or `ThreatBig` `CategoryIndividualMTBByBiome`) adds no threats and is named,
  `threat_comp_unmodelled:<comps>`, on the assessed schedule; the harm term carries the finding. The schedule is not `Assessed`, with
  a finding (`storyteller_unobserved`, `days_passed_unobserved`, `storyteller_unknown:<def>`, `raid_points_unobserved`,
  `threat_comp_invalid:<shape>`), when a fact it reads is unread; the hospital credit then throws `RequiredFactException`. Non-raid
  threats are not modelled. The mirrored literals are in [kept constants](kept-constants.md#mirrored-game-literals-threat-schedule-3539).

## Coherent coupled observation frame

The routine forecast read includes `CoupledForecastRead` alongside needs in one
main-thread observation frame. It carries bounded harvest/delivery, recipe,
construction and clothing options; physical routes and food lots; product counts,
ingestion eligibility/rates and freshness; and thermal cell/source facts. It
reports its actual native observation tick. Decoding does not relabel an older
census with the caller's requested tick. Policy requires agreement with needs,
shared work-resource evidence and demand before constructing a coupled request.

`HuntActivityRead` is the hunting part of that frame (#3130, #3533): every free colonist
who could hunt and every unfogged wild edible animal (kind, cell, age, xenotype, weapon by def, stuff and quality and its range,
apparel, skills, traits, the hediff set by definition and part, missing parts; for prey also downed/mental, herd,
meat and its nutrition if killed, and the corpse store), each with the game's own combat inputs: a hunter's
`HuntGunRead` (ShotReport's accuracy and range-band factors, the weapon's hit chance factor at the band edges, warmup
with its aiming delay, cooldown, burst, the projectile's damage, armour penetration, sharpness and pain per damage),
every pawn's `HuntBodyRead` (body size, health scale, pain shock threshold, current pain and injury, overall sharp and
blunt armour, ticks per cell, dodge), and a prey's `HuntMeleeRead` (MeleeDPS's selection-weighted average attack and
its hit chance) and per-hunter `HuntRevengeRead` (`PawnUtility.GetManhunterOnDamageChance` and the pack's join
chance); plus the weather's accuracy multiplier. It holds no roster, target or estimate; policy proposes rosters and
estimates them.

`ResearchActivityRead` is the research part of that frame (#3527): for the current project (`ResearchManager.GetProject()`;
none reads no workers and the finding `research_no_current_project`), every free colonist who has a job on
every bench the project can be researched at (`WorkGiver_Researcher.HasJobOnThing`, reservable and reachable), with the
points the game adds per work tick (`ResearchSpeed` x the bench's `ResearchSpeedFactor` x the difficulty's research speed,
as `ResearchManager.ResearchPerformed` applies them). The bench's interaction cell joins the coupled route locations, so the
walk is priced by the shared leg cache. `ResearchMethodCandidates` (policy, behind the `economy/research` Need's enablement)
makes one one-hour block per pawn and bench: a Work step through the research WorkGiver with Job `Research`, one root per
pawn (`research/<pawn>`), the bench as exclusive equipment, and `ForecastActivity.ProjectProgress` (project, points per tick)
for the investment credit (#3531: the tier-2 marginal value per research point, `ForecastCoupledContext.Investment`, times the block's
points, written to the ledger's `Investment` term). It values nothing itself and never picks the project. Sowing tasks (#3528) carry the
same metadata in cells per tick, keyed `sow:<zone>`.

`HuntAlternatives` (policy, pure) is the hunt comparison. `Propose` makes bounded rosters from that read: solos,
pairs and a third gunner from the six best-shooting available hunters, for each of up to four nearby prey groups
(herd-mates within 12 cells are one group, a search aid only); no roster size is required. Each roster is also
proposed with a retriever (#3513): a hauler, an available colonist outside the gunner pool whose hauling work is
active (`HuntActorRead.Hauls`), the two fastest by their walk to the prey and hauls to the corpse store. A hauler's
roster shares its gunners' encounter (`HuntRoster.SceneKey`), so it shares their estimate. Once the plain rosters
are estimated, a second stage (`ProposeRetrieverStage`) restates the three best usable rosters of two or more gunners
per prey group with their least-skilled shooter as a gunner-retriever who stops shooting at the plain roster's
estimated encounter ticks (`HuntRoster.DepartureTick`). Each encounter is estimated by `HuntEstimator` and the
estimate is a `HuntEstimate`: whether risk was estimated at all, the chance a gunner suffers serious harm and that
one dies, the chance every prey ends downed within the 2.4-hour horizon, the nutrition net of that chance, and the
expected encounter ticks. `Choose` ranks usable rosters by the death chance, then the harm chance, then nutrition
still missing, then total participant labor.
Labor is every gunner's walk to the prey and its encounter ticks (the gunner-retriever's ending at its departure),
plus retrieval from the coupled routes: each corpse's haul to the store (`HuntActorRead.CorpseStore`, native's own
store choice: the nearest cell of the highest-priority store accepting the corpse) by the retriever, or without one by
the cheapest gunner, and the empty walk home of every gunner that hauled nothing; a hauler's walk out counts as haul.
Retrieval enters the labor term only, never the ordering above it. A roster the estimator refuses, or with no known
walk, is refused with its reason (`route_unknown`); a missing haul or return row, including no store that accepts the
corpse, refuses it `return_route_unknown`. Nothing is priced at zero. With no nutrition needed, or no usable roster,
the choice is not to hunt.

`HuntEstimator` (policy, pure and synchronous, #3533) is the analytic estimate from the game's formulas: no
simulation, no worker, no clock hold, so the food plan estimates every roster inside its review. Every input is a
read; a missing one refuses the roster (`prey_unobserved`, `hunter_unobserved`, `gunner_unarmed:<pawn>`,
`body_unobserved:<pawn>`, `revenge_unobserved:<prey>`), never a default. The prey's meat (a race without meat yields none) and
the weather's accuracy are always read.
The gunners take the prey one at a time, nearest first. Each walks into 95% of his range (the hunt job's cast
position) and fires casts of warmup, burst and cooldown at ShotReport's hit chance (the shooter's per-cell accuracy to
the distance times his range-band factor, floored at 0.0201 when the shot can go wild; the weapon's banded factor; the
weather unless the weapon ignores accuracy maluses; floored, then times the body size clamped to 0.5-2). A hit does
the projectile's damage after ApplyArmor's odds (with a = max(armour - penetration, 0), deflected below a/2, halved
below a). The prey goes down at the hits whose pooled damage reaches pain shock (injury pain is damage times pain per
damage over the health scale, from its current pain) or the lethal threshold (150 per health scale, less its current
injury), so hits are a Poisson process and downing is its tail; the gunners turn to the next prey at the mean tick the
last one went down. A gunner-retriever's shots stop at his departure tick. Prey that cannot fight back skip risk
estimation (`RiskEstimated` false, harm and death zero): no damaging melee attack with a hit chance, or a zero revenge
chance against every rostered gunner (a hare). A prey that can turns manhunter on each hit with that gunner's revenge
chance, at most once, while it stands; it closes on the nearest gunner at its own ticks per cell and attacks him until
it goes down, its herd-mates joining at the pack chance. Its landed hits (melee hit chance, the target's dodge, his
overall armour) are his harm, and reaching his lethal threshold before his pain shock is death; across prey, harm and
death combine as independent. Not modelled: cover and darkness, posture, fleeing prey, the hit chance rising as a
manhunter closes, vital-part and bleeding deaths, and a downed gunner leaving the shooting. The RealCombat simulator is
its offline check (`docs/developers/testing/hunting-prediction-qualification.md` records the comparison).

The food plan compares the alternatives (`HuntPlanningRules.PlanHunt`, from the coupled read's hunt observation and
routes) for the nutrition the colony still needs to reach its target runway, both stages in the one review. The chosen
roster becomes one formation channel (`HuntRosterChannel`) naming its gunners, retrievers and prey
(`SupplyCandidate.Hunters`, `Retrievers`, `Prey`), with the estimate's nutrition, labor (and its `hunt_return_ticks`
and `hunt_haul_ticks` parts) and harm chance as its risk, and `hunt_risk_estimated`, `hunt_harm_chance`,
`hunt_death_chance` and `hunt_downed_chance` as terms; every other animal stays a lone channel. The plan's open
formation channel files the prey and the selected gunners on the `military/combat` standing (`RoundsCombat.Prey`,
`RoundsCombat.Hunters`), and `HuntFormation` sends exactly those defenders, however many. A hauler is not sent:
`WorkRules.ReviewWorkResources` adds the open channels' retrievers (`HuntRequestRetrievers`) to the shared pawn claims,
so no other voluntary work takes them for the trip, and the corpse is hauled as native hauling work with the existing
delivery credit. For this the coupled read's routes hold, for each available hunter, a leg from each prey to its
corpse store and a leg back to the hunter's start.

An opened formation is held, not re-compared (`HuntAdmission`, pure policy in memory; a restart forgets it): the hunt
walks and drafts its gunners and startles its prey, and none of that may change the estimate it was chosen on. It is
released only on an explicit trigger (#3514), and the same review then compares again from the current read, so
nothing extends a hunt without a comparison. The triggers, in the order they are checked and as the `hunt_choice`
note's `released:<reason>` names them: an admitted animal gone from the hunt sources (`prey_gone`, the kill the
delivery credit observes) or downed (`prey_downed`); a rostered gunner at or past the hunger the clock holds for
(`gunner_hungry`, `HuntPlanningRules.Hungry` over the work census's hunger category: a drafted pawn does not eat, so
the hunt would starve him; the comparison that follows proposes no hungry colonist as gunner or hauler); with the hunt read known, an admitted animal out of the observed
window (`prey_unobserved`), a live animal of the group that joined within the group radius (`prey_joined`), a
rostered gunner absent from the read (`gunner_unobserved`), downed (`gunner_downed`) or in a mental state
(`gunner_mental`), or a gunner the read the roster was chosen from no longer describes (`gunner_changed:<pawn>:<what>`,
`HuntAdmission.StillDescribesGunner`: kind, xenotype, weapon and apparel by def, stuff and quality, traits, the hediff
set by definition and part, missing parts and the weather's accuracy (`weather`); not his position, draft state, skill
levels, current pain or injury, or the shot and body numbers derived from them, which the hunt itself moves). An
unknown hunt read decides nothing about the gunners. A drafted gunner is not a trigger: the hunt's own draft and the
player's read the same in `HuntActorRead.Available`. A released hunt whose prey still stands may be chosen again, by
the comparison only.

The formation is executed by the defense planner's hunt tactic (#3702). An open hunt fight arms no combat decision
event, so each review hold of the clock (`ClockRunConfig.ReviewEveryTicks`) re-runs the defense planner while one is
open (`ClockCombatStops.HuntReview`, the `hunt_review` clock_run reason), and the formation is read and ordered at
that cadence rather than the planner's own. `CombatHuntRules.HuntFormation` stages each gunner where the estimate
priced him: a gunner within his stage distance of the prey's centre (`StageDistance`: `EngageRangeFactor` of his weapon
range, less the group's spread and half a diagonal) shoots from where he stands, and one beyond it walks straight in
to it. A ranged role whose target left the prey or stands beyond his weapon range from his stage cell re-forms the hunt
(`PreyOutOfReach`), so the gunners follow a prey that walks off; an attack native refuses `cannot_hit` takes the
fight's line-of-fire move as in any fight. The prey stays in the combat read while the squad shoots it: the combat
census carries every hunt row, the ones the gunners' own jobs mark taken included. A roster pawn the formation gives
no role (released by the review, a stage cell refused unreachable, no longer able to hunt) is dropped from the fight's
roster and undrafted (`IdleHunters`); a formation with no role left, or a prey gone from the combat read, closes the
fight (`hunt_formation_empty`, `hunt_prey_gone`), so a hunt is never left drafted.

The execution evidence is the `food/hunt-roster` acceptance case (#3515): a lab colony of two riflemen short of food,
with a butcher table, its bill and a corpse stockpile, and a wild rhinoceros and boar inside the hunt read's window;
the crew is exactly the two staged riflemen, since the runner's default crew of three adds an unarmed third hunter to
the read. It runs locally and on remote CI like any case: the estimate needs no worker. Native reads no hunt route:
the former `HuntingSafety.RouteSafe` verdict (a route within a margin of any wild predator was unsafe, the prey
included, so a grizzly was never a hunt source and a wandering wolf held every hunt) went with #3515. It asserts in
order: the comparison made (the `rounds_review` row's `hunt_choice` names a chosen roster) and the drafted colonists
exactly the chosen roster's gunners (`RoundsCombat.Hunters`); a kill of an admitted prey in the delivery ledger; the
release afterwards (no standing hunters, `military/combat` cleared, nobody drafted); and the corpse butchered into
meat in stock. It records the roster, the chosen channel's `hunt_*` terms, the kill tick and the meat tally. Roster
size is the comparison's: a lone-gunner roster passes on the same evidence. The lab's quiet storyteller zeroes
revenge, so the case proves the hunt's execution, not revenge frequency (the qualification sweep does).
`food/ledger-hunt` keeps the delivery ledger's own kill-and-butcher claim on a lone native hunt; the former
`combatlab/hunt-squad` and `combatlab/hunt-door` cases went with #3399. The hunting sweep still needs the RealCombat
worker built and runs locally by name.

These are mechanics and current observations, not selected colony policy. Native
does not spawn hypothetical food to estimate a future meal. Definition-based
future ingestion facts do not claim ingredient-dependent memories or poisoning
outcomes. Physical thermal facts do not contain another copy of planned-room
intent: pure policy joins them to the current native room census and the existing
saved layout. Observed physical rooms remain roof-repair alternatives when strategic
layout moves future rooms elsewhere. Observed and planned identities are typed and
separate; matching footprints produce one candidate, while overlapping incompatible
footprints produce an explicit finding. Generation considers at most four distinct
rooms, taking observed heated rooms first, then other observed rooms (occupied or
bed-bearing first), then planned rooms in layout ID order. Omission is reported as
`shelter_geometry_truncated`; this bound does not establish that omitted rooms are
less valuable. Missing physical geometry remains unknown. A complete-roof `Enclosed`
flag is not required for roof repair: the existing per-cell shell, source and route
checks still determine whether the thermal estimate is supported. Nonrectangular
rooms and missing thermal observations retain explicit unsupported/unknown findings. Unsupported
reads retain findings and unknown estimates, including unreachable route pairs and
unpowered heaters.

The [coupled trajectory](../architecture/colony-forecast.md#coupled-food-needs-and-production-trajectory)
consumes this frame with shared resource ownership. It distinguishes loose stock,
reserved inputs, carried/delivered lots and future yields; accepted orders are
never observed production. Existing inventory-only diagnostics remain conditional
accounting. Coupled consumers must inspect each schedule's feasibility, timings
and uncertainty rather than treating a final balance as proof of execution.

### Planned construction sites

`ConstructionActivityRequest.Planned` asks the construction activity read about
hypothetical, unplaced sites. A `PlannedConstructionSite` is an id, a definition
(thing or floor), a stuff, a rotation and its `Positions`: one placement anchor per
piece (every cell of a wall run; the one anchor of a 2x1 shelf). Each comes back as
a `PlannedSiteAnswer` in `ConstructionActivityRead.Planned`.

Every `ConstructionActivityOption` row names its states (#3775): `SiteApproachTicks`
is the pawn's route to the site (a pawn with no route has no row); `Delivery` is
`Carried` (`ApproachTicks` the pawn's path to the stack, `DeliveryTicks` the loaded
carries plus returns), `Unsourced` (the row carries no stack), `CannotCarry` (the
pawn cannot haul it) or `Unreachable` (a carry leg has no path); `Finish` is
`Finishes` (`Ticks` at the pawn's speed, with the `SkillWork` it learns) or
`CannotFinish` (no giver, a guard, capability or skill refuses it, or its speed is
zero); `AvailableInput` is the stack's count (0 without one) and `Storage` is
`Accepted` (`Distance` to the nearest accepting storage, 0 when stored),
`Unaccepted` or `Unsourced`. A read that fails throws: there is no unknown read.

- A site native cannot resolve (definition or stuff unresolved, empty, out of
  bounds or over the position cap) comes back `Unresolved` with no rows and no
  clearing; the reason is a read finding (`planned_site_*`).
- `Clearing`: the clearing prerequisites, each an id, a `Kind` (`Cut`, `Haul`,
  `Mine`, `Deconstruct`) and per-pawn `ConstructionActivityOption` rows with `Site` set
  to the op id and `Definition` to the planned site's. One `Cut` op (`<site>/cut`)
  covers the site's plants, its `Targets` their ids lowest first (what the brain binds
  its Work/CutPlant set step over, #3613): the `Finish` ticks sum `harvestWork /
  JobDriver_PlantWork.WorkDonePerTick` (PlantWorkSpeed scaled by growth), for a
  pawn not incapable of PlantCutting (the cut giver's work type) and willing to cut
  every plant. One `Mine` op (`<site>/mine`) covers its natural rock, its `Targets`
  the rocks' ids lowest first (what the brain binds its Work/Mine set step over,
  #3616): the `Finish` ticks sum `ceil(HitPoints / 80)` pick hits at
  `round(100 / MiningSpeed)` ticks (JobDriver_Mine), for a pawn not incapable of
  Mining. For both, `SiteApproachTicks`
  is the route to the target nearest the pawn and `Location` is that target;
  walking between targets is not modelled. One `Haul` op per stack
  (`<site>/haul/<thing>`): `Origin`/`Input`/`Resource`/`Count` are the stack,
  `Location` is the nearest cell of the best-priority player storage accepting it
  outside the footprint, `SiteApproachTicks` and the `Carried` delivery's `ApproachTicks`
  the pickup route, its `DeliveryTicks` the loaded carries plus returns over `Capacity`, and
  `Finish` is `Finishes` at 0. Native's own blocking haul moves the stack aside; the forecast
  charges a carry to storage instead (#3241), and binds the stack as its Deliver to
  `Location` before placement (#3614), one carry at a time over a stack larger than
  `Capacity` (#3625). One `Deconstruct` op per building the
  player may deconstruct (`<site>/deconstruct/<thing>`: a ruin wall, door or piece
  of furniture, or the colony's own; #3557): `Input` is the building's id,
  `Resource` its def and `Location` its cell; the `Finish` ticks are `JobDriver_Deconstruct`'s
  work (`WorkToBuild` clamped to 20..3000) over `ConstructionSpeed x 1.7` a tick
  (`JobDriver_RemoveBuilding`), for a pawn not incapable of Construction and able to
  manipulate. A roof-holder the enclosure guard would hold for roof support
  (`RoofWait`) has a valued op too (#3617): `Cells` are the roofed cells its removal
  leaves unsupported, nearest the building first, as the game's
  `RoofCollapseCellsFinder.ProcessRoofHolderDespawned` would find them with the
  building gone (`RoofSupportSafety.Unsupported`: the roof components around its
  footprint no other holder connects to, the collapsible cells within
  `RoofMaxSupportDistance` of it with no holder within that range through roofed
  cells, and the components those leave with no holder); the rows' `Finish` ticks
  add `JobDriver_RemoveRoof`'s work (65 a cell at the same rate) and route to, and
  `Location` is, the cell nearest the pawn. The brain removes that roof before the
  Deconstruct in one order; a cell outside the planned room stays unroofed (the
  room's RoofIn re-roofs only its own interior). Only a roof-holder whose support
  is unknown (fogged or out-of-bounds geometry, a pending collapse, a thick roof or
  an unprovably large roof among the cells, or an empty set while `RoofWait` still
  holds) has no op (finding `planned_clearing_roof_support`). A speed
  of zero is `CannotFinish`; a pawn with no route or unable to do the work has no
  row.
- `Verdict`, from `GenConstruct.HandleBlockingThingJob`'s kinds for every thing in
  the footprint: `Illegal` when a refusal outlasts clearing (native re-checks each
  piece with every removable footprint thing skipped: terrain affordance or water,
  map edge, unfinished research, interaction spots) or a thing is not removable
  (a building the player may not deconstruct), which is infeasible; else
  `Unestimable` when a thing is removable but not estimated (a blueprint or frame
  to cancel, a non-natural mineable, a never-haulable item), or a refused piece is
  fogged (finding `planned_site_fogged`), or a haulable has no accepting storage (finding
  `planned_clearing_haul_destination_unknown`) or a deconstructible roof-holder's
  support is unknown (`planned_clearing_roof_support`): leave the alternative
  unvalued; else `Feasible`. A pawn, something
  blueprint placement wipes, or something `CanPlaceBlueprintOver` allows that does
  not `BlocksConstruction` needs no clearing.
- `Options`: the placed-site `ConstructionActivityOption` rows, one per pawn and
  material source (the multi-stack rule is in
  [pawn activities](pawn-activities.md#live-material-delivery-and-building)). The `Finish` ticks are `WorkToBuild(stuff)` summed over
  the pieces over the pawn's `ConstructionSpeed`. Routes end at the piece nearest
  the origin. Material owed is the piece cost times the pieces. A pawn with no route
  has no row. A quality definition has no stored finishing minimum before placement,
  so only its native skill prerequisite gates `Finish` (finding
  `planned_quality_minimum_unset`).

`ConstructionActivityRequest.Routes` asks for routes between planned sites
(#3313). A `PlannedRouteProbe` is a `From` cell, a `To` cell and the cells it may
`Reach` (a site's pieces; empty for one cell); `FromStart` probes from each pawn's own
position instead of `From`. Each comes back per free pawn as a `CoupledRoute` in
`ConstructionActivityRead.Routes`: `From` (the probe's, or the pawn's position), the
probe's `To`, `FromStart`, and `Ticks`, the native path cost (`FindPathNow`, touch)
to the `Reach` cell nearest the origin, else `To`. No path leaves `Ticks` unknown; a
probe out of bounds has no row (finding `planned_route_out_of_bounds`).

A read covers every free spawned colonist (#3341). Its only bound is on input: at
most 16 placed sites, 16 planned sites and 16 route probes; a longer list sets
`Truncated` and the caller batches. Rows, sources, positions
and colonists are not capped. The coupled census passes no planned sites; Rounds ask
them per round ([planned-site facts](../architecture/colony-forecast.md#planned-site-facts)).

### Standing removals

Every construction activity read also reports the standing removal designations
(#3557, #3612, #3614): the Deconstruct the room reconcile stood over an interior ruin
or a stale piece (`FurnitureOut`), or the player's own, the Uninstall it stood over a
piece it packs (`Pack`, `PackInUse`) and the Haul it stood over a stack it moves out
(`HaulOut`). Under voluntary control nothing works a designation by itself, so the
coupled census reads them and the forecast binds each building as a pawn's Work order
of the op's kind and each stack as a pawn's Deliver of it. `ConstructionActivityRead.Removals`
is one `PlannedSiteClearing` op per designated thing, the same shape as a planned
site's clearing: `Id` the thing's id, `Kind` `Deconstruct` (a building the colony may
deconstruct), `Uninstall` (a minifiable building of the colony's own, what
`WorkGiver_Uninstall` takes) or `Haul` (a spawned haulable item), and `Options` one
row per free colonist. A building's rows: `Site` and `Input` the building's id,
`Definition` and `Resource` its def, `Location` its cell, `WorkGiver` the deconstruct
or uninstall giver, `SiteApproachTicks` the route and the `Finish` ticks the job's work
(`JobDriver_Deconstruct`'s clamped `WorkToBuild`, or `JobDriver_Uninstall`'s
`uninstallWork`) over `ConstructionSpeed x 1.7` a tick; `Count` and `Capacity` are 0.
A Deconstruct over a roof-holder the enclosure guard would hold for roof support
carries the roofed cells its removal leaves unsupported in `Cells`, priced and
located as a planned clearing's is (#3617); one whose support is unknown, and an
Uninstall over any roof-holder the guard would hold, has no op (finding
`removal_roof_support`); a pawn incapable of Construction or with no route
has no row. A stack's rows are the planned-clearing haul rows (`Definition` the
stack's def): `Location` the nearest cell of the best-priority player storage
accepting it outside the footprint of every requested planned site covering the
stack's cell (the whole map when none does), `Origin` its cell, `Count` the stack,
`Delivery` carried by trips; a stack with no such destination has no op (finding
`removal_haul_destination_unknown`).

A standing Cut wave (#3613) is read the same way: the spawned plants under a CutPlant
designation (the reconcile's `Cut` obstruction clearing, or the player's) grouped by
contiguity (eight-adjacent cells; CutPlant takes no guard record, so the cells group
the wave) are one `Cut` op per group, `Id` the lowest plant's id, `Targets` the
plants lowest id first, `Definition` the first plant's def, and rows in the planned
Cut op's shape (`Location` the member nearest the pawn, the `Finish` ticks the summed
plant work, `WorkGiver` the plant-cut giver). A HarvestPlant stood as chop wood is a
harvest's designation and is not read.

A standing FloorOut wave (#3615) is read as the cell form: the cells under a
RemoveFloor designation (the reconcile's `FloorOut`, or the player's) whose top
layer can still be removed, grouped by contiguity (eight-adjacent cells), are one
`RemoveFloor` op per group, `Id` `removefloor/{x}/{z}/x{n}` of the wave's first cell
(lowest x then z, as `GroundStepMethod` names the method), `Cells` the wave in that
order (empty `Targets`), `Definition` the first cell's terrain, and rows for each
free colonist not incapable of Construction and able to manipulate: `Location` the
member cell nearest the pawn, `SiteApproachTicks` the route to it, `WorkGiver` the
remove-floors giver (`WorkGiver_ConstructRemoveFloor`) and the `Finish` ticks the cells'
summed `JobDriver_RemoveFloor` work (200 a cell) over `ConstructionSpeed x 1.7` a
tick (a rate of zero is `CannotFinish`).

### Planned shelter thermal read

The coupled capture samples `ThermalCell` rows only within six cells of placed sites
and heaters, so an unplaced planned shelter has none. `ConstructionActivityRequest.Thermal`
(a `PlannedThermalRequest`) asks for them (#3323): each `PlannedThermalRoom` is a
room's interior bounds, and each `PlannedThermalHeater` is a planned heater
site id, definition and cell. `ConstructionActivityRead.Thermal` answers with a
`CoupledThermalRead`:

- `Cells`: a row for every interior and ring cell, in the coupled capture's shape.
- `Sources`: one `ThermalSource` per requested heater whose definition is a native
  `Building_Heater` or a fueled heat pusher. `Target` is the planned site id and
  `Complete` is false. Heat per tick, default target and power draw come from the
  definition. The network is the one a connector at the planned cell would join
  (`BestTransmitterForConnector`). A fueled source is read as below.
  Any other definition, or a cell off the map, returns no source.
- `Workers`, `Outdoors` and the exchange constants are read as in the coupled capture.
  `RoofRoutes` is empty, because a planned roof is a planned site.

Every member of the read is set. `Supply` is a `HeatSupply` state: an electric heater
is `Powered` (network id, its additional draw, the network's surplus, supplied for a
day) when the network's current surplus covers its draw and a built heater is on,
`ShortOfPower` when it does not, and `Unconnected` with no network at its cell. The
forecast prices heat only from `Powered` and `Burning` sources, and a schedule that
overloads a network turns its sources `ShortOfPower`. A `ThermalWorker` is a free
colonist who can do construction and has a construction speed; one without is not
listed. A `ThermalRoofRoute`'s `Approach` is a `Path` of ticks or `Unreachable`; an
unreachable colonist gets no roof option for that cell, never a free approach. The
roof exchange rates are `RoomTempTracker`'s private constants, and the read throws
when a game version lacks one.

The caps are 4 rooms, 16 heaters and 1,024 cells; anything past them sets `Truncated`.
Rounds ask once per round
([planned shelter](../architecture/colony-forecast.md#planned-shelter-and-occupancy)).

A fueled heat pusher (#3673) is a def with a `CompProperties_HeatPusher` of positive
`heatPerSecond` and a `CompProperties_Refuelable` (a campfire); the coupled capture
reads standing ones, blueprints and frames beside the electric heaters, and the
planned read the requested ones. `HeatPerTick` is `heatPerSecond / 60`,
`TargetTemperature` is `heatPushMaxTemperature`, and it has no power network. When
the burn is steady (not `consumeFuelOnlyWhenUsed`, `consumeFuelOnlyWhenPowered` or
`externalTicking`) and of one fuel def, its supply is `Burning`: unlimited, `Fuel`
that def and `FuelPerTick` `fuelConsumptionRate / 60000 /
FuelMultiplierCurrentDifficulty` items a tick; otherwise it is `UnsteadyBurn`. The brain charges the fuel as material
([forecast value](../architecture/forecast-value.md)).

### Planned room prediction read

`ConstructionActivityRequest.RoomPredictions` asks (#3339), per planned room and ring
stuff, for the room as it would stand once its ring is built: a `PlannedRoomShape` is
the room id, the stuff, the interior cells and the ring `PlannedRoomPiece`s (cell,
definition, stuff) the planned wall and door sites would build, plus one piece per
cell of the room's planned `FloorIn` sites (the floor's `TerrainDef`, no stuff, in
every ring stuff, #3797), and its `Furniture` (#3840): one `PlannedRoomFurniture`
(anchor cell, definition, stuff, rotation) per interior anchor of the room's planned
Build sites, in every ring stuff, which native counts standing in the closed room so
the stage is the furnished room's. Rounds ask it in the
same read as the thermal read, for every enclosed unplaced planned room with ring
sites, and every planned kitchen even with none (its cleanliness is priced, #3802; a
ringless prediction owes no work), once per ring stuff (`PlannedRoomPredictionRequestOf`); nothing is capped.
`ConstructionActivityRead.RoomPredictions` answers one `PlannedRoomPrediction` per
shape whose ring closes a room, read whole (#3834). A shape whose interior has no
passable cell closes no room and has no row; a piece naming neither a `ThingDef` nor a
`TerrainDef`, a piece off the map, a stuffed def with no loaded stuff (a stuff-less
piece stands in the shape's stuff) or an unreadable game member fails the read:

- `Role`: the game's `RoomRoleDef` with the highest `Worker.GetScore` over an
  unregistered room that lists the things the closed room would contain or touch;
  `None` at the map edge or over 60 regions.
- `Impressiveness` and `Stage`: `RoomStatWorker_Impressiveness` over the predicted
  Wealth, Beauty, Space and Cleanliness (the game's worker bodies run over the planned
  interior with the planned ring pieces counted), and `RoomStatDef.GetScoreStageIndex`.
  A `None` room has the stat's `roomlessScore` and that score's stage. The planned
  floor is not counted here.
- `Cleanliness`: `RoomStatWorker_Cleanliness` over the same room with each planned
  floor piece's terrain in place of its cell's (things contained or adjacent times
  their stack count, plus each cell's terrain stat, over the cells; natural soil is -1),
  and no filth beyond what stands now; the stat's `roomlessScore` for a `None` room.
  The steady state with the room's stations' filth is the
  policy's (`PlannedRoomCleanlinessRules.Steady`,
  [facilities](../architecture/facilities.md)).
- `Cells`, `OpenRoofCells`, `TouchesMapEdge`: what PsychologicallyOutdoors and
  UsesOutdoorTemperature read once closed.
- `Beds`: the beds inside; `Benches`: the bill-giving buildings inside, each with
  whether `StatPart_WorkTableOutdoors` applies to it now.

The unregistered room is never added to the map. The staged
`observations/planned-room-prediction` case predicts an open 3x3 patch with a wooden
bed closed by a wood ring and door, stages that ring, and requires the prediction's
role, impressiveness and cleanliness (within 0.001), cell and open-roof counts and bed
to equal the game's own room as `observations_list_rooms` reads it (the room read has
no stage, so the stage follows from the impressiveness); a second shape with sterile
tile floor pieces must predict the same impressiveness and the cleanliness the room
reports once the stage lays that floor.

## Food reserve components

Native reports forbidden stacks (the thing row's `forbidden`) with their
eligible eaters; the brain decides the reserve (`policy.ReserveFoodDefinition`:
forbidden shared pemmican or packaged survival meals). Native eligibility
describes who could eat the stack after release; `ForecastFood` validates it
but excludes it from runway and usable nutrition. Other forbidden food is
dropped from the food census by the decoder.

`ReviewFoodReserve` budgets `reserveDays × observed daily demand`
(`RoundsPolicy.FoodReserveDays`, fixed at `policy.DefaultFoodReserveDays`, five
days). It proposes whole-stack holds
only after roofed storage is observed, up to the target rounded by the final
stack. Release requires runway below the supplied food minimum and a complete
channel read with no delivery before exhaustion. Unknown delivery facts cannot
authorize release. Prospective reserve foods are excluded from this decision
so released stock is not immediately forbidden again.

`SelectReserveBill`, used by `PreserveFood`, prefers available survival-meal
recipes, then pemmican. It subtracts observed reserve stock rather than promised
bill output. Native target counts include existing units of the selected
product; other reserve products reduce that target. Existing recipe bills keep
their settings. The policy review proposes IDs only; the shared concern and Hands
integration owns holds, releases, replenishment timing and policy configuration.

The food-stores/storage Need schedules refill independently
of ordinary food deficit and commits bounded supply actions for holds/releases.
Release uses the shared portfolio delivery leads; unknown channels cannot prove
an emergency. Reserve access has foothold priority and does not wait for a
standing preservation bill. Forbidden stock is already excluded from the forecast,
so the portfolio does not subtract the reserve a second time.

A caravan formed with `FormCaravanIntent` takes reserve stock first for each
cargo definition; the food-stores/storage Need then
refills the reduced reserve through its ordinary holds and preservation bills.

## Meal tier policy

`ReviewMealTier` consumes a reviewed `FoodPlan`, raw-food runway excluding meals
and reserves, allocated cooks, expectations pressure, the previous tier and
native recipe facts. `SelectProductionBill(CookFood)` accepts this input through
`ProductionBillContext.Meals`; without it, ordinary cooking selection is unchanged.
The food-plan owner supplies the context. Previous tier means the last observed
active tier; a recommendation alone must not advance that latch.

Upgrades require runway at least half a day above the seasonal target (half the
minimum/target gap when narrower). Fine persists down to the target; lavish needs
strict surplus and observed high-expectations pressure. Falling below the target,
losing the qualified cook or losing a required ingredient source lowers the tier.
Every ingredient slot must have an alternative supplied by an observed-open,
retained ledger channel. A proposed Open channel is not yet ingredients; Hold on
an already-open channel is usable. Unknown and Close rows provide no ingredients.
Recipe skill floors are native facts. Within a tier, selection prefers nutrient
efficiency, then less work per nutrition, with stable name/bench ties.

Raw runway below minimum or constrained cooking labor recommends paste only when
the dispenser definition is available and one active network has enough calm-night
headroom for its native power draw. Separate networks are not added together.
The recommendation identifies the network; it does not prove placement, physical
connection, hopper availability or pawn feeding. Returning from paste requires
recovery above the minimum margin and available cooking capacity.

The cooking concern supplies the context each tick. Native raw-stock categories
exclude prepared meals and reserves from its forecast, and only available pawns
with an observed cooking priority contribute skill. Stock ingredient support
rows carry accessible nutrition with zero production rate, preventing double
counting. Only unsuspended observed bills establish the previous tier.

Hands replaces a superseded meal bill through the ordinary durable bill action,
whoever wrote it: an older-tier bill on any cooking bench, or a bill of
the chosen recipe that is suspended or repeats to a smaller target than the
colony needs. A bill of the chosen recipe that is active and repeats forever or
to at least that target ends the review. Native admits the replacement for any
ordinary meal bill it finds by id (`CookMeal*` short of the survival pack in
the brain; `MealSimple`..`MealLavish` preferability natively), deletes it and adds the
successor; reserve and butcher bills never go through this path. The replacement
ID survives a restart; a tracked old action receives a superseded
outcome.

High native expectations plus a measured mood deficit name the `food/cooking` Need through
MoodProvision. Paste is a PlanSiteType construction method: one firm network,
connector reach, native definition sizes and a clear apron select a dispenser
and adjacent hopper. Both placements and combined costs pass ordinary building
admission. Cooking is recovered only from a powered dispenser with hopper food,
not its blueprint or placement receipt. `policy.TestMealTiersFineThenSimpleAfterDrain`
checks the fine-to-simple tier decision once raw stock is drained.

## Shared food portfolio

The rounder retains one complete FoodPlan per observed tick and fact
invalidation generation. It budgets combined human and animal demand, seasonal
thresholds, native forage/hunt sources and field estimates. Unknown inputs do not
certify surplus. Field harvest ETA remains an optimistic native bound; projected
delivery never increases stored-food runway. Animal feed reuses the ledger's
consumer allocation when available. The portfolio is the food view
(`policy.SupplyFoodPlan`) of one `PlanSupply` call over the Nutrition demand
([supply model](../architecture/supply-model.md)); the slaughter offer re-runs it.

Each Round the portfolio credits a channel by what it delivers
([credit](../architecture/supply-model.md#credit-and-state)): `Open` is true once
the delivery ledger shows the channel delivering, and the channel's rate is scaled
by its observed/expected factor. Acquisition admits Open sources. Field and cooking capacity and stock protection
are zero-contribution Hold rows: their own observed preconditions and existing
resource/labor admission still apply. Extra housing waits while GapPerDay is
positive. A second pending field is admitted only when the gap remains positive
after its predecessor's known projected output; unknown infrastructure output
keeps the existing-work barrier.

`policy.PlanSupply` is the same budget generalised over a demand vector and
unified candidates; it reproduces FoodPlan's decisions and replaces it at the
food cut-over ([supply model](../architecture/supply-model.md)).

GET /api/player/colony exposes foodPlan and foodPlanTick from the retained review,
including portfolio/unknown rows, decisions, rates and explanation terms. Missing
or stale reviews are null; this read never runs a new food review.

### Delivery ledger

`ColonyFactsSnapshot.DeliveryLedger` is the `DeliveryLedger` facts record: native's cumulative delivery
counters, latched at the production site of the player's own work: a plant
harvested (`QuestManager.Notify_PlantHarvested`; a plant outside a growing zone or
basin is forage, counted only when edible), a catch rolled
(`FishingUtility.GetCatchesFor`, counted under its water body), milk, wool and other gathered body resources
(`CompHasGatherableBodyResource.Gathered`) and eggs (`CompEggLayer.ProduceEgg`).
Hauling and stack merges never count. Each `DeliveryCount` is keyed by `Kind` (`DeliveryKind`: crop, fish, forage,
animal product), `Source` (growing zone or basin id, water body root cell `x,z`, plant
def, animal race) and delivered `Def`, with units and human-edible nutrition. Keys are unbounded; the set is
bounded by the game's defs, zones and water bodies. The `LoadToken` changes on load or restart, so a reader diffs
within one token and re-baselines on a change. The ledger is always read: a colony facts read that cannot count
fails ([facts records](interface-contracts.md#facts-records)), and the projection holds it as a `required`
member. The brain maps source to channel from its own
census: the channel builders name their counter group (`Source`: `crop:<zone>`,
`fish:<x,z>` as `water-<x>-<z>`, `forage:<plant def>`, `animal_product:<race>`, the kind's wire name before
the colon). The credit lives in memory only.

Hunting is counted in two stages, each keyed by the corpse's thing id (the food census's
stock id): a `Kill` when a player pawn kills an animal (a hunt, or a slaughter
that leaves a corpse; the animal's `Pawn`, race, body size, potential nutrition as meat amount x meat
nutrition, tick) and a `Butcher` where `GenRecipe.MakeRecipeProducts` makes a
butcher recipe's products (any bill, any butcher; meat units and nutrition, leather units). The ledger holds
every record of the load token in tick order.
The brain links a kill to its butcher by corpse id; hauling a corpse never counts. Rot is read from the existing
corpse census. The plan credits the `hunt` group from these records: a kill at its
corpse's potential yield times its rot clock, replaced by the butchered meat; a kill
cadence over the trailing 14 days feeds the hunters' throughput
([hunting](../architecture/supply-model.md#hunting)).

### Need-task targets and the capacity rule

`ForecastCoupled.EvaluateCoupledSchedule` checks every need target a portfolio claims
against its capacity. A food or hauled-stack claim holds its units from its use tick
(#3626, below); every other claim holds the target for the whole Method.
A target is refused when its distinct selected owners exceed its `MaxPawns`
(`conflicting_selected_target_owners`; the minimum over its claims, an unknown value
read as 1, which is this evaluator's policy and not part of the contract) or when the
claimed counts of one stack sum past its quantity (`target_stack_quantity_exceeded`).
The claims are the `ForecastNeedTask`s of the selected activities (`Target`, `Pawn`,
`Count` per claim, `AvailableCount` the stack quantity, `MaxPawns`, and `Batch` naming
the produced batch when several batches can sit in one delivery cell; counts sum per
target and batch), plus each selected binding that names an
observed `Needs.Pawns[].Targets` id without a need task (capacity from that observed
target, count 0). The rule never reads `Kind`; the first replaced #3135's one-owner check.

Timed claims (#3626): one rule, `TargetUseRules` in `WorkResources.cs`, decides both here
and in Hands admission, so the forecast cannot select a pairing admission refuses. Each
claim is a `TargetUse` (owner, count, `[From, Until)`, `Alone`). A food claim holds the
stack from its allocation's `Depart` until its `End` (native executes a non-preparing
order at once, so the ingest reservation starts at departure). A Deliver or Supply
binding on an observed stack with a known `StockCount` is an `Alone` claim of its count
over its own allocation: vanilla hauls reserve with `maxPawns` 1, so no other owner may
overlap it, but a haul done before an eat departs shares the stack. Owners conflict when,
at any claim's `From`, more distinct owners hold the target than `MaxPawns`, or an `Alone`
claim overlaps another owner's; units conflict when the counts sum past the stock.
`ForecastNeedTask` is a policy-internal record; the wire type is `NeedTarget`. What a
target means depends on its producer:

| Producer | `Kind` | `Target` | Meaning | `MaxPawns` |
| --- | --- | --- | --- | --- |
| Native `NativeNeedsForecast` bed row | `rest` | bed thing id | the bed the pawn lies in (`CanUseBedNow`) | observed (`SleepingSlotsCount`) |
| Native recreation building row (`NativeRecreation`) | `joy` | building thing id | the exact recreation object | observed (`joyMaxParticipants`) |
| Practice joy tasks (`ForecastCoupledCandidates`) | `joy` | practice target | the exact practice object | the observed target of that id, else unknown |
| Native food row (observed stack) | `food` | meal stack thing id, `AvailableCount` = stack size | where to take meals from | observed (10) |
| `CoupledFoodMethodCandidates.AddEating` (produced or delivered food) | `food` | `cell/x/z` of the delivery cell (`CoupledFoodUse.Location`) | the cell the meal stack will sit in; `Count` is the meal, `AvailableCount` the batch | 10, vanilla's ingest value, the generated stack's capacity |
| Incumbent preparation (`ForecastIncumbentPreparation`) | observed service kind | the observed service target | the current service the pawn already holds | the observed target of that id, else unknown |

Capacity (#3209): each observed `NeedTarget` carries `MaxPawns`, a `Fact<int>`, the
number of simultaneous claimants vanilla `ReservationManager.CanReserve` admits on that
target (claims must share one `maxPawns`, and summed stack counts must not exceed the
stack). Native fills it from vanilla, never from the target's `Kind`: a food stack
reads the literal 10 that `JobDriver_Ingest` reserves with (vanilla names no symbol for
it, so `NativeNeedsForecast.IngestMaxPawns` mirrors it); a bed reads
`Building_Bed.SleepingSlotsCount`; a recreation building reads `joyMaxParticipants` of the
job def of the `JoyGiverDef` that uses it. A value native cannot read (not positive)
is unknown, which is explicit on the wire and not a guess; what the planner assumes for
unknown (1) is evaluator policy, not part of the contract. Quantity is the existing
`StockCount` (the stack size claims sum against), distinct from `AvailableCount`, which
is capped at `maxNumToIngestAtOnce`. The evaluator reads it as above (#3210); a generated
target without a `NeedTarget` row (a delivery cell) carries its capacity on the
`ForecastNeedTask` itself.

An ingestion target names only the source stack. The Eat step carries a thing target
(or `observed-output`, rebound natively to the selected resource within two cells of
the use cell and the observed lot) plus a `count`; native builds `JobDefOf.Ingest` on
that stack with `job.count` and checks `stackCount >= count`
(`NativePawnActivities.cs`). Nothing reserves a dining
cell, chair or table for the eater: the chew spot is chosen by RimWorld at ingestion
and is listed as unsupported (`chew_spot_travel` on the food `NeedTarget`). The native
legality check is per pawn, so conservation of the shared stack lives in the
evaluator's lot ledger (`Inputs` with `Lot`; refusals `source_lot_shortfall`,
`timed_material_shortfall`), which bounds what a batch can supply independent of the
capacity check, whose summed-count test refuses claims that exceed the stack at once.

### Hands claims and native bind-time reservation (#3211)

Hands applies the same capacity model when it admits a Method, so a portfolio the
evaluator accepts is not refused later. `WorkClaimsOf` projects every Sleep,
Recreation and Eat `Step.Target` (an Eat target of `observed-output` is not yet an
identity and is not claimed) as a `WorkTargetClaim` in `WorkResourceClaims.Shared`: the
target, the claiming pawn, the task and the stack count (the Eat step's `Count`; 0 for a
bed or a pin). The task row and its order name one claim once. A shared target is also
listed in `Claims.Targets`, which generators read to avoid every claimed target; admission
is what shares it. The capacity comes from `WorkAdmissionEvidence.NeedTargets`
(`WorkTargetCapacity`: the observed `MaxPawns` and `StockCount`, frozen with the review
and persisted with it); a target without an observed row, or with an unknown `MaxPawns`,
reads as capacity 1 (this admission's policy, as in the evaluator), and an unknown
quantity skips the summed-count bound. The rule never reads `Kind` or a target's string
form.

`WorkResourcesRefusal` refuses a shared target only when its distinct owners (committed
claims, the proposal, and any pawn natively holding it) exceed `MaxPawns`, or the summed
counts exceed the quantity: `shared_target:` when the proposal alone does,
`target_claimed:` against committed work, and the existing `pawn_target_claimed` /
`native_target_claimed` when a native holder tips it over. The test is
`TargetUseRules.Conflict`, the forecast's rule: an Eat claim, and a Deliver or Supply step
on an observed stack with a known quantity (an `Alone` claim), is timed from its task's
`DepartTick` to `UseUntilTick` (persisted on `SelectedActivityTask`, from the selected
allocation), else to the step's deadline; native holders and other claims are open-ended.
Each order of a proposal is checked against its own task (`ordersOnly`). A need target
with a committed `Alone` claim is not proposed again. Any other target a claim takes
whole (a bench, a site, a stack whose quantity is unknown) stays exclusive against
shared claims too. `cell/` targets of a PawnActivities batch keep their existing exemption.

Native bind-time reservation uses the numbers vanilla reserves with. Before offering the
job, `NativePawnActivities.OfferJob` checks `CanReserveAndReach(target, Touch, ...)` with
`NativeNeedsForecast.ServiceReservation`: Eat reserves (stack, 10, `step.Count`) as
`JobDriver_Ingest` does, Sleep (bed, `SleepingSlotsCount`, 0) as `JobDriver_LayDown`, and
Recreation (building, `joyMaxParticipants`, 0) as the giver's play driver; any other step
keeps vanilla's default. The activity-task bind read (`NativeActivityTasks`) checks Eat,
Sleep and Recreation targets with the same claim (#3570). `ReservationManager.CanReserve` refuses a claim whose `maxPawns`
differs from an existing one's, which is why the default of 1 refused a stack another pawn
already held with 10, and it sums stack counts, so two pawns bind one stack while their
counts fit and the next is refused. The observation-time legality filter in
`NativeNeedsForecast` makes the same check through `ServiceReservation`, so an observed
target is one bind-time accepts; with no step yet, an Eat target's count is what the
ingest job asks for, `FoodUtility.WillIngestStackCountOf` (the pawn's wanted nutrition
capped by `maxNumToIngestAtOnce`) clamped to the stack. Tests:
`WorkTargetCapacityClaimsTests` (stack within and beyond quantity, a one-slot bed, observed
capacity, unknown as 1, a whole-taken target). There is no native unit-test harness for the
bind-time path; it is verified by compile and the decompiled vanilla drivers, and natively by the `needs/target-capacity` nightly case (#3213) and, for a held multi-seat
recreation building at bind, `needs/recreation-shared-seat` (#3570).

Exclusivity is still real for a one-slot bed and for recreation objects with a single
participant (pawn-owned for the whole service); it is now a capacity of 1 rather than a
separate rule. Tests that pin the evaluator side:
`ForecastCoupledSelectionTests.TwoParticipantsCannotSelectTheSameObservedServiceTarget`
(joy), `ForecastIncumbentSelectionTests.SyntheticRetainedTargetCannotBeAssignedToAnotherParticipant`
(joy), `FutureNeedTargetClaimsTests` (Hands claims for sleep/recreation) and
`PortfolioSearchDiagnosisTests` (the four-worker food case, now accepted). The rule was
introduced in #3135 (e05747f10) as Hands ownership of exact targets for the whole Method,
and replaced by the capacity model in #3210.

## Trade food policy

`ReviewTradeNeed` accepts an optional `TradeFoodContext` from the shared food
review, and the food plan decides the purchase: a present caravan's recorded
offers ([trade offers](../architecture/supply-model.md#trade-offers)) become one
one-shot `FoodTrade` candidate per trader (`TradeFoodChannels`, ID the trader
id): cheapest-per-nutrition food rows, lead 0, silver priced as upfront labor,
stock capped by the trader's goods, the silver above the reserve and the gap
over the plan's window (`FoodPlan.HorizonDays`). `TradeFoodNeed.Nutrition` is
the stock of the candidates the plan opened; the session buys only its own
trader's (`PlannedTradeNutrition`). A tradeable caravan with no fresh record is
browsed by the always-browse session whatever the food state ([trade
offers](../architecture/supply-model.md#trade-offers)); until its sheet is
recorded, and for a travelling caravan, it has no candidate: windfalls are
never planned for.

Desired recipe ingredient slots use the meal policy's alternatives. Above the
food target, missing meat/animal-product slots create ingredient purchases.
`TradeFoodGood` takes the native nutrition stat and reads ingredient class, preparation,
perishability and crop classification from the definition catalog rows for trade selection. Purchases rank durable
food, then prepared meals, then raw food, sharing a nutrition budget across
available definitions. Existing silver and price limits still apply.

`CropSurplusFloors` supplies explicit retained targets to `SelectTrade`. Only a
known raw vegetable crop may use this exception, and only after a protein
purchase has been selected. Unknown protection flags still refuse export;
retained targets and economic floors take their maximum. No purchase budget
means no protected crop sale.

Rounds and fresh trade selection both use the shared per-tick food
plan with seasonal runway thresholds. Active fine or lavish meal bills supply
the desired ingredient slots; existing raw protein stock reduces the purchase
quantity.

Native trade sheets provide validated definition nutrition and ingredient
classification; drugs, corpses, kibble and human meat are excluded. Typed native
acceptance independently requires a raw protein purchase and positive retained
floor for crop exports. Recorded colony snapshots (`RimGovernor.Brain.Executor`) replay the review's
food bridge and the above-target crop exchange with its missing protein.

## Fishing policy

`FishingChannels` budgets one row per Odyssey water body. Its raw nutrition/day
is `min(0.025 * maxPopulation * nutritionPerFish, pawnFishWorkCapacity)`.
Native fish yield and fishing speed determine work and capacity (eight working
hours per available fisher per day). Cooking gains remain a separate ledger
contribution. Frozen or unreachable water contributes nothing and carries an
explanation term; missing facts stay unknown and invalid known values fail.
At or below the population floor (`domain.FishingPopulationFloor`, 0.6 of the
maximum) native pauses fishing, so the row expects 0 with term
`fishing_below_floor` until the lake regrows. A fishing row is Designated while
a zone exists and Delivering once the ledger's `fish:<root>` counters rise
([credit and state](../architecture/supply-model.md#credit-and-state)); a zone
over a lake below its floor delivers nothing, so its factor falls.
On Core, Odyssey water and its fishing channels are absent.

Native reports raw fishing facts and never chooses (`FishableWater` in
[observations](../../../contracts/wire/observations.md)): per water body its root, fishable cells with a per-cell
reachability flag and distance to the colony centre, population, nutrition per
fish, batch yield and work, the population yield-curve value, frozen state and
the touching zones (allowed, repeat mode, population floor, fishable cells);
globally the fishers' yield and speed, the batch duration, and the Fishing
project's cost, progress, cost factor, researcher speeds and points per tick.
`FishingFacts.cs` decides from them when the colony projection is
built: the footprint, the reachability verdict, delivery (the observed zone
state), per-body fisher capacity and the research lead.

The shared tick food plan includes these rows. An admitted Open channel requests
Fishing through the `economy/research` Need when needed; research lead is the remaining project
cost over the fastest researcher's daily points. The field family creates the selected fishing
zone through the shared concern, admission and zone Hands path. Work allocation
uses the native `Fishing` type and Animals skill. The proposed footprint is the
nearest reachable fishable cell and its cardinally connected reachable
neighbours, one cell per available fisher; area never multiplies yield. An
unzoned body with a shorter footprint is unreachable. Existing player zones are not reconfigured by the planner.

Typed fishing zone creation and extension use the zone-map CAS token. Extension
names the exact existing zone and supplies its complete final footprint, a strict
superset in the same water body. Both set ordinary `DoForever` fishing with
`targetPopulationPct = 0.6`. Native fishing pauses below the floor and resumes
as population regrows; bursts above daily regeneration are allowed. Receipt
verification checks actual cells and settings, separately from catch outcomes.

`FoodChannelPolicyTests` own the fishing rate (the lesser of regeneration and
fisher capacity); `food/ledger` counts native catches on an Odyssey coast.

The [observed survival-deadline adapter](../architecture/colony-forecast.md#observed-deadlines-for-shelter-and-food)
uses the existing eligibility/rot-aware food forecast and per-pawn cold model in
bounded activity comparisons. Food stock exhaustion is not starvation onset;
constant-temperature serious-hypothermia timing is not a weather or room-heat
forecast. Candidate relief and labor ceilings remain explicit supplied evidence.
No native execution or production cutover is implied.

[Quantitative candidate effects](../architecture/colony-forecast.md#quantitative-activity-effects)
replace a supplied prevention flag in that adapter. Food is checked against
catalog nutrition, actual scheduled output time, eater shares and expiry;
shelter requires occupant arrival and a sufficient conditional temperature range.
The plan shares one stock/claim budget for all activity inputs. Thermal estimates
and future output evidence remain supplied, not native predictions.

[Observed food candidate generation](../architecture/colony-forecast.md#food-candidates-from-acquisition-and-recipe-observations)
joins native acquisition/crop and bench/recipe facts into raw and cooked activity
alternatives, including bounded ascending whole-batch sizes rather than only the
largest possible batch. Every size spends generation budget; truncation means
omitted sizes have not been compared. Bindings expose recipe iteration counts,
which are not repeating native Cook authorization. The existing recipe-input catalog projection carries product counts
and base work alongside ingredient slots. Worker rates, carrying/travel estimates,
destination eligibility and continued bench availability remain explicit estimates.
Generation remains pure policy. Normal selection binds its chosen dependencies
through the existing Method journal and Hands; observation never dispatches work.

The [shared live work-resource view](../architecture/colony-forecast.md#shared-live-work-resources)
extends fixed-batch admission to pending building placements and planner-owned
food activities. Colony facts include each spawned pawn's activity identity,
revision/state, current job target/bill, carried thing/definition/count and local hour. These are observed
facts, not additional stock or a completion estimate. Native bill holds include
spawned queued/placed ingredients; carried inputs are outside that loose census.

Needs observations and the conditional projection are described in
[needs alternatives and future capacity](../architecture/colony-forecast.md#needs-alternatives-and-future-capacity).
The routine `Forecast.Needs` census carries native rates, target eligibility and
memory expiry. Missing census/targets/rates remain unknown or explicitly absent;
truncation is not completeness. No save intent or journal schema is added.

## Residual matching

Forecast residual diagnostics compare each selected prediction with later native
evidence without changing the forecast, selection or schedule. The
[OTLP schema and report](otel-signals.md#forecast-residual-evidence) specify
identity, timing, execution cohorts and incomplete-history handling.

`FoodStock` subjects are resource definitions; scope includes the precise census
area and eligibility used by the forecast. Available stock must not be compared
with a total-food census. Preserve the forecast's quantity unit (for example
items versus nutrition); resource definitions and units never merge in a summary.
`FoodLevel` and `RestLevel` compare native need levels, not exposure integrals or
malnutrition severity. `Mood` compares actual mood, never its target. Their unit
is a level fraction, and scope describes the native measurement definition.
`ShelterTemperature` uses degrees Celsius on matching physical geometry; scope
must change when that geometry changes, even if a room ID is reused.
`TaskCompletion` uses absolute game ticks on the existing Method/task/revision,
with native exact timestamp or interval-censored evidence, never an accepted-order
receipt. Unknown geometry, food census, temperature coverage or completion time
must remain named gaps when the existing native reads cannot establish them.

Origin is the observation tick on which the prediction was based; target is the
supported forecast endpoint. No state prediction is extrapolated past that
endpoint. Observation lag remains visible even when it prevents aligned scalar
statistics. Timing and execution divergence are separate: a matched operational
outcome under interrupted or replaced work is not execution-consistent model
error. Normal replanning alone does not establish that the relevant work changed.

Live capture runs after the existing coupled admission decision, retaining the
original selected portfolio even when dispatch filters protected incumbents.
Each Rounds revision is captured once; review identity includes a reset epoch so
rewinds cannot reuse an old correlation key. Task rows carry the actual Method and
revision when a binding exists. Deferred/unplaced tasks without that binding
report `task_execution_identity_unavailable`. Refused proposal tasks remain
not executed; incumbent tasks retain continued admission independently.

Fresh review reads resolve food/rest levels and actual mood at the first review
on or after the target tick. Lag is retained, with no interpolation. Shelter
samples require the same canonical interior-cell geometry and complete, uniform
native temperature coverage; geometry changes and missing coverage are gaps.
Food-stock predictions use the selected evaluator's retained eligible per-resource lots.
Later reviews count the same map food-supply scope: nonreserve, native diet and holder eligibility.
Incomplete census, unknown eligibility, expiry or quantities remain named gaps. Current stock is never substituted for a prediction.

The Player's pending owner keeps at most 4096 compact rows, expiring one game day
after their target. Capacity evicts oldest origin first, ties by review/sample;
retirement emits `pending_capacity`, `pending_expired`, `world_changed`, or
`tick_rewind`. No contexts, candidate graphs, native reads or scheduler wakes are
added. OTLP retains history; disposal or ring rotation can leave unmatched rows.

Existing native task reads supply completion intervals only after a positively
observed unfinished task, never from admission or a generic poll/state-transition
timestamp. A first completed read without that bound is an explicit gap. Same
Method/revision task execution with both endpoints can enter the consistent task
cohort; global needs/mood/thermal rows remain unknown absent sufficient execution
evidence. Detected interruption or replacement is propagated to affected pawn
rows. Unchanged reviews alone neither prove consistency nor mark replacement.

### Native residual evidence

`production/forecast-residuals` stages one tired colonist, a bed, meals and recreation in a quiet world,
then leaves Food, Rest, Joy and actual Mood unfrozen through the ordinary served production horizon.
It uses the acceptance charter's typed horizon observation declaration and never injects endpoint values,
completion, or extra review wakes. `ForecastEvidenceReader` decodes the registered prediction and observation
kinds; the case requires known native food/rest/mood values and a genuinely completed task (a bounded
completion interval is sufficient). Its result retains identity, origin/target/read ticks, provenance,
execution cohorts, timing mismatches and named unknowns. The case also requires a known per-resource food-stock prediction and native observation;
when no shelter prediction exists, the report names that gap. This proves correlation, not prediction accuracy.
The full retained-window report can also be generated with `RimGovernor.cmd forecastresiduals -logs <logs.jsonl>`.

### Carried long-term model state

`ColonyModelState` is disposable state owned by one pure forecast invocation.
`Observed` builds the baseline and marginal entry from the review; the same
`ColonyModel.Run` engine also accepts carried state. `Day` is elapsed days from
that review, and `EndDay` is the common final date on that axis (normally day
60), so a day-one continuation runs 59 days. Original climate and scheduled
input stay tied to the review. A carried snapshot includes **all** events at
or before its day; the day-zero observed entry precedes its time-zero events.
The engine skips earlier scheduled effects and advances the twelfth before
allocating labor. Completion, harvest and skill-level dates remain relative
to the original review. It preserves kind/id ordering and the 4,096-event cap.

Material and medicine quantities are fractional per-resource stocks. Physical
material stock includes held inputs and excludes consumed inputs. External
shared-owner holds exclude graph-owned holds; both subtract from available
material. Each graph node carries remaining work, waiting/done/failed status,
growth-initialization state and unpaid/held/consumed input status. Completion
never reruns a done prerequisite and never charges consumed inputs again.
The state is a complete graph census, not the single-node marginal perturbation.
Carried pawns supply skill mechanics, stored levels and XP; continuous daily
skill decay keeps fractional progress and dates its level changes on the same
axis. The existing model's steady-cycle labor and coarse skill-decay mechanics
remain unchanged.

Unavailable material/medicine stocks reject a carried run, as do invalid dates,
project identities or overcommitted holds. At the ordinary observed origin,
legacy partial trajectories remain available with explicit stock-unknown
findings; their aggregate placeholders do not establish known zero stock.
A budget-limited trajectory retains its truncation finding, never a horizon-end
claim. The existing food-climate observation carries the native daily growing
pattern for a full year from the review instant, including multiple growing
windows. The food-runway diagnostic advances that periodic calendar on the
same review-relative axis as climate and scheduled events. It solves crossings
against the changing seasonal reserve, including phase-in knees between engine
events. Restarting neither freezes the original harvest gap nor starts another
year. Missing annual observations are named unknowns, never a zero reserve.

This engine entry does not yet provide continuation **valuation**: the bridge
must account for initial needs and restrict pricing to the remaining interval.
A done craft/construction node is not evidence that the original review's
outfit, shelter, capacities or needs changed. Supported handoff extraction must
supply those consequences in its valuation context or declare them uncovered;
pre-handoff output must not be paid again. Initial recovery and coverage-aware
valuation are owned by #3719, with partial-room continuation in #3709.

### Detailed terminal handoff

The detailed evaluator retains its final physical resource balances, eligible food and medicine lots, and
successful completion times, from the same inventory walk that produces its
findings. `ForecastHandoffPolicy.Project` projects a selected outcome into
`ColonyModelState`; search does not run model, growth or skill projections for
every candidate. The immutable review input/policy travels with that compact
accounting. No extra native read, persistent state or second inventory forecast
is involved. The current consumer is selected-plan coverage telemetry; ranking
still uses the existing marginal comparison.

Physical stock includes holds and excludes consumption. Shared-owner budget
projection releases only linked bill/debit holds; unrelated holds remain.
Construction delivery removes paid construction demand rather than charging it
again. Food and medicine retain their own eligible lot census instead of requiring equality with all material stock.
The existing native resource census also carries its exact thing identities, so unreachable medicine and held/forbidden food cannot debit loose material stock.
The same detailed ledger applies production, oldest-first unbound debits, spoilage and continuous native demand from consumers without a pawn timeline.
Their observed daily demand continues in the long-term engine; it does not disappear at handoff.
Held food and forbidden reserves are not loose material debits. New output carries observed dietary eligibility and shelf life; deliveries retain the original lot expiry.
Scheduled physical effects retain resource identity and apply once before handoff, converting nutrition to item units through observed catalog nutrition. Aggregate research points retain their original-review accumulation alongside project remaining work. Unknown native quantities, eligibility and expiry remain unknown, never zero. Project work is taken
only from successfully evaluated allocations. Research completion integrates
concurrent work. Each completed sown cell retains its native callback completion
date. The handoff's `Graph` expands the field into age cohorts and its remaining
unsown area; each cohort grows and harvests independently in the same engine.
Payoff-free milestones retain the original aggregate graph identities. A
continuation must use this graph with the handoff state, not the review graph.
Maturity crossings before the boundary retain their actual climate-integrated
dates. Recipe and construction target identities join detailed work to graph
nodes, whose remaining inputs and products exclude work already paid and made.
An unrelated output sharing a resource never consumes another project or herd.

Pawn terminal need levels retain the detailed result, including unknowns.
Skills reuse the completed productive/practice callbacks and native passive
decay. Callback validity still ends before local/global midnight. Terminal-stock
projection may continue passive decay and level settlement after that cut,
because those midnight resets affect callback saturation/range budgets, not
stored XP or decay; it rejects every callback at or after the cut. It does not
create a fresh learning-validity window. Research/sowing XP without observed
callback mechanics remains outside both existing forecast tiers.

Completed outfit changes and their ordering, room closures and shelter endpoints
travel with the handoff. Active effects retain their original start dates for
memory aging and strip already-paid stock changes and claims. Completed shelter
plans restart at their endpoint temperature with their remaining source supply,
without redoing construction. Completed insulation changes update carried pawn
temperature ranges. Continuation valuation consumes these effects; retaining
them does not create a benefit for unfinished rooms. Standing construction's
exact remaining owed inputs transfer from external shared-owner protection to
graph-owned holds; unrelated bill holds stay external even when they use the
same resource. Unassessed detailed terms do not invalidate unrelated covered
continuation inputs. Missing terminal needs and unavailable native mechanics
remain explicit. Initial recovery and continuation ranking are separate stages.
