# Spatial contracts

[Documentation](../../README.md) · [Space and resources](../architecture/space-and-resources.md) (why planned geometry is rechecked)

These rules constrain shared plan admission and each later dispatch. They use native
observations; they do not reserve permanent ownership of coordinates.

## Shared admission

Shared spatial validation rejects overlapping planned room bounds, growing zones
inside planned rooms, and building footprints across reserved walkways or a room's
doorway and its immediate inside/outside approaches. Indoor stockpiles stay allowed.
Changing construction, zones or walkways refreshes native footprints for the live
plan, retained buildings included; unavailable geometry refuses admission. Hands
rechecks the current placement's footprint before writing, and again before the first
real write of each construction batch. These constraints protect accepted planned
space; they do not establish native reachability, arbitrary room connectivity, future
expansion rights or access throughout pawn construction. Cancelled unbuilt intent
releases its projected geometry; surviving objects remain part of native observations.

## Native zone and doorway checks

New room shells inspect the complete native zone census, rejecting enclosed farms even
when their cells do not touch the perimeter. Perimeter overlap, unknown zone kinds and
incomplete or conflicting zone geometry block construction. Exact three-cell native
reads require walkable, passable, unfogged approaches immediately inside and outside
the doorway. Hands refreshes these checks for every unissued shell placement,
preserving existing receipts on refusal. These reads do not predict future blueprint
obstruction or prove a route to colonists, and native input is not atomic with
controller validation.

## Bounded connectivity

Shell admission and each dispatch batch scan the interior and a three-cell exterior
margin, clipped to observed map bounds, with detailed reads split into at most 1024
cells each. Four-neighbor traversal requires usable interior cells to connect to the
inside approach, and the outside approach to reach the edge of that local margin
without crossing the proposed shell or another planned shell's walls. Unknown
interior geometry refuses construction; exterior unknowns cannot establish a route;
map edges are not exits. This is local topology validation, not native pawn
reachability. Changing planned shells revalidates retained shells too, so expansion
cannot seal the only observed local exit of a completed tracked room.

Furniture changes revalidate tracked shells using exact native footprints and
completed-definition passability. Impassable non-door buildings are projected as
obstructions; unknown classification refuses admission.

## Native pawn access

`Observations.ReadSpatialAccess` compares each mobile colonist's current safe,
unfogged, allowed-area four-neighbor component with projected building/terrain
obstruction. Every previously reachable cell outside the footprint must remain
reachable, including observed rooms whose actions are retired. Native door opening
eligibility and exact-target reachability remain separate checks. A pawn standing on a
proposed footprint needs a currently native-reachable exit; the read does not move it.
The one cap is 262144 map cells (over it the read is unavailable). Blocked cells,
targets and colonists are unbounded; the audit runs under the request's `budget_ms`
(Go: `bridge.SpatialAccessBudgetMS`, 2.5 s at about 70-80 ms per colonist), and a
colonist reached after it is spent comes back `PawnAccess.skipped` with position only.
The defense layout treats any skipped colonist as `field_unavailable spatial_access_skipped`,
never accepted and never refused for space.
Unsupported or unreadable evidence blocks admission. Future danger, door locking and
actual pawn labor remain simulation outcomes.

### Remote construction pickup

Under supervised play, `RemotePickupGuard` (a postfix on
`WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor`, frames and
blueprints) enlarges the finished `HaulToContainer` job; player-forced orders and
bill ingredients (whose placed things are consumed whole) are untouched. All
distances are straight-line `LengthHorizontal`, with no path or region search.

- A delivery is remote when the first stack is farther from the site (D) than
  from the nearest cell of storage that accepts it (zero when already stored,
  infinite when no storage accepts it). A remote job takes a full carry load
  (`MaxStackSpaceEver`) instead of the site's need; vanilla drops the surplus
  beside the frame, where it stays the nearest stack for the next delivery.
- A remote job also queues same-def stacks, nearest the first stack first. A
  stack at distance d is worth the detour when `d < n * D / capacity`, with n the
  items of that stack that fit in the remaining capacity, and `d <= D / 2`.
  Stacks vanilla already queued (5 cells) are kept.
- A local job keeps vanilla's exact-need pickup and 5-cell radius.

`wall/remote_pickup` covers the full-load trip, the radius, local behaviour and
that every item is counted once. The formula is `HaulBatching` (no Verse types).

## Room footprints

Wall-frame definitions are standable so thick perimeter sections can place and
fund all depths together. Under Auto, native completion projects the
finished wall as blocked: neighbouring unfinished walls need a reachable
standable touch cell, and reachable colonists must retain access to the builder's
component or an escape from the completing footprint. Completed-work frames
whose completion is refused are skipped until access is safe; delivery is never
ordered by wall depth. Normal pawn jobs supply the work and vanilla owns pawn
displacement at completion. `wall/layers` covers the native ring, completion veto
and pawn-on-frame outcome; Go tests cover section geometry.

In-place wall replacement (#2529) swaps a built wall's stuff, or its def for
another sharing the `Wall` replace tag, with the cell impassable at every tick.
A `BuildingIntent` with `replace_wall` skips the wipe in `Place` and goes through
`GenConstruct.PlaceBlueprintForBuild`: vanilla accepts the blueprint over the
built wall (`CanReplace`) and, on the first delivery, wipes the wall and spawns
the Frame in one synchronous call. The guard makes the standable wall Frame a
new-ring-only rule: at `Install` it registers one Impassable clone of each wall
Frame def (a Frame is saved by defName), and a `Blueprint_Build.MakeSolidThing`
prefix uses the clone when a built player wall occupies the cell. Completing a
clone is not vetoed, because it takes over a cell that was already impassable.
Native refuses with `NO_WALL_TO_REPLACE` (no built wall), `WALL_REPLACEMENT_REFUSED`
(vanilla refuses the blueprint, or the new def is not an impassable building) and
`WALL_REPLACEMENT_STRANDS_ROOF`: the wipe fires `Notify_RoofHolderDespawned` and a
Frame holds no roof, so the swap is allowed only where `RoofSupportSafety` finds
other holders for every roofed cell the wall supports. `wall/replace` covers it.

Every planned shell is an exact-cell `RoomFootprint`: a 4-connected interior of
1..3844 cells away from the map edge, a wall ring of every non-interior cell touching
the interior orthogonally or diagonally, and one door on the ring whose inward
neighbour is interior and whose outward neighbour is neither. The door is placed
first, then walls in north-facing, z-outer/x-inner order. The ring is one wave with
the door first in dispatch order; no wall waits for the door to complete.

A planned room's shell is built from its `Region` (`PlannedRoom.Footprint`), so any
4-connected cell set the rules above accept shells, not only rectangles. Its ring is
`Region.Walls()`; `GroundMatches` requires a wall on every ring cell (a door only on
the cells the plan holds one: its doors, links and airlock, and another room's link
standing orthogonally beside the region, never on a diagonal corner). `CensusRoomIn`
matches the enclosed census room that holds the region's anchor cell (the cell nearest
the bounding-box centre) and lies wholly inside the region, so a census room one cell
off or merged with a neighbour is rejected. The sparring ring, training range and the
other unfenced roles keep their rectangle blocks (`ringBlock`, `rangeBlock`).

Large irregular rooms and the cell-list actions. `BuildRoofAction` and
`RemoveRoofAction` take any non-empty deduplicated cell list with no count, shape or
span limit, and `ExcavationAction` and `DeconstructionAction` are per cell, so a
reshape of any size is a list of cells and is covered with no new action. The span
limit is the game's roof support, not the actions: an unpropped roof holds only within
`roof_max_support_distance` of a wall (`RoomFootprint.RoofSupported`, Euclidean, counted
from every ring cell, so the pillar a hole leaves extends the span). The footprint
constructor (`NewRoomFootprint`, reached through `PlannedRoom.Footprint`) does not
enforce it. The roof limit is enforced where the groups are sized: clustering caps a
group's area with `RoofSupported` within the bounds of 3844 interior cells and 255 ring
cells (`layout_cluster.go`). A roof that fails the check is not buildable without
columns, which the planner does not place.

`UnionFootprint` and `GrowFootprint` (`domain/room_footprint.go`) have no production
caller. A composite footprint is the union of one to eight interior rectangles
(`UnionFootprint`), deduplicated, 4-connected and roof-supported, with its south door
on the bounding box's entrance edge: the door's inward cell is interior with interior
on both sides and one cell deeper, so a door never lands in a notch or on a
connector's mouth; the nearest such cell to the centre line wins.

### Where a shell stands

No routine searches for a site. Every shell is a room of the layout plan, placed by the
tiler (see Layout planner below), and a routine raises the planned room of its role. The
shelter is the plan's `PlannedShelter` room: a station-less `RoomNode` sized by
`ShelterInteriorArea` (`layout_seed_rooms.go`), so the first plan, which is derived
before any station is built, already holds it. A shell is buildable when the native
preview accepts its cells; a planned room whose interior holds rock is dug first (see
Digging planned rooms). There are no starter templates or per-routine search tiers.

### Initial shelter rungs

`stepShelterRoom` (`rounds_shelter_bunks.go`) raises the shelter room. On a fresh site
the initial shelter runs three rungs under one Episode:

1. Sleeping spots at the first review, one per colonist owed, on the room's bunk slots
   (`shelterBunkSlots`, `policy.BunkCells`).
2. Wooden beds (`Bed`, north-facing 1x2; bedrolls in stocked fabric or leather while
   `Bed` is locked) on the same slots. The native refuses a bed over a standing spot, so
   the bed rung first deletes the standing spots (`shelter-clear-beds`; a bedroll being
   upgraded is packed to storage instead, never deleted), and places the beds once none
   stands. The rung waits while the spots are unbuilt or the deletion is open.
3. The ring around them, one `reconcileRoom` of the planned room with `ringOnly`.

Each rung is one plan (methods `shelter-spots`, `shelter-bedrolls`, `shelter-beds` and
`shelter-clear-*`), admitted one per review, and the ring methods are named
`shelter-shell-<x>-<z>` (`plannedRoomMethod`) with `-build-` waves. The bunk rungs and the
ring are admitted in the same review when each is admissible: the walls stand on cells
the bunks never take, so neither waits on the other, and an open ring wave is existing
work that does not stop the ladder. The bunks wait only for the room's rock to be dug.
Once the ring stands the ladder still goes up its rungs while the ring waits on its roof.

- A bed rung the native previews refuse whole falls through to the ring in the same
  review.
- Partial stock pays for the enclosure first: the bed rung admits only the beds the
  stock pays for, and the ring follows.
- A restart rereads the bound rungs from the journal (`shelterBunks`) rather than
  placing new bunks.

### Planned-room reconcile after a world change

Pausing and resuming control suspends every routine concern and reactivates it in the
same world with plans still open. A world change (load token, map, tick rewind)
invalidates the concern and cancels its plans, and the executor cancels the plan's native
blueprints and frames. The plan itself is the durable record: it is saved
(see [persistence](persistence-contracts.md)), and the standing walls and doors are read
from the census, so the per-cell diff of Layout reconcile (`policy.Reconcile`) reissues
exactly the cells not standing. There is no adoption pass, no earlier-shell
plan search and no repair-method chain.

Indoor furnishing treats the four orthogonal neighbours of every observed doorway (a
door, or a door blueprint or frame) as protected: the entrance aisle is never a
furniture candidate.

## Layout geometry

The layout plan is the only colony geometry: no colony grid, snapping or alignment
term, no district and no per-routine anchor. A routine reads the planned room of its role
from the plan.

### Layout planner

The plan is made by the station chain (`go/internal/policy/layout_*.go`; design in
[facilities](../architecture/facilities.md)): `StationCatalog` (the defs) and
`StationFacts` (the colony) give `StationGraph` (`BuildStationGraph`, or `SeedGraph` while
a fact is unread on a derive); `ClusterStations` groups it into rooms; `Embed` places the
groups and the power sites; `TileEmbedding` turns the embedding into the plan;
`AdviseStations` returns the `StationAdvice` (graph, groups, embedding, comfort, matched
rooms) that `TileStations` hands to the tiler. `CheckRoutes` proves the plan routes.
The tiler runs, in order: field zones, link doors (`chooseLinks`), exterior doors
(`chooseExteriors`), sealed-room joins (`joinSealed`), kept routes, vents (`chooseVents`),
the build, the furnishing with the interior search, `PlanRoofed`, airlocks, lamps and the
conduit routes (`routeTileConduits`). The plan holds Rooms, Links, Entrances, Owed, Trips,
OutsideTrips, Zones, Reservations, Sites, RetiredGround, Roofed, Lamps, Conduits, Cold
and Hot; `Spine` and `Wings` are leftovers of the old generator.

- **Tiling.** `policy.TileEmbedding` (`layout_tile.go`) turns the station embedding into
  a hallway-free plan; `TileStations` calls it after `AdviseStations`, and both
  `DeriveLayoutPlan` and `ReplanLayout` take its plan as the plan. Doors between rooms
  in a shared wall are the plan's typed
  door graph, `LayoutPlan.Links` (`RoomLink`: room ids `A < B` plus the wall cell,
  persisted in `family/layout_plan`); `PlannedRoom.Door` is the room's exterior door,
  and `Link` and `Doors` are leftovers of the old generator. On a plan with no
  hallways, `Entrances` are the outside
  cells beside an exterior door, and `CheckRoutes` walks the open ground between them.
  A room packed in with no door onto the ground is joined through a neighbour. Each
  tiled room carries `PlannedRoom.Crossing` (`RoomCrossing`), read from its station
  group, not its role: `Cold` (a cooled room: door temperature equalization leaks the
  cold), `Beds` (player-faction beds: clamor within 7 cells gives the sleeper the
  SleepDisturbed memory, -1 mood) and `Clean` (a station's effect scales with room
  cleanliness: a cook, a tending or operating bed, a research bench). A room that
  triggers none is walked freely; a cold room is never crossed; any other is not walked
  through unless the plan marks it `Through`, which the tiler does only when no link
  door can be dropped (`MayBeThrough`). A sealed room is joined through the neighbour
  triggering the fewest mechanisms (`throughRank`). `CheckRoutes` also demands every
  tiled room be reachable from an entrance without crossing a closed room. A room with no
  `Crossing` (a plan the old generator wrote, or a room a plan holds without a station
  group) keeps its role's `noThroughfare` rule and is not demanded reachable. Any other
  closed room in the way is `ErrTileClosed`. The tiled plan carries the group flows as
  `LayoutPlan.Trips` and scoring walks them (`tripWalk`); a plan without trips keeps the
  `routeTrips` role-pair table. Hallways are not planned by the tiler:
  they need ground between blocks that only the embedding can reserve. A group's area
  counts the exterior door and one door threshold per required link
  (`RoomGroup.AddRequiredDoors`); other link doors open, after the exterior and
  sealed-room doors, only where the placer still furnishes both rooms (a room it
  cannot furnish stands bare: `TileResult.Unfit`). The tiled plan's `Owed` lists one
  room role per station group, and the hard tier's Missing term counts a room per
  entry instead of per role (empty keeps `coreBaseRooms`).
  **Interior search (#2869).** When `TileInput.Graph` is set, `furnish` improves each
  room's greedy layout with `SearchInterior`: seeded from the room id and the embedding's
  siting seed (no wall time, default move budget), door candidates the room's chosen link
  and exterior doors (doors are inputs, not variables), flows `GraphFlows` over the station
  graph. A kept move is a `TileDecision` (`interior_search_move`, `Move` = slot:kind:terms
  improved), journalled as `layout_interior` by `deriveLayoutPlan`. The result is never worse
  than greedy on a gated term; the tiler has no built input, so nothing is pinned or moved free.
  **Live furnishing (#2956).** The runtime reads a room's furniture slots from
  `PlanRoomInterior` (`interior_region.go`). A role in `regionRoles` (workshop, laboratory,
  kitchen, tomb, prison cell, hospital, bedroom, suite, throne room, the child rooms, dining, rec room, shelter and barn: every role) states what its floor should hold as
  `RegionWant`s (`interior_region_roles.go`: the capacity its template had along the entrance
  wall or aisle, the pieces named as the reconcilers and consumers read them) and the placer
  seats them on `InteriorRoom.Floor()`, the planned room's region, so an L or notched room is
  furnished on its own cells rather than its bounding rectangle. `SearchInterior` then
  improves the layout, seeded from the role, floor and doors (no wall time). The live caller
  passes no station graph, so the search improves facility links and free floor only; tests
  give it the graph flows and hold the result to the template's count of every definition,
  its slot names and its gated terms. `InteriorTable` is the judgment table (the stove
  count). `InteriorRoom.Built` is what already stands (the closers pass `FurnitureRoom.Planning()`):
  the plan leaves those pieces where they stand and holds only what is missing. A want can
  sit `Beside` another (a chair at its table): the piece faces and touches a piece of that want,
  the objective counts it as a facility link (so the search keeps it), and only the pieces up
  to the want's minimum fall back to any free cell. `Open` seats a want where the most of its
  `Beside` pieces still fit, nearest the doors (a table with room for its chairs); `Clear` keeps a
  lane open in front of a piece (the pin's throw lane; a hard constraint). A role with no
  `regionRoles` entry has no interior; the `InteriorTemplate`s and `PlanInterior` remain
  only as the parity tests' baseline until #2957 deletes them. A barn's beds carry a
  synthetic front interaction (tended from the cell before them) and its heater is placed first.
  **Roofed set (#2941).** On a bad-climate map (the Cold or Hot latch, or a curve under
  `ColdMapBelowC` or over `HotEnter`) `PlanRoofed` (`layout_roof.go`) puts every
  room's floor and every priced hallway into `LayoutPlan.Roofed`, except the outdoor
  roles (`Valid` rejects a roofed cell on one); a temperate map's set is empty. There is
  no outer climate wall: the envelope is the roofed rooms and hallways. A hallway is
  roofed only when the watts its roof saves the doors that open onto it exceed the
  watts that hold it through its entrances (`climate.go`, in the `ColdEnter`..`HotEnter`
  band); a roof takes work, not materials. Cells farther than `RoofMaxSupportDistance`
  from a wall stay in the set and are reported (`roof_unsupported`), never vetoed.
  `TileResult.RoofDecisions` carries the reasons. The runtime builds the set through
  `MaintainClimateEnvelope` (Runtime, below).
  **Tiler airlocks (#2916).** On a roofed map each room's exterior door is priced in
  sustained watts as a door pawns use (`OpeningDoor`: open for the share of the time its
  trips hold it) against an airlock (`attachAirlock`; the second door and two walls at
  `BuildW`'s rate). The airlock is placed, as a child room with `Parent` set, when its
  total is lower; no veto, though a priced airlock with no free ground for its vestibule,
  walls and outer door is left out and the decision says so. Door traffic is derived from
  `LayoutPlan.Trips`: each trip is walked between its rooms' centres (`routeWalk`) and
  charges its movements a day to every exterior door on the way
  (`exteriorDoorMovements`). Trip weights stay one way; `exteriorDoorMovements` doubles
  them (a visit is a passage in and one out). The graph's `PurposeOutside` node
  (`OutsideNode`, no station, ignored by clustering) carries the colonist day cycle: one
  edge from the bed per healthy colonist a day (wake out, sleep back). Clustering splits
  it onto `RoomGroup.Outside`, the tiler onto `LayoutPlan.OutsideTrips`, and each such trip
  is walked from its room to the nearest entrance. A herd's pen is an outdoor `PenNode`
  (`StationGraph.Pens`, one per race of `StationPopulation.Herds`, the plan's ceilings;
  not a station, ignored by the embedder): its edges to the stores count as outside
  trips. Products haul to the store that keeps them at animals times `RaceProduct`
  amount over interval a day (milk, wool, eggs, chemfuel); feed hauls what the pen's
  grazing leaves of `AdultFeedPerDay` (`PenCells` over the weakest pasture, so a pen is
  hand-fed only past `penCellsCap` or on barren ground) in the richest feed item. Unread
  yields, hunger, pasture or feed add no flow (#2996). Joy is `StationGraph.Recreation`
  (`RecreationNode`, from `StationCatalog.Joy`, #2997): the awake fall of the Joy need
  (`joyGame`: Need_Joy's 0.0015 an interval over the schedule template's awake hours)
  over the chance-weighted mean session of the joy givers that need no building
  (`joyGainRate` x `joyDuration` x 0.36/2500), split by the givers the def flags outdoors
  (`unroofedOnly`, `requiresEnjoyOutdoors`) or `joyOutdoorGivers` (the walk). Each outdoor
  session is one trip from the bed to the outside node, merged into the day-cycle edge.
  It is a lower bound: no tolerance, weather, or building-backed givers (they wait on a
  rec room with stations in the graph, whose indoor sessions are `RecreationNode.Indoor`).
  Mined, chopped and hunted goods are hauls from the outside node to the store that
  holds them (`StationCatalog.Haul`, `layout_haul_nodes.go`, #2998): an owner works
  `CookTicksPerDay` of the day; a mineable tile costs `ceil(hit points / 80)` pick hits of
  100 ticks and gives `mineableYield` x `mineableDropChance` (x `MiningYield` when
  wasteable), a tree `harvestWork` ticks for `harvestYield` (`haulGame` reads the four
  stats at their default base); the defs of one item average. The woodcutters are the
  PlantCutting owners and the hunters the Hunting owners of `baselineDemand`; Mining has
  no baseline owner, so `haulGame.MinerOwners` (one) stands for it. An owner's ticks go in
  equal shares to the goods the stock demand holds, hauled as stacks (items over
  `StackLimit`; weights are loads a day, not items); a kill is one load of
  `HuntKillsPerHunterDay` per hunter to the cold store, only where the orders butcher. The
  output is the owners' capacity (an upper bound where the stock is met sooner) and leaves
  out skill, the walk to the rock, tree or prey, builders' and deep drills' ore and trade
  or loot; an unread yield, work or stack adds no flow. Each passage holds a door open for its window,
  from the def: the wait (`Building_Door.TicksToOpenNow`, 45 over `DoorOpenSpeed`), two
  cell moves (`climateTable.CellMoveTicks`) and the close delay (`ClimateModel.Door`). An
  airlock passage has an overlap of one close delay with both doors open (the vestibule
  passes 2/3 of one open door's heat per event) and otherwise one door open over the
  other's closed leak. Circulation rooms are priced like any room; the tiler plans no
  hallways. `TileDecision` carries the reason
  (`tile_airlock`, `_not_paid`, `_no_ground`, `_unpriced`, `_temperate`) and both
  totals. `airlockPasses` is deleted: the tiler prices every airlock.
  **Heaters and coolers in the comparison (#2950).** A room's price, for vents
  (`ventPricer.watts`) and for `ClimateOptions` alike, is its mean running watts plus the
  build cost of the heaters and coolers its load needs (`ClimateModel.PlantsW`): whole
  plants, as many as the peak heating or cooling draw (`ClimateWatts.HeatingPeakW`,
  `CoolingPeakW`) fills at the def's power draw, each at the cheapest heater's or best
  cooler's market value (`ClimateBuildValue.Heater`/`Cooler`, read from the defs) at
  `BuildW`'s rate. Both sides of a comparison carry it, so a vent that removes the cooler
  saves its steel and a room that still needs it pays the same on each. A plant whose cost
  cannot be read is zero: unpriced, no veto. `SiteTypeWeights.Power` is a running rate
  only; it holds no generation capital, and none is counted.
  **Runtime (#2962).** The reconcile library builds the climate plan through the same
owner path as a room's ring: `OpVentIn` builds each `NodeVent` site over its standing
wall (`ReconcileInput.VentAvailable` false, an unresearched `Vent`, makes it an absent
option; rock and a standing vent owe nothing; a vent row on a planned vent is no
furniture to remove), and `OpRoofIn` roofs the room's `Roofed` cells that the mirror reads
open to the sky (`ReconcileInput.Unroofed`), with its airlock children's vestibules, once
the ring stands. An airlock's walls and outer door are the parent ring's. An owner
reconciles only while its own predicate holds, so the plan-wide `MaintainClimateEnvelope`
(Shelter, family `envelope`, `RoundsEnvelopePlanner`) asks the plan itself: it diffs every
top-level planned room ring-only, keeps just its `OpVentIn` and `OpRoofIn`
(`policy.EnvelopeOps`) and commits them through the same `reconcileRoom` path with
`climateOnly`, whatever the owner is doing, and designates the roofed-set cells outside
every room (the hallways, `policy.EnvelopeHallwayRoof`) that the mirror reads open. It is
open while `RoundsFacts.EnvelopeOwed` is true and idle once every planned vent stands and
every roofed-set cell is roofed; an unread plan or construction census is unknown, never
idle. A hallway cell has no ring to wait for: its roof was priced to pay at plan time.
  **Lamps (#2945).** A roofed cell has no sky light, so a colonist working or walking on
  it loses speed (`StatPart_Glow`: x0.80 at glow 0 up to x1.00 at `LitGlow` 0.3, on
  `WorkSpeedGlobal` and `MoveSpeed`; `LightSpeed`). The tiler weighs that against lamps in
  sustained watts (`layout_light.go`), per roofed room, and places lamps where lighting
  costs less than the dark; never a veto. A colonist's work-day is priced at their own
  food upkeep a day (the food census over the colonists, in site-type units against the
  best crop's `cropRate`, turned into watts by `SiteTypeWeights.Power` as `BuildW` does):
  a derived lower bound with no new constant. A lamp costs its power draw plus its build
  value at `BuildW`'s rate (`StationCatalog.Light`, read by the bridge from
  `LightDefs().LampDef`). A work cell is a worker station's interaction cell, charged
  a whole colonist-day; a walked cell is charged the extra time its movements a day
  (`tripCellMovements`, the same walk as the airlocks) take at the dark speed. A standing
  lamp stands on a floor cell no station or worker holds; a lamp lights a cell within its
  reach (`glowReach`, below); the greedy search adds the lamp that lights the most dark
  cost, less its own cost, while that is positive.
  **Wall lamp (#2948, #2951).** The second option (`LightDefs().WallLampDef`,
  `LightModel.Wall`). From the defs and `Placeworker_AttachedToWall`: it is not built into
  a wall cell but stands on a floor cell with the wall in the cardinal direction it faces
  (`PlannedLamp.Facing`), is Standable with no path cost and is not an edifice, so it shares
  a cell with furniture, a work cell's standing spot included; the place worker refuses only
  a Full-fill building on its own cell (`Station.FillsCell`: fillPercent over 0.99, a
  cooler, a crematorium, a nutrient paste dispenser; a piece of unknown def is treated the
  same) or another attachment facing the same way. It needs a neighbour with
  `supportsWallAttachments`: a wall or natural rock, not a door, a vent or a fence. It costs
  15 steel to the standing lamp's 20, draws the same 30 W and glows to radius 11 against 12;
  the glow flood stops at walls, so it lights its own room. Per cell the tiler offers it
  where a neighbour is a plain wall (`plainWalls`: the wall ring of every fenced indoor
  room and the survey's rock, less floor, doors, links, entrances and the vents the tiler
  placed), for rooms (`roomCands`) and for roofed hallways (`hallwayLightW`, whose edge
  cells next to a room's ring take wall lamps, and elsewhere only standing lamps), and the
  search takes whichever lamp covers the dark for least. A held wall cell makes the wall
  lamp absent there and the standing lamp answers, so contention is priced by the
  substitute, never refused. A catalog without the wall lamp, its radius or its colour
  leaves it out; without the standing lamp's radius or colour the lamps are unpriced.
  **Reach (#2951).** `glowReach` solves the game's falloff for `LitGlow`
  (`ComputeGlowGridsJob.SetGlowFromDist`: the lamp's colour times a blend of `1-x/radius`
  and `1/x^2`, x being 1 plus the octile path distance, per channel truncated;
  `GlowGrid.GroundGlowAt`: brightest channel over 255 times 3.6, lit above 0.3). Both lamps
  glow (214,148,94), so the radius sets the reach: 9.0 cells along an axis from a standing
  lamp and 8.2 from a wall lamp (the wiki's "9 to 8 tiles"). The planner measures
  Euclidean distance, so `LightPrice.LampReach` and `WallReach` divide the path reach by
  the worst octile stretch of a diagonal (8.35 and 7.59 cells), keeping every cell inside lit.
  The earlier flat 6 cells and its 11/12 scaling undershot both; the infestation room-lamp
  planner (`BaseRoomLamps`) spaces its standing lamps by the same catalog reach. Constants: `LightTable` in `DefTables`.  A room under the sky (an outdoor role) gets none, and fields stay unroofed (the
  grow-lamp price is `PlanSiteType`'s). A roofed hallway's price in the roofed set
  (`RoofDecision.LightW`) includes the same lamps-or-dark for its walkers. An unknown
  price leaves lamps unplaced (`tile_lamps_unpriced`) or the hallway weighed without it
  (`LightUnpriced`). Not priced: the `EnvironmentDark` mood (deferred to #2878), surgery
  success, partial glow beyond the reach, and the lamps' power wiring (they are in
  `TileResult.Lamps`, not pieces, so the conduit net does not route to them yet).
  `MaintainLighting` is the runtime reconciler: its round builds the plan's `Lamps`
  (`policy/lighting.go`, `rounds_lighting.go`; see [upkeep](upkeep-contracts.md)).
  **Comfort and beauty (#2975).** Furniture buys mood through two seeker needs the game
  keeps as one shape (`Need_Seeker`: every 150 ticks the level moves toward an instant level
  at `seekerRisePerHour` x 0.06 up or `seekerFallPerHour` x 0.06 down, frozen while asleep
  if `freezeWhileSleeping`). `SeekerNeed` (`layout_comfort.go`) is that shape with the
  `NeedDef` curve (comfort 0.60 up, 0.04 down; beauty 0.32 up, 0.08 down, base 0.4, frozen
  asleep) and the thought's category thresholds (`SeekerGame`, in `DefTables`: comfort's are
  `level <` edge at 0.1/0.6/0.7/0.8/0.9, stages -3/+4/+6/+8/+10; beauty's `level >` edge at
  0.01/0.15/0.35/0.65/0.85/0.99, stages -15/-10/-5/none/+5/+10/+15). Comfort's instant level
  is the stat of the building the pawn stands on while it was used in the last 15 ticks, else
  0: a bed while lying (`Toils_LayDown`), any edifice while eating, a sittable building only
  while working a recipe or research (`chairsOnly`). Beauty's is 0.4 + 0.1 x the mean beauty
  of the cells its room shows within 8.9. `ChooseComfort` lays a colonist's day out from the
  schedule template (the 8 sleep hours on the bed, then the spots over the Anything hours,
  the rest idle), repeats it to its steady level, reads the time-averaged mood of the stages,
  and prices the difference with `PriceMood` against the piece's `BuildW`: the bed first
  against the planner's own, then each worked bench in graph order against the choices so
  far, so a bed that holds the level makes a work chair worth little. A bench's dwell is its
  node's `DailyWork` over the colonists at work speed 1; dining (needs the ingest time) and
  recreation (no model) are not dwelled yet, so the day is a lower bound of the level. Quality
  is the crafter's and priced at Normal; the end table and dresser's offsets are a sunk
  `BedOffset`. An unknown curve, mood model, work-day, steel value or colony mood
  (`StationFacts.ExpectedMood`) leaves the node unpriced at the current behaviour (the
  planner's bed, no chair); nothing is vetoed. Output: `StationAdvice.Comfort`, journaled in
  `layout_stations`. The furnisher does not read it yet (a work chair stands on the bench's
  interaction cell, which `PlaceInRegion` keeps open floor); beauty has the shape
  (`BeautyTarget`) but no decor candidate feeds it.
  **Circulation (#2911).** The station graph carries station-less room nodes
  (`StationGraph.Rooms`, `RoomNode`: role, area demand, optional strip width; the morgue
  and waste yard of #2882 reuse the kind). Circulation is the corridor that joins rooms
  nobody may walk through: when the private rooms (`RoomCrossing.Mechanisms() > 0`, not
  cold) outnumber the public ones, some hub would carry two bedroom doors, so clustering
  adds `PlannedCirculation` strips (`addCirculation`, `layout_circulation.go`), required
  beside every private room and the hubs they have flows to, sized from the short sides
  of the rooms joined plus a wall each over its two sides, and at most the longest strip
  the shell allows. Width is `CirculationWidth` = 1: in peacetime pawns neither block
  nor slow each other, so width buys nothing; wider lanes are the defense planner's
  (#2886). The tiler joins a sealed room to circulation first (`throughRank`) and opens
  no flow door from a room with beds to a non-circulation room while one stands; the
  corridor's group links carry no flow weight, so `Trips` are unchanged. Colonies whose
  private rooms do not outnumber the public ones get no corridor.
  **Outskirts rooms (#2882).** The tomb, morgue, graveyard, waste yard and incinerator
  are station-less room nodes of the same graph (`OutskirtsRoomNodes`,
  `layout_outskirts_nodes.go`): each asks the floor its slot gives and keeps its role's
  interior template, and clustering makes a `Planned` group of it. No distance band sits on
  the nodes (#2987): no minimum clear ground (once walls stand, line of sight to a corpse
  pile is blocked, so `CorpseSightRadius` is no standing rule) and no maximum reach. The
  only pressure on where a room stands is the hauling cost of the flows that reach it
  (`RoomNode.Flows`, priced in the embedding as any link is) and, for a room no flow
  reaches, the enclosure it adds to the base. The repo has no corpse or waste volume
  model, so these rooms carry no flow yet: they are unpriced by distance, never forced
  near or far and never guessed. An unpriced room (`embedder.unpriced`: no link of
  positive weight, so the incinerator beside the waste yard counts) is placed by the cell
  terms (soil, edge, centre) plus the marginal Wall and Footprint of the plan score
  (`enclosureCost`, `layout_embed_enclosure.go`, `planWeights`, no new constant): the base
  pays a wall ring for its size, so a room standing apart pays a ring of its own and one
  beside the rest shares theirs. The ring is read off rectangles (blocks grown by the
  killbox yard and `perimeterThick`, only on ground a wall can stand on) and ignores
  closing, hole filling and shoreline detours, which the plan's own score still prices.
  The morgue is cold, so it
  carries the cooler any cold group carries; the incinerator is required beside the waste
  yard. Wind is not a constraint: it has no game effect here. The tiler gives the
  unfenced graveyard and waste yard no wall, door or entrance. `OutskirtsArea` as a region, the
  siting reasons and the perimeter overlap checks are #2886.
  **Training rooms (#2887).** The range and the sparring ring are station-less nodes too
  (`TrainingRoomNodes`, `layout_training_nodes.go`; `StationGraphInput.TrainingStands`
  and `TrainingMarkers` ask for them, zero asks for none). Each asks its exact floor
  (stands by `RangeDistance+1`; `RingSide` squared) and clustering adds no door threshold
  to it (`exactFloor`); `rangeBlock` and `ringBlock` keep their rectangle shells. Both
  no distance band (#2987: the old `rangeGap` of two clear cells is a judgment no game
  rule derives, and `outskirtsReach` was the old siting window); the ring is required
  beside the range. Nothing behind the dummies is kept free: a practice shot is cast with
  non-target pawns excluded, so it cannot hit a pawn (#2938). The tiler writes `Facing`
  (the dummies point to the end farther from the bounds of the rooms that are part of the
  base, not the outskirts, training or shelter rooms: `apartFromBase`, `dummyFacing`) and
  `Markers` onto the room. Both are rooms of the tiled plan; the
  in-place widening (`widenRange`, `widenRing`) is deleted.
  **Power sites (#2919).** `AdviseStations` asks `EmbedSites` for a site node per
  planned turbine, solar plot and geothermal enclosure (`PowerSiteNodes`,
  `layout_power_nodes.go`); the sites land in the tiled plan and `PlannedPowerSites`,
  `TurbinePlacement` and `TurbineCatchZone` are the read API the power rounds use
  (`rounds_power.go`). `PlanUtilities` has no production caller. A turbine is a 7x2 footprint with four turns and a wind lane; a geothermal
  node is anchored on its enclosure; the battery room stays a planned room. The lane is
  priced only as the game prices it (`windGround`; constants in the `windPath` DefTables
  entry, read from `CompPowerPlantWind`): a path cell that is off the map, under thick
  roof, in a fenced room or under another footprint removes 20% of the output
  (`WindOutput`; zero at five). Crops do not block wind, so a lane over a field or over
  another lane is free; a turbine's own cells are not in its path. A tree is a blocker (`TreeBase`
  sets `blockWind`; crops and bushes do not).
  **How many (#2999).** A first plan owes the sites its planned demand needs, not a number:
  `StationFacts.powerSites` adds turbines and solar plots one at a time, re-quoting each at its
  cheapest site, until `ComputePowerBudget(...).GenerationShortfallW <= 0` against the demand
  (`Station.BaseW` over the groups, plus lamps from group area over the catalog lamp's reach) and
  the planned battery room's storage. Turbines come first while their price per delivered watt is
  least; solar fills the rest. Every site is priced in sustained watts (`SitePrice`): the output its
  lane loses, the conduit cells to the nearest room or footprint, the ring wall it adds, and the soil it
  covers (`unitsW`). There is no distance band. The generator, conduit and wall rows are
  `StationCatalog.Power` (`PowerModel`, built in `bridge.powerModel`); a missing row with demand
  above zero is `unknownStationFact("power supply")`, never a default. Replans keep the plan's
  sites and add none. The `layout_stations` row carries `sites` and `site_issues`.
  **Infestation ground (#2925).** The pocket walls are site nodes produced after the ring
  node (`PlanMountainPockets`), dropped and laid again with the ring. Each `NodePocketWall`
  is a walled-solid patch of at most 20 cells within `pocketReach` of the ring, with no
  room; it builds as a `TierPerimeterPrefix` section (`pocket-`), so the perimeter recut
  builds and removes it with the wall. `pocketReach` is a judgment constant
  (the game offers no signal for it). There is no bait room: the game picks an
  infestation cell by weighted random over every candidate, so a dark room does not steer
  it (#2944).

**Old generator (deleted, #2969).** `SiteCore`, the spine generator, the packer and the
core, kitchen, wing and suite files are gone. `LayoutPlan.Spine` and `Wings`, the doors
`PlannedRoom.Link` and `Doors` carry, and `PlanUtilities` (`layout_utility.go`, which
only works on a plan with a hallway and core zones, so it has no production caller) are
leftovers until #2971. The room sizes the generator kept in its tables are
`roomSizeTable` (`layout_room_sizes.go`, a `DefTables` entry). The ground the embedder
reads is `buildGround` (`layout_build_ground.go`: the core and rock cells, the soil costs
and the obstacle levels) and `layout_site_ground.go` (the siting seeds), that `Embed`
uses with `FieldGain`.

**Entry points and what a replan does.** `DeriveLayoutPlan` (`layout_derive.go`) sets
`StationFacts.Derive` and runs `TileStations`, then merges the field zones and plans the
herd and yard sites and the perimeter (`PlanHerdSites`, `PlanYardSites`,
`PlanMountainPockets(PlanPerimeter(...))`). `ReplanLayout` pins the rooms growth holds
(`TilePinned`, `ReplanTileFacts`) and runs `TileStations` again, so the next plan is the
tiled plan wholesale. It then puts back the held rooms the tiler left out (`keepHeld`),
retires the ground of a standing room that has no node (`retiredGround`), carries the
barns, pens and yards that still clear (`carriedSites`) and traces the perimeter around
what the defense record holds (`LayPerimeter`). What moves among built rooms is decided
inside the embedder by the switching price (`switchThreshold`, `layout_switch.go`:
`ReplanGain` plus `BuiltSwitch` and `BuiltSwitchCell` per cell of the room's area). A failed replan
(`ErrReplanEmpty`, `ErrStationFacts`) keeps the saved plan. The runtime derives when no
plan is saved and re-checks every `layoutReplanEvery` (2500 ticks) when the inputs key
changes (`reviewLayoutPlan`, `colony_plan.go`), journaling `layout_stations`,
`layout_plan` and `layout_replan_tiled` rows. [Facilities](../architecture/facilities.md)
holds the station-chain design.

### Layout reconcile

There is no separate tidy pass. A PlannedRoom is worked toward the plan by the room's owner
(build side) and by planned-ground clearance (clear side), both over one per-cell diff
(`policy.Reconcile`, `ReconcileRoom`): furniture off its template slot is packed and the
slot filled from packed stock or built on site, a wall of a lower-ranked stuff is swapped
in place, floors are replaced. Standing census rooms are reconciled too. The plan's
`RetiredGround` (a retired shelter's footprint) is cleared with the rest: its ring, spots
and furniture come down, a research table packed to stock for the laboratory to install,
and the entry drops once nothing of ours stands on it.

Reshape order: the diff runs over the room's `Region` (an L's notch is no ground, floors
lay on region cells only). A standing wall now inside the region (a column added) is owed
but not ready, and brings no roof off, until the new ring stands (no wall or door in
owed). A retired wall that still bounds an enclosed census room reaching outside the
retired ground (a row shrunk away) waits until the new ring has split that room off.
No new OpKind; the native ClearanceTargets request stays rectangles.

The shelter retires (`policy.ShelterRetirable`, re-read every review, no latch) when every
capability it hosts has a better home: every colonist owns a bed in a built bedroom, a
planned workshop stands, a research bench stands in a built room outside the shelter (or a
planned laboratory stands to take the packed table; research is a capability of whichever
room holds the bench, so a solo colony needs no laboratory) and, on a cold map, those
bedrooms hold temperature. Unknown facts never retire it, and a colony with nothing else
built keeps the shelter as its permanent home.

### Stockpile maintenance

`MaintainStockpiles` (family `stockpiles`) applies the stores the departments declare
(`policy.DeclareStores`; see [storage](../architecture/storage.md)) against the colony's
own zones (`store.OwnedZone`, role-keyed) every review cycle, at any tier. A store is one
zone over its declared site; the edits are:

| Edit | Rule |
| --- | --- |
| create | a declared store has no zone and its room interior is open ground; admitted alone, once no other edit stands |
| patch | the store's desired filter or priority differs from the last applied (`store.StockpilePatches`) |
| grow | a whole-footprint store has reachable cleared cells within its site; add them to the first owned zone by native ID |
| delete | the department declares the store `Retired` (its purpose is gone); a moved store's new zone is admitted first |
| consolidate | after growth settles, delete compatible adjacent owned fragments; add their freed cells to the survivor next review |
| shrink | a zone holds cells outside every serving site's room region, on an `Avoid` cell, or under furniture (a non-shelf building, blueprint or frame); remove exactly those cells (`zone_cell_edit` remove). Stock there drains by vanilla hauling: no haul action. A zone left with no cell is moved or deleted instead |

A store's zone follows its room's `Region`. A grown room is filled by `grow`; a replaced
room (a successor with `Replaces`) re-points the zone: the new zone is created over the
region cell for cell (`domain.NewFilteredStockpileCellsZone`, one zone per connected part,
furniture cells left out) with the declared filter and priority, and the old zone is
deleted only once it stands, matched by position. Zones are soft for furniture: a zone
never claims a furnished cell.

Geometry maintenance preserves blockers, unknown ground, exclusions, unrelated
zones and separate store sites. Fixed-size stores retain their requested size.
A store whose zones are 85% used asks layout for a further room. A role-less legacy claim
stands as created. A source answers nothing while the fact it judges by is unknown.

`RoundsStockpilePlanner` commits the edits as one plan per cycle, each action under
its target's fresh CAS token: `stockpile_patch` (zone or shelf), `zone_cell_edit` or `zone_delete`; a
create is a native zone preview and a `zone_create` method. Each is a
`layout`/`stockpiles` clock event.

**Waste dump** (`wastedump`) is the Sanitation department's declared store: one Low zone over
the waste yard interior outside the incinerator outline, allowing all storable items except
the native not-burnable special (`domain.DumpFilter`). Warehouse gear (apparel or weapons in a
`general` zone) below the gear hit-point or quality floor sells to traders (`policy.SaleGear`);
unknown hit points or quality is not sale gear.

**Gear rooms.** Gear is stored in layout's armory and wardrobe rooms; until a room stands,
gear stays in the warehouse. The armory leaves out cells within six of a prison, and layout
keeps both rooms out of each other's clearance.

### Spatial program

Where a room stands is the layout plan's (see Layout planner above); the runtime does
not compare candidate sites. The persisted spatial program describes habitable shelter,
food services and maintained capacity using existing functional concerns and fresh
native gates. Population growth reopens shelter capacity. Routine storage development
waits for observed indoor sleeping capacity; urgent cooking, food acquisition, medical
care and temperature control keep their priorities. Future phases own no cells or game
orders.

### Digging planned rooms

A planned room whose footprint holds natural rock is dug before it is walled.
`plan.RoomRock(room, cells).Dig` lists the rock cells of the interior and door
(`layout_mining.go`), and `digPlannedRoom` (`rounds_plan_dig.go`) admits them through the
shared `admitRockStep` as the method `plan-dig-room-<role>-<x>-<z>`, reached from
outside the room's door. A building that needs open sky also has its roof removed
(`digPlannedSky`). Each wave designates only the cells with an open walkable cardinal
neighbour (`exposedFirst`), because a pawn cannot step between two diagonal gaps; up to
64 waves (`digWaves`) mine the rock each wave exposed. A dig that settles with rock
still standing refuses with `rock_not_dug` and the layout never repositions: the tiler,
not the dig, chose the place. The ring is raised beside an open room dig, so a dig does
not hold the shell planner (`isRoomDigMethod`).

### Staged excavation (ore corridors)

The staged machinery is now corridor-only. `policy.CorridorExcavationSites`
(`tunnel_site.go`) proposes a one-wide corridor from a known walkable access cell into a
visible rock face toward an ore cell, and `rounds_resource_tunnel.go` drives it; the
code states that dug rooms are the layout plan's and this only reaches ore. The
shelter is not a staged excavation. The rules that still hold:

- Fogged cells are unknown, never assumed empty or safe, and nothing outside the
  observed planning window is planned. Native `ReadExcavationSite` is the authority for
  roof support and for a miner able to reach the access cell.
- Work proceeds in stages of at most eight cells (`excavationStageLimit`), each admitted
  only after a fresh native site read reports the stage supported. The next stage waits
  for the previous stage's observed completion and re-reads the geometry.
- The target key rides on the stage method id (`excavation-stage-<n>@<key>`,
  `ExcavationMethod` parses it), so an in-progress project is recovered from the journal
  alone.
- A stage action held not ready, unsupported or on changed geometry for longer than
  `excavationStallTicks` (one hour) is cancelled so the review runs against the geometry
  that changed under it. A cleared cell is never re-designated.

## Zone and facing postconditions

New zone project targets retain expected patches, kind, crop and native settings.
Growing zones retain sow/cut permissions; stockpiles retain priority and exact
ThingFilter definition/special-filter allowances and condition, quality and
mental-break ranges. Stockpile expectations come from native previews, including
presets; truncated display samples cannot establish the contract. Matching labels with
conflicting settings require explicit editing. Fresh complete native list/grid
geometry and settings must match before an action can satisfy dependent work.

Building targets retain facing through blueprints, frames and completed buildings.
Native Rot4 integers are invariant across languages; recognized cardinal labels are
accepted, and unavailable facing holds verification.

Native edits invalidate completion. An explicit retry of an invalidated project
requires fresh exact native restoration before preserving its confirmed slots and
resuming dependencies. Unknown observations can recover through reads without replay.
Geometry/facing expectations are recovered only from a unique durable action owning
the exact project targets; current map state cannot invent missing historical
settings.

## Clearance observations

`Observations.GetClearanceTargets` is a read-only census of spawned, visible,
deconstructible non-player buildings whose occupied rectangle touches Home. Each row
records the complete occupied rectangle, nullable faction, classification, whether
every occupied cell is in Home, sealed ancient-danger membership, and the current
deconstruct designation with controller ownership.

Haulable rock chunks and slag are items, never targets. The same read lists them as
`chunks` (every chunk-category stack on a Home cell, bounded to 256).

A missing roof blocker means removing this building alone preserves roof support;
blocked or unknown geometry carries a reason. It does not authorize removal, prove
access, or establish safety for removing several holders together. Ancient-danger
rows stay visible as blocked candidates (sealed ancient-temple warning regions and
enclosed roofed rooms with occupied caskets or fogged hives/hostile pawns, including
the enclosing walls). For a roof-holding building the counterfactual excludes every
occupied cell and seeds connected-roof searches from the union of the installed
support-radius neighborhoods of those cells. Non-holders cannot remove support. Fog,
unknown map-edge geometry or a pending collapse blocks removal. The pure geometry
probe runs under `task probes:test`.

The scan walks the Home cells rather than the map's building list, with no row cap.
An unreadable scan is unavailable, never sampled; Go treats an explicit unavailable
native stub as an unknown fact, distinct from a complete empty census. Required
safety booleans cannot be omitted. The observation context must match the expected
load, map, generation and fresh tick. The read admits nothing itself;
`ClearHomeObstructions` consumes it ([upkeep contracts](upkeep-contracts.md)).

## Ancient shrine observations

`Observations.GetAncientShrines` is a read-only census of ancient-danger rooms, one
row per shrine rather than per flagged wall. A row carries the room rectangle,
`sealed` (interior fogged, no open roof, not on the map edge), whether any room cell
is in Home, every cryptosleep casket with its hit points, `has_contents` and player
claim, the hostile pawns and hives inside once the interior is unfogged
(`guards_known`; a sealed shrine never reports guards), and the perimeter walls the
player may deconstruct without a roof-support blocker. For a visible neutral wall on
a sealed shrine, roof structure is inspected inside that bounded room and its border
despite fog; unsupported roofs, pending collapse and fog outside that footprint still
block. This check reveals no occupants and never unfogs the room.

The occupant of a filled casket is unknown until it opens; a casket under 20% hit
points explodes, so hit points are a safety reading. The census has no
count caps on shrines, caskets, guards or breach walls; an
unreadable scan is unavailable, never sampled. Nothing in this read admits a breach,
a casket order or a claim: readiness, the breach concern and casket
handling decide.

Optional heat facts (visible roof-connected interior,
independent of building ownership: measured temperatures, enclosure, colonist
presence, one repairable door site, heater sites and existing heaters, doorway/retreat
cells) are omitted on ambiguous geometry or fog; unknown never means heat-ready.

## Defense tier counts

The turret, mortar and approach-IED tiers have no fixed ceiling. Each count is
the smallest of four Go gates, and the gate that stops a tier short is named:

| Tier | Threat demand (raid points) | Power | Stock | Geometry |
|---|---|---|---|---|
| Turrets | `TurretBudget`: 2, plus 2 per full 300 points; unknown, NaN or infinite keeps 2 | spare watts cover every turret's draw | steel and components for the turrets, then again with routed conduits | verified, spaced flank slots (`defenseMaxWidth` per side, two rows) |
| Mortars | `MortarBudget`: none below 800 points, then 1, plus 1 per 1700 more | none (unpowered) | `stockCovers` the summed mortar cost | unroofed, spaced cells behind the line (`mortarBehindMin`..`Max`) |
| IEDs | `IEDBudget`: 2, plus 1 per full 300 points | none | `IEDStock` covers the summed IED cost | free approach cells every `defenseIEDSpacing` out to `defenseIEDReach` |

Kept constants are geometry, not caps: `defenseMaxWidth`, `defenseMaxDefenders`,
`turretSpacing`, `conduitReach`, `defenseIEDReach`, `defenseIEDSpacing`,
`mortarBehindMin`/`mortarBehindMax` (the native placement and blast rules they
mirror). Turret candidates are every slot with a line of sight to the lane, bounded by that
geometry. Native still reports its own placement refusals, handled as ordinary
refusals (`placement_preview_refused`).

The defense census is built in Go from the cell mirror and the definition catalog
(`bridge.DefenseSiteFromCells`), not read from the game: the layout round makes no
`observations_read_defense_site` call. Per cell it takes the edifice (a frame is its own
`Frame_<def>`, a blueprint is no edifice), cover as the game's `CoverGrid` keeps it (the
thing of highest `fillPercent` above 0.001; a frame 0.2, a blueprint none), the cover
thing's clearance kind (`NativeClearCover.KindOf`: plant, mineable, chunk by thing
category, building), `Passable` from the impassable flag, a door from the door edifice,
`Roofed`, the `foundation_removal` mirror column, and `EdgeReachable` as a four-connected
flood over walkable cells from the map edge with doors read closed. A cell the
mirror does not hold, or holds without terrain or walkability, is fogged; the flood
treats it as passable, because the game's pathing has no fog (raiders path the real map)
and nothing is built within 10 tiles of the map edge, so treating fog as blocked would
call a base sealed that raiders can enter. A fogged cell's own `EdgeReachable` is not
asserted (read unknown). The edifice
stuff is not in the mirror, so the waiting fight's plasteel-door hardening still reads
the native census.

Line of sight is computed in Go over the defense census (`policy/defense_sight.go`);
the defense layout round makes no `observations_read_lines_of_fire` call. It is
`GenSight.LineOfSight(start, end, map, skipFirstCell: true)`: the game's integer
walk, which skips the shooter's cell and stops short of the target's, blocked by any
walked cell whose edifice has fill above 0.99 (`GenGrid.CanBeSeenOver`; the census
cover fill is the largest fill on the cell). A door reads closed. A fogged endpoint,
or an unknown cell on an otherwise clear walk, is unknown and the position is not
placed. The rule is checked against the native answers recorded with the defense
layout snapshots (`TestDefenseLineOfSightMatchesRecordedNativeAnswers`).

A gate that cuts a tier journals `defense_action` / `refused` with the gate as
`reason` (`turret_unavailable`, `turret_threat_budget`, `turret_power`,
`turret_stock`, `turret_no_conduit_route`, `mortar_unavailable`,
`mortar_threat_budget`, `mortar_stock`, `ied_threat_budget`, `ied_stock`) and the
tier as `target`. `policy.DefenseTier.Gated` carries it; `DefenseLayout.Gated`
also names an IED or turret tier a gate emptied. Count scaling and each gate are
proven by Go snapshot tests (`defense_budget_test.go`, `defense_gates_test.go`).

## Defense approach demand

`policy.DefenseLayout.Approaches` groups observed, Home-connected boundary cells into
contiguous sectors on each side of the bounded defense census. The flood starts at
Home, or at the layout's Entry when Home itself is not a passable cell.
`EdgeReachable` proves a connection to some map edge; it does not identify which edge
a distant raid used. Arrival inputs therefore carry a distinct raid ID, its observed
local boundary crossing and its arrival tick. Duplicate observations count once,
arrivals older than three game days expire, and unmatched crossings remain explicit.
Sectors rank by recent raid count, then shortest observed distance to Home, with
deterministic coordinate ties. A turret's `LastAttackTargetTick` is firing evidence,
not a raid identity or arrival location, and cannot by itself increment a count.

Each sector has an observed passable route to Entry with proposed funnel walls
closed. Reachability to firing positions with both entry lanes closed reports
`route_bypasses_entry`; this is a layout finding, not clearance demand.

Cover demand uses the observed native cover fill threshold, strictly exceeded, within
the shortest defender range ahead of Entry or beside the last six route cells. The
game grants a block chance to any positive fill, so the native census reports a
threshold of zero: a tree (0.25) or a chunk (0.5) is cover as much as a rock wall.
Unknown range or threshold holds selection. Accepted footprints, firing positions,
both lanes and existing rock supporting their flanks are protected. Map-edge rock,
mountain interiors and rock faces carry concrete holds; only free-standing rock is
mined. Demand within a sector ranks nearest Entry first. Ranking cover demand does
not change geometry.

Each cell whose fill comes from a thing the game's own designators could remove names
it (`cover_thing_id`, `cover_def_name`, `cover_kind` plant/chunk/mineable/building,
`cover_designated`) with a CAS token over identity, definition, cell and designation
state. `RaidArrivalState`, a map component, samples every hostile lord every 60 ticks:
its first pawn position is the spawn (ground when within 14 cells of the map edge) and
the pawn nearest the home area adds one trail cell per sample; a live lord's trail is
never truncated, and a track not sampled for 60000 ticks (one game day) is evicted. The colony threat census's `raids` rows (`ThreatFacts.raids`) carry those tracks; the controller
takes the first trail cell inside its census region as the crossing, and the policy
snaps it to the nearest sector edge cell within eight cells. Drop pods and tunnellers
are not ground arrivals.

Clearance is the `cover_clearance` action (a Designate on the thing; `Mine` under
`mine_safety`, `Deconstruct` under `enclosure`): one exact thing by identity and cell
with the designation its kind takes (`CutPlant`, `Haul`, `Mine`, and `Deconstruct` on
an unowned building such as a ruin wall; player-owned cover is held as `structure`).
Native re-checks presence, cell, fog, fill, forbiddance, an existing designation, roof
and mining safety, a store for a chunk, the designator's own acceptance (its refusal
text is surfaced) and a reachable free colonist with the work type; the same
designation already standing applies again. `CutPlant` on a harvestable tree
designates `HarvestPlant` (chop wood). Applied is terminal; ordinary work removes the
thing, and the census skips designated cover. The defense layout planner orders up to
eight clearances per method once every tier stands; native refusals of the cover write follow the shared refusal budget (no per-day method bound).

## Perimeter routing cut (#2973)

`policy.PlanPerimeterCut` (`perimeter_cut.go`) advises the perimeter as a routing cut
beside the ring `PlanPerimeter` traces. The review only logs it (`logPerimeterCut`,
`colony_plan_cut.go`); nothing reads it, and the plan's ring is still `PlanPerimeter` on a
derive and `LayPerimeter` on a replan. The wall is about routing: the cheapest set of wall cells such
that every walk from a map edge, or from the opening's lane, to the core enclosure
crosses a wall or passes the killbox opening. Only walls are barriers (not sandbags, not
traps). Terrain a pawn cannot cross (rock, hazard, unwalkable ground) is a free barrier,
so the cut uses it before it spends a wall cell. The cut is a minimum vertex cut by
maximum flow over the four-connected walkable grid (a diagonal chain of walls is sealed:
`PathFinderJob` refuses a diagonal step past a blocked orthogonal neighbour); of the
cuts of least cost it takes the one nearest the base. A cell is priced one wall (two on
a bridgeable stretch); ground nothing can wall is priced 1000, not vetoed, and reported
`cut_unclosable`. Ordinary raiders path ByPawn, where a wall cell is impassable, so an
unbroken one-cell cut already funnels them to the opening.

Thickness is designed against sappers (`JobGiver_AISapper` prices a destroyable wall cell
at `Cost_BlockedWallBase` 70 plus 0.2 per hit point, plus the 13-tick step): breaching
costs linearly in thickness, so a stretch whose detour to the opening is D ticks (the
outside walk from the cut cell to the opening's mouth, 13 ticks a cell) is
`N = ceil(D / (70 + 0.2*HP + 13))` thick, at least one. HP is the wall's `MaxHitPoints`
for the stuff the colony would build it from (the wood wall on light footing and plain
bridges). A stretch no outside walk joins to the opening is one thick: an entry cell must
reach the colony, so no raider arrives there. Unknown HP leaves one cell. The one judgment
is `perimeterTable.DetourBudgetTicks` (a `DefTables` entry): detours past it are designed
for the budget, so a far stretch is not thickened without limit. Breachers (`BreachingGrid`
prices a wall at 20 + 0.015 per hit point) barely feel thickness: the killbox and turrets
answer them. The raid generator rolls `ImmediateAttackSappers` (selection weight 0 below
700 raid points, 0.4 at 1000) for the factions with a `canBeSapper` pawn kind (pirates,
mercenaries, tribes, Empire, wasters), so early raids walk and later ones may tunnel.

The cut is deterministic: the flow network is built in a fixed order and nothing is
random. The decisions are `layout_plan` rows with target `perimeter_cut` (see
`otel-signals.md`).

## Defense tier nodes (#2973)

`policy.PlanDefenseTiers(plan, census, pins)` (`defense_tiers.go`) cuts the plan's killbox
node into the tiers that stand in it, as `PlannedSite` nodes whose `Tier` facts name the
killbox node they stand under, the defense-record tier they belong to and the placements
they hold: corridor and trap lane (the raiders' snake, in order), funnel walls, fence
bar, spike traps, firing line (cover, with the shooters and their retreat cells) and
shooter floor, turrets, mortars, IEDs and bait. They show in the layout overlay
(`LayoutPlan.WithDefenseTiers` merges them for drawing).

Where a tier stands depends on live cover, trees and standing buildings, so the nodes are
a function of the plan and a census, not of the survey: they are re-sited from each
census and are not stored with the plan. The census (`DefenseRequest`: the cell mirror
window, defenders, definitions and the turret, mortar and IED gates) is an explicit input;
its Killbox, Home and Region are the plan's. A started tier (built, an attempt admitted, a lost building re-opening it, or any building standing) is pinned, the way a replan holds a
room that holds anything of ours (`keepHeld`): its node is the built tier where it stands, the census
reads its standing buildings as the ground under them so the same census sites the same
cells, and the tiers still to build are sited around it (a building of theirs on a pinned
cell is dropped, `tier_displaced_by_built`). A census the layout planner refuses leaves the
pinned tiers as the nodes (`tier_layout_failed`). The defense round reads the nodes:
`RoundsDefenseLayoutPlanner.siteTiers` cuts them after each tier census, once per reverify
interval, sets the record's unstarted killbox tiers (and the trap lane and firing line they
define) to what the nodes site, holds the nodes for the overlay and journals them as
`layout_plan` rows with target `defense_tiers`. The turret and mortar proposals are gone:
a tier whose gates open later joins the record through the same siting.

### A blocked tier

When a tree, a rock chunk or a new building stands on a cell a pinned tier needs
(`DefenseCell.Cover`), `policy.PriceTierBlocks` (`defense_block.go`) prices both ways out
in the layout's units and takes the cheaper; neither is a veto, and a tie keeps the tier.
Clear is the work from the game's defs (a plant's `harvestWork`, a building's
`WorkToBuild` clamped to `JobDriver_Deconstruct`'s limits; `DefinitionCatalog.ClearRow`)
over the work ticks of a colonist's work-day (the available colonists' timetable work hours
at their work speed, `ColonistWorkTicksPerDay`), charged at the #2945 work-day price
(`StationWorkPrice`), net of the yield (a felled tree's wood, a deconstruction's refund). A
hauled chunk costs the walk of one cell, the lower bound of a haul; rock needs mining,
whose work the defs do not state, so it is unpriced. Move is the build cost of the tier at
the place the census sites it with nothing pinned plus the deconstruct work of the old
tier's standing buildings. The downtime, the days the old tier no longer covers the lane,
is not priced, so a move's price is a lower bound and a move taken says so in its reason.
Journaled as `layout_plan` rows with target `defense_tier_block`.
