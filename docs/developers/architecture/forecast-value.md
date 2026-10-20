# Forecast value, horizon and harm (proposed design)

[Architecture](overview.md) · [Normal portfolio selection](portfolio-selection.md) · [ColonyForecast](colony-forecast.md) · [Combat predictability](combat-predictability-research.md)

**Status: milestones 3 and 5 (harm model v1) and the tier-2 model with its investment credit (#3531) are implemented; the larger search is proposed.**
Tier-1 selection ranks on the safety floors, then on capability-days ([Comparator](#comparator) describes what runs).
`ForecastValue.Of` (#3520, #3521) prices hunger, rest, food poisoning, thermal exposure, mood breaks and
wellbeing per outcome; `ForecastValue.Explain` (#3522) adds the floors; `coupled_selection` reports both ([policy constants](../contracts/kept-constants.md#policy-constants-3469),
[tables](../contracts/forecast-contracts.md#capacity-penalty-tables-and-per-pawn-capacities)); harm (#3546, [harm model](#harm-model)) is priced when the harm inputs are observed and the exposure table has a calibrated cell for the colony, else unknown with a specific finding (three cells are calibrated today); investment is the tier-2 credit of research and sowing blocks and of the skill XP work applies, less the medicine it consumes ([Marginal value of progress](#tier-2-event-driven-colony-model)), 0 for any other outcome. The player's goals add a gain term ([Goal value](#goal-value), #3503).
The tier-2 model's input (`ColonyModelInput`, assembled per review by `RoundsColonyModel.InputOf` from the review frame, #3523) exists and names
each section the frame did not carry. The model runs once per review, before the comparison (`RoundsColonyModel.Review`): the engine, the project graph and the
valuation are in `Policy/ColonyModel`, and the `colony_model` event reports each run.
Sections other than the comparator, the harm model, the problems and the milestones describe the proposed design, not current behavior.
Selection is also described in [portfolio selection](portfolio-selection.md). The design replaced the rule that
"no injury probability or universal conversion of death, mood and labor is invented". It arose from
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
3. **Lexicographic ranking could not trade (replaced by #3522).** Any difference in an earlier term
   outranks every later term. Continuous tick counts rarely tie, so harm and
   investment terms would almost never decide. The order also cannot express
   "labor now for a payoff later".
4. **A one-day horizon cannot see investment.** Devilstrand needs research
   (800 points), a Plants 10 sower, 22.5 growing days and tailoring before it
   pays anything. Plants rest when the day is under 25% or over 80% through
   (`Plant.Resting`), so growth to harvestable takes about 27 calendar days at full rate (41 for full growth). Within
   one day it is pure cost.
5. **Harm is priced (#3546), once the exposure table is calibrated.** The ledger's `Harm` term and the death floor read the
   threat schedule, raid composition, exposure and armor response ([contract](../contracts/forecast-contracts.md#harm-term-and-death-floor-3546)).
   While a cell of the exposure table is uncalibrated (#3543) the term is unknown with a specific finding, and the other
   terms still compare.

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

These are hard constraints on the tier-1 schedule and the tier-2 trajectory. Tier 1 checks them on every
evaluated outcome (`ForecastValue.Explain`); an outcome that breaks one loses to any outcome that does not, and
when both break one the smaller excess wins. A floor that cannot be checked is not treated as passed: the review
reports `break_floor_unassessed` (a mood break risk with no observed threshold) and `death_floor_unassessed` (the harm
term is unknown), and the outcome is ranked on what remains. Pruning partial portfolios on a floor waits for the search
stage 3 ([#3472](https://github.com/davidarcher/rimgovernor/issues/3472)).

- No pawn's food or rest reaches zero, unless every feasible portfolio does.
  Then the least zero-time wins, as today. Zero-times less than a tick apart are the same
  floor, so summing residue never outranks the ledger (#3598).
- Expected raid deaths over the horizon do not rise above the baseline by more
  than `DeathRiskFloor`.
- Each pawn's break risk stays under `BreakRiskCap`. The risk is the expected breaks over the horizon, per pawn-day.

The floors replaced the top tiers of the old lexicographic order (urgent
exhaustion, depth, exposure). They are meant to prune the search cheaply too: a partial
portfolio that already breaks a floor is not extended (not yet; see above).

### Capability-days

One capability-day is one pawn-day at full capacity: consciousness, moving,
manipulation, sight and talking all at their observed normal. The value of a
portfolio is the capability-days the colony has over the horizon. Most
exchange rates are mechanical, not chosen:

| Effect | How it costs capability-days |
| --- | --- |
| Hunger | The capacity penalties of malnutrition over their duration. |
| Exhaustion | No rest category changes a capacity, so the cost is the collapse: the expected time a pawn awake at zero rest spends asleep where it falls, as a full loss (below). |
| Raw ingredients | The meal nutrition a raw eat of a cookable ingredient forgoes, in days of the eater's food fall (below). |
| Injury | Capacity lost to pain and part damage until healed. Healing per pawn is 8/day naturally (12 not standing) plus 8 × (0.5 + tend quality) tended, × health scale and healing factors. |
| Downed | Full loss from the down until able, plus the rescue labor. |
| Treatment | Tend labor, about 600 ticks ÷ tend speed per injury plus walking, and medicine consumed. |
| Mood | Break risk × expected days lost to the break, including the harm and labor of its kind. Mood changes no work speed directly. |
| Death | Remaining capability-days to the horizon, plus `DeathTerminalValue`. |
| Labor | Hours spent are hours not available for other work. This is already in the schedule. |
| Investment | Capability added after completion, from tier 2: the work's progress (project progress, applied skill XP) times its marginal value, a gain, so a negative loss like the goals term; less the medicine the work consumes at its marginal value and the construction material a priced planned room's deliveries consume, at replacement labor (*Construction material* below). |

**Exhaustion collapse (#3598).** Rest changes no capacity and no work speed in 1.7; its mood thoughts are priced
below. What exhaustion costs is the involuntary sleep (`Need_Rest.ShouldInvoluntarySleepFromMTB`): once a pawn has
been at zero rest for more than 1000 ticks, each 150-tick need interval it falls asleep where it stands with chance
150 / (MTB × 60000), the MTB being 0.25 days, then 0.125, 1/12 and 0.0625 days once the time at zero passes a quarter,
a half and three quarters of a day. It then sleeps on the ground (`BedRestEffectiveness.valueIfMissing`, 0.8) until its
wake threshold, which is `WakeThreshold / UnservedGainPerTick` ticks from empty. `ForecastValue.Of` walks each rest zero
span of the pawn's timeline in those intervals and charges, per pawn, the probability it collapses there (having stayed
awake until then) times that sleep, clipped to the horizon, as a full loss. The time at zero restarts with each span,
and a collapse ends the walk because the sleep refills the need. A plan that sleeps the pawn in a bed pays its sleep in
schedule time instead, so when food's zero time is unavoidable and ties the urgent-zero floor, cutting rest's zero time
still wins on the ledger. The time a pawn has already spent at zero before the review is not observed, so a span
open at the review counts from 0 (`rest_ticks_at_zero_unobserved:<pawn>`, which under-prices it). Without the
pawn's ground gain or wake threshold a pawn that reaches zero leaves the term unknown (`rest_collapse_unassessed:<pawn>`).
Not priced: the ground-sleep and outdoor-sleep thoughts of the collapse, the slower immunity of the tired categories
(`StatPart_Rest`), and the life-stage collapse curve of children.

**Raw ingredients (#3626).** Eating a cookable ingredient raw is allowed, but it forgoes the meal its units would make.
`ForecastCoupledCandidates` reads each observed conversion's ratio from the catalog's `Nutrition` stat: the product's
nutrition times its count over the ingredient count, divided by the ingredient's own nutrition (rice: 0.9 / 10 over 0.05,
1.8). The best ratio per ingredient above 1 is `ForecastCoupledContext.CookedNutritionRatio`. The `RawIngredients` term
charges each selected food need task of such a resource its level gain times the ratio less one, over the eater's
observed food fall per day: the food-days the colony must find again, one food-day per capability-day. An eater without
an observed food fall leaves the term unknown (`raw_ingredient_loss_unassessed:<pawn>`); a conversion whose ingredient or
product has no catalog nutrition is named `cooked_nutrition_unread:<ingredient>:<product>` and costs nothing.

**Hungry and tired (#3598).** Time below the hungry or tired threshold costs its mood: the `NeedFood` thought is -6
hungry, -12 urgently hungry and -20 starving, `NeedRest` -6 tired, -12 very tired and -18 exhausted (the defs'
`baseMoodEffect`). The needs walk records each need's stage over the horizon and the mood timeline follows it, each stage
moving the target by its mood less the observed stage's, instead of holding the observed thought constant
([contract](../contracts/forecast-contracts.md#food-and-rest-thoughts-3598)). The ledger then prices it with no new
constant: as `MoodBreaks` when the debuff takes the target under a break tier (the expected breaks times the days they
cost), and as `Wellbeing` otherwise. A plan that plays first and feeds or beds the pawn just before zero therefore loses
to one that serves it first when the deeper stage crosses a break threshold; far above every threshold the difference is
the wellbeing weight times the extra mood-days, which is usually inside `TieResolution`.

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

**Ties and explanation.** Every term is a loss, so the portfolio that loses fewer capability-days wins.
Two outcomes are compared on the terms both assess, and the per-term differences are summed
(`ForecastValueResult.LossAgainst`); a term one side cannot assess never counts for or against it, and its finding is
still reported. A difference within `TieResolution` capability-days is a tie. A tie goes to the terms that have no
mechanical conversion yet, in this order: first-care time of urgent care, thermal relief, pending care batches, tend
quality and room factors (same care services only), maintenance missing XP, joy threshold exposure, requested
development, unsupplied demand and first-care time; then the incumbent, then less labor. Every decision
reports each term's contribution in capability-days, the floors it passed, and which constants it used (the nested
`value` attributes of `coupled_selection`).

### Store-haul value

A haul to storage ([store hauls](../contracts/support-work.md#store-hauls), #3624) is priced in the same currency, as
labor saved plus loss avoided, less its own labor (`ForecastValue.StoreHaulValueOf`). Travel saved: each future use of
the stack, the most saved first, takes what is left of it; its trips (units over the row's carry, rounded up) walk to
the consumer and back from the storage cell instead of where the stack lies, so storage farther from the work saves
less, or costs. A use with unknown travel saves nothing. Loss avoided: the market value the stack loses where it lies
less what it loses stored, over tier 2's horizon (`ColonyModelPolicy.HorizonDays`): all of it if it rots inside the
horizon, otherwise its deterioration over the horizon as a share of its hit points. Market value converts to work at
the game's `StatWorker_MarketValue.ValuePerWork`. Labor: the row's approach and delivery ticks. Every input is a native
read: the rates come from the game (`SteadyEnvironmentEffects.FinalDeteriorationRate`, rescaled to the storage cell by
`StatPart_EnvironmentalEffects`'s factor there, and `CompRottable.TicksUntilRotAtTemp`), so steel, wood, stone blocks
and plasteel earn nothing here. A haul is admitted only with a positive total, and only as standby work: it never
competes with the coupled selection's tasks.

### Goal value

The player's goals ([player goals](player-goals.md)) enter the currency as a ledger term, `Goals`, a gain (a negative loss)
for the progress a schedule makes inside the horizon (#3503). A goal is worth `GoalImportanceValue[Low, Normal, High]`
capability-days when it is completed, times `GoalDeadlineDecay` per day past its deadline (never under
`GoalDeadlineFloor`); a schedule earns the fraction of that worth it progresses. Research is paid per point of the goal
project's remaining cost, a worn-garment goal per colonist dressed (the requirement relief it injected), and relief a
goal injected is not also counted as a unit of requested development. Other goal kinds have no forecast route and earn
nothing yet. A goal tier 2 models (#3505) is paid instead per unit of progress at its marginal value there, not by its flat
share ([goal terminals](#tier-2-event-driven-colony-model)). Every decision lists each goal
and its contribution (`goal_contributions`). The player's risk profile and pace choose rows of two tables
(`ForecastValuePolicy.RiskRows`, `PaceWellbeingWeight`), so the constants a decision used are the ones for that colony.
A goal adds value above the floors and never relaxes one: a goal whose every evaluated route breaks the urgent-zero or
break-risk floor the colony holds today is reported `Infeasible`, naming the floor.

The starting values are playtest constants ([kept constants](../contracts/kept-constants.md#policy-constants-3469)): a
whole Low goal (0.5) is worth five mood-days at the default wellbeing weight, a Normal goal (2) four times that and a High
goal (8) sixteen times; the deadline decays 0.8 per day to a floor of 0.1. So a High goal outranks a small wellbeing
sacrifice and a Low one does not.

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

- **Pawns.** Each pawn has a daily cycle derived per twelfth of the year
  (`PawnCycle.Year`, twelve entries) from its need rates: sleep and recreation
  hours are the day split `fall : fall + gain` on the rest or joy need, eating
  hours are meals a day (food fall over the nutrition of a meal) times the
  meal's service plus the mean approach, and the free labor hours are what
  remains. Daylight hours per twelfth (the gear census's seasonal read) split the
  free hours into outdoor-capable and indoor; outdoor work at night is possible
  at the glow speed (`LayoutLight.LightSpeed`). Temperature is not a speed
  factor (the game has none): the outdoor hours become one stationary thermal
  span against the pawn's comfort range, which the hypothermia and heatstroke
  integration prices. Skills gate tasks (`PawnCycle.SowGate`: devilstrand
  sowing needs Plants 10; an unread skill makes the task unavailable, reported)
  and set work speed. A rate the review did not observe leaves the cycle unknown
  and is named in `ColonyModelInput.Unknowns()`; timetables are not modelled.
- **Stocks and flows.** Arrays indexed by resource id: food, materials,
  research points, power, medicine. Production, consumption and labor are
  rates that change only at events.
- **Land.** A growing cell is one crop at a time; a devilstrand cell is not
  growing food.
- **Projects.** A small graph of research, sowing, growth, harvest, crafting,
  construction and hunting nodes with prerequisites (`ProjectNode`, `ProjectGraph`;
  the builders read a `ColonyModelInput`, and a gap leaves the node out and
  is named). The graph's order is the priority order of the labor allocation
  (`ColonyModelPolicy.Projects`). v1 allocation is a fixed order with no
  search: at each event the free hours of each pawn in this twelfth go to the
  ready nodes in that order, a node taking all the hours of every pawn able to
  work on it (research needs a read `ResearchSpeed`, sowing the Plants gate and
  a read `PlantWorkSpeed`, harvest a read `PlantWorkSpeed`; an unread stat makes
  the pawn unavailable and is named). Research, crafting and construction use
  all free hours; sowing and harvest only the daylight ones. Research makes
  speed × 0.00825 points per work tick (the pawn's `ResearchSpeed` stat times
  the difficulty's research factor, times the best bench's factor) and has one
  researcher per powered research bench the needs read counts, whatever the
  current project is; the other nodes make
  speed × 1 work per tick, with crafting and construction at speed 1 (named,
  the pawn model has no such stat). An unread bench count leaves the
  researchers unlimited and is named. The rates are constant between events, so the engine fires each
  node's completion (or, for a harvest, harvest) event at its exact finish
  time. A craft takes its ingredients (the alternative of each slot that is the
  garment's stuff, else the first) from
  a per-resource ledger when it starts, so a craft waits for the harvest that
  feeds it; the materials stock moves at completion. A harvest delivers cells ×
  `UnitsPerCell` of the crop's resource to materials, or cells ×
  `HarvestNutrition` to nutrition for an edible crop. One harvest per chain;
  regrowth is not modelled.
- **Hunting.** The wildlife census (`ColonyModelInput.Wildlife`, the food prey
  the hunt channels are offered on) gives one hunt-and-butcher node per race,
  last in the graph (`ProjectGraph.Hunts`). Its work is the hunt planner's
  pursuit work (`HuntSquadRules.Herds`: 7,500 ticks an animal, shortened by
  weapon reach, halved asleep); the hunt channels have no lead days, so the
  arrival is when that work is done. It takes daylight hours of the pawns
  with a hunting weapon and the Hunting work (`ModelPawn.Hunts`; unread, the
  pawn is left off and named). At completion the meat (each animal's
  `MeatAmount` × the meat's nutrition) joins nutrition and the race's butchery
  products (`AnimalRace.Butchery`: leather by `LeatherAmount`) join materials
  and the ledger, so a craft can take the leather. Butchering takes no labor
  (`butcher_work_unread`). A leather arrival beyond tier 1's one day is
  valued here, not in tier 1. Each addition to the ledger (a harvest's yield,
  a hunt's products, a craft's products) is a `ResourceArrival` on the
  trajectory (`ColonyTrajectory.Arrivals`, the `colony_model` event's
  `arrivals`, #3622): the day each stuff arrives and how much.
- **Crop growth.** The game's rate is 1 / (60,000 × growDays) per tick while
  not resting, × fertility × temperature × light (`Plant.GrowthPerTick`,
  `PlantUtility`). Growth stops outside the growth temperature band. Tier 2
  integrates it per twelfth (five days) of the tile's seasonal temperature,
  about ten segments per crop (`CropGrowthModel`): per calendar day it is
  (1 / growDays) × the fertility factor at the zone's mean cell fertility ×
  the temperature factor × the fraction of the day the plant does not rest
  (`PlantResting`). Light is the outdoor daylight, which is the resting window;
  an indoor zone under grow lights is not modelled. The Grow node completes
  when growth reaches `HarvestMinGrowth`, not full growth (devilstrand: 0.65 of
  22.5 grow days is about 27 calendar days at full rate, 41 for full growth).
  That also answers "will it mature before winter?": when a twelfth with no
  growth comes before the node completes, the node fails with
  `crop_will_not_mature:<node>`, nothing is harvested and the nodes behind it
  are `project_blocked`.
- **Food runway.** It is a floor in tier 2 as well, so a fiber field cannot
  starve the colony.

**Marginal value of progress.** Tier 2 gives each project the value of moving
its completion earlier:
`value of one hour today = d(value) / d(completion date) × days that hour advances completion`.
Tier 1 trades hours of research or sowing today against today's needs in
capability-days. Each review re-plans, so a failed crop or a cold snap changes
the next decision without any special case.

*As built (#3531).* `ProjectMarginalValue` runs the model once as it is and once per research and sowing node with one hour of work
injected at time zero (a pawn at speed 1: 0.00825 × 2,500 research points, 2,500 sow work); the difference of the two `ColonyValue`s
is the hour's value (`dV/dt`, the `colony_model` event's `dvdt`). `ColonyValue` is in capability-days, higher better: the apparel
a craft node makes is priced by the harm model as the harm of a day of raids at the worn outfit less the same with the product in
its slot, for the best wearer of each unit, times the days from the craft to the horizon (`apparel_harm_unassessed` when the
harm model or the gear option is unknown). The garments are walked in the order they are made (#3622): each unit goes to the pawn
it helps most against what that pawn wears by then, including a garment made earlier in the run, and is credited only that gain,
so a later, stronger garment counts only its upgrade over the earlier one and a weaker one made after it counts nothing; starvation spans walk each pawn's malnutrition severity through the game's stage table,
and a pawn whose severity reaches lethal costs the rest of the horizon plus `DeathTerminalValue`. A crop that cannot mature
changes nothing, so it is worth nothing. Tier 1 reads the value per progress unit (research point, cell sown) from
`ForecastCoupledContext.Investment`, keyed by `ForecastProjectProgress.Project`, and `ForecastValue.Of` writes
`-(work ticks × progress per tick × value per unit)` to the `Investment` term, which `coupled_selection` logs as the value's `investment`,
one `investment_contributions` row per allocation and key.
In a real review (`ProjectGraph.Standing`, #3559) the graph is the current research chain and each standing field's crop from sowing to
harvest. A crop's sowing waits on the research its plant def names (`CropChoice.Research`, the catalog's sowing prerequisites: Devilstrand,
TreeSowing), so an unfinished one is a research node the sowing depends on. Then the herds are hunted (above), and last come the garment
nodes (`ProjectGraph.Garments`, #3622): one craft node per reachable stuff and gear slot (`garment:<stuff>:<slot>`). A stuff is reachable
when the stock covers the garment's units of it, or a harvest or hunt node of the graph yields it. Within a stuff and slot the garment is
the gear planner's bill option (excluding slave, child, shield, psychic and smoke-pop garments) with the most damage avoided per hit
against the colony's expected raid (`GearArmorTerm`, the planner's own item score), ties by option id; none when no such option has armor.
The recipe is the one that makes it with that stuff as its first ingredient alternative, and the node waits on the research its bill
needs. A node needs no harvest or hunt prerequisite: it takes its stuff from the ledger when it starts, so a sourced garment is made
once its harvest or hunt has delivered. Before the run a node is dropped when another of its slot is known to arrive no later and has
at least its armor: that one's stuff is in stock, its research a subset of the other's and its work no more. Two sourced garments both
stay, and the arrival-order walk above credits only the later one's upgrade. With the stuff in stock the garment is made at once, so
a field of the same stuff adds nothing to it. No fibre stock is priced, and while the exposure table is a placeholder (#3543) the nodes
exist, the harm is `apparel_harm_unassessed` and the chain's investment credit is 0.

*Skills, practice and medicine (#3634).* Each pawn's skills are stocks keyed `skill/<pawn>/<skill>` (`SkillStock`), started from
the observed `SkillLearning` (a skill whose mechanics are unread is `skill_stock_unknown:<key>:<gap>`). Between events XP moves at
the practice it is paid (raw XP a day through the unsaturated factor, the excess over the saturation threshold at the saturated
factor; coarsened to days, so each day starts unsaturated) less its level's native decay (`DecayXp` per `DecayInterval` while
learning is allowed). Levels change at `RequiredXp` and the loss threshold by `ForecastSkill`'s level rule, and each change is dated
inside the interval (`ColonyTrajectory.Skills`). A practice slot is a living prisoner with a missing limb a material part installs
on, or with a material part to remove (`RoundsColonyModel.PracticeOf`); its surgery is that install recipe's work, learning callback
(#3632), speed stat and medicine (the removal is priced as the install). `ProjectGraph.Practice` adds one `practice:<prisoner>`
node per slot, last in the graph, so it takes the free hours the finite nodes leave: the first pawn with free hours and an enabled
stock of the skill, at its speed at the stored level (#3633 curve; `practice_speed_unknown` otherwise), is paid the recipe's raw XP
per tick and uses up a surgery's medicine per work it completes. No slot, no node; no medicine, no surgery. Medicine is held by kind,
best potency first, and both the surgeries and raid tending (`ColonyModelPolicy.MedicineTendsPerDay`, set from
`ColonyValue.RaidTreatmentsPerDay`: the harm model's raid wounds per pawn, batched at their mean severity under the native cap of 20
per treatment) use up the best kind first. `ProjectMarginalValue` perturbs each pawn's stock of a practised skill by an hour of that
practice's raw XP and each medicine kind by one unit, writing value per XP and per unit to `PerUnit` under the stock key and
`medicine/<kind>`. A slot a surgery could open (#3639, `ColonyModelInput.Openings`, one per material install recipe on a held
prisoner without a slot) is run again with its practice node added; the difference is written per slot under `practice_slot/<recipe>`,
and a value that leaves any stock unpriced writes `practice_slot_unpriced:<recipe>` instead. Tier 1 credits a slot-opening step that
key as project progress at its native success chance ([capability maintenance](../contracts/capability-maintenance.md#practice-slots-3639)).

*Raid tending (#3635).* `ColonyValue` subtracts the colony's raid harm over the run at the tending the run gives it. The run is cut
at each doctor's level dates and at each medicine kind's run-out (between events the stock falls at a constant rate, best kind
first, from `ColonyTrajectory.StartMedicine`); each span costs its days × raids a day × each raid's loss through `HarmLedger`
(bleeding, healing and infection, every raid priced over the whole horizon as the ledger counts it) with the wounds tended at once
(`raid_first_care_delay_unmodelled`) at the span's tend quality. That quality is `TendUtility.CalculateBaseTendQuality`: the best
doctor's MedicalTendQuality curve (#3633) at its stored Medicine level then × the medicine's `MedicalPotency`, clamped to its
`MedicalQualityMax` (without medicine the native `NoMedicinePotency` and `NoMedicineQualityMax`, read by the catalog); the bed's
offset and self-tending are not modelled. No pawn able to tend leaves the wounds untended; no raid to expect makes the term 0. A pawn
who may tend but has no curve is `medicine_skill_value_unassessed:<pawn>` and its stock is unpriced; when no pawn qualifies, the
harm model has no number (for example `exposure_cell_uncalibrated`) or a medicine's numbers are unread, the term is left out and every
skill and medicine stock is unpriced (`ColonyValueResult.Unpriced`): `ProjectMarginalValue` writes no `PerUnit` for an unpriced key,
so its marginal is a finding, not 0. Unchanged spans cancel between the perturbation runs, so a skill's per-XP value is the harm its
earlier levels avoid and a medicine unit's the harm of its later run-out.

*Skill and medicine credit in tier 1 (#3636).* Skill progress is capability, valued through forecast demand only
([skill learning](../contracts/skill-learning.md#skill-progress-is-capability)). An allocation whose learning evidence (tend, surgery,
cooking) moves a stock tier 2 valued gains `-(applied XP × value per XP)` under `skill/<pawn>/<skill>`: the XP is the gain
`ForecastSkill` applies for that callback (after learning factors and saturation), the unit the perturbation adds to the stock. Each
input an allocation consumes that tier 2 holds as a medicine kind is charged `+(units × value per unit)` under `medicine/<kind>`. Real
work and practice earn the same credit per XP; a key without a marginal adds nothing, so with no marginals the term is unchanged.

*Construction material (#3653, #3647 decision 2).* A delivery composed for a planned room whose role is in `PricedRoomRoles`
consumes its material, and the Investment term charges `+(units × value per unit)` under `material/<def>`, one
`investment_contributions` row each. Every other construction root, placed sites included, stays free until its role is priced
(#3650): a cost with no credit would stop it. Refuel and support draws on the same stack are other activities and are not
charged. The value per unit is the work to replace one unit (`ForecastValue.MaterialValueOf`), priced once per review before the
comparison (`Rounder.MaterialValuesAsync`, one `ListResourceSources` read per charged material, held in
`ForecastCoupledContext.MaterialValues`): the catalog's chop or mine work over its yield (`HaulModel`), plus one haul trip, the
nearest reachable source's distance × `PathMoveCardinal` ticks, over its load of min(stack limit, max(1, yield)). One work tick
is one capability-tick. A material with no source in reach (steel with no ore, components, blocks) or a failed read is priced at
`MarketValue / SilverGapRules.ValuePerWorkTick`, with the ledger finding `material_value_market:<def>`; with no market value
either it is charged 0 with `material_value_unpriced:<def>`. The shipped `PricedRoomRoles` is empty, so nothing is charged yet.

*Goal terminals (#3505).* `GoalPaths.Of` turns an unmet goal into a terminal of that graph: a research goal is the project's research
chain, a `WornCount` goal the sow, grow, harvest and craft chain of its stuff (the crop's research gates, the garment's recipe, one
craft per garment still missing). `ProjectMarginalValue` finds each goal's completion day (the last event of its terminal nodes) in
the base run and prices it with `ColonyValue.GoalValue`: the goal's worth on that day (importance, deadline decay continuous in the
day) for the share of the horizon it then stands, `worth × (horizon − day) / horizon`, as the apparel is worth the days it is worn.
An injected hour moves the completion earlier, and the difference is the hour's value for that goal, divided into a value per
progress unit. The value rises as a deadline passes before the completion day, since each day earlier also saves decay. A goal
whose terminal completes in no run (no recipe, no crop, no field, no recipe-research gate, a missing speed) is *unmodelled*: it keeps
the flat per-unit share of [Goal value](#goal-value) and has no ETA, and so has every goal kind but research and `WornCount`. The
estimated day is derived on each review from the base run (`GoalEta`, never stored), is the goal status's `EtaDay`, is shown on the
panel's Pursuing row, and is the `eta_day` of the `goal_review` row; the `goals` list of the `colony_model` event gives each goal's day
or the reason it is unmodelled.

**Devilstrand worked through.**

| Step | Cost | Source |
| --- | --- | --- |
| Research Devilstrand | 800 points | `ResearchProjects_1.xml` |
| Sow | Plants 10, 400 work per plant, fertility at least 0.5 | `Plant_Devilstrand` |
| Grow | Growth to `HarvestMinGrowth` (0.65 of 22.5 growing days), about 27 calendar days at full rate; 41 is full growth | `growDays`, `Plant.Resting` |
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

1. **Threat schedule** (`ThreatSchedule.Of`, #3539; decoded from the storyteller comps, [contract](../contracts/forecast-contracts.md#threat-schedule-3539)). Expected major threats per day come from the
   storyteller:
   - Cassandra: 4.6 days on, 6 off, about 1.5 big threats per cycle from
     day 11, so about one per 7.1 days.
   - Phoebe: about one per 16 days from day 13.
   - Randy: about one per 9 days.

   Every big threat before day 20 is a raid; after that, raids are about half.
   Size is `StorytellerUtility.DefaultThreatPointsNow`: a wealth curve plus
   points per colonist, × adaptation, × difficulty `threatScale`, × a
   days-passed ramp, clamped to [35, 10,000]. The model holds the observed
   points over the horizon (they grow with wealth in tier 2's model, #3531).
2. **Composition.** Points ÷ pawn-kind combat power gives raid size. The
   faction's pawn kinds give a distribution over about ten weapon bins, each
   with damage, armor penetration (damage × 0.015 unless the def sets it) and
   damage kind. `RaidCompositionRules.Of` (#3542) is the expected raid, not a
   sampled one: the hostile factions raid with equal weight; within a faction
   the Combat group makers compete by commonality; within a group a kind is
   drawn by selection weight among the kinds the faction's cost curve allows at
   these points, and the group's pawn count is the points over their weighted
   mean combat power. A kind's weapon is drawn from the defs its weapon tags
   select, by commonality (a kind with no tags fights with its race's own
   attacks). Weapons collapse into bins by armor category, ranged or melee and
   damage decile. `weaponMoney` and tech level do not filter the weapons in v1.
   No hostile faction, or a faction with no decoded Combat group, leaves the
   composition unknown with a finding; an unmatched weapon tag is a finding.
3. **Exposure.** Hits taken per colonist per raid. This is not derivable in
   closed form: target choice is a weighted random draw, and outcomes depend
   on cover, pathing and projectiles (see
   [combat predictability](combat-predictability-research.md)). It is
   calibrated from real-game `acceptance sweep` runs of the lab fixtures (no
   combat simulator is built), keyed by raid points ÷ defense capacity bin and
   the pawn's role, taken from its worn primary weapon (the defense layout is
   reported, not keyed, in v1). It is stored as ranges, so the uncertainty
   stays explicit. The harm model reads the committed table (`RaidExposure`)
   and duplicates no constants; see
   [raid exposure calibration](../testing/harm-exposure-calibration.md).
   **Calibrated today:** three cells, ratio 1 to 2 for gunners and ratio 2 to 4
   for gunners and melee fighters, from 20-seed sweeps whose means held in a
   second sweep. Every other cell (ratio under 1 or over 4, noncombatants, and
   melee at 1 to 2) is uncalibrated: the harm term is unknown with
   `exposure_cell_uncalibrated:<bin>/<role>` and the death floor is unassessed,
   so a colony outside those cells compares on the other terms only. Fixtures
   for the missing cells are #3561.
4. **Armor response, per pawn and weapon bin.** For each layer covering the
   part hit (`ArmorUtility.GetPostArmorDamage`, outermost first), effective
   armor = max(rating − penetration, 0). The hit is deflected with chance
   effective/2 and halved with chance effective/2, and a halved sharp hit
   becomes blunt and stops bleeding. Part probabilities come from body
   coverage. The result is a fixed array per (pawn, outfit): expected damage
   per hit and the chance a hit bleeds. A changed outfit swaps one array.
   `ArmorResponseRules.Of` and `OfOutfit` (#3540) are pure; the mirrored rules are in
   [kept constants](../contracts/kept-constants.md#mirrored-game-rules-armor-response-3540).
   The gear planner reads the same code in damage-per-hit units
   (`GearArmorTerm`, #3545; [loadout model](../contracts/equipment-upkeep.md#loadout-model)); the ledger reads capability-days.
5. **Consequences.**
   - Bleeding: 0.06 × severity per day for cuts, stabs and gunshots, until
     tended. Untended, it stops by itself after 1.5–3 days.
   - Blood loss downs a pawn at 0.60 and kills at 1.0.
   - Downed: pain shock at a pain threshold of 0.8 (about 64 HP of fresh
     injuries), or consciousness under 0.3.
   - Death: blood loss, a destroyed vital part, or total severity of
     150 × health scale.
   - Infection: 15% per wound, reduced by tend quality and the room.

   `HarmConsequences.Of` (#3544) prices these; the [contract](../contracts/forecast-contracts.md#raid-health-consequences-3544) and
   [mirrored rules](../contracts/kept-constants.md#mirrored-game-rules-raid-health-consequences-3544) say how.

   Downs and deaths are thresholds, so each raid carries a small distribution
   (hits × damage per hit), not only a mean. The CVaR in the currency reads
   that distribution.

The currency moves in the expected direction with armor: with the same
exposure, a garment that deflects more of the raid's hits lowers the harm term,
which the acceptance case `apparel/harm_armor_direction` (#3547) shows natively
(a devilstrand duster against a cloth one, in a colony inside a calibrated
cell). The direction is the native claim; the magnitudes are the unit tests'
(`ForecastValueTests`, `HarmConsequencesTests`) and the calibration's.

**Not modeled in v1:** adaptation feedback (a down lowers future threat points
by about 5–8 adaptation days, a death by 30), non-raid threats (infestations,
manhunters, ship parts), and fire. They are reported as unassessed terms.

## Search engine

The comparator has four nested levels:

1. **Portfolio search** over subsets of roots. Roots and tasks are indexed
   once per search; closures, compatibility, redundant pairs and portfolios
   are bitsets over those indexes, and a subset's keys are its bit words.
2. **Schedule search** per portfolio: depth-first over task × pawn × window ×
   service time × departure. A prefix is known by an order-free 128-bit hash
   that each placement adds to and its undoing subtracts from; a plan's text
   is formatted only to break ranking ties. It returns every complete plan.
3. **Every plan gets a schedule evaluation** by one pooled evaluator per
   search. Its scratch is reused; the sequences plans repeat are interned to
   integer ids; each per-pawn timeline (needs core, capacity, mood, roof,
   banded thoughts), shelter projection, support, maintenance, planned-room
   and relief result is memoized by exactly the rows it reads. The physiology
   call computes capacity without mood. The stock walk raises shortfall
   findings; no outcome carries trajectories.
4. A consequence vector per outcome, built with loops in a fixed order.

Stages. Each stage is proven by identical output on the recorded fixture
baselines, and benchmarked at a one-day horizon in .NET
([portfolio allocations](../testing/portfolio-allocations.md)) and in game.

1. **Stop wasted work** ([#3467](https://github.com/davidarcher/rimgovernor/issues/3467), shipped).
   No mood in the physiology call, no mood trajectory in the search, and each
   pawn's needs result cached per search by that pawn's own inputs.
2. **Integer ids, pooled evaluator, exact mood segments**
   ([#3468](https://github.com/davidarcher/rimgovernor/issues/3468), shipped).
   Ids are interned once per search; the evaluator's buffers are pooled; the
   stock walk raises findings, not trajectories; the mood timeline uses exact segments
   between real events, not 150-tick steps, which tier 2 needs; closures,
   compatibility, redundant pairs and portfolio keys are bitsets, and the
   schedule search knows a prefix by an incremental 128-bit hash. What
   remains per evaluated plan is the schedule search's own output (its
   allocation and plan records) and the memo misses.
3. **Evaluate lazily, budget by evaluations.**
   - Floors and cheap terms are computed first; a candidate that cannot beat
     the best on its bound stops early (branch and bound on capability-days).
   - The budget counts evaluations, deterministically, then is raised.

## Observation gaps

Native does not yet observe:

- the storyteller's cycle phase and the story watcher's adaptation days (the
  storyteller def, days passed and the hostile factions are read; the schedule
  uses the expected rate, not the phase, and adaptation is inside the observed
  raid points);
- bed healing (the pain shock threshold, health scale, healing and
  incoming-damage factors are read; per-layer apparel armor and body coverage
  come from the catalog);
- tile seasonal temperature by twelfth, growing-season bounds, and cell
  fertility for planned fields.

## Open questions

- Exposure calibration: which fixtures reach the uncalibrated bins (raid points
  ÷ defense capacity under 1 or over 4, and melee at 1 to 2) so their cells can
  be filled ([#3561](https://github.com/davidarcher/rimgovernor/issues/3561))?
- The tier-2 pawn cycle: is one derived day per pawn accurate enough, or does
  it need per-season variants (temperature, daylight)?
- Initial values for the policy constants.

## Milestones (proposed)

Each milestone has a GitHub issue.

1. [#3467](https://github.com/davidarcher/rimgovernor/issues/3467) Search stage 1 (no behavior change).
2. [#3468](https://github.com/davidarcher/rimgovernor/issues/3468) Search stage 2, including exact mood segments.
3. [#3469](https://github.com/davidarcher/rimgovernor/issues/3469) Capability-days and floors over today's one-day horizon (implemented; #3519 to #3522). This replaced the
   lexicographic ranking; the vector stays as explanation.
4. [#3470](https://github.com/davidarcher/rimgovernor/issues/3470) Tier 2: pawn cycle, stocks and flows, project graph, crop growth by twelfth (implemented: `Policy/ColonyModel/` with `RoundsColonyModel.Review`, the research and sowing providers `ResearchMethodCandidates` and `SowingMethodCandidates`, the `colony_model` event; the devilstrand payoff chain is wired by [#3559](https://github.com/davidarcher/rimgovernor/issues/3559) (its apparel value waits on the calibrated exposure table, #3543) and proven in play by `production/devilstrand-investment` ([#3560](https://github.com/davidarcher/rimgovernor/issues/3560)); `production/devilstrand-investment-starving` proves that investment never outranks the urgent floor).
5. [#3471](https://github.com/davidarcher/rimgovernor/issues/3471) Harm model v1: threat schedule, composition, armor response,
   consequences, with exposure calibrated from real-game sweeps (implemented, #3539 to #3547; three exposure cells are calibrated).
6. [#3472](https://github.com/davidarcher/rimgovernor/issues/3472) Search stage 3 and a larger budget.
