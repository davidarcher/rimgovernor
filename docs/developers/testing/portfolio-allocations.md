# Coupled portfolio allocation baseline

[Documentation map](../../README.md) · [Choose checks](choose-tests.md) ·
[Productive learning contract](../contracts/skill-learning.md#productive-cooking-in-normal-selection)

`PortfolioAllocationTests` measures normal generation/composition and shared
selection independently. The baseline policy revision is
`3d130260b6bded4b5e24f48fead51d9caca34188` (#3164), with only the test harness and
expectations added. It preserved the 16,384 default, plain cardinality traversal,
scheduler bounds, all generated roots and options, and explicit unknowns. #3197
then changed the traversal to be incumbent-seeded ([below](#incumbent-seeded-extension-3197));
the default budget, scheduler bound, roots, options and unknowns are unchanged. The
sections before it record the plain-cardinality measurements as history. This is
pure-policy allocation/choice evidence; it makes no native gameplay or completion claim.

## Reproduce

From the worktree root in PowerShell:

```powershell
dotnet build brain/tests/RimGovernor.Brain.Domain.Tests -c Release
New-Item -ItemType Directory -Force .rimgovernor/portfolio-baseline | Out-Null
$env:RIMGOVERNOR_PORTFOLIO_OUTPUT = (Resolve-Path .rimgovernor/portfolio-baseline).Path
& brain/tests/RimGovernor.Brain.Domain.Tests/bin/Release/RimGovernor.Brain.Domain.Tests.exe -method '*MeasureWarmedAllocationMatrix' -parallel none
& brain/tests/RimGovernor.Brain.Domain.Tests/bin/Release/RimGovernor.Brain.Domain.Tests.exe -method '*ProfileAllocationTypes' -parallel none
Remove-Item Env:RIMGOVERNOR_PORTFOLIO_OUTPUT
```

Run these commands in isolation from other tests. Each measurement operation is
synchronous on one thread, with one unrecorded warmup followed by three samples,
without forced collection. `GC.GetAllocatedBytesForCurrentThread` and monotonic
`Stopwatch` timestamps bracket only the operation. Fixture construction, request
construction, formatting and assertions are outside those intervals. Candidate
and request objects are constructed once and reused across selector budgets.
Generation includes composition and is measured separately with the same facts.
All three raw byte/time samples and complete selected quality are written to JSON.
`environment.json` records configuration/runtime/machine and the measured assembly
commit independently of the historical baseline commit. Record uncommitted policy
changes separately when comparing a dirty candidate build.

The profiling command is separate from timed measurements. It warms generation
and default-budget selection, then samples 100 generation calls or three selection
calls via the CLR `GCAllocationTick` event. A post-workload GC/`GCHeapStats` marker
waits for buffered runtime events before freezing the sample; collection is never
inside a measurement interval. An empty profile or missing marker fails. Sampled
bytes are approximate attribution to the type at each roughly 100 KB allocation
tick, not exact per-type bytes or retained memory. Events are process-wide and
include small runner/listener overhead; sampling variance and trailing partial
ticks limit comparisons. Compiler-generated types are mapped to their actual
method names by reflection. General framework types do not supply call stacks.

Without the environment variable, measurement/profile methods do no work. The
recorded [original](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/productive.quality.json)
and [larger](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/four-workers.quality.json)
seven-budget snapshots (outcome-evaluation budgets since #3722,
[below](#outcome-evaluation-budget-3722)) hold physical identities, participants,
travel/wait/work/timing, learning, complete baseline/selected consequence vectors,
findings, the search's outcome evaluations and expansions with the baseline's share and
each phase's spend, and truncation. JSON `null` means unknown, never zero. The gate no longer compares
them with a fresh selection: that test cost 23–40 s per run and was deleted. Only
`ReferenceIsNeverWorseThanAnyRecordedIncumbent` still reads the larger snapshot, so
treat both as records of their last re-recording, not as current output. The
measurement run's `*.measurements.json` carries the same quality rows for a fresh
comparison. Keep raw outputs and traces uncommitted.

## Workloads

The original fixture is extracted directly from
`NormalFoodCompositionAndSharedSelectionValueLearningThroughLaterDemandedMeals`
with learning .1 and speed 1. A developing cook and experienced helper have eight
rice, one stove and two demanded meals. The larger fixture extends these same
facts to four workers (`pawn`, `helper`, `diner`, `porter`), sixteen rice and two
hungry consumers, each with two demanded ingestion events. The additional workers
have ordinary observed cooking rates of 105 and 110 ticks; the experienced helper
has 91, and the developing cook uses the original learning table. All workers have
normal capacity/routes and all legal generated alternatives remain available.
Neither fixture names a selected worker or filters generated recipes/roots.

Both use a 700-tick horizon, food level .75, fall .002/tick, threshold .25 and .5
nutrition per meal. Those facts produce two hunger events per hungry pawn within
the horizon. Helpers' food does not fall. Candidate generation, including the
normal recovery-before-production alternatives, supplies these measured sizes:

| Fixture | Workers | Demanded meals | Tasks | Options | Roots | Composition expansions | Generation truncated |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| Original | 2 | 2 | 15 | 18 | 8 | 1,029 | false |
| Larger | 4 | 4 | 88 | 194 | 62 | 8,234 | false |

## Measured baseline (plain cardinality order, before #3197)

Recorded on DAVID-PC, AMD Ryzen 9 9950X (32 logical processors), 93.7 GiB RAM,
Windows 10.0.26300, .NET 10.0.12 x64, Release, 2026-10-15. Tables report medians
of three samples. MB is decimal. Wall time is descriptive, never a pass/fail gate;
allocation counts also vary slightly with runtime/JIT behavior. Compare identical
fixtures, configuration, runtime and protocol before drawing conclusions.

Generation/composition costs 1.337 MB / 1.307 ms (original) and 12.069 MB /
8.464 ms (larger), separately from the selector figures below.

| Fixture | Budget | Allocated MB | Elapsed ms | Search expansions | Recipes / ingestions | Labor | End tick |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Original | 1,024 | 40.913 | 22.474 | 1,024 | 1 / 1 | 103 | 250 |
| Original | 4,096 | 142.415 | 87.070 | 4,096 | 1 / 1 | 103 | 250 |
| Original | 8,192 | 267.091 | 180.263 | 8,192 | 2 / 2 | 208 | 342 |
| Original | 16,384 | 578.082 | 393.453 | 16,384 | 2 / 2 | 206 | 500 |
| Original | 65,536 | 1,866.388 | 1,235.328 | 52,654 | 2 / 2 | 206 | 500 |
| Original | 262,144 | 1,866.390 | 1,233.615 | 52,654 | 2 / 2 | 206 | 500 |
| Larger | 1,024 | 104.041 | 69.144 | 1,024 | 2 / 1 | 211 | 251 |
| Larger | 4,096 | 426.148 | 298.315 | 4,096 | 2 / 1 | 198 | 251 |
| Larger | 8,192 | 856.888 | 647.450 | 8,192 | 2 / 1 | 198 | 251 |
| Larger | 16,384 | 1,763.820 | 1,398.035 | 16,384 | 2 / 1 | 198 | 251 |
| Larger | 65,536 | 8,197.480 | 6,436.267 | 65,536 | 2 / 1 | 198 | 251 |
| Larger | 262,144 | 26,611.990 | 20,325.560 | 262,144 | 1 / 1 | 103 | 250 |

Every search row is **truncated**, including original rows that stop below the
outer budget: the scheduler's per-portfolio bound also contributes truncation.
These are reproducible incumbents, not exhaustive optima. In particular, the
larger workload served only one of four demanded ingestions at all measured
budgets; the last row removes unhelpful labor without improving hunger relief.
Do not report that labor reduction as satisfying four meals.
[Issue #3193](https://github.com/davidarcher/rimgovernor/issues/3193) tracked the
feasibility/search-quality investigation; allocation work (#3186/#3192) preserved
these results, and #3197 is where they intentionally changed.

At budgets 1,024/4,096 the original picks the helper's 91-tick recipe (1–92) and
pawn ingestion (240–250). At 8,192 it picks developing-pawn recipes (1–101,
251–331), with ingestion ending at 250/342. At 16,384 and above it picks the same
worker's 100/80-tick work at 1–101/104–184, with ingestion at 240–250/490–500;
the helper supplies/delivers. Larger 1,024 uses diner/helper recipes
(1–106/108–199); 4,096–65,536 use helper recipes (1–92/92–183). All have one
diner ingestion (240–250), with diner supply and a helper's delayed delivery
ending at 251. At 262,144 only one helper recipe (1–92) remains, with diner
supply/delivery/ingestion. Exact task identities and all intervals are in the
embedded snapshots.

Original low-budget urgent consequences are `[75,34.375,200,200,0]`; at 8,192
and above they are all zero. Larger urgent consequences remain
`[325,96.875,450,650,0]`. Needs vectors are `[null,null,0,0,1,0]` for original
low budgets, `[null,null,0,0,0,0]` for higher budgets, and
`[null,null,0,0,2,0]` for larger. Mood exposure/depth stay unknown; development
is zero. All retained `serious_harm_probability_unassessed` (now `harm_capability_unassessed`, #3522), `unassessed_mood`
and `unassessed_thermal_exposure`. Full vectors, including thermal occupancy,
support and baseline comparison, are pinned in JSON rather than rounded here.

## Allocation attribution

The stage profiles identify actual allocated CLR types. Selection is distributed
across many small types; no single dictionary dominates. At the default budget,
strings, double-tree nodes, need-choice/memory predicate delegates, arrays and
boxed immutable/equatable arrays of conditional thoughts are prominent. Recorded
compiler closures identify `ForecastMoodTimeline`, `ForecastNeedsTimeline` and
`EvaluateCoupledSchedule` directly. Generation samples identify composition
allocations, string-key entries/sets, requirement iterators and closures belonging
to `ComposeActivityDependencies`.

Representative sampled shares (approximate percentages of each stage's sampled
bytes; compiler identities may change when source moves):

| Actual allocated type | Original generation | Larger generation | Original selection | Larger selection |
| --- | ---: | ---: | ---: | ---: |
| String-key hash entry arrays | 7.3% | 9.0% | — | — |
| `HashSet<string>` | 5.0% | 6.4% | — | — |
| `string` | 5.6% | 5.8% | 3.6% | 2.3% |
| `Node<double>` | — | — | 2.6% | 3.2% |
| Need-choice predicate delegates | — | — | 2.5% | 3.1% |
| Need-memory predicate delegates | — | — | 2.0% | 2.7% |

Dashes mean omitted from this concise table, not no allocations. The generated
profile JSON retains every sampled type, sampled bytes/count and available
compiler method mapping.

The following source-inspection hypotheses are distinct from measured attribution:
rebuilding timeline breakpoints/delegates for every candidate schedule may explain
the needs/mood types; repeated composition search-state copies may explain the
string-key entries. General strings/arrays cannot be assigned to a method from a
type-only sample. Profile and measure each proposed change, retaining the same
quality snapshots and search semantics. There is no approved percentage target,
new cache, persistent state, selector or scheduler in this baseline.

## Optimized timeline representation

The #3192 candidate is based on `957bda7be` with the `ForecastNeeds` and
`ForecastMood` edits in that change (the pre-commit assembly reports
`1.0.0+957bda7be25b4a5afb6edfeec91c7c9c2f6feb83`). The same Release/runtime/machine
and one-warmup/three-sample protocol above produced these isolated measurements
on 2026-10-15. Both complete six-budget quality snapshots match byte-for-byte
in the regression tests, and the measurement JSON quality objects match them too.
No budgets, generated candidates, search ordering or scheduler code changed.

Needs now builds its shared activity breakpoints once per timeline. Its at most
five integration cuts use a stack buffer instead of a tree, retaining sorted
unique values and exact cut ordinals. Ordered scans remove per-interval needs
predicates and per-point mood predicates; the current conditional offset is
constant within a call. Memory and meal sums retain their separate accumulation
order. There is no result cache or scratch state shared between calls/branches.

| Fixture | Budget | Baseline MB | Candidate MB | Reduction | Candidate elapsed ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Original | 1,024 | 40.913 | 35.803 | 12.49% | 63.960 |
| Original | 4,096 | 142.415 | 124.452 | 12.61% | 231.291 |
| Original | 8,192 | 267.091 | 231.327 | 13.39% | 251.636 |
| Original | 16,384 | 578.082 | 492.192 | 14.86% | 360.563 |
| Original | 65,536 | 1,866.388 | 1,586.721 | 14.98% | 1,127.647 |
| Original | 262,144 | 1,866.390 | 1,586.722 | 14.98% | 1,227.699 |
| Larger | 1,024 | 104.041 | 84.990 | 18.31% | 62.131 |
| Larger | 4,096 | 426.148 | 348.295 | 18.27% | 307.402 |
| Larger | 8,192 | 856.888 | 700.565 | 18.24% | 607.765 |
| Larger | 16,384 | 1,763.820 | 1,454.295 | 17.55% | 1,377.951 |
| Larger | 65,536 | 8,197.480 | 6,757.113 | 17.57% | 6,164.352 |
| Larger | 262,144 | 26,611.990 | 21,917.542 | 17.64% | 25,091.099 |

At the default, the original drops from 578,082,392 to 492,191,880 bytes
(**14.86%**); larger drops from approximately 1,763,820,000 (historical baseline
rounded to 0.001 MB) to 1,454,294,808 bytes (**17.55%**). All rows reduce selector
allocation. The post-change sampled profile still contains timeline allocations,
but need-memory-to-double delegates are only about 0.12%/0.26% of selection
samples, versus the earlier prominent per-point memory closures. Sampled type
shares are attribution, not exact savings estimates.

Generation/composition is unchanged: candidate medians are 1,336,664 bytes /
1.932 ms (original) and 12,666,440 bytes / 14.161 ms (larger). The latter is about
5% above the historical 12.069 MB. Neither changed timeline function is called
by generation. This protocol warms each stage once and leaves tiered compilation
active; measured byte counts vary between process runs even at unchanged source
(the candidate default original measured 490.570 MB in an earlier run). Runtime
compilation state is a possible explanation, not established causal attribution;
there is no claimed generation improvement. The larger 262,144-budget time also
rose from 20,325.560 to 25,091.099 ms despite lower allocations. These descriptive
shared-machine timings do not establish a throughput improvement or regression;
no latency gate or broader benchmark claim is made.

Focused regressions cover recovery saturation, declining levels, thresholds at
0/.5/1 (including coincident cuts), interrupted intervening calls, and repeat
results. Existing needs, conditional mood, survival, productive learning and
both matrix fixtures pass. At that change the larger fixture still served only one
of four ingestions; #3197 later raised it to two at the default budget. Native
execution is not needed for this representation/allocation claim.

## Independent four-meal reference (#3195)

The larger fixture's measured incumbents served one of four demanded ingestions at
every budget (before #3197). `PortfolioReferenceScheduleTests` establishes, independently of the
selector and scheduler, whether more is physically possible. **Result: 4 of 4 is
feasible.** A hand-built schedule relieves every demanded ingestion; no constraint
binds before the horizon. This shows feasibility, not proven global optimality: it
is a test-only existence proof, not an exhaustive solver, and says nothing about
why the search misses it (tracked under
[#3193](https://github.com/davidarcher/rimgovernor/issues/3193)).

The reference uses root `food/27`, the four-meal production chain generated from
the fixture, and one ingestion per demand event. The helper cooks all four meals
back to back at its observed 91-tick rate; porter carries rice early and each meal
just in time; diner and pawn each eat in both hunger windows. Activity numbers are
`composition/N` of the generated ids.

| Step | Worker | Activities | Ticks |
| --- | --- | --- | --- |
| Rice to the stove (4 each) | porter | `food/27/supply/0..3/rice/4` | 0–1, 2–3, 4–5, 6–7 |
| Recipes (1 stove, serial) | helper | `food/27/recipe/0..3` | 1–92, 92–183, 183–274, 274–365 |
| Carry meal to dining cell | porter | `food/27/delivery/0..3` | 92–93, 183–184, 274–275, 365–366 |
| First ingestions | diner (meal 0), pawn (meal 1) | `food/27/delivery/0/eat/diner/0`, `food/27/delivery/1/eat/pawn/0` | 240–250 |
| Second ingestions | diner (meal 2), pawn (meal 3) | `food/27/delivery/2/eat/diner/1`, `food/27/delivery/3/eat/pawn/1` | 490–500 |

The last meal is ready at 366 and the last ingestion ends at 500, within the
700-tick horizon. Four meals need four recipes and all 16 rice, so stove time is
the constraint to watch: about 364 of 700 ticks with the fastest cook, which does
not bind. A variant in which the developing pawn cooks the first two meals (100
then 80 ticks) and the helper the rest is also accepted.

`ReferenceScheduleChecker` re-derives the constraints from the fixture's data
records and plain arithmetic; it calls no policy scheduling code (a test
fails if its source mentions it). It validates per-worker non-overlap including
travel and waiting, routes and availability windows; the single stove's exclusive
use and location; dependencies, earliest/latest bounds and the horizon; exact
observed rates (91/105/110 ticks, 1-tick carry, 10-tick ingestion) and the
developing cook's level-by-experience table; lot conservation (rice in, meals out,
no meal eaten before it is cooked and delivered); and ingestion timing. A hungry
pawn's food crosses its threshold at (level − threshold) / fall, tick 250, and every
nutrition / fall ticks later, 500. An event is relieved only if its ingestion ends
by that tick (so 240–250 and 490–500).

Negative tests show the checker rejects an overlapping worker, an overbooked stove,
a meal eaten before it is cooked and delivered, a conservation violation (two
ingestions of one meal), wrong cook rates and a missed hunger window. The reference
is compared read-only with the recorded incumbents (one ingestion at all six
budgets then; at most two since #3197, so the reference still serves more). No production code, budget or traversal changed. The two quality baselines already carry the drift from other landed work (each Support object has `"Maintenance": []`; the top-level Findings include `assigned_capability_floor_unknown`, `practice_held_essential_emergency_or_unknown`, `practice_observation_unknown` and `unsupported_defensive_improvement_without_assignment:<pawn>`), and this change leaves them unchanged. No numeric, vector, physical or selection value changed.

## Why the search serves one of four (#3196)

This is the diagnosis of the plain cardinality order that production followed until
#3197 (see [the remedy](#incumbent-seeded-extension-3197)); its numbers describe that
order, not today's traversal. `PortfolioSearchDiagnosisTests` observes the production
search and scheduler through their public API. Its
`TraversalReplica` mirrors the plain subset order of `CompareActivityOutcomes` for counting
only. **Measurements** below are asserted by those tests unless marked *opt-in* or
*recorded*; the opt-in run
(`RIMGOVERNOR_PORTFOLIO_OUTPUT`, `-method '*WriteSearchDiagnosisMeasurements'`, about
four minutes) writes `search-diagnosis.*.json` and also covers the budgets too slow
for the normal gate. **Hypotheses** are labeled as such.

### Measurements

1. **Generation is not the limiter.** 88 tasks, 194 options and 62 roots; generation
   is not truncated. The reference's four ingestions are four single-completion roots
   (`food/27/delivery/{0..3}/eat/...`, at indexes 37, 43, 46 and 52 of the 62 in id order). Their
   prerequisite closure is exactly the sixteen reference activities, and every
   reference (activity, worker) pair is a generated option. The production chain root
   `food/27/composition/18` is also present. The smallest portfolio that serves four
   ingestions has four roots.
2. **Traversal never reaches cardinality 4, nor most of 2.** *Recorded*: the test that
   asserted these counts cost 18–40 s per gate run for an order production no longer
   follows, so it was deleted. After exclusive-outcome and
   graph-redundancy pruning, the search enumerates 62 / 1,698 / 28,580 / 339,189 subsets
   at cardinality 1 / 2 / 3 / 4. The reference set is the 357,973rd evaluation, which is
   beyond the largest budget even at one expansion per evaluation. Each evaluation
   spends 1 plus the scheduler's expansions, and the scheduler is bounded at 4,096:
   43 of the 62 single roots spend the full bound (4,097 each, truncated), 16 spend 547,
   two spend 1 and one spends 214. The cardinality-1 sweep alone therefore costs
   **185,139** expansions.

   | Budget | Roots entered (completed) | Cardinality reached | Notes |
   | ---: | ---: | ---: | --- |
   | 1,024 | 2 (1) | 1 | root 1 of 62 |
   | 4,096 | 2 (1) | 1 | root 1 truncated |
   | 8,192 | 3 (2) | 1 | root 2 |
   | 16,384 (default) | 5 (4) | 1 | root 4 |
   | 65,536 | 19 (18) | 1 | root 18 *(opt-in run agrees)* |
   | 262,144 | 62 (62) | 2 | sweep ends at 185,139; 77,005 left cover 20 of 1,698 pairs *(opt-in)* |

   The rows are the replica's count. Before #3197 production matched them (the first
   four in the test, the last two in opt-in runs by last root and pair position); the
   production comparison was removed with the traversal change.
3. **Scheduler on the reference set (production bound).** Replaying the four roots
   through `ForecastColony` (the public route to `ForecastCoupledScenarioOf`) with the
   production per-portfolio bound: 4,096 expansions, truncated, 1,023 alternatives,
   **1,023 feasible**, none carrying an owner or stack-quantity refusal (#3210; before
   it, 0 feasible, every alternative refused with `conflicting_selected_target_owners`
   on `cell/3/1`). Pinning each activity to its reference worker leaves 420
   alternatives, all feasible; the best serves all four ingestions with urgent
   `[0, 0, 0, 0, 0]` and labor 841. A diner ingestion plus a pawn ingestion
   (1,018 alternatives, all feasible) was refused before. The scheduler cannot be given a larger
   bound: `ExpansionBudget` above 4,096 is rejected as an invalid activity graph or
   budget (asserted), so a larger-bound replay is not possible without a production
   change and was not made.
4. **Why the reference was refused, and why it is accepted now.** Both consumers'
   ingestion tasks name the same delivery-cell target `cell/3/1`, the stack the
   produced batch sits in. Until #3210 the evaluator allowed one pawn to own an exact
   target across a portfolio. It now admits `MaxPawns` distinct owners (a generated
   delivery stack carries vanilla's ingest value 10) and sums the claimed counts
   against the batch quantity, so the two consumers share the stack and the #3195
   physical schedule is a statement the production evaluator accepts. The ledger
   (`source_lot_shortfall`, `timed_material_shortfall`) still bounds what the batch
   can supply. The accepted statement concerns the scheduler on a given root set; the
   subset search still does not reach cardinality 4 within a budget (item 2).
   One consumer's two ingestions were accepted before: the diner pair
   (`food/27/delivery/0/.../diner/0` + `delivery/2/.../diner/1`) gives 1,646
   alternatives, 1,190 feasible, best consequence urgent
   `[325, 96.875, 450, 450, 0]`, needs component 4 equal to 1, labor 446.
5. **A feasible, better plan exists and is missed.** The incumbent (one ingestion,
   `food/0/.../after/food/14/.../diner/0`) has urgent `[325, 96.875, 450, 650, 0]`,
   needs component 4 equal to 2, labor 198. Restricted to the incumbent root plus
   `food/27/.../diner/0`, the production search selects both: two ingestions, urgent
   `[325, 96.875, 450, 450, 0]`, needs component 4 equal to 1, labor 355, with the
   per-portfolio scheduler still truncated. That pair is the 119th evaluation overall
   (57th pair).
   *Opt-in* production runs beyond the matrix: 393,216 and 524,288 select a
   two-ingestion portfolio (urgent `[..., 450, 0]`, labor 354; last pair evaluated 64th
   and 120th); 1,048,576 selects another (labor 328; 438th pair). Cardinality 3 is never
   entered. No budget improves the consequence beyond two ingestions.

### Hypotheses (not measured)

- (Measured before #3210.) The ceiling for this fixture under the evaluator of the time
  was two of four ingestions: the worst-pawn components of the urgent vector cannot
  improve while the second consumer is unfed, and a two-consumer portfolio was refused
  by the shared target. #3210 removed that refusal; the ceiling is now the search's reach.
- A remedy that reaches the pair stage sooner (a cheaper single-root sweep, pair-first
  ordering, or a larger budget) would find the two-ingestion plan; the observed
  crossover is between 262,144 and 393,216 expansions. Choosing the lever is #3197.
- Whether the shared target in `LargerFacts` represents a real colony (two pawns using
  one dining target) or a fixture artifact is moot after #3210: the shared stack admits both.

### Verdict

**Feasible plan missed, limiter = subset traversal exhausting the budget inside the
cardinality-1 sweep** (43 of 62 single roots each spend the whole 4,096 scheduler bound;
the sweep costs 185,139 expansions, so every budget up to 65,536 never leaves the first
18 roots and 262,144 reaches 20 pairs). The missed plan serves two of four ingestions
(one consumer's both events) and is strictly better than the incumbent. *History
(superseded 2026-10-15, #3210/#3212):* when this was written the evaluator refused four of
four on the fixture's shared target (exact-target ownership), so the remedy (#3197) aimed at
two, not four. #3210 replaced that rule with a capacity check and the scheduler now accepts
the four-root set; the limiter for four of four is the search's reach, measured
[below](#re-measurement-after-the-capacity-rule-3212). Generation and feasibility are not
limiters; the per-portfolio bound is fixed at 4,096 and only matters through its cost per
evaluation.

## Incumbent-seeded extension (#3197)

The #3196 diagnosis found a strictly better two-ingestion plan that plain cardinality
order never reaches, because 43 of 62 single roots each spend the whole 4,096
scheduler bound and the single-root sweep alone costs 185,139 expansions.
`CompareActivityOutcomes` now traverses in three phases, all deterministic and all
inside the same budget:

1. **Seed pass.** Single roots in root-id order, but a new evaluation starts only
   while less than a quarter of the budget (`budget / 4`) is spent. If every single
   root fits under that, the pass is the whole cardinality-1 sweep. Stopping here is a
   hand-off, not truncation.
2. **Extension.** If a portfolio beat the baseline, it is extended with each other root
   in root-id order (compatibility and graph-redundancy rules unchanged; the portfolio
   keeps canonical root order, so the scheduler sees what the pairwise sweep would give
   it). The ordinary evaluation decides: a retained incumbent is never released on a
   consequence tie, and labor breaks ties between feasible plans. It runs once, from the
   leader the seed pass found; it is not repeated for a new leader.
3. **Cardinality sweep.** The original traversal (singles, pairs, ...) resumes and skips
   every subset already evaluated.

The default budget (16,384), the 4,096 per-portfolio bound, generation and the evaluator
are unchanged. Truncation stays explicit (`portfolio_search_truncated`,
`SearchTruncated`); every row below remains truncated. The price is that pairs of the
leader are evaluated before the later single roots. This is a heuristic, not a
guarantee: a better single root among the unevaluated ones can still be missed. The
matrix and `ExtensionIsNeverWorseThanTheOneIngestionIncumbentAtAnyMatrixBudget` check
the measured fixtures only. A seed share of a half also reaches the improvement at the
default budget (on the last evaluation); a quarter leaves a margin of one more root.

### Result on the four-worker fixture

At the default budget the seed pass evaluates two single roots (root 0, then the
one-ingestion incumbent, root 1, which costs the scheduler bound). The extension adds each
later root to root 1 and reaches the 29th of the 62 roots, where the budget ends. The first
two-ingestion plan arrives at root 28; root 29 (the same consumer's second event) ties its
consequences and wins on labor, 354 against 355. The selected pair is root 1 plus
`food/14/delivery/1/eat/diner/1`, so the diner eats at 240–250 and 490–500. This is
equivalent in consequence to the issue's "incumbent plus one food/27 diner root" plan
(urgent `[...,450,0]`, needs component 4 equal to 1) but is the first found in root order.
*History (superseded 2026-10-15):* when #3197 landed, four of four was unreachable because
the evaluator allowed one owner per exact target and both consumers share `cell/3/1`.
#3210 lifted that (the current numbers are in the
[re-measurement](#re-measurement-after-the-capacity-rule-3212)): the larger
fixture's selection at 65,536 and 262,144 expansions is now the incumbent plus
`food/14/delivery/1/eat/pawn/0` (the pawn eats at 305-315 from the stack the diner also
uses), urgent `[75, 38.6, 265, 465, 0]`, needs component 4 equal to 2, labor 354, ending tick
315, which beats the diner's second meal lexicographically (`75 < 325`). Budgets up to 16,384
are unchanged and the original fixture is unchanged. Four of four was still not selected
then (superseded by [#3215](#fixpoint-extension-with-screened-evaluation-3215)).

| Budget | Incumbent before (ingestions, urgent, needs[4], labor) | After |
| ---: | --- | --- |
| 1,024 | 1, `[325,96.875,450,650,0]`, 2, 211 | unchanged |
| 4,096 | 1, same, 2, 198 | unchanged |
| 8,192 | 1, same, 2, 198 | unchanged |
| 16,384 | 1, same, 2, 198 | **2, `[325,96.875,450,450,0]`, 1, 354** (ends tick 500) |
| 65,536 | 1, same, 2, 198 | 2, same as 16,384 |
| 262,144 | 1, same, 2, 103 | 2, same as 16,384 |

The three smaller budgets cannot afford the extension: each full evaluation costs 4,097
expansions and the first improving pair is the third full-cost pair after the leader. Those
rows are unchanged, not regressions.

### Original fixture

Budgets of 16,384 and above are unchanged. At the three smaller budgets the extension
finds the two-meal plan earlier than the plain order did; each result is at least as good:

| Budget | Before (ingestions, urgent, labor, end tick) | After |
| ---: | --- | --- |
| 1,024 | 1, `[75,34.375,200,200,0]`, 103, 250 | 2, `[0,0,0,0,0]`, 218, 362 |
| 4,096 | 1, same, 103, 250 | 2, `[0,0,0,0,0]`, 208, 342 |
| 8,192 | 2, `[0,0,0,0,0]`, 208, 342 | 2, same urgent, 206, 500 (the 16,384 plan) |
| 16,384 and above | 2, `[0,0,0,0,0]`, 206, 500 | unchanged |

These changes are intentional: the lever applies to every bounded search, so a budget too
small to finish the single roots now also reaches a second contribution. The production
caller (`Rounds`) uses the default budget only.

### Measured cost

Same machine and protocol as above (DAVID-PC, Release, .NET 10.0.12, 2026-10-15, one
warmup and three samples, medians). Allocation, expansions and wall time are descriptive,
not gates; they vary with the runtime and machine load. The budget remains the ceiling on
expansions, and the default did not rise, so there is no added latency from a larger
budget. The extension adds one set lookup per evaluation.

| Fixture | Budget | Allocated MB | Elapsed ms | Search expansions | Recipes / ingestions | Labor | End tick |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Original | 1,024 | 30.711 | 36.239 | 1,024 | 2 / 2 | 218 | 362 |
| Original | 4,096 | 119.869 | 200.905 | 4,096 | 2 / 2 | 208 | 342 |
| Original | 8,192 | 260.684 | 261.656 | 8,192 | 2 / 2 | 206 | 500 |
| Original | 16,384 | 509.401 | 303.024 | 16,384 | 2 / 2 | 206 | 500 |
| Original | 65,536 | 1,641.251 | 911.117 | 52,654 | 2 / 2 | 206 | 500 |
| Original | 262,144 | 1,641.249 | 844.133 | 52,654 | 2 / 2 | 206 | 500 |
| Larger | 1,024 | 88.713 | 42.683 | 1,024 | 2 / 1 | 211 | 251 |
| Larger | 4,096 | 363.466 | 195.728 | 4,096 | 2 / 1 | 198 | 251 |
| Larger | 8,192 | 804.734 | 492.537 | 8,192 | 2 / 1 | 198 | 251 |
| Larger | 16,384 | 1,366.145 | 810.000 | 16,384 | 3 / 2 | 354 | 500 |
| Larger | 65,536 | 5,548.106 | 3,203.696 | 65,536 | 3 / 2 | 354 | 500 |
| Larger | 262,144 | 23,383.424 | 13,758.467 | 262,144 | 3 / 2 | 354 | 500 |

Rows can be compared with the [optimized timeline table](#optimized-timeline-representation)
(same machine, earlier session): the larger default budget measured 1,454.295 MB /
1,377.951 ms there and 1,366.145 MB / 810.000 ms here. Run-to-run variance on this shared
machine is not characterized, so this is an observation and not a speed claim. Generation
and composition cost is unchanged (12.746 MB / 10.424 ms larger; 1.350 MB / 1.278 ms
original).

The #3197 test that pinned this selection was replaced by the
[#3215 test](#fixpoint-extension-with-screened-evaluation-3215). The re-recorded
[original](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/productive.quality.json)
and [larger](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/four-workers.quality.json)
snapshots carry the rest.

## Re-measurement after the capacity rule (#3212)

Recorded 2026-10-15 on main after #3211 (same machine and Release build as above), with
the opt-in `WriteSearchDiagnosisMeasurements` run (`search-diagnosis.production.json` and
`.narrow.json`). It changes no production code, budget or baseline: the evaluator now
checks need-target capacity (`MaxPawns`, summed stack counts; #3210) and Hands and the
native bind use the same model (#3211); the search is the #3197 incumbent-seeded
traversal. Statements in the #3195, #3196 and #3197 sections that four of four is
unreachable, refused, or limited by one owner per target are history as of this date;
their measurements stand.

### Scheduler verdict on the `food/27` set

Unchanged from [#3196 item 3](#measurements) and pinned by
`ProductionAcceptsTheFourIngestionReferenceOnASharedStack`: the four reference ingestion
roots, replayed at the production per-portfolio bound (4,096), give 1,023 alternatives,
all feasible, none refused for `conflicting_selected_target_owners` or
`target_stack_quantity_exceeded`; pinning each activity to its reference worker leaves 420,
all feasible, the best serving four ingestions with urgent `[0, 0, 0, 0, 0]` and labor 841.
Four of four is accepted by the scheduler.

### Production incumbent per budget (four-worker fixture, 62 roots)

Ingestions count food ingestion allocations. Urgent is the five-component vector, needs
component 4 the needs-vector entry quoted in the tables above. Every
search is truncated.

| Budget | Expansions | Ingestions | Urgent | Needs[4] | Labor | End tick | Cardinalities entered |
| ---: | ---: | ---: | --- | ---: | ---: | ---: | --- |
| 1,024 | 1,024 | 1 | `[325, 96.875, 450, 650, 0]` | 2 | 211 | 251 | 1 |
| 4,096 | 4,096 | 1 | same | 2 | 198 | 251 | 1 |
| 8,192 | 8,192 | 1 | same | 2 | 198 | 251 | 1, 2 |
| 16,384 (default) | 16,384 | 2 | `[325, 96.875, 450, 450, 0]` | 1 | 354 | 500 | 1, 2 |
| 65,536 | 65,536 | 2 | `[75, 38.6, 265, 465, 0]` | 2 | 354 | 315 | 1, 2 |
| 262,144 | 262,144 | 2 | same | 2 | 354 | 315 | 1, 2 |
| 393,216 *(opt-in)* | 393,216 | 2 | `[75, 34.375, 200, 400, 0]` | 2 | 220 | 250 | 1, 2 |
| 524,288 / 1,048,576 *(opt-in)* | same as above | 2 | same | 2 | 220 | 250 | 1, 2 |

This matched the embedded `four-workers.quality.json` expectations at the six matrix
budgets, which the gate pinned at the time, so the baselines were not stale then. The 65,536 and 262,144
selections (the incumbent plus `food/14/delivery/1/eat/pawn/0`) beat the default's
second diner meal lexicographically (`75 < 325`) with `Needs[4]` 2 against 1, as recorded
in the #3197 section. Both budgets above the default serve the same two ingestions; the opt-in budgets drop the one-ingestion
incumbent root altogether (the pair is `food/0/.../eat/diner/0` plus
`food/14/.../eat/pawn/0`, two ingestions with labor 220), and no budget up to 1,048,576
selects three or four.

### How far the search is from four of four

- **Cardinality reached:** 1 at 1,024-4,096, 2 from 8,192 up (8,192 enters pairs but its
  selection is still one ingestion). No budget measured, up to 1,048,576, evaluates a
  portfolio of three or four roots in the full universe.
- **Where the reference set sits:** the plain cardinality order enumerates 62 / 1,698 /
  28,580 / 339,189 subsets at cardinality 1 / 2 / 3 / 4 (unchanged from #3196), putting the
  reference set at evaluation 357,973. The seed pass and extension skip at most the roughly
  hundred subsets they already evaluated, so the position is unchanged, and an evaluation
  of a multi-root set typically spends the 4,096 scheduler bound. It is out of reach of
  every budget measured, up to the opt-in 1,048,576.
- **The extension is one level:** the #3197 extension adds each other root to the seed
  leader once and never extends the pair it finds, so it cannot form a triple.
- **Reachable in a small universe:** the same search over only the four reference
  ingestion roots (`PortfolioSearchDiagnosisTests`, opt-in `narrow` rows) reaches
  cardinality 4 once the budget allows:

  | Universe | Budget | Expansions | Ingestions | Urgent | Needs[4] | Labor |
  | --- | ---: | ---: | ---: | --- | ---: | ---: |
  | 4 reference roots | 16,384 | 16,384 | 2 | `[75, 34.375, 200, 400, 0]` | 2 | 220 |
  | 4 reference roots | 65,536 and 262,144 | 57,905 (exhausted) | **4** | `[0, 18.413, 185, 185, 0]` | 0 | 947 |
  | incumbent + 4 reference roots | 16,384 | 16,384 | 2 | `[75, 50, 325, 525, 0]` | 2 | 448 |
  | incumbent + 4 reference roots | 65,536 | 65,536 | 3 | `[75, 34.375, 200, 371, 0]` | 1 | 922 |
  | incumbent + 4 reference roots | 262,144 | 74,305 (exhausted) | **4** | `[0, 18.413, 185, 185, 0]` | 0 | 947 |

  The four-ingestion plan is strictly the best on every urgent component, so the selector
  prefers it whenever it is evaluated. Even with five candidate roots the exhaustive
  search needs 57,905-74,305 expansions, about 3.5-4.5 times the default; the 62-root
  universe is far larger.
- **What blocks it:** (1) the universe (62 roots) against a 16,384 budget; (2) the cost of
  an evaluation, since 43 of the 62 single roots and most multi-root sets spend the whole
  4,096 per-portfolio bound, so one level of extension from one leader costs up to about
  61 x 4,097 expansions (an upper-bound estimate, not a measurement); (3) the extension
  stopping at cardinality 2. Generation, feasibility and the evaluator are not limiters.

**Verdict:** four of four is accepted by the scheduler but not reached by the search at
the default budget (nor at any budget below the cardinality-4 position). The default serves
two of four demanded ingestions. The follow-up search work, with candidate levers
(iterating the extension, restricting candidates, cheaper extension evaluations,
composite roots, a larger budget), is tracked in
[#3215](https://github.com/davidarcher/rimgovernor/issues/3215); no search change is made
here. *History (superseded by [#3215](#fixpoint-extension-with-screened-evaluation-3215)):*
"the extension is one level" and "what blocks it" above describe the #3197 traversal; the
fixpoint extension with screened evaluation reaches four of four at the default budget.

## Fixpoint extension with screened evaluation (#3215)

Recorded 2026-10-15 (same machine and Release build as above, one warmup and three
samples, medians). It changes the search in `ForecastCoupledSelection` only: no budget,
evaluator, Hands rule or per-portfolio bound changed, and no need kind or fixture is named.

**Result.** At the default 16,384 budget the four-worker fixture selects four of four
demanded ingestions: urgent `[0, 13.924, 118, 185, 0]`, needs component 4 equal to 0, labor
947, ending tick 567. This is strictly better than the `food/27` reference set (urgent
`[0, 18.413, 185, 185, 0]`) on every urgent component after the first, so it satisfies "the
reference or an equal-or-better one". `ScreenedFixpointExtensionServesAllFourDemandedIngestionsAtTheDefaultBudget`
pins the selection, vector, expansions, explicit truncation and determinism;
`ServedIngestionsGrowWithTheBudgetUpToTheDefault` pins 2, 3, 4, 4 ingestions at 1,024,
4,096, 8,192 and 16,384 in the four-worker fixture.

### The levers

| Lever | Outcome |
| --- | --- |
| 1. Iterate the extension to a fixpoint | Built. Each new leader is extended by every other root until none improves it, so a set of any cardinality is built one root at a time. |
| 3. Cheaper (screened) evaluation | Built. Single roots and every extension step run the scheduler at a bound of 64 expansions instead of 4,096. The depth-first scheduler finds its first complete plans within a few expansions per activity and the rest of the bound only varies the last tasks, so 64 ranks candidates like the full bound. The final leader is re-evaluated at 4,096 and the cardinality sweep resumes at that bound. |
| 2. Restrict candidates to roots relieving unrelieved demand | Measured and dropped: not needed once screening is cheap, and it risks hiding a root that helps only in combination. |
| 4. Composite roots | Not built; the fixpoint reaches the target without it. |
| 5. Larger default budget | Measured and not viable (the reference set sits at plain-order evaluation 357,973). The default is unchanged. |

A larger bound on the same subset is a new evaluation (the dedupe is keyed by bound), so
confirming the leader at 4,096 is not skipped. Truncation stays explicit
(`portfolio_search_truncated`).

### Incumbent per budget

Urgent is the five-component vector; "before" ingestions are from the earlier sections.

| Fixture | Budget | Before (ingestions, urgent, labor, end tick) | After |
| --- | ---: | --- | --- |
| Original | 1,024 | 2, `[0,0,0,0,0]`, 218, 362 | 2, same urgent, 207, 500 |
| Original | 4,096 | 2, same, 208, 342 | 2, same urgent, 206, 500 |
| Original | 8,192 and above | 2, same, 206, 500 | unchanged |
| Four-worker | 1,024 | 1, `[325,96.875,450,650,0]`, 211, 251 | 3, `[75,48.775,320,520,0]`, 370, 370 |
| Four-worker | 4,096 | 1, same, 198, 251 | 4, `[75,34.375,200,320,0]`, 833, 461 |
| Four-worker | 8,192 | 1, same, 198, 251 | **4**, `[0,13.924,118,185,0]`, 947, 567 |
| Four-worker | 16,384 (default) | 2, `[325,96.875,450,450,0]`, 354, 500 | **4**, `[0,13.924,118,185,0]`, 947, 567 |
| Four-worker | 65,536 | 2, `[75,38.6,265,465,0]`, 354, 315 | 4, same as 8,192 |
| Four-worker | 262,144 | 2, same, 354, 315 | 4, same as 8,192 |

The original fixture is never worse: urgent stays `[0,0,0,0,0]` and labor falls (207
against 218 at 1,024, 206 against 208 at 4,096); the end tick moves to the 500 the larger
budgets already chose. Every four-worker row serves more ingestions and improves urgent
lexicographically; labor rises because more food is produced, which the order does not
penalise ahead of urgent needs.

### Measured cost

Allocation, expansions and wall time are descriptive, not gates. "Before" is the parent
commit measured in the same session.

| Fixture | Budget | Allocated MB before / after | Elapsed ms before / after | Expansions after | Recipes / ingestions after | Labor | End tick |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Original | 1,024 | 29.4 / 34.8 | 38.3 / 15.0 | 1,024 | 2 / 2 | 207 | 500 |
| Original | 4,096 | 115.5 / 140.4 | 199.5 / 63.4 | 4,096 | 2 / 2 | 206 | 500 |
| Original | 8,192 | 249.2 / 262.4 | 221.1 / 134.2 | 8,192 | 2 / 2 | 206 | 500 |
| Original | 16,384 | 486.9 / 487.9 | 244.1 / 229.9 | 16,384 | 2 / 2 | 206 | 500 |
| Original | 65,536 | 1,569.4 / 1,589.4 | 787.3 / 829.7 | 53,128 (was 52,654) | 2 / 2 | 206 | 500 |
| Original | 262,144 | 1,569.4 / 1,589.4 | 803.2 / 829.8 | 53,128 (was 52,654) | 2 / 2 | 206 | 500 |
| Four-worker | 1,024 | 84.7 / 63.9 | 37.5 / 25.4 | 1,024 | 3 / 3 | 370 | 370 |
| Four-worker | 4,096 | 347.1 / 276.0 | 188.1 / 118.6 | 4,096 | 4 / 4 | 833 | 461 |
| Four-worker | 8,192 | 767.8 / 511.9 | 470.0 / 210.8 | 8,192 | 4 / 4 | 947 | 567 |
| Four-worker | 16,384 | 1,303.8 / 1,018.2 | 788.3 / 474.1 | 16,384 | 4 / 4 | 947 | 567 |
| Four-worker | 65,536 | 5,362.6 / 6,303.4 | 3,185.4 / 3,393.7 | 65,536 | 4 / 4 | 947 | 567 |
| Four-worker | 262,144 | 22,482.3 / 21,754.4 | 12,842.1 / 11,633.3 | 262,144 | 4 / 4 | 947 | 567 |

The default budget is unchanged and costs less on the four-worker fixture (1,018 MB and 474
ms against 1,304 MB and 788 ms): screened evaluations are cheaper than 4,096-bound ones.
Generation cost is unchanged (12.75 MB, about 8.8 ms). The 65,536 row allocates more
because the extra budget is spent on full-bound evaluations around a better leader; those
budgets are opt-in and not used by `Rounds`.

### Baseline diffs

Both embedded baselines were re-recorded deliberately (`RIMGOVERNOR_PORTFOLIO_OUTPUT`).

- [`productive.quality.json`](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/productive.quality.json):
  labor at 1,024 and 4,096 (207 and 206), the 65,536 and 262,144 rows record 53,128
  expansions (a few more subsets are evaluated before the search exhausts), and finding
  counts move with the searches. The ingestions and urgent `[0,0,0,0,0]` do not change.
- [`four-workers.quality.json`](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/four-workers.quality.json):
  every budget has the new, better selection tabulated above.

### Unverified

Wall time and allocation vary with machine load. The screen bound of 64 was chosen on these
two fixtures; where first complete plans need many more expansions per activity the screen
could rank poorly, which costs incumbent quality, not determinism or boundedness.

## Re-recorded for the capability-days ranking (#3522)

Both snapshots were re-recorded when the comparator moved from the lexicographic vector to the safety floors and
the capability-days ledger ([forecast value](../architecture/forecast-value.md#comparator)). Each vector now carries
a nested `Value` (floors and ledger terms). Where the ledger ties, the unpriced tiebreakers and labor decide, so the
urgent-time components that used to break such ties no longer do:

- `productive.quality.json`: the second meal is eaten by `pawn/0` (`composition/4`) instead of `pawn/1`
  (`composition/5`) at every budget; completion moves from tick 500 to 260. Hunger loss is equal (0).
- `four-workers.quality.json`: 1,024 is unchanged. At 4,096 the third meal is eaten by `diner/0`
  (`delivery/2/eat/diner/0/composition/14`) instead of `pawn/0` (`composition/16`), labor 833 to 848. From 8,192 the same
  four ingestions are served, but the third and fourth meals swap consumers (`delivery/1` is eaten by the diner, `delivery/2`
  by the pawn); labor (947) and completion (567) are equal.

The scheduler's option order changed in #3624: a task tries the pawns its placed prerequisites went to first, then
the cheapest from where each stands, not pawn ids. At the default 16,384 the four-worker fixture serves the same four
ingestions at labor 721 instead of 947 (`ScreenedFixpointExtensionServesAllFourDemandedIngestionsAtTheDefaultBudget`);
the other pins hold. The tables above predate it.

Tie resolution is 0.01 capability-days over a one-day horizon; the 700-tick fixtures scale it to the horizon, so
only differences above that resolution decide.

## Wasted needs work removed (#3467)

`MeasureWarmedAllocationMatrix` also writes `<fixture>.one-day.measurements.json`: generation
and default-budget selection at the production one-day horizon, with the same quality row.

Stage 1 of the [forecast value search engine](../architecture/forecast-value.md#search-engine)
stops three kinds of wasted work:

- The physiology timeline computes no mood. Its capacity was the only part read.
- The search builds no per-point mood trajectory. `ForecastCoupledContext.MoodTrajectory` keeps it
  for tests that inspect it.
- Each pawn's needs result is cached per search, keyed by that pawn's own inputs.

Every quality row, at all six budgets and at one day, is identical before and after. Best of
three samples, same machine:

| Fixture | Horizon | Budget | MB before / after | ms before / after |
|---|---|---|---|---|
| Original | 700 | 16,384 | 576 / 409 | 217 / 195 |
| Original | 60,000 | 16,384 | 2,426 / 613 | 890 / 235 |
| Four-worker | 700 | 16,384 | 1,267 / 781 | 494 / 343 |
| Four-worker | 700 | 262,144 | 26,830 / 14,800 | 10,403 / 6,434 |
| Four-worker | 60,000 | 16,384 | 6,497 / 1,330 | 2,383 / 540 |

## Search repeats removed (#3468)

Stage 2 removes work that schedules repeat:

- Mood is cached apart from needs. Mood reads neither occupancy nor capacity, so schedules that
  differ only in a pawn's work reuse that pawn's mood.
- Schedule-search prefixes are packed interned indices and raw double bits, not formatted text
  (later an incremental hash, [below](#bitset-portfolio-search-and-hashed-prefixes)).
  Plan keys are formatted only for complete plans, which the ranking still orders by.
- Need tasks are indexed once per search, not scanned for each event and pawn.

Every quality row is identical before and after. Allocation, best of three samples (wall time is
within run-to-run noise except at one day):

| Fixture | Horizon | Budget | MB stage 1 / stage 2 | ms stage 1 / stage 2 |
|---|---|---|---|---|
| Original | 700 | 16,384 | 409 / 359 | 195 / 239 |
| Original | 60,000 | 16,384 | 613 / 362 | 235 / 178 |
| Four-worker | 700 | 16,384 | 781 / 679 | 343 / 384 |
| Four-worker | 700 | 262,144 | 14,800 / 13,082 | 6,434 / 7,182 |
| Four-worker | 60,000 | 16,384 | 1,330 / 707 | 540 / 325 |

### Exact mood segments

The mood timeline integrates exact segments between real events instead of 150-tick steps. Every
on/off state (hour band, start, end, expiry, sleep) is read at a segment's midpoint, the clamp at 0
and 1 cuts a segment, and actual mood chases the target in closed form. The old steps read a state
change up to one step late, so the old values carried up to half a step of error.

The criterion is identical selections and feasibility, with mood values inside a small tolerance.
Both fixtures' quality rows stay identical (their mood is unassessed). Every mood timeline in the
domain suite (36,156 calls) was run through the old and new code and compared. The only known-ness
change is the old `mood_event_budget_truncated` refusal for a tiny response interval, which no longer
applies. The largest differences were:

| Value | Largest difference |
|---|---|
| Target exposure | 75 ticks (half a step) |
| Target pressure | 3 ticks |
| Break risk | 0.0003 breaks |
| Actual exposure | 150 ticks over a day |
| Terminal actual mood | 0.0072 |

### Pooled schedule evaluation

Each schedule's evaluation now rebuilds only what depends on it:

- Fixed for a search: each task's material shape, the observed targets and decaying stacks, the
  external stock effects, the material holds before bills, and roof mood by pawn choices and
  closed rooms.
- Schedule events go into one reused, sorted buffer.
- Inventory, plan search and consequence metrics are loops in the old order, with the same sums,
  maxima and finding order.
- Plan search places allocations by task index, sorts options once and formats a plan's text only
  to break ranking ties. The visited prefixes already keep one plan per assignment.

Every quality row is identical to stage 2. The stage 3 column includes the exact mood segments.
Allocation, best of three samples:

| Fixture | Horizon | Budget | MB stage 2 / stage 3 | ms stage 2 / stage 3 |
|---|---|---|---|---|
| Original | 700 | 16,384 | 359 / 191 | 239 / 215 |
| Original | 60,000 | 16,384 | 362 / 191 | 178 / 107 |
| Four-worker | 700 | 16,384 | 679 / 366 | 384 / 213 |
| Four-worker | 700 | 262,144 | 13,082 / 6,818 | 7,182 / 3,981 |
| Four-worker | 60,000 | 16,384 | 707 / 373 | 325 / 205 |

### Pooled evaluator

One evaluator per search owns the scratch every schedule evaluation needs (its ledger, events,
findings and per-pawn rows) and reuses it. The sequences evaluations repeat are interned to integer
ids: a pawn's choices and occupancy, the completed rows each result reads, the need services and the
thermal exposures. Each result is memoized by exactly the completed rows it reads, so schedules that
differ elsewhere share it:

- shelters by the prerequisite rows; planned rooms and development by the development tasks by
  pawn; support by the service rows, and constant for a context with no demands or deliveries;
  maintenance by the completed set; thermal relief and the thermal occupancy by the need-service
  rows.
- The needs timeline is split. Its core (metrics, the needs' own occupancy, memory delta, useful
  food, findings) reads no occupancy and is computed once per choices, grants and renewals; only
  the capacity stage runs per occupancy, with the pawn's free windows memoized per occupancy. The
  capacity projection is loops, with the windows the old grouping produced.
- Material budgets are memoized by the bill bindings. The stock walk runs against a base built
  once (the observed stock and the commitments' own effects) and raises the shortfall findings;
  nothing projects stock trajectories.
- Equal allocations placed by different search branches map to one object, so every
  reference-keyed sequence compares by value.
- Findings and thermal spans are copied once, into the outcome.

Every quality row is identical to stage 3. Allocation and wall time, stage 3 (main at b06f0ff88,
best of three samples) and stage 4 (one sample at the landed change) measured on the same machine:

| Fixture | Horizon | Budget | MB stage 3 / stage 4 | ms stage 3 / stage 4 |
|---|---|---|---|---|
| Original | 700 | 16,384 | 189 / 31 | 274 / 113 |
| Original | 700 | 262,144 | 456 / 71 | 306 / 104 |
| Original | 60,000 | 16,384 | 192 / 32 | 191 / 38 |
| Four-worker | 700 | 1,024 | 25 / 6 | 23 / 7 |
| Four-worker | 700 | 16,384 | 373 / 65 | 304 / 95 |
| Four-worker | 700 | 262,144 | 6,458 / 690 | 3,805 / 1,600 |
| Four-worker | 60,000 | 16,384 | 452 / 57 | 244 / 99 |

At the four-worker 16,384 budget about 13 KB per evaluated plan remains. Roughly 3 KB is the
schedule search's own output (the allocation and plan records it emits and its visited prefixes),
5 KB the evaluation (memo misses: a capacity stage per new occupancy, a needs core and mood per new
choice set; the outcome and its per-pawn needs), and the rest the
portfolio level (consequence vectors with their explanations, subset keys and evidence).

### Bitset portfolio search and hashed prefixes

The portfolio search indexes its roots and the context's tasks once. Each root's prerequisite
closure, the roots it is redundant with and the roots it cannot share a portfolio with are bitsets
over those indexes, so extending a partial portfolio is a word test rather than a pairwise LINQ
scan, and a subset's schedule and evaluation keys are its bit words (closure union, keyed roots,
relieving closure union), built in one scratch and copied only when first seen. The schedule
search knows a prefix by an order-free 128-bit hash: two independent mixes of each placed
allocation (task and pawn indexes, raw time bits), summed, so a placement adds one term and its
undoing subtracts it; the visited set holds the hashes, not packed prefixes.

Every quality row is identical. Allocation and wall time, best of two samples at main
(147d16de2) and of three at this change, on the same machine in one sitting (wall time is within
run-to-run noise):

| Fixture | Horizon | Budget | MB before / after | ms before / after |
|---|---|---|---|---|
| Original | 700 | 16,384 | 31.2 / 29.0 | 116 / 104 |
| Original | 700 | 262,144 | 70.6 / 64.4 | 119 / 99 |
| Original | 60,000 | 16,384 | 31.4 / 29.3 | 38 / 41 |
| Four-worker | 700 | 1,024 | 5.4 / 5.0 | 7 / 7 |
| Four-worker | 700 | 16,384 | 65.8 / 59.6 | 94 / 96 |
| Four-worker | 700 | 262,144 | 699 / 647 | 1,709 / 1,731 |
| Four-worker | 60,000 | 16,384 | 57.9 / 54.6 | 101 / 100 |

What remains per evaluated plan is the schedule search's allocation and plan records, the memo
misses in the evaluation, and the portfolio level's consequence vectors and evidence.

In game (`needs/target-capacity`, `production/coupled-thermal` and `shelter/service-investment` on a
private root, one sitting), every `coupled.select` call that spends its full 16,384-expansion budget
takes 0.11 to 0.31 s at 12 to 99 roots and 300 to 1,000 schedules, on this change and on the previous
landed code alike; the brain's search is not the round review's bound (`planner.military` and the
rest of the scheduler step are, at 0.2 to 0.5 s and 0.9 to 1.3 s mean). `needs/target-capacity` and
`shelter/service-investment` fail their native wait budgets identically on both builds, which is not
this change.

## Sweep skipped when the budget cannot finish it (#3563)

After the greedy extension and the confirmed leader, the search swept every subset by
cardinality at the full bound until the budget ran out. In game (43 reviews of one
colony-review day, session `139dc2a8ced6498bae8450393361d084`) that sweep spent 13,000 of
the 16,384 expansions per review and chose the selection in none of them: the baseline
led in 33 reviews, the extension in 7, the warm start in 3. A sweep the budget cuts short
has seen the subsets in root order up to some cardinality, a lottery over the root order
rather than a search. It now runs only when the budget left can finish it, the subsets not
yet evaluated at the mean cost of an evaluation so far; otherwise the search ends
converged at the confirmed leader, still reported truncated (the sweep was never finishing
either) and with the budget unspent.

Measured with `MeasureWarmedAllocationMatrix` on a quiet machine, parent commit (before)
and this change (after) in one sitting. Selections, labor and completion ticks are
unchanged in every row; only the expansions and the findings the sweep's extra
evaluations added moved.

| Fixture | Budget | Expansions before / after | Allocated MB before / after | Elapsed ms before / after |
| --- | ---: | ---: | ---: | ---: |
| Original | 1,024 | 1,024 / 1,024 | 3.5 / 3.5 | 14.6 / 13.3 |
| Original | 4,096 | 4,096 / 4,096 | 8.9 / 8.9 | 37.4 / 35.9 |
| Original | 8,192 | 8,192 / 4,571 | 17.0 / 9.6 | 78.7 / 38.3 |
| Original | 16,384 | 16,384 / 4,571 | 33.8 / 9.6 | 143.8 / 49.1 |
| Original | 65,536 | 40,450 / 40,450 | 78.4 / 78.4 | 249.1 / 280.8 |
| Original | 262,144 | 40,450 / 40,450 | 78.4 / 78.4 | 231.1 / 232.7 |
| Four-worker | 1,024 | 1,024 / 1,024 | 5.7 / 5.7 | 13.4 / 13.9 |
| Four-worker | 4,096 | 4,096 / 4,096 | 24.7 / 24.7 | 68.9 / 72.4 |
| Four-worker | 8,192 | 8,192 / 8,192 | 43.0 / 43.0 | 121.0 / 124.9 |
| Four-worker | 16,384 | 16,384 / 12,902 | 70.9 / 57.1 | 204.5 / 173.7 |
| Four-worker | 65,536 | 65,536 / 12,902 | 245.2 / 57.1 | 1,070.2 / 189.0 |
| Four-worker | 262,144 | 262,144 / 12,902 | 825.9 / 57.1 | 3,990.2 / 215.2 |

The original fixture (8 roots) still exhausts its 255 subsets at 65,536 and above, where the
sweep fits; at 8,192 and 16,384 it ends at the confirmed leader. The four-worker fixture
(62 roots) has no budget that finishes its sweep, so every budget from 16,384 up ends at the
same 12,902 expansions; the 65,536 and 262,144 rows (opt-in, not used by `Rounds`) stop
spending 0.2 to 0.8 GB on a sweep that changed nothing. At the one-day production horizon
and the default budget: original 16,384 expansions, 34.8 MB, 93 ms before, 4,768, 13.0 MB,
37 ms after; four-worker 16,384, 72.1 MB, 306 ms before, 4,098, 23.2 MB, 72 ms after.

With the sweep no longer spending whatever is left, the budget is a ceiling the converged
search rarely reaches, so the default was raised from 16,384 to 65,536 in the same change:
in game the extension ran out of budget before the confirm phase in 2 of the 10 reviews that
found a leader, and the seed pass, capped at a quarter of the budget, screened about 64 of
up to 99 roots. At the raised default the one-day rows are original 4,768 expansions,
13.0 MB, 33 ms (unchanged: the search converged before either ceiling) and four-worker
5,723, 30.7 MB, 87 ms (the larger seed share screens every root before the extension). The
explicit-budget rows above and both baselines are unchanged by the default.

Both embedded baselines were re-recorded (`RIMGOVERNOR_PORTFOLIO_OUTPUT`): the expansions
rows above and the findings the sweep's extra evaluations added. It is the first
re-recording since the consequence vectors changed, so the recorded vectors and findings
moved with it. The phase test asserts the skipped sweep at the default budget and the
finished one at 2^20; the diagnosis test asserts the budget left unspent.

## Forecast evidence instrumentation (#3713)

`ForecastEvidenceMeasurementTests.MeasureForecastEvidenceOverhead` reuses the
original and four-worker fixtures at the 60,000-tick production horizon and the
production selector budget. Set `RIMGOVERNOR_PORTFOLIO_OUTPUT` as above and run:

```powershell
& brain/tests/RimGovernor.Brain.Domain.Tests/bin/Release/RimGovernor.Brain.Domain.Tests.exe -method '*MeasureForecastEvidenceOverhead' -parallel none
```

The emitted `*.evidence.measurements.json` records the measured assembly,
all three warmed samples, output path and pending row count. Raw files stay
uncommitted. With a gate build, the same executable lives under
`.rimgovernor/gate/artifacts/bin/RimGovernor.Brain.Domain.Tests/release/`.

Measurements separate selection from the pending owner's capture/retirement and
from offline reader/report work. Capture includes the production `Decide` envelope
and enqueue into the real OTLP file writer. The asynchronous writer's allocations
are outside the calling thread's allocation counter; file size is measured after
disposal flushes it. Retirement resolves four accumulated reviews at the endpoint.
The portfolio fixture supplies no admitted task bindings, so task observations are
explicit identity gaps. It is not native task-completion evidence. Endpoint needs
are fixture observations, not gameplay results.

One unrecorded warmup precedes three recorded samples. Capture retains the four
reviews in one owner; retirement prepares a fresh owner with four captures before
each sample, excluding that setup from the measured interval. The resulting log
contains twenty captures and sixteen retired reviews. The offline reader and JSON
report each use the same one-warmup/three-sample protocol over that file. The
capacity experiment disables telemetry callbacks, fills the owner to its 4096-row
bound and reports a process-wide forced-GC retained-byte estimate. Those
collections are outside every timed sample. Retained bytes are descriptive,
including runtime noise, not an allocation or memory-budget assertion.

These measurements cover the incremental synchronous evidence path, not full
Rounds review latency, native read costs, task polling, or asynchronous sink CPU.
A full review comparison and real native task/endpoint behavior are not established
by these fixture measurements. No test threshold or forecast behavior changes follow
from these samples.

Recorded on DAVID-PC, .NET 10.0.12 x64 Release, 2026-10-21. The before build is a
`git archive d6085f76c` extraction (before live capture); the candidate is
`276c9b99a` plus this reader-envelope fix and measurement harness. An archive
built below another checkout can inherit its assembly Git version; the archive
SHA, not that inherited version string, identifies this baseline. The unchanged
`MeasureWarmedAllocationMatrix` protocol was run on both builds in the same
sitting. All one-day quality fields agree exactly. Rows below give three samples,
bytes in decimal MB and elapsed time in milliseconds:

| Fixture | Selector MB before | Selector MB after | Selector ms before | Selector ms after |
| --- | --- | --- | --- | --- |
| Original | 14.3285 / 14.3289 / 14.3280 | 14.3142 / 14.3133 / 14.3133 | 28.14 / 17.76 / 17.65 | 18.24 / 17.80 / 17.62 |
| Four-worker | 29.1204 / 29.1200 / 29.1204 | 29.1260 / 29.1256 / 29.1260 | 36.23 / 41.06 / 35.26 | 32.74 / 40.46 / 33.49 |

The selector does not call the new evidence path; these are control measurements,
not a claimed speedup. The separate evidence benchmark starts selection earlier
in a fresh process and runs colder (61.7–75.3 ms / 125.1–153.9 ms); do not compare
those figures to the warmed matrix as a regression. No selector change explains
the small byte/time variation above.

| Incremental stage | Original bytes, three samples | Original ms, three samples | Four-worker bytes, three samples | Four-worker ms, three samples |
| --- | --- | --- | --- | --- |
| One capture and enqueue | 303048 / 302520 / 303608 | .817 / .592 / .551 | 156392 / 155304 / 157848 | .329 / .380 / .338 |
| Retire four reviews and enqueue | 259696 / 259696 / 259696 | .462 / .459 / .444 | 622024 / 622024 / 622024 | 1.298 / 1.301 / 1.281 |
| Offline reader | 12046272 / 12048608 / 12046496 | 36.314 / 37.511 / 34.582 | 9640952 / 9638968 / 9638744 | 30.814 / 26.632 / 26.540 |
| Offline report and JSON | 1932848 / 1932848 / 1932848 | 3.003 / 2.564 / 2.509 | 1477008 / 1477008 / 1477008 | 2.226 / 2.392 / 2.298 |

These additional evidence operations and telemetry did not exist at the baseline,
so their measured costs are incremental to it. They are not the entire runtime
review delta. Original/four-worker logs contain respectively 300/260 predictions
and 280/212 observations, occupying 1,324,564/1,024,060 bytes. Original has more
selected unbound tasks and immediate gaps; worker count alone does not predict
log volume. Pending rows after four captures are 20/48; populated capacity is 4096
in both, with estimated retained bytes 1,477,992/1,448,552. Capture does not retain
the candidate graph. Three samples do not establish a general latency bound.

The production-shaped measurement also caught an integration defect: the reader
looked for evidence directly in the row payload while `Decide` stores it under
`attrs`. Before correction the same emitted workload decoded zero predictions or
observations. The serialized roundtrip regression now uses `Decide`, and the
reader accepts its canonical envelope; there is no fallback for the former
test-only event layout. These fixture records demonstrate serialization and
matching only, not native residual accuracy or completion.

### Native horizon correlation evidence (#3713)

The focused `production/forecast-residuals` run on 2026-10-21 passed in 104 seconds
against native package `c21ae6cd6347`, with the case's meal staging corrected to
split thirty meals into legal stacks (`StageThing.Near`). No fixture mutation or
extra scheduler wake occurred after service start. Seed `rimgovernor-lab`, save
SHA256 `a4edfb4cd68d55ebce70dee16914087c13840c9f3c84c30af14249ca12259e41`, fixture
hash `62909b3739019f84`; all installed DLC were active. The lab retained three
colonists (Crew.Size is a minimum); one was staged tired beside the bed.

The registered offline reader decoded 478 predictions with zero reader gaps.
The first ordinary endpoint read was tick 60,990 against target 60,000: nine known
food-level, nine rest-level and nine actual-mood rows, across three initial
reviews and three pawns. All 27 are timing mismatches with 990 ticks of lag,
so none enters aligned scalar statistics. Nine endpoint rows are Replaced and
18 Unknown; absence of a detected change does not establish consistency.
There are 24 genuine task-completion intervals across 15 task identities:
14 Consistent and 10 Unknown. For example, the first sleeping task was predicted
to finish at 11,213.249 ticks and observed complete in (10,997, 11,400], giving
an observed-minus-predicted interval (-216.249, 186.751]. Another 14 bound task
rows have detected replacement/divergence, not completion evidence.

The report retains all 478 prediction rows: 24 intervals, 27 timing mismatches,
82 missing completion evidence (68 selected tasks without execution identities
and 14 diverged), 175 missing observations (later targets had not elapsed), and
170 unknown predictions. Explicit reasons include `food_stock_prediction_unavailable`,
`temperature_prediction_unavailable` and `mood_prediction_unavailable`; known
mood observations above are actual native mood, not an inferred thought total.
No scalar error or accuracy threshold was manufactured for a pass.

Candidate-only context from the existing `planner_step` rows: 27 Rounds steps,
235–4,413 ms total duration, median 508 ms, mean 836 ms. Summed `stage_alloc_kb`
ranged 56,933–3,458,016 KB per step (mean 731,701 KB); these are process-wide
allocation deltas that include concurrent runtime work. They are not an
attributable instrumentation delta or a before/after native comparison. The
captured full OTLP log was 15,782,660 bytes, including unrelated runtime kinds;
use the fixture measurements above for incremental evidence-path costs.

The passing `result.json` embeds correlated raw rows, cohorts, provenance and
coverage gaps. Reproduce its complete retained-window report with
`RimGovernor.cmd forecastresiduals -logs <case-output>/otlp/logs.jsonl`.
The initial attempt failed before service start because thirty meals exceeded
a single native stack; no gameplay evidence came from that attempt.

### Native food-stock correlation rerun (#3475)

The run above predates live food-stock evidence: its FoodStock rows were all
`food_stock_prediction_unavailable`. After `ea9f42eff` made the detailed
evaluator export eligible per-resource lots, the same case was rerun once on
2026-10-22 and passed in 107 seconds against a native package built from a
clean `739274879` source tree (current main). Same staging: seed
`rimgovernor-lab`, fixture hash `62909b3739019f84`, save SHA256
`6c889715a93d8de0306f49a3e53e7ecfe482537330a7012c8fbf20fccb7d676d` (the lab
save regenerated for the rebuilt package), all installed DLC active, three
colonists, one staged tired. No fixture mutation or extra scheduler wake after
service start; the game log carries no error.

The registered offline reader over the retained `otlp/logs.jsonl` (15,235,238
bytes) decoded 469 predictions and 273 observations across 27 reviews with
zero reader gaps; every observation matched a prediction. FoodStock has 27
prediction rows, all known, for the one eligible resource (`MealSurvivalPack`,
unit `items`, scope `map_food_supply/eligible_nonreserve/holder_and_diet/v1`);
no FoodStock row reports a prediction, census or eligibility unknown. The three
initial reviews (origin tick 0) predicted 25, 30 and 30 items at target 60,000
and the first ordinary read observed 17 items at tick 60,990 through the same
scope, provenance `rounds_native_observation`. These three are timing
mismatches with 990 ticks of lag, so no aligned scalar FoodStock statistic
exists; the other 24 FoodStock targets (63,000 and later) had not elapsed.
The eligible count fell from 30 staged packs to 17; the run records that
count, not its cause. The same read supplied nine known food-level, nine
rest-level and nine actual-mood rows at the same lag (nine Replaced, 18
Unknown). Task completion has 26 genuine intervals across 20 task identities
(16 Consistent, 10 Unknown); the first sleeping task was predicted at
11,213.249 and observed complete in (10,966, 11,400], an observed-minus-predicted
interval (-247.249, 186.751]. 74 bound task rows lack completion evidence (55
without an execution identity, 19 diverged), 196 later targets had no
observation, and 143 predictions remain unknown (`mood_prediction_unavailable`
50, `temperature_prediction_unavailable` 93). Shelter temperature is still
predicted for no room. No scalar error or accuracy threshold was manufactured.

The embedded `forecast_residuals` block in `result.json` reports 450
predictions and eleven `missing_prediction` gaps, all for the final review
`1/27`. The case probe reads the prediction and observation kinds as two ring
snapshots, and the second snapshot saw review 27's immediate gap observations
before the first had its 19 predictions; the whole-log reader has them and
reports no gap. Treat the reader output, not the probe's snapshot, as the
run's count. Artifacts (passing `result.json`, `otlp/logs.jsonl`,
`native-residual-report.json`) stay under the worktree's
`.rimgovernor/forecast-evidence-3475/`, uncommitted.

## Outcome-evaluation budget (#3722)

The selector's allowance is `ForecastSearchBudget(Evaluations, Traversal)`
([portfolio selection](../architecture/portfolio-selection.md)). `Evaluations` counts
outcome evaluations: one unit per complete plan a schedule search hands to the evaluator
(`CoupledSearchCache.Evaluate`), none for a schedule or outcome reused from the search's
cache. `Traversal` bounds the scheduler expansions of the same comparison, so refused,
duplicate, infeasible and unknown-heavy branches stay finite when they evaluate nothing.
The baseline is mandatory: counted and reported apart (`BaselineEvaluations`,
`BaselineExpansions`), charged to neither allowance. The warm start, the seed share, the
extension, the confirmation, the schedule search's capacity-recovery retries and the
planned-unplaced reselection draw from one ledger, and each allowance's end is its own
finding (`outcome_evaluations_exhausted`, `traversal_bound_exhausted`). Both counts bound
algorithmic work, never wall time.

The matrix (`PortfolioAllocationTests.Budgets`) is in evaluation budgets, each with the
default traversal bound of 65,536: 256, 1,024, 2,048, 4,096, 8,192 (the production
default), 16,384 and 65,536. Both snapshots were re-pinned in that unit on 2026-10-23
(DAVID-PC, Release, .NET 10.0.12, the gate's build of this change; `EvaluationBudget` and
`Traversal` key each row, and `Quality` carries `OutcomeEvaluations`, `BaselineEvaluations`,
`BaselineExpansions` and `SearchPhases` with each phase's expansions, schedule searches and
outcome evaluations). A former expansion budget is not an evaluation budget: the two are
comparable only through what the former code spent in evaluations, which was measured on
the parent commit (`739274879`) with a temporary probe before the change and is quoted
below as "former".

### Derivation of the default

The grooming rule: the outcome evaluations the former 65,536-expansion default spent on the
four-worker fixture at the one-day horizon, rounded up to a power of two. The former code
spent **1,728** outcome evaluations there (the mandatory baseline and 1,727 screened plans
over all 91 single roots; the extension then found nothing that served) and 5,723
expansions, so **2,048** by the rule. 2,048 keeps both fixtures' default selection (below),
but the former budget bought a different number of evaluations on every graph: 1,728 on the
four-worker fixture at one day (3.3 expansions per evaluation), 3,138 at 700 ticks (4.1),
7,833 on the original fixture at 700 ticks, where the sweep runs (5.2), and about 13,000 on
the ten-root planned-shelter fixtures, whose schedule searches cost a few expansions each.
At 2,048 four default-budget tests regressed: the three `PlannedShelterTests` fixtures find
the complete shelter only in the cardinality sweep, which the affordability gate (the
subsets left, at the mean cost so far, now in evaluations as well as expansions) skipped at
2,048 where the former default afforded it, and two of them selected an incomplete shelter;
and `ScreenedFixpointExtensionServesAllFourDemandedIngestionsAtTheDefaultBudget` was cut at
exactly 2,048 inside the confirm phase (the four-worker 2,048 row below: 300 of the
confirm's 742 plans evaluated, `outcome_evaluations_exhausted`).

Decision (David, 2026-10-23): `DefaultEvaluations = 8192`, the smallest power of two at
which every existing default-budget test keeps its selection and the sweep runs where the
former default ran it, with `DefaultTraversal = 65536` keeping scheduler work at the former
ceiling. The four tests were not rewritten to accept the regression and the affordability
estimate was not changed; tuning the default on measured quality and cost is #3725.

### Measured at the default

Phases are expansions / schedule searches / outcome evaluations; the baseline phase is
0 / 0 / 1 in every row (the baseline portfolio has no schedule to search, so its mandatory
work is one evaluation). Medians of three samples; MB and ms are descriptive.

| Fixture | Horizon | Outcome evaluations | Expansions | Seed | Extend | Confirm | Selection | MB | ms |
| --- | ---: | ---: | ---: | --- | --- | --- | --- | ---: | ---: |
| Original | 700 | 763 | 4,551 | 327 / 8 / 95 | 128 / 11 / 19 | 4,096 / 1 / 648 | `composition/1` + `/4`, 2 ingestions, labor 206, tick 260 | 19.7 | 52 |
| Original | one day | 1,081 | 4,739 | 451 / 11 / 133 | 192 / 17 / 31 | 4,096 / 1 / 916 | `composition/1` + `/6`, labor 206, tick 500 | 26.7 | 22 |
| Four-worker | 700 | 3,138 | 12,736 | 3,968 / 62 / 1,174 | 4,672 / 228 / 1,221 | 4,096 / 1 / 742 | `/3`, `/6`, `/10`, `/16`, 4 ingestions, labor 721, tick 459 | 103.2 | 99 |
| Four-worker | one day | 1,728 | 5,888 | 5,888 / 91 / 1,727 | (unserved: nothing to extend) | — | baseline; nothing selected | 49.1 | 46 |

Every default selection is the one the former default produced, and the evaluation counts
match the former code's (1,081 and 1,728 at one day, 763 and 3,138 at 700 ticks). The
expansions differ from the former counts for two accounting reasons, not a search change:
the former count added one unit per evaluated subset (the one-day original fixture's former
4,768 were 4,739 scheduler expansions plus 29 schedule searches), and a capacity-recovery
retry's expansions are now counted (the four-worker one-day search's 5,888 are the former
5,723 less 91 subset units plus the 256 expansions of the screened searches' retries, which
the former count omitted; the 700-tick row's 12,736 against the former 12,899 is the same
arithmetic, 291 subset units out and 128 retry expansions in).

### Lazy outcome evaluation (#3723)

The search now prices an evaluated outcome's floors, harm and goal terms at evaluation and
the rest of its ledger only when a comparison reaches the ledger or a consumer needs the
complete vector ([lazy outcome evaluation](../architecture/forecast-value.md#search-engine)).
Both snapshots are unchanged by it: the same selections, counts and phases at every budget
(checked by regenerating both JSONs against the pinned files). `LazyEvaluationTests` pins
the avoided work on the four-worker fixture at 700 ticks and the default budget: of the
3,138 outcome evaluations, 2,923 are rejected subsets ranked against a leader, 435 of which
(15%) lost at a floor (`lost_to_leader:urgent_zero`, `death_risk_floor` or `break_risk_cap`)
and never priced their ledger, reported without a vector; the other 2,488 reached the ledger
or a later stage and carry the complete vector. The evaluation count is #3722's, since a
started evaluation costs one unit wherever its comparison stops.

### Row mapping and changed constrained-budget selections

Former rows are the parent commit's output at an expansion budget with the evaluations it
spent; the new row is the evaluation budget nearest that spend. Ids are `composition/N`.

Four-worker fixture, 700 ticks:

| Former expansion budget | Evaluations it spent | Former selection | Evaluation budget | Outcome evaluations | Selection |
| ---: | ---: | --- | ---: | ---: | --- |
| 1,024 | 257 | `/16` + `/12`, 2 ingestions, labor 370, tick 370 | 256 | 257 | same, allowance spent in the extension |
| 4,096 | 929 | `/16`, `/12`, `/14`, 3 ingestions, labor 848, tick 478 | 1,024 | 1,025 | **`/1`, `/8`, `/10`, 3 ingestions, labor 844, tick 476**, allowance spent in the extension |
| 8,192 | 2,069 | `/3`, `/6`, `/10`, `/16`, 4 ingestions, labor 721, tick 459 | 2,048 | 2,049 | same, allowance spent in the confirm (300 of 742 plans) |
| 16,384 and up | 3,138 | same | 4,096 | 3,002 | same, converged (56 of 62 roots screened, confirm complete) |
| | | | 8,192 and up | 3,138 | same, converged (every root screened) |

Original fixture, 700 ticks:

| Former expansion budget | Evaluations it spent | Former selection | Evaluation budget | Outcome evaluations | Selection |
| ---: | ---: | --- | ---: | ---: | --- |
| 1,024 | 196 | `/1` + `/4`, 2 ingestions, labor 207 (pinned before #3624), tick 260 | 256 | 257 | `/1` + `/4`, labor 206, tick 260, allowance spent in the confirm (160 of 648 plans) |
| 4,096 | 689 | `/1` + `/4`, labor 206 | 1,024 | 763 | same, converged |
| 8,192 and 16,384 | 763 | same | 2,048 to 8,192 | 763 | same |
| 65,536 and 262,144 | 7,833 | same, sweep finished | 16,384 and 65,536 | 7,833 | same, sweep finished (35,780 / 99 / 7,070) |

The one changed constrained-budget selection is the four-worker 1,024-evaluation row
against the former 4,096-expansion row: both serve three ingestions, but the seed share is
now a quarter of the evaluations left (256, which screens 21 roots) where it was a quarter
of the expansions (1,024, 18 roots), so the extension starts from a different leader and
ends at `/1`, `/8`, `/10` with labor 844 instead of `/16`, `/12`, `/14` with labor 848. The
original 256-evaluation row records labor 206 where the former 1,024-expansion pin recorded
207; that pin predates #3624 and the parent commit's labor at 1,024 expansions was not
re-measured, so whether the difference is the cut confirm or the scheduler order is not
established. Every other row keeps its selection, labor and completion tick.

### Baseline diffs

Both snapshots were re-recorded (`RIMGOVERNOR_PORTFOLIO_OUTPUT`). Rows are keyed by
`EvaluationBudget` and `Traversal` rather than `Budget`; `Quality` gains
`OutcomeEvaluations`, `BaselineEvaluations`, `BaselineExpansions` and `SearchPhases`;
`Findings` carry `outcome_evaluations_exhausted` on the rows the allowance ended (original
256; four-worker 256, 1,024 and 2,048). The previous pins predated #3624: their four-worker
rows at 8,192 expansions and up still showed `/1`, `/8`, `/10`, `/16` at labor 947, while
the parent commit's output at those budgets was already `/3`, `/6`, `/10`, `/16` at labor
721 (the probe above), which the new pins carry. That difference is the stale pin, not this
change. The full matrix, medians of three samples:

| Fixture | Evaluation budget | Outcome evaluations | Expansions | Ingestions | Labor | End tick | MB | ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Original | 256 | 257 | 4,487 | 2 | 206 | 260 | 10.3 | 25 |
| Original | 1,024 to 8,192 | 763 | 4,551 | 2 | 206 | 260 | 19.7 | 52 to 68 |
| Original | 16,384 and 65,536 | 7,833 | 40,331 | 2 | 206 | 260 | 177 | 200 to 396 |
| Four-worker | 256 | 257 | 1,024 | 2 | 370 | 370 | 9.4 | 9 |
| Four-worker | 1,024 | 1,025 | 4,096 | 3 | 844 | 476 | 36.4 | 38 |
| Four-worker | 2,048 | 2,049 | 10,816 | 4 | 721 | 459 | 74.8 | 83 |
| Four-worker | 4,096 | 3,002 | 12,352 | 4 | 721 | 459 | 100.3 | 121 |
| Four-worker | 8,192 to 65,536 | 3,138 | 12,736 | 4 | 721 | 459 | 103.2 | 96 to 99 |

### Unverified

The roughly 13,000 evaluations the former default was worth on the planned-shelter fixtures
is the estimate from the 2,048 attempt, not a pinned measurement. No native run was made;
in game the allowance is reported on every `rounds_review` and `coupled_selection` row
([signals](../contracts/otel-signals.md#kinds)), and the nightly supplies that evidence.
