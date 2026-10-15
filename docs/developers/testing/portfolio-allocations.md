# Coupled portfolio allocation baseline

[Documentation map](../../README.md) · [Choose checks](choose-tests.md) ·
[Productive learning contract](../contracts/skill-learning.md#productive-cooking-in-normal-selection)

`PortfolioAllocationTests` measures normal generation/composition and shared
selection independently. The baseline policy revision is
`3d130260b6bded4b5e24f48fead51d9caca34188` (#3164), with only the test harness and
expectations added. It preserves the 16,384 default, traversal, scheduler bounds,
all generated roots and options, and explicit unknowns. This is pure-policy
allocation/choice evidence; it makes no native gameplay or completion claim.

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

## Measured baseline

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
larger workload still serves only one of four demanded ingestions at all measured
budgets; the last row removes unhelpful labor without improving hunger relief.
Do not report that labor reduction as satisfying four meals.
[Issue #3193](https://github.com/davidarcher/rimgovernor/issues/3193) tracks the
separate feasibility/search-quality investigation; allocation work preserves
these results.

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
both matrix fixtures pass. The larger fixture still serves only one of four
ingestions; #3193 remains the separate quality investigation. Native execution
is not needed for this representation/allocation claim.
