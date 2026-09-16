# Mood relief contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`EnsureMood-<pawn Thing ID>` is a durable colony goal. It enters at the pawn's
observed minor-break threshold, or when the native current thought target is below
that threshold and falling relative to current mood. Recovery requires five mood
points above the threshold and recovery of retained need deficits. Missing pawns,
thresholds or need reads never certify recovery. Native thresholds incorporate
individual traits and ideology; current thought pressure predicts neither a break
probability nor its timing.

The Go routine reviewer keeps the same semantic name for ordinary native pawn IDs;
IDs longer than 210 bytes use `EnsureMoodHash-<digest>` with the exact pawn retained
in the review. Typed nullable storage fields preserve unknown, zero and false.
Manual clears current method proposals and invalidates shared work while retaining
mood history; world replacement and tick rewind reset that history. Departed pawns
with unresolved risk remain unknown, while departed recovered goals retire.

The controller ranks measured food, rest and recreation deficits by their distance
from a 0.5 recovery target. Entry is below 0.3. Each correction uses one pawn and
existing native resources; future mood benefit and labor duration remain unknown.
Native cached thoughts, cache validity, traits and other needs remain evidence.
Food, shelter and temperature provisioning use the existing shared colony goals.
Social recreation, tolerated recreation kinds and environmental eligibility remain
native job-giver choices. Thoughts without a measured eligible corrective method
produce an explicit blocker, including relationship and ideology choices requiring
player direction. A facility placement is never a mood or need postcondition.

`home/relieve_need` offers one ordinary food, rest or recreation job. It checks an
exact pawn and current job identity, current timetable assignment, player-forced
jobs, queued work, draft/mental/medical state, carried cargo, fire, native priority,
target restrictions, safe reachability and reservations. It uses the installed
native job givers and never changes schedules, policies, traits, ideology or needs.
Recreation excludes ingestible joy; food relief requires an ordinary ingestion job
and leaves resource acquisition to its own goal. Preview checks admission only and
does not run a job giver or reserve a target. Dispatch can still refuse when no
eligible target exists. Jobs remain ordinary AI work, interruptible by subsequent
player direction and timetable changes.

Hands records intent before dispatch. `need_recovered` requires a fresh native
need level of at least 0.5; an accepted job is only a receipt. Readback is scoped to
the persisted load and plan identities. A lost receipt can be resolved
by observed recovery without replaying the order or fabricating a receipt. Active
breaks and player orders interrupt recovery. Missing reads remain unverified, and
the shared no-progress watchdog bounds issued work. Each need method is attempted
once per goal activation; changed conditions can reopen admission blockers. Unchanged
blockers are rechecked at most once per 2,500-tick observation window, allowing new
facilities or freed reservations to become eligible. The progress watchdog uses
per-pawn need high-water marks so unrelated observations or falling needs cannot
keep stalled work alive.

An active mental break creates an emergency hold and suspends routine development.
The controller does not force recovery, draft the pawn, arrest it or accelerate time
through the break. Fresh observations release the hold when the break ends; player
control and the existing native hazard supervisor retain authority over time.
