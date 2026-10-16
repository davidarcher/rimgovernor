# Construction projects: one record per planned room (design proposal)

Status: built so far: the planned source for walls, doors and floors (`PlannedOwedCost`, the reconciler's owed operations over the plan, priced; merged into `ConstructionDemand` as `Planned`) room-id method names, and the tiler's per-room stations (`LayoutPlan.Pieces`, persisted, carried across replans for held rooms, priced by the same reconcile diff). The per-room requirements are also the forecast's planned-room commitments (`colony-forecast.md`, Planned rooms). Colony-driven contents are not priced (the plan stores no furniture: only lamps, which are not priced yet, and each owner derives its template when it furnishes) and the `RoomProject` record is still proposal. Decisions below were made with the maintainer. Written after tracing why wood demand needed a hard-coded
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

## Where a room's contents are decided today (trace)

The plan prices walls, doors and floors because they are functions of the room's geometry. Furniture is
not, and the reason is structural: the contents of a room are decided in four places that do not agree.

1. **The tiler places the stations, then drops them.** `Tiler.Furnish` (`LayoutTile.cs`) runs
   `PlaceAndSearch` for every room over the station group's wants (`Wants(i)`: def, count, interaction cell)
   and the interior search. The result is `Out.Pieces[room.Id]`, a full `InteriorPiece` list (def, cell,
   rotation). Its only readers are the conduit and lamp routing (`LayoutConduit.cs:379`, `LayoutLight.cs:504`)
   inside the same tile pass. Nothing persists it: `LayoutPlan` keeps the rooms, links, conduits and
   lamps.
2. **Role owners re-derive a template.** Bedroom, couple bedroom, shelter, throne, jail, tomb, hospital
   and the child rooms each call `InteriorRoomFromLayout` (role to `RoomRole`, door side, capabilities)
   and plan their own pieces (`BedroomTemplate`, the shelter bunks, `ThroneTemplate`...). They do not
   read the tiler's pieces for the same room.
3. **Benches are demand-driven and placed natively.** `RoundsWorkshop` follows the facility ladder
   (`facilities.md`): a bench kind is staged when throughput is short, one `workshop-<def>-<n>` method at a time,
   capped by `BenchCap`, and placed by the native placer with `PlacementEnvironment.Indoors`. The tiler's cell
   for that bench is not an input.
4. **Quality levers add pieces later** (`RoomQualityUpgrade`, `RoomQualityTitle`) from the standing room.

Consequences: the plan cannot say what a room will cost to furnish, the tiler's placement work is
discarded, and the benches the plan sized the room for and the benches the ladder eventually builds are
independent decisions that can differ.

### What this implies for the design

The plan should own "what goes in this room" for the pieces it already decides, and the owners should
consume it:

- **Persist the tiler's station pieces per room** (`LayoutPlan.Pieces: roomId -> [InteriorPiece]`, like
  `Lamps`). Free to produce: `Out.Pieces` already exists. It needs a persistence-table row and a codec.
- **Role owners read their room's pieces where the tiler placed them** instead of re-deriving, or keep their
  derivation but the plan stores its result. The sleeping-spot count, the bunk count and the title pieces depend
  on live colony facts (colonists, titles), so those stay owner-derived and are priced when they are
  derived. The plan's piece list is the stations; the colony-dependent pieces are an addition on top.
- **Pricing** sums the room's pieces by `CostListAdjusted` with the stuff the bulk chooser takes, the same
  catalog route as walls and floors. A piece already standing is subtracted.
- **Benches**: the ladder keeps deciding when a bench is built (it is demand-driven on purpose), but it
  should place at the plan's cell for that bench and count the plan's bench as the demand-side estimate.
  This is the largest behaviour change and should be its own step.

Open: the pieces depend on the door the tiler chose and on `Built`, so a held or re-sited room must carry
or re-derive its list; a replan re-tiles unbuilt rooms, so their lists are replaced with them.

### Contents changes as costed proposals (overlay)

A change to a room's contents is a **proposal**: `{ RoomId, Slot, Def, Cell, Rot, Cost, Benefit }`, an overlay on
the room's stored station pieces, never an edit of them. Proposals come from the owners that know why
(more sleepers, a quality gap, a title, a better bench becoming available) and are derived each review, so
they need no storage and cannot go stale.

Each proposal carries a cost (catalog pricing, the same route as walls and floors) and a benefit in the
terms the ranker already weighs (`PlanSupply`: value per labor and distance, scaled by `Priority`). The
colony then spends its budget on the best of all of them: a bench, a bed, a dresser, a wall.

Today this is the opposite. `NextRoomUpgrade` (`RoomQualityUpgrade.cs`) takes the first room by id, in a fixed
slot order (end table, dresser, lamp), only below the room's target, one piece at a time, with no cost or benefit
in sight. That is a hard-coded build order. The proposal form replaces it: the weakest-stat reasoning (space
weakest means a suite, not a table) becomes the benefit estimate, not a filter.

Open: how a proposal's benefit is judged. No common unit: the forecast (`ColonyForecast` Baseline against Proposed) judges outcomes, and mood and infrastructure effects are still unsupported domains there. Slot
identity stable across a re-tile (the overlay is keyed by room id and slot).

#### Judging a proposal: the forecast's interface, not a mood model

Mood and needs are the forecast's job (`colony-forecast.md`: pawn time, mood and needs are #3132, calibration
#3125; need relief is valued from one shared needs map with relief capped per need). This design does not model
mood. A content proposal is a **candidate activity** for the forecast: build the piece (its catalog cost and the
labor the construction slice already costs), with an outcome that relieves a need. The benefit is whatever the
forecast's needs model says; until #3132 supplies it the candidate carries an unknown benefit and is not
ranked against other actions on a guess.

Consequences: no break-day currency, no stat-to-stage tables here, and no second scenario type. Until the
forecast can value the outcome, `NextRoomUpgrade` keeps its current ordering; replacing it waits on #3132.
Open: slot identity stable across a re-tile.

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
- **Key by room id (built).** Shell, bedroom and `RoomName` methods carry the room id instead of the
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
- A candidate's value is `amount * (1 + UnitValue)`; demands carry no priority.
- Urgent work (`Urgent`) holds acquisition unless a demand the candidate serves is an emergency.

Remaining risk: wood is demanded as an absolute stock level, so a large plan keeps wood gathering justified
while storage has room. That is intended; it should be watched in a playtest, not capped in code.

## Open questions

- Demand for a room waiting on builders must still count, or nothing gathers for it. Demand is declared for the
  whole plan, so this holds by construction, but a test should say so.
- Which Round facts feed the urgency weight, and whether the weight is needed at all once the ranker has
  run on real whole-plan demand.

Planned rooms also expose candidate Method alternatives per room (stuff, shell/floor/stations scope), `PlannedRoomAlternatives`; see
[Planned room alternatives](colony-forecast.md#planned-room-alternatives). They are not yet outcomes the shared comparator can take; Rounds does not
generate or choose among them yet.
