# Construction projects: one record per planned room (design proposal)

Status: built so far: the planned source for walls, doors and floors (`PlannedShellDemand`, `PlannedFloorDemand`, merged into `ConstructionDemand` as `Planned`) and room-id method names. Furniture is not priced (the plan stores no furniture: only lamps, which are not priced yet, and each owner derives its template when it furnishes) and the `RoomProject` record is still proposal. Decisions below were made with the maintainer. Written after tracing why wood demand needed a hard-coded
floor (the wood latch, since removed) and why a replan could site a second shelter.

## What exists today

A planned room ends up represented three ways, and nothing joins them by project.

| Stage | Where it lives | Grain | Identity |
| --- | --- | --- | --- |
| Planned | `LayoutPlan.Rooms` (journal, `RecordLayoutPlanAsync`) | one `PlannedRoom` per room | `PlannedRoom.Id` (re-issued when a replan re-sites an unbuilt room) |
| Admitted | `DependencyRecord` in the Rounds review (`RoundsDependency.cs`) | one cost per action (`DependencyCost(ActionId, Count)`) | `(Need, Standard, Episode, Method, Plan, Resource)` |
| Owed | native census, `ConstructionDeficitRead` (`ReadConstructionDeficitsAsync`) | every blueprint and frame, summed per resource | none |

How a room becomes admitted work (`RoundsRoomReconcile.cs`):

1. An owning concern calls `ReconcileRoomAsync` for its `PlannedRoom`. The reconcile is a stateless diff of
   the plan against the world's walls, doors, floors and furniture, so "what is built" is re-read every pass.
2. The diff's operations commit as waves: removals first, then installs from packed stock, then the on-site
   builds (`RoomBuildKinds`: door, wall, floor, furniture, vent).
3. `CommitBuildsAsync` previews each ready piece natively and admits the accepted ones as one method:
   `"{role}-shell-room{id}-build-{hash of the piece list}"` (it was keyed by region coordinates until the room-id rename). Each piece is one action with id
   `"{plan}-{i}"`. The method name is keyed by role and region coordinates, not by `PlannedRoom.Id`.
4. A `DependencyRecord` is written only when the previewed cost of the wave exceeds the stock at that
   instant. It lives at most one game day, at most 64 records, and drops when the need's episode ends or
   every action settles.
5. Placed blueprints are not funded-gated: a blueprint holds no material until delivered and the native
   tier gate orders delivery. After placement the demand is only the Owed census.

`ConstructionDemand` merges Owed and Admitted per resource by maximum. Before a wave is admitted a room
contributes no demand at all.

## Consequences

- Demand is reactive. The colony learns it needs wood for a room only when that room's owner reconciles it
  and a wave previews short, so the supply plan starts gathering after the build is ready to place.
  A flat floor (the removed wood latch) was the only look-ahead.
- No per-project view. Nothing can say what a given room needs, how far along it is, or which room is next.
- Project identity is unstable. Method names used to follow the room's coordinates, so a re-site orphaned a room's history; they now carry `PlannedRoom.Id`. This is
  the same instability that let replans site a second shelter beside one under construction (fixed for held
  rooms in `1fc827a36`).
- Admitted and Owed overlap once a blueprint is placed; the per-resource maximum is what prevents double
  counting, not a shared key.

## Proposal

One derived record per layout room, keyed by `PlannedRoom.Id`, holding its pieces and a state:

```
RoomProject { RoomId, Role, State: Planned | Admitted | Placed | Built, Pieces: [Piece { Def, Cell, Cost, State }] }
```

Decisions:

- **Declare the whole plan.** Construction demand is the cost of every unbuilt piece of every project, by
  resource. It is not capped and not filtered by which concern is raised: a filter would be a hidden build
  order, and it would make the demand wrong for every other reader (trade would sell wood the plan still needs).
- **No hardcoded order.** Which project gets labor first is left to the supply ranker. Urgency, where
  it is wanted, is a weight on the demand's `Priority` (1 to 100, today a flat 1 for resource demands), taken
  from facts the Round already derives, such as an unmet owning concern. It weights; it never excludes.
- **Catalog pricing for unadmitted rooms.** Planned pieces are priced from the def catalog
  (`CostListAdjusted`) with the stuff the shell chooser would pick: pure, instant and whole-plan. The native
  preview stays the authority at admission, and Admitted and Owed take over once pieces are placed, so a
  pricing error corrects itself. A native preview per piece was rejected: one game-thread call per piece,
  hundreds per review for a whole plan, stale when it lands.
- **Key by room id (built).** Shell, bedroom, incinerator and `RoomName` methods carry the room id instead of the
  room's coordinates, and `Occupancy.RoomMethodIds` maps them to in-flight rooms. Dig, tomb and jail methods still
  carry coordinates.
- **No new persisted state.** Per `AGENTS.md`, state has one owner. The plan is the journal's, the world is
  native, the project record is derived in memory each review. The persistence table needs one line saying
  "RoomProject: derived, not stored".
- Admitted becomes a view of the project record. Owed stays as a cross-check against the world.

## Why a large demand is safe for the ranker

Checked in `SupplyPlan.PlanSupply`:

- Labor is one shared budget (work ticks per day); candidates are admitted in score order
  `Value / (1 + labor + distance...)` until it is spent, so a 40-room demand cannot over-commit labor.
- A stock demand's contribution per candidate is bounded by the remaining deficit and, for hauled yields,
  by storage headroom; the acquisition planner opens at most eight rows per pass.
- `Priority` already multiplies a candidate's value (`amount * Priority * (1 + UnitValue)`), and nutrition is
  100, so food outranks a flat-priority wood demand without any wood-specific rule.
- Higher-priority urgent work (`UrgentPriority`) holds acquisition outright.

Remaining risk: wood is demanded as an absolute stock level, so a large plan keeps wood gathering justified
while storage has room. That is intended; it should be watched in a playtest, not capped in code.

## Open questions

- The construction-labor admission check (`ConstructionLabor.cs`) holds builders while the shelter is owed.
  Demand for held rooms must still count, or nothing gathers for them. Because demand is declared for the
  whole plan this holds by construction, but a test should say so.
- Which Round facts feed the urgency weight, and whether the weight is needed at all once the ranker has
  run on real whole-plan demand.
