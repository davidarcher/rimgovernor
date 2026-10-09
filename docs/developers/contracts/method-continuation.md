# Method continuation

[Contracts](README.md) · [Control loop](../architecture/control-loop.md)

A Method owns an attempt at an observed outcome. Applied intent proves the write,
not the Concern's completion. Each family keeps its identity, memory and release
path. The common contract is a set of transitions, not another workflow engine
or persistent Method store.

| Evidence | Required decision |
| --- | --- |
| Stable prerequisites and useful issued work | Continue without replacing commands. |
| Required observations unknown | Hold useful work; neither infer completion nor admit its replacement. |
| Known danger, refusal or lost prerequisite | Reconsider. Unknown unrelated facts cannot erase known invalidation. |
| A newly available useful alternative | Reconsider; replace only when the family selector establishes the alternative. |
| Game-tick progress horizon reached | Reobserve and reassess; never cancel or certify completion solely from elapsed ticks. |
| Expected result observed | Release this responsibility; independently inspect the Concern's complete outcome. |

## Combat shelter

Entry requires live threats and no viable combat assignment. Shelter selects
native-verified layout retreat cells first, then safe roofed rooms, then bounded
standable candidates away from threats on the defended side. Refused unreachable
cells remain excluded by the fight's existing memory. Missing geometry cannot
create a destination or invalidate useful previously issued shelter.

Arrival at a safe cell is useful while threats remain. An arrived pawn within a
hostile's observed weapon range (or adjacent melee reach), a refused destination,
a raid phase change or breach causes reassessment. Newly eligible defenders go
through the ordinary formation selector. Stable moving pawns receive no
replacement; unknown positions do not trigger movement. The existing humanoid
raid wait horizon is 38,000 game ticks; reaching it reassesses the fight without
proving the raid ended. Fight closure and roster undrafting retain their owners.

Reconsideration uses stable `defenders_available`, `shelter_exposed` and
`shelter_unreachable` reasons in existing `admission` flight rows.

## Disaster recovery

Entry requires a current disaster deficit or independently observed exposure
restriction. Pending actions remain owned by the shared Plan. After application,
the current pawn job and target establish whether repair, breakdown or refuel
work still serves an observed pending need. Increasing hit points or fuel does
not justify replacing that useful job. Unknown jobs or targets hold admission.
When the job ends, selection can reconsider known work using the shared Episode's
used Method identities; refusal does not erase that history.

The scheduler's existing game-tick review deadline releases its wait and obtains
fresh observations; it does not cancel the native job. Native building state and
service gates establish recovery. Neither an applied receipt nor an expired
condition duration establishes restoration. Known recovery releases the
Incident; independently observed hazard absence releases area restrictions.

Existing `planner_step` rows distinguish unknown observations, no eligible
worker, missing/protected infrastructure, existing work, exhausted methods and
no pending work. Existing flight readers consume these rows.

## Evidence

`continuation_test.go` applies the stable/reconstruction, unknown, deadline and
observed-outcome requirements to both owners. Shelter tests cover refused/wall
destinations, available defenders and exposed arrival; recovery tests cover job
progress, interruption and observed repair. These prove decisions from native
facts. Movement, path legality and completed repair remain native nightly
evidence. Extend a shared implementation to development only when it removes
duplicated decisions rather than renaming family branches.
