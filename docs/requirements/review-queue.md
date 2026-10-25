# Requirements review queue

[Collection](README.md) · [Food requirements and evidence](food-security.md)

Food remains the first priority. The expansion adds control, shelter, care,
resources, protection, commitments, continuity and explanations. The queue separates
known documentation problems from proposed evidence work. The capability pages
record the inspected sources and the limits of each claim.

## Q1 — Who chooses food work?

The [controller contract](../developers/contracts/controller-contracts.md#priority-evaluation)
says vanilla work priorities schedule routine food work. The
[pawn activity contract](../developers/contracts/pawn-activities.md#needs-forecast-support-matrix)
says the governor explicitly chooses controlled eating and supported work.

The inspected code contains explicit work selection. Registered
[control/unassigned-runs-vanilla](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Control/ControlUnassignedVanilla.cs)
also explicitly checks vanilla job selection for an unassigned pawn under Auto,
with no brain running. This narrows the disagreement to the conditions under which
each path chooses work. The berry case and
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

## Q2 — What does Stop close?

The controls and save guides distinguish stopping the brain from closing the
game. The launch guide and inspected launcher button wiring close the private
game process. That difference matters to unsaved progress.

**Required correction:** Align Stop, Restart, Close game and save guidance with
the actual launcher behavior. Any intended lifecycle change needs its own design.
**Tracking:** [#3998](https://github.com/davidarcher/rimgovernor/issues/3998), P1,
still open when checked during this expansion. The existing issue records the
source evidence and duplicate search.

## Implementation and documentation priorities

1. **Correct Stop and save guidance (Q2).** Players need an accurate account of
   whether a control ends the game before using it with unsaved progress.
2. **Correct the execution descriptions (Q1).** The disagreement affects both
   interpretation of existing tests and the choice of code to investigate when
   feeding stalls. Resolve it against the current running path.
3. **Investigate a current feeding failure if T1 reveals one.** Starvation has high
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

**Priority:** Later. The expansion sampled storage and seasonal decisions;
their completed native outcomes still need evidence. Sustained feeding depends
on those capabilities as well as the short food path.

**Requirement and claim:** RG-FOOD-001/002 — the colony continues to feed itself
across seasons and disruptions.

**Setup and result:** Agree a campaign colony and explicit food/health outcomes
before running. Allow autonomous play after setup and retain observed supplies,
consumption and colony condition. Acceptance criteria and duration still require
a product decision.

**Evidence layer:** Campaign evidence. F5/F6 use isolated prepared situations.
Coordinate with [#2351](https://github.com/davidarcher/rimgovernor/issues/2351),
recorded as parked in the original inspection. Reopening that work requires approval.

### T6 — Control and save boundaries

**Priority:** After the first food checks, alongside the Stop correction.
**Claim:** RG-CONTROL-002 and RG-CONTINUITY-001/002 — relinquishing control fences
old work; loading restores save-owned intent and starts from fresh world facts.

First inspect existing authority and persistence cases. For any uncovered claim,
use two focused checks: a boundary test that prepares an order, switches to Manual
and verifies refusal; a native save/load case with one saved layout and a fresh
journal that verifies restored intent and absence of old-load dispatch. Pass
requires the expected state and refusal, with no duplicated effect. Separate the
claims so a failure has one clear cause. Current continuation tests supply their
observations, and the checkpoint sample stops at the native reply.

### T7 — Finish shelter and essential care

**Priority:** Next after food and control. **Claims:** RG-HABITAT-001 and
RG-HEALTH-001 — colonists gain usable sleeping space; an urgent patient receives
completed treatment.

Obtain a compatible retained result for `layout/first-bedroom-closes`, or use the
existing case. Its staged materials and room/bed assertions already define the
claim. For care, survey existing treatment cases first. If uncovered, stage one
patient with a tendable condition and one eligible doctor, enable autonomous care
and require observed treatment of that patient within a short fixed budget.
Both claims need native evidence. Pairing tests and `medical/tend-gates` stop before
treatment; the room case is already defined but its current result is unexamined.

### T8 — Connect resource demand to delivery

**Priority:** After basic survival services. **Claim:** RG-SUPPLY-001 — a real
material shortage drives useful acquisition and delivered stock.

Survey current extraction cases before adding one. A minimal native setup has one
supported material deficit, one eligible worker and one reachable source near a
valid destination. Pass requires autonomous source selection, observed yield and
arrival where the consuming work can use it. A blocked-source variant belongs in
a cheaper policy test. The sampled mining case installs its own order and checks
rock removal; it does not assert this demand-to-delivery chain.

### T9 — Observe restored service after disruption

**Priority:** After T7. **Claim:** RG-PROTECT-002 — restoration follows observed
service recovery after a bounded disruption.

First inspect recovery cases and retained results. For an uncovered claim, stage
one damaged essential facility with an eligible repairer and needed supplies.
Enable the governor and require both native restored service and the corresponding
closed deficit. A pure test should cover unread service preventing restoration.
The family tests cover supplied states, while the sampled fire case observes a
response starting. Neither establishes repaired native service.

### T10 — Carry one commitment through to its outcome

**Priority:** Later, after food and care. **Claim:** RG-COMMIT-001 — accepting a
supported commitment reserves its ongoing demand until fulfilment or failure.

Choose one already-supported short quest after inspecting its existing tests.
Use a policy/journal test with a prepared offer, known home demand and sufficient
crew. Pass requires admission only when affordable, retained demand while active,
and release after an observed terminal result. Native quest completion is a
separate claim for an existing objective case. The sampled acceptance case checks
native acceptance and rewards; it does not exercise autonomous capacity accounting.

### T11 — Read a blocker through the player view

**Priority:** Alongside the first failing behavior investigation.
**Claim:** RG-EXPLAIN-001/002 — the displayed reason tracks the actual blocked work
and changes when the obstacle changes.

Inspect current presentation tests first. Feed a known blocked need through the
status projection and launcher reader, then replace the cause and finally clear
it. Pass requires the correct subject, readable current reason and cleared active
blocker. A component test is the cheapest check; inspect the rendered view once
to judge readability. The live audit verifies typed explanation coverage for
encountered causes, leaving display and comprehension unassessed.

### T12 — Preserve food and complete assisted feeding

**Priority:** Before T5, after the central food path. **Claims:** RG-FOOD-006/008 —
at-risk food gains useful protection, and a dependent receives suitable food.

Survey storage and childcare cases first, then use separate native checks for
uncovered outcomes. For preservation, stage a perishable stack, a reachable
working cold store and an eligible hauler; require observed arrival and the native
storage temperature that satisfies the preservation policy. For assisted feeding,
stage one hungry baby, suitable food and an eligible caregiver with no
breastfeeder; require observed feeding and improved food need. Each test should
stop when its one effect is observable. The current route benchmark performs no
haul, and the baby-food sample only assesses stock. Availability and diet choices
can first be checked with cheaper supplied-fact policy tests.

## Scope of the next milestone

The documentation expansion is authorized. Runtime fixes and new tests remain
separate work. Begin with existing evidence for T1, then the autonomous recovery
claim in T2; retain the scenario and observations if a defect appears. The proposed
checks above provide choices for later milestones. Follow the
[test-selection guide](../developers/testing/choose-tests.md) and
[acceptance charter](../developers/testing/acceptance-guide.md#charter).

No new behavior issue is justified solely by unexamined coverage. The two known
documentation problems already have issues. Campaign work remains parked in
[#2351](https://github.com/davidarcher/rimgovernor/issues/2351), confirmed during
this expansion.
