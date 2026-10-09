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

The owner of a thought is the audit table's `owner` column (see below); there is no
second table in code. Every negative thought with an empty owner, or no table row at
all, is **unowned**: when owned provisioning does not apply, the pawn's mood state
records its unowned thoughts (`Unowned`, most negative first) and, once no measured
need method remains, the proposal is the explicit `unowned_thought_pressure` blocker
naming the largest instead of `no_measured_correctable_need`; measured relief runs
first. Social memories are unowned in this sense but stay native relief and recovery
evidence. The apparel thoughts `ApparelDamaged` (ratty and tattered), `DeadMansApparel`
(tainted) and `WrongApparelGender` are owned by `MaintainEquipment`: its levers already
exist (the outfit excludes tainted gear, floors hit points at `apparelMinHP`, offers only
gender-correct definitions, and `ClothingRunway` replaces garments before they tatter),
so dominant apparel pressure raises that concern's deficit and adds no new action.
Cosmetic preferences and Ideology apparel precepts stay unowned. Every review that provisions `EnsureComfort` must rank it with a
deficit at least the provisioned fraction
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

`policy.ThoughtAuditRow` embeds the table (it stays beside the policy package that owns the
owner column, so neither policy nor the bridge imports the other for it). The bridge's
`DefinitionCatalog.ThoughtFacts` joins a catalog `ThoughtDef` row with that row into
`policy.ThoughtFacts`: `WorstOffset` (lowest stage `BaseMoodEffect`; null stages skipped),
`NullifyingTraits`, `NullifyingPrecepts`, `RequiredTraits`, `MinExpectation`, `Dependency` and
`Owner`. A def with no table row (modded, or new since the last regeneration) gets the
dependency `unclassified` and no owner. Policy stays catalog-free.

There is no check mode; a stale row shows as unowned. Modded thoughts are out of scope.
`HighExpectations`, `SkyHighExpectations` and `AteAwfulMeal` are not ThoughtDefs in the
installed game, so the table has no rows for them and no owner entry exists; high
expectations reach the review only through the pawn's `HighExpectations` flag.

### Mood ledger

`policy.BuildMoodLedger` (pure) turns pawns' thought rows (`MoodLedgerPawn`: the
`MoodThought` rows plus traits, precepts and expectation level as facts) and a
`map[def]ThoughtFacts` into:

- per pawn, the mood lost per thought: negative offsets only, most negative first, after
  dropping thoughts the pawn cannot carry (a nullifying trait or precept, a missing
  required trait, an expectation below `MinExpectation`); unknown when the pawn's thoughts
  are unreadable;
- per colony thought, `Pawns` affected and summed `Lost`, ranked most negative first,
  with the audit owners;
- `Unowned`, the same ranking restricted to thoughts with no owner;
- the expectation levels by pawn count, shown only (no concern fixes it).

An unknown attribute or a def without facts never drops or zeroes a loss: the observed
row counts and the source's `Unverified` count rises.

Every Round builds the ledger in `observation/rounds_mood_ledger.go` and publishes it as
`RoundsFacts.MoodLedger` (unknown whenever the mood census is). Inputs: the census thought
rows, `AllThoughtFacts()` from the load's catalog (none without a catalog, so every source
is unverified), each pawn's biography traits and the ideology precepts from its policy
inputs. A pawn whose biography or policy block was skipped has unknown traits or precepts.
The expectation level is not observed yet, so it is always unknown: `MinExpectation`
thoughts stay unverified and the ledger's expectation list is empty. Later children
(display, the colony aggregate, the gathering trigger) read this fact; the ledger itself
changes no relief or provisioning decision.

## Relief jobs

A `GiveJobIntent` with `relieve_need` on Actions/Apply offers one ordinary food, rest
or recreation job. Native checks only that the exact pawn is a spawned,
living, undowned, undrafted colonist outside a mental break, then takes the job and
reservations the installed native job giver returns; the game's refusal, not a native
pre-veto on medical rest, interruptibility, need level, timetable, priority, cargo,
fire, forbidden targets or reach danger, is the answer when no job comes. It never
changes schedules, policies, traits, ideology or needs. Go decides whether relief is
worth asking for from the observed facts.
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

The AttackMelee `GiveJobIntent` accepts exact colonist snapshots and a spawned target
pawn; which target is worth subduing (an aggressive, standing colonist) is Go policy. It drafts an undrafted responder (the plan's draft keeps it
drafted) and issues an ordinary AttackMelee job; an existing draft is retained.
Any violence-capable responder is legal whatever it wields. The job prefers a
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

## Gatherings

The `gathering` action (`GatheringIntent`: `gathering_def` plus a required
`organizer` pawn) starts one vanilla `GatheringDef` party; Ideology precept
gatherings stay under `ritual begin`. Any `GatheringDef` is accepted on the wire.
The intent carries no spot: `GatheringWorker.TryExecute(map, organizer)` takes none
and the game chooses it. Game code read with ilspycmd:

- Native validates with `GatheringDef.CanExecute(map, organizer, ignoreGameConditions)`
  and refuses otherwise. `AcceptableGameConditionsToStartGathering` needs local hour
  4 to 21, danger rating None, no lord job that forbids new gatherings, at least four
  spawned free colonists, none bleeding, under half drafted, and enough potential
  guests (`round(0.65 x colonists)` clamped to 2..10 who may join joy and are awake,
  fed, not bleeding or in a mental state). The worker also needs a spot.
- `GatheringsUtility.PawnCanStartOrContinueGathering(organizer)` must hold: not
  drafted, bleed rate at most 0.3, not a prisoner or slave, blood loss at most 0.2,
  not a wild man, inhumanized or subhuman, spawned, not downed, no mental state.
  Native refuses otherwise.
- Spot choice (`GatheringWorker_Party.TryFindGatherSpot` ->
  `RCellFinder.TryFindGatheringSpot`) prefers a built, colony-owned building of any
  def in `GatheringDef.gatherSpotDefs` (`PartySpot` for `Party`): a random one whose
  cell passes `ValidateGatheringSpot` (standable, not dangerous, roofed unless outdoor
  joy is enjoyable, not forbidden, reservable and reachable by the organizer, enough
  reachable guests). Only when none validates does it fall back to cells within 4 of
  an active `CompGatherSpot` and then within 25 of the organizer, rejecting rooms
  with under 10 cells that are not huge or outdoors. So a PartySpot controls the
  spot; policy keeps one placed (below).
- The party area is the whole room when the spot's room is at most 100 cells, not
  huge and not outdoors, otherwise cells within 18.

### Common-room targets

`policy.CommonRoomTargets` sets the impressiveness target of each dining and rec
room (no beds) to the tech-tier baseline raised one tier per four colonists, plus one
while the ledger shows mood lost to common-room thoughts (`policy.CommonRoomPressure`:
`AteInImpressiveDiningRoom`, `JoyActivityInImpressiveRecRoom`, `NeedRoomSize`), capped at
two tiers above the current one and at Spacer's baseline, never below today's baseline.
`RoomTarget.Cells` adds a size target of three interior cells per colonist from four
colonists, capped at 120. `RoomGate` still charges every upgrade step. Production passes
zero ledger pressure until the ledger is wired in (#2622); nothing yet consumes `Cells`.

### The PartySpot

`EnsureComfort` keeps exactly one free, instant-build `PartySpot` placed, independent
of any gathering. `policy.ReviewPartySpot` scores the dining and rec rooms
lexicographically: the highest observed impressiveness stage (the Impressiveness
`RoomStatDef` score stages, `ImpressivenessLevels.Stage`), ties broken by cell count.
It owes one step, each resolved by a single action through the existing paths:

- no spot stands: a `building` action places `PartySpot` on a free, roofed, indoor cell
  of the winning room (the game's `ValidateGatheringSpot` filter), or on any such cell
  while no shared room qualifies;
- a room of a strictly higher stage than the incumbent's room exists (the incumbent's
  room is read from the observed spot, never stored): the old spot is deconstructed
  with the `deconstruction` action and the new one is placed on a later pass; rooms of
  the same stage never move it;
- duplicates: each extra spot is deconstructed.

The need is `RoundsFacts.PartySpotOwed`. While it holds and neither facility phase is
owed, `inspectComfort` raises `EnsureComfort` in the `spot` phase (served by the ranked
planner) and assesses it Unmet, so comfort is briefly Unmet for the pass or two the
step takes. Guards keep it from holding comfort Unmet: a blueprint or frame already
standing, an unbuildable `PartySpot` (definition unavailable or no builder), no free
cell, or an already spent per-Episode one-shot (`retireBuilding` once per building, one
placement per room) all drop the deficit. Mood relief for `AteWithoutTable` and
`NeedJoy` defers to `EnsureComfort` while it is active, so it defers during that window
too. Gathering itself is #2548.

`GatheringEffect` records `gathering_def`, `organizer_id` and the `spot` the game
chose. `NativeGathering` is an immediate write with no native job: it refuses an
unknown def, a non-colonist organizer, `PawnCanStartOrContinueGathering`, unacceptable
game conditions or a failing `CanExecute`, then runs `GatheringWorker.TryExecute`.
A repeated start applies again without a second party when a joinable gathering lord
job of the def is already running on any map; the effect then reports that job's
organizer and spot. Applied means the lord was created, not that guests attended. A refusal is
final for the attempt; the owning routine replans from live state.

Go dispatches `gathering` as a plain intent (`executor.plainIntents`, bridge
encoder `bridge/gathering.go`), persists it as an `actions` row (`pawn` =
organizer, `definition` = gathering def) and admits it in routine execution
(`roundsExecutableKind`, `roundsActionsSupported`, the clock window kind switch);
tests fail if one registration is missing. The native handler is #2546.

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
