# Goals family contracts

[Contracts](README.md) · [Persistence](persistence-contracts.md#what-must-survive) · [OTEL signals](otel-signals.md)

The player's goals (`family/goals`, #3489) reach the brain through the goals family (#3494). The family owns
status and proposes no plans: a goal becomes demand on a channel that already exists, and that channel's own
Need and safety floors decide what happens. The one exception is the trade beacon and geothermal generator a
built-count goal owes, which the family's own work step places ([Built-count goals](#built-count-goals-3496)).

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
| `BuiltCount` | `Count` finished buildings of the def (`Target`) stand (`RoundsFacts.BuiltBuildings`); `NoMethod` `no placer for <def>` when nothing places it | see [Built-count goals](#built-count-goals-3496) | `layout/defense` (turrets), the goal's own Need (trade beacon, geothermal generator) |
| `Precept`, `Meme` | the ideoligion holds it | the ideoligion reform steps toward it, one meme change or precept swap per reform, gated by development points (#3500, [ideology contracts](ideology-contracts.md#creation-and-reform)); `NoMethod` `not fluid` on a non-fluid ideoligion, `unavailable` for a missing def, `missing_dlc` without Ideology; `Blocked` with the first missing prerequisite or `development points <points>/<cost>` | `ideology/reform` |
| `VeneratedAnimal` | the ideoligion venerates the race | the ideoligion reform adds the animal, one per reform, gated by development points (#3517, [ideology contracts](ideology-contracts.md#creation-and-reform)); `Blocked` `venerated_animal_unavailable:<def>` for an animal the game never offers (insects, dryads) or past the limit; the other states as `Precept`, `Meme` | `ideology/reform` |
| `Believers` | every free colonist (`Count` 0) or `Count` colonists share the ideoligion | moral-guide conversion of the free colonist with the lowest certainty, and conversion of recruitable prisoners whatever their worth (#3501, [conversion](ideology-contracts.md#conversion)) | `ideology/convert/<pawn>` |
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
`blocked:<reason>`, `infeasible:<floor>` (below) or `no_method:<reason>`. The owner's state is read from its rounds progress row.

An unmet goal also carries `EtaDay` (#3505), the day the tier-2 colony model completes it: derived each review into the Rounder's `GoalEta` memory, never stored, unknown for a goal tier 2 has no path for. The panel shows it on the Pursuing row.

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

## Importance and deadline

`GoalRecord.Importance` and `DeadlineDay` (a theme's, unless the goal overrides them: `GoalRules.ImportanceOf`,
`GoalRules.DeadlineOf`) are stored, shown, sort the panel and weight theme progress. They also value the goal in the
forecast's capability-days currency (#3503), in the coupled selection only; a goal Need is still a Development Need and
neither field reaches Need selection. `GoalRules.ValuesOf` turns each unmet goal that has a forecast route into a
`ForecastGoalValue` on `RoundsFacts.GoalValues`, and the ledger's `Goals` term pays the goal's worth times the
fraction of the goal an evaluated schedule progresses:

- **Research** (`ResearchProject`, `ResearchTier`): the goal's worth is split over the unfinished projects it waits on and
  paid per research point of each project's remaining cost, so a scheduled research block at one of them earns its share.
- **Worn garment** (`WornCount`): one share (`1 / missing`) per eligible colonist among the first `Count` not yet wearing
  it, on the requirement relief `clothing/<pawn>/required/<garment>` the clothing planner gives that colonist. That relief
  is valued by the goal and is not also counted as a unit of requested development; other relief keeps its unit.
- Other kinds (stock, room, population, ideology, prisoner, quest) have no forecast route yet and earn no ledger value.

The worth is `GoalImportanceValue[importance]` times the deadline's decay at the observed day (full until the deadline,
then `GoalDeadlineDecay` per day, never under `GoalDeadlineFloor`); the constants are in
[kept-constants](kept-constants.md#policy-constants-3469). The player's risk profile and pace set `HarmRiskAlpha`,
`HarmRiskWeight`, `DeathRiskFloor` and `WellbeingWeight` through `ForecastValuePolicy.For`. A goal never relaxes a floor.
The estimated completion day (#3505) waits on the tier-2 colony model (#3470).

**Infeasible.** When every evaluated schedule that progresses a goal breaks the urgent-zero or break-risk floor by more
than the unchanged colony does, the coupled selection reports `goal_infeasible:<goal need id>:<floor>`; the next goals
read keeps a pursuing or blocked goal in `GoalState.Infeasible` with the floor as its reason (verdict `infeasible`).
The death floor is unassessed until the harm model (#3471), so it is never named.

## Telemetry

One `goal_review` decision per goal per review ([OTEL signals](otel-signals.md)): `target` the goal id, `verdict`
the state, `reason` as above, attrs `kind`, `need`, `owner`, `deficit`, `eta_day` and the goal's theme progress.

## Worn-count goals (#3496)

A `WornCount` goal (`Target` the garment def, optional `Qualifier` stuff, `Count` colonists) is met once `Count`
colonists wear the garment (of the stuff, when named; read from each pawn's loadout model). Otherwise the owning
Need is `economy/resource`, and the goal asks three existing channels for help; the goals family places no order:

- **Outfit.** `GoalRules.Stamp` appends the garment to `ApparelPolicyState.Required` of the first `Count` eligible
  `GearPawn`s, ordered by pawn id. Eligible: not blocked, not a soldier, child or prisoner, and the garment is among
  the definitions the pawn can wear as an adult. The outfit policy then allows it whatever the role; wearing stays the
  outfit policy's job. Stamping is idempotent and runs on every read of the world, beside the preference stamping.
- **Material.** The missing count (`Count` less the colonists already wearing it) becomes an `OpenBill` in
  `RoundsFacts.WornBills`, priced from the garment's recipe slots through `GearRules.GearFilter` (the stuff alone in
  its slot), and `ClothingRunway.Needs` merges it with the replacement demand. A garment
  not in `RoundsFacts.Garments` (no available recipe, or no outfit allows it yet) prices nothing.
- **Research.** While the goal is unmet, the stuff's research joins the research-wanted set. It is found through the
  plant definitions: the plant whose `HarvestedThingDef` is the stuff gives its `Research` (devilstrand cloth to the
  Devilstrand project), unfinished projects only. Sowing and tailoring then follow the field and clothing channels.

`NoMethod` `no_wearer` when no colonist is eligible; `Blocked` `facts_unread` while the gear census is unread. The
deficit is `Count` less the wearers.

## Built-count goals (#3496)

A `BuiltCount` goal (`Target` the building def, `Count`) is met once `Count` finished buildings of the def stand
(`RoundsFacts.BuiltBuildings`, counted from the building table). `GoalRules.PlacerOf` names who places a def; a def
nothing places is `NoMethod` with the reason `no placer for <def>` (it still reads Met when that many already stand).

- **Turrets** (`Turret_MiniTurret`, `Turret_Autocannon`, `Turret_Sniper`). The goal's `Count` (the largest of several) is
  `GoalBuildDemand.Turrets`, a floor under `DefenseBudget.TurretBudget` (and so `DefenseIEDs.IEDBudget`): the budget is
  `max(standing + raid-owed, floor)`. The `layout/defense` Need is a verified, opt-in cycle, so the floor takes effect
  only when the layout is next re-verified and proposes its turret tier; stock, power and geometry gates still apply
  and are named on the tier, and the goal reports the Need's block. The count is of the goal's own def while the layout
  builds the rung its armory tier allows, so a goal for another rung stays unmet. A met goal keeps its floor so the
  budget never plans standing turrets away. The mortar has no placer here.
- **Trade beacon** (`OrbitalTradeBeacon`) and **geothermal generator** (`GeothermalGenerator`, a standalone owed
  count). The goal's own Need (`goals/built-count/<id>`) has a work step (`GoalsRounds.WorkAsync`, scheduled on
  `ActionKind.Building`) modelled on the biotech charger and gene bank: one building Method per count standing
  (`built-<def>-<n>`) on a free site previewed natively (`FreeSitesRules.FreeSites` near the colony centre for the
  beacon; the nearest unoccupied steam geyser for the generator) and admitted by claims. The family proposes no held
  Method for these goals; the step's verdict (`field_unavailable`, `no_space`, existing work, admitted) is the Need's
  progress row, which the goal reports as `Blocked` when the Need has no method. The step does not wire the
  generator; the power family connects it.

## Toggle

The `goals` family (`RIMGOVERNOR_ROUTINE_FAMILIES`) composes the family and the review's demand injection
(`RoundsCapabilities.Goals`). Off, goals persist and render but ask for nothing.
