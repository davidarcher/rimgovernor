# #256 Vanilla alert rows interrupt the poll, disable authority and strand or cancel the haul in flight (routinehaul/storage)

open · davidarcher · 2026-09-19 · 0 comments · https://github.com/davidarcher/rimgovernor/issues/256

labels: priority:P1, area:G01

Two consecutive `routinehaul/storage` runs under the #241 build (worktree `rimgovernor-issue-11-2ac376`, `.rimgovernor/runs/serve` and `serve2`) failed after the poll interrupted on vanilla alert rows: `[clock-scheduler] poll: interrupting gap=false events=*clockpb.Event_Alert,*clockpb.Event_Alert` (`RimWorld.Alert_NeedWarmClothes|High`, `RimWorld.Alert_NeedColonistBeds|High`, tick ~860). `clock.EventInterrupts` treats every event kind it does not list as an interruption hold, so an ordinary alert disables authority (`writer authority unavailable`, worker calls `context canceled`), the harness keep-alive re-acquires (`authority_reacquisitions.reacquired: 3`), and the haul in flight is lost:

- run 1: `routine-haul-9fa5f031…-0` stayed `prepared` (transition `Kind:prepare … Native:10`) under root `Native:12`; the worker logged `authorize plan=routine-haul-… err=plan or action identity already exists: plan is not authorized under the root plan` 179 times, `Haul.step result: reason=no_active_deficit` (goal `MaintainStorage` Need `recovered`, its `goal_methods` row still bound to the plan), `EvaluateClockWindow … refused=[no_work]`; the case failed `first haul completion: wait stalled: signature "prepared|0|" unchanged for 3m1s`.
- run 2: the first haul completed; after the same alert interrupt the second plan `routine-haul-ea8dc7a1…` `reached cancelled instead of completed`.

Both alert pages arrived on unheld reads (`wait_ms` 0, the step's bundle read, 3.3 s under peer load), so this is not the held poll from #241. `speedmatrix/plain` sees the same alert rows at each stage start and passes, because nothing is in flight then.

Resolve: (a) stop treating `Event_Alert` (and other informational kinds) as an interruption hold in `EventInterrupts` — alerts are facts, like outcomes and authority changes, unless the alert is a threat; and (b) a re-acquire must not strand a `prepared` action under the old generation while its goal's method row keeps the goal from re-planning: retire the method or re-prepare the action under the new root (#122 taught the stores to re-prepare `not_ready`; the unauthorized-plan path does not).

