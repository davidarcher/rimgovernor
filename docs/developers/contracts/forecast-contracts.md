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
  stays constant inside `MoodUnclampedTarget`, so the
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
  once the commitment holds; per pawn the last change wins, and a pawn with none wears
  `ForecastCoupledContext.Outfits[pawn]` (the gear census's `GearLoadoutInput.Worn`). The effect-source generator sets it from each
  candidate's `EffectSourceOutfit.Items`. The harm term prices its armor; nothing else reads it.

### Temperature mood from banded cards

Temperature mood is priced from the Live stage-band cards whose input reads the
ambient temperature (`EnvironmentCold`, `EnvironmentHot`; #3327). There are no
hand-written temperature-mood constants, and mood is valued only as `BreakRisk`.

- `ForecastThermalExposure.Spans` is the exposure integrator's per-span output:
  start, end, initial and final temperature, and the comfort range with the
  pawn's comfort shifts applied. The degree-ticks are integrated from the same
  spans.
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
Every field is unknown, or null, when unread; nothing is capped.

- `NeedsForecastRead`: the current weather's `WeatherThought` and its stage count,
  `RainRate`, `MapArea`, and `Deterioration`, the DeteriorationRate stat's
  `StatPart_EnvironmentalEffects` def data (unroofed and outdoors offsets, edifice
  protection factor).
- `NeedsForecastPawn`: `OutdoorsLevel` and `OutdoorsDisabled` (Need\_Outdoors),
  `OwnedBed` and `Ascetic` (the two pawn facts `ApplyBedThoughts` reads), and
  `ThoughtStages`, one `NeedThoughtStage` per thought stage the pawn can get, priced
  by the game's `MoodOffset` for this pawn, with its duration. `Role` names why the
  row was read: `slept_outside`, `slept_in_bedroom`, `slept_in_barracks`, `weather`,
  `need_outdoors`, `undergrounder_indoors`, `undergrounder_underground`,
  `undergrounder_outdoors`. A stage the game cannot price is left out.
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
pawn's `ForecastThermalExposure.Spans`: exact piecewise integration of the givers' severity (quadratic gain under a linear temperature, exponential recovery), the stage path priced as the other terms are, and a severity reaching `LethalSeverity` charging the remaining horizon plus `DeathTerminalValue`. A pawn with unknown occupancy has the term unassessed, with `unassessed_thermal_exposure`.

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
  `FoodPoisoningSeverity` (0 when absent), each unknown without a health tracker.
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
  `pawn.HealthScale`) are unknown without a health tracker.
- `CapacityPenaltyTables.BloodLoss` and `WoundInfection` are decoded like the other tables; `HediffPenaltyTable.Immunizable`
  (`HediffCompProperties_Immunizable`) and `SeverityPerDayTended` (`HediffCompProperties_TendDuration`) carry the infection's course.
  They are not part of `CapacityPenaltyTables.Valid()`; an unknown one leaves its part unassessed.
- `HarmConsequences.Of(hits, bins, response, pawn, tables, support, policy, horizon)` is pure. It returns a `HarmDistribution`:
  `Points` (capability-days lost, probability) summing to 1, `DownProbability`, `DeathProbability`, `Findings`. Deaths carry the remaining
  horizon plus `DeathTerminalValue`. Tended means the support serves a care (`CareServices`) with an observed `TendQuality` (quality and
  room factor are the mean over services); first care is `FirstCareTicks`; otherwise the pawn is untended (`harm_untended`).
  An unobserved pain shock threshold, health scale, incoming-damage or healing factor, capacity or weapon mix makes the distribution unknown
  (`capacities_unobserved:<pawn>`, `weapon_mix_unobserved`). Unassessed parts are findings: `adaptation_feedback_unassessed`,
  `rescue_labor_unassessed`, `injury_part_effects_unassessed`, `bleed_part_factors_unobserved`,
  `capacity_penalty_table_unknown:BloodLoss` or `:WoundInfection`, and for infection `infection_immunity_gain_speed_unobserved`,
  `infection_difficulty_factor_unobserved`, `infection_onset_delay_unmodelled`, `infection_room_factor_unobserved`.
  Mirrored rules: [kept constants](kept-constants.md#mirrored-game-rules-raid-health-consequences-3544).

### Harm term and death floor (#3546)

The ledger's `Harm` term and the `Death` floor read `ForecastCoupledContext.Harm` (`HarmInputs`, assembled per review by
`HarmInputs.Of(RoundsFacts)`) and `Outfits`:

- `HarmInputs`: `Schedule` (`ThreatSchedule.Of` at the observed storyteller and days passed), `Composition`
  (`RaidCompositionRules.Of` at the observed `RaidPoints` and `HostileFactions`, over `RoundsFacts.HarmCatalog.Raid`), `RaidPoints`,
  `DefenseCapacity`, `Exposure` (the embedded `exposure.json` unless injected), `Coverage` and `Natural` (`HarmCatalog`, from the
  catalog). Each defaults to unknown. The hediff tables are `CapacityPenalties`.
- `Harm` = raids per day (major threats per day × raid fraction) × horizon days × Σ over the outcome's pawns of
  (1 − `HarmRiskWeight`) × E + `HarmRiskWeight` × CVaR`HarmRiskAlpha` of `HarmConsequences.Of` for the pawn. Its inputs are
  `RaidExposure.HitsPerColonist(points ÷ DefenseCapacity, role)` and `ArmorResponseRules.OfOutfit` of the pawn's outfit. The role
  comes from the worn primary weapon (ranged: `gunner`, melee: `melee`, none: `noncombatant`); the outcome's outfit is the last
  `OutfitChanges` row for the pawn, else the worn one. The worn primary is a `GearSlot.Primary` option in `GearLoadoutInput.Worn`
  that `ColonyGearReads.GearLoadoutModelFacts` builds from the gear census's `PawnEquipment` (`PrimaryId` and the equipped item
  resolved in the frame's things table), with `Ranged` from the catalog's weapon rows (#3562). A schedule that expects no raids is a known 0. The distribution of a pawn
  is memoized per context by (pawn, role, outfit, care facts, hits).
- Unknown, with the specific finding and no `harm_capability_unassessed`, when an input is: `threat_schedule_unknown` (with the
  schedule's own finding), `raid_composition_unknown` (with the composition's), `raid_points_unobserved`,
  `defense_capacity_unobserved`, `exposure_cell_uncalibrated:<bin>/<role>`, `outfit_unobserved:<pawn>`,
  `capacities_unobserved:<pawn>`, and `HarmConsequences`' own. A known term still reports `other_threats_unassessed`,
  `adaptation_feedback_unassessed`, `fire_unassessed` and `exposure_layout_unassessed`. An unknown `Harm` makes `Total` unknown;
  a comparison still uses the terms both sides assess (`LossAgainst`).
- `ForecastValueExplanation.Death` is assessed when the outcome's and the reference's `Harm` are: its excess is the expected deaths per
  day (raids per day × Σ death probability) above the reference's, minus `DeathRiskFloor`, floored at 0. The reference wears the
  worn outfits. `CompareConsequences` ranks the floors urgent zero, death, break risk, in that order, before the ledger.
- `ForecastValueResult.HarmDetail` (`raids_per_day`, `exposure_range`, `down_probability`, `death_probability`, `cvar`) is reported
  as `harm_detail` ([otel-signals](otel-signals.md)). Until the exposure table is calibrated (#3543) every cell is uncalibrated, so
  a served review reports `exposure_cell_uncalibrated:…` and `harm` null.

### Threat schedule (#3539)

The harm model's first step ([harm model](../architecture/forecast-value.md#harm-model)) reads the storyteller the game is running.

- `ThreatFacts.Storyteller` (`Find.Storyteller.def.defName`), `DaysPassed` (`GenDate.DaysPassedFloat`) are read beside `RaidPoints` and decoded onto `RoundsFacts.Storyteller` and `DaysPassed`; each is unknown when the section or the field is missing. Raid points stay the observed `RaidPoints`
  (adaptation and difficulty are inside it); the schedule does not model their growth.
- `RoundsFacts.ThreatTable` (`ThreatScheduleTable`): per `StorytellerDef`, its home-map threat comps decoded by `Catalog/Storyteller.cs`
  (on-off cycles of `ThreatBig` and random mains with their `ThreatBig` weight share), and the comps it could not model. Empty without a catalog.
- `ThreatSchedule.Of(table, storyteller, daysPassed)`: `MajorThreatsPerDay`, the sum of the comps' expected incidents a day at that day, and
  `RaidFraction` (1 before day 20, 0.5 after). Both are unknown, with a finding (`storyteller_unobserved`, `days_passed_unobserved`,
  `storyteller_unknown:<def>`, `threat_comp_unmodelled:<class>`), when an input or the def is missing. Non-raid threats are not modelled. The
  mirrored literals are in [kept constants](kept-constants.md#mirrored-game-literals-threat-schedule-3539).

## Coherent coupled observation frame

The routine forecast read includes `CoupledForecastRead` alongside needs in one
main-thread observation frame. It carries bounded harvest/delivery, recipe,
construction and clothing options; physical routes and food lots; product counts,
ingestion eligibility/rates and freshness; and thermal cell/source facts. It
reports its actual native observation tick. Decoding does not relabel an older
census with the caller's requested tick. Policy requires agreement with needs,
shared work-resource evidence and demand before constructing a coupled request.

`HuntActivityRead` is the hunting part of that frame (#3130): up to 16 free colonists
who could hunt and 16 wild edible animals, each stated as the encounter predictor's
actor (kind, cell, age, xenotype, weapon and range, apparel, skills, traits, health,
missing parts; for prey also sleeping/downed/mental, herd, and meat, leather and
nutrition if killed), plus the map window around them (weather, temperature, sky glow,
terrain/roof runs, impassable obstacles) and a file-manifest mod identity. It holds no
roster, target or estimate; policy proposes rosters and the runtime asks the
prediction service. Truncation is reported, never silent.

`ResearchActivityRead` is the research part of that frame (#3527): for the current project (`ResearchManager.GetProject()`;
none is Known with no workers and the finding `research_no_current_project`), every free colonist who has a job on
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
roster shares its gunners' predicted scene (`HuntRoster.SceneKey`), so it costs no prediction. Once the plain rosters
are answered, a second stage (`ProposeRetrieverStage`) restates the three best usable rosters of two or more gunners
per prey group with their least-skilled shooter as a gunner-retriever: stated to the predictor as a timed `Leave` at
the plain roster's median encounter ticks (`HuntRoster.DepartureTick`); outside the horizon it is refused
`departure_outside_horizon`. The runtime asks the prediction service for each scene and hands back a `HuntEstimate`
(per-sample counts of serious harm, death and downed prey, expected nutrition net of the chance the prey is not
downed, encounter ticks). `Choose` ranks usable rosters by hunter deaths, then any serious harm (each by its 95%
Wilson upper bound, so a small sample cannot look safer), then nutrition still missing, then total participant labor.
Labor is every gunner's walk to the prey and its encounter ticks (the gunner-retriever's ending at its departure),
plus retrieval from the coupled routes: each corpse's haul to the store (`HuntActorRead.CorpseStore`, native's own
store choice: the nearest cell of the highest-priority store accepting the corpse) by the retriever, or without one by
the cheapest gunner, and the empty walk home of every gunner that hauled nothing; a hauler's walk out counts as haul.
Retrieval enters the labor term only, never the ordering above it. A roster with no estimate, a non-evidence
prediction, unknown prey yield or no known walk is refused with its reason (`route_unknown`); a missing haul or
return row, including no store that accepts the corpse, refuses it `return_route_unknown`. Nothing is priced at
zero. With no nutrition needed, or no usable roster, the choice is not to hunt.

The runtime side is `IHuntEstimates` (`HuntEstimateService` in the host). `HuntEncounterRequests.Build` states each
roster as an `EncounterRequest`: the observed window as the map, the roster gunners each aimed at the nearest
prey, 16 fixed seeds, a 2.4-hour horizon. The service asks the prediction service one request at a time in the
background, never from the planner step, and keeps each answer as a `HuntEstimate` or a refusal, keyed by scene.

A comparison is anchored to one read (#3525): the service holds the hunt read the plan first asked with (the
anchor, `HuntEstimateView.Anchor`, returned to the runtime as `HuntPlan.Anchor` with the rosters to ask: the read
with the prey the plan could hunt then, standing and not designated or taken) and builds every scene of the
comparison from it, both stages, whatever read the later rounds ask from. Policy judges the anchor, never the
service. While scenes are pending, `PlanHunt` gathers the answers without regard to how far the pawns drift between
the reads in between: the worker answers scenes one at a time, seconds each, and judging each answer against the
newest read staled the early ones before the late ones arrived. The comparison compares the prey its anchor saw: an
animal that wanders into the window meanwhile is proposed by the next comparison, after a choice or a restart, and
never restarts this one. The anchor is judged two ways. Every round, its prey (`HuntEncounterRequests.PreyDrift`):
one the read no longer shows, downed, in a mental state or no longer huntable, or a prey group whose members stand
grouped otherwise now (`HuntAlternatives.PreyGroups`, the group radius); where an animal grazed to is never judged.
When a stage is fully answered, at choice time, its gunners (`HuntEncounterRequests.StillAnchored`): each rostered
gunner within `MoveTolerance`, 8 cells, of where the anchor saw it and otherwise the gunner it saw (kind, xenotype,
weapon, apparel, traits, missing parts, the hediff set by definition and part; not his skill levels or a hediff's
severity, which move with every shot and every hour of healing), in the same scene identity and weather. A hunter
the anchor never saw in a proposed roster is judged at any round. Then the plan asks the whole comparison again from
the current read, with the findings `hunt_anchor_stale` and `hunt_anchor_drift:<what>` (`<pawn>=unobserved`,
`downed`, `mental`, `gone`, `group:<members>`, `<gunner>=unseen`, `moved:<cells>`, `changed:<fields>` or `scene`);
the service takes that read as the new anchor, forgets the old comparison's answers, queues the scenes again at
once, and skips an old comparison's scene unasked when the worker reaches it. A stale anchor with no prey left to
compare is only the finding; the next huntable prey starts the next comparison. Rosters, labor and the choice itself
always come from the current read and its routes, and a chosen formation's release triggers judge it against each
later read, never the anchor. The estimate carries the skills and health the anchor stated. An answer lives a day; a
hauler is not in the scene, and its trip is re-priced from each round's routes.
A busy, cancelled, timed-out or unavailable service is retried after an hour of game time; an unsupported roster
stays refused. Policy sees only the view (anchor, estimates, refusals, rosters pending), so a roster that is pending,
refused or unanswered is never ranked as safe.

In production the Bridge hashes the engine identity once at start (`NativeEncounterIdentity`: the game assembly, every
running mod's xml/dll content, and the official mods the worker boots with — RealCombat's `EngineIdentity` algorithm),
off the game thread; until it finishes the hunt scene is unobserved and every roster is refused. When
`RIMGOVERNOR_ENCOUNTER_WORKER` names the built `RimGovernor.RealCombat.Host.dll` (`RIMGOVERNOR_ENCOUNTER_DOTNET`
overrides `dotnet`), `BrainBootstrap` starts a `HuntEstimateService` over it and hands it to the Rounds
(`BrainCompositionOptions.HuntEstimates`); unset, rosters stay unestimated and no formation hunt is offered.

The food plan compares the alternatives (`HuntPlanningRules.PlanHunt`, from the coupled read's hunt observation and routes)
for the nutrition the colony still needs to reach its target runway. It asks for every unanswered roster and offers no
formation hunt until each has an estimate or a refusal, so the first answer back never beats a better one still
pending, and it waits again for the second stage. The chosen roster becomes one formation channel
(`HuntRosterChannel`) naming its gunners, retrievers and prey (`SupplyCandidate.Hunters`, `Retrievers`, `Prey`), with
the estimate's nutrition, labor (and its `hunt_return_ticks` and `hunt_haul_ticks` parts) and harm rate and the sample
counts as terms; every other animal stays a lone channel. The plan's open formation channel files the prey and the
selected gunners on the `military/combat` standing (`RoundsCombat.Prey`, `RoundsCombat.Hunters`), and `HuntFormation`
sends exactly those defenders, however many. A hauler is not sent: `WorkRules.ReviewWorkResources` adds the open
channels' retrievers (`HuntRequestRetrievers`) to the shared pawn claims, so no other voluntary work takes them for
the trip, and the corpse is hauled as native hauling work with the existing delivery credit. For this the coupled
read's routes hold, for each available hunter, a leg from each prey to its corpse store and a leg back to the
hunter's start.

An opened formation is held, not re-compared (`HuntAdmission`, pure policy in memory; a restart forgets it): the hunt
walks and drafts its gunners and startles its prey, and none of that may stale the estimate it was chosen on. It is
released only on an explicit trigger (#3514), and the same review then compares again from the current read, asking
the service for every roster whose estimate went stale, so nothing extends a hunt without a comparison. The triggers,
in the order they are checked and as the `hunt_choice` note's `released:<reason>` names them: an admitted animal gone
from the hunt sources (`prey_gone`, the kill the delivery credit observes) or downed (`prey_downed`); with the hunt
read known, an admitted animal out of the observed window (`prey_unobserved`), a live animal of the group that joined
within the group radius (`prey_joined`), a rostered gunner absent from the read (`gunner_unobserved`), downed
(`gunner_downed`) or in a mental state (`gunner_mental`), or a gunner the read the roster was chosen from no longer
describes (`gunner_changed:<pawn>:<what>`, `HuntEncounterRequests.StillDescribesGunner`: kind, xenotype, weapon, apparel,
traits, the hediff set by definition and part, missing parts and the scene identity; not his position, draft state, skill
levels or a hediff's severity, which the hunt itself moves). An unknown hunt read decides
nothing about the gunners. A drafted gunner is not a trigger: the hunt's own draft and the player's read the same in
`HuntActorRead.Available`. A released hunt whose prey still stands may be chosen again, by the comparison only.

The execution evidence is the on-demand `food/hunt-roster` acceptance case (#3515): a lab colony of two riflemen
short of food, with a butcher table, its bill and a corpse stockpile, and a wild rhinoceros and boar inside the hunt
read's window, served with the Release RealCombat worker as the predictor (at the harness's pinned Ultrafast: every
acceptance game launches with `-rimgovernor-test-acceleration`, so `ServeSpec.ClockSpeed` is a no-op; the anchored
comparison, #3525, keeps its scenes usable there and the `hunt_restart` attribute names what staled an anchor that
re-asked; the crew is exactly the two staged riflemen, since the runner's default crew of three adds an unarmed third
hunter to the read. The case has not passed: the uncapped test-acceleration clock runs the 30,000-tick window in
seconds, fewer than the worker needs for one comparison, #3533). Native reads no hunt
route: the former `HuntingSafety.RouteSafe` verdict (a route within a margin of any wild predator was unsafe, the
prey included, so a grizzly was never a hunt source and a wandering wolf held every hunt) went with #3515. It asserts in order: the comparison
answered (the `rounds_review` row's `hunt_choice` names a chosen roster with no `hunt_estimates_pending`) and the
drafted colonists exactly the chosen roster's gunners (`RoundsCombat.Hunters`); a kill of an admitted prey in the
delivery ledger; the release afterwards (no standing hunters, `military/combat` cleared, nobody drafted); and the
corpse butchered into meat in stock. It records the roster, the chosen channel's `hunt_*` terms, the kill tick and
the meat tally. Roster size is the comparison's: a lone-gunner roster passes on the same evidence. The lab's quiet
storyteller zeroes revenge, so the case proves the hunt's execution, not revenge frequency (the qualification sweep
does). `food/ledger-hunt` keeps the delivery ledger's own kill-and-butcher claim on a lone native hunt; the former
`combatlab/hunt-squad` and `combatlab/hunt-door` cases went with #3399. The remote workflows run neither the case
nor the sweep: both need the worker built and run locally by name.

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
a `PlannedSiteAnswer` in `ConstructionActivityRead.Planned`:

- `CanPlace`: native `CanPlaceBlueprintAt` for every piece, plus finished research.
  Unknown when native cannot resolve the definition or stuff, the site is empty,
  out of bounds or over the position cap; the reason is a read finding
  (`planned_site_*`).
- `Clearing`: the clearing prerequisites, each an id, a `Kind` (`Cut`, `Haul`,
  `Mine`) and per-pawn `ConstructionActivityOption` rows with `Site` set
  to the op id and `Definition` to the planned site's. One `Cut` op (`<site>/cut`)
  covers the site's plants: `FinishTicks` sums `harvestWork /
  JobDriver_PlantWork.WorkDonePerTick` (PlantWorkSpeed scaled by growth), for a
  pawn not incapable of both PlantCutting and Construction and willing to cut
  every plant. One `Mine` op (`<site>/mine`) covers its natural rock: `FinishTicks`
  sums `ceil(HitPoints / 80)` pick hits at `round(100 / MiningSpeed)` ticks
  (JobDriver_Mine), for a pawn not incapable of Mining. For both, `SiteApproachTicks`
  is the route to the target nearest the pawn and `Location` is that target;
  walking between targets is not modelled. One `Haul` op per stack
  (`<site>/haul/<thing>`): `Origin`/`Input`/`Resource`/`Count` are the stack,
  `Location` is the nearest cell of the best-priority player storage accepting it
  outside the footprint, `ApproachTicks` (and `SiteApproachTicks`) the pickup route,
  `DeliveryTicks` the loaded carries plus returns over `Capacity`, and `FinishTicks`
  is a known 0. Native's own blocking haul moves the stack aside; the forecast
  charges a carry to storage instead (#3241). A speed of zero leaves `FinishTicks`
  unknown; a pawn with no route or unable to do the work has no row.
- `Verdict`, from `GenConstruct.HandleBlockingThingJob`'s kinds for every thing in
  the footprint: `Illegal` when a refusal outlasts clearing (native re-checks each
  piece with every removable footprint thing skipped: terrain affordance or water,
  map edge, unfinished research, interaction spots) or a thing is not removable
  (another faction's building), which is infeasible; else `Unestimable` when a
  thing is removable but not estimated (a colony building or frame to deconstruct
  or cancel, a non-natural mineable, a never-haulable item), or a refused piece is
  fogged (finding `planned_site_fogged`): leave the alternative unvalued; else
  `Unspecified` when a haulable has no accepting storage (finding
  `planned_clearing_haul_destination_unknown`); else `Feasible`. A pawn, something
  blueprint placement wipes, or something `CanPlaceBlueprintOver` allows that does
  not `BlocksConstruction` needs no clearing. `CanPlace` stays native legality as the
  footprint stands, so a site needing only clearing reads `CanPlace` false and
  `Verdict` `Feasible`.
- `Options`: the placed-site `ConstructionActivityOption` rows, one per pawn and
  whole-stack material source. `FinishTicks` is `WorkToBuild(stuff)` summed over
  the pieces over the pawn's `ConstructionSpeed`. Routes end at the piece nearest
  the origin. Material owed is the piece cost times the pieces. A pawn with no route
  has no row. A quality definition has no stored finishing minimum before placement,
  so only its native skill prerequisite gates `FinishTicks` (finding
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

### Planned shelter thermal read

The coupled capture samples `ThermalCell` rows only within six cells of placed sites
and heaters, so an unplaced planned shelter has none. `ConstructionActivityRequest.Thermal`
(a `PlannedThermalRequest`) asks for them (#3323): each `PlannedThermalRoom` is a
room's interior bounds, and each `PlannedThermalHeater` is a planned heater
site id, definition and cell. `ConstructionActivityRead.Thermal` answers with a
`CoupledThermalRead`:

- `Cells`: a row for every interior and ring cell, in the coupled capture's shape.
- `Sources`: one `ThermalSource` per requested heater whose definition is a native
  `Building_Heater`. `Target` is the planned site id and `Complete` is false. Heat per
  tick, default target and power draw come from the definition. The network is the
  one a connector at the planned cell would join (`BestTransmitterForConnector`).
  Supply is known only when that network's current surplus covers the draw. Any other
  definition, or a cell off the map, returns no source.
- `Workers`, `Outdoors` and the exchange constants are read as in the coupled capture.
  `RoofRoutes` is empty, because a planned roof is a planned site.

The caps are 4 rooms, 16 heaters and 1,024 cells; anything past them sets `Truncated`.
Rounds ask once per round
([planned shelter](../architecture/colony-forecast.md#planned-shelter-and-occupancy)).

### Planned room prediction read

`ConstructionActivityRequest.RoomPredictions` asks (#3339), per planned room and ring
stuff, for the room as it would stand once its ring is built: a `PlannedRoomShape` is
the room id, the stuff, the interior cells and the ring `PlannedRoomPiece`s (cell,
definition, stuff) the planned wall and door sites would build. Rounds ask it in the
same read as the thermal read, for every enclosed unplaced planned room with ring
sites, once per ring stuff (`PlannedRoomPredictionRequestOf`); nothing is capped.
`ConstructionActivityRead.RoomPredictions` answers one `PlannedRoomPrediction` per
shape:

- `Role`: the game's `RoomRoleDef` with the highest `Worker.GetScore` over an
  unregistered room that lists the things the closed room would contain or touch;
  `None` at the map edge or over 60 regions, "" when unknown.
- `Impressiveness` and `Stage`: `RoomStatWorker_Impressiveness` over the predicted
  Wealth, Beauty, Space and Cleanliness (the game's worker bodies run over the planned
  interior with the planned pieces counted), and `RoomStatDef.GetScoreStageIndex`.
- `Cells`, `OpenRoofCells`, `TouchesMapEdge`: what PsychologicallyOutdoors and
  UsesOutdoorTemperature read once closed.
- `Beds`: the beds inside; `Benches`: the bill-giving buildings inside, each with
  whether `StatPart_WorkTableOutdoors` applies to it now.

The unregistered room is never added to the map. The staged
`observations/planned-room-prediction` case predicts an open 3x3 patch with a wooden
bed closed by a wood ring and door, stages that ring, and requires the prediction's
role, impressiveness (within 0.001), cell and open-roof counts and bed to equal the
game's own room as `observations_list_rooms` reads it (the room read has no stage, so
the stage follows from the impressiveness).

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

`ColonyFactsSnapshot.delivery_ledger` carries native's cumulative delivery
counters, latched at the production site of the player's own work: a plant
harvested (`QuestManager.Notify_PlantHarvested`; a plant outside a growing zone or
basin is forage, counted only when edible), a catch rolled
(`FishingUtility.GetCatchesFor`), milk, wool and other gathered body resources
(`CompHasGatherableBodyResource.Gathered`) and eggs (`CompEggLayer.ProduceEgg`).
Hauling and stack merges never count. Rows are keyed by kind (crop, fish, forage,
animal product), source (growing zone or basin id, water body root cell `x,z`, plant
def, animal race) and delivered def, with units, human-edible nutrition and the
last delivery tick. Keys are unbounded; the set is bounded by the game's defs, zones
and water bodies. The `epoch` changes on load or restart, so a reader diffs
within an epoch and re-baselines on a change. The brain maps source to channel from its own
census: the channel builders name their counter group (`Source`: `crop:<zone>`,
`fish:<x,z>` as `water-<x>-<z>`, `forage:<plant def>`, `animal_product:<race>`). `observation.ColonyProjection.DeliveryLedger` is Unknown for a missing,
unavailable or malformed ledger, never zero, and lives in memory only.

Hunting is counted in two stages, each keyed by the corpse's thing id (the food census's
stock id): a `KillRecord` when a player pawn kills an animal (a hunt, or a slaughter
that leaves a corpse; the animal's `pawn_id`, race, body size, potential nutrition as meat amount x meat
nutrition, tick) and a `ButcherRecord` where `GenRecipe.MakeRecipeProducts` makes a
butcher recipe's products (any bill, any butcher; meat def, units and nutrition,
leather units, tick). Each window holds every record of the epoch in tick order.
The brain links a kill to its butcher by corpse id; hauling a corpse never counts. `CorpseHandling.in_storage` tells a
corpse in a stockpile from one in the field; rot is read from the existing
corpse census. The plan credits the `hunt` group from these records: a kill at its
corpse's potential yield times its rot clock, replaced by the butchered meat; a kill
cadence over the trailing 14 days feeds the hunters' throughput
([hunting](../architecture/supply-model.md#hunting)).

### Need-task targets and the capacity rule

`ForecastCoupled.EvaluateCoupledSchedule` checks every need target a portfolio claims
against its capacity (not time-aware: Hands owns exact targets for the whole Method).
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
`native_target_claimed` when a native holder tips it over. A target any claim takes whole
(a Deliver or Supply source stack, a bench, a site) stays exclusive against shared
claims too. `cell/` targets of a PawnActivities batch keep their existing exemption.

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
