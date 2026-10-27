# Proposal

## Why

Routine bot assignments currently suppress RimWorld's native job selection, overrides and cleanup, making the bot responsible for behavior the game already supplies, including baby feeding. Manage colony outcomes and available labor through native operating settings, reserving direct orders for meaningful interventions and reconciling actual interruptions instead of enforcing an exact ordinary-work schedule.

## What Changes

- Make numbered work priorities, timetables, bills, designations, policies and facilities the normal means of directing colony labor. The bot retains full control of these settings and may change assignments freely.
- Compare demand, reserves, observed throughput, bottlenecks and estimated capacity across colony goals. Account for native work already happening; a preemptible pawn is not an idle pawn and a timetable is not guaranteed labor.
- Use observed native conditions as the execution baseline. Keep supported future estimates conditional and unsupported future behavior unassessed. Detailed activity forecasts inform what-if comparisons; omitting eating or work from a modeled schedule does not predict vanilla inactivity or authorize direct orders.
- Require a meaningful shortfall, credible risk, stalled essential work or worthwhile opportunity to justify a direct order. Permit preventive action; a small scheduling efficiency gain alone is insufficient.
- **BREAKING:** Replace exclusive routine `PawnActivities` control with native-led execution and explicit bounded interventions. Native selection, interruptions and finalizers must operate at the intended boundaries; observed takeover invalidates bot execution ownership and triggers shared Method reconciliation.
- Use native prioritized orders and short queues where their semantics fit. Preserve actual vanilla forced-order behavior rather than claiming ordinary work can always interrupt a forced order.
- Deliver a first complete baby-feeding milestone, followed by migration of routine callers and labor comparisons. Remove superseded advance caregiver allocation/release and strict routine-control machinery when its readers are migrated.

## Capabilities

### New Capabilities

- `colony-labor`: Native-led routine work, labor-capacity management, meaningful interventions and observed interruption recovery under full automatic control.

### Modified Capabilities

None. RG-FOOD-001–013 remain unchanged; especially RG-FOOD-001, RG-FOOD-004 and RG-FOOD-008 already require observed outcomes and compatible native feeding.

## Impact

Affected owners include `WorkAssignment`, `PawnSchedule`, coupled candidate generation/comparison/binding, `NativePawnActivities`, existing prioritized/queued native work, `PawnActivityTracker`, shared continuation/admission, worker wakeups and current care/resource facts. Retain Families deriving Needs, Methods with Claims, journal admission and Hands as the bot's order writer. Vanilla performs routine job selection and simulation; it does not become a second writer of bot intent. No new scheduler, state store, LLM call or provider dependency is proposed.

Update the owning work-assignment, pawn-activities, voluntary-control, Method continuation, support-work, forecast and persistence contracts as affected, with documentation-map links. Labor implementation evidence belongs in a new `docs/requirements/colony-labor.md` evidence page; existing food evidence remains in `docs/requirements/food-security.md`. Creating planning artifacts establishes no implementation or gameplay pass.

This proposal supersedes the conflicting execution design in [coordinate-baby-feeding](../coordinate-baby-feeding/design.md), not its confirmed food obligations or useful observations. That change remains incomplete and unlanded. Its local checkpoint and later dirty implementation must be reconciled explicitly during apply; do not land either execution design independently, discard unrelated fixes, mark its tasks complete or close [#4040](https://github.com/davidarcher/rimgovernor/issues/4040) from planning alone. This proposal does not edit the earlier change's artifacts.

## Decision provenance and review boundary

In this chat on 2026-10-26, the maintainer requested exploration of native takeover and described numbered priorities, schedules and occasional forced/queued work as the desired play style. They confirmed the proposed outcome/capacity/native-execution model with “that fits,” reserved direct orders for “meaningful” reasons, and confirmed unrestricted bot control over routine priority changes: “bot has 100% control so sure, shuffle away.” Their explicit request to propose `native-led-labor-execution` authorizes these planning artifacts.

The requirements formalize that confirmed direction. Concrete algorithms, thresholds, signal choices, migration milestones and acceptance scenarios below are proposed implementation decisions for review, not separately confirmed product policy or execution evidence. Other existing requirements collections are not migrated by this change.

During apply, inspection found that the exact-activity comparison could treat
omitted future eating as a no-service hunger trajectory. The maintainer then
authorized revising the design/tasks around the distinction between an observed
native execution baseline and detailed what-if forecasts. This revision makes
that boundary explicit across the existing artifacts; it preserves meaningful
preventive intervention, all routine migrations and the confirmed food
requirements. It establishes no additional implementation completion or native
gameplay evidence.

During apply, the maintainer chose generated regular sleep/recreation/work
blocks, explicitly accepting policy and calibration without guaranteeing
execution. Timetables are native preferences, recalibrated from observed service
mechanics through the existing work-settings Method and Hands path.
