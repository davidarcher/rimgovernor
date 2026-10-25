# Explain colony decisions and blockers

[Collection](README.md) · [Review queue](review-queue.md)

The player should be able to understand what the governor is trying to achieve,
what is stopping it and what evidence supports its view of the colony.

## RG-EXPLAIN-001 — Give a useful reason for waiting or refusal

When a need remains unmet, the governor should expose the current cause and the
affected subject in language a player can use. When that cause changes, the
current explanation should change with it. This makes a stalled colony diagnosable.

For example, a missing ingredient should identify the blocked work; a later
unavailable worker should replace the old reason. Missing observations should
show an unresolved condition. Recovered needs should leave the active blocker
view. A new session should make the limits of its available history clear.

**Intent:** Documented in [controls](../players/controls.md) and
[persistence](../developers/contracts/persistence-contracts.md).
**Implementation:** Supported from sampled cause tracking through the launcher
data reader and render functions. Message clarity and the live rendered experience
have no user-observation evidence. Evidence E1–E3.

## RG-EXPLAIN-002 — Distinguish plans from observed results

Status and diagnostics should distinguish expected effects, accepted orders,
ongoing work and observed outcomes. Predictions should retain their conditions
and missing inputs. This lets the player and maintainer judge whether progress is
real and where a plan failed.

A forecast meal should remain expected until production is observed. A successful
order should identify dispatch progress while eating or treatment is still pending.
If an observation is unavailable, the last prediction should retain that limit.
After recovery, the current state should reflect fresh evidence.

**Intent:** Documented in [action completion](../developers/contracts/action-contracts.md),
[forecast contracts](../developers/contracts/forecast-contracts.md) and
[signals](../developers/contracts/otel-signals.md). A unified player comparison
of predicted and realized outcomes is an inferred candidate, pending Q5.
**Implementation:** Partial. The player view separates expected work, blockers
and progress timestamps, and diagnostics expose individual decisions. It does
not present a complete comparison of predicted and realized colony outcomes.
That broader presentation is an inferred candidate requiring review (Q5).
Evidence E2–E4.

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
