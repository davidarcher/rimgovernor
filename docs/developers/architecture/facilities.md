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
| Bedroom, Barracks, PrisonCell, PrisonBarracks, Storeroom, Kitchen, Tomb, Barn | pending | | |
| WorshipRoom (Ideology), Nursery, Playroom, Classroom, DeathrestChamber (Biotech), ContainmentCell, CeremonialChamber (Anomaly) | pending, content-gated | | |

### Throne room

A colonist who holds an Empire title, or has the favor to claim the next
one, is owed the throne room of the first title above its own whose
requirement names a throne and a minimum area (`RoyalRung.Throne*`, the
royalty read; the largest room any colonist is owed wins). The sleeping
planner raises it under MaintainHousing, like the tomb:

1. The layout review grows a `throne` core room of at least
   `throne_min_area` cells (`policy.ReplanLayoutWithThrone`); existing
   rooms never move, so a title that outgrows the room adds a larger one.
2. `NextThroneStep` shells the room, then places the title's throne at
   the template's back-wall slot. The throne's footprint is the native
   definition catalog's size, never a constant; an unavailable throne or an
   unknown size places nothing.
3. The standing room gets the title's minimum impressiveness as a room
   quality target (reason `title`), which the room upgrade and beauty
   upgrade fill with end table, dresser, lamp, plant pot and floor.

The royalty read reaches the projection through the optional
`RoyaltyNative` source (like `MapSurveyNative`); without it, or without
Royalty, no throne room is owed.

Assigning the throne to its holder is not implemented: it needs a
`ThroneAssign` action kind, which awaits a decision (#1601).
`NextThroneStep` reports `ThroneAssign` once the throne stands and the
runtime's `throneAssignmentSeam` receives it and does nothing, so the step
is never owed and MaintainHousing is not held open by it.

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
