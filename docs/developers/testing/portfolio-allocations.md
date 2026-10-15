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
normal test gate does run both six-budget deterministic quality tests. To run
those alone, use `-method '*BudgetMatrixPreservesPhysicalChoicesAndConsequences'`.
Their embedded [original](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/productive.quality.json)
and [larger](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/four-workers.quality.json)
expectations pin physical identities, participants, travel/wait/work/timing,
learning, complete baseline/selected consequence vectors, findings, expansions
and truncation. JSON `null` means unknown, never zero. An explicit output run
writes candidate snapshots outside the source tree; it does not replace approved
expectations. Keep raw outputs and traces uncommitted.

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
is zero. All retain `serious_harm_probability_unassessed`, `unassessed_mood`
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
records and plain arithmetic; it calls no `PolicyFunctions` scheduling code (a test
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
only (`PlainCardinalityOrderNeverLeavesTheSingleRootSweep`). **Measurements** below are
asserted by those tests unless marked *opt-in*; the opt-in run
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
2. **Traversal never reaches cardinality 4, nor most of 2.** After exclusive-outcome and
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
   **0 feasible**; every alternative carries `conflicting_selected_target_owners` on
   `cell/3/1`. Pinning each activity to its reference worker leaves 420 alternatives,
   still 0 feasible and all with the same finding, so the refusal does not depend on
   assignment or on the truncation. A diner ingestion plus a pawn ingestion
   (1,018 alternatives) is already refused. The scheduler cannot be given a larger
   bound: `ExpansionBudget` above 4,096 is rejected as an invalid activity graph or
   budget (asserted), so a larger-bound replay is not possible without a production
   change and was not made.
4. **Why the reference is refused (true infeasibility under production rules).** Both
   consumers' ingestion tasks name the same observed target `cell/3/1` (the fixture
   copies the first pawn's need targets to `diner`); the evaluator allows one pawn to
   own an exact target across a portfolio. The #3195 checker validates physical
   constraints and does not model target ownership, so "4 of 4 feasible" there is a
   physical statement, not one the production evaluator accepts for this fixture.
   One consumer's two ingestions are accepted: the diner pair
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

- The ceiling for this fixture under the current evaluator is two of four ingestions:
  the worst-pawn components of the urgent vector cannot improve while the second
  consumer is unfed, and a two-consumer portfolio is refused by the shared target.
  Two ingestions is the best vector observed in every run; it is not proven optimal.
- A remedy that reaches the pair stage sooner (a cheaper single-root sweep, pair-first
  ordering, or a larger budget) would find the two-ingestion plan; the observed
  crossover is between 262,144 and 393,216 expansions. Choosing the lever is #3197.
- Whether the shared target in `LargerFacts` represents a real colony (two pawns using
  one dining target) or a fixture artifact is a separate question; if distinct targets
  were intended, the four-ingestion reference would also need a feasibility run.

### Verdict

**Feasible plan missed, limiter = subset traversal exhausting the budget inside the
cardinality-1 sweep** (43 of 62 single roots each spend the whole 4,096 scheduler bound;
the sweep costs 185,139 expansions, so every budget up to 65,536 never leaves the first
18 roots and 262,144 reaches 20 pairs). The missed plan serves two of four ingestions
(one consumer's both events) and is strictly better than the incumbent; four of four is
refused by the evaluator's exact-target ownership on the fixture's shared target, so the
remedy (#3197) can aim at two, not four, on this fixture. Generation and feasibility are
not limiters; the per-portfolio bound is fixed at 4,096 and only matters through its cost
per evaluation.

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
Four of four stays unreachable: the evaluator allows one owner per exact target and both
consumers share `cell/3/1` (#3202).

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

`PortfolioSearchDiagnosisTests.IncumbentSeededExtensionServesBothEventsOfOneConsumerAtTheDefaultBudget`
pins the exact selection, every food allocation's worker and interval, the consequence
vector, labor, expansions, truncation and determinism. The re-recorded
[original](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/productive.quality.json)
and [larger](../../../brain/tests/RimGovernor.Brain.Domain.Tests/Baselines/four-workers.quality.json)
snapshots carry the rest.
