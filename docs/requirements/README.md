# Product requirements

[Documentation](../README.md) · [Food requirements](food-security.md) · [Review queue](review-queue.md)

This collection describes the outcomes RimGovernor should deliver, the behavior
found in the code and the evidence available from tests. Use it to choose useful
improvements and protect intended behavior during implementation changes.

The governor has full autonomy. Food is the first capability being worked through
in detail. Review this pilot before expanding the collection.

**Revision examined:** `06db87fb4deb9df2626e1dfbc7059e0655379463`, on 2026-10-24.
The evidence records source inspection and test registration. No game test was run
or current result bundle examined during this work.

## Proposed capability map

The map organizes the remaining investigation around player and colony outcomes.
The food page contains the current detailed requirements.

| Player or colony outcome | Proposed capability file | Where the expectation is described | What we examined |
| --- | --- | --- | --- |
| Start a playable colony with supported settings and understand launch failures | `start-and-control.md` | [Setup](../players/setup.md), [launch](../players/launch.md) | Guides read; Stop button connection checked; generation implementation/tests unexamined |
| Delegate colony control, take it back, and stop safely when control is unavailable | `start-and-control.md` | [Controls](../players/controls.md), [voluntary control](../developers/contracts/voluntary-control.md) | Guide read; speed, manual control, threat detection and permission to issue orders unexamined |
| Keep colonists fed and preserve usable food | [Food security](food-security.md) | [Controller contracts](../developers/contracts/controller-contracts.md), [architecture](../developers/architecture/overview.md) | Active pilot: forage-to-eating path, cooking and recovery sampled; farming, storage and sustained feeding unassessed |
| Provide habitable shelter, power and maintained facilities | `habitat.md` | [Spatial](../developers/contracts/spatial-contracts.md), [power](../developers/contracts/power-contracts.md), [upkeep](../developers/contracts/upkeep-contracts.md) | Bench requests sampled; construction completion and other paths unexamined |
| Keep people healthy, rested, equipped and able to work | `colonist-wellbeing.md` | [Medical](../developers/contracts/medical-care.md), [mood](../developers/contracts/mood-control.md), [equipment](../developers/contracts/equipment-upkeep.md) | Map only; implementation/tests unexamined |
| Acquire resources and develop useful capabilities without starving essential work | `development-and-supply.md` | [Research](../developers/contracts/research.md), [trade](../developers/contracts/controllable-trade.md), [mining](../developers/contracts/mining-contracts.md) | Supply documentation sampled; detailed development/resource/trade/mining inspection pending |
| Protect the colony from threats and recover from disruption | `protection-and-recovery.md` | [Disasters](../developers/contracts/disaster-planning.md), [controller](../developers/contracts/controller-contracts.md) | Map only; combat, hazards and recovery unexamined |
| Sustain people, animals and chosen social/ideological commitments | `colony-commitments.md` | [Population](../developers/contracts/population-contracts.md), [husbandry](../developers/contracts/husbandry-contracts.md), [ideology](../developers/contracts/ideology-contracts.md), [quests](../developers/contracts/quests.md) | Map only; execution/tests unexamined |
| Resume a saved colony without repeating outdated orders or orders whose effects are unclear | `continuity.md` | [Save/resume](../players/save-and-resume.md), [persistence](../developers/contracts/persistence-contracts.md), [continuation](../developers/contracts/method-continuation.md) | Save guide and persistence ownership table read; game save/load and recovery after an unconfirmed order unexamined |
| Understand actions, blockers, missing facts and limits of predictions | `explanations.md` | [Controls](../players/controls.md), [glossary](../developers/glossary.md), [signals](../developers/contracts/otel-signals.md) | Food failure handling sampled; player-visible explanations and diagnostic readers unexamined |

## Shared concerns

Each capability should account for control authority, RimWorld's rules, safety,
saved state, recovery, explanations and unsupported situations. Cross-link these
requirements as the collection develops.

## Inspection coverage

- **Read:** working agreement, documentation map, player guides, architecture
  overview, glossary, test-selection guide and acceptance charter.
- **Sampled for food:** supply and pawn-activity contracts; food assessment,
  forage selection, cooking, eating and continuation code; the test assertions
  listed in the [food evidence inventory](food-security.md#evidence-and-inspection-coverage).
- **Pending:** other food sources, storage, seasonal sufficiency, special feeding,
  save/reload, the running UI, campaign results and the remaining capabilities.

## Reading evidence and status

| Evidence term | Meaning |
| --- | --- |
| Inspected | Code or test assertions were read. |
| Registered | A test is included in the test catalog. |
| Executed | An identified run took place. |
| Passed | That run met its assertions, with a retained result. |

Record revision, inputs, environment and result location for executed tests.
Acceptance examples describe the expected behavior. Evidence entries describe
which parts have been checked and the limits of those checks.

| Implementation status | Meaning |
| --- | --- |
| Supported | Inspected code connects the required steps under the stated conditions. Execution evidence is recorded separately. |
| Partial | Some behavior exists; the entry identifies the remaining gap. |
| Absent | Inspection establishes that the behavior is missing. |
| Unassessed | Further inspection is needed. |

Intent status is **documented intent**, **inferred candidate** or **human-approved**.
Human approval requires an explicit decision with its source. Keep conflicting
sources in the review queue until the disagreement is resolved.

## Ownership and maintenance

The product maintainer approves intended behavior. Whoever changes a capability
updates its requirements, evidence and owning developer contracts. Test authors
state the exact claim their assertions establish.

Write for a reader who knows RimWorld. Lead with the expected outcome and use
concrete examples. Keep exact code and test names in the evidence section. State
limits where they affect a decision, and consolidate shared caveats in one place.

Before implementation, identify affected requirement IDs and acceptance claims.
Afterward, update behavior and evidence together. AI may propose changes and flag
drift; changing approved intent requires a deliberate product decision.

Assign each requirement a stable `RG-<capability>-<number>` ID. Preserve it when
files move and never reuse retired IDs. Record retirement decisions with a reason
and any replacement. Git history retains deleted documents.

Keep unresolved questions in [review-queue.md](review-queue.md). Track actionable
deferred work in GitHub after checking for duplicates. File behavior bugs when
there is concrete evidence of incorrect behavior; keep unverified coverage in the
evidence inventory.
