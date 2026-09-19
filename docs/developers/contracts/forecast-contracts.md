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

The review includes existing recipes for reconciliation, but the bill selector
preserves matching player bills and performs no retirement. The goal/Hands owner
must prove ownership before replacing a bill, obtain construction placement for
paste, and verify native outcomes. These policy decisions alone are not gameplay
acceptance.