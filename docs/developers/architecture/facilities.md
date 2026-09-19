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
   target (`policy.ResearchGoal`: an operator `--routine-research-target`
   still wins, and the default research ladder follows when no need is
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
   `MaintainResource`. Hauling then brings the inputs to the bench.
6. **Bill**: `RoutineResourcePlanner` dispatches the bill and native readback
   of the rising item count carries the deficit to recovery. The native
   preview admits a bill on an unfueled bench (`UsableForBillsAfterFueling`):
   a bill waiting on the bench is what makes haulers refuel it, and a bench
   with no bill leaves the clock with no work to run those hauls under.

Each rung reports an explicit reason when it cannot proceed
(`workshop_research_needed`, `workshop_bench_unavailable`, `no_space`,
`unknown`) rather than staging something else. A research bench itself is the
Laboratory row: when the research ladder's next rung is locked only for lack
of a bench, `RoutineResearchPlanner` walks the same furnish-or-shell ladder
under `EnsureResearch` for a `SimpleResearchBench` (`routine-laboratory-*`
plans) and selects the rung once it stands (#254).

## The matrix

| Role | Status | Hosts | Furniture |
| --- | --- | --- | --- |
| DiningRoom | implemented | RecRoom, Room | Table1x2c, DiningChair |
| RecRoom | implemented | DiningRoom, Room | HorseshoesPin |
| Hospital | implemented (hosted bed) | Bedroom, Barracks, Room | medical bed / sleeping spot |
| Workshop | implemented | Barracks, Room | the bench the recipe catalog names for the deficit |
| Laboratory | implemented | Workshop, Barracks, Room | SimpleResearchBench |
| Bedroom, Barracks, PrisonCell, PrisonBarracks, Storeroom, Kitchen, Tomb, Barn | pending | | |
| ThroneRoom (Royalty), WorshipRoom (Ideology), Nursery, Playroom, Classroom, DeathrestChamber (Biotech), ContainmentCell, CeremonialChamber (Anomaly) | pending, content-gated | | |

Content-gated rows are pursued only when their definitions exist in the
planning census. The table is the catalog's own rows; `go test ./internal/policy`
keeps it complete against the installed RoomRoleDefs.

## Acceptance

The `production/ladder` case (`acceptance run production/ladder`, `go/internal/nativeaccept/cases/production`) opens the
Core tribal baseline; `test/production_ladder_prepare` stages a roofed
starter hut with a sleeping spot per colonist (the room the workshop rung
furnishes, `scripts/fixtures/FixtureHut.cs`), a simple research bench inside
it, steel and wood beside its door and Smithing at 97%, and the case requires
live native evidence for every rung: Smithing finished, a smithy in a
Workshop-hosting room carrying the gladius bill, an allow-list stockpile for
steel in that room, and the gladius count above the pre-service baseline.

The `production/stone` case (`acceptance run production/stone`) runs the
same baseline with only `--routine-stone-block-target 40`: the fixture
stages the same hut and research bench, the table's steel and Stonecutting at
97% through `test/production_stone_prepare`, and the audit requires Stonecutting
finished, a stonecutter's table in a Workshop-hosting room carrying the
derived stone's `Make_StoneBlocks` bill, and the live block count above the
pre-service baseline — the chunks are the map's own (#231).
