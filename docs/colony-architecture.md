# Colony architecture: implementation map

The requested direction is preserved in `colony-architecture-request.md`.
This is an incremental refactor of the Python controller and native RIMAPI, not a replacement stack.

| Capability | Existing implementation | Remaining gap / next change |
| --- | --- | --- |
| State and events | `runtime.poll`, `event_loop`, `world_model`, typed native/OpenAPI responses | Derived medical/power transitions now target managers with game-time clear hysteresis. Broader native SSE event routing still needs typed triggers. |
| Derived facts / prediction | `resources.resource_brief`, `decision_context`, `project_progress` | Supply aggregates and native site blockers exist. Add diet-aware food runway and harvest estimates with explicit missing inputs; do not equate all nutrition with edible accessible food. Power, mood, wealth and labor projections need their own observed inputs. |
| Persistent goals | `semantic.retain_project`, SQLite memory, strategy/day plans | Projects already carry owner, outcome, constraints, success signals, progress and work IDs. Extend these with dependencies, deadlines and suspension; do not add another goal queue. Legacy `memory.goals` and executable projects need consolidation. |
| Action lifecycle | `runtime.execute`, `reconcile`, `reconcile_projects` | Unknown-before-send persistence, in-flight duplicate checks, native completion and timeout already exist. Project retirement still relies on administrator judgment; verified orders deliberately do not mean goal achieved. |
| Commitment | `project_schedule`, existing project records | First slice implemented: preserve unchanged approvals; observe before re-invoking; game-time reassessment; changed evidence or steering bypasses waiting. Broader resource/priority commitments remain. |
| Resource arbitration | `resource_budget`, serialized `runtime.execute`, native outstanding construction deliveries | Shared construction admission now subtracts every observed native site's remaining requirements from full allowed stock, plus policy reserves. Native base-definition costs price new placements; existing sites are not charged twice. Future bills, labor and other consumption are not allocated yet. |
| Scheduling / interrupts | Daily administrator, bounded parallel proposals, derived risk transitions, persisted project interruption | Native current life-threatening conditions target Survival and suspend nonurgent new execution. Care, supply access, security and explicitly urgent projects continue. Expand to other observed crises without using hostile presence as a blanket veto. |
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

1. Derived risk facts and hysteretic manager triggers, with explicit unavailable/uncertain inputs.
2. Extend existing project lifecycle with dependencies and emergency suspension/resumption.
3. Deterministic work coverage and goal postconditions beyond individual order completion.
4. Crisis playbooks, separate combat loop, occasional strategic critic, decision replay scenarios.

Each slice requires focused regression tests and a checkpoint. The full acceptance scenario in the
request is not yet implemented or proven by the starter-base benchmark.

## Shared construction budget

`resource_budget` reads the complete native supply survey and every page of construction work.
Player-created sites and retired projects' remaining native orders count too. The native
`ThingCountNeeded` observations are remaining deliveries, so delivered frame contents are not
subtracted again from loose stocks. Cancelling or completing a native site releases its remaining
obligation on the next survey; retiring controller tracking alone does not.

All controller orders are serialized through the same execution lock. Material admission runs
before sending each construction batch, with fixed ingredients and selected stuff from native
definitions. Existing placements and free objects need no extra supply survey. A failed budget
records costs and shortages on the project and emits a diagnostic for administrator review.
Project proposal priorities are retained and determine execution order; same-priority instant
setup remains first. This does not cancel an existing lower-priority blueprint to satisfy a new one.

The budget uses exact native definition names, never interchangeable material-category totals.
`memory.resource_reserves` can hold nonnegative item counts; no default reserve policy is invented.
New-placement quotes currently use published **base** definition costs, not a new native adjusted-cost
quote endpoint. Mod-specific cost adjustments, future production bills, pawn inventories, travel
safety, and external concurrent player/game consumption are outside this first admission model.
It is not a transactional reservation inside RimWorld. The native command still enforces placement
and eligibility; uncertain observations never imply zero cost or unlimited resources.

Regression coverage includes competing 250/180-steel requests against 300 spendable steel,
concurrent execution callers, delivery/completion/cancellation arithmetic, full pagination,
unknown materials, free orders, and material definitions with modded names. Read-only live
verification succeeded against the eight-member tribal test colony; this is not a completed
starter-base gameplay result.

## Derived risks and medical interruption

`world_model` projects native current medical flags, aggregate power headroom, and crop counts/yield
into compact facts. Missing medical observations are explicit. Neither old injuries nor a hediff's
ability to become lethal is treated as a current emergency. Map-wide power headroom is a prompt
to inspect networks, not a claim that their generators and consumers are connected. Harvest and
battery ETAs remain unknown without the necessary rates and network/season inputs.

Medical emergencies wake Survival and bypass seasonal/daily planning on that urgent review. A new
urgent transition cancels a pending model review; already-sent commands retain unknown-outcome
tracking. Power deficits wake Infrastructure. Clearing either alert requires 250 stable game ticks;
pausing does not advance this interval and missing observations cannot clear an active alert.
Daily broad reviews still exist as a backstop; this is not yet a complete event-driven scheduler.

Projects suspended for care retain their IDs, work references, scope and previous status. New
nonurgent execution pauses; existing RimWorld jobs/blueprints are not cancelled. Care, supply access,
security and explicitly urgent projects remain eligible. When the observed emergency clears, the
same projects resume with a fresh assessment. The dashboard shows suspension/resumption outcomes.
No hostile-count or distant-insect construction veto was introduced.
