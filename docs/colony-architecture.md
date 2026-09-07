# Colony architecture: implementation map

The requested direction is preserved in `colony-architecture-request.md`.
This is an incremental refactor of the Python controller and native RIMAPI, not a replacement stack.

| Capability | Existing implementation | Remaining gap / next change |
| --- | --- | --- |
| State and events | `runtime.poll`, `event_loop`, typed native/OpenAPI responses | Semantic event routing currently uses broad event-name matching; needs typed triggers and hysteresis. |
| Derived facts / prediction | `resources.resource_brief`, `decision_context`, `project_progress` | Supply aggregates and native site blockers exist. Add diet-aware food runway and harvest estimates with explicit missing inputs; do not equate all nutrition with edible accessible food. Power, mood, wealth and labor projections need their own observed inputs. |
| Persistent goals | `semantic.retain_project`, SQLite memory, strategy/day plans | Projects already carry owner, outcome, constraints, success signals, progress and work IDs. Extend these with dependencies, deadlines and suspension; do not add another goal queue. Legacy `memory.goals` and executable projects need consolidation. |
| Action lifecycle | `runtime.execute`, `reconcile`, `reconcile_projects` | Unknown-before-send persistence, in-flight duplicate checks, native completion and timeout already exist. Project retirement still relies on administrator judgment; verified orders deliberately do not mean goal achieved. |
| Commitment | `project_schedule`, existing project records | First slice implemented: preserve unchanged approvals; observe before re-invoking; game-time reassessment; changed evidence or steering bypasses waiting. Broader resource/priority commitments remain. |
| Resource arbitration | Native placement validates material/legality; administrator selects objectives | **No central project budget ledger.** `Proposal.resources` does not enforce shared budgets. Next: derive costs from native definitions/current orders, reserve by project, reconcile delivered/consumed resources, deterministically reject over-allocation. Unknown costs must remain unknown. |
| Scheduling / interrupts | Daily administrator, bounded parallel department proposals, periodic execution, SSE events | Add targeted manager triggers and persistent suspend/resume policy. Presence of distant hostiles must not suspend colony work. |
| Work allocation | Native work restrictions/priorities, observed site workers, scoped work-assignment executor | No deterministic coverage optimizer. Use native worker eligibility and observed backlog; models set policy, not arithmetic. |
| Semantic skills | `WorkObjective`, scoped executors, enclosure compiler, routine instant actions | Extend existing decomposition as needed. Do not add room-specific command APIs or duplicate the native registry. |
| Spatial planning / logistics | Persistent `BasePlan`, staged regions, reserved corridors, native validation | Add inexpensive cached distance estimates between semantic areas. Current legal placement is not proof of a good layout. |
| Combat | Security executor supports equipment/pawn jobs; native restrictions checked | No separate fast tactical loop. Needs observed threat assessment and validated combat actions before automation; avoid fabricating a combat policy from hostile count. |
| Strategy expertise | Versioned `data/strategies`, bounded deterministic retrieval | Extend crisis playbooks against sourced game rules; do not insert every playbook in every prompt. |
| Critic / uncertainty | Administrator conflict escalation, single configured local model | Same-model occasional strategic review is compatible. Keep prior user preference: no request for an unavailable larger model. Confidence cannot bypass validation. |
| Persistence / replay | SQLite events/memory, model/tool diagnostics, `benchmark.py`, tribal-eight fixture | Session-scoped persistence is intentional. Add compact decision-boundary replay records and deterministic competing-resource/emergency scenarios; do not serialize the whole map per call. |
| Prompts / telemetry | Scoped schemas, bounded context, outcome dashboard, detailed diagnostics | Remove bookkeeping from prompts as code takes ownership. `executor_schedule` records invoked/skipped transitions without per-tick repetition. |

## First implementation slice

Unchanged administrator approval now preserves the existing execution lifecycle.
Executor invocation is gated after fresh observations, using the same project and work records.
Changed objective, order status or observed blocker immediately opens a review. Ordinary construction
work increments alone do not. An unchanged blocked attempt can be reassessed after one game hour;
other unchanged work after six game hours. Steering/urgent administration bypasses the wait.
Neither a successful HTTP response nor expiration of this interval completes a project.
Project age presented to the administrator now uses game ticks, not wall-clock time.
Existing projects without a creation tick start measured age at their first new administrator review.

This gate reduces model calls, not the current observation/API refresh cost. It is a conservative
commitment mechanism, not a completed event-driven scheduler. Changes outside the observed project
facts may wait for the bounded reassessment; add typed relevant event triggers in the next slice.

## Ordered follow-up

1. Central native-cost resource reservations and executable competing-steel regression.
2. Derived risk facts and hysteretic manager triggers, with explicit unavailable/uncertain inputs.
3. Extend existing project lifecycle with dependencies and emergency suspension/resumption.
4. Deterministic work coverage and goal postconditions beyond individual order completion.
5. Crisis playbooks, separate combat loop, occasional strategic critic, decision replay scenarios.

Each slice requires focused regression tests and a checkpoint. The full acceptance scenario in the
request is not yet implemented or proven by the starter-base benchmark.
