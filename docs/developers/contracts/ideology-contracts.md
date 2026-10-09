# Ideology contracts

[Contracts](README.md)

How the ideoligion reaches Go. It follows the wire pattern: each
concept is modelled once, and there is no Ideology read tool.

## Creation and reform

`common.IdeoligionDesign` names memes, plain precept definitions and an explicit
fluid/fixed mode. Roles, rituals and building instances are initialized or
preserved by the game's foundation. Creation carries the design in
`NewColonySpec.ideoligion`, before starting colonists receive their ideoligion.
`governor_ideoligion` requests Go selection; an explicit player design takes
precedence. Missing required facts fail explicitly, without a random fallback.
The existing definition-catalog request's `creation` mode reads static generated
rows at a fresh main menu, without inventing a map identity.

The pure selector searches initial fluid designs with one structure and one
normal meme. Native rechecks the game's count, faction, exclusion, issue and
required-precept rules. Costs use an explicit lexicographic ordering: fewer
action/work restrictions, lower worst known mood penalty, fewer mandatory
obligations, then higher known mood benefit. Catalog-only positive mood stages receive no creation benefit credit;
their potential is preserved conservatively during reform. Stable ordering resolves
equal costs. Search stops unavailable at 100,000 visited candidates. Unvalued stat,
ability, apparel, mental-break and mod effects are unavailable; mandatory ritual
and other specialized meme choices are excluded when their feasibility cannot
be established. The selector does not assume unknown obligations cost nothing.

`ideoligion-reform` composes `ImproveIdeoligion` in People. It uses the same
evaluator and a current emergency census; Manual follows the shared suspension
path. It considers one plain precept change at a time, preserving memes and
special instances whose transition costs cannot be valued. Only strict cost
improvement that preserves known mood benefits is admitted. Complete believer
coverage must demonstrate an assigned work restriction or an observed mood
penalty removed by the change. Mood relief must exceed the candidate's worst
known penalty across all believers; losing an observed positive thought holds.
No score component may worsen. Unknown eligibility,
effect costs or safety holds; ties retain the current design. Eligibility comes
from the keyed section's fluid mode, points, reform count and next threshold.
The pure result carries a typed comparison (current/candidate costs, work and
mood relief, new mood cost and decision); an admitted Method's existing reason
shows that comparison in player inspection without storing a second score.

Hands dispatches `IdeoligionReformIntent` with expected design and reform count.
Native prepares a detached candidate, validates normal-game eligibility and
compatibility, then commits with `IdeoDevelopmentUtility.ApplyChangesToIdeo`.
Refused requests leave live selections and progression untouched. A new-key
resend whose target already holds at expected count + 1 returns the observed
state without reforming again. Completion requires an observed target and that
counter increment; a receipt alone does not settle the Standard. Pending intent
uses its existing save Record; there is no second current-design store.

Native coverage: `lifecycle/ideoligion-design` and `ideology/legal-reform`.
These prove game creation and write/progression contracts; policy decisions
remain Go tests, including the recorded full catalog fixture.

| Fact | Where it rides | Go |
| --- | --- | --- |
| Static defs: memes, precepts with their comps, role precepts, ritual patterns and behaviors | Rows of the catalog's generated def mirror (`DefinitionCatalog.defs`: `MemeDef`, `PreceptDef`, `RitualPatternDef`, `RitualBehaviorDef`, `RitualObligationTargetFilterDef`, `ThoughtDef`), read once per load token | `bridge.DefinitionCatalog.IdeologyDefs()` (`policy.IdeologyDefs`) |
| Per-pawn: ideoligion id, held role, certainty | Pawn row's `policy_inputs` (`ideo_id`, `ideo_role`, `ideo_certainty`) | `policy.PawnPolicyInputs` |
| Colony: precepts and roles in force, ritual state, building precepts | `BundleSnapshot.ideology`, an omittable section with watermark `ideology` | `bridge.RoundsFrame.Ideology`, `policy.Facts.Ideology`, `facts.Ideology` |

Native names are from the game's reference assemblies (`PreceptDef`,
`PreceptComp`, `MemeDef`, `RitualPatternDef`, `Ideo`, `Precept_Role`,
`Precept_Ritual`, `Precept_Building`, `Pawn_IdeoTracker.Certainty`); the
section writer is `NativeIdeologyObservation.cs`, the defs are filled by the
def mirror's protobuf reflection ([schema generation](../../../contracts/schema-generation.md#def-mirror)).

## Static defs

The catalog mirrors every Ideology def with all of its fields (a mod's
included, `modPackageId` says whose), so a fact the planners do not read yet
is already in the rows: burial and apparel requirements and per-race meat
comps (`PreceptComp_*`, `buildingRoomRequirements`, `roleApparelRequirements`),
role trait and gender requirements (`roleRequirements`), ritual outcome and
spectator filters (`RitualBehaviorDef`, `RitualOutcomeEffectDef`). No Go code
lists a def name; `policy.IdeologyDefs` is a view over the rows, built once
per load, and a row a def names that the catalog lacks is an error.

- A precept def is the degree of its issue: `Slavery_Abhorrent` through
  `Slavery_Honorable` are `PreceptDef`s of the Slavery issue. The game has no
  separate degree field. A def whose `preceptClass` is or derives from
  `RimWorld.Precept_Role` (by the catalog's class chains) is a role, not a
  plain precept.
- `policy.PreceptDef.Effects` is one effect per `PreceptComp`, typed by the
  comp class the mirror filled (`PreceptCompAny`). A thought comp carries each
  stage's `baseMoodEffect` of its `ThoughtDef` row (negative penalises,
  positive approves). An event comp carries the history event it reacts to. An
  `UnwillingToDo` comp is what the member refuses (forbids), with the traits and
  hediffs that cancel it and, for the `_Chance` class, its chance.
  `policy.PreceptEffect` exposes `Penalises`, `Approves` and `Forbids`.
- `policy.RoleDef` is a role precept def: `maxCount` and the skills of each
  `RoleRequirement_MinSkillAny`.
- `policy.RitualDef` is a `RitualPatternDef`: cadence
  (`ritualFreeStartIntervalDaysRange`), when it may start, the obligation
  target filter's `thingDefs` (`RequiredBuildings`) and the role slots of its
  `RitualBehaviorDef.roles` (`RitualRole`: id, role precept, `maxCount`,
  `required`). The generator mirrors `RitualRole` although it implements
  `ILoadReferenceable` (its `DataClasses` table): XML builds it.
  Required buildings also come from `PreceptDef.buildingDefChances` and
  `MemeDef.requiredRituals`.

## Colony section

`IdeologySnapshot` is the player faction's primary ideoligion
(`FactionIdeosTracker.PrimaryIdeo`): its memes, the precepts in force (roles,
rituals and building precepts held apart), each role's holders as pawn row
refs, each ritual's raw `lastFinishedTick`, active obligation count, repeat
penalty flag and whether a lord job of it runs (`running`), and each
building precept's ThingDef. The section is absent, with no watermark, without Ideology or a primary ideoligion; Go then holds
`Facts.Ideology` unknown.

`bridge.DecodeIdeology` resolves every def name against the catalog rows and
fails the frame on a name the catalog lacks, a repeated precept id, a missing field
or a section whose load has no Ideology defs. `lastFinishedTick` is passed
through as read: Go interprets no never-performed sentinel.

`policy.Ideoligion` pairs the defs with the held facts and answers
`EffectsInForce` (the typed effects of every precept in force) and
`RequiredBuildings` (building precepts' ThingDefs and the held rituals'
required buildings).

## Precept rule

`policy.ActionStance(facts.Ideology, action, subject)` answers whether the precepts in force
allow, approve, penalise or forbid an action, for the colony or one pawn. The action is the
`HistoryEventDef` it raises (callers bind their action to the event; no def is listed in policy), or
`Apparel`. The verdict carries the stance, the doer's and witnesses' mood cost (worst stage of each
penalising thought), the refusal chance of unwilling effects (cancelled by the pawn's nullifying
traits and hediffs) and the effects behind it. An unread ideoligion, or an `APPAREL` effect (no
payload on the wire), is `unknown`: callers hold. There is no `required` stance yet: the apparel, burial and room comps are
in the rows, no stance reads them.

Consumers: slavery (`EnslavedPrisoner`), organ harvest and sale
(`HarvestedOrgan`, `SoldOrgan`; the doer's cost once, witnesses' on every
colonist) and the diet facts (`AteHumanMeat*`, `AteInsectMeat*`, `AteMeat`,
`AteNonMeat`, `AteFungus*`) read the rule; no precept name is listed. A pawn's
diet reads the pawn row's own `precepts` against the catalog defs
(`Ideoligion.HeldBy`), since a pawn's ideoligion may differ from the primary
one. Apparel stays native: the nudity requirement is the game's
`IdeoPrefersNudityForGender`, the apparel precepts and role requirements are
typed rows on the pawn. Human butchery (`policy.SelectHumanButcher`) skips a worker the rule forbids for
`HistoryEventDefOf.ButcheredHuman`, or cannot answer for: an unread ideoligion with Ideology
installed holds. The native disposition still gates the worker. Selling a surplus prisoner to the
tribute collector (`policy.FavorPrisonersHeld`) asks the rule about `SoldPrisoner` for the
colony and sells only when it is allowed or approved: a mood cost, a refusal or an unread ideoligion
with Ideology installed holds; without Ideology it sells.

`BundleSnapshot.ideology_active` (`ModsConfig.IdeologyActive`, carried on every frame) is the
distinct DLC-absent signal: an absent ideology section alone is unread, never absent. `false` is
`policy.IdeologyRead.Absent`: the rule answers `allowed` ("Ideology not installed") and butchery
falls through; unknown or `true` with no section stays unread and holds. An ideology section with
`ideology_active` false fails the frame. Only butchery reads the signal so far
(`RoundsFacts.IdeologyInstalled`, `IdeologyRead`). The game raises no history event for burying, entombing or
burning a corpse (`HistoryEventDefOf` has none), so those choices have nothing to bind to.

## Role assignment

`MaintainIdeoRoles` (`policy.RoleAssignments`) keeps the role precepts
filled. Each review gives every active role with fewer holders than its def's
`maxCount` to the available believer (same `ideo_id`, no role yet, skills
read) who best fits it: a pawn must meet, for every role requirement that
lists skills, one listed skill at its minimum level and enabled; the score is
the sum of the best matching skill levels, ties go to higher certainty, then
the lower pawn id. Requirements without skills are the game's to judge at
apply. A pawn that holds a role is never moved. The planner commits one Assign
whose `thing_id` is the role precept id and whose expected previous is none;
the write is the shared Assign intent ([action contracts](action-contracts.md)),
postcondition: the pawn holds the role. Native refusals follow the shared refusal budget
per pawn and role ([action contracts](action-contracts.md)). Composed by the `ideo-roles` routine family.

## Ritual scheduling

`MaintainRituals` (`policy.PlanRituals`) begins each due ritual precept
through the Ritual `begin` verb ([action contracts](action-contracts.md)).
Every value is read from the game; no ritual, building or role name is listed
in Go.

- **Due** (`HeldRitual` against its `RitualDef`): not running
  (`IdeoRitual.running`: a `LordJob_Ritual` of the precept exists). An active
  obligation is due. Otherwise the pattern must allow a free start
  (`canStartAnytime` or `alwaysStartAnytime`), the repeat penalty must be off
  and the pattern's own cooldown, the minimum of
  `ritualFreeStartIntervalDaysRange` in days, must have passed since the raw
  `lastFinishedTick`. Each ritual has its own cooldown.
- **Spot**: the cells of the finished buildings of the pattern's
  `required_buildings`, from the frame's building table
  (`Facts.RitualSites`), in building id order. The game offers the begin
  command at the building its obligation targets, so the planner tries the
  sites in turn. A held ritual whose pattern the catalog lacks, or that names
  no required building, is not planned: nothing says where to hold it.
- **Attendees**: available believers of the ideoligion (same `ideo_id`). A
  slot bound to a role precept takes that role's active holders up to the
  slot's `max_count` (the moral guide leads through it); a required slot with
  no role precept takes the most certain unassigned believer; an optional
  unbound slot stays empty. The organizer is the first pawn of the first
  filled slot and every other believer attends as a spectator. A required slot
  nobody can fill means no plan. One pawn attends one ritual per review.
  Whether the game accepts a pawn in a slot is its check when the begin
  applies (`PawnNotAssignableReason`).
- **Calm**: nothing is planned unless the emergency census reads no hostile
  threat and no critical patient (`policy.RitualCalm`); an unread census plans
  nothing. A fight therefore holds the ritual and releases its attendees.
- **Hold**: the attendees of every plan are kept off the Sleep timetable
  (`policy.HeldOffSleep` merges them with the bestowing ceremony's
  `CeremonyHold` for `PlanSchedulesHeld`), so they are awake when the begin
  lands.

The planner commits one Ritual `begin` per step: the plan's organizer, the
site, the slot fills and the spectators. A refused begin follows the shared refusal budget
per ritual and site (tried at the next site meanwhile); the
method's reason records the organizer, ritual, site and attendance.
Postcondition: a `LordJob_Ritual` of the precept is running
(`RitualEffect.started`), read back as `running`. Composed by the `rituals`
routine family.

## Not here

The Ritual `begin` verb ([action contracts](action-contracts.md)) names a held ritual by its
`IdeoRitual.id` and fills the role slots of the behavior's `RitualRole`s.
Building and room planning consumes `RequiredBuildings`:
[worship room](../architecture/facilities.md#worship-room-ideology).
