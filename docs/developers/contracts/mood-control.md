# Mood relief contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`EnsureMood-<pawn Thing ID>` is a durable colony goal. It enters at the pawn's
observed minor-break threshold, or when the native current thought target is below
that threshold and falling relative to current mood. Recovery requires five mood
points above the threshold and recovery of retained need deficits. Missing pawns,
thresholds or need reads never certify recovery. Native thresholds incorporate
individual traits and ideology; current thought pressure predicts neither a break
probability nor its timing. The [pawn profile](work-assignment.md#pawn-profile)
exports the trait flags (Pyromaniac, chemical interest, NightOwl) for relief and
containment ordering to consume.

The Go routine reviewer keeps the same semantic name for ordinary native pawn IDs;
IDs longer than 210 bytes use `EnsureMoodHash-<digest>` with the exact pawn retained
in the review. Typed nullable storage fields preserve unknown, zero and false.
Manual clears current method proposals and suspends shared work while retaining
mood history; world replacement and tick rewind reset that history. Departed pawns
with unresolved risk remain unknown, while departed recovered goals retire.

The controller ranks measured food, rest and recreation deficits by their distance
from a 0.5 recovery target. Entry is below 0.3. Each correction uses one pawn and
existing native resources; future mood benefit and labor duration remain unknown.

A psychic drone pushes one gender's mood down for days. A pawn whose observed
thought rows carry the drone's own `PsychicDrone` thought (the census keeps it
only while it pulls the mood down; the same def's soothe stage is dropped)
enters early (#408): the entry margin above the minor-break threshold is the
drone's offset as a mood fraction, at most 0.15, and every need short of the
0.5 relief target is a cause while the thought lasts, so relief is proposed
before the drone alone crosses the threshold. Pawns without the thought keep
the ordinary entry, whatever the condition census says.
Native cached thoughts, cache validity, traits and other needs remain evidence.
Food, shelter and temperature provisioning use the existing shared colony goals.

## Facility provisioning

The routine pawn read carries each colonist's grouped thought rows (memories and
the situational cache, the `social` block). The review keeps the rows that pull
mood down and maps the removable environment thoughts to the upkeep goal whose
facility removes them (a thought names every goal providing it): `AteWithoutTable` and `NeedJoy` to `EnsureBasicComfort` and `EnsureComfort`,
`SleptOutside`/`SleptOnGround` to `EnsureInitialShelter`, `EnvironmentDark` to
`MaintainLighting`, `EnvironmentCold`/`EnvironmentHot` to
`EnsureTemperatureSafety`, `NeedBeauty` to `MaintainCleanFacilities` and
`NeedRoomSize` to `EnsureExpansion`. When those thoughts carry at least half of
the pawn's negative thought offset, the pawn's mood state records the owners
(most negative first) and the method proposal is `facility_provision` naming the
first owner instead of a relief job: `DetectRoutine` raises each owner's
development deficit to at least the fraction of reviewed pawns under it, and the
owner's own census still decides whether it is active and what it builds. A
recovered owner is never re-raised; when no owner goal is active with a deficit
the relief planner falls back to the measured need method. An unreadable social
block keeps the previous provisioning; a readable one with no such pressure
clears it. `SleptInBarracks` is removable environment pressure no goal owns
(nothing builds private bedrooms): when it dominates a pawn's negative offset the
mood state records it instead (`Unowned`) and, once no measured need method
remains, the proposal is the explicit `unowned_thought_pressure` blocker naming
the thought rather than `no_measured_correctable_need`; measured relief still
runs first. Apparel and social memories stay native relief and recovery
evidence. The served comfort ladder (`facility/comfort`) audits the raise: every
review that provisions `EnsureComfort` must rank it with a deficit at least the
provisioned fraction, and the report counts the reviews where that fraction is
the ranked deficit. Schedules are never written.
Social recreation, tolerated recreation kinds and environmental eligibility remain
native job-giver choices. Thoughts without a measured eligible corrective method
produce an explicit blocker, including relationship and ideology choices requiring
player direction. A facility placement is never a mood or need postcondition.

`home/relieve_need` offers one ordinary food, rest or recreation job. It checks an
exact pawn and current job identity, current timetable assignment, ordered jobs in
flight (`playerForced`, which this adapter's own orders set too), queued work, draft/mental/medical state, carried cargo, fire, native priority,
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

An active mental break keeps its pawn's mood goal in deficit but is neither a
clock hold nor an emergency that suspends routine development: a break only ends
as ticks pass, so a hold could never observe its clearance. The controller does not
force recovery, draft the pawn or arrest it; other goals' work still opens ordinary
windows, and the existing native hazard supervisor retains authority over time.
