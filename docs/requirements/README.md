# Product requirements — pilot for review

[Documentation](../README.md) · [Research goals pilot](research-goals.md) · [Review queue and investments](review-queue.md)

This collection recovers intended player and colony outcomes from guides, contracts,
reachable code and tests. It helps choose implementation and evidence investments;
it is neither a second subsystem specification nor a claim of product certification.
**Stage 1 only:** review the map and research-goal pilot before expanding it. No
requirement here has been marked human-approved.

Examined baseline: `5e067eaf9f7a6a6345b78a1bd0a63a354e1b1e54`, after fetching and
merging `origin/main`, on 2026-10-24. Issue statements were read on that date.
Later edits or issue closure do not automatically update this evidence snapshot.

## Proposed capability map

Rows describe outcomes to investigate, not promises inferred merely from a folder
or an API. Only the linked pilot has worked requirements. Future file names are
suggestions; stable requirement IDs survive any regrouping.

| Player or colony outcome | Proposed capability file | Starting intent sources | Inspection coverage |
| --- | --- | --- | --- |
| Start a playable colony with supported settings and understand launch failures | `start-and-control.md` | [Setup](../players/setup.md), [launch](../players/launch.md) | Guides read; Stop wiring sampled; generation implementation/tests unexamined |
| Delegate colony control, take it back, and stop safely when control is unavailable | `start-and-control.md` | [Controls](../players/controls.md), [voluntary control](../developers/contracts/voluntary-control.md) | Guide read; clock, Manual, hazard and authority paths unexamined |
| Express a desired colony and see whether it is being achieved | `research-goals.md` initially; later other goal capabilities | [Goals](../players/goals.md), [goals contract](../developers/contracts/goals-contracts.md) | Ordinary research pilot traced; other kinds/themes/preferences only sampled in documents |
| Keep colonists fed and preserve usable food | `food-security.md` | [Controller contracts](../developers/contracts/controller-contracts.md), [architecture](../developers/architecture/overview.md) | Map only; food policy, farming, hunting, storage and tests unexamined |
| Provide habitable shelter, power and maintained facilities | `habitat.md` | [Spatial](../developers/contracts/spatial-contracts.md), [power](../developers/contracts/power-contracts.md), [upkeep](../developers/contracts/upkeep-contracts.md) | Research-bench request sampled; construction completion and other paths unexamined |
| Keep people healthy, rested, equipped and able to work | `colonist-wellbeing.md` | [Medical](../developers/contracts/medical-care.md), [mood](../developers/contracts/mood-control.md), [equipment](../developers/contracts/equipment-upkeep.md) | Map only; implementation/tests unexamined |
| Acquire resources and develop useful capabilities without starving essential work | `development-and-supply.md` | [Research](../developers/contracts/research.md), [trade](../developers/contracts/controllable-trade.md), [mining](../developers/contracts/mining-contracts.md) | Ordinary research sampled end to end; resource/trade/mining paths unexamined |
| Protect the colony from threats and recover from disruption | `protection-and-recovery.md` | [Disasters](../developers/contracts/disaster-planning.md), [controller](../developers/contracts/controller-contracts.md) | Map only; combat, hazards and recovery unexamined |
| Sustain people, animals and chosen social/ideological commitments | `colony-commitments.md` | [Population](../developers/contracts/population-contracts.md), [husbandry](../developers/contracts/husbandry-contracts.md), [ideology](../developers/contracts/ideology-contracts.md), [quests](../developers/contracts/quests.md) | Goals contract table sampled; execution/tests unexamined |
| Resume the saved timeline without replaying obsolete or uncertain work | `continuity.md` | [Save/resume](../players/save-and-resume.md), [persistence](../developers/contracts/persistence-contracts.md), [continuation](../developers/contracts/method-continuation.md) | Save guide and persistence ownership table read; goal store seam sampled; native save/load and uncertain-write recovery unexamined |
| Understand actions, blockers, missing facts and limits of predictions | `explanations.md` | [Controls](../players/controls.md), [glossary](../developers/glossary.md), [signals](../developers/contracts/otel-signals.md) | Research goal status producer and panel seam sampled; other explanations and diagnostic readers unexamined |

Control authority, native legality, safety floors, truthful explanations, missing
facts and unsupported states apply across capabilities. They need cross-links,
not a copied safety paragraph in every subsystem. The pilot does not settle global
authority or promise survival in every RimWorld situation.

## Inspection inventory and evidence language

- **Read:** repository working agreement, documentation map, player guides,
  architecture overview, glossary, test-selection guide and acceptance charter;
  goals/research contracts and player-goals architecture note.
- **Sampled:** persistence ownership table, pawn/action contracts, runbook and
  architecture rules 7–9; exact code paths and assertion bodies named in the
  [pilot evidence ledger](research-goals.md#evidence-ledger). Whole files outside
  those named symbols were not exhaustively reviewed.
- **Not examined:** all remaining capability implementations and test bodies,
  mod/DLC combinations, live UI, native save/reload, competing research goals,
  full laboratory construction, Anomaly study execution, current campaign results
  and CI/native result bundles. This is a bounded discovery pass, not a repository
  audit. No native game was run for this documentation milestone.

**Inspected** means source/assertions read. **Registered** means a case has a
catalog registration in source; it does not mean executed or passed. **Executed**
requires an identified attempt; **passed** additionally requires its result and
assertions. Retain revision, inputs, environment and result-bundle location when
adding a run. A reported historical result is identified as such, never promoted
to a current pass. The documentation check for this milestone is not gameplay
evidence. Acceptance examples below are proposed checks, not results.

Implementation status is scoped: **supported** means a reachable path was found
for the stated conditions, not verified success; **partial** names a specific
subset or gap; **absent** requires evidence of missing behavior; **unassessed**
means inspection is insufficient. Missing execution evidence alone does not make
implementation absent. Intent is **documented intent**, **inferred candidate** or
**human-approved**; only an explicit human decision with provenance permits the
last status. Contradictory documents remain visible rather than being ranked by
an invented precedence rule.

## Ownership and maintenance

The product maintainer owns approval and changes to intent. A capability's
implementation maintainer updates its requirements and evidence alongside the
owning contracts; the test author owns the precision of each evidence claim.
These are roles, not assignments to unconsulted individuals. AI may propose
requirements and flag drift, but cannot approve or silently rewrite intent.

Before implementation, identify affected stable IDs and the exact acceptance
claims. After implementation, update the implementation status, evidence and
relevant contracts together. Keep player outcomes here and mechanics in contracts.
Record contradictions in the review queue; actionable deferred work belongs in
GitHub after a duplicate search. Do not create bugs from unverified coverage.

Use IDs of the form `RG-<capability>-<number>`, assigned once. Retired requirements
retain their ID, a retirement reason, decision link and replacement ID if any;
never reuse a number. Preserve human-approved intent until a deliberate product
decision changes it. Review new source drift against the recorded revision and
the affected claims, without rebuilding the collection from code.

The next stage requires explicit review approval of this map, entry structure,
pilot boundaries and unresolved intent. Approval to expand is not automatically
approval of every candidate requirement.
