# Ideology contracts

[Contracts](README.md) · epic #1653, read side #1654

How the ideoligion reaches Go. It follows the wire pattern of #1333: each
concept is modelled once, and there is no Ideology read tool.

| Fact | Where it rides | Go |
| --- | --- | --- |
| Static defs: memes, precepts with typed effects, role precepts, ritual patterns | `DefinitionCatalog.ideology` (`IdeologyCatalog`), read once per load token | `bridge.DefinitionCatalog.Ideology` (`policy.IdeologyDefs`) |
| Per-pawn: ideoligion id, held role, certainty | Pawn row's `policy_inputs` (`ideo_id`, `ideo_role`, `ideo_certainty`) | `policy.PawnPolicyInputs` |
| Colony: precepts and roles in force, ritual state, building precepts | `BundleSnapshot.ideology`, an omittable section with watermark `ideology` (#1347) | `bridge.RoutineFrame.Ideology`, `policy.Facts.Ideology`, `facts.Ideology` |

Native names are from the game's reference assemblies (`PreceptDef`,
`PreceptComp`, `MemeDef`, `RitualPatternDef`, `Ideo`, `Precept_Role`,
`Precept_Ritual`, `Precept_Building`, `Pawn_IdeoTracker.Certainty`); the
writer is `NativeIdeologyObservation.cs`.

## Static defs

Every def is read from the game; no Go code lists a def name.

- A precept def is the degree of its issue: `Slavery_Abhorrent` through
  `Slavery_Honorable` are `PreceptDef`s of the Slavery issue. The game has no
  separate degree field.
- `PreceptDefinition.effects` is one row per `PreceptComp`, typed by
  `PreceptEffectKind`. A thought comp carries its thought and each stage's
  `baseMoodEffect` (negative penalises, positive approves). An event comp
  carries the `HistoryEventDef` it reacts to. An unwilling comp is what the
  member refuses (forbids), with the traits and hediffs that cancel it. A
  comp class the contract does not type is `OTHER` with its `comp_class`
  only. `policy.PreceptEffect` exposes `Penalises`, `Approves` and `Forbids`.
- `PreceptDefinition.flags` names the action rules a def switches on, by
  game member: `approvesOfSlavery`, `approvesOfCharity`, `approvesOfBlindness`,
  `approvesOfRaiding` and the four `disallow*Camps`.
- A role precept def is a `RoleDefinition` (requirements, effects, disabled
  and required work tags, believer thresholds); it is not repeated in
  `precepts`.
- A `RitualDefinition` is a `RitualPatternDef`: cadence
  (`ritualFreeStartIntervalDaysRange`), the obligation target filter's
  buildings (`required_buildings`), the behavior's role slots and when it may
  start. Required buildings also come from `PreceptDefinition.buildings`
  (`buildingDefChances`) and `MemeDefinition.required_rituals`.

## Colony section

`IdeologySnapshot` is the player faction's primary ideoligion
(`FactionIdeosTracker.PrimaryIdeo`): its memes, the precepts in force (roles,
rituals and building precepts held apart), each role's holders as pawn row
refs, each ritual's raw `lastFinishedTick`, active obligation count and repeat
penalty flag, and each building precept's ThingDef. The section is absent,
with no watermark, without Ideology or a primary ideoligion; Go then holds
`Facts.Ideology` unknown.

`bridge.DecodeIdeology` resolves every def name against the catalog and fails
the frame on a name the catalog lacks, a repeated precept id, a missing field
or a section whose load has no Ideology defs. `lastFinishedTick` is passed
through as read: Go interprets no never-performed sentinel.

`policy.Ideoligion` pairs the defs with the held facts and answers
`EffectsInForce` (the typed effects of every precept in force) and
`RequiredBuildings` (building precepts' ThingDefs and the held rituals'
required buildings).

## Precept rule

`policy.ActionStance(facts.Ideology, action, subject)` (#1655) answers whether the precepts in force
allow, approve, penalise or forbid an action, for the colony or one pawn. The action is the
`HistoryEventDef` it raises (callers bind their action to the event; no def is listed in policy), or
`Apparel`. The verdict carries the stance, the doer's and witnesses' mood cost (worst stage of each
penalising thought), the refusal chance of unwilling effects (cancelled by the pawn's nullifying
traits and hediffs) and the effects behind it. An unread ideoligion, or an `APPAREL` effect (no
payload on the wire), is `unknown`: callers hold. There is no `required` stance: no effect kind types
a requirement.

Consumers: human butchery (`policy.SelectHumanButcher`, #1657) skips a worker the rule forbids for
`HistoryEventDefOf.ButcheredHuman`; an unread ideoligion forbids nothing there, and the native
disposition still gates the worker. The game raises no history event for burying, entombing or
burning a corpse (`HistoryEventDefOf` has none), so those choices have nothing to bind to.

## Not here

Building and room planning (#1658), role
assignment (#1661) and ritual intents (#1659) consume these facts; writes
reuse the shared Assign and Ritual shapes.
