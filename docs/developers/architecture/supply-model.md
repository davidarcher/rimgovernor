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
| `Terms`, `Prey` | Explanation numbers; a squad hunt's animals. |

The type carries state, never credit: which states earn credit (today a
fishing or product channel counts when opened, a hunt or forage when
delivered) is the ranker's and ledger's rule.

## Adapters

`SupplyCandidateOfFood` / `FoodChannelOfSupply` and
`SupplyCandidateOfAcquisition` / `AcquisitionCandidateOfSupply` convert
`FoodChannel` and `AcquisitionCandidate` losslessly. `RankResourceCandidates`
still consumes the old type; the snapshot golden test proves the food plan and
the ranking identical through the adapters on every recorded food plan and
acquisition census. An open food channel maps to `delivering`, a closed one
to `closed`; an acquisition candidate is a lead-0 one-shot whose labor is its
upfront cost, with hunt revenge risk left inside that labor.

## The ranker

`policy.PlanSupply(SupplyPlanRequest)` (`supply_plan.go`) is one pure function
over a **demand vector** and the candidates. It budgets projected rates and
unit deficits, never stored goods or completed work. Food runs on it: `reviewFoodPlan`
builds the channel rows and `policy.SupplyFoodPlan` plans them through
`PlanSupply` with the Nutrition demand and reads the supply plan back as the
`FoodPlan` that the player API and the method planners consume. Resource
mines, bills, chops, harvests and hunts run on it through the Round's resource
supply plan (`policy.PlanResourceSupply`, [space and resources](space-and-resources.md#resource-demand-and-acquisition-scoring));
deep drill, trade, loot and salvage still use `RankResourceCandidates` until
their cut-overs (#2168), and the resource simulator matrix runs through a
`PlanSupply` adapter.

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
nothing. Under an emergency a one-shot hunt or forage opens but earns no credit
(`uncredited_until_delivered`) until it delivers.

Hold reasons are stable words: `no risk-adjusted <good>`, `lead exceeds
runway`, `target covered`, `labor budget`, `no_demand`, `no_storage_headroom`,
`competing_urgent_work` (urgent work outranks every matched demand),
`unknown: <facts>` for a candidate missing a fact and `unknown_demand_or_cost`
when the demand itself is unknown. Explain terms are `risk_discount`,
`<good>_per_day` (`<good>_units` for a stock demand), `work_per_day`,
`lead_days`, `target_cover` and `labor_excess`.

### Credit and state

Credit keeps the food plan's rules: a delivering candidate counts, a designated
or closed one is credited when the plan opens it, and the emergency rule above
withholds a one-shot's credit. The explicit designated/delivering credit factor
is row 11 (#2157).

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

## How a channel plugs in

A channel builds `SupplyCandidate`s from its own facts and nothing else: its
yields (rate, stock cap, unit value, headroom), lead, labor per day, upfront
cost, risk, state, and distance and haul for a one-shot. It never ranks, budgets
or decides: it hands the candidates and the demands to `PlanSupply` and reads
the entries back (decision, reason, credit per demand). A new good is a new
demand and a new yield key; a new channel is a new `CandidateKind`. A candidate
whose facts are unknown is listed in `Unknown`, never opened.
