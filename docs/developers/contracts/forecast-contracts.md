# Native forecast contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

Forecasts are read-only and use native definitions and rates. One forecast
implementation serves deterministic facts and player fact inspection.

| Projection | Observed inputs | Bound |
| --- | --- | --- |
| Food and animal feed | Fed food fall, nutrition per eligible eater, food policy, safe reachability, holder and rot deadline | Demand-weighted allocation at current temperature/access; no grazing, future hauling or harvest credit. Downed pawns need assistance, which is not presumed. |
| Harvest and labor | Growing-zone crop, fertility, growth stalls, native yield, sow/harvest work and pending construction work remaining | Native work units; no pawn-hour conversion, season guarantee or completion date. Filtered construction censuses remain unknown. |
| Medical | Native total bleeding and untreated blood-loss death estimate | Current untreated estimate; no inferred disease prognosis or treatment completion. |
| Mood | Current mood, thought target and pawn-specific native break thresholds | Current risk band and target pressure; no probability or time-to-break. |
| Power | Per-network observed watts and stored watt-days | Reserve duration at current deficit; no assumed future generation or grid connectivity. Failed readings remain unavailable. |

Food stock is apportioned only among eaters permitted by native diet, policy and
safe access; kibble is apportioned among animals only, since a colonist eats it
only when nothing better is reachable (#311). Held food belongs to its observed
holder. Earliest-expiry allocation
uses native rot deadlines at the current temperature. The food gate uses the
lowest colonist runway after reserving competing animal shares; unknown combined
demand cannot certify a safe runway. Future harvesting, changing temperature,
feeding jobs and food sharing are not guaranteed. Harvest ETA is an optimistic
lower bound, and crop work does not reserve future production.

Fresh animal corpses carry their native meat amount times meat nutrition,
body size, forbid state and a one-tile footprint. Unforbidden corpses count
as pending-butcher stock for eaters eligible for their meat; forbidden
corpses remain observable but contribute no runway. Corpse stock replaces
the separate pending-hunt corpse credit; live designated prey remains pending.
The [corpse larder](upkeep-contracts.md#corpse-larder) owns reserve release.

Humanlike corpses carry `is_humanlike`; raw meat and meals containing it carry
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
ordinary reserve release. `food/human-butchery` verifies this channel on the
EmptyChannels fixture in the nightly suite.

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
controller hysteresis, not a forecast. Missing thresholds retain established risk.
Power reserve risk likewise persists across unavailable reads and serialization;
recovery requires readable current networks.

See native forecast acceptance for the bounded
Docker probe and the distinction between forecast validation and pawn outcomes.
## Food reserve components

`FoodStock.reserve` identifies forbidden shared pemmican or packaged survival
meals. Native eligibility describes who could eat the stack after release;
`ForecastFood` validates it but excludes it from runway and usable nutrition.
Other forbidden food remains outside the food census.

`ReviewFoodReserve` budgets `reserveDays × observed daily demand` (the default
is `policy.DefaultFoodReserveDays`, five days). It proposes whole-stack holds
only after roofed storage is observed, up to the target rounded by the final
stack. Release requires runway below the supplied food minimum and a complete
channel read with no delivery before exhaustion. Unknown delivery facts cannot
authorize release. Prospective reserve foods are excluded from this decision
so released stock is not immediately forbidden again.

`SelectReserveBill`, used by `PreserveFood`, prefers available survival-meal
recipes, then pemmican. It subtracts observed reserve stock rather than promised
bill output. Native target counts include existing units of the selected
product; other reserve products reduce that target. Existing recipe bills keep
their settings. The policy review proposes IDs only; the shared goal and Hands
integration owns holds, releases, replenishment timing and policy configuration.

`SelectCaravanFood` accepts observed transfer groups eligible for the entire
crew, per-unit nutrition and unrefrigerated remaining shelf life. It packs
reserve groups first, then the longest-lived food, and returns no selection
when the journey cannot be covered. The departure adapter owns obtaining these
facts, respecting home stock floors and admitting the resulting cargo.

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

The cooking goal supplies the context each tick. Native raw-stock categories
exclude prepared meals and reserves from its forecast, and only available pawns
with an observed cooking priority contribute skill. Stock ingredient support
rows carry accessible nutrition with zero production rate, preventing double
counting. Only unsuspended observed bills establish the previous tier.

Hands replaces a superseded owned meal bill through the ordinary durable bill
action. Native checks its current-load ownership, unchanged settings and stack
position before deleting it and adding the successor. Player-created, edited,
reordered or ownership-unknown bills are preserved. The replacement ID survives
controller restart; the old action receives a superseded outcome.

High native expectations plus a measured mood deficit raise EnsureCooking through
MoodProvision. Paste is a PlanSiteType construction method: one firm network,
connector reach, native definition sizes and a clear apron select a dispenser
and adjacent hopper. Both placements and combined costs pass ordinary building
admission. Cooking is recovered only from a powered dispenser with hopper food,
not its blueprint or placement receipt. The registered food/meal-tiers case
checks native fine-to-simple replacement across a controller restart.

## Shared food portfolio

The routine reviewer retains one complete FoodPlan per observed tick and fact
invalidation generation. It budgets combined human and animal demand, seasonal
thresholds, native forage/hunt sources and field estimates. Unknown inputs do not
certify surplus. Field harvest ETA remains an optimistic native bound; projected
delivery never increases stored-food runway. Animal feed reuses the ledger's
consumer allocation when available.

Acquisition admits Open sources. Field and cooking capacity and stock protection
are zero-contribution Hold rows: their own observed preconditions and existing
resource/labor admission still apply. Extra housing waits while GapPerDay is
positive. A second pending field is admitted only when the gap remains positive
after its predecessor's known projected output; unknown infrastructure output
keeps the existing-work barrier.

GET /api/player/colony exposes foodPlan and foodPlanTick from the retained review,
including portfolio/unknown rows, decisions, rates and explanation terms. Missing
or stale reviews are null; this read never runs a new food review. Reserve-days
configuration and reserve dispatch remain separate integrations.

## Trade food policy

`ReviewTradeNeed` accepts an optional `TradeFoodContext` from the shared food
review. Below the seasonal minimum, a known plan with positive gap buys a bridge
only when every retained production channel arrives after exhaustion. Nutrition
is gap times earliest lead; no producer uses one target window. Unknown channel
facts cannot authorize a bridge. A zero-lead hunt suppresses the purchase.

Desired recipe ingredient slots use the meal policy's alternatives. Above the
food target, missing meat/animal-product slots create ingredient purchases.
`TradeFoodGood` supplies native nutrition, ingredient class, preparation,
perishability and crop classification to trade selection. Purchases rank durable
food, then prepared meals, then raw food, sharing a nutrition budget across
available definitions. Existing silver and price limits still apply.

`CropSurplusFloors` supplies explicit retained targets to `SelectTrade`. Only a
known raw vegetable crop may use this exception, and only after a protein
purchase has been selected. Unknown protection flags still refuse export;
retained targets and economic floors take their maximum. No purchase budget
means no protected crop sale.

Routine goal review and fresh trade selection both use the shared per-tick food
plan with seasonal runway thresholds. Active fine or lavish meal bills supply
the desired ingredient slots; existing raw protein stock reduces the purchase
quantity. Crop exports retain the maximum of the resource target and economic
floor, and require a selected raw protein purchase.

Native trade sheets provide validated definition nutrition and ingredient
classification; drugs, corpses, kibble and human meat are excluded. Typed native
acceptance independently requires a raw protein purchase and positive retained
floor for crop exports. The registered `trade/routine-food-bridge` and
`trade/routine-food-surplus` cases assert native inventory changes for emergency
pemmican purchases and above-target crop exchanges; the nightly suite runs them.

## Fishing policy

`FishingChannels` budgets one row per Odyssey water body. Its raw nutrition/day
is `min(0.025 * maxPopulation * nutritionPerFish, pawnFishWorkCapacity)`.
Native fish yield and fishing speed determine work and capacity (eight working
hours per available fisher per day). Cooking gains remain a separate ledger
contribution. Frozen or unreachable water contributes nothing and carries an
explanation term; missing facts stay unknown and invalid known values fail.
On Core, Odyssey water and its fishing channels are absent.

The shared tick food plan includes these rows. An admitted Open channel requests
Fishing through EnsureResearch when needed; research lead is estimated from
remaining native research work. The field family creates the selected fishing
zone through the shared goal, admission and zone Hands path. Work allocation
uses the native `Fishing` type and Animals skill. The proposed connected footprint
has one safely reachable cell per available concurrent fisher; area never
multiplies yield. Existing player zones are not reconfigured by the planner.

Typed fishing zone creation and extension use the zone-map CAS token. Extension
names the exact existing zone and supplies its complete final footprint, a strict
superset in the same water body. Both set ordinary `DoForever` fishing with
`targetPopulationPct = 0.6`. Native fishing pauses below the floor and resumes
as population regrows; bursts above daily regeneration are allowed. Receipt
verification checks actual cells and settings, separately from catch outcomes.

`food/fishing` starts a two-pawn no-soil coast with Odyssey declared in the case
profile (including `knownExpansions`). It checks the live Open portfolio rate,
the controller-created zone's body, cells and population floor, then native
feeding over 15 days. It does not require population to end at 90% of its start.
The nightly full tier runs this case; the smoke tier covers landing regressions.
