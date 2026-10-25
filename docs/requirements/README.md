# Product requirements

[Documentation](../README.md) · [Review queue](review-queue.md) · [Completion audit](completion-audit.md)

This collection describes the outcomes RimGovernor should deliver, what the code
supports and what existing tests actually check. Use it to prioritize useful
changes and preserve intended behavior as the implementation evolves.

The governor has full autonomy. Research goals are excluded by the user's
direction. Detailed behavior inferred from code remains a candidate until reviewed.

**Repository revision examined:** the epic #3650 requirements update examined
`d0fe7897d`, on 2026-10-25. It updates room selection, clothing/facility value,
dependent occupants, supply timing, combat exposure and their diagnostic limits.
The wider collection's recovery baseline remains `402a78e62` (2026-10-24); this
focused update does not imply every unrelated path was re-audited. Earlier food evidence
retains its provenance at `06db87fb4deb9df2626e1dfbc7059e0655379463`; the initial
expansion examined `39c9a3bd29e8bbb38730d784e0d322f8405aa2d5`.

Evidence includes source/test inspection, catalog registration and explicitly
identified reused runs. The epic's final full gate passed 3,877 domain tests and
29 workflow tests, with zero skipped. [P6](protection-and-recovery.md#p6--civilian-exposure-and-forecast-evidence)
reuses the recorded 280-fight native civilian calibration, whose raw observations
and failed stability rounds are retained in the repository. This documentation
update runs no game cases. Other registered acceptance examples retain their
original evidence limits; no remote results or sustained campaign outcome are
claimed. The docs-only gate supplies no new gameplay evidence.

## Capability map

Each area has a representative trace from intent or observed need through
decision, execution and observable outcome. Test assertions qualify which parts
of that trace are exercised and whether the fixture bypasses autonomous choice.

| Outcome | Requirements | Examined paths | Evidence limit or confirmed gap |
| --- | --- | --- | --- |
| Start a colony and hand control over predictably | [Start and control](start-and-control.md), RG-CONTROL-001–002 | Launch/setup gating, generation, loading, failure display, authority, clock and speed | Native interrupt/resume and clean-machine execution; conflicting Stop guides |
| Keep colonists fed and preserve food | [Food](food-security.md), RG-FOOD-001–008 | Supply timing, forage, cooking, continuation, sowing/harvest, hunting, fishing/milk facts, storage/cooling, diets, babies and herd feed | Assisted-feeding effects, autonomous loss recovery and seasonal sufficiency |
| Provide habitable shelter and working facilities | [Habitat](habitat.md), RG-HABITAT-001–003 | First rooms, service-day value for every planned role, prerequisite/connector work, priced room sharing, temperature and upkeep | Explicit repair/replacement holds; repeated growth/loss outcomes and forecast approximations |
| Keep people healthy and able to work | [Wellbeing](colonist-wellbeing.md), RG-HEALTH-001–002 | Urgent pairing/tend, condition history, surgery gates, rest/recreation, subdue, clothing | Treatment/recovery outcomes; gathering and residual apparel holds |
| Supply useful work and trade affordably | [Supply](development-and-supply.md), RG-SUPPLY-001–002 | Deficits, extraction, shared stock arrivals, conditional native incident timing, selected trade, mission cargo | Full delivered supply/expedition proof; bootstrap and conditional arrival estimates |
| Protect people and restore disrupted services | [Protection](protection-and-recovery.md), RG-PROTECT-001–002 | Fire/shelter response, combat batch/readback, civilian empirical exposure, service recovery | Unconfirmed exposure stability, survival/episode outcomes; equipping/disarm holds |
| Sustain people, animals and accepted work | [Commitments](colony-commitments.md), RG-COMMIT-001–003 | Capacity, quests, custody/herds, classroom, nursery and prison-room effects, ideology/DLC | Bounded/approximate dependent forecasts, separate prisoner mortality; population/ritual/progression holds |
| Resume safely after interruptions | [Continuity](continuity.md), RG-CONTINUITY-001–002 | Checkpoint, state blobs, native load/replay, world reconstruction, continuation, autosave and manual-save handshake | Arbitrary mid-write crash recovery; manual-save freshness decision |
| Explain actions and blockers | [Explanations](explanations.md), RG-EXPLAIN-001–002 | Cause transitions, journal/API projection, launcher reader/render functions, forecast/observed distinction | Executed visual transition/readability evidence; broader outcome presentation decision |

## Shared concerns

Control and continuity apply to every capability. RimWorld determines legal
operations and physical outcomes. Requirements include normal progress, blockers
and reassessment; explanations should expose those states. A stale observation
requires a fresh read. The governor may revise colony construction and settings
within its supported paths.

Unsupported work is explicitly identified on its owning capability page and in
[Q3](review-queue.md#q3--confirmed-autonomous-planning-gaps). A native operation
beneath a held planner path does not establish autonomous support. Those holds
are implementation gaps, with desired admission policy still to decide.

## Inspection coverage

- **Examined foundations:** working agreement, documentation map, player guides,
  architecture overview, glossary, owning contracts, test-selection guide and
  acceptance charter.
- **Sampled implementation and tests:** every row in the map, with source paths,
  symbols, case assertions and limits on its capability page. Food is the deepest
  trace. Other areas include both the decision boundary and a native outcome or
  explicit unsupported boundary.
- **Uninvestigated mapped capabilities:** none. This is a representative recovery,
  not a line-by-line audit of every branch, DLC combination or map.
- **Evidence not obtained:** new gameplay results beyond the reused scoped combat
  calibration, live visual interaction, arbitrary crash/corruption experiments and
  sustained campaign results. These
  are proof limits, not findings that the behavior is broken. Their useful next
  checks are specified separately in the review queue.

The [completion audit](completion-audit.md) checks the collection against the
original recovery task and this focused implementation sync. Campaign evidence
remains separate; the calibration does not establish full colony performance.

## Reading evidence and status

| Evidence term | Meaning |
| --- | --- |
| Inspected | Code or test assertions were read. |
| Registered | A test appears in the test catalog. |
| Executed | An identified run took place. |
| Passed | That run met its assertions, with a retained result. |

Executed evidence must record revision, inputs, environment and result location.
Acceptance examples state expected behavior; evidence entries state what was
checked and where its claim stops.

| Implementation status | Meaning |
| --- | --- |
| Supported | Inspected code connects the required steps under the stated conditions; execution evidence is separate. |
| Partial | Some behavior exists and the entry identifies a concrete remaining implementation gap. |
| Absent | Inspection establishes that the scoped behavior is missing. |
| Unassessed | The necessary investigation has not been done; this is unfinished recovery work. |

Intent is **documented intent**, **inferred candidate** or **human-approved**.
Human approval requires an explicit decision and its source. Source disagreements
remain visible in the queue until resolved. Existing policy constants are not
automatically lasting product promises.

## Ownership and maintenance

The product maintainer approves intended behavior. Whoever changes a capability
updates its requirements, evidence and owning developer contracts. Test authors
state the exact claim their assertions establish.

Write for someone who knows RimWorld. Lead with an observable outcome and use
concrete examples. Keep exact code/test names in the evidence section. State
limits where they affect a decision and consolidate shared caveats here.

Before implementation, identify affected requirement IDs and acceptance claims.
Afterward, update behavior and evidence together. AI may propose changes and flag
drift; changing approved intent requires a deliberate product decision.

Assign stable `RG-<capability>-<number>` IDs. Preserve them when files move and
never reuse retired IDs. Record retirements with a reason and any replacement;
Git history retains deleted documents. Research-goal requirements were removed at
the user's request and are not reinstated by this recovery.

Keep source conflicts and intent questions in the [review queue](review-queue.md).
Track actionable deferred implementation and decisions in GitHub after duplicate
searches. File behavior bugs from concrete evidence; record missing proof in the
evidence inventory. Recheck linked issues when implementing, because issue
descriptions and runtime code can change independently of these requirements.
