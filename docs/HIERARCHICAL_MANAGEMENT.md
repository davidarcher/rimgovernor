# Hierarchical colony management

> Historical notes for the retired RimBot C# mod, removed from this repository.
> For the supported Python/RIMAPI architecture, see EXTERNAL_CONTROLLER.md.

The live management path is now `ColonyManager.Start` → `ManagementCoordinator` → native `ColonyTools.Execute`.

Five specialists inspect compact domain state and submit proposals. They share one model client per cycle. Survival covers food and immediate health/temperature needs; Infrastructure covers construction, storage, production and power; Security covers threat assessment and direct security orders; Development covers research and long-term intentions. Workforce translates approved labor requests. The Administrator arbitrates twice when staffing is needed: first to grant work, then to approve the resulting pawn orders. The final pass cannot grant new labor after Workforce has reviewed it.

Specialists receive only read tools and `submit_proposal`. Proposed actions carry the existing tool argument schemas, and labor work types come from RimWorld's `WorkTypeDef` database. Runtime validation rejects malformed output, invented actions/handles, unowned actions, unfunded reservations, competing Security/Workforce pawn orders, and priority inversions. A failed specialist is logged and omitted; other specialists continue. Only the Administrator's validated accepted actions reach the existing executor on the game thread. Native eligibility is checked there again.

The blackboard contains resource counts, estimated worker availability, notifications/health conditions, player direction, domain observations, active work, temporary commitments, and recent concise decisions. It persists intentions rather than executable delegates. Actual game observations establish completed or removed construction. Resource availability is refreshed before arbitration and execution. Routine notifications schedule follow-up rather than discarding every pending order; newly worsening measured medical emergencies invalidate pending execution.

## Scheduling and reasoning

A deterministic registry maps food, medical, temperature, power, threat, pawn-availability and construction changes to relevant roles. A daily periodic review covers every role; each proposal and the Administrator also recommends a review time. Native hostile-faction presence invokes assessment and is not a construction prohibition. Power-off counts are a trigger for inspection, not proof of a shortage.

Administrator arbitration uses reasoning. A player steering review also uses reasoning for specialists. Specialists are planners and use the configured strategic reasoning setting (medium by default); execution itself is deterministic and makes no additional model calls. The same `ILanguageModel` instance serves all roles; no model server or independent model instances were added. Seasonal and daily planning still supply strategic context. Steering preserves the current daily plan and requests a concise player-facing response. Enter sends without closing the manager window.

Local reviews have no tool-count or action-count rejection. Cloud hourly and action spending controls remain. Repeated identical reads can fail a specialist rather than run indefinitely. Context compaction retains recent query results; it is not a substitute for a richer persistent query cache.

## Files

- `ManagementContracts.cs`: DTOs, blackboard, registry, ownership and schema/decision validation.
- `ManagementCoordinator.cs`: role prompts, sequential shared-client inference, read-only query flow and arbitration.
- `ColonyManager.Hierarchy.cs`: native state slices, scheduling, persistence, thread boundaries and execution telemetry.
- `ColonyManager.cs`: production entry point; obsolete single-manager action loop removed.
- `LocalModel.cs`: per-request reasoning selection on the existing client.
- `TaskLedger.cs`, `ColonyManager.Tasks.cs`, `ManagerWindow.cs`: automatic construction reconciliation, tracking dismissal, steering keyboard behavior.
- `ManagementChecks.cs`, `TaskDismissChecks.cs`: deterministic hierarchy and ledger regression cases.

## Limits and next work

Ownership is exact for named semantic tools, not every possible game intent. Native opaque right-click options have actor/target provenance, but the current bridge cannot classify every mod-added command by domain without interpreting labels. Security and Workforce can propose these actions; the Administrator approves them and same-pawn cross-domain orders conflict. This does not prove that an unknown mod command has correct semantic ownership. The next bridge refactor must provide capability metadata where available and explicit treatment of unclassified commands (see `NATIVE_ACTION_BRIDGE_PLAN.md`).

Trade, schedules and many policies still lack adapters. Managers can describe missing capabilities, not invent them. Infrastructure remains the sole construction/production owner; requests for facilities from other roles travel as high-level context for subsequent reviews, not automatic recipe expansion.

Pawn-hours use an explicit estimate of eight productive hours per available pawn within a day; estimates are not engine reservations. Material requests are model estimates checked against allowed map stocks, not a proof of reachability or exact recipe cost. Native construction still determines real costs and completion. A commitment override releases the planning reservation and does not cancel native jobs, blueprints or bills.

Tests using a fake model verify enforcement and orchestration, not the model's RimWorld competence. Gameplay results and any failed runs must be recorded separately. No compatibility scaffolding is required for obsolete AI plans or tool aliases; ordinary colony state is independent of those interfaces.
