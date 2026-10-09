# Mood relief contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

`EnsureMood` is a Response: one incident per pawn, keyed by the pawn's Thing ID as
its subject, never a concern row. It enters at 0.10 above the pawn's observed
minor-break threshold, or when the native current thought target is below that
threshold and falling relative to current mood. Recovery requires 0.15 above the
threshold and recovery of retained need deficits. Missing pawns, thresholds or need
reads never certify recovery. Native thresholds incorporate individual traits and
ideology; current thought pressure predicts neither a break probability nor its
timing. The [pawn profile](work-assignment.md#pawn-profile) exports the trait flags
(Pyromaniac, chemical interest, NightOwl) for relief and containment ordering.

Typed nullable storage fields preserve unknown, zero and false. Manual clears current
method proposals and suspends shared work while retaining mood history; world
replacement and tick rewind reset that history. Departed pawns with unresolved risk
remain unknown; departed recovered occurrences close.

The controller ranks measured food, rest and recreation deficits by distance from a
0.5 recovery target. Entry is below 0.3. Each correction uses one pawn and existing
native resources; future mood benefit and labor duration remain unknown. Food,
shelter and temperature provisioning use the shared colony concerns.

**Psychic drone.** A pawn whose observed thought rows carry the drone's own
`PsychicDrone` thought (kept only while it pulls the mood down; the same def's
soothe stage is dropped) enters early: the entry margin above the minor-break
threshold is the drone's offset as a mood fraction, at most 0.15, and every need
short of the 0.5 target is a cause while the thought lasts. Pawns without the thought
keep the ordinary entry, whatever the condition census says.

## Facility provisioning

The routine pawn read carries each colonist's grouped thought rows (memories and the
situational cache, the `social` block). The census carries every thought row (positive ones too, e.g.
`KnowBuriedInSarcophagus`); the provisioning review reads only the rows that pull mood down
and maps removable environment thoughts to the upkeep concern whose facility removes
them (a thought names every concern providing it):

| Thought | Concern |
| --- | --- |
| `AteWithoutTable`, `NeedJoy` | `EnsureComfort` |
| `SleptOutside`, `SleptOnGround`, `NeedRoomSize` | `MaintainHousing` |
| `EnvironmentDark` | `MaintainLighting` |
| `EnvironmentCold`, `EnvironmentHot` | `EnsureTemperatureSafety` |
| `NeedBeauty` | `MaintainCleanFacilities` |

When those thoughts carry at least half of the pawn's negative thought offset, the
pawn's mood state records the owners (most negative first) and the method proposal is
`facility_provision` naming the first owner instead of a relief job.
`DetectRounds` raises each owner's development deficit to at least the fraction of
reviewed pawns under it; the owner's own census still decides whether it is active
and what it builds. A recovered owner is never re-raised; when no owner concern is
active with a deficit the planner falls back to the measured need method. An
unreadable social block keeps the previous provisioning; a readable one with no such
pressure clears it.

`SleptInBarracks` is removable pressure no concern owns (nothing builds private
bedrooms): when it dominates a pawn's negative offset the mood state records it
(`Unowned`) and, once no measured need method remains, the proposal is the explicit
`unowned_thought_pressure` blocker naming the thought instead of
`no_measured_correctable_need`; measured relief runs first. Apparel and social
memories stay native relief and recovery evidence. Every review that provisions
`EnsureComfort` must rank it with a deficit at least the provisioned fraction
(snapshot-tested). Schedules are never written. Social recreation, tolerated
recreation kinds and environmental eligibility remain native job-giver choices.
Thoughts without a measured eligible corrective method produce an explicit blocker,
including relationship and ideology choices requiring player direction. A facility
placement is never a mood or need postcondition.

### Thought trigger table

`go/internal/policy/thought_triggers.tsv` lists every vanilla ThoughtDef (all installed
packs) with how the game grants it and the game state it reads. `go run ./cmd/thoughtaudit`
(from `go/`) regenerates it from the installed game: it decompiles `Assembly-CSharp.dll`
with the global `ilspycmd` tool (`dotnet tool install -g ilspycmd`; not pinned in the repo)
and reads the defs under `Data/*/Defs`. Columns:

- `def`.
- `kind`: `situational` when the def has a `workerClass`, else `memory`.
- `grant`: the worker class, or the memory trigger categories (`ingest`, `sleep`, `social`,
  `ritual`, `other`) with the code and XML sites that grant it.
- `dependency`: sorted `;` tokens (`room_stat:<Stat>`, `room`, `room_role:<Role>`,
  `need:<need>`, `apparel`, `temperature`, `hediff`, `light`, `other`) read from the worker,
  or from the granting method body, so a method granting several thoughts lists the state
  of all of them.
- `owner`: the concern that can remove the thought. Hand-kept: regeneration preserves it
  and leaves new defs unowned.

There is no check mode; a stale row shows as unowned. Modded thoughts are out of scope.
`moodProvisionOwners` keys `HighExpectations`, `SkyHighExpectations` and `AteAwfulMeal`
are not ThoughtDefs in the installed game, so the table has no rows for them.

## Relief jobs

A `GiveJobIntent` with `relieve_need` on Actions/Apply offers one ordinary food, rest
or recreation job. It checks an exact pawn and current job identity, current
timetable assignment, draft/mental/medical state, carried cargo, fire, native
priority, target restrictions, safe reachability and reservations. It uses the
installed native job givers and never changes schedules, policies, traits, ideology
or needs. In Auto, forced and queued work do not prohibit a fresh relief admission;
current job identity and native interruptibility still guard the replacement.
Recreation excludes ingestible joy; food relief requires an ordinary ingestion job
and leaves acquisition to its own concern. Preview checks admission only: it does not
run a job giver or reserve a target, and dispatch can still refuse when no eligible
target exists. Jobs remain ordinary AI work, interruptible by player direction and
timetable changes.

Hands records intent before dispatch. `need_recovered` requires a fresh native need
level of at least 0.5; an accepted job is only a receipt. Readback is scoped to the
persisted load and plan identities. A lost receipt can be resolved by observed
recovery without replaying the order or fabricating a receipt. Active breaks and
player orders interrupt recovery. Missing reads remain unverified, and the shared
no-progress watchdog bounds issued work using per-pawn need high-water marks, so
unrelated observations or falling needs cannot keep stalled work alive. Each need
method is attempted once per concern activation; changed conditions can reopen admission
blockers, and unchanged blockers are rechecked at most once per 2,500-tick window.

## Mental breaks and containment

An active mental break keeps its pawn's mood incident in deficit and releases its
reserved work through shared cancellation. State kind, aggression and age remain
mood evidence. Non-violent breaks continue ordinary clock windows.

SocialFighting continues ordinary clock windows despite its native aggression flag:
it neither activates ActiveCombat nor selects a subdue response. Independent hostile
threats and urgent medical needs keep their emergency classification.

Other standing aggressive colonists activate defense. The controller drafts one or two
nearest healthy armed-melee colonists and applies an AttackMelee `GiveJobIntent`; the
undraft sweep releases them afterward. Other dispatch stays outside an eight-cell
radius of the target; uncertain attempts remain reconcilable. Downing the target ends
containment and lets the ordinary RESCUE planner carry the pawn to a colonist bed
using native bed selection. There is no Capture or prisoner custody; Arrest remains a
non-aggressive custody operation. `go/internal/policy/mood_snapshot_test.go` replays
`SelectBreakSquad` and `SelectRescue` over inputs recorded from a tribal8 run: of
eight colonists, only the two wooden-club responders beside the berserk target are
chosen to SUBDUE, and once it is down RESCUE pairs a rescuer with it. Native bed
delivery itself is not re-proved there.

Containment clearance applies to new dispatch's observed worker and explicit target
positions (including the fixture's nearby damaged wall), not a predicted walking
route. The case audits ordered native dispatch requests against pawn censuses
throughout containment while repair stays enabled. SUBDUE and its plan-owned drafts
are exempt; the undraft sweep remains available. A receipt or completed plan alone
cannot satisfy the native bed outcome.

### Native subdual

The AttackMelee `GiveJobIntent` accepts exact colonist snapshots and an aggressive,
standing colonist target. It drafts an undrafted responder (the plan's draft keeps it
drafted) and issues an ordinary AttackMelee job; an existing draft is retained.
Unarmed and melee responders are legal; ranged weapons are refused. The job prefers a
legal blunt verb, including fists, without changing native damage. It ends when the
target is downed or its aggressive break ends. Death is failure, never successful
containment. Progress requires the same order on a still-drafted responder. The
mood/subdue case covers native refusal, resend and living containment.

## Recreation

`EnsureComfort`'s basic phase supplies a reachable recreation source; its ranked
phase adds a second distinct building-backed joy kind for multiple colonists, or for a
lone colonist bored with their only reachable kind. The native census supplies kinds,
each colonist's tolerance and boredom, and available building methods. Selection
prefers a researched, powered television, then billiards, chess or horseshoes when
they add a kind. Two kinds cap this provisioning; inaccessible existing facilities
are access blockers, not reasons to duplicate them.

After Brewing research, ordinary resource production targets twelve Beer and twelve
SmokeleafJoint, with small hops and smokeleaf plots when climate permits. Each
colonist holds its own drug policy (the [`drug_policy` action](action-contracts.md)):
beer, smokeleaf and psychite tea for joy unless addiction risk or a trait rules them
out. Every addiction, a child's included, gets a scheduled dose and is never left cold
turkey: weaned at an interval widening as severity falls when the colony's stock of
the drug covers the weaning doses, otherwise maintained at the drug's addiction
interval and allowed for the need; luciferium is always maintained. Each colony
prisoner holds its own policy too (inputs on its population row): no recreation, only
that maintenance. Recreation relief itself excludes ingestible joy; RimWorld chooses
ordinary drug use. `policy/drug_policy_test.go` proves the entries.

## Observations

**Active mental state.** The colonist status and pawn list reads carry the native
`mental_state` defName, `mental_state_is_aggro` (`MentalStateDef.IsAggro`) and
`mental_state_ticks` (`MentalState.Age`, 30-tick granularity). They describe the
active state, not a prediction from mood, and are absent when no state is active. Go
projects them as `EmergencyPawn.MentalState`, a `Fact[policy.MentalState]`; missing
fields leave it unknown. Worker eligibility uses mental-state presence.
`pawn/mental-state` uses the test-only `test/mental_state_berserk` fixture and
verifies both reads.

**Inspiration.** Pawn rows carry `inspiration`, the current `InspirationDef`
defName. An empty string is a known "no inspiration"; the field is absent when the
pawn has no inspiration handler, and Go keeps that unknown. It reaches
`policy.PawnProfile.Inspiration` through `WorkPawn.Inspiration`; nothing acts on it.
`pawn/inspiration` uses the test-only `test/inspire_creativity` fixture and reads
`Inspired_Creativity` back through the profile.
