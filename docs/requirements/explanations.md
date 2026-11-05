# Explain colony decisions and blockers

[Collection](README.md) · [Review queue](review-queue.md)

## Intent and evidence ownership

The player should be able to understand what the governor is trying to achieve,
what is stopping it, why it chose what it chose and what evidence supports its
view of the colony. The [explanations specification](../../openspec/specs/explanations/spec.md)
is the standing owner of RG-EXPLAIN-001–003 and their scenarios, confirmed by the
maintainer on 2026-11-05 in the [decision record](../../openspec/changes/archive/2026-11-05-confirm-remaining-capabilities/proposal.md#decision-provenance-and-review-boundary). RG-EXPLAIN-003 promotes
the review queue's former candidate Q5 in its narrow form and has no inspected
implementation. This page owns implementation inspection, executed results and
proof limits, not requirements. Its inspection history (E1–E4) is preserved as
recorded.

## Requirement map

Each requirement is stated once, in the specification.

- RG-EXPLAIN-001: [Give a useful reason for waiting or refusal](../../openspec/specs/explanations/spec.md#requirement-rg-explain-001--give-a-useful-reason-for-waiting-or-refusal)
- RG-EXPLAIN-002: [Distinguish plans from observed results](../../openspec/specs/explanations/spec.md#requirement-rg-explain-002--distinguish-plans-from-observed-results)
- RG-EXPLAIN-003: [Explain the comparison behind a choice](../../openspec/specs/explanations/spec.md#requirement-rg-explain-003--explain-the-comparison-behind-a-choice)

## Implementation status of the original requirements

The status below was recorded before the specification owned the requirement text and stays with the evidence it cites.

### Status of RG-EXPLAIN-001 (give a useful reason for waiting or refusal)

**Intent:** Documented in [controls](../players/controls.md) and
[persistence](../developers/contracts/persistence-contracts.md).
**Implementation:** Supported from sampled cause tracking through the launcher
data reader and render functions. Message clarity and the live rendered experience
have no user-observation evidence. Evidence E1–E3.

### Status of RG-EXPLAIN-002 (distinguish plans from observed results)

**Intent:** Documented in [action completion](../developers/contracts/action-contracts.md),
[forecast contracts](../developers/contracts/forecast-contracts.md) and
[signals](../developers/contracts/otel-signals.md). A unified player comparison
of predicted and realized outcomes is an inferred candidate, pending Q5.
**Implementation:** Partial. The player view separates expected work, blockers
and progress timestamps, and diagnostics expose individual decisions. It does
not present a complete comparison of predicted and realized colony outcomes.
That broader presentation is an inferred candidate requiring review (Q5).
Evidence E2–E4.

Room and clothing forecasts should explain the consequences they change, not
an obsolete development-count score. Approximation and sparse-evidence findings
must remain attached to predicted benefits or harm. Evidence E5 describes the
implemented diagnostic boundary without claiming a redesigned player view.

## Evidence and inspection coverage

- **E1 — Cause changes.** [PlannerReasonLog](../../brain/src/RimGovernor.Brain.Runtime/Planner/PlannerScheduler.PlannerReasons.cs)
  retains each need's current cause in memory and emits transitions when it
  changes. First-seen clear needs produce no transition; subject-only changes
  update the standing note without a new transition. Restart begins fresh history.
- **E2 — Live audit definition.** Registered Nightly
  [explain/every-concern](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Explain/ExplainEveryConcern.cs)
  observes a served baseline for half a game day, gathers planner telemetry and
  stored transitions, then passes them to
  [ExplainAudit](../../brain/src/RimGovernor.Brain.Acceptance.Cases/Explain/ExplainAudit.cs).
  The audit checks observed refusals/waits against typed causes with shared wording.
  It covers causes encountered in that window. It supplies no visual UI check or
  evidence that a reader understands the wording.

The [food](food-security.md) and [continuity](continuity.md) pages identify concrete
outcomes against which explanations can be checked. Review a blocked need through
the actual player view before judging wording or status completeness; see
[T11](review-queue.md#t11--read-a-blocker-through-the-player-view).

### E3 — From stored cause to player display

[Diagnostics.ReviewStatusAsync](../../brain/src/RimGovernor.Brain.Host/HostProviders.cs)
reads stored progress and fact freshness. [ReadProjections.ConcernProgressRow](../../brain/src/RimGovernor.Brain.PlayerApi/ReadProjections.cs)
projects method, expected work, blocker, subject and review/progress ticks.
[ObserveService.ViewAsync](../../tools/launcher/Observe.cs) fetches that route and
the current colony/control state; missing route responses remain null.
[obConcerns and obRender](../../tools/launcher/ui/index.html) render those fields,
unknown values and stale/disconnected state. Expanded log rows show the recorded
details. The in-game [GovernorStatusPanel](../../integrations/rimgovernor-native/src/Bridge/Protocol/GovernorStatusPanel.cs)
only shows connection status; detailed explanations live in the launcher.

This is a source trace through rendering, not an executed visual test. The view
still uses older labels such as “Concerns” and raw reason codes. Clear reasons
remain a product obligation; the typed-cause audit alone cannot establish clarity.
Q5 tracks the presentation decision.

### E4 — What a forecast means

[ForecastObservedMealTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/ForecastObservedMealTests.cs)
checks that two observed meals can fund separate forecast eating times and that
insufficient or expired stock cannot fund repeated recovery. These are simulated
outcomes from supplied facts. Food F6 observes actual production, F11 observes
repeated eating, and continuity R3 requires terminal native readback before
continuation. Together they explain why the player view must keep “Expected”
separate from progress. No current test here compares all displayed predictions
with realized outcomes or measures a player's understanding.

### E5 — Room value and qualified risk

[PlannedRoomSelectionTelemetryTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/PlannedRoomSelectionTelemetryTests.cs)
checks the selection telemetry after development-count scoring is removed.
[HarmExposureStateTests](../../brain/tests/RimGovernor.Brain.Domain.Tests/HarmExposureStateTests.cs)
checks that the harm consumer retains empirical, stability and sparse-sample
findings. A finite price must not hide those qualifications or imply that a
sampled zero proves safety. The executed final full gate at `d0fe7897d` covers
these assertions. It does not prove launcher wording, visual clarity or a complete
forecast-versus-observed display; Q5 remains the presentation owner.
