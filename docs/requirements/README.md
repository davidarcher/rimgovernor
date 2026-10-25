# Product requirements — pilot for review

[Documentation](../README.md) · [Food pilot](food-security.md) · [Questions and recommended work](food-security.md#what-to-do-next)

These documents explain what RimGovernor is intended to do for the player and
colony, what the code currently supports, and what the tests establish. They help
readers decide which improvements and checks matter. Implementation details stay
in the linked developer contracts.
**Stage 1 only:** review the map and food pilot before expanding beyond it. No
whole requirement has been marked human-approved; the user has explicitly confirmed autonomous responsibility in RG-FOOD-001.

Food pilot revision: `06db87fb4deb9df2626e1dfbc7059e0655379463`, examined on
2026-10-24. The earlier research inspection used
`5e067eaf9f7a6a6345b78a1bd0a63a354e1b1e54`. Later code changes or issue closure do
not automatically update these findings.

The user redirected the pilot to food because the bot has full autonomy and
player-set research goals are intended to be dropped. [RG-RES-001–004](research-goals.md)
are retained as retired historical requirements, not promises to preserve.
[Retirement work](https://github.com/davidarcher/rimgovernor/issues/3999) is separate
from this documentation task. Autonomous research remains in scope for the product;
retirement of other goals or preferences has not been decided.

## Proposed capability map

The map lists areas to investigate. The active food pilot has detailed
requirements; the other rows are not yet established product promises.
Suggested file names can change without changing requirement IDs.

| Player or colony outcome | Proposed capability file | Where the expectation is described | What we examined |
| --- | --- | --- | --- |
| Start a playable colony with supported settings and understand launch failures | `start-and-control.md` | [Setup](../players/setup.md), [launch](../players/launch.md) | Guides read; Stop button connection checked; generation implementation/tests unexamined |
| Delegate colony control, take it back, and stop safely when control is unavailable | `start-and-control.md` | [Controls](../players/controls.md), [voluntary control](../developers/contracts/voluntary-control.md) | Guide read; speed, manual control, threat detection and permission to issue orders unexamined |
| Understand and guide the colony where player input remains part of the product | To be decided | [Goals](../players/goals.md), [goals contract](../developers/contracts/goals-contracts.md) | Research-goal requirements retired; other goals, themes and preferences need a separate product decision |
| Keep colonists fed and preserve usable food | [Food security](food-security.md) | [Controller contracts](../developers/contracts/controller-contracts.md), [architecture](../developers/architecture/overview.md) | Active pilot: forage-to-eating path, cooking and recovery sampled; farming, storage and sustained feeding unassessed |
| Provide habitable shelter, power and maintained facilities | `habitat.md` | [Spatial](../developers/contracts/spatial-contracts.md), [power](../developers/contracts/power-contracts.md), [upkeep](../developers/contracts/upkeep-contracts.md) | Code that requests a research bench examined; construction completion and other paths unexamined |
| Keep people healthy, rested, equipped and able to work | `colonist-wellbeing.md` | [Medical](../developers/contracts/medical-care.md), [mood](../developers/contracts/mood-control.md), [equipment](../developers/contracts/equipment-upkeep.md) | Map only; implementation/tests unexamined |
| Acquire resources and develop useful capabilities without starving essential work | `development-and-supply.md` | [Research](../developers/contracts/research.md), [trade](../developers/contracts/controllable-trade.md), [mining](../developers/contracts/mining-contracts.md) | Ordinary research followed from request to result reporting; resource/trade/mining paths unexamined |
| Protect the colony from threats and recover from disruption | `protection-and-recovery.md` | [Disasters](../developers/contracts/disaster-planning.md), [controller](../developers/contracts/controller-contracts.md) | Map only; combat, hazards and recovery unexamined |
| Sustain people, animals and chosen social/ideological commitments | `colony-commitments.md` | [Population](../developers/contracts/population-contracts.md), [husbandry](../developers/contracts/husbandry-contracts.md), [ideology](../developers/contracts/ideology-contracts.md), [quests](../developers/contracts/quests.md) | Goals contract table sampled; execution/tests unexamined |
| Resume a saved colony without repeating outdated orders or orders whose effects are unclear | `continuity.md` | [Save/resume](../players/save-and-resume.md), [persistence](../developers/contracts/persistence-contracts.md), [continuation](../developers/contracts/method-continuation.md) | Save guide and persistence ownership table read; goal storage partly examined; game save/load and recovery after an unconfirmed order unexamined |
| Understand actions, blockers, missing facts and limits of predictions | `explanations.md` | [Controls](../players/controls.md), [glossary](../developers/glossary.md), [signals](../developers/contracts/otel-signals.md) | Research status calculation and its connection to the window examined; other explanations and diagnostic readers unexamined |

Several expectations apply throughout the product: knowing when the governor may
act, obeying the game's rules, protecting essential colony needs, explaining
choices, and handling missing information or unsupported situations. Link these
shared requirements where they apply. This pilot has not settled every question
about control or established survival in every RimWorld situation.

## Inspection inventory and evidence language

- **Read:** repository working agreement, documentation map, player guides,
  architecture overview, glossary, test-selection guide and acceptance charter;
  goals/research contracts and player-goals architecture note; selected food, supply and pawn-activity contracts for the food pilot.
- **Partly examined:** rules about saved state, pawn actions, machine setup and
  architecture rules 7–9. The [food evidence inventory](food-security.md#evidence-and-inspection-coverage) and historical [research evidence ledger](research-goals.md#evidence-ledger)
  identifies the code and test assertions examined. It does not represent a full
  review of every file linked there.
- **Not examined:** the other capabilities' code and tests beyond the samples named in each pilot, mod/DLC combinations,
  the running game window, game save/reload, competing research goals, building a
  complete laboratory, Anomaly study, current campaign results and saved results
  from automated game tests. No game was run for this documentation task.

Use these evidence terms consistently:

| Term | Meaning |
| --- | --- |
| Inspected | Someone read the code or test assertions. |
| Registered | The test is included in the test catalog. That does not mean it ran. |
| Executed | An identified test run took place. Its result may be success or failure. |
| Passed | The identified run met its assertions; retain the result and what it tested. |

For a run, record the code revision, inputs, environment and where to find its
results. Label historical results as historical. A documentation check is not
gameplay evidence, and acceptance examples describe expected behavior rather than
reporting successful tests.

Implementation status always has a stated scope:

| Status | Meaning |
| --- | --- |
| Supported | The inspected code connects the required steps under the stated conditions. This alone does not prove they work in the game. |
| Partial | Some of the behavior exists; name the missing or limited part. |
| Absent | Inspection establishes that the behavior is missing. |
| Unassessed | Too little has been examined to decide. |

Intent status is **documented intent**, **inferred candidate** or **human-approved**.
Only an explicit human decision, recorded with its source, permits the last label.
Keep disagreements between documents visible rather than silently choosing which
one to believe. A test that has not run is not evidence that a feature is absent.

## Ownership and maintenance

The product maintainer approves intended behavior and changes to it. Whoever
changes a capability updates its requirements, evidence and developer contracts.
Test authors describe exactly what their assertions establish. These are roles,
not assignments to particular people. AI may propose changes and flag
inconsistencies, but must not silently change intent to match new code.

Write for someone familiar with RimWorld who does not know this codebase. State
what the player should expect first, then explain conditions and examples. Put
internal names, exact test identifiers and implementation details in the evidence
section, where an engineer can follow them without interrupting the requirement.

Before implementation, identify the affected requirement IDs and what the tests
need to establish. Afterward, update the behavior description and evidence
together. Keep mechanics in the developer contracts. Record unresolved questions
in the review queue and actionable deferred work in GitHub after checking for
duplicates. Do not file a bug merely because test evidence has not been examined.

Assign each requirement an ID of the form `RG-<capability>-<number>` once. Keep that
ID if the file moves. When retiring a requirement, retain its ID, reason, decision
link and any replacement ID; never reuse its number. Human-approved intent stays
in force until a deliberate product decision changes it. Recheck affected claims
when code changes instead of regenerating requirements from the latest code.

The next stage requires explicit approval to expand this map and pilot. Permission
to expand does not automatically approve every proposed requirement.
