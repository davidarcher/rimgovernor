# Forecast value, horizon and harm (proposed design)

[Architecture](overview.md) · [Normal portfolio selection](portfolio-selection.md) · [ColonyForecast](colony-forecast.md) · [Combat predictability](combat-predictability-research.md)

**Status: milestones 3 and 5 (harm model v1) and the tier-2 model with its investment credit (#3531) are implemented; the larger search is proposed.**
Tier-1 selection ranks on the safety floors, then on capability-days ([Comparator](#comparator) describes what runs).
`ForecastValue.Of` (#3520, #3521) prices hunger, rest, food poisoning, thermal exposure, mood breaks and
wellbeing per outcome; `ForecastValue.Explain` (#3522) adds the floors; `coupled_selection` reports both ([policy constants](../contracts/kept-constants.md#policy-constants-3469),
[tables](../contracts/forecast-contracts.md#capacity-penalty-tables-and-per-pawn-capacities)); harm (#3546, [harm model](#harm-model)) is priced when the harm inputs are observed and the exposure table has a calibrated cell for the colony, else unknown with a specific finding (seven cells are calibrated today); investment is the tier-2 credit of research and sowing blocks and of the skill XP work applies, less the medicine it consumes ([Marginal value of progress](#tier-2-event-driven-colony-model)), 0 for any other outcome. The player's goals add a gain term ([Goal value](#goal-value), #3503).
The tier-2 model's input (`ColonyModelInput`, assembled per review by `ReviewColonyModel.InputOf` from the review frame, #3523) exists and names
each section the frame did not carry. The model runs once per review, before the comparison (`ReviewColonyModel.Review`): the engine, the project graph and the
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
evaluated outcome, and first: the search prices an outcome's floors, harm and goal terms when it evaluates it
(`ForecastValue.Stage`) and the rest of the ledger only when a comparison reaches the ledger or a consumer needs the
complete vector (`ForecastValue.Explain` is the eager form, the same ledger assembled at once; #3723). An outcome that
breaks a floor loses to any outcome that does not, and when both break one the smaller excess wins. A floor that cannot
be checked is not treated as passed: the review reports `break_floor_unassessed` (a mood break risk with no observed
threshold) and `death_floor_unassessed` (the harm term is unknown), and the outcome is ranked on what remains.

- No pawn's food or rest reaches zero, unless every feasible portfolio does.
  Then the least zero-time wins, as today. Zero-times less than a tick apart are the same
  floor, so summing residue never outranks the ledger (#3598).
- Expected raid deaths over the horizon do not rise above the baseline by more
  than `DeathRiskFloor`.
- Each pawn's break risk stays under `BreakRiskCap`. The risk is the expected breaks over the horizon, per pawn-day.

The floors replaced the top tiers of the old lexicographic order (urgent
exhaustion, depth, exposure). The urgent-zero floor also prunes the search, through the one
optimistic bound proven sound under this comparator ([#3724](https://github.com/davidarcher/rimgovernor/issues/3724),
`ForecastCoupledSelection`, the urgent-zero unserved-pawn bound `urgent_zero_unserved`).

Claim: a pawn none of whose need tasks lies in a portfolio's task closure keeps the baseline's
food and rest zero times in every plan of that portfolio, so every plan's urgent-zero excess is
at least that pawn's baseline zero time. Proof sketch: a plan allocates exactly the closure's
tasks; the evaluator builds a pawn's need choices from its own allocated need tasks alone, so
the unserved pawn's choices are empty, as in the baseline; the needs core
(`ForecastNeeds.NeedsCoreOf`) derives the food and rest timelines (`ZeroTicks`) from the
observed level, fall and threshold and those choices, and the grants, renewals and occupancy a
plan adds reach the mood and capacity terms, never the zero time; the urgent-zero time is the
maximum over the pawns, and the comparator decides at that stage whenever two excesses differ
by at least `UrgentZeroResolutionTicks`. So when some unserved pawn's baseline zero time exceeds
the current leader's excess by a tick, every feasible completion of the subset loses to that
leader at the first stage, and the subset is skipped before its schedule is searched (an
infeasible plan is never ranked). The search prunes against the current leader only; the
retained-finalist bound of #3720 plugs in at that comparison, with the weakest retained
finalist's excess in place of the leader's.

Domain: every plan of the subset must carry an assessed urgent-zero floor, since an unassessed
floor reads as excess 0 and would pass. The bound is therefore unavailable, the subset
evaluated within the #3722 allowance, the phase counting it `unbounded` and the selection
naming the reason in `BoundsUnavailable` as `urgent_zero_unserved:<reason>`, when the closure
holds a rest need task (`rest_service_in_closure`: sleeping a pawn can leave rest unassessed in
some plans), when a pawn's baseline zero time is unknown (`baseline_zero_unassessed`), when the
leader is infeasible or its floor unassessed (`leader_unassessed`), when the context values
goals (`goal_routes`: every evaluated outcome reports its goal routes, a skipped one would
not), and for a single construction root while the relief gate still collects unserved roots
(`relief_gate_order`). A served pawn is never bounded: its zero time can only improve with the
plan. No sound bound exists before evaluation for the death and break-risk floors (both read the
plan's mood and harm), for the ledger (gain terms, the shared-assessed-term comparison and its
tolerance) or for the tiebreaks, so those subsets are evaluated; a partial portfolio's own
value is never taken as a bound on its completions, and nothing is pruned on a heuristic. A
skipped subset is cached at the bound it was skipped at, so the confirm and the sweep meet it
again at theirs; in the cardinality sweep a prefix whose every completion leaves a pawn
unserved is skipped whole. The pruned search ranks what the unpruned one ranks, the same
selection, vectors and findings, reporting the skipped losers as `pruned:` and
`pruned_branch:` evidence rows ([portfolio selection](portfolio-selection.md#dependencies-and-search)).

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
| Investment | Capability added after completion, from tier 2: the work's progress (project progress, applied skill XP) times its marginal value, a gain, so a negative loss like the goals term; less the medicine the work consumes at its marginal value and the construction material a priced planned room's deliveries consume, at replacement labor (*Construction material* below); plus a closed planned bedroom's occupant mood gain to tier 2's horizon (*Bedroom credit* below, #3655). |

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

### Goal value

The player's goals ([player goals](player-goals.md)) enter the currency as a ledger term, `Goals`, a gain (a negative loss)
for the progress a schedule makes inside the horizon (#3503). A goal is worth `GoalImportanceValue[Low, Normal, High]`
capability-days when it is completed, times `GoalDeadlineDecay` per day past its deadline (never under
`GoalDeadlineFloor`); a schedule earns the fraction of that worth it progresses. Research is paid per point of the goal
project's remaining cost, a worn-garment goal per colonist a selected outfit change dresses in the garment (#3816), and a
garment delivery is not a unit of requested development. Other goal kinds have no forecast route and earn
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

### Pawn value

A colonist's value (#3825, `PawnValueRules`) is in capability-days per day: the work it adds less its upkeep. It
can be negative. The review computes it for every colonist once tier 2 has run and the colony has a layout plan
(`Findings.PawnValues`, the
`pawn_values` attr of `rounds_review`).

**Work shortfall.** The work the tier-1 selection could not schedule is a shared model (`WorkShortfallRules`,
`Findings.WorkShortfall`, the `work_shortfall` attr), per work type and speed stat, in ticks per day at speed 1.
Every candidate root belongs to one outcome, the roots sharing its `ExclusiveOutcome`. An outcome the selection serves
with none of its roots is unserved. It costs its cheapest root's unallocated work activities: the root's completions
and their prerequisites, each counted once over all outcomes. An activity's work type comes from its binding:

- a delivery or supply is hauling at no stat;
- a recipe is its giver's work type at the recipe's `workSpeedStat`;
- other work is its giver's work type at the stat its job driver applies (`DefinitionCatalog.WorkSpeedFacts`).

Its work is the cheapest option's ticks times that pawn's speed. The plan's idle pawn time over the horizon first takes
the work it has an option on. The rest is short. So work that waits on material is not short while a builder stands
idle. #3805 and #3809 build on this model.

**Work added.** A colonist's free hours (`PawnCycle.FreeHoursByPawn`: the day less sleep, meals and recreation, each
twelfth's cycle weighted by the investment horizon's days in it, known on a colony with no recreation building through
the walk's joy gain in the weather that allows it) fill
the shortfall rows of the work types it is capable of. Its fastest stat goes first, and each row is capped at its
size. One work tick is one capability-tick, the same price as `MaterialValueOf`. The rest of its time adds nothing.
The native needs read carries each colonist's side (`NeedsForecastPawn.Upkeep`): its value of every work speed stat, its move speed and carrying capacity (0
where the stat is disabled for it), the work types it is incapable of, the nutrition it eats a day and its mood target and
break threshold. A stat the game has not loaded makes the read throw.

**Upkeep.**

- Food: the nutrition it eats a day (`Need_Food.FoodFallPerTickAssumingCategory(Fed)`) times the work a nutrition
  costs. That is the cheapest researched meal recipe's work per nutrition made, plus its ingredients' growing at the
  cheapest researched human-edible crop's sow and harvest work (`DefinitionCatalog.FoodWork`). Without a meal recipe it
  is the crop's growing alone. Plus hauling it (#3849, `FoodHaulRules`, the `food_haul` part), at the colonist's own
  `MoveSpeed` and `CarryingCapacity`: the colonist is priced as hauling its own food.
  - The walks come from the planned layout (`FoodHaulRules.Legs`, set on the coupled context beside the bed). Each is
    the mean shortest walk over the plan's route cells, a diagonal step 1.41421. A field cell reaches the base over open
    ground through its nearest entrance. The legs are: a field cell to the nearest store of the harvest (a cold store
    when it rots, a plain one otherwise); the store (the field without one) to the nearest stove that cooks the meal;
    and the stove to the meal's place (the table, else a meal closet, else a store).
  - A cell costs `Pawn.TicksPerMove`: 60 over the pawn's move speed, clamped to 1..450. Terrain cost and the weather's
    slowing of unroofed cells are not priced.
  - A carry holds `Pawn_CarryTracker.MaxStackSpaceEver` units: the stack limit or the carrying capacity over the
    thing's volume, whichever is less.
  - Each trip is the walk there and back. Picking up and putting down take no tick. A haul job (the harvest to its
    store, the meal to its place) lasts at least `JobDriver_HaulToCell.MinimumHaulingJobTicks`, 30 ticks. The cook's
    ingredient fetch (`JobDriver_DoBill`) carries a run's ingredients, one carry at a time, with no floor.
  - The eater's own walk to its food and on to the table is its meal's time (the eating hours), not hauling.
  - A plan with no field hauls no harvest and names `food_field_unplanned`.
- Breaks: `MoodBreakModel.DaysLost` of a day held at its observed mood target under its minor break threshold.
- Bed: nothing while a colonist bed stands vacant (`SleepingRules.VacantColonistBed`). Otherwise it is the primary
  bed's build (`LayoutClusterRules.PiecePrice`, cheapest common stuff) over tier 2's horizon.

A fact the value needs and lacks throws `RequiredFactException`. That covers a stat the shortfall needs but the pawn's
read lacks, a giver, job or recipe the defs do not map, a colonist tier 2 gives no free hours, an unpriced break curve
or an unread bed. It also covers a meal with no planned stove, a planned walk no route joins, and a move speed or
carrying capacity the read lacks. The value never falls back to nothing.

**Awaiting the layout plan** (#3857). A colony with no layout plan yet is a real state: every colony's first reviews
run before its first plan derives (`ColonyPlan.ReviewFirstPlanAsync` runs after the review), and a derive that waits
on an unread fact or fails records none. Its food has no planned walks to haul over, so the review values no colonist:
the shortfall stands, `Findings.PawnValues` is empty and `PawnValuesAwaitingPlan` names `layout_plan`
(`PawnValueRules.AwaitingLayoutPlan`, the subject a building verdict awaiting the plan names). On the `rounds_review`
row `pawn_values` is null and `pawn_values_awaiting_plan` is `layout_plan`. No distance stands in for the plan.

**Not modelled**, named on every value: `comparative_advantage` (a newcomer freeing an existing pawn for other work),
`effects_on_others` (Abrasive and other traits'
mood effects on the colony) and, when the colony can
sow no human-edible crop, `food_source_not_crop`.

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
  hours are the day split `fall : fall + gain` on the rest or joy need, at the
  best target's gain or, with none, the need's `NeedRate.UnservedGainPerTick`:
  sleeping on the ground, and for joy the better of a walk and skygazing
  (`joyGainRate × 0.36 / 2500 ×` the tolerance factor, #3825). A walk needs
  `JoyUtility.EnjoyableOutsideNow`, so when it is the best gain it serves only
  the twelfth's `NeedRate.OutdoorShareByTwelfth` (#3825 D7) and the rest of the
  twelfth goes at the best building's gain (`ModelNeed.ShelteredGain`, 0 with
  none): the recreation hours are the share's mix of the two days. The share is
  the native read's, hour by hour through the twelfth's window: the outdoor
  temperature the game will read then (season, sun cycle and daily variation,
  `TileTemperaturesComp.OutdoorTemperatureAt`, plus an active condition's offset
  while it lasts) against the pawn's comfortable range, times the chance that
  neither rain of 0.25 or more (the biome's weather commonalities at that
  temperature as `WeatherDecider` weighs them, each held for its mean duration)
  nor an active game condition rules it out; a vacuum biome rules it out for a
  pawn concerned by vacuum. A day with no recreation hours names it
  (`joy_unserved:<pawn>`). Eating
  hours are meals a day (food fall over the nutrition of a meal) times the
  meal's service plus the mean approach, and the free labor hours are what
  remains. Daylight hours per twelfth (the gear census's seasonal read) split the
  free hours into outdoor-capable and indoor; outdoor work at night is possible
  at the glow speed (`LayoutLight.LightSpeed`). Temperature is not a speed
  factor (the game has none): the outdoor hours become one stationary thermal
  span against the pawn's comfort range, which the hypothermia and heatstroke
  integration prices. Skills gate tasks (`PawnCycle.SowGate`: devilstrand
  sowing needs Plants 10) and set work speed. A rate the review did not observe
  leaves the cycle unknown and is named by a finding; a frame that lacks a section
  of the input yields no input (`ColonyModelInputRead.Unready`); timetables are not modelled.
- **Stocks and flows.** Arrays indexed by resource id: food, materials,
  research points, power, medicine. Production, consumption and labor are
  rates that change only at events.
- **Land.** A growing cell is one crop at a time; a devilstrand cell is not
  growing food.
- **Projects.** A small graph of research, sowing, growth, harvest, crafting
  and hunting nodes with prerequisites (`ProjectNode`, `ProjectGraph`;
  the builders read a `ColonyModelInput`, and a gap leaves the node out and
  is named). The graph's order is the priority order of the labor allocation
  (`ColonyModelPolicy.Projects`). v1 allocation is a fixed order with no
  search: at each event the free hours of each pawn in this twelfth go to the
  ready nodes in that order, a node taking all the hours of every pawn able to
  work on it (research needs a read `ResearchSpeed`, sowing the Plants gate and
  a read `PlantWorkSpeed`, harvest a read `PlantWorkSpeed`; an unread stat makes
  the pawn unavailable and is named). Research and crafting use
  all free hours; sowing and harvest only the daylight ones. Research makes
  speed × 0.00825 points per work tick (the pawn's `ResearchSpeed` stat times
  the difficulty's research factor, times the best bench's factor) and has one
  researcher per powered research bench the needs read counts, whatever the
  current project is; the other nodes make
  speed × 1 work per tick, with crafting at the recipe's `workSpeedStat` of the
  crafter (1 when it names none, #3868). Construction is not a graph node: its
  timing is the native schedule's (#3890). An unread bench count leaves the
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

#### Restarting the long-term engine

The ordinary baseline and all marginal perturbations now enter through
`ColonyModelState.Observed`; carried invocations use the same engine with an
explicit start day and common final day. Project remaining work, prerequisite
status, paid versus held inputs, fractional resource stocks and skill XP are
independent state, rather than a collection of marginal perturbations.
See the [carried-state contract](../contracts/forecast-contracts.md#carried-long-term-model-state)
for the post-event boundary convention and unavailable-state behavior.
This is the engine boundary; candidate extraction, initial recovery and
remaining-interval valuation must be composed before candidate ranking changes.

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

*Skills and medicine (#3634, #3344).* Each pawn's skills are stocks keyed `skill/<pawn>/<skill>` (`SkillStock`), started from
the observed `SkillLearning` (a skill whose mechanics are unread is `skill_stock_unknown:<key>:<gap>`). No tier-2 work pays XP: between
events a stock falls by its level's native decay (`DecayXp` per `DecayInterval` while learning is allowed), and a level is lost at the
loss threshold by `ForecastSkill`'s level rule, each change dated inside the interval (`ColonyTrajectory.Skills`). Medicine is held by
kind, best potency first, and tending (`ColonyModelPolicy.MedicineTendsPerDay`, set from `ColonyValue.TreatmentsPerDay`: the
harm model's raid wounds per pawn, batched at their mean severity under the native cap of 20 per treatment, plus the illness term's
tends a day, `IllnessTerm.TendsPerDay` (#3829), at the tending the run starts with, the best tender's day-0 quality with the
medicine held; `TendUtility.DoTend` uses up one medicine a treatment and tends a disease alone) uses up the best kind first. The
stock-target bill crafts medicine (`ColonyModelPolicy.MedicineCraft`, #3868, [stock arrival](../contracts/forecast-contracts.md#stock-arrival-3868)):
below the target the best-potency kind a source makes is crafted at the full rate until its ingredients run out, at the target what
tending uses, above it nothing (`ModelEventKind.MedicineCraft` marks reaching the target and running out of ingredients). The routine trade's purchases join it
(`ColonyModelPolicy.MedicineTrade`, #3907): on the medical reserve's latch the cheapest kind arrives at the trader comps' rate
(`ModelEventKind.MedicineTrade` marks the latch starting and stopping, the silver spent, a comp's visits beginning and the best
kind, which the latch does not count, running out). `ProjectMarginalValue` values one XP as one level more over the XP that level takes: it runs each pawn's stock of each skill
again one stored level higher, its XP toward the next level kept (`SkillStock.Raise`), and divides the value difference by the native
`RequiredXp` of the observed level (`SkillTrack.NextLevel`). A stock at the top level is worth 0 per XP. Only a stock the value
reads is run (`ITrajectoryValue.Reads`, #3664): for `ColonyValue` that is each doctor's stock of its tend quality curve's skill
(Medicine) while raids or disease incidents are expected, and each fighter's Melee and Shooting stock while raids are expected
(#3852: a pawn among the sparring ring's or the training range's `PracticeTrainees`; unread trainees or a power curve shorter than the
skill's levels raise `RequiredFactException`); any other priced stock is worth 0 per XP by construction and is written without a run. Each medicine kind is
perturbed by one unit. The values per XP and per unit go to `PerUnit` under the stock key and `medicine/<kind>`; the `colony_model` event logs each stock's
value per XP (`skill_per_xp`), and `production/skill-marginal` (#3663) proves a live review prices the staged colonist's Medicine stock.

*Anomaly knowledge (#3843).* A knowledge project (a `ResearchProjectDef` with a `knowledgeCategory`) is a `Knowledge` node of the
knowledge points still to give (its `knowledgeCost` less the progress the research read gives from `anomalyKnowledge`). Each knowledge
category's point funds one project (`ResearchManager.ApplyKnowledge`, `ProjectGraph.KnowledgeProject`): its slot's project; for an empty
slot the project the research Need funds into it first (`ResearchRules.KnowledgeFunding`); while it funds none, its overflow category's
(`KnowledgeCategoryDef.overflowCategory`, from the catalog: Advanced into Basic). `ProjectGraph.Standing` adds those projects with their
unfinished prerequisites. Study is not in the run (its yield is the containment credit's, #3811), so a knowledge node takes no labor and
completes only once its points are given: it is run again with all of them given at time zero, and a point is worth its share of what
finishing the project now adds. The value goes to `PerUnit` under `knowledge/<category>` (`ProjectGraph.KnowledgeKey`) for every category
the colony has; a category that funds no project, or a project whose points are all given, is worth 0 a point. Without Anomaly the research
read has no knowledge slot and there is no category. A study's yield of a category is priced at `Investment[KnowledgeKey(category)]`.

What finishing the project adds is what it unlocks, valued by the terms that value the rest of the run (#3848). The catalog gathers a
project's unlocks the way `ResearchProjectDef.UnlockedDefs` does (`ResearchProjectFacts.Unlocks`): recipe products, things, terrain,
sown plants, surgeries and psychic rituals that name it. The gear census lists only bills available now, so the gear read adds the bill
options of apparel that only unfinished knowledge projects keep from the colony (`GearObservation.KnowledgeBills`,
`ColonyGearReads.KnowledgeBillOptions`). Each is priced at its best crafter's quality odds from the census's locked bill row
(`GearSnapshot.LockedBills`, #3850); a def no free colonist could craft adds no option. Their garment nodes wait on the knowledge node, picked apart from the garments that can be made
now (`ProjectGraph.Garments` groups by stuff, slot and the research a bill still waits on), so the apparel term prices the armor they add.
No other term prices an unlock (weapons, serums, buildings, floors, surgeries, rituals): each unlock of a funded project that is not a gear
option is named `knowledge_unlock_unmodelled:<project>:<def>` in the graph's findings, which `ProjectMarginals.Findings` carries. An
apparel unlock is a gear option even without armor (a flare pack): the apparel term prices it at what its armor avoids. In vanilla
Anomaly the garments with armor are BioferriteExtraction's cultist masks.

*Tending (#3635, #3829).* `ColonyValue` subtracts the colony's raid and illness harm over the run at the tending the run gives it.
The run is cut at each doctor's and each fighter's level dates and at each medicine kind's run-out (between events the stock falls at a constant rate,
best kind first, from `ColonyTrajectory.StartMedicine`); each span costs its days × the harm term a day through `HarmLedger`: raids a
day × each raid's loss (bleeding, healing and infection, every raid priced over the whole horizon as the ledger counts it) plus the
illness term's loss (`IllnessForecast`, a disease tended by the same quality as a wound), with the wounds and illnesses tended at once
(`raid_first_care_delay_unmodelled`) at the span's tend quality. That quality is `TendUtility.CalculateBaseTendQuality`: the best
doctor's MedicalTendQuality curve (#3633) at its stored Medicine level then × the medicine's `MedicalPotency`, clamped to its
`MedicalQualityMax` (without medicine the native `NoMedicinePotency` and `NoMedicineQualityMax`, read by the catalog); the bed's
offset and self-tending are not modelled. Each span's raid loss is at the defense shift of the fighters' stored levels then
(#3852), the sparring ring's and the training range's path: `DefensePointsPerDps` × the trainee's power (MeleeDPS or ranged defense
DPS) at that level less at the observed one, passed to `HarmLedger.Of`. No pawn able to tend leaves the wounds untended; no raid and no disease incident to expect
makes the term 0, and an unassessed illness term throws a `RequiredFactException` (`illness_schedule`, `illness_harm`). A pawn
who may tend but has no curve is `medicine_skill_value_unassessed:<pawn>` and its stock is unpriced; when no pawn qualifies, the
harm model has no number (for example `exposure_cell_uncalibrated`) or a medicine's numbers are unread, the term is left out and every
skill and medicine stock is unpriced (`ColonyValueResult.Unpriced`): `ProjectMarginalValue` writes no `PerUnit` for an unpriced key,
so its marginal is a finding, not 0. Unchanged spans cancel between the perturbation runs, so a skill's per-XP value is the harm its
next level avoids over the XP that level takes, and a medicine unit's the harm of its later run-out.

*Skill and medicine credit in tier 1 (#3636).* Skill progress is capability, valued through forecast demand only
([skill learning](../contracts/skill-learning.md#skill-progress-is-capability)). An allocation whose learning evidence (tend, surgery,
cooking) moves a stock tier 2 valued gains `-(applied XP × value per XP)` under `skill/<pawn>/<skill>`: the XP is the gain
`ForecastSkill` applies for that callback (after learning factors and saturation), the unit of the level's `RequiredXp`. A practice
session (#3852) gains the same under its pawn's practised skill, the XP `ForecastMaintenancePolicy.SessionXp` applies from its native
callbacks within the day and the read's validity (an unread cadence or learning raises `RequiredFactException`). Each
input an allocation consumes that tier 2 holds as a medicine kind is charged `+(units × value per unit)` under `medicine/<kind>`. Real
work of every kind earns the same harm-priced credit per XP; a key without a marginal adds nothing, so with no marginals the term is unchanged.

*Work speed of a skill (#3912).* Besides the harm a level saves, one XP of any skill is worth the work its level speeds up
(`SkillValue.PerXp`, added to `Investment[skill/<pawn>/<skill>]` for every learning, practice and classroom XP). It is the
pawn's work added (`PawnValueRules.WorkAdded` against the outcome's shortfall) at the work speeds one more stored level gives it, less
its work added now, over the days that level lasts inside the horizon (`ProjectMarginals.SkillRaises`: the stock raised one level
and advanced by decay, `SkillStock.RaisedLasts`), over the XP the level takes. The raised speeds are the native read
(`PawnUpkeepRead.SkillSpeeds`, `NativeSkillStat.OneLevelUp`): the stat with the skill one stored level higher, exact for a
stat whose later pipeline is linear; a stat the read cannot hold exact fails the read, never a quiet zero. A skill no needed work
reads gains nothing by its level, so it adds nothing. The pawn's free hours are `PawnCycle.FreeHoursByPawn`; a pawn without
them, upkeep or raise throws `RequiredFactException`.

*Construction material (#3653, #3647 decision 2).* A delivery composed for a priced planned room (`ForecastCoupledContext.Priced`: its role has a
credit in `RoomCredits`, #3796) consumes its material, and the Investment term charges `+(units × value per unit)` under `material/<def>`, one
`investment_contributions` row each. Every other construction root, placed sites included, stays free until its role is priced
(#3650): a cost with no credit would stop it. Refuel and support draws on the same stack are other activities and are not
charged. The value per unit is the work to replace one unit (`ForecastValue.MaterialValueOf`), priced once per review before the
comparison (`ColonyReview.MaterialValuesAsync`, one `ListResourceSources` read per charged material, held in
`ForecastCoupledContext.MaterialValues`): the catalog's chop or mine work over its yield (`HaulModel`), plus one haul trip, the
nearest reachable source's distance × `PathMoveCardinal` ticks, over its load of min(stack limit, max(1, yield)). One work tick
is one capability-tick. A material with no source in reach (steel with no ore, components, blocks) or a failed read is priced at
`MarketValue / SilverGapRules.ValuePerWorkTick`, with the ledger finding `material_value_market:<def>`; with no market value
either it is charged 0 with `material_value_unpriced:<def>`. The shipped credits (`PlannedRoomCredits.Shipped`) price `Bedroom` (#3655), `Barracks` (#3658), `Lab` (#3800), `Throne` and `Suite` (#3807), `Kitchen` and `Butchery` (#3802), `Storage`, `MealCloset`, `Reserve` and `Yard` (#3798, the storage credit below), `Morgue` and `Tomb` (#3806), `Worship` (#3808), `Pen` (#3812), `DeathrestChamber` (#3810), `Hospital` (#3804), `ContainmentCell` (#3811), `IsolationRoom` (#3844), `SparringRing` (#3813), `TrainingRange` (#3846), `Prison` (#3805), `Classroom` (#3809), and `Dining` and `Rec` (#3803).
Each fueled source with a steady burn in a shelter (#3673, #3679: a campfire or torch, one of `ForecastShelterPlan.Sources`
whose native supply is `HeatSupply.Burning`, with its `Fuel` and `FuelPerTick`) is charged, whatever the room's role, `FuelPerTick × (horizon − complete)` units
of its own fuel under `material/<fuel>` when the outcome completes the shelter inside the horizon, on a row of that source with no
pawn; each fuel is one of
`ForecastValue.ChargedMaterials` and priced the same way. The climate watts model does not charge fuel.

*Garment credit (#3816).* A selected commitment that overlays a garment on a colonist's worn outfit (a craft's delivery, a
retained bill's completion) is credited, under `apparel/<pawn>/<garment>`, the apparel thoughts the outfit then gives less the
worn outfit's (`ForecastApparel.MoodOf` over the pawn's native stage rows, a title's or role's required apparel by the
unmet-requirement thought it lifts, #3836, and a garment at a title's minimum quality by that thought, #3841, a crafted garment's
quality being its crafter's odds, each mood outcome priced and weighted by its chance, #3850), priced as a bedroom's delta below from the change's
tick to tier 2's horizon. A pawn's garments are taken in delivery order, each credited the gain its delivery adds; the break
shifts of every outfit the alternatives can reach are integrated before the search (`ForecastApparel.BreakShifts`, at most 64
outfits a pawn). A pawn without an apparel tracker wears nothing to credit; one whose outfit, stage row, mood, tick or shift is
unread throws `RequiredFactException` (#3835).
Armor is the harm term's ([harm model](#harm-model)), not a credit.

*Bedroom credit (#3655, #3647).* A planned bedroom the outcome serves (`PlannedRoomCredits.ServiceDays`, logged on the outcome
as `ServedRooms`; #3796, the day its reconcile owes nothing, furniture included) is credited through the room credit seam
(`ForecastHousing.BedroomCredit`, `PlannedRoomCredits.Credit`), in the Investment term under `bedroom/<room>/<pawn>`
(`barracks/<room>/<pawn>` for a barracks), the mood gain of the colonist
the bedroom step would move into it (`SleepingRules.PlannedBedroomOccupants`, `ForecastCoupledContext.BedroomOccupants`, room →
pawns: the step's order, the unhoused taking the vacant bedroom beds, then the planned barracks' bunks in id order, then the owed
rooms in plan order, a bedroom its colonist and that colonist's waiting partner, a barracks one per missing bed slot), else the one
colonist owning a bed in the predicted room: a standing bedroom's wall upgrade (#3657) credits its owner the swap's predicted stage
against the stage it sleeps at now. A planned barracks credits each pawn in its beds under the same key, at the barracks
thought (`slept_in_barracks` at the predicted stage against the current bed thought; an ascetic gains nothing and loses any
Ascetic thought it has, #3658). The pricing is `ForecastHousing`, which the priced merge and priced moves reuse:

- *Mood delta* (`BedroomMoodDelta`): for a non-ascetic, `slept_in_bedroom` at the prediction's Impressiveness stage less the
  current bed thought (`slept_in_bedroom` or `slept_in_barracks` at the owned bed's room stage, none for a prisoner bed, plus
  `slept_outside` when that room is psychologically outdoors; none without a bed). For an ascetic, the `ascetic` thought at the
  predicted stage less the one observed now (the stage of a room it owns alone; nothing in a shared barracks). Every offset is the
  game's own `NeedsForecast` thought-stage read. Where the pawn sleeps now is `NeedsForecastPawn.Sleep` (#3840, `NeedSleep`:
  the bed `RestUtility.FindBedFor` gives it, else the ground at its cell, read natively with the cell's room, the bed's costs,
  Comfort, rest gain a tick and the cell's temperature).
- *Bed terms* (#3840, `BedMoodDelta`, `SleeperOf`), every one `Toils_LayDown.ApplyBedThoughts` and the rest tick apply, against
  the occupant's planned bed (`ForecastCoupledContext.PlannedBeds`, the Build sites of a sleeping bed def in the room in id order,
  each `SleepingBedFacts` read from the catalog: costless, `BedRestEffectiveness` with the stat's valueIfMissing, `Comfort`):
  `slept_on_ground` lost (or gained) as the planned bed is costed against where it sleeps now; the comfort need's thought
  (`need_comfort` rows, `ThoughtWorker_NeedComfort`) over the layout occupancy day at the planned bed's Comfort less the current
  one's (`ComfortMood`, the NeedDef curve `ForecastCoupledContext.ComfortCurve`); the sleep temperature memory (`slept_in_cold`,
  `slept_in_heat` against the pawn's `SleepComfortableMin/Max`) relieved now and taken at the room's temperature (its shelter
  plan's forecast at the horizon, else the outdoors; `PlannedSleepTemperature`), both shifts integrated as break shifts; and the
  capability-days the faster rest frees (`RestDaysFreed`: the steady day spends fall / (fall + gain) asleep). A pawn without a
  rest need never lies down and changes none of them. Darkness and rain give no sleeping thought, so they are no term.
- *Gain per day* (`GainPerDay`): `WellbeingWeight × delta / 100`, plus the break days a day the tier-1 target mood shifted by
  `delta / 100` saves against the unshifted one (`MoodBreaks.DaysLost` on the assessed breaks' `TierTicks` and `Shifted`, the
  timeline integrating each shift with its own clamp at 0 and 1), plus the rest's freed capability-days.
- *Credit*: the gain × (H − service day), H tier 2's `HorizonDays` (`ForecastCoupledContext.InvestmentHorizonDays`); a gain, so negative.

A room whose shape closes no room (native answers no prediction row) earns nothing. An occupant whose stage, sleep
place, observed thoughts or mood is unread throws
`RequiredFactException` (#3835), as does an unread bed term (an occupant's planned bed, a memory or comfort row, the comfort
curve, the rest fall, the room's temperature; #3840). A bedroom with no occupant, or one not served before tier 2's horizon
(no free builder, or owed work no option can do), pays its material with no credit and waits.

*Storage credit (#3798, epic #3650).* A served planned storage room, meal closet or yard is credited the stock it keeps from
deteriorating (`ForecastStorage.StorageCredit`), under `storage/<room>/<thing>` (`meal_closet/`, `yard/`); a reserve keeps no
store and earns nothing.

- *Stock*: `StoreRules.PlannedStoreStock` over the stockpile review's request (`ReviewFacts.PlannedStoreStock`,
  `ForecastCoupledContext.StoreStock`): the stranded loot rows whose accepting groups include a zone of the role of a store
  sited on the room's region (a shelf in one), and the room's interior cells, one stack each.
- *Per stack*: native reads each stack's `LootItem.Deterioration` (MaxHitPoints, `FinalDeteriorationRate`, the
  StatPart_EnvironmentalEffects inputs at its cell). The part's factor is rebuilt roofed and indoors (a yard: unroofed open
  air), terrain and edifice held, and the rate rescaled by it. The hit points a day are rate / 36 × the visits a tick ×
  ticks a day (none below 0.001), as `DeterioratedAt` reads them. The gain is the hit points saved × count ×
  `MaterialValues[def].PerUnit` / MaxHitPoints.
- *Capacity and sharing*: each room keeps its largest gains up to its cells; rooms take stacks in id order, each stack once. A food
  stack hunger already debits (a needs food target with a deterioration read) is left to it.

No stockpile request (the stockpile review did not run: no stranded stock to place) is the no-stock state and earns nothing; a
stack no colonist reaches is not stock a store keeps, and a material with no market value (`MaterialValueBasis.Unpriced`) saves
nothing. An unread material value, rain, visit rate or environmental part throws `RequiredFactException` (#3835). A yard credits
only stock its open cells protect more than where it lies, so in practice nothing.

*Kitchen and butchery credit (#3802, epic #3650 P1).* Both are priced by the food poisoning their room changes
(`KitchenCredits`). A meal's first poisoning roll (`CompFoodPoisonable.Notify_RecipeProduced`) is its bench room's
`RoomStatDefOf.FoodPoisonChance`; the planned room's is `PlannedRoomCleanlinessRules.FoodPoisonChance`: the stat's curve
(`FilthModel.FoodPoison`) at the room's steady-state cleanliness ([facilities](facilities.md)), at its dirty value for the
share of the day its stations' filth stands and its clean value otherwise, the roomless score for a None room. One
poisoning is priced as the ledger prices an expected case (`ForecastValue.PoisoningPrice`, the poisoning term below).

- *Kitchen*, under `kitchen/<room>/food_poisoning`: the planned kitchen's own cooking (#3835): its meals a day
  (`PlannedRoom.Meals`, the layout group's exposed stations' meals) made at the stat's roomless chance
  (`FilthModel.FoodPoisonRoomless`, its stations standing with no room) less made in the planned kitchen, times the price.
- *Butchery*, under `butchery/<room>/<kitchen>`: per planned kitchen, its chance with the butchery's filth runs added to its
  own less its chance with its own, times the kitchen's meals and the price.

A kitchen with no planned meals earns nothing, and one whose shape closes no room (native answers no prediction row) leaves
its stations roomless and saves nothing. One whose layout room or filth model is unread, or whose price is unread, throws
`RequiredFactException` (#3835). The dirt pawns track in is not priced (the layout's traffic is
unknown), nor a floor laid in a standing kitchen with no ring left (it is not a served room).

*Throne room and suite credit (#3807).* A planned throne room or suite is credited, through the same seam
(`ForecastHousing.RoyalRoomCredit`), under `throne/<room>/<pawn>` (`suite/<room>/<pawn>`), with the mood its titled colonist
stops losing to the title's room thoughts. The room's colonist is `ForecastCoupledContext.RoyalRooms`
(`RoyalRoomRules.PlannedRoyalRooms`): a suite's occupant from `RoomQualityRules.PlannedSuiteOccupants` (a standing suite's sole
owner, else the suite claims in order over the vacant planned suites) with that colonist's title's `BedroomMinArea` and
`BedroomMinImpressiveness`; the throne room for the throne holder (`ThroneNeed`) with `ThroneNeed.Held`, the throne-room
requirement of the most senior title held now. The relieved mood is minus the def offsets (native `NeedsForecast` rows
`title_bedroom_unmet` and `title_no_bedroom` for a suite, `title_throne_unmet` and `title_no_throne` for a throne room, from the
thought defs by worker class) of those of the room's two thoughts the colonist has now. The credit applies only when the
prediction meets both requirements (`Cells >= MinArea`, `round(Impressiveness) >= MinImpressiveness`, the game's
`RoomRequirement_Area` and `RoomRequirement_Impressiveness`); a partial match earns nothing. The gain per day and credit are the
bedroom's (`GainPerDay` × (H − service day)). A room serving no title (no Royalty, no titled colonist, no throne owed), or one
whose colonist has neither thought now, or whose shape closes no room, is credited 0; unread observed thoughts or an unread mood throws
`RequiredFactException` (#3835).

*Greedy and Jealous suite credit (#3826).* A suite also relieves its occupant's trait bedroom thoughts, titled or not:
`RoyalRoomRules.PlannedRoyalRooms` gives every suite occupant a row (an untitled one with no requirement, 0 and 0), and the
suite's delta is the sum of every thought it changes, title and trait alike, through one `GainPerDay`. Greedy
(`ThoughtWorker_Greedy`, native rows `greedy`) is its stage at the predicted Impressiveness stage + 1 less the one held now;
Jealous (`ThoughtWorker_BedroomJealous`, rows `jealous`) is held after the move when a room another pawn owns
(`NeedsForecastPawn.RivalRooms`) beats the predicted Impressiveness by a tenth of its absolute value, less the one held now.
A colonist whose rows are all 0 (no trait, no title) is credited 0 without its thoughts read. For a Greedy or Jealous
colonist an unread observed thought list, closed room, stage or stage row is a `RequiredFactException` (P10 as revised).

*Laboratory credit (#3800).* A planned lab the outcome serves is credited through the same seam (`ForecastLaboratory.LaboratoryCredit`)
under `lab/<room>/<pawn>`: the research speed the closed room gives the bench. `BenchSpeedModel` (the bridge's
`DefinitionCatalog.ResearchBenchSpeeds`, from each research bench def's `ResearchSpeedFactor` statBases, the stat's parts and its
minValue) evaluates the bench in the closed room, indoors, at the prediction's role and its `PlannedRoomCleanlinessRules.Steady`
cleanliness. A research bench standing in the room is compared with its observed factor (`NeedsForecastRead.ResearchBenches`) and
gains the research its researchers do at it; with none standing, the room's planned bench (`LaboratoryBench`) is compared with
that bench placed outdoors (no proper room, unroofed) and gains all of the schedule's research. Per day each research allocation
earns its points over the horizon's days / its bench's factor now × the factor gain × `Investment`'s value of a point of its
project. No research allocation of a valued project earns nothing. The temperature part and the bookcase reading bonus are held at
1 and facility offsets (multi-analyzer) are not modelled. A room whose shape closes no room earns nothing, and a predicted
role `RoomRole` does not name (a modded one) is a proper room of no vanilla role. An unread filth model, an unread stat part,
or a research bench without a bench row or model throws `RequiredFactException` (#3835).

*Morgue and tomb credit (#3806).* A planned morgue or tomb the outcome serves is credited through the same seam
(`ForecastCorpses.MorgueCredit`, `TombCredit`) under `morgue/<room>/<pawn>` (`tomb/<room>/<pawn>`): the observed-corpse moods
the room removes by taking the corpses that lie in sight. The facts ride on the needs read: `NeedsForecastRead.Corpses` (each
spawned, unfogged corpse that raises an observed-corpse event, `Corpse.GiveObservedHistoryEvent`, so none in storage and none of
a fresh ritual kill: its rot stage, whether it was a colonist, and per colonist the ticks ago of each observer pass that saw it
from the corpse's cell) and per colonist `CorpseThoughts` (the memories its ideo gives for `ObservedLayingCorpse` and
`ObservedLayingRottingCorpse`, read from the `PreceptComp_SelfTookMemoryThought` comps keyed by those events with their
`onlyForNonSlaves` and `minExpectation` gates, each built with `ThoughtMaker.MakeThought` and its `MoodOffset` taken only when
`CanGetThought` allows, so a nullifying trait or precept gives 0, with the def's `DurationTicks`, `stackLimit` and
`stackedEffectMultiplier`) and `SightingTicks` (how long its sighting record covers). The record is the native `SightingLog`
(`TrafficState.Sightings`): a prefix on `PawnObserver.ObserveSurroundingThings` logs the tick and cell of each pass a free
colonist observes from (awake, sighted, with mood, the game's own early-outs). A pass sees a corpse within 5 cells in line of
sight (`PawnObserver.ObservationRadius`, `PossibleToObserve`); the read replays that test on each logged cell.

The renewal model, from the game's mechanics: `Need_Mood.NeedInterval` runs every 150 ticks and the observer looks every
4 ± 1 intervals, so a colonist observes at most once every 450 ticks. A sighting gives each memory the event raises;
`Thought_MemoryObservation` merges with the memory of the same corpse and renews it instead of adding one, so one corpse's memory
is active from a sighting for its `DurationTicks` (half a day in Core) or until the next sighting renews it. The colonist's
routine is taken to repeat: the whole days the record covers are one cycle, a memory running past the cycle's end wrapping to its
start. At each moment the n active memories of one thought count as `ThoughtHandler.MoodOffsetOfGroup` with
`MemoryThoughtHandler`'s stack limit does, `offset × Σ_{i<min(n, stackLimit)} stackedEffectMultiplier^i`; the colonist's corpse
mood is that sum's mean over the cycle, summed over the thoughts (`ForecastCorpses.CorpseMood`). Two fresh corpses by the
kitchen door seen once a day cost a Core colonist `-4 × (1 + 0.5)` half the day, -3.

The forecast's mood timeline renews no observation memory, so it already holds the colony with the corpses out of sight; the
room's gain a day is minus `ForecastHousing.GainPerDay` of the corpse mood (wellbeing and the break days the timeline shifted by
`mood / 100` costs), credited × (H − service day). A tomb takes the colonist corpses; a morgue takes the strangers', and the
colonists' too while no priced tomb is planned; with several rooms of a role the lowest id takes them. A fresh colony (no corpse,
no record) credits nothing. A colonist whose record covers less than a whole day is credited from what it holds: the covered
ticks are the cycle, and an empty record (no pass yet, an old save) covers nothing and credits nothing (#3835). Not modelled:
PawnObserver's region-traversal bound (only the radius and line of sight are
replayed), a corpse moving before the room takes it, the rot stage changing over the horizon, a grave outside a room taking a
colonist corpse first, a tomb's sarcophagus count, and the exposure a morgue's own haulers still meet.

The sighting log is bounded: one ring per free colonist of its observer passes in the last 2 days (120000 ticks), at most
120000 / 450 + 1 = 267 entries (about 2 KB a colonist), pruned to the free colonists every game hour and saved with the map
(`TrafficState.ExposeData`, the `sightings` node, #3835); a save from before it loads an empty record.

*Worship credit (#3808).* A planned worship room the outcome serves is credited through the same seam
(`ForecastWorship.WorshipCredit`) under `worship/<room>/<pawn>`, per believer (`WorshipFacts.Believers`: the free non-slave
colonists on the map holding the primary ideoligion), a mood delta through `GainPerDay` (registered in `BreakShifts`). The delta
sums two parts. Each building precept's presence demand that applies (`BuildingDemand`, read natively from
`IdeoBuildingPresenceDemand`): the thought now (`IdeoBuildingMissing` without the building, `IdeoBuildingDisrespected` while no
room of it meets every requirement, none otherwise; `IdeologyDefs.BuildingMoods`) against none once the planned room is
predicted to meet every requirement (`ForecastWorship.Meets`: the prediction's cells reach the area, its rounded impressiveness
the impressiveness, every terrain it is floored with carries a demanded tag, and the room's furniture holds the counted things and
no forbidden building) or a standing room already does, `IdeoBuildingDisrespected` otherwise. And each held ritual the room lets
the colony hold (free start, a required building, a positive `IntervalDaysMin`): its whole expected outcome memory at the room's
quality (`RitualOutcomeModel`, the catalog's `RitualOutcomeEffectDef` drawn from quality: startingQuality plus the participant
count's and the room stat's curves at every believer attending and the prediction's impressiveness or steady cleanliness,
clamped; each outcome weighted chance × quality when positive, chance otherwise), over min(duration, interval) / interval. Other
quality comps are not modelled. No ideoligion, no believer or no worship room owed earns nothing. A demand requirement of a
kind the plan cannot predict adds nothing and is named `worship_requirement_unmodelled:<kind>`, and a ritual outcome effect not
drawn from quality `ritual_outcome_unmodelled:<effect>`, not-modelled findings of the served room. A room whose shape closes
no room earns nothing; missing demand moods throw `RequiredFactException` (#3835).

*Deathrest chamber credit (#3810).* A planned deathrest chamber the outcome serves is credited through the same seam
(`ForecastDeathrest.ChamberCredit`) under `deathrest-chamber/<room>/<pawn>`, per deathrester (a pawn whose needs row carries
`NeedsForecastPawn.Deathrest`, read natively from `Gene_Deathrest`: capacity, the efficiency its spawned bound buildings give now,
and its `chamberThoughtIndex`). With the need falling `Need_Deathrest.FallPerDay` a day awake and gaining
`GainPerDayDeathresting` × efficiency a day deathresting (a deathrest starts at 0.05 and, auto-waking, ends at full), the share
of days spent deathresting is F / (G·e + F): 1/7 with nothing bound. The chamber's furniture
(`ForecastCoupledContext.DeathrestFurniture`, the staging's own chamber need resolved against the catalog,
`ChildRoomsRules.DeathrestFurniture`) is bound in id order: each deathrester takes one casket (it is laid in and does not count
toward capacity) while caskets last, then the other bindables by factor, each up to its stack limit and, when it counts, the
remaining capacity (`ForecastDeathrest.Binds`; the factors and limits are the native `NeedsForecastRead.Deathrest` defs). A deathrester left without a
casket gains nothing. The gain per day is the share no longer deathresting (one deathrest day is one capability-day: the Deathrest
hediff caps consciousness at 0.1) plus the `DeathrestChamber` thought between deathrests, through `GainPerDay` (registered in
`BreakShifts`): the stage the prediction's impressiveness gives (1 + its Impressiveness score stage, none for an ascetic) over the
awake share after, against the stage the last deathrest left over the awake share now. The lowest-id chamber serves every
deathrester. No Biotech, no deathrester or no chamber owed earns nothing. Missing deathrest defs, a missing chamber thought stage
row, or a bindable the defs do not name is a required fact (`RequiredFactException`). Not modelled: a deathrester with auto-wake
off, and a bound building's power or fuel (`CanIncreasePresence`). The staging furnishes the chamber by the same `Binds` rule
(#3842, [facilities](facilities.md)).

*Pen credit (#3812).* A planned pen the outcome serves is credited through the same seam (`ForecastPens.PenCredit`) under
`pen/<room>/<animal>`, per roamer it takes (`AnimalUpkeepObservation.Roamers`, from the animal census rows' `Roam`: the race,
`Pawn.RoamMtbDays`, the `MarketValue` stat and `Pawn.CanReachMapEdge`). A roamer that can reach the map edge starts a Roaming
mental state once per `RoamMtbDays` on average (`ThinkNode_ChancePerDay_Roam`, `JobGiver_StartRoaming`, gated by
`MentalStateWorker_Roaming.CanRoamNow`) and walks off the map; a pen encloses it. Each such roamer gains the pen its market value in
capability-days (over `SilverGapRules.ValuePerWorkTick`, as a market good is priced) over its `RoamMtbDays` a day. A pen takes the
roamers of its herd's race (`PlannedRoom.Herd`, carried as `ReviewFacts.PlannedPens`), a misc pen those whose race has no pen of
its own, and of several pens of one herd the lowest id. An animal that does not roam, or a roamer already enclosed, earns nothing;
an animal census native could not read raises `RequiredFactException("animal census")`. Not modelled: the game's five-day roaming
grace at a new colony and the cooldown after a roam, handlers roping a roamer back before it exits, the animal's future products
beyond its market value, and pen capacity.

*Sparring ring credit (#3813).* A planned sparring ring the outcome serves is credited (`ForecastSparring.RingCredit`) under
`sparring_ring/<room>/<pawn>` by the raid harm its trainees' Melee saves, as the flak vest's armor is (#3816). The trainees
(`ForecastCoupledContext.Sparring`, `PracticeMethodCandidates.TraineesOf`) are every work pawn the one practice eligibility rule
admits for Melee (`SkillPractice.Drill.Capable`, native `RangeTraining.CanTrain`), with its native learning, the sparring ceiling
(`SkillPractice.Drill.Ceiling`), its biography's MeleeDPS curve (`StatWorker_MeleeDPS` is damage × MeleeHitChance / cooldown; the hit chance at
each stored Melee level, `NativeSkillStat.MeleeDpsLevels`) and its health. From the service day to tier 2's horizon, each day every
trainee below the ceiling spars until today's XP reaches the game's daily cap (`SaturationThreshold`, SkillRecord.MaxFullRateXpPerDay)
at its learn rate, while two remain below it; without the ring its Melee only decays. Both run the native skill arithmetic
(`ForecastSkillCursor`: decay, the midnight reset, saturation, the XP-to-level table). A day's stored levels read the curves; the
melee power they add (MeleeDPS × health × `DefensePointsPerDps`) shifts the defense capacity in the harm ledger
(`HarmLedger.Of`'s `defenseShift`), whose raid loss without the ring less with it is the day's saving, shared by the added power.
The lowest-id ring takes the trainees. No capable pawn (nobody to train), one (nobody to train with), two standing Melee practice
targets (the colony already spars) or no raid to expect earn nothing. Unread trainees, learning mechanics or raid harm raise
`RequiredFactException`; a capable pawn without a MeleeDPS curve is the finding `sparring_ring_melee_not_modelled:<pawn>`. Named
as not modelled with each served ring (`PlannedRoomCredits.Unmodelled`): sparring injuries (`sparring_injury_unmodelled`), the
bout's recreation (`sparring_recreation_unmodelled`) and the time practice takes from other work (`sparring_work_time_unmodelled`;
the sessions' own scheduling prices that); and with each assigned role, its skill requirement
(`ideology_role_skill_requirement_not_modelled:<id>`).

*Training range credit (#3846).* A planned training range the outcome serves is credited (`ForecastTrainingRange.RangeCredit`)
under `training_range/<room>/<pawn>` by the raid harm its trainees' Shooting saves, the same walk as the ring
(`ForecastPractice.Credit`). The trainees (`ForecastCoupledContext.Range`, `PracticeMethodCandidates.TraineesOf`) are every
work pawn the one practice eligibility rule admits for Shooting (`SkillPractice.Drill.Capable`), with its native learning,
the range ceiling (`SkillPractice.Drill.Ceiling`) and its biography's `RangedDefenseDps` curve: the ranged DPS defense capacity counts
(`PawnRangedDps` × `PawnRangedHitFactor`, #3845) at the ShootingAccuracyPawn each stored Shooting level gives
(`NativeSkillStat.RangedDefenseDpsLevels`; ShotReport.HitFactorFromShooter takes that accuracy, every other shooter factor
held), all zeros without a ranged primary. Ranged damage is not health-scaled. From the service day to tier 2's horizon, each
day every trainee below the ceiling drills the game's daily range budget (`SkillPractice.Range.DailyBudget`, native
`RangeTraining.DailyXpBudget`, applied XP on the range path); without the range its Shooting only decays. The budget is taken
once per credit day, not at the game's own reset phase. A day's stored levels read the curve; the power they add shifts the
defense capacity in the harm ledger, whose raid loss without the range less with it is the day's saving, shared by the added
power. The lowest-id range takes the trainees. No capable pawn, a standing Shooting practice target or no raid to expect earn
nothing. Unread trainees, learning mechanics or raid harm raise `RequiredFactException`; a capable pawn without the curve is the
finding `training_range_shooting_not_modelled:<pawn>`. Named as not modelled with each served range: the time practice takes
from other work (`range_work_time_unmodelled`), more trainees than stands (`range_stand_capacity_unmodelled`) and a weapon
the pawn may equip later (`range_future_weapon_unmodelled`).

*Containment cell credit (#3811).* A planned containment cell the outcome serves is credited through the same seam
(`ForecastContainment.CellCredit`) under `containment-cell/<room>/<entity>`, for the entity it holds
(`ForecastCoupledContext.ContainmentCells`: per planned cell, the strength `ContainmentDefs.Predict` gives its platform there, floored
as the cell need floors it, and the entity on a standing platform inside it). An entity already held there gains the escapes the
cell's strength stops, (escapes a day at its platform's strength now − at the cell's) × the loss of one escape, plus the study the
cell adds, (yield at the cell's strength − yield now). A cell holding no one takes the next waiting entity by id (one the game lets
the colony capture that no platform holds), which gains its study at the cell's strength less its escapes there × the loss of one;
it is taken as healed, new to captivity and without the cold bonus. The escape rate and the loss of one escape are the harm term's
([escape term](../contracts/forecast-contracts.md#escape-term-3811)); a study gives `AnomalyKnowledge` × the study curve ÷ 5 once
per `frequencyTicks`, each point at `Investment[ProjectGraph.KnowledgeKey(category)]` (#3843; 0 until tier 2 prices a category,
named `knowledge_unpriced:<category>`). No Anomaly or no entity earns nothing; unread entities, curves or a held entity's cell
prediction are required facts. The served cell names what is not modelled (`PlannedRoomCredits.Unmodelled`):
`containment_escape_raid_calibration_assumed` (an escape is priced as a raid of the entity's points),
`containment_escape_join_unmodelled` (other held entities joining an escape), `containment_captivity_growth_unmodelled` (the days
held growing over the horizon), `containment_cell_cold_unmodelled` (the cell's temperature) and
`containment_study_activity_unmodelled` (an entity's activity level and an attached electroharvester).

*Isolation room credit (#3844).* A planned isolation room stands out of an agony pulse's reach of every other planned room but
the circulation corridors (`IsolationRoomRules.ApartFromIsolation`: the nearest cells more than the worker's 14.9 apart; no room
where the map has no such site). It is credited through the same seam (`ForecastIsolation.IsolationCredit`) under
`isolation-room/<room>/<pawn>`, once per creepjoiner the colony holds apart (`ForecastCoupledContext.CreepJoiners`: the
admin's isolated pawns, with the days since the tracker's `joined_tick`, and the catalog's downside table): the chance its
downside is `CreepJoinerWorker_PsychicAgony` and first fires between the room's service day and the horizon, given it has not
shown yet (`FirstPulseChance`: `CreepJoinerUtility.GetRandom`'s draw by weight over the drawable downsides that add no trait,
each one's `triggersAfterDays` timer and `triggerMtbDays` past `triggerMinDays` checked every quarter day), times one pulse's
capability-days over every other colonist (`PulseLoss`: AgonyPulse's severity course through its stages' pain, a downed stretch
costing it all). No creepjoiner held apart earns a named zero (`isolation_no_creepjoiner_held`), as do downsides with no pulse
(`isolation_no_agony_downside`); unread held creepjoiners, a pulse the catalog cannot read, a downside gated on combat points
and a form or benefit whose requires list names a downside are required facts. The served room names
`isolation_passers_by_unmodelled`, `isolation_release_unmodelled`, `isolation_nonColonists_unmodelled`,
`isolation_pulse_pain_mood_unmodelled`, `isolation_trigger_conditions_unmodelled` and
`creepjoiner_downside_list_edit_unmodelled` (GetRandom's excludes loop edits the def list a later draw reads).
The other downsides the harm term prices (Aggressive, Leaves, CrumblingMind, OrganDecay; [creepjoiner downside
term](../contracts/forecast-contracts.md#creepjoiner-downside-term-3844)) earn the room no credit: a hostile or leaving
creepjoiner has a lord and ignores its allowed area, and the hediffs act on the creepjoiner alone.

*Hospital credit (#3804).* A planned hospital the outcome serves is credited through the same seam
(`ForecastHospital.HospitalCredit`) what a patient gains lying in its bed instead of the bed it owns now (none: the ground, at the
roomless scores), per colonist under `hospital/<room>/<pawn>`. The room adds no tend quality; the doctor's is the one tier 2 reads
off its trajectory (`ColonyValue.TendingOverHorizon`, #3868: the best tender's quality with the best medicine held, averaged over
the horizon as the run's medicine runs out or is crafted, untended without a tender), first care at once. Three per-day parts, summed:
wound infection, raids a day × (one raid's risk-transformed harm, `HarmLedger.RaidOn`, tended with the owned bed room's stored
`InfectionChanceFactor` less with the hospital's, its stat curve at the predicted steady cleanliness,
`PlannedRoomCleanlinessRules.InfectionChance`); illness, disease incidents a day × (one incident's cost with immunity gained at the
owned bed's `ImmunityGainSpeedFactor` less at the hospital bed def's, #3824); and mood, the patient-days a day (raids a day × one
raid's tended days from first care to healed, `HarmDistribution.PatientDays`, plus the illness's) × `GainPerDay` of the sleep
thoughts the bed trades: `ThoughtWorker_HospitalPatientRoomStats` at the predicted stage when the prediction is a Hospital, against
the ones `ApplyBedThoughts` gives in the owned bed (`SleptOutside`, `SleptOnGround` for a costless bed or none, the bedroom or
barracks thought for a non-ascetic), registered in `BreakShifts`. Each queued `Bill_Medical` on a colonist the schedule does not
finish before the hospital serves adds a `Once` gain under `hospital/<room>/surgery:<demand>`: the failure cost at the owned bed's
`SurgerySuccessChanceFactor` less at the hospital bed def's × the room's `SurgerySuccessChanceCleanlinessFactor`, at the best
surgeon's quality (`SupportWorker.SurgeryQuality`, every other outcome comp applied), the outcomes walked in
`SurgeryOutcomeEffectDef.GetOutcome`'s order (`SupportDemand.SurgeryOutcomes`): death the horizon's rest and `DeathTerminalValue`,
a failure its `totalDamage` dealt as wounds of the read mean severity, bleeding at 19/24 of the model rate (`HarmConsequences.Wounds`), tended in
the surgery's room. A future surgery is a rate (`ForecastHospital.SurgeryRate`: patient, outcome model, surgeries a day): an illness's stocked organ replacements (#3830) come at incidents a day × their probability, and a raid's stocked part replacements (#3847, `RaidPartLoss`) at raids a day × their probability in one hospital-tended raid (`ForecastHospital.Surgeries`), by recipe, each credited per day the same failure cost saved, read from the replacement's `OrganReplacementRead` outcomes and best doctor's quality. No raid, disease or surgery to expect credits nothing. An unread fact throws a `RequiredFactException`: the
prediction, layout room, filth curves, threat or disease schedule, tending, hospital bed, a colonist's harm reads, outfit, owned
bed room or thought rows, or a patient's mood. Named as not modelled with each served hospital: a planned bed's linked facilities (`hospital_bed_facilities_unmodelled`), the
`BotchedMySurgery` thought, the temperature sleep thoughts, the colony's standing medical beds (#3817), outcome classes other
than success, death and failure, and surgeries on non-colonists. Two planned hospitals each credit every patient.

*Prison credit (#3805, #3899).* A planned prison the outcome serves is credited (`ForecastPrison.PrisonCredit`) the recruitment it
enables, per prisoner under `prison/<room>/<prisoner>`. A held prisoner without a prison bed is never recruited; the outcome's
served prisons, in service order, bed the recruitable ones (not unwaveringly loyal) that the shared
`PopulationRules.RecruitIntent` actually selects for recruitment or conversion, up to their beds (planned beds plus the
prediction's), the most valuable first. The best-NegotiationAbility warden chats with each from the room's service, chat by chat
(`ForecastPrison.Recruitment`): job k starts `k × 60000 / 2` ticks in; its five rapport chats come 350 ticks apart, each a
`RapportBuilt` memory at its opinionOffset × the warden's SocialImpact with its stack limits (`ForecastPrison.Opinion`), and its
attempt comes 1,750 ticks after the first. The prisoner's opinion of the warden at an attempt is its relations plus its situational
social thoughts plus its social memory groups (a fading memory's InverseLerp, a cumulative one's day steps) and the rapport, times
the hediff factor, rounded and clamped as `Pawn_RelationsTracker.OpinionOf`. Its mood at the attempt is its unclamped mood now with
its prison thought replaced by the room's PrisonCell (one bed) or PrisonBarracks stage and each mood memory group at its fade and
expiry then (`ForecastPrison.Mood`). Observed ground-sleep, outside-sleep, cold/heat-sleep and no-table memories that a
bedless prisoner renews keep their fresh native contribution until the room serves, then fade and expire over their full
duration from that service; unrelated memories age from now. Conversion's mood drift uses the same timeline.
An attempt with resistance left lowers it by NegotiationAbility × the mood curve × the opinion
curve; at 0 the next one recruits. A prisoner of another ideoligion that the autopilot converts first
(`PopulationRules.RecruitIntent` sets Convert, `ForecastCoupledContext.PrisonerPopulation`, #3915) is converted before any of that, in
the same chat slots (`ForecastPrison.Converted`): a job is three chitchat toils then a `ConvertIdeoAttempt` 1,050 ticks in, which
takes the converter's `CertaintyReduction` times the tracker's change factor from the prisoner's certainty (read now, drifting up
hourly by `ConversionTuning.CertaintyPerDayByMoodCurve` at its mood, over the factor), and at 0 it takes the colony's
ideoligion; the converter is the warden holding the colony's ideoligion with the largest reduction, and the recruit jobs start in
the slots after the conversion. Each chitchat adds the native stage offset times the converter's SocialImpact to the
prisoner's cumulative memory of it, subject to the native thought gates. When the converter recruits it too, the same opinion
timeline merges those raw offsets into its existing memory without resetting age, caps only the displayed offset and decays
one toward zero per day; this raises the following recruitment's opinion. A different converter gives no opinion of the
recruiting warden. A conversion the served days do not reach leaves the prisoner unrecruited. A wild man is tamed at the warden's tame chance each attempt. The warden's inspiration is a
renewal process (`ForecastPrison.Inspirations`, one-hour steps): the one it has now runs out, a new one starts at the MTB its mood
target gives, the def picked by its share of the defs that can occur; `Inspired_Recruitment` (`Inspired_Taming` for a wild man)
recruits at the next attempt, and any other blocks new ones while it lasts. The prisoner's value is its #3825 value
as a colonist (`JoinerMoodRules.ValueOf`) against the work the outcome leaves short (`WorkShortfallRules.Of` over the outcome's
chosen roots, `JoinerMoodRules.ShortfallOf`, computed once per outcome by the shared `PerOutcome` cache, #3913): its own speeds, food and incapable work; its mood the colony's food, room and recreation thoughts
(the median of the colonists' observed rows, picked by thought_triggers.tsv owner, grant and dependency) plus its trait thoughts (a colony thought that is one of them counted once, at its own offset;
Greedy, Jealous and Ascetic at the median over the colonists of the stage their owned bedroom gives, `JoinerMoodRules.RoomTraits`;
apparel, primary weapon, flames, Night Owl and Undergrounder at their stages over each colonist's selected outfit and located awake/sleep services (with unprojected exposure named); pain, added body parts, Recluse, chemical desire and VoidFascination at their native state now; see [joiner situational traits](../contracts/forecast-contracts.md#joiner-situational-traits)) and memories; its free hours the median over the colonists of its own need
falls served as each is served. The row is that value a day with a `Once` of −value × the expected recruitment days, less the
expected chat jobs × the warden work a job takes (its 2,100 toil ticks plus its native round trip, as the shortfall rows they
would fill, as the classroom charges its teacher), less the converter's work for each conversion job (its 1,400 toil ticks plus
that converter's round trip). The origin is each location the outcome assigns the warden work, weighted by its work ticks; with
no assigned work it is the pawn's observed location. Both legs use the room's projected walls, doors and furniture in the native
path job, including the interaction position after Touch and its return to work. An unreachable recruiter or converter walk
credits no recruit and names `prison_warden_unreachable:<pawn>` or `prison_converter_unreachable:<pawn>`; a missing route is a
required-fact failure. Standing and planned bed anchors share the sleeping census and construction sites. A non-positive value earns nothing
(`prison_joiner_costs_more`). Named states, each earning nothing: no prisoner (`prison_no_prisoner`), no recruitable prisoner
without a bed (`prison_no_recruit`, with `prison_unrecruitable` for a loyal one), no warden (`prison_no_warden`), a prison closing
no bed (`prison_no_bed`), a prisoner to convert that no warden can convert (`prison_no_converter`) and the review before the first
layout plan (`prison_awaiting:layout_plan`). Cell meals recur from native nutrition, hunger fall and ingest time; each chat
reads their native memory groups with expiry, fading and oldest-member renewal. The prisoner's comfort and beauty needs
follow native seeker rates under the furnished room's exposure bounds: per-cell perceptible beauty and bed/facility comfort,
with rest intervals derived from its rest need. Recruitment uses lower exposure and conversion drift upper exposure;
`prison_cell_exposure_bounded` names the spatial uncertainty rather than assigning a fabricated wander schedule.
Named as not modelled: any applicable trait thought outside the supported native worker families
(`joiner_situational_trait_thoughts`), future fellow-prisoner mortality memories (`prisoner_mortality_not_modelled`, #4014) and one inspiration counted for every
prisoner (`prison_inspiration_shared`, #3968).

*Classroom credit (#3809).* A planned classroom the outcome serves is credited (`ForecastClassroom.ClassroomCredit`) the lessons
it seats, per child under `classroom/<room>/<child>`. A child (a colonist with a learning need, `NeedsForecastRead.School`) takes
no lesson without a school desk (`LearningGiver_Lessontaking.CanDo`); the colony's standing desks seat the children the lessons
are worth most to, and the served classrooms, in service order, seat the rest, one per planned desk (the prediction's `Desks`),
the most valuable first. Every learning job refills the need to satisfied, so a desire's share of the refill is its share of the
jobs: a seated child's lesson share (`ForecastClassroom.LessonShare`) is the stationary chance that Lessontaking is one of its two
active desires (the tracker's draw by `selectionWeight` among the givable defs not active, the oldest dropped) times the half of
the jobs it then wins. Its lessons teach `share × BaseFallPerTick × ticks a day × xpPerTick / NeedSatisfiedPerTick` raw XP a day
(the LearningRateFactor cancels), split evenly over the skills its teacher teaches (`ChooseSkill`'s top four of the first standing
childcarer's skills neither is totally disabled in, age-disabled work included); each skill's XP goes through the skill arithmetic
over the read's validity (at most a day, as a practice session's, #3852) and is priced at the stock's value per XP
(`SkillValue.PerXp`: `Investment[skill/<child>/<skill>]` plus the work its level speeds up, #3912; a skill without a marginal adds nothing, `classroom_skill_unvalued`). The teacher's lesson
ticks (the child's lesson refill over `NeedSatisfiedPerTick × LearningRateFactor × the desk's SchoolDeskLearningRate`) are charged
as lost work: the shortfall rows those ticks would fill at the teacher's speeds (`PawnValueRules.WorkAdded` against the outcome's
shortfall, the same `JoinerMoodRules.ShortfallOf` the prison reads, #3913). The row is the XP value less that work, a day; it can be negative, since tier 2 values a child's
skill only where a doctor's Medicine or a fighter's Melee and Shooting stock reads it. Named states, each earning nothing: no child
(`classroom_no_child`), no teacher (`classroom_no_teacher`), no Lessontaking desire givable (`classroom_no_lesson`), every taught
child already seated (`classroom_children_seated`), a child taught no skill (`classroom_nothing_taught`) and a classroom with no
planned desk (`classroom_no_desk`). The credit reads no planned walk, so it prices the same before the first layout plan. Named as
not modelled: the XP of the desire a lesson displaces (Workwatching, Reading), the teacher's Social XP, the teacher held free
whenever a lesson is wanted, desks shared by turns, a blackboard's `maxSimultaneous` and the lesson memories. There is no growth
term (D3): the learning need, whose level sets a child's growth points, is refilled by the other desires with or without a
classroom, so growth is unchanged. No playroom is planned (#3809): its role is deleted, and the babies' play is the nursery's (#3854).

*Nursery credit (#3854).* A planned nursery the outcome serves is credited (`ForecastNursery.NurseryCredit`) the crying and
giggling it moves away from the colonists, per colonist under `nursery/<room>/<colonist>`; the lowest-id planned nursery takes
every baby (`nursery_babies_housed` on the others). A baby (`NeedsForecastRead.Babies`) fits at the rates its mood gives
(`ForecastNursery.FitRates`): `MentalFitGenerator` checks an awake baby every `CheckInterval` ticks, trying each fit def in def
order with the chance `interval / (mtbDaysMoodCurve(mood) × a day)`, then holds the fit for its duration and waits the
generator's recovery awake ticks; a fit's rate is its share of the fires over that mean cycle, with the awake share the balance of
the baby's rest fall and its actual sleeping surface's gain on each side. Each outcome supplies the baby's changing actual mood
(#3941), including its food, rest, timed memories and conditional thoughts. The shared mood integrator retains immutable raw
target spans and native rise/fall/freeze inputs for nursery babies and hearers. Counterfactual room offsets replay that same
response from the observed actual mood, without another model run. Fit rates are time-weighted midpoint quadrature, split at
target events, clamp cuts and response catches, with pieces no longer than the native 150-tick check interval. This retains
the existing steady fit-cycle and Poisson-memory approximations; it does not assert the unobserved native hash-check phase. Each colonist the native sound reach
finds (`GenClamor.DoClamor`'s region walk through open doors, at most 15 regions, within `BabyScreamRadius × Hearing`) gains the
fit's memory (CryingBaby, GigglingBaby, the My* defs for a parent); the memories it holds are stacked as the game stacks them
(`ForecastNursery.HeardMood`): per def, the count heard within a duration is Poisson at the summed rate, the latest
`stackLimit` count, split binomially over the babies, and a baby's k of them count `offset × Σ_{i<k} stackedEffectMultiplier^i`.
One shared projected presence timeline supplies the baby's sound source and each recipient's selected work,
need service, observed service and actual sleeping slot. It accounts for service delays and time spent outside
or inside the nursery; it does not hold the snapshot standing position through the day. Native feeding,
static play, walking play, toy play and carried state use the same owner, without assigning a second carer.
Existing map geometry uses native-equivalent clamor traversal and line of sight. Future room geometry,
meal pickup-to-chew travel, unallocated waking time and unassigned care retain visible exposure bounds.
The nursery takes adverse helpful and harmful endpoints on both sides, so uncertain exposure can reduce its
credited benefit. Prisoners, guests and slaves remain the separate `nursery_other_hearers` scope.
Heard BabyCriedSocial and BabyGiggledSocial memories also change the adult's opinion of the baby (#3943). Opinion has no
independent currency. A shared forward nursery timeline ages the observed memories, adds conditional Poisson cohorts,
applies native global/per-other caps, fade and signed group rounding, and normalizes the native interaction weights at
that opinion. Insult's admitted InsultedMood (including Baby-stage eligibility) changes the baby's later mood, fit rates,
hearer mood and existing wellbeing/break ledger. Positive interaction mood effects and their denominator share remain
included. Homogeneous vanilla mood groups use native average-offset stacking; differing labels/offsets, fading or custom
mood classes are named `nursery_opinion_mood_group_unprojected:<baby>/<def>`.

This is the approved conditional expected-value approximation: opinion rounding/selection and mood-to-fit feedback act on
mean state, with Poisson cohorts and native tracker probability mass. `nursery_opinion_mean_field_feedback` and
`nursery_opinion_poisson_cohorts` remain visible in outcome findings; no certified small error bound is claimed. The two
room counterfactuals use the same owner and observed starting state. Verified existing stationary spans use
native social modes and recipient competition; rest disables initiation. For bounded positions, mode or
recipient membership, native check/retry opportunities bound an adverse interaction envelope: potential
harm is retained with the nursery and potential help without it. No distribution over uncertain locations
or smaller invented recipient denominator is used. This enclosure is conditional on the approved mean-field
feedback approximation; it does not certify its global nonlinear error. Observed non-job admission and
incoming interaction cooldown limitations remain visible, and there are no additional model calls.

A crying fit's disturbed sleep uses the same exact sleeping slots and temporal spans as its other exposure.
The selected terminal sleeping target supplies recurrent rest; otherwise the canonical native default
surface remains. Rest fall `f` and gain `g` give recurrent asleep share `f / (f + g)`. Actual scheduled rest
and lying-in spans use their own slot and duration. The shared care owner supplies finite need-conservation
time budgets, not uniform occupancy probabilities: continuous sleep arrival dose can change by at most the
largest fit rate times the unioned care duration. Brief care can intersect a fit's only heard scream, so
once-per-fit memory exposure retains the broader union bound instead of averaging random care timing.
All babies feed one shared `SleepDisturbed` pool per hearer: vanilla calls `TryGainMemory` without an `otherPawn`,
so the three-memory limit is shared. The observed thought is -1 for one day, stack limit 3, multiplier 1 in vanilla 1.7.
The read excludes hearers the native admission predicates reject and giggles never enter the pool. For aggregate
arrival rate `r` and native cooldown `d`, `r / (1 + r*d)` approximates accepted events as a non-paralyzable renewal
process, then the existing Poisson memory-count model prices the shared stack. This is a stationary expectation,
not a predicted next wake or exact event simulation: fit timing is refractory, asleep share is assumed independent
of cries, and the cooldown correction treats their superposition as Poisson. Observed movement clamor is held
constant; its competition for the same cooldown/stack is not jointly simulated. The finding
`nursery_sleep_disturbance_projected` discloses this approximation; `nursery_no_sleep_hearers` names no eligible
recipient. A missing sleeping surface, rest rate, selected target or its hearing row throws `RequiredFactException`.

Crib rest (#3939) also credits the baby under its pawn id. Planned cribs use the shared sleeping catalog's Normal-quality
rest effectiveness and comfort, in site-id order paired with babies in pawn-id order. A standing nursery without a planned
replacement uses the baby's observed sleeping surface, including its actual quality. Without the nursery, the native sleep
read retains an existing bed outside it, otherwise selects an eligible bed outside it using the game's owned-bed, def,
danger and closest-reachable ordering. No eligible bed means the ground, with the native stat's `valueIfMissing` rest gain.
Both surfaces use `Need_Rest.BaseRestGainPerTick × RestRateMultiplier × BedRestEffectiveness`; comfort does not multiply rest.
For rest fall `f` and gain `g`, the shared steady rest contract gives awake share `g / (f + g)` and sleep loss `f / (f + g)`.
The capability credit is sleep loss without minus sleep loss with the crib, and the two awake shares feed separate fit rates
even when the same adults hear both sides. A worse crib can therefore cost capability-days and a faster crib can increase crying.
Rest category rate changes and involuntary sleep timing retain the canonical needs forecast's existing explicit limitations.
Vanilla babies have no Comfort need: `NeedDef.developmentalStageFilter` defaults to Child and Adult, and Comfort does not
override it. This is `nursery_baby_no_comfort_need`, with zero comfort mood effect. A baby with admitted comfort thought rows
uses the shared housing comfort term, which also feeds its fit mood input. Missing sleeping surfaces, planned crib stats or
rest rates throw `RequiredFactException`; they never substitute ground or zero credit for an unread fact.

The own room terms (#3941) include the native CribQuality thought: only a current crib gives it, indexed by quality,
with null stages at Awful, Poor and Normal. The native read supplies current, alternate and new Normal crib offsets;
the credit subtracts the current term once on each side. Vanilla babies have no Beauty, Comfort or Outdoors needs,
and cannot gain bedroom/barracks, outside or ground sleep memories (their developmental stage filters exclude Baby).
Room impressiveness and space therefore add no invented baby mood bonus. The cold/heat sleep memories do admit Baby:
the nursery uses the shared planned sleep-temperature prediction and the alternate uses its observed temperature,
relative to the observed surface. As in housing, these are steady service-day offsets; the generic outcome continues
to own memory aging. A baby without an eligible temperature thought is `nursery_baby_no_temperature_thought`.
The own mood difference is credited to the baby as well as changing its fit rates. Continuous heard-fit offsets price
break tiers directly from the outcome's retained raw targets, with their own clamp cuts. They never mutate
`PlannedRoomCredits.BreakShifts`, which stays constant per context and mood cache key. Missing projected response,
crib offsets, needed sleep temperature or break costs throws `RequiredFactException`.

The nursery also credits caretaker play time (#3940), under `play/<baby>`, naming the observed ongoing carer when
one exists. With no current play job it uses a shared row and bounds every eligible carer's alternative; it does
not assign a carer by sorting. `NativeBabyPlay` reads `Need_Play.BaseFallPerInterval / 150`, `BabyPlayUtility.PlayPerTick`, the
loaded `BabyPlayDef` giver kinds, normal-quality per-stuff `BabyPlayGainFactor` stats and usable toy sources with and
without the nursery. A baby without a play need is `nursery_baby_no_play_need`; no eligible carer is `nursery_no_play_carer`.
Static and walking play have no source multiplier; toys multiply the refill by the source's observed/projected stat.
`GetRoomPlayGainFactors` applies the first decoration within 9.9 cells in the baby's non-huge room, without stacking.
Planned static play uses its crib's position; a standing nursery uses its observed multiplier. Missing mechanics, source
stuff stats or needed planned positions raise `RequiredFactException`.

`WorkGiver_PlayWithBaby` tries the defs in random order, so each viable activity has an equal probability per job.
The time average therefore uses each job's refill duration, rather than the arithmetic mean of refill rates. For play
fall `f` and gross activity gains `g`, the caretaker's play share is `r / (1 + r)`, where `r = f × mean(1 / (g - f))`;
the refill deficit from the low threshold to full cancels. Play falls while asleep too. The credit is the play share
without the nursery minus its share with it, summed across babies; rest credit remains the baby's own capability.
The comparison prices refill ticks rather than forecasting setup, travel or the adult's changing recreation need.
An active native play job supplies the observed allocation (`nursery_play_carer:<baby>:<carer>`); otherwise
`nursery_play_carer_unassigned:<baby>` discloses that only shared caretaker time is valued. The lower bound takes
the least without-room care time minus the greatest with-room care time across eligible carers; the upper bound
reverses those extrema. This permits different carers in the two scenarios rather than inventing a persistent
assignment. An arbitrary adult never receives the shared credit.

Walking can leave the nursery over multiple random legs, and `PlayToys` drops the baby at a random nearby cell.
Their decoration multipliers therefore retain verified lower/upper enclosures across reachable rooms, extended by
planned decorations. The enclosure deliberately retains absence multiplier 1 even where tighter geometry could
exclude it: its endpoints need not be attainable. Exact static exposure still values decoration-only improvements.
A carried baby's room follows `PositionHeld` while
the native decoration distance uses its pickup `Position`; no fixed walking position or invented average multiplier
is used. Projected multiple decorations with differing stats also retain their first-match range. The room receives
the lower bound of caretaker savings (best without minus worst with), while
`nursery_play_savings:<baby>:<lower>..<upper>` records both ends in caretaker-days per day and
`nursery_play_exposure_bounded` identifies a nonzero interval. Decoration-free intervals collapse to the exact random
activity calculation. Bounds deliberately permit pessimism when the actual route or drop is not yet determined.

*Dining and recreation credits.* `ForecastRoomThoughts` compares the observed service profile with the combined profile of
all rooms served so far. Each room receives its marginal change in capability-days, starting on its service day. Adding a
second table can serve a second simultaneous diner; it does not pay again for the first. The shared retained
`ForecastMoodResponse` prices arbitrary combined-room mood changes through `ShiftedTierTicks`, without another model run or
an enumeration of room subsets.

`NeedServiceRules` owns the same steady best-service assumption for the colony cycle and room accounting. A meal uses the
observed food's nutrition per pawn, whole-item ingestion bound and hungry threshold. Useful refill is capped by the hungry-to-full
gap; a smaller available portion gives a shorter cycle. Dining accounting starts from the observed food level and merges the
pawns' periodic meal events in time and pawn-id order. It chooses the nearest free seat within the food's native
`chairSearchRadius`, holds that seat for the native ingestion time, and records the actual room stage or the tableless meal.
Standing seats carry native reachability, safety, social and reservation predicates; planned seats come from the furnished
room prediction and must adjoin an eat surface. Planned geometry is a conditional spatial prediction: future native path and
reservation checks still occur at execution. Meals with radius at most ten do not produce these memories. Food without an
observed service target reports `dining_meal_source_unobserved:<pawn>`: an exact future pickup source has not been selected.

For joy, the Governor chooses exact building jobs; vanilla random `JoyGiverDef.baseChance` does not choose controlled activity.
Native facts read the eligible giver's joy kind, pawn tolerance factor, gain rate, bounded job duration and verified room-memory
finish contract. The existing colony-cycle assumption takes the best building rate, or faster outdoor recreation during its
observed seasonal outdoor share. Other rooms' faster sources therefore retain their use. Ties keep the better observed room
stage, then stable source identity. Short sessions contribute their actual partial refill and therefore recur more often than
one full refill. A source with no verified duration/finish contract or observed power carries a named finding. A planned source
whose absolute duration includes approach needs an observed route to that position; otherwise it reports
`rec_approach_unobserved:<definition>`, because a future native route has not been measured. Future tolerance
changes remain explicit (`rec_future_tolerance_changes:<pawn>`); the rate is conditional on the observed tolerance.

The baseline is generated by those same observed services and room stages, rather than preserving today's room memory forever.
Each stage group uses the pawn's native thought offset, duration, fade flag, stack limit and `stackedEffectMultiplier`.
The native `stagesStack` flag determines whether different stages share a group; a shared group uses their use-weighted mean offset.
`ForecastRoomThoughts.Held` integrates periodic expiry and the native group's mean fading offset multiplied by its geometric
stack shares. Limits at or below one renew the first memory, as `Thought_Memory.TryMergeWithExistingMemory` does.
A room without usable seats or recreation sources, a colony without the corresponding users, or a shape closing no room earns
nothing and names the state. Unread required mechanics throw `RequiredFactException`. These facts and allocations are derived
within the existing forecast; they create no persistent state and write no game orders.

*Who moves first (#3656).* A vacant bedroom bed, standing or planned, goes to the unhoused colonist with the largest priced gain
from it (`ForecastHousing.LargestGainFirst`, a stable sort, so ties keep id order). The gain is read before the search, from
station facts alone (`BedroomGainFacts.Of`, which the clustering's bed merge also reads): `ForecastHousing.BedroomGain` runs the
observed `NeedMood.UnclampedTarget` and `BreakThreshold`, held flat over tier 1's horizon, through `MoodBreakModel.AddTierTicks` and
then `GainPerDay` with `BedroomMoodDelta`. The stage is a standing bed's room stage now (the pawn's rest target), or a planned
bedroom's predicted stage when every stuff predicts the same one (a `RoomStage`: `Staged`, or `Unstaged` without a rest target
or when the stuffs disagree). A delta lacking a read is `RoomMoodDelta.Unpriced`, naming the read; the occupant step throws on it.
When any candidate's gain is unread or unstaged the order stays id order and
each such pawn is named `bedroom_order_unpriced:<pawn>` in the coupled findings. An ascetic is housed by its NeverUpgrade room only
while it owns that room alone; in a shared barracks it is unhoused and ranked like anyone else.

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

   Native cycle occurrence times include minimum spacing and failed placement draws. Each cycle's own forced-raid cutoff applies
   at the occurrence day (day 20 in Core Cassandra and Phoebe); later cycle incidents retain the calibrated half-raid share.
   Scheduled harm uses equipment and stock on that day and the remaining recovery horizon; MTB sources retain their continuous law.
   See [native occurrence windows](../contracts/forecast-contracts.md#native-incident-occurrence-windows).
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
   **Calibrated today:** seven cells, gunners at every ratio (under 0.5, 0.5 to
   1, 1 to 2, 2 to 4 and over 4) and melee fighters under 0.5 and at 2 to 4,
   from 20-seed sweeps whose means held in a second sweep. The under-0.5 cells
   price the small fights: a creepjoiner turning aggressive and a held entity
   escaping (#3880). Every other cell (noncombatants, #3969; melee from 0.5 to 2
   and over 4) is uncalibrated: the harm term is unknown with
   `exposure_cell_uncalibrated:<bin>/<role>` and the death floor is unassessed,
   so a colony outside those cells compares on the other terms only.
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

Beside raids, the term adds illness (#3824), held entities' escapes (#3811) and colonist creepjoiners' unshown downsides
(#3844, [creepjoiner downside term](../contracts/forecast-contracts.md#creepjoiner-downside-term-3844)), priced in the same
capability-days with their deaths in the death floor.

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
3. **Evaluate lazily, budget by evaluations** ([#3472](https://github.com/davidarcher/rimgovernor/issues/3472)).
   - **The budget counts outcome evaluations** ([#3722](https://github.com/davidarcher/rimgovernor/issues/3722), shipped).
     `ForecastSearchBudget(Evaluations, Traversal)`: one unit per complete plan a
     schedule search hands to the evaluator (a cached schedule or outcome spends
     none), and a separate bound on scheduler expansions so refused, duplicate and
     unknown-heavy branches terminate without evaluating anything. One ledger
     spans the baseline (mandatory, reported apart and charged to neither), the
     warm start, the seed share, every extension and confirmation, the schedule
     search's capacity-recovery retries and the planned-unplaced reselection;
     nothing nested resets or extends it. Each allowance's end is its own
     finding (`outcome_evaluations_exhausted`, `traversal_bound_exhausted`). The
     default is 8,192 evaluations and 65,536 expansions
     ([derivation](../testing/portfolio-allocations.md#outcome-evaluation-budget-3722)),
     kept by #3725 on measured quality and cost
     ([tuning](../testing/portfolio-allocations.md#default-budget-tuning-3725)).
   - **Lazy outcome evaluation** ([#3723](https://github.com/davidarcher/rimgovernor/issues/3723), shipped).
     An evaluated outcome is priced in the comparator's order: the urgent-zero
     time, the harm term with the death excess, the break-risk excess and the
     goal terms at evaluation; the remaining ledger terms (hunger, rest, raw
     ingredients, poisoning, thermal, breaks, wellbeing, investment) only when a
     comparison against the leader reaches the ledger, or when a consumer needs
     the complete vector (the selected and baseline outcomes, every
     `coupled_selection` vector, `ForecastServiceInvestments`,
     `DeferUnselectedPawns`, residual evidence). The reference's harm is priced
     once per search. One comparator decides both forms, so the lazy search
     selects what the eager control selects, with the same evidence rows,
     findings and #3722 counts (a started evaluation costs one unit wherever its
     comparison stops); the eager form exists only as a test control
     (`CompareActivityOutcomesEagerly`). A loser decided at a floor is reported
     with `lost_to_leader:<stage>` and no vector rather than a complete
     zero-valued one; `ForecastPortfolioEvidence.StageOf` reads the stage, the
     hook for a finalist collector (#3720). Measured on the four-worker fixture
     at 700 ticks: 435 of the 2,923 ranked losers (15%) stop at a floor and never
     price their ledger; the other 2,488 reach it. Both pinned allocation
     baselines are unchanged
     ([portfolio allocations](../testing/portfolio-allocations.md#lazy-outcome-evaluation-3723)).
   - **Proven optimistic bounds and safe pruning** ([#3724](https://github.com/davidarcher/rimgovernor/issues/3724),
     shipped). A subset is skipped before its schedule is searched only when a
     proven bound says no completion of it can beat the current leader: the
     urgent-zero unserved-pawn bound ([safety floors](#safety-floors)), applied to
     a subset in the seed, extension and confirm, and to a prefix with every
     completion in the cardinality sweep. Where the bound is unavailable (a rest
     need task in the closure, an unknown baseline zero time, an unassessed
     leader, goal values in the context, the relief gate's order) the subset is
     evaluated within the #3722 allowance; each phase reports `pruned` and
     `unbounded`, the selection `search_pruned` and `bounds_unavailable`. No bound
     exists for the death and break-risk floors, the ledger or the tiebreaks, and
     none is heuristic. Against the unpruned control
     (`CompareActivityOutcomesUnpruned`) the pruned search selects the same
     portfolio with the same vectors and findings at every budget on both pinned
     fixtures; at the default budget the four-worker fixture at 700 ticks spends
     2,823 outcome evaluations and 11,648 expansions instead of 3,138 and 12,736
     (20 extension subsets skipped), the original fixture 749 and 4,487 instead of
     763 and 4,551 (one seed subset), and its finished sweep 7,713 and 39,809
     instead of 7,833 and 40,331 (two prefixes)
     ([portfolio allocations](../testing/portfolio-allocations.md#proven-pruning-3724)).

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

- Exposure calibration: how can the under-0.5 gunner cell, where hits are rare,
  pass the two-sweep stability check, and which raids fill the melee and
  noncombatant cells outside 2 to 4?
- The tier-2 pawn cycle: is one derived day per pawn accurate enough, or does
  it need per-season variants (temperature, daylight)?
- Initial values for the policy constants.

## Milestones (proposed)

Each milestone has a GitHub issue.

1. [#3467](https://github.com/davidarcher/rimgovernor/issues/3467) Search stage 1 (no behavior change).
2. [#3468](https://github.com/davidarcher/rimgovernor/issues/3468) Search stage 2, including exact mood segments.
3. [#3469](https://github.com/davidarcher/rimgovernor/issues/3469) Capability-days and floors over today's one-day horizon (implemented; #3519 to #3522). This replaced the
   lexicographic ranking; the vector stays as explanation.
4. [#3470](https://github.com/davidarcher/rimgovernor/issues/3470) Tier 2: pawn cycle, stocks and flows, project graph, crop growth by twelfth (implemented: `Policy/ColonyModel/` with `ReviewColonyModel.Review`, the research and sowing providers `ResearchMethodCandidates` and `SowingMethodCandidates`, the `colony_model` event; the devilstrand payoff chain is wired by [#3559](https://github.com/davidarcher/rimgovernor/issues/3559) (its apparel value waits on the calibrated exposure table, #3543) and proven in play by `production/devilstrand-investment` ([#3560](https://github.com/davidarcher/rimgovernor/issues/3560)); `production/devilstrand-investment-starving` proves that investment never outranks the urgent floor).
5. [#3471](https://github.com/davidarcher/rimgovernor/issues/3471) Harm model v1: threat schedule, composition, armor response,
   consequences, with exposure calibrated from real-game sweeps (implemented, #3539 to #3547; seven exposure cells are calibrated, #3561 and #3880).
6. [#3472](https://github.com/davidarcher/rimgovernor/issues/3472) Search stage 3: the outcome-evaluation budget (#3722, shipped), lazy evaluation, proven pruning and the tuned default.

The food-runway diagnostic uses the native annual daily growing pattern at the
forecast's current review-relative day. Seasonal reserve phase-in and shrinking
non-growing waits are piecewise linear; crossings include changes between stock
and project events. A carried run shares this calendar and the original end date.
