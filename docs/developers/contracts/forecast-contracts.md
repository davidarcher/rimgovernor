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

The coupled comparator prices forecast mood as expected mental breaks (#3221).
`MoodBreakModel.BreaksPerDay` (`LayoutMood.cs`, the one owner of the break math)
gives a level's tier one break per renewal cycle of the tier's MTB (4, 0.8 or 0.5
days) plus the 15,000-tick post-recovery cooldown. The pawn's observed minor-break
threshold sets the tier levels. `ForecastMoodResult.BreakRisk` integrates that
rate over the piecewise-linear target trajectory, cut at the tier thresholds. The
2,000-tick wait below a threshold before a break can fire and the break's own
duration are not modelled. Actual mood and layout's work-day/watt price are not used.

`ForecastConsequenceVector.BreakRisk` sums the pawns' risk and is known only when
every pawn's threshold and mood are assessed. In that case it replaces target
exposure and pressure within the mood tier, so mood far above every threshold has
no comparator value. Otherwise the vector keeps
`mental_break_probability_unassessed` and the raw ticks rank as before. The
serious-harm, unmet-needs and development ordering is unchanged; there is no
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

## Coherent coupled observation frame

The routine forecast read includes `CoupledForecastRead` alongside needs in one
main-thread observation frame. It carries bounded harvest/delivery, recipe,
construction and clothing options; physical routes and food lots; product counts,
ingestion eligibility/rates and freshness; and thermal cell/source facts. It
reports its actual native observation tick. Decoding does not relabel an older
census with the caller's requested tick. Policy requires agreement with needs,
shared work-resource evidence and demand before constructing a coupled request.

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
rooms and missing thermal observations retain explicit unsupported/unknown findings. Unsupported or
truncated reads retain findings and unknown estimates, including missing route
pairs and unpowered heaters.

The [coupled trajectory](../architecture/colony-forecast.md#coupled-food-needs-and-production-trajectory)
consumes this frame with shared resource ownership. It distinguishes loose stock,
reserved inputs, carried/delivered lots and future yields; accepted orders are
never observed production. Existing inventory-only diagnostics remain conditional
accounting. Coupled consumers must inspect each schedule's feasibility, timings
and uncertainty rather than treating a final balance as proof of execution.

The prepared `observations/coupled-forecast` case checks native tick/stock capture,
recipe batch counts and unpowered-source evidence. These native reads cannot be
proved by a policy-only unit test. It does not dispatch a colony portfolio or
establish prediction accuracy from registration/compilation alone.

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
| Native horseshoes pin row | `joy` | pin thing id | the exact recreation object | observed (`joyMaxParticipants`) |
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
`Building_Bed.SleepingSlotsCount`; the horseshoes pin reads `joyMaxParticipants` of the
`Play_Horseshoes` job def from `DefDatabase`. A value native cannot read (not positive)
is unknown, which is explicit on the wire and not a guess; what the planner assumes for
unknown (1) is evaluator policy, not part of the contract. Quantity is the existing
`StockCount` (the stack size claims sum against), distinct from `AvailableCount`, which
is capped at `maxNumToIngestAtOnce`. The evaluator reads it as above (#3210); a generated
target without a `NeedTarget` row (a delivery cell) carries its capacity on the
`ForecastNeedTask` itself. The wire codec is pinned by
`WireCodecTests.NeedTargetMaxPawnsRoundTripsKnownAndUnknownAsNumberOrNull`.

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
Recreation (pin, `joyMaxParticipants`, 0) as `JobDriver_WatchBuilding`; any other step
keeps vanilla's default. `ReservationManager.CanReserve` refuses a claim whose `maxPawns`
differs from an existing one's, which is why the default of 1 refused a stack another pawn
already held with 10, and it sums stack counts, so two pawns bind one stack while their
counts fit and the next is refused. The observation-time legality filter in
`NativeNeedsForecast` makes the same check through `ServiceReservation`, so an observed
target is one bind-time accepts; with no step yet, an Eat target's count is what the
ingest job asks for, `FoodUtility.WillIngestStackCountOf` (the pawn's wanted nutrition
capped by `maxNumToIngestAtOnce`) clamped to the stack. Tests:
`WorkTargetCapacityClaimsTests` (stack within and beyond quantity, a one-slot bed, observed
capacity, unknown as 1, a whole-taken target). There is no native unit-test harness for the
bind-time path; it is verified by compile and the decompiled vanilla drivers, and natively by the `needs/target-capacity` nightly case (#3213).

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
floor for crop exports. Recorded colony snapshots (`RimGovernor.Brain.Snapshot`) replay the review's
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

`food/fishing` starts a two-pawn no-soil coast with Odyssey declared in the case
profile (including `knownExpansions`). It checks the live Open portfolio rate,
the brain-created zone's body, cells and population floor, then native
feeding over 15 days. It does not require population to end at 90% of its start.
The nightly tier runs this case.

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
