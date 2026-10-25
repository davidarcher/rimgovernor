# Food review queue

[Collection](README.md) · [Food requirements and evidence](food-security.md)

The current review has one documentation disagreement and five proposed evidence
investments. Evidence labels F1–F6 refer to the food requirements page. Source
inspection has identified an automatic-recovery evidence gap. A current game run
would establish whether that gap hides a gameplay failure.

## Q1 — Who chooses food work?

The [controller contract](../developers/contracts/controller-contracts.md#priority-evaluation)
says vanilla work priorities schedule routine food work. The
[pawn activity contract](../developers/contracts/pawn-activities.md#needs-forecast-support-matrix)
says the governor explicitly chooses controlled eating and supported work.

The inspected code contains explicit work selection. The berry case and
[#3668](https://github.com/davidarcher/rimgovernor/issues/3668) describe an older
approach in which the governor designates plants and vanilla chooses harvesting
and eating jobs. The case checks designation and consumption, leaving the choice
of each job unobserved.

**Agreed product direction:** The governor is responsible for autonomous feeding.

**Required investigation:** Trace the current berry scenario and any remaining
designation-only paths, then align the contract and case descriptions. This
clarification will guide test design and diagnosis of feeding failures.

**Tracking:** [#4000](https://github.com/davidarcher/rimgovernor/issues/4000), P2.
Open-issue searches for controller contracts and work priorities found no matching
report before filing it.

## Implementation and documentation priorities

1. **Correct the execution descriptions (Q1).** The disagreement affects both
   interpretation of existing tests and the choice of code to investigate when
   feeding stalls. Resolve it against the current running path.
2. **Investigate a current feeding failure if T1 reveals one.** Starvation has high
   player impact. Preserve the setup and observed result, diagnose the failed step,
   and file a focused behavior issue. Current inspection alone supplies insufficient
   evidence to choose an implementation fix.

The tests below address evidence gaps. Check existing tests and retained results
before adding work. An unexamined result stays an evidence question until there is
concrete evidence of incorrect behavior.

## Recommended checks

### T1 — Feeding from an unarmed start

**Priority:** First. It checks the central feeding outcome using an existing,
small game case.

**Requirement and claim:** RG-FOOD-001/002 — an unarmed colonist obtains and eats
berries autonomously.

**Setup and result:** Reuse `food/forage-unarmed` (F5): one colonist, mature bushes,
no food or weapon. Pass requires designation of a staged source and recorded
berry consumption within the existing tick budget.

**Evidence layer:** Native acceptance, because harvesting and ingestion are game
effects. F4 checks forage selection using a preselected plan. F5 has a registered
case but no current result was examined. Reuse a retained result if its code,
inputs and environment still apply; otherwise run the case and retain `result.json`.

### T2 — Recover and get fed

**Priority:** Second. It addresses the largest gap in the inspected cooking path:
a failed attempt followed by an independently chosen replacement and eating.

**Requirement and claim:** RG-FOOD-003/004 — after losing a selected ingredient,
the governor chooses usable replacement work and the colonist gets fed.

**Setup and result:** First survey existing cooking and coordination cases. If the
claim is uncovered, stage one hungry colonist, a supported cooking option and an
alternative usable option. Remove the selected input after assignment as the one
controlled disruption. Allow the governor to choose the recovery. Pass requires
that new choice, observed output if cooking is used, and recorded eating within
the case budget.

**Evidence layer:** A short native acceptance case exercises both the real failure
and autonomous response. F6 directs its own recovery attempts. F4 supplies the
observations used by continuation decisions. Prepare the starting conditions close
to the effect being checked and diagnose stalls within the existing charter limits.

### T3 — Food arrives before the shortage

**Priority:** Third. Timing mistakes can strand the whole colony while apparently
promising crops are still growing. This decision can be checked cheaply.

**Requirement and claim:** RG-FOOD-002 — the selected supply plan addresses a shortage
before a late crop matures.

**Setup and result:** Inspect current supply tests first. If needed, construct a
policy test with stock running out before harvest and one earlier source within
the available labor budget. Pass requires timely replacement. In a second setup
with no timely source, the plan must retain the gap.

**Evidence layer:** Pure decision test. F4's forage example begins after a food
source has already been selected, leaving the timing choice outside its assertions.

### T4 — Recover from missing information

**Priority:** Fourth. It checks whether missing data can hide a shortage or prevent
a later review from resuming useful planning.

**Requirement and claim:** RG-FOOD-004 — an unread food observation leaves adequacy
unresolved, and a later valid observation restores planning against the real need.

**Setup and result:** Survey boundary tests first. If needed, provide an unavailable
food observation followed by valid information showing a shortage. Check the
published status after the failure and the renewed food plan after the valid read.
Pass requires no false report of adequacy and a plan reflecting the shortfall.

**Evidence layer:** Boundary integration test. F4 establishes an exception for
missing inputs; publication and the next review remain unassessed.

### T5 — Continued food replacement

**Priority:** Later, after surveying storage and the remaining food sources.
Sustained feeding depends on those capabilities as well as the short food path.

**Requirement and claim:** RG-FOOD-001/002 — the colony continues to feed itself
across seasons and disruptions.

**Setup and result:** Agree a campaign colony and explicit food/health outcomes
before running. Allow autonomous play after setup and retain observed supplies,
consumption and colony condition. Acceptance criteria and duration still require
a product decision.

**Evidence layer:** Campaign evidence. F5/F6 use isolated prepared situations.
Coordinate with [#2351](https://github.com/davidarcher/rimgovernor/issues/2351),
recorded as parked in the original inspection. Reopening that work requires approval.

## Review checkpoint

Review the four food requirements, Q1 and these priorities before expanding the
collection. Implementation and test execution remain future work. Follow the
[test-selection guide](../developers/testing/choose-tests.md) and
[acceptance charter](../developers/testing/acceptance-guide.md#charter) when that
work is authorized.
