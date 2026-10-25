# Product requirements

[Documentation](../README.md) · [Food requirements](food-security.md) · [Review queue](review-queue.md)

This collection describes the outcomes RimGovernor should deliver, the behavior
found in the code and the evidence available from tests. Use it to choose useful
improvements and protect intended behavior during implementation changes.

The governor has full autonomy. This collection covers the main colony outcomes
with representative implementation and test inspection. Detailed requirements
remain proposals where their intent has only been inferred from code.

**Expansion revision examined:** `39c9a3bd29e8bbb38730d784e0d322f8405aa2d5`,
on 2026-10-24. Food evidence F1–F6 retains its earlier inspection provenance.
All evidence on these pages is source inspection and catalog registration unless
an entry gives an executed run and result location. No game test was run or current
result bundle examined during this documentation work. The repository's docs-only
check does not supply gameplay evidence.

## Capability map

Each page contains stable requirements, normal and disrupted examples, intent and
implementation status, evidence, and the remaining inspection scope.

| Outcome | Requirements | Inspection coverage | Main remaining limit |
| --- | --- | --- | --- |
| Start a colony and hand control over predictably | [Start and control](start-and-control.md), RG-CONTROL-001–002 | Load-service test, authority boundary, native work under Auto | Generation, launcher interactions, speed transitions |
| Keep colonists fed and preserve food | [Food](food-security.md), RG-FOOD-001–008 | Forage, cooking, continuation, seasonal targets, fields, storage, hunt choices, baby-food assessment | Assisted feeding, preservation effects and sustained sufficiency |
| Provide habitable shelter and working facilities | [Habitat](habitat.md), RG-HABITAT-001–002 | Shelter routing, completed-room case, fuel decision and powered-stove case | Extreme climates, growth, loss recovery and maintenance |
| Keep people healthy and able to work | [Wellbeing](colonist-wellbeing.md), RG-HEALTH-001–002 | Medical needs, exclusive pairing, native tend dispatch and eligibility case | Treatment completion, chronic recovery, recreation and equipment |
| Supply useful work and trade affordably | [Supply](development-and-supply.md), RG-SUPPLY-001–002 | Production declaration, exact mining case, native trade exchange | Autonomous source/deal choice through delivery |
| Protect people and restore disrupted services | [Protection](protection-and-recovery.md), RG-PROTECT-001–002 | Disaster read/work path, recovery decisions, native fire-response interruption | Combat, evacuation, survival and restored native services |
| Sustain new people, animals and accepted work | [Commitments](colony-commitments.md), RG-COMMIT-001–002 | Capacity contracts, herd routing, native quest and animal settings cases | Capacity-to-outcome chain, ideology and quest fulfilment |
| Resume safely after saves and interruptions | [Continuity](continuity.md), RG-CONTINUITY-001–002 | Checkpoint boundary, world rebuild, journal continuation guards | Native save/load round trip and uncertain-write recovery |
| Explain actions and blockers | [Explanations](explanations.md), RG-EXPLAIN-001–002 | Cause changes, transition storage contract, live audit assertions | Rendered player view and prediction wording |

## Shared concerns

Control and continuity apply to every capability. RimWorld determines legal
operations and physical outcomes. Each capability's examples identify normal
progress, blockers and reassessment; explanations should expose those states.
The governor may revise existing colony construction and settings within its
supported control path. A stale observation requires a fresh read.

## Inspection coverage

- **Read:** working agreement, documentation map, player guides, architecture
  overview, glossary, test-selection guide and acceptance charter.
- **Sampled:** the named contracts, runtime paths and test assertions on each
  capability page. Food remains the deepest trace, from shortage through native
  eating. Other pages use narrower representative paths and state where those stop.
- **Pending:** the final-column limits above and each page's remaining inspection
  scope. In particular, no running UI, save/reload experiment or campaign result
  was examined. Source-file presence alone supplies no implementation verdict.

## What to do next

Use the [review queue](review-queue.md) to choose a bounded improvement. Resolve
the tracked control-description disagreements, then obtain current evidence for
the central food path and its recovery. Later checks build outward to care,
shelter, preservation and continuity. Broad campaign work remains parked.

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
