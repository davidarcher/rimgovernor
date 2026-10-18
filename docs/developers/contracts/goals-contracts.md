# Goals family contracts

[Contracts](README.md) · [Persistence](persistence-contracts.md#what-must-survive) · [OTEL signals](otel-signals.md)

The player's goals (`family/goals`, #3489) reach the brain through the goals family (#3494). The family owns
status only and proposes no plans: a goal becomes demand on a channel that already exists, and that channel's own
Need and safety floors decide what happens.

## Needs

Each goal is a Development Need `goals/<kind>/<id>` (`<kind>` the hyphenated `GoalKind`, for example
`research-project`). It is Met, Unmet or Unknown by the kind's predicate over the review facts, with the deficit
in the kind's unit (projects, tiers, items, colonists or animals short; 1 for an adoption).

| Kind | Predicate | Demand | Owning Need |
|---|---|---|---|
| `ResearchProject` | the project is in the finished list (Unknown when the census does not list it) | the project joins the research-wanted set while unfinished | `economy/research` |
| `ResearchTier` | the tech tier reached is at least the named `TechTier`; a name that is only a `TechLevel` (Neolithic, Medieval, Ultra, ...) is met once no listed project at that level is unfinished | the first unlocking project of every tier above the one reached, unioned with the unfinished projects of the named tech level while the goal is unmet ([Research tier](#research-tier)) | `economy/research` |
| `StockCount` | the economy stock of the thing is at least `Count` | `Count` is a floor in `RoundsFacts.ResourceNeeds` (max-merged with the other floors) | `economy/resource` |
| `Colonists` | the colonist count is at least `Count` | the joiner capacity and lance population target is `max(100, Count)` | `joiners/offers` |
| `Animals` | the colony holds at least `Count` of the race | the herd plan raises the race's band to `Count` and does not retire it | `animals/herd` |
| `NamedPrisoner` | the pawn (`Target`) is an admitted colonist; `NoMethod` `not a prisoner` when it is neither held nor a colonist, `not_recruitable` when held but unrecruitable | the prisoner is recruited whatever its worth (`PrisonerColony.NamedPrisoners`; Convert first when its ideoligion differs, as for any recruit); custody and food rules stay, and it is not an organ-harvest candidate | `prisoners/recruit` |
| `NamedQuest` | the quest (`Target`) is `EndedSuccess`; `NoMethod` `expired` once the offer lapses, `ended` after another end state, `unknown_quest` when the census does not list it | the quest step considers it before every other offer (`RoundsFacts.NamedQuests`) and accepts it when its admission checks pass; `Blocked` with the failing check (`home_capacity`, `no_spare_pawn`, `capacity_unknown`, ...) otherwise; native `CanAcceptQuest` stays the final check | `joiners/quests` (`joiners/offers` for a joiner quest) |
| `RoomRole` | a standing room has the role (`Target` the `RoomRole` wire name); a `Hospital` goal: the hosted medical beds reach `Count` (1 when it names none). `NoMethod` `no_pursuit` for a role no planner stages, `unknown_role`, `missing_dlc` or `no_required_buildings` for the worship room | see [Room goals](#room-goals) | `shelter/sleeping` (child rooms), `medical/care` (hospital) |
| `Precept`, `Meme` | the ideoligion holds it | none: `NoMethod` `not fluid` on a non-fluid ideoligion, `missing_dlc` without Ideology, otherwise `no_pursuit` | none |
| every other kind | Unknown | none: `NoMethod` `no_pursuit` | none |

A kind with no pursuit is accepted and reported, never refused. A goal's demand is replaced each review, so a
finished or removed goal stops asking. Safety floors are never relaxed: a goal only raises a stock floor, a head
count or a herd band, and the existing planners keep their own refusals.

## Research tier

`ResearchProjectFacts.TechLevel` is `ResearchProjectDef.techLevel`, filled from the definition catalog
(`Bridge/Catalog/Research.cs`) and carried to the review as `ResearchFacts.Levels` (project to level). A
`ResearchTier` target is read as a `TechTier` (the unlock chain, unchanged) and as a `TechLevel`; only `Industrial`
and `Spacer` name both. While the goal is unmet, its demand is the chain's projects plus every unfinished project
the census lists at that level; all of them join the research-wanted set. An unread census asks for nothing from the
level and leaves a level-only goal Unknown.

## Status

`GoalStatus` is `Met`, `Pursuing` (the owning Need is at work or has no block to report), `Blocked` (the owner has
no method and an actionable or held block, or the goal's facts are unread: `facts_unread`) or `NoMethod`. The
family's Method for an unmet goal is only `NeedMethod.Held`, with the subject `pursuing:<owner need>`,
`blocked:<reason>` or `no_method:<reason>`. The owner's state is read from its rounds progress row.

## Room goals

A `RoomRole` goal (#3496) adds demand to the planner that owns the room; it places and builds nothing itself.
`GoalRules.DemandOf` collects it into `GoalDemand.Build` (`GoalBuildDemand`), which `GoalRules.Stamp` carries on
`RoundsFacts.GoalBuild` into every read of the world, so room demand lags the goal by one review. A room goal keeps
asking after the room stands: dropping the need would end the role in the plan and retire the room.

- **Nursery, Playroom, Classroom, DeathrestChamber.** `ChildRoomsRules.GoalRoomNeeds` adds the smallest `ChildRoomNeed`
  of the role (two baby beds; a toy and a decoration; a blackboard and a desk; a casket) to `ChildRoomNeeds`, unless the
  pawns already owe the role, whose larger need stands. Layout sizes and places the room (the playroom, classroom and
  deathrest chamber are `ChildNodeRoles` room nodes; the nursery is the crib station group, so the station population
  floors its babies at the two beds while the catalog has a crib) and `shelter/sleeping` reconciles it.
- **Worship.** The ideoligion's required buildings already size `WorshipRoomNeed`; the goal adds no need of its own and
  reports `no_required_buildings` when the ideoligion requires none.
- **Hospital.** `GoalBuildDemand.HospitalBeds` is the goal's `Count` (at least 1, the largest of several goals). It is the
  minimum bed count of `HospitalRules.SelectHospitalBed` (`HospitalRequest.MinBeds`: demand is `max(patients, floor)`)
  and the station graph's injured floor. The hospital step runs only while `medical/care` is unmet, so
  `MedicalFacts.HospitalShort` (hosted medical beds below the floor, read in `RoundsMedicalFamily.ReadAsync`) keeps the
  Need unmet while no patient is sick.

## Telemetry

One `goal_review` decision per goal per review ([OTEL signals](otel-signals.md)): `target` the goal id, `verdict`
the state, `reason` as above, attrs `kind`, `need`, `owner`, `deficit` and the goal's theme progress.

## Worn-count goals (#3496)

A `WornCount` goal (`Target` the garment def, optional `Qualifier` stuff, `Count` colonists) is met once `Count`
colonists wear the garment (of the stuff, when named; read from each pawn's loadout model). Otherwise the owning
Need is `economy/resource`, and the goal asks three existing channels for help; the goals family places no order:

- **Outfit.** `GoalRules.Stamp` appends the garment to `ApparelPolicyState.Required` of the first `Count` eligible
  `GearPawn`s, ordered by pawn id. Eligible: not blocked, not a soldier, child or prisoner, and the garment is among
  the definitions the pawn can wear as an adult. The outfit policy then allows it whatever the role; wearing stays the
  outfit policy's job. Stamping is idempotent and runs on every read of the world, beside the preference stamping.
- **Material.** The missing count (`Count` less the colonists already wearing it) becomes an `OpenBill` in
  `RoundsFacts.WornBills`, priced from the garment's recipe slots through `GearRules.GearFilter` (the stuff and the
  stuffs sharing its catalog category), and `ClothingRunway.Needs` merges it with the replacement demand. A garment
  not in `RoundsFacts.Garments` (no available recipe, or no outfit allows it yet) prices nothing.
- **Research.** While the goal is unmet, the stuff's research joins the research-wanted set. It is found through the
  plant definitions: the plant whose `HarvestedThingDef` is the stuff gives its `Research` (devilstrand cloth to the
  Devilstrand project), unfinished projects only. Sowing and tailoring then follow the field and clothing channels.

`NoMethod` `no_wearer` when no colonist is eligible; `Blocked` `facts_unread` while the gear census is unread. The
deficit is `Count` less the wearers.

## Toggle

The `goals` family (`RIMGOVERNOR_ROUTINE_FAMILIES`) composes the family and the review's demand injection
(`RoundsCapabilities.Goals`). Off, goals persist and render but ask for nothing.
