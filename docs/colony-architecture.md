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
| Work allocation | `work_allocation`, native work types/relevant skills, existing verified priority commands | Coverage policies now select exact workers deterministically. Timetable-only/legacy direct tasks still use the scoped executor. Travel, fatigue, site-specific skills and throughput optimization remain outside the allocator. |
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

1. Native diet/access and consumption observations for actual food runway and harvest timing.
2. Deterministic work coverage and goal postconditions beyond individual order completion.
3. Crisis playbooks, separate combat loop, occasional strategic critic, decision replay scenarios.

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

## Prerequisites, targets and stock trends

Work objectives now carry optional `after_projects` and `deadline_tick`. Prerequisites reference
existing projects, reject cycles/unknown IDs, and run before dependants in the same execution pass.
They require tracked orders to be verified and recheck their native effects before releasing work.
Cancelled or missing prerequisites block for revision; support-only progress cannot release a
construction dependency. This remains an **order prerequisite**, not automatic proof that a
high-level outcome is achieved. Defaults are empty: stockpiles are not prerequisites for food
access, farming or using allowed loose materials. Deadlines request administrator review without
cancelling native orders. Blockers and missed targets appear in Work and Activity.

`food_forecast` samples the existing native human-edible stock summary at game-hour intervals,
keeps at most one day of samples, and requires six game hours before projecting net depletion.
Paused wall time cannot create a forecast. Population changes, rewinds and long observation gaps
reset the sample window. A decline crossing two projected days wakes Survival; the clearing band
is four days with existing clear-time hysteresis. This is an advisory stock-trend signal only.

The native summary includes forbidden/unexplored loose food and excludes pawn inventories. Net
change also includes production, trade and spoilage. Consequently `food_runway_days` remains
unavailable: no nominal per-pawn hunger constant or guaranteed-safe-food claim was invented.
Harvest timing also remains unavailable pending native rates and season/temperature inputs.

## Deterministic work coverage

An optional `work_policy` on work-assignment objectives specifies native work types, worker counts,
manual priorities and protected coverage. Native `relevant_skills` joins pawn skill records using
definition names, including modded definitions. Missing eligibility/skill observations do not become
invented zero-skill workers. Dead, downed and observed drafted workers are excluded.

The allocator preserves adequate existing coverage first, spreads new duties, then compares skill,
passion and stable pawn ID. Protected coverage is processed first, and a sole enabled protected
provider receives no unrelated new promotion. It never disables existing priorities. Infeasible
coverage produces no partial priority batch and requests policy revision from the administrator.
This is a conservative coverage heuristic, not an optimal labor-throughput solver.

Policy execution uses the normal manual-priority toggle and batch-priority API through existing
execution/postcondition verification. It does not call the executor model, force jobs, or treat idle
pawns as proof of bad priorities. Repeated policy checks use fresh observations without model calls.
Work shows the selection separately from verified changes. Timetable-only objectives can retain the
existing scoped planner; the native commands were not replaced with a parallel game-action API.

Tests cover native/modded skill mapping, disabled/downed/drafted exclusions, stable existing coverage,
sole-provider protection, insufficient coverage, normal native command generation and the model-free
project execution path. A read-only live tribal-colony selection was exercised; actual priority writes
and resulting labor throughput have not yet been validated in a full playtest.

## Outcome evidence

Projects now retain `outcome_evidence` separately from order completion. Existing
observations measure designated crop cells, plants present for that crop, and roof
coverage of a project's planned interior. A partial roof survey is unknown, not
proof of completion. Refreshes replace earlier evidence so crop loss or roof loss
can invalidate a previously met measurement. The Work view shows measured results.

This adds no inference calls or new surveys; the existing roof survey now explicitly
requests fresh data. It does not automatically retire projects or interpret free-text
success criteria. Plants present do not establish food sustainability; a roof does not
establish a usable room. Those broader criteria remain explicit review requirements.
Other project kinds currently report unknown outcome evidence. Regression tests cover
missing observations, scope, regressions and the distinction from order receipts;
full gameplay outcome validation remains outstanding.

## Quiet execution while ordinary work progresses

Growing-project scheduling now fingerprints only the selected crop's zone IDs,
cell counts and sowing settings, alongside its target and tracked orders. Other
crops, individual plant jobs and growth percentages no longer trigger an executor
turn. Target changes, removed/replaced zones and sowing changes still reopen it.
Crop loss and labor stalls remain covered by the six-game-hour reassessment;
this is not an immediate crop-damage event detector.

An executor returning no actions and no blockers now watches for changes rather
than entering the hourly failure retry. It does not mark the goal complete.
Explicit blockers retain the shorter retry; player direction still bypasses the
wait. Integration tests exercise repeated execution, not just fingerprint equality.

Successful executor batches now acknowledge their new tracked-order receipts in the
scheduling baseline. The display's copied order list is excluded from scheduling;
the authoritative work records supply those statuses. This avoids a follow-up model
turn solely because the controller recorded its own commands. Existing work changing
during inference, failed/partial batches, later completion and new spatial observations
still trigger review. No game result is inferred from a receipt. The instant-command
integration test verifies state through the fixture API and then checks that a second
execution pass makes no model call; live gameplay performance remains unmeasured.

## Tribal playtest: architect input budget

The September 6 three-minute tribal-eight run issued zero orders: the architect
failed its 55,296-unit request budget before inference, blocking spatial projects.
This prevented measurement of executor scheduling improvements.

The architect now receives active project scope, one spatial strategy entry and
the terrain survey, without full resource discovery payloads or retired project
histories. Identical survey runs merge vertically into lossless rectangles,
encoded as semicolon-separated `x1,z1,x2,z2,class` rows with inclusive coordinates.
Coordinates, bounds and their terrain class table are protected from generic
truncation; an oversized map fails explicitly instead of becoming incomplete.
Native placement validation remains authoritative.

A read-only reconstruction against that live colony now fits at 53,167 conservative
input units without trimming, against the same 55,296 budget. This validates the
request-size fix, not layout quality or successful starter-base construction.
