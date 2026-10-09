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
Bounds: 262144 map cells, 32 mobile colonists, 16384 projected cells, 128 targets.
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

Every planned shell is an exact-cell `RoomFootprint`: a 4-connected interior of
1..3844 cells away from the map edge, a wall ring of every non-interior cell touching
the interior orthogonally or diagonally, and one door on the ring whose inward
neighbour is interior and whose outward neighbour is neither. The door is placed
first, then walls in north-facing, z-outer/x-inner order. The ring is one wave with
the door first in dispatch order; no wall waits for the door to complete.

A composite footprint is the union of one to eight interior rectangles
(`UnionFootprint`), deduplicated, 4-connected and roof-supported, with its south door
on the bounding box's entrance edge: the door's inward cell is interior with interior
on both sides and one cell deeper, so a door never lands in a notch or on a
connector's mouth; the nearest such cell to the centre line wins.

### Site search

Every shell searches the same deterministic tiers. The search tries the 9x9
rectangle at each candidate centre; when it does not fit, concave templates wrap the
obstacle or span two clearings: `concave-l-ne/nw/se/sw` (35-cell-class L shapes in
9x9 bounds) and `connector-ew/ns` (two 4x4 chambers joined by a one-cell passage).
Sites of an earlier tier always outrank a later tier's, however near the anchor;
within a tier the first template fitting a centre wins. A shell is buildable only
when every cell is free, lit ground and its door opens onto free ground (a door
against rock seals the room; the threshold is checked only where observed). When no
template fits near the anchor, the routine grows a connected footprint from the
nearest free seed over cells whose eight neighbours are all free, stopping at 49
interior cells or sooner when terrain runs out (minimum nine), and walls its ring.
Site score, reserved yard and indoor storage placement are computed from the
footprint, not a fixed rectangle.

### Initial shelter rungs

On a fresh site the initial shelter runs three rungs under one Episode:

1. Sleeping spots at the first review, one per colonist owed, on the chosen layout's
   interior.
2. Wooden beds (`Bed`, north-facing 1x2; bedrolls in stocked fabric or leather while
   `Bed` is locked) on the same slots, off the ring's corner cells, the entrance aisle
   and the storage patch. The native refuses a bed over a standing spot, so the bed rung
   first deletes the standing spots (`shelter-clear-beds`; a bedroll being upgraded is
   packed to storage instead, never deleted), and places the beds once none stands
  . The rung waits, holding the ring, while the spots are unbuilt or the
   deletion is open.
3. The ring around them.

Each rung is one plan (`routine-bunks-*`, methods `shelter-spots`, `shelter-clear-*`,
`shelter-bedrolls` and `shelter-beds`), admitted one per review; once the beds are
admitted an open bed rung does not hold the ring, so
a stalled bed never keeps the walls and door from starting. The store admits a method
beside open bunk rungs only when it is pure construction on no bunk cell; an open
shell still holds the concern, and the indoor furnishing step waits for the bunks.

- Bunk cells count as free to the shell search; the layout enclosing every bunk with
  no bed on a corner is preferred. The dig is weighed only before any bunk is placed,
  and a ring already standing is adopted without bunks.
- A bed rung the native previews refuse whole falls through to the ring in the same
  review.
- Partial stock pays for the enclosure first: the bed rung holds back the ring's
  previewed costs and admits only the beds the remainder pays for; the ring follows
  at the next review.
- A restart rereads the bound rungs and sites the ring around them rather than
  placing new bunks.

### Shell adoption and repair

Pausing and resuming control suspends every routine concern and reactivates it in the
same world with plans still open. A world change (load token, map, tick rewind)
invalidates the concern and cancels its plans, and the executor cancels the plan's
native blueprints and frames; the walls and door already built, and the shell plans
in the journal, are then the only durable records of a shell in progress.

Before siting a shell the routine reads the player wall and door census within 64
cells of the colony centre and its earlier shell plans (`routine-shell-*`, retired or
not, the newest 64). For each candidate door, nearest the centre first, it tries
every earlier plan that placed a door on that cell (its whole ring; how a grown
irregular shell is recognised), then every starter shape whose south door lands there
(the 9x9 rectangle, then the concave templates).

- Shapes at one door share their lowest courses, so the adopted shape is the one whose
  ring the census matches best (most standing cells; an earlier plan, then the
  earliest template, on a tie), decided before any placement preview. A shape nothing
  standing matches is never adopted.
- The admitted plan holds only the shape's missing cells; a ring whose door was
  cancelled is reissued door first.
- When a missing cell of the best-matched shape is not placeable now, or it stands
  whole but the room census lists no enclosed room inside it yet, the review reports
  `awaiting_plan:earlier_shell` and waits: it never adopts a lesser shape at the same
  door nor sites a second shell beside an unfinished first.
- A facility ladder (comfort, workshop, hospital, sleeping) reaches adoption only
  because its furnishing step found no site, so it passes by every ring that already
  encloses a census room and sites a fresh shell, at most one per Episode.
- A controller restarted with an empty journal recognises template shells from the
  census alone; a grown shell is not recognised and the routine sites afresh.

A shell plan that settles with a cell unsuccessful leaves a gap the concern alone would
never close, since a suspended-and-resumed concern keeps its epoch and bound method. The
shell planner therefore walks a repair chain under the epoch (`<method>`,
`<method>-repair-1`, `-2`, ... up to eight): once the latest bound plan has settled
short of every cell completed, it binds the next repair method, reissuing exactly the
cells not standing.

Indoor furnishing treats the four orthogonal neighbours of every observed doorway (a
door, or a door blueprint or frame) as protected: the entrance aisle is never a
furniture candidate.

## Layout geometry

The layout plan is the only colony geometry: no colony grid, snapping or alignment
term in any site search. Districts are the layout plan's sectors
(`LayoutPlan.District`, `DistrictAnchor`); `RoomDistrict` maps a room role to its
district. Each routine anchors its site search on its district (`layoutAnchor`), and
on the colony centre when the plan has no free cell there.

### Layout generator

`policy.SiteCore` sites a fresh plan (`go/internal/policy/layout_gen*.go`):

- **Obstacles.** Core candidates lose their obstacle cells first (rich soil, ore rock,
  field zones), level by level; a level holds only if some plan places every base room
  and houses every colonist, else the next, looser level runs.
- **Clusters.** Base rooms are grouped by affinity (the weighted trip table plus the
  `besideRoles` pairs), each cluster placed as a unit on the hallways, packed on
  shared walls with Link doors.
- **Housing blocks.** Bedrooms go in wings and suites in suite blocks: each a straight
  corridor off a hallway, sited whole (a wing at most 10 rooms, a suite block at most
  6) and never grown.
- **Rings.** Hallway ends are joined into rings where the ground allows, rooms get
  second doors, and the plan's entrances are the hallway ends that reach outside the
  base; `CheckRoutes` proves every trip routes.
- **Search.** The best sites by score get a deterministic, iteration-bounded local
  search; wall time is only a hang guard.
- **Replan.** Two-stage: the same generator runs with every room that has anything of
  ours on it (built, blueprint or frame, in-flight plan or claim) fixed, and the
  result replaces the saved plan only when it houses as many colonists and scores
  clearly better (`planWeights.ReplanGain`, `layout_replan.go`).

### Layout reconcile

There is no separate tidy pass. A PlannedRoom is worked toward the plan by the room's owner
(build side) and by planned-ground clearance (clear side), both over one per-cell diff
(`policy.Reconcile`, `ReconcileRoom`): furniture off its template slot is packed and the
slot filled from packed stock or built on site, a wall of a lower-ranked stuff is swapped
in place, floors are replaced. Standing census rooms are reconciled too. The plan's
`RetiredGround` (a retired shelter's footprint) is cleared with the rest: its ring, spots
and furniture come down, a research table packed to stock for the laboratory to install,
and the entry drops once nothing of ours stands on it.

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
`general` zone) below the gear hit-point or quality floor sells to traders (`policy.SaleGear`,
`export_thing_ids`); unknown hit points or quality is not sale gear.

**Gear rooms.** Gear is stored in layout's armory and wardrobe rooms; until a room stands,
gear stays in the warehouse. The armory leaves out cells within six of a prison, and layout
keeps both rooms out of each other's clearance.

### Site selection and spatial program

Site selection compares up to the configured method-attempt limit using native
terrain/fertility, placement, danger, current stock and projected travel to each
candidate entrance. Comparisons retain refusal evidence without reserving unselected
sites.

The persisted spatial program describes habitable shelter, food services and
maintained capacity using existing functional concerns and fresh native gates.
Population growth reopens shelter capacity. Routine storage development waits for
observed indoor sleeping capacity; urgent cooking, food acquisition, medical care and
temperature control keep their priorities. Future phases own no cells or game orders.

### Staged excavation

When the initial shelter is due, the planner compares the open-site starter shell with
a staged excavation into a visible rock face.

**Target.** An ordered cell set: a one-wide corridor of two to four cells from a known
walkable access cell, then an interior past the door cell generated by a deterministic
shape, a rectangle or an integer-membership ellipse (`domain.EllipseInterior`), whose
parameters, not its cells, travel in the target key. Every target cell must lie within
the native roof-support radius of a cell left untouched, so the rock left standing
holds the roof, and must be visible rock under a natural rock roof or fogged inside
the observed planning window. Any known open, indoor or protected cell inside or
beside the room rejects it; nothing outside the window is planned.

**Choice.** The planner tries shapes at each face in an order that follows the shelter
style (a neolithic colony digs the round room first, everyone else the rectangle) and
proposes the first legal shape at the shortest legal corridor; faces rank by distance.
The planning window carries no reachability; a site no colonist can reach is refused
at `placement_preview` and the planner moves on. A verified site within planning
reach of the colony anchor wins over any shell; farther sites win only when no clean
shell exists or when nearer than the shell.

**Stages.** Work proceeds in stages of at most eight visible, eligible,
frontier-adjacent cells, each admitted only after a fresh native site read reports the
stage supported with a miner able to reach the access cell. The next stage waits for
the previous stage's observed completion and re-reads the geometry rather than
assuming the fogged interior. The door is built only after every target cell is
observed cleared or kept; furnishing then follows the ordinary indoor sleeping method.

**Review of changes.**

- A cell that unfogs into something not rock and not clearable is kept: never staged,
  dug around, and the room completes without it so long as the corridor and the cell
  past the door stay clear.
- A kept corridor or entrance cell, or an access cell no miner can reach, blocks the
  way in: the project is dropped and the same review re-sites the shelter (another
  verified face first, the shell otherwise), the stage count continuing.
- A whole-target read that is unsupported with no collapse pending means the roof rock
  is gone; the project is dropped the same way. A pending collapse, an unknown verdict
  or a missing miner only wait.
- A stage admitted before such a change closes through its own executor; a stage
  action held not ready, unsupported or on changed geometry longer than the stall
  grace is cancelled so the closed plan lets the review run.

**Restart.** A restarted controller whose concern survives rediscovers the project from
its durable stage plans. When the concern was invalidated the successor concern resumes the
most recently planned target from those plans, verified by a fresh native site read
that still shows rock to dig, before any new face is considered. A cleared cell is
never re-designated; a project the read reports breached, blocked or fully cleared is
dropped and the review plans from the geometry the pawns actually opened.

### Adopting an existing shell

Adoption can describe a connected nonrectangular interior of at most 3844 unique cells
inside the inspected bounds, with an exact boundary entrance and direction.
`interior_cells` and `entrance_cell` must be supplied together. Native room geometry,
roof and completed doorway must match, with a verified safe native pawn route to both
doorway approaches. The observed role and load/map identity are retained. Furnishing
keeps a connected entrance aisle and every narrow connector. Native neutral structures
can be reused without claiming them; missing shell pieces need ordinary explicit
construction and roof completion before adoption. A load requires fresh adoption; an
altered interior invalidates it.

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
points explodes, so hit points are a safety reading. The census is bounded to 64
shrines, 32 caskets, 256 guards and 64 breach walls per shrine; overflow or an
unreadable scan is unavailable, never sampled. Nothing in this read admits a breach,
a casket order or a claim: readiness, the breach concern and casket
handling decide.

Optional heat facts (visible roof-connected interior of at most 256 cells,
independent of building ownership: measured temperatures, enclosure, colonist
presence, one repairable door site, heater sites and existing heaters, doorway/retreat
cells) are omitted on ambiguous geometry or fog; unknown never means heat-ready.

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
not change geometry or `LinesVerified`.

Each cell whose fill comes from a thing the game's own designators could remove names
it (`cover_thing_id`, `cover_def_name`, `cover_kind` plant/chunk/mineable/building,
`cover_designated`) with a CAS token over identity, definition, cell and designation
state. `RaidArrivalState`, a map component, samples every hostile lord every 60 ticks:
its first pawn position is the spawn (ground when within 14 cells of the map edge) and
the pawn nearest the home area adds one trail cell per sample, up to 128 per lord and
32 lords per session. The snapshot's `raids` rows carry those tracks; the controller
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
eight clearances per method once every tier stands, at most four methods per game day.
