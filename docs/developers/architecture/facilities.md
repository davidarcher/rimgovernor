# Facilities

Room functions are the game's own `Room.Role`; the controller never assigns a
role, it only reads which one the game scored. `policy.FacilityCatalog` is the
per-role matrix over every installed RoomRoleDef, and every planner that
pursues a role walks the same ladder. This page is the ladder and the matrix;
[space and resources](space-and-resources.md) covers siting and budgets.

## The ladder

Each rung is a separate deficit under an existing maintained goal, ranked by
`RankDevelopment` like any other; nothing here adds a per-role goal.

1. **Reuse**: a room the game already scores as hosting the function, or an
   existing bench, bed or spot that already does the job. Reuse always wins
   over building, so a colony that has the facility never gets a second one.
2. **Research**: when the first bench that could produce a `MaintainResource`
   deficit is gated only by unfinished research, the workshop planner records
   the projects on the `production_ladder` journal record and steps aside. The
   next routine review reads that record as the derived `EnsureResearch`
   target (`policy.ResearchGoal`: the default research ladder follows when no need is
   recorded, see [research](../contracts/research.md)), raises the goal, and
   adds Research to the work requirements so a researcher is assigned;
   `RoutineResearchPlanner` selects the prerequisite chain natively.
   Finishing the project clears the record on the next workshop step.
3. **Power**: a bench that needs power is staged only once a generator
   definition is buildable; standing unpowered it is an ordinary consumer for
   `EnsureBasicPower`, which raises its own deficit and connects it.
4. **Bench**: the first candidate bench the planning census reports available
   and buildable by a builder the colony has (unpowered before powered) is
   furnished into a hosting room, or the starter shell is staged first.
5. **Ingredient storage**: once a bench hosts an available recipe for the
   deficit, `RoutineIngredientStoragePlanner` places one allow-list stockpile
   for the recipe's ingredients on the nearest free roofed 2x2 patch inside the
   room the census scores as the Workshop, as a second method under
   `MaintainResource`. Hauling then brings the inputs to the bench. Once
   ComplexFurniture is researched, `RoutineStorageShelvesPlanner` places a
   Shelf inside that stockpile (and the SecureSupplies general store), up to
   a third of its footprint; `MaintainStockpiles` patches each built shelf
   with the zone's desired filter and priority (role `shelf:<buildingID>`)
   and again whenever those change. Native storage capacity
   counts a shelf cell's free slots (three stacks per cell).
6. **Bill**: `RoutineResourcePlanner` dispatches the bill and native readback
   of the rising item count carries the deficit to recovery. Native
   applies a bill on an unfueled bench (`UsableForBillsAfterFueling`):
   a bill waiting on the bench is what makes haulers refuel it, and a bench
   with no bill leaves the clock with no work to run those hauls under.

Each rung reports an explicit reason when it cannot proceed
(`workshop_research_needed`, `workshop_bench_unavailable`, `no_space`,
`unknown`) rather than staging something else. A research bench itself is the
Laboratory row: when the research ladder's next rung is locked only for lack
of a bench, `RoutineResearchPlanner` walks the same furnish-or-shell ladder
under `EnsureResearch` for a `SimpleResearchBench` (`routine-laboratory-*`
plans) and selects the rung once it stands (#254).

## Electrical safety

`EnsureBasicPower` uses `HiddenConduit` for new connections and replaces ordinary
conduits in bounded eight-cell methods. Turret connections use the same safe
definition. Native research, placement, stock and construction checks still apply;
unknown blockers, forbidden conduits and unfinished work cannot be replaced.
The topology census includes ordinary, hidden and waterproof conduits.

Native power facts identify rain-sensitive equipment and whether its whole
footprint is roofed. Exposed equipment and ordinary conduits keep power at
priority 2 even in dry weather. Where a free, observed perimeter exists, the power
planner builds a small enclosure around exposed equipment through shared Hands.
The door precedes the walls in one wave; ordinary colonist roofing has a bounded allowance,
and only an observed roof clears exposure. Blocked sites remain a deficit.
New rain-sensitive equipment requires a finished roof at placement admission.

These are separate hazards: roofing batteries and appliances prevents rain
shorts; roofing ordinary conduits does not prevent the random conduit incident.
See the wiki's [hidden conduit](https://rimworldwiki.com/wiki/Hidden_conduit) and
[battery](https://rimworldwiki.com/wiki/Battery) mechanics, and
[disaster recovery](../contracts/disaster-planning.md) for incident evidence.

## The matrix

| Role | Status | Hosts | Furniture |
| --- | --- | --- | --- |
| DiningRoom | implemented | RecRoom, Room | Table1x2c, DiningChair |
| RecRoom | implemented | DiningRoom, Room | HorseshoesPin |
| Hospital | implemented (hosted bed) | Bedroom, Barracks, Room | medical bed / sleeping spot |
| Workshop | implemented | Barracks, Room | the bench the recipe catalog names for the deficit |
| Laboratory | implemented | Workshop, Barracks, Room | SimpleResearchBench |
| ThroneRoom (Royalty) | implemented (own planned room) | | the title's throne definitions, then bedroom furnishing |
| Barn | implemented (own planned room, #1633) | | AnimalSleepingSpot; the vet room (a controller role, natively scored Barn) takes AnimalBed |
| Bedroom, Barracks, PrisonCell, PrisonBarracks, Storeroom, Kitchen, Tomb | pending | | |
| Nursery (Biotech) | implemented (own planned room, #1680) | | catalog role BabyBed |
| Playroom (Biotech) | implemented (own planned room, #1680) | | catalog roles Toy, Decoration |
| Classroom (Biotech) | implemented (own planned room, #1680) | | catalog roles Board, Desk |
| WorshipRoom (Ideology) | implemented (own planned room, #1658) | | the buildings the ideoligion requires, read from the game |
| DeathrestChamber (Biotech) | implemented (own planned room, #1690) | | catalog roles DeathrestCasket, DeathrestAccelerator |
| ContainmentCell, CeremonialChamber (Anomaly) | pending, content-gated | | |

### Child rooms (Biotech)

A baby, toddler or child is owed the room its developmental stage calls for
(`policy.ChildRoomNeeds`, from the pawn row's `biotech` block): a nursery
while a newborn or baby lives, a playroom while a baby or child does, a
classroom while a child does. The roles are the game's own room scores
([Rooms, Roles](https://rimworldwiki.com/wiki/Rooms)): a Nursery scores 0
under two baby beds or beside any other bed, a Playroom scores each toy box
and baby decoration and a Classroom each blackboard and school desk, both
only while the room holds no humanlike bed. Furniture counts follow the
pawns: a bed per newborn or baby (at least the game's two), one toy box and
one decoration, one blackboard and a desk per child.

Furniture is never a def-name list: the native definition catalog gives each
`PlanningDefinition` its `room_roles` (`BabyBed`, `Toy`, `Decoration`, `Board`,
`Desk`, `DeathrestCasket`, `DeathrestAccelerator`; see
[observations.md](../../../contracts/proto/observations.md#room-role-furniture))
and a room's pieces are the catalog definitions carrying the role, the first
available by name placed. The same machinery owes a **deathrest chamber**
while a deathrester lives: a casket per deathrester and, as an optional piece
(left out when no accelerator is available), the accelerators its deathrest
capacity beyond the casket allows.

1. The layout review grows a `nursery`, `playroom` or `classroom` core room
   sized to hold the furniture (`policy.ChildRoomSizes`,
   `policy.ReplanLayoutWithRooms`); existing rooms never move or shrink.
2. `NextChildRoomStep` shells the room, then places each piece at the first
   free slot of the child room template: bands of free floor as high as the
   piece, a free row between bands, the row inside the entrance and the
   entrance column kept free. Footprints are the native definition
   catalog's; a role whose required furniture is unavailable or has no known
   size is passed over, and any available definition of the role answers an
   unresearched one.
3. Feeding is `MaintainBabyFeeding` (below); play and lessons belong to the
   next children.

### Baby feeding (Biotech)

The game feeds babies itself: a lactating pawn breastfeeds any baby, and a
colonist on Childcare work (pinned for every pawn) bottle-feeds food a baby
can eat, which is baby food, milk or insect jelly
([Baby, Food](https://rimworldwiki.com/wiki/Baby); the def flag is
`IngestibleProperties.babiesCanIngest`, carried as `FoodProduct.baby_edible`).
The mother is Urgent and every other pawn Childcare by default. No write
beyond the existing production bill is owed. `MaintainBabyFeeding`
(priority 2, Food domain) is raised while babies live, no colonist can
breastfeed and the shared stock a baby eats (stocks whose eaters include a
baby) is under the game's low-baby-food alert level per baby
(`policy.BabyFoodAlertNutrition`, `Alert_LowBabyFood`). It places one
standing target-count bill for the baby-edible recipe (bulk first) through
`ProductionBillIntent`, sized to the babies' native nutrition per day over
the seasonal food target days, less other baby foods in stock.

### Polluting-machine siting (Biotech)

A building whose catalog row has `Pollutes` known true (a toxifier,
pollute-over-time or wastepack-producing comp, read natively) is sited by
`policy.PollutionSites`: it ranks the caller's legal candidate footprints by
distance only (no wind term; no sourced rule). Farthest from the nearest field
zone, bedroom, living-room or polluted cell first, then nearest the wastepack
disposal (atomizer) cells, then by cell order. The function is pure: it holds no
reservation and sets no threshold, so a goal (mech gestation, charging) takes the
first ranked site its native placement preview accepts. Unknown or off-map input
is an error, never a guess.

### Worship room (Ideology)

An ideoligion that requires buildings is owed one worship room holding one of
each. `Ideoligion.RequiredBuildings` names them (the ideology section:
building precepts' ThingDefs and the held rituals' required buildings, from
the game's defs; [ideology contracts](../contracts/ideology-contracts.md)),
and `policy.WorshipRoomNeed` turns them into a child room need
(`ChildRoomNeed`, one of each), so the room is grown, shelled and furnished
exactly like the child rooms above, on the same template. The names are the
ideoligion's and the footprints the definition catalog's: no def is listed
in Go. The review names the required buildings for the catalog and
remembers them for the planners' reads. Without Ideology or a primary
ideoligion, or while any required building is unavailable or has no known
size, no room is owed. The ideology read carries no room-quality
requirement, so none is staged.

### Throne room

A colonist who holds an Empire title, or has the favor to claim the next
one, is owed the throne room of the first title above its own whose
requirement names a throne and a minimum area (`RoyalRung.Throne*`, the
royalty read; the largest room any colonist is owed wins). The sleeping
planner raises it under MaintainHousing, like the tomb:

1. The layout review grows a `throne` core room of at least
   `throne_min_area` cells (`policy.ReplanLayoutWithRooms`); existing
   rooms never move, so a title that outgrows the room adds a larger one.
2. `NextThroneStep` shells the room, then places the title's throne at
   the template's back-wall slot. The throne's footprint is the native
   definition catalog's size, never a constant; an unavailable throne or an
   unknown size places nothing.
3. The standing room gets the title's minimum impressiveness as a room
   quality target (reason `title`), which the room upgrade and beauty
   upgrade fill with end table, dresser, lamp, plant pot and floor.
4. Once the throne stands and the holder holds a title, `NextThroneStep`
   reports `ThroneAssign` while the royalty read's `thrones` list shows the
   standing throne unowned (a throne built after the read waits for the next
   one; one owned by another colonist is left alone). The runtime sends the
   generic `assign` action (`AssignIntent`) with the holder's current throne
   as the expected previous assignment, once per holder and throne per goal
   epoch. The step holds MaintainHousing open until the read lists the holder
   as the throne's owner.

The royalty read reaches the projection through the optional
`RoyaltyNative` source (like `MapSurveyNative`); without it, or without
Royalty, no throne room is owed.

### Bestowing ceremony and title claim

The royalty read lists each bestowing-ceremony quest (`ceremonies`: quest id,
colonist, bestower, title bestowed, accepted, bestower waiting, started,
spot, attendees). While one is accepted:

- the throne room owed is the bestowed title's (`CeremonyThroneNeed`
  outranks the favor-driven need), so the room stands ready;
- the colonist and the lord's attendees are held off the Sleep timetable
  (`CeremonyHold`, `PlanSchedulesHeld`), which keeps them able to join the
  ritual; rest below the sleep band still sends them to bed.

An offered quest is how a title is claimed: when `NextTitleClaim` says claim
(favor, the next rung's bedroom and throne room met), the review lists the
quest in `RoutineFacts.TitleClaimQuests` and MaintainPopulation's
`SelectEmpireQuestMethod` accepts it through the existing QuestAccept (quest
identified by the read's quest id, not its script def). The ritual starts
only on the player's command (the bestower's Wait toil gizmo): once the
bestower waits and the ritual is known not to have started
(`CeremonyStart`), the same MaintainPopulation planner issues the generic
`Ritual` action (`bestowing`/`start`, #1639) and native runs the gizmo's
action. No draft avoidance beyond combat's own rules is applied (a raid
outranks a ceremony).

Content-gated rows are pursued only when their definitions exist in the
planning census. The table is the catalog's own rows; `go test ./internal/policy`
keeps it complete against the installed RoomRoleDefs.

## Acceptance

The `production/ladder` case (`acceptance run production/ladder`, `go/internal/nativeaccept/cases/production`) opens the
Core tribal baseline; `test/production_ladder_prepare` stages a roofed
starter hut with a sleeping spot per colonist (the room the workshop rung
furnishes, `scripts/fixtures/FixtureHut.cs`), a simple research bench inside
it with a fueled generator, steel, wood and the bench's 12 components loose
beside its door and Fabrication at 97%; the default component floor (10)
drives the ladder, and the case requires live native evidence for every rung:
Fabrication finished, a fabrication bench in a Workshop-hosting room carrying
the component bill, an allow-list stockpile for steel in that room, and the
stored component count above the pre-service baseline.

The `production/stone` case (`acceptance run production/stone`) runs the
same baseline under the default stone-block floor (150): the fixture
stages the same hut and research bench, the table's steel and Stonecutting at
97% through `test/production_stone_prepare`, and the audit requires Stonecutting
finished, a stonecutter's table in a Workshop-hosting room carrying the
derived stone's `Make_StoneBlocks` bill, and the live block count above the
pre-service baseline — the chunks are the map's own (#231).
