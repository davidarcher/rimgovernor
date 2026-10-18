# Forecast value, horizon and harm (proposed design)

[Architecture](overview.md) · [Normal portfolio selection](portfolio-selection.md) · [ColonyForecast](colony-forecast.md) · [Combat predictability](combat-predictability-research.md)

**Status: proposed; the capability-days ledger is implemented, nothing ranks on it.**
`ForecastValue.Of` (#3520, #3521) prices hunger, rest, food poisoning, thermal exposure, mood breaks and
wellbeing per outcome and `coupled_selection` reports it ([policy constants](../contracts/kept-constants.md#policy-constants-3469),
[tables](../contracts/forecast-contracts.md#capacity-penalty-tables-and-per-pawn-capacities)); harm and investment are 0 and unassessed.
The rest of this page describes no current behavior. Current selection is described in
[portfolio selection](portfolio-selection.md), which states that "no injury
probability or universal conversion of death, mood and labor is invented".
This design deliberately replaces that rule. It arose from
[#3435](https://github.com/davidarcher/rimgovernor/issues/3435): the coupled
search took 5–29 s per review in game while the clock ran.

## Problems

1. **The search costs too much for its budget.** In-game profile of
   `needs/target-capacity` (3 pawns, 12 roots, one-day horizon, budget 16,384):
   5.2 s, 2.2 GB allocated, 201 gen-0 collections. 83% of it is
   `ForecastNeedsTimeline`: 11,076 calls at about 390 µs each. The
   [cost table](../testing/portfolio-allocations.md) never showed this because
   its fixtures use a 700-tick horizon. The same fixtures at one day cost
   0.9 s (2 pawns) and 2.5 s (4 pawns, 6.5 GB) in .NET 10.
   `ForecastMoodTimeline` accounts for 95% of the needs timeline.
2. **The budget does not bound time.** It counts schedule-search nodes, not
   evaluation work. One in-game review spent 9,663 expansions in 237 ms;
   the next spent 16,384 in 5.2 s.
3. **Lexicographic ranking cannot trade.** Any difference in an earlier term
   outranks every later term. Continuous tick counts rarely tie, so harm and
   investment terms would almost never decide. The order also cannot express
   "labor now for a payoff later".
4. **A one-day horizon cannot see investment.** Devilstrand needs research
   (800 points), a Plants 10 sower, 22.5 growing days and tailoring before it
   pays anything. Plants rest when the day is under 25% or over 80% through
   (`Plant.Resting`), so that is about 41 calendar days at full rate. Within
   one day it is pure cost.
5. **Harm is unassessed.** `serious_harm_probability_unassessed` is added to
   every vector. Apparel is valued by a heuristic,
   (Sharp×2 + Blunt) × 10 for soldiers.

## Decisions

- **Safety floors first.** A portfolio that breaks a floor is infeasible.
  Everything else is ranked by one currency.
- **The currency is capability-days, risk-averse on harm.** The consequence
  vector stays as the explanation of each decision, not as the ranking.
- **Two horizons.** Tier 1 is the exact near-term schedule (at most one day).
  Tier 2 is an event-driven model of stocks, flows and projects over about a
  game year.
- **A real harm model**, built from the game's own threat, armor and health
  mechanics, with exposure calibrated by simulation.
- **The search becomes allocation-free**, and its budget counts evaluations.

## Comparator

### Safety floors

These are hard constraints on the tier-1 schedule and the tier-2 trajectory:

- No pawn's food or rest reaches zero, unless every feasible portfolio does.
  Then the least zero-time wins, as today.
- Expected raid deaths over the horizon do not rise above the baseline by more
  than `DeathRiskFloor`.
- Each pawn's break risk stays under `BreakRiskCap`.

The floors replace the top tiers of today's lexicographic order (urgent
exhaustion, depth, exposure). They also prune the search cheaply: a partial
portfolio that already breaks a floor is not extended.

### Capability-days

One capability-day is one pawn-day at full capacity: consciousness, moving,
manipulation, sight and talking all at their observed normal. The value of a
portfolio is the capability-days the colony has over the horizon. Most
exchange rates are mechanical, not chosen:

| Effect | How it costs capability-days |
| --- | --- |
| Hunger, exhaustion | The capacity penalties of malnutrition and exhaustion over their duration. |
| Injury | Capacity lost to pain and part damage until healed. Healing per pawn is 8/day naturally (12 not standing) plus 8 × (0.5 + tend quality) tended, × health scale and healing factors. |
| Downed | Full loss from the down until able, plus the rescue labor. |
| Treatment | Tend labor, about 600 ticks ÷ tend speed per injury plus walking, and medicine consumed. |
| Mood | Break risk × expected days lost to the break, including the harm and labor of its kind. Mood changes no work speed directly. |
| Death | Remaining capability-days to the horizon, plus `DeathTerminalValue`. |
| Labor | Hours spent are hours not available for other work. This is already in the schedule. |
| Investment | Capability added or loss avoided after completion, from tier 2. |

**Policy constants.** These are named, documented and tuned by playtest.
Nothing else is chosen by hand.

- `DeathTerminalValue`: the value of a colonist beyond the horizon.
- `WellbeingWeight`: capability-days per mood-day for wellbeing valued for
  itself, above what break risk already counts.
- `HarmRiskAlpha`, `HarmRiskWeight`: the risk transform below.
- `DeathRiskFloor`, `BreakRiskCap`: the floors.

**Risk aversion.** Raid harm is lumpy: downs and deaths are thresholds. A raid's
cost is (1 − w) × E[loss] + w × CVaRα[loss], where CVaRα is the mean of the
worst (1 − α) of outcomes, w is `HarmRiskWeight` and α is `HarmRiskAlpha`.
Other terms use expected values.

**Ties and explanation.** Equal value within one capability-day ×
`TieResolution` keeps the incumbent, then prefers less labor. Every decision
reports each term's contribution in capability-days, the floors it passed, and
which constants it used.

**Other currencies considered.**

- Silver: wealth raises raid points, and needs have no price. It stays a
  sub-ledger for trade.
- Colony survival probability: it barely moves for everyday choices. It
  becomes the floors instead.
- Mood-days: weakly grounded. It enters as `WellbeingWeight`.
- Labor-hours: misprices armor.
- Days to a goal: a possible later layer when the player sets a goal.

## Two-tier horizon

### Tier 1: exact near-term schedule

This is today's portfolio and schedule search with a horizon of at most one
day. It decides what each pawn does next. Its end state (pawn needs, positions,
stocks, partial project progress) is the initial state of tier 2.

### Tier 2: event-driven colony model

Tier 2 simulates about one game year (60 days) by jumping from event to event.
Its cost grows with the number of events, not with ticks.

- **Pawns.** Each pawn has a daily cycle derived once from its need rates:
  sleep, eating and recreation overhead per day, and the free labor hours that
  remain. Skills gate tasks (devilstrand sowing needs Plants 10) and set work
  speed.
- **Stocks and flows.** Arrays indexed by resource id: food, materials,
  research points, power, medicine. Production, consumption and labor are
  rates that change only at events.
- **Land.** A growing cell is one crop at a time; a devilstrand cell is not
  growing food.
- **Projects.** A small graph of research, sowing, growth, harvest, crafting
  and construction nodes with prerequisites and lead times.
- **Crop growth.** The game's rate is 1 / (60,000 × growDays) per tick while
  not resting, × fertility × temperature × light (`Plant.GrowthPerTick`,
  `PlantUtility`). Growth stops outside the growth temperature band. Tier 2
  integrates it per twelfth (five days) of the tile's seasonal temperature,
  about ten segments per crop. That also answers "will it mature before
  winter?"
- **Food runway.** It is a floor in tier 2 as well, so a fiber field cannot
  starve the colony.

**Marginal value of progress.** Tier 2 gives each project the value of moving
its completion earlier:
`value of one hour today = d(value) / d(completion date) × days that hour advances completion`.
Tier 1 trades hours of research or sowing today against today's needs in
capability-days. Each review re-plans, so a failed crop or a cold snap changes
the next decision without any special case.

**Devilstrand worked through.**

| Step | Cost | Source |
| --- | --- | --- |
| Research Devilstrand | 800 points | `ResearchProjects_1.xml` |
| Sow | Plants 10, 400 work per plant, fertility at least 0.5 | `Plant_Devilstrand` |
| Grow | 22.5 growing days, about 41 calendar days at full rate | `growDays`, `Plant.Resting` |
| Harvest | 6 per plant (cotton gives 10 after 8 days) | `harvestYield` |
| Tailor a duster | 80 fabric, 10,000 work, Complex Clothing | `Apparel_Duster` |

The duster multiplies its material's armor by 0.3
(`StuffEffectMultiplierArmor`), so at normal quality a devilstrand duster has
sharp 0.42, blunt 0.108 and heat 0.90; a cloth duster has sharp 0.108. Its
value is the harm it avoids from completion to the horizon, from the harm
model below.

## Harm model

The harm model turns a pawn's outfit and the colony's defense into expected
raid harm per day. The output feeds the currency and the death floor.

1. **Threat schedule.** Expected major threats per day come from the
   storyteller:
   - Cassandra: 4.6 days on, 6 off, about 1.5 big threats per cycle from
     day 11, so about one per 7.1 days.
   - Phoebe: about one per 16 days from day 13.
   - Randy: about one per 9 days.

   Every big threat before day 20 is a raid; after that, raids are about half.
   Size is `StorytellerUtility.DefaultThreatPointsNow`: a wealth curve plus
   points per colonist, × adaptation, × difficulty `threatScale`, × a
   days-passed ramp, clamped to [35, 10,000].
2. **Composition.** Points ÷ pawn-kind combat power gives raid size. The
   faction's pawn kinds give a distribution over about ten weapon bins, each
   with damage, armor penetration (damage × 0.015 unless the def sets it) and
   damage kind.
3. **Exposure.** Hits taken per colonist per raid. This is not derivable in
   closed form: target choice is a weighted random draw, and outcomes depend
   on cover, pathing and projectiles (see
   [combat predictability](combat-predictability-research.md)). It is
   calibrated from simulator sweeps (epic E5), keyed by raid points ÷ defense
   capacity, the pawn's role and the defense layout. It is stored as ranges, so
   the uncertainty stays explicit. The simulator owns combat mechanics; the
   harm model reads its tables and duplicates no constants.
4. **Armor response, per pawn and weapon bin.** For each layer covering the
   part hit (`ArmorUtility.GetPostArmorDamage`, outermost first), effective
   armor = max(rating − penetration, 0). The hit is deflected with chance
   effective/2 and halved with chance effective/2, and a halved sharp hit
   becomes blunt and stops bleeding. Part probabilities come from body
   coverage. The result is a fixed array per (pawn, outfit): expected damage
   per hit and the chance a hit bleeds. A changed outfit swaps one array.
5. **Consequences.**
   - Bleeding: 0.06 × severity per day for cuts, stabs and gunshots, until
     tended. Untended, it stops by itself after 1.5–3 days.
   - Blood loss downs a pawn at 0.60 and kills at 1.0.
   - Downed: pain shock at a pain threshold of 0.8 (about 64 HP of fresh
     injuries), or consciousness under 0.3.
   - Death: blood loss, a destroyed vital part, or total severity of
     150 × health scale.
   - Infection: 15% per wound, reduced by tend quality and the room.

   Downs and deaths are thresholds, so each raid carries a small distribution
   (hits × damage per hit), not only a mean. The CVaR in the currency reads
   that distribution.

**Not modeled in v1:** adaptation feedback (a down lowers future threat points
by about 5–8 adaptation days, a death by 30), non-raid threats (infestations,
manhunters, ship parts), and fire. They are reported as unassessed terms.

## Search engine

The comparator has four nested levels today:

1. **Portfolio search** over subsets of roots. Subsets are deduplicated by
   string keys.
2. **Schedule search** per portfolio: depth-first over task × pawn × window ×
   service time × departure. Each node builds a formatted prefix string. It
   returns every complete plan.
3. **Every plan gets a full schedule evaluation.** Per pawn, that includes two
   needs timelines. The first timeline's mood result is discarded; only its
   capacity is read.
4. A consequence vector per outcome, built with LINQ and sorted sets.

Proposed stages. Each stage is proven by identical output on the recorded
fixture baselines, and benchmarked at a one-day horizon in .NET and in game.

1. **Stop wasted work.** The physiology call computes capacity without mood.
   The search does not build the mood trajectory, which no production code
   reads. Each pawn's needs result is cached per search, keyed by that pawn's
   own inputs.
2. **Integer ids, bitsets, no allocations in hot loops.**
   - Ids are interned once per search.
   - Closures, compatibility and redundant pairs become bitsets; portfolio
     keys become bitset structs.
   - The schedule search updates array state in place and detects duplicate
     prefixes with incremental 64/128-bit hashes.
   - Buffers are pooled, and plan details are built only for winners.
   - The mood timeline uses exact segments between real events, not 150-tick
     steps. Tier 2 cannot exist without this.
3. **Evaluate lazily, budget by evaluations.**
   - Floors and cheap terms are computed first; a candidate that cannot beat
     the best on its bound stops early (branch and bound on capability-days).
   - The budget counts evaluations, deterministically, then is raised.

## Observation gaps

Native does not yet observe:

- the storyteller def, its cycle phase, days passed and adaptation days (only
  raid points are read);
- raid factions and their pawn kinds and weapons;
- per-layer apparel armor with body-part coverage (today `PawnArmor` is the
  strongest single layer);
- pain shock threshold, health scale, healing and incoming-damage factors, and
  bed healing;
- tile seasonal temperature by twelfth, growing-season bounds, and cell
  fertility for planned fields.

## Open questions

- Exposure calibration: which simulator sweeps, and how many raids per cell,
  are enough for stable ranges?
- The tier-2 pawn cycle: is one derived day per pawn accurate enough, or does
  it need per-season variants (temperature, daylight)?
- Initial values for the policy constants.

## Milestones (proposed)

Each milestone has a GitHub issue.

1. [#3467](https://github.com/davidarcher/rimgovernor/issues/3467) Search stage 1 (no behavior change).
2. [#3468](https://github.com/davidarcher/rimgovernor/issues/3468) Search stage 2, including exact mood segments.
3. [#3469](https://github.com/davidarcher/rimgovernor/issues/3469) Capability-days and floors over today's one-day horizon. This replaces the
   lexicographic ranking; the vector stays as explanation.
4. [#3470](https://github.com/davidarcher/rimgovernor/issues/3470) Tier 2: pawn cycle, stocks and flows, project graph, crop growth by twelfth.
5. [#3471](https://github.com/davidarcher/rimgovernor/issues/3471) Harm model v1: threat schedule, composition, armor response,
   consequences, with exposure calibrated from simulator sweeps.
6. [#3472](https://github.com/davidarcher/rimgovernor/issues/3472) Search stage 3 and a larger budget.
