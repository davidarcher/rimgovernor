# Supply model

Epic [#2140](https://github.com/davidarcher/rimgovernor/issues/2140). One type,
`policy.SupplyCandidate` (`go/internal/policy/candidate.go`), describes every
way the colony obtains a good: a food channel, a loot, salvage or mining
source, a bench bill, a trader. The flow ranker and every channel migration
build on it.

## SupplyCandidate

| Field | Meaning |
| --- | --- |
| `Kind`, `ID` | One kind set (`CandidateKind`) covering food and acquisition kinds; a kind both had (hunt, trade) is one value. |
| `State` | `designated` (committed, not yet delivering), `delivering` (observed), `closed`. A fact; unknown is allowed. |
| `Yields` | A set of `{Good, PerDay, StockCap, UnitValue, Headroom}`: a deer yields nutrition and leather. `PerDay` is the steady rate; `StockCap` bounds a finite source. |
| `LeadDays` | Days until the first delivery. A one-shot source is lead 0 with a stock cap and no rate. |
| `LaborPerDay`, `UpfrontCost` | Steady work of a delivering candidate; labor-equivalent ticks and resources to establish it. |
| `Risk` | `{Kind, Weight}` hazards; summed weights discount the yield. |
| `PathDistance`, `DistanceSquared`, `NeedsHaul`, `UnitsPerTrip` | Reach and haul. |
| `Terms`, `Prey` | Explanation numbers; a formation hunt's animals (its mode: one without prey is a lone hunt). |

The type carries state, never credit: which states earn credit is the ranker's
and ledger's rule ([Credit and state](#credit-and-state)).

## Builders

Every producer emits a `SupplyCandidate` directly; there is no other candidate
type and no adapter. `policy.FoodCandidate` starts a food candidate (nutrition
is its first yield) and `policy.SourceCandidate` a one-shot source (loot,
salvage, a deposit, a bill, a trader). `PlanSupply` is the only ranker. A food
candidate is `delivering` when observed delivering, `designated` when
committed and not yet delivering, else `closed`; a one-shot source is a lead-0
candidate whose labor is its upfront cost, with hunt revenge risk left inside
that labor.

## The ranker

Unpriced controllable trade uses `policy.PlanTradeAcquisition` within the
same supply owner, after local and live-priced coverage is calculated. Its
typed proposal names a request or an exact-crew settlement scout, carries the
remaining demand, and earns zero coverage. It orders eligible attempts within
the demand's horizon by native bounded lead, away labor, then stable ID.
Request goodwill remains an explicit native cost; it is never converted to
labor or risk. Requests require a post-payment Ally, an expired cooldown,
reachable console/negotiator, and no passing ships for orbital requests.
Catalog sell generators establish compatibility only; unsupported forms
remain unknown, and no stock or price is inferred.

The Rounder retains proposals for food and resources in derived memory. An
executor claims one before admitting Hands work, holds the claim through
reconciliation, and releases it after the attempt resolves. Native queued
arrivals and comms work also block request proposals after restart. This
does not close local production or reduce the supply gap. Mission planners
provide exact native packing/crew options: known outbound and return routes,
food and rot covering the round trip, and safe away labor. Live settlement
purchases remain away inventory; only observed home delivery changes home
stock. See [controllable trade](../contracts/controllable-trade.md).

`policy.PlanSupply(SupplyPlanRequest)` (`supply_plan.go`) is one pure function
over a **demand vector** and the candidates. It budgets projected rates and
unit deficits, never stored goods or completed work. Food runs on it: `reviewFoodPlan`
builds the channel rows and `policy.SupplyFoodPlan` plans them through
`PlanSupply` with the Nutrition demand and reads the supply plan back as the
`FoodPlan` that the player API and the method planners consume. Resource
mines, bills, chops, harvests and hunts run on it through the Round's resource
supply plan (`policy.PlanResourceSupply`, [space and resources](space-and-resources.md#resource-demand-and-acquisition-scoring));
deep drill runs on it as a candidate, and remote loot and salvage rank through the
recovery queue (`policy.RankRecovery`). A caravan's priced goods
are candidates too ([trade offers](#trade-offers)), and the resource simulator
matrix runs through a `PlanSupply` adapter.

### Demands

A `SupplyDemand` is a good (`ResourceKey`), a priority (1-100), a horizon and
either a flow (`PerDay`) or a stock deficit (`Units`).

- **Nutrition** is the flow `NutritionDemand` builds from the food forecast,
  as the food plan always budgeted it: usable runway = `max(0, runway - reserve)`; the
  horizon is the usable runway, or `max(usable, TargetDays)` once usable runway
  reaches `MinDays`; target coverage is `1 + max(0, TargetDays - usable) /
  TargetDays` and the target is consumption times coverage. It carries the
  emergency flag (runway under `EmergencyDays`) and outranks every resource
  deficit.
- **A resource deficit** (`target - stock`) is a stock demand wanted now
  (horizon 0). A construction shortfall edge or a runway forecast gives the
  demand a deadline as its horizon.

A clothing material (fabric, leather) is a resource demand of the same kind,
wanted within `ClothingHorizonDays` ([equipment upkeep](../contracts/equipment-upkeep.md#clothing-material-demand)):
a hunt serves it as one candidate priced at its leather, and a field with a lead
serves it within the horizon.

### Eligibility and contribution

A candidate serves a demand when a yield matches the good (a demand without a
stuff matches any stuff) and `lead <= horizon`. Contribution:

- toward a flow: `rate * max(0, 1 - sum(risk))`, bounded by the stock cap spread
  over the demand's window `max(1, horizon)`;
- toward a stock demand: a one-shot source gives `min(stock cap, remaining
  deficit)`, and `min(headroom)` too when the output is hauled (unknown headroom
  is none: a hauled candidate must report where it lands); a steady yield gives
  its risk-adjusted rate over `window - lead`.

An upfront cost is labor-equivalent, amortised over the window; resource costs
of a prerequisite are demand edges, not paid here. A candidate's labor is
charged once however many yields it has: a deer's meat counts toward Nutrition
and its leather toward Leather under one charge. The labor budget
(`workers * 20000` ticks per day) is shared by every demand.

Unknown labor returns a usable plan: new commitments Hold with
`unknown_capacity`, including candidates with zero estimated cost. Existing
designations remain committed without gaining delivery credit; observed delivery
and independently established surplus closure remain available. Labor-excess
terms require a known budget. Known zero is an exhausted budget, not uncertainty.

Resource supply uses pure `ResidualSupplyLabor`: effective workers times 20000,
less each selected or retained food commitment's `LaborPerDay`, floored at zero
only when known. Newly selected, designated and delivering work is charged once
per kind and ID across the food plan's portfolio and unknown entries, regardless
of yield count. Closed or unselected closed candidates consume nothing. Missing
workers, food plan, committed labor or commitment state leaves the residual
unknown; a known empty plan reserves nothing. Known pawn observations take
precedence over the aggregate worker fact, including a known empty pawn list.

### Order

Flow demands rank before stock demands. Among flow candidates **lead is the
primary key**, then fishing distance (nearest first), then labor per unit
delivered, then kind and id. The matrix and `food_plan_test.go` require this for
Nutrition: a channel that arrives before the runway expires beats a cheaper
later one, so efficiency orders only candidates of equal lead. Among stock
candidates the key is the score `Value / (1 + labor + distance*(1 + 2*trips) +
trips)` (value is covered units times priority times `1 + unit value`), then
lead, kind, id. An emergency demand re-ranks the candidates that serve it
breadth first: the best of every kind, then the second of every kind.

### Decisions

Candidates are admitted in rank order against what each demand still lacks.
Delivering candidates keep their observed contribution, even over the labor
budget (the excess is a Hold with a `labor_excess` term); a strict surplus
(delivered minus target greater than the contribution) closes the least
efficient first, so a boundary never churns. Closed candidates over budget add
nothing. Under an emergency a candidate that is not yet delivering opens but earns no
credit (`uncredited_until_delivered`) until it delivers.

Hold reasons are stable words: `no risk-adjusted <good>`, `lead exceeds
runway`, `target covered`, `labor budget`, `no_demand`, `no_storage_headroom`,
`competing_urgent_work` (urgent work outranks every matched demand),
`unknown: <facts>` for a candidate missing a fact and `unknown_demand_or_cost`
when the demand itself is unknown. Explain terms are `risk_discount`,
`<good>_per_day` (`<good>_units` for a stock demand), `work_per_day`,
`lead_days`, `target_cover` and `labor_excess`.

### Credit and state

A candidate is `designated` (committed, not yet seen delivering) until the
[delivery ledger](../contracts/forecast-contracts.md#delivery-ledger) shows its
counter group delivering over an observed interval, then `delivering`.
A historical cumulative counter alone does not establish a flow. When recent
delivery stops the measured rate falls to zero; the state remains `delivering`,
so failed existing work does not regain its estimated yield. A closed channel
is never credited. Under an emergency (runway
below `EmergencyDays`) every candidate that is not delivering is uncredited.

`policy.DeliveryCredit` (`delivery_credit.go`, Go memory only, never persisted)
credits a counter group by its factor: the nutrition the ledger counted over a
trailing window divided by the risk-adjusted nutrition the group's committed
channels expected over it, clamped to [0,1], starting at 1. `credited rate =
expected rate x factor`.

- **Attribution** is each builder's own census, named in `SupplyCandidate.Source`:
  growing-zone id for a crop field, water-body root for a fishing region, plant
  def for forage (every plant of a def shares one group), animal race for an
  animal product, `hunt` for every hunt channel (see [Hunting](#hunting)).
- **Window**: `LedgerWindowDays` = 3 days; a bursty channel is judged over at
  least its cycle plus a replant day (a crop's grow days); hunting over
  `HuntCreditWindowDays` = 6 days, so a kill every few days averages out.
- **Warm-up**: an undelivered group is judged after its lead plus one window
  since the plan opened it (`Opened`), so unrequested candidates are not
  penalised. Once a group has deliveries it uses the available trailing
  counter differences immediately, with at least one day's expected demand
  in the denominator; a burst between nearby Rounds cannot promise a sustained
  flow. The first cumulative read is a baseline, earning no delivery credit.
- **Holds**: a factor holds while the ledger is unavailable, after a load
  (re-baseline: the epoch changed) and for a group without a row while rows were
  lost. A group remains tracked through gaps between designations while its
  source remains in the census; its samples are bounded to the trailing window.
  A group no longer in the census is forgotten.
- **Flight**: a `food_credit` decision row per group when its factor moves by 0.1
  or more, its state changes, or a hold ends a window it was judged over.

### Hunting

Hunting is hunter throughput over a finite, live stock of wildlife
(`policy/hunt_credit.go`, `hunt_squad.go`):

- **Stock**: each hunt channel (one per animal or squad group) carries
  `StockCap`, the nutrition of its animals in reach from the wildlife census,
  rebuilt every Round, so the ranker prices it as a finite source
  (`min(rate, stock / horizon)`). Kills shrink it; regrowth and migration
  refill it through the next census.
- **Rate**: `HuntThroughput` caps the channels' summed rate at hunters x kills
  per hunter-day x the meat of an average prey and scales each channel's
  nutrition, work and products by the same share. Hunters are the ranged,
  Hunting-capable colonists (`policy.Hunters`). Kills per hunter-day is the
  ledger's trailing cadence (`HuntCadence`: kills of the last 14 days over
  hunters and the days they span) once it holds three kills, else
  `HuntKillsPerHunterDay` (1). A channel waiting on a hunter's weapon is not
  scaled.
- **Credit**: the `hunt` group is credited per corpse id in two stages
  (`DeliveryCredit.HuntDelivered`). A KILL credits the corpse at its potential
  yield times its rot clock (rot ticks over the most seen; a frozen or
  non-perishable corpse does not decay; a corpse the complete census no longer
  lists is worth nothing); the BUTCHER record for that corpse id replaces it
  with the meat made. A corpse hauled twice is credited once and humanlike
  corpses stay the human-butchery channel's. A corpse rotting unbutchered takes
  the group's factor to zero, so the plan holds the hunt and the butcher bill or
  cook shows as the blocker. The group is `delivering` from the first credited
  kill; a formation hunt is `designated` once the plan opens its prey
  (`HuntAdmission`).

### Husbandry candidates

An animal product is a rate candidate per race: native product nutrition per day
net of the herd's feed per day (never below zero), ledger-counted under
`animal_product:<race>`. A slaughter is a one-shot candidate (`slaughter:<animal>`):
stock cap its meat nutrition, lead 0, 180 slaughter ticks as `UpfrontCost`, no
steady labor. Herd floors, breeding-pair and slaughter exclusions stay policy
([husbandry contracts](../contracts/husbandry-contracts.md#food-channel-planning));
`MaintainHerd` executes the open candidate. Candidates that acquire animals
(taming, purchase) are not built yet.

### Crop candidates

`policy.CropChannels` prices a field as the better of raw and cooked nutrition.
Cooked is the raw harvest times the best vegetable meal's nutrient efficiency
at a usable bench (`CropKitchen.Cooking`); it adds the meal's work per nutrition
to `WorkPerDay`, needs a usable bench and a cook, and wins only when it beats
raw and its cook work fits the bench capacity (`CookTicksPerDay` per
bench-cook pair). Raw needs no cook. A perishable harvest sets the yield's
`StockCap` to its nutrition per day times the item's rot days, so a harvest
burst is credited no further than what survives to be eaten. An unknown recipe
or rot fact leaves that facet Unknown (raw stands, no cap), never zero.

A planted field is `designated`; the delivery ledger (its zone id) moves it to
`delivering` when CROP counters rise. Its lead is the remaining harvest bound
(`policy.HarvestLeadDays`: temperature and the growing calendar).

A field not yet sown is a closed candidate (`policy.NewFieldChannels`, ID
`new:<crop>`), one per viable crop of the field request: the cells the crop
still needs priced like a planted field, the sowing (`FieldSowTicksPerCell`
per cell) as upfront labor, the full grow days across the calendar as lead
(`NewFieldLeadDays`: growth that outlasts the growing days left waits out the
frost) and the rot stock cap. None is offered while crops cannot be sown
outdoors. The field executor (`rounds_field.go`) places a new field only when
the plan opened a `new:` candidate; pending zone creates and add-cells count as
designated nutrition against the gap (`foodPlanFieldRoom`). Site choice stays
with the executor.

### Crop risk and pause

Crop channels carry weights in [0, 1] equal to the expected fraction of delivery
lost (`policy/crop_risk.go`); each is derived from observed facts and left out
(never priced as zero or as a loss) while a fact is unknown:

| Risk | Weight |
| --- | --- |
| `Fallout` | While `ToxicFallout` is on the map, the share of the field's zone cells with no roof (the planning window's cell map; unknown while a zone cell's roof is unread). A new field lies in the open: weight 1. |
| `Blight` | `BlightedPlants / PlantedCells` of the field's farm facts. A new field has none. |
| `Frost` | When the lead outlasts the growing days left, the non-growing share of the lead window times the unroofed share; 0 when the harvest lands first. |

`CropPauseDays` is the longest remaining duration of an eclipse, volcanic winter
or cold snap with a native read; it is added to every crop lead, planted or new,
capped at a year. The same `ToxicFallout` read feeds the indoor-site
`risk-fallout` term (`siteRiskTerms`).

### Trade offers

A caravan's prices exist only in an open trade session's sheet, so the plan
cannot see them when it is built. The negotiator's session is the look a player
takes at the goods: each time `RoundsTradePlanner` reads the sheet of its open
session it records a `policy.TradeOffers` in `Rounder.tradeOffers` (Go memory
only, derived state, per trader id): the trader, negotiator, observed tick, the
colony's silver, the trader's goods-stack count and one `TradeOffer` (def,
units held, silver price) per priced row. The book is read through
`tradeOfferBook.fresh`, which hands out the records of traders still on the
census that can trade and whose record `policy.TradeOffersFresh` accepts: not
older than `TradeOffersMaxAgeTicks` (three game hours) and silver and goods
stacks within `TradeOffersShift` (25%) of the recorded values. A trader that
leaves the census, a settled session (accept or end) and a new generation drop
the record. #2166 presents traders on the model from the same records.

Browsing is general: the Round marks every tradeable caravan on the census with
no fresh record `TraderFacts.Unpriced` (`Rounder.markUnpriced`), and
`TradeRecovered` keeps the TradeWithCaravan Concern standing while one is present,
whatever the colony needs. The negotiator opens a session, the sheet read
records the offers and the plan is rebuilt; lines stage only for what the plan
(or a sale) opened, and otherwise the session is cancelled. A caravan whose
session ended is settled for the occurrence and is not reopened until its
record goes stale.

`policy.TradeOfferCandidates` turns the fresh records into candidates for each
resource in deficit: yield is the cheapest priced row of that def, capped at
`min(deficit, units held, silver above the trade reserve / price)`; the upfront
cost is the silver, priced as labor (`tradeLaborPerSilver`), no haul (the goods
land at the colony) and no lead (the walk is a fraction of a day). The ID is
`trader/resource`. Recording a record changes the book's revision, which keys the
Round's cached plan, so the plan the session reads is built with its own offers.

The food plan reads the same records: `policy.TradeFoodChannels` makes one
one-shot `Trade` channel per present trader with priced food (see
[trade food policy](../contracts/forecast-contracts.md#trade-food-policy)), added
beside the slaughter offers when the plan has a gap, so the ranker opens it when
its lead beats the alternatives. The book is keyed by the loaded world, not the
plan revision, so the food plan (built from the frame's trader census) and the
resource plan read one record.

Trade buys only what the plan opened: `RoundsTradePlanner.selection` limits the
MaintainResource shortfall and the component target to the units the plan opened
for that trader (`resourceSupply.tradeLines`) and the food nutrition to the food
plan's opened candidate for that trader; medicine, surgery parts, ingredient
upgrades and every sale are not plan-opened and are untouched. The live sheet is read
again at staging and accept, so its prices and the existing price floors still
apply, and a session whose plan opened nothing cancels without trading. A
resource whose plan opened a trade offer is held for the caravan: the resource
planner neither mines nor tunnels for it (`claim_held`), and a stale record stops
holding it within three hours.

## How a channel plugs in

A channel builds `SupplyCandidate`s from its own facts and nothing else: its
yields (rate, stock cap, unit value, headroom), lead, labor per day, upfront
cost, risk, state, and distance and haul for a one-shot. It never ranks, budgets
or decides: it hands the candidates and the demands to `PlanSupply` and reads
the entries back (decision, reason, credit per demand). A new good is a new
demand and a new yield key; a new channel is a new `CandidateKind`. A candidate
whose facts are unknown is listed in `Unknown`, never opened.

Priced records also carry the typed trade participant. Orbital offers share the
colony scope and freshness policy; settlement offers belong to the visiting
caravan and never supply colony stock before native delivery at home. Present
orbital ships use the same browsing and purchasing routine as map traders.

Outbound settlement missions are saved Projects chosen by the same acquisition
planner from exact native crew/pack and round-trip estimates. They carry no
secured yield. Live settlement purchases run through the shared supply ranker
under mission demand, silver and mass limits; away cargo enters home supply
facts only after native home entry. Purchase commitment and authorized return
goods follow the ordinary save blobs; uncertain reloads return without buying
again. See [controllable trade](../contracts/controllable-trade.md).
