# Spatial contracts

[Documentation](../../README.md)

These rules constrain shared plan admission and each later dispatch. They use native
observations; they do not reserve permanent ownership of coordinates.

## Shared admission

Shared spatial validation rejects overlapping planned room bounds, growing zones inside
planned rooms, and building footprints across reserved walkways or a room's doorway and
immediate inside/outside approaches. Indoor stockpiles remain allowed. Changing
construction, zones or walkways refreshes native footprints for the live plan, including
retained buildings; unavailable geometry refuses admission. Hands rechecks the current
placement's footprint before writing. These constraints protect accepted planned space;
they do not establish native reachability, arbitrary room connectivity, future expansion
rights or access throughout pawn construction.

## Native zone and doorway checks

New room shells also inspect the complete native zone census, rejecting enclosed farms
even when their cells do not touch the perimeter. Indoor stockpiles remain allowed;
perimeter overlap, unknown zone kinds and incomplete or conflicting zone geometry block
construction. Exact three-cell native reads require walkable, passable, unfogged
approaches immediately inside and outside the doorway. Hands refreshes these checks for
every unissued shell placement, preserving existing receipts on refusal. These reads do
not predict future blueprint obstruction or prove a route to colonists, and native input
is not atomic with controller validation.

## Bounded connectivity

Shell admission and each dispatch batch also scan the interior and a three-cell exterior
margin, clipping to observed map bounds and splitting detailed reads into at most 1024
cells each. Four-neighbor traversal requires usable interior cells to connect to the
inside approach, and the outside approach to reach the edge of that local margin without
crossing the proposed shell or another planned shell's walls. Unknown interior geometry
refuses construction; exterior unknowns cannot establish a route. Map edges do not count
as exits. This is local topology validation, not native pawn reachability or a forecast
of arbitrary future building obstructions. Changing the planned shells revalidates
retained shells too, so expansion cannot silently seal the only observed local exit of
an already completed tracked room.

Furniture changes revalidate tracked shells using exact native footprints and
completed-definition passability. Impassable non-door buildings are projected as
obstructions; unknown classification refuses admission. Before the first real
write of each construction batch, Hands refreshes the shared spatial checks after
ordinary placement and resource checks. Cancelled unbuilt intent releases its
projected geometry; surviving objects remain part of native map observations.

## Native pawn access

`home/spatial_access` compares each mobile colonist's current safe, unfogged,
allowed-area four-neighbor component with projected building/terrain obstruction.
Every previously reachable cell outside the footprint must remain reachable,
including observed rooms whose old actions have been retired. Native door opening
eligibility and exact-target native reachability remain separate checks. A pawn
standing on a proposed footprint needs a currently native-reachable exit; this
read does not move it. The audit is bounded to 262144 map cells, 32 mobile colonists,
16384 projected cells and 128 targets. Unsupported or unreadable evidence blocks
admission. Future danger, door locking and actual pawn labor remain simulation
outcomes.

## Room footprints

Every planned shell is an exact-cell `RoomFootprint`: a 4-connected interior of
1..3844 cells away from the map edge, a wall ring of every non-interior cell
that touches the interior orthogonally or diagonally, and one door on the ring
whose inward neighbour is interior and whose outward neighbour is neither. The
door is placed first, then walls in north-facing, z-outer/x-inner order; the
ring is one wave with the door first in dispatch order, and no wall waits for
the door to complete (a door blueprint or frame no more seals a room than a
wall's does). Rectangles keep their
former cells exactly. Ovals use integer membership on the axis-aligned or 45°
diagonal ellipse (long axis north-south, east-west, north-east or north-west,
radii 2..30) and are only generated when every interior cell lies within
Euclidean distance six of a wall, so the finished hut roofs itself without
columns.

A composite footprint is the union of one to eight interior rectangles
(`UnionFootprint`), deduplicated, 4-connected and roof-supported like any
other, with its south door on the bounding box's entrance edge: the door's
inward cell is interior with interior on both sides and one cell deeper, so a
door never lands in a notch or on a connector's mouth, and the nearest such
cell to the centre line wins.

A shell's shape follows the build tier and the colony's observed player
faction tech level (`shelterStyle`, #609): from `Masonry` up every room is
a module of the colony grid (the module style below); at `Camp` a Neolithic
colony raises huts and everyone else the rectangle, in deterministic tiers.
A Neolithic colony tries the hut templates
(`hut-template-0..7`: circle r4, ovals 3x5 north-south, east-west, north-east
and north-west, circle r3, then the low ovals 2x6 north-south and east-west
that fit a strip seven cells wide) at each candidate centre; every colony
then tries the 9x9 rectangle; when neither fits, the concave templates
(`concave-l-ne/nw/se/sw`: an L of two three-wide arms, 33 cells in 9x9
bounds, notch in each quadrant; `connector-ew/ns`: two 4x4 chambers joined
by a one-cell passage, 35 cells) wrap the obstacle or span two clearings.
Sites of an earlier tier always outrank a later tier's, however near the
anchor; within a tier the first template fitting a centre is that centre's
shell. A shell is buildable only when every cell is free, lit ground and its
door opens onto free ground (a door against rock seals the room; the
threshold is checked only where it is observed). When no template fits the
lit free ground near the anchor, the routine grows a connected footprint
from the nearest free seed over cells whose eight neighbours are all free,
stopping at the rectangle's 49 interior cells or sooner when the terrain runs
out (minimum nine), and walls its ring; that is how shapeless rooms in a
corridor arise. Site score, reserved yard and indoor storage placement are
computed from the footprint, not a fixed rectangle.

The module style (`policy.ShelterModule`) fills the colony grid's modules
instead of searching centres: `ModuleShells` places every template in one
module — the two 5x11 and two 11x5 halves first (55 cells, nearest the
starter interior), then the whole 11x11, then the four 5x5 quarters — each
a rectangle on one `SubCells` interior so neighbours share their divider
wall and a whole module roofs itself, with the door centred on every side
that faces an aisle (four for the whole, three for a half, two for a
quarter; `module-<w>x<h>-<sub-cell>-<side>`). The search tries every
module the observed cells touch in corner order and sites each with its
first buildable class, the door nearest the plaza (the origin module's
centre) preferred, the class as the tier; a module's threshold is an aisle
cell, so it need only be open ground, not unprotected. Without a known grid
the style searches as the rectangle. `ModuleShellsAtDoor` is the module
counterpart of `ShellShapesAtDoor` for adopting a ring begun earlier.

Past `Masonry` a build tier also unlocks a shape family beyond the single
module (`policy.ModuleShapeFamily`, a pure `f(tier, roomRole)`; #610, #637).
The roles partition: the dining room and the production rooms take the
double-module hall from `Powered`, the housing rooms paired wings from
`Industrial`, and the plaza's own rooms the enclosed courtyard at `Spacer`;
`Camp` and `Masonry`, storage and the fields keep the single module. The
geometry is a pure `f(grid, module)` (`ShapeFamilyShells`):

- the hall spans a module, the aisle bay and the neighbouring module along
  one grid axis, an 11x27 interior whose own side walls support the roof, so
  it needs no pillar row — one template per neighbouring module and side
  (`hall-<w>x<h>-<du>,<dv>-<side>`);
- a wing is a whole module opening onto the plaza, its twin the module
  mirrored through the plaza (`PairedWingModule`), so housing grows as
  symmetric pairs (`wings-11x11-<side>`);
- the courtyard is a whole module whose central 5x5 sub-cell stays open to
  the sky inside its own wall ring, leaving a two-wide roofed room around it
  (`courtyard-11x11-<side>`).

`ModuleShapes` offers the family's shells in a search class ahead of the
halves and falls back to `ModuleShells`, so a module that cannot hold the
shape is still filled; `ModuleShapesAtDoor` adopts either. The planners pass
their room role's family (`StarterRequest.Shape`).

On a fresh site the initial shelter runs three rungs under one goal epoch
(#612): sleeping spots at the first review, one per colonist owed, on the
chosen layout's interior; then the wooden beds (`Bed`, north-facing 1x2)
as the first construction, off the ring's corner cells, the entrance aisle
and the storage patch; then the ring around them. Each rung is one plan
(`routine-bunks-*`, methods `shelter-spots` and `shelter-beds`) and the next
waits for it to settle. Bunk cells are treated as free by the shell search
and the layout enclosing every bunk, with no bed on a corner, is preferred;
the dig is weighed only before any bunk is placed, and a ring already
standing is adopted without bunks. A bed rung the native previews refuse
whole falls through to the ring in the same review.

Pausing and resuming control (a letter pause, a keep-alive resume, a paired
restart) suspends every routine goal and reactivates it in the same world
with its plans still open. A world change (load token, map, tick rewind)
instead invalidates the goal and cancels its plans, and the executor cancels
the cancelled plan's native blueprints and frames; the walls and door already
completed natively, and the shell plans in the controller's own journal, are
then the only durable records of a shell in progress. Before siting a shell,
the routine reads the player wall and door census within 64 cells of the
colony centre and its earlier shell plans (`routine-shell-*`, retired or not,
the newest 64), and for each candidate door nearest the centre first -- a
door standing natively or one an earlier plan ordered -- tries, first, every
earlier plan that placed a door on that cell (its whole ring, which is how a
grown irregular shell with no template is recognised) and then every starter
shape whose south door lands there (the hut templates for the hut style, then
the 9x9 rectangle, then the concave templates). Shapes at one door share their lowest courses, so the
shape adopted is the one whose ring the census matches best (most standing
cells; an earlier plan, then the earliest template, on a tie), decided before
any placement preview; a shape nothing standing matches is never adopted, so
a ring cancelled before anything was built is sited afresh. The admitted plan
holds only that shape's missing cells, and a ring whose door was cancelled is
reissued door first as a fresh shell would be. When a missing cell of
the best-matched shape is not placeable now (for instance a cancelled frame
still clearing), or when it stands whole but the room census lists no enclosed
room inside it yet, the review reports `earlier_shell_blocked` and waits: it
never adopts a lesser shape at the same door nor sites a second shell beside
an unfinished first. A facility ladder (comfort, workshop, hospital, sleeping)
reaches adoption only because its furnishing step found no site, so it passes
by every ring that already encloses a census room, whole or a template cell
short, and sites a fresh shell for the facility, at most one per goal epoch. A controller restarted with an empty journal recognises template
shells from the census alone; a grown shell is then not recognised and the
routine sites afresh.

A shell plan that settles with a cell unsuccessful (a wall the player
cancelled in-game, a failed frame) leaves a gap the goal alone would never
close, since a suspended-and-resumed goal keeps its epoch and its bound
method. The shell planner therefore walks a repair chain under the epoch --
`<method>`, `<method>-repair-1`, `-2`, ... up to eight -- and once the latest
bound plan has settled short of every cell completed, binds the next repair
method to a plan produced by the same adoption path, which reissues exactly
the cells not standing.

Indoor furnishing treats the four orthogonal neighbours of every observed
doorway (a door, or a door blueprint or frame) as protected: the entrance
aisle is never a furniture candidate.

## Colony grid

`policy.ColonyGrid` is the shared layout geometry every site search can
snap to (#605): an origin cell, a pitch and two perpendicular unit axes.
The pitch is `policy.GridPitch` (16) at every build tier: an 11x11 module
interior, its two walls and a 3-wide aisle. 11x11 is the largest interior
the game roofs without a pillar and the lit disc of one sun lamp, and it
subdivides 5+1+5 into two 5x11 halls or four 5x5 rooms (`SubCells`) with
the divider walls on the module's own sub-grid; conduits run inside the
walls, never in the aisle, so power never dictates the pitch. `Camp` simply
ignores the grid and higher tiers fill module sub-cells. The pure helpers
are `Snap` (nearest intersection), `OnGridLine` and `CornerError` (a
rectangle's south-west corner offsets to the nearest lines, C4's penalty
input), `Aisles`/`AislesWithin` (aisle cells at offsets 13-15 of each pitch
along either axis) and `District` (below).

Districts (#609) are the grid's coarse sectors: `policy.Districts` over a
grid assigns the origin module to `plaza`, the ring of modules at the
defense radius (three, or one module past the furthest module a known
colony extent reaches, `DistrictsFor`) to `defense`, and every other module
to the wedge of the axis it lies furthest along — `housing` along +axis1
(north), `production` +axis0 (east), `storage` -axis0 (west), `fields`
-axis1 (south); ties go to the axis1 wedges. A known wind (the unit vector
it blows toward) rotates the wedges so `fields` lie downwind, `storage`
upwind and `production` a quarter turn on from the wind. `RoomDistrict`
maps a room role to its district beside `FacilityCatalog` (sleeping and
care in housing, benches and kitchens in production, storerooms in storage,
barns with the fields, common rooms on the plaza). `Districts.Anchor`
returns the centre of the district's module nearest the origin that a
caller's free predicate accepts, ring by ring, and reports none when the
district is full. Each routine declares its district and anchors its site
search there (`layoutAnchor`: the shelter and expansion planners and the
bedroom ladder in housing, the workshop and laboratory ladders in
production, covered storage and the supply room in storage, farms, hay and
pens in the fields); at `Camp`, without a grid, or when the district has no
module whose cells are all observed free, the search anchors on the colony
centre as before. Indoor furnishing keeps its radius reaching the colony
centre from the district anchor, so the starter shell stays a candidate
until a room stands in the district.

`DeriveColonyGrid` fixes the origin at the starter shell's south-west
exterior corner or, without a recorded starter shell, at the largest wall
ring's corner in a complete construction census; neither known leaves it
unknown, and identical inputs give identical grids. The routine review
(`reviewColonyGrid`) serves the persisted grid on the colony projection
(`ColonyProjection.ColonyGrid`) and derives one only when the timeline holds
none; a grid once recorded never moves (persistence:
[persistence contracts](persistence-contracts.md)). The routines API reports
it as `colonyGrid` with the map bounds, the dashboard's development panel
draws the overlay (grid lines and origin marker) and `acceptance why` lists
the persisted rows.

### Layout tidy

`TidyLayout` (#611) re-sites a settled colony's early sprawl one item at a
time. It is a maintenance goal ranked below every production, upkeep and
defense goal (the lowest priority, a nominal deficit) and is active only at
tier >= `Masonry`, with a known grid, while the colony has no unfilled
construction or hauling work: any open project definition or open building,
haul, zone or clearance action, an incomplete zone census or unknown zone
claims all read as busy. Candidates are managed items only: a zone the
colony itself created (a completed `zone_create` under an autopilot goal,
`store.ZoneClaims`) still listed by the zone census, or a `Camp` shell whose
every ring cell stands on a construction claim. A managed field is a
candidate when its corner is off the module sub-cell corners
(`tidyAlignment`, offsets 1 and 7 from a grid line) or it holds fewer than
a half module's cells; a managed stockpile when it is off those corners; a
shell when it is off the grid lines, not in use (no beds, no contents) and
another empty enclosed room of the same role stands on the grid.
`policy.PlanTidyLayout` proposes the candidate with the largest alignment
gain (ties to the nearest free sub-cell, then the lowest id): a field moves
to the nearest free fertile sub-cell of the C5 size in the Fields district
(then any), a stockpile to the smallest sub-cell holding its cells in
Storage, a shell is deconstructed. Player zones and buildings are never
touched, one re-site is in flight at a time and a tidied item (moving, done
or abandoned) is never proposed again; the set is journaled per world and
timeline (`store.RecordLayoutTidy`, persistence contracts).

The planner (`RoutineTidyPlanner`, family `tidy`) runs a zone re-site as two
methods under the goal: `tidy-create-*` admits the new zone through the
building admission family (a zone preview, no cost) and journals the tidy
moving; once the new field reports planted cells (a stockpile as soon as it
stands, its contents move by ordinary hauling) `tidy-delete-*` commits a
one-shot `zone_delete` of the old zone by its per-zone CAS token, and the
tidy is journaled done when the census no longer lists it. A refused
preview or admission, a create method that closed without a zone, or a new
zone that vanished journals the tidy abandoned. A replaced shell is one
`tidy-shell-*` method of deconstruction actions over its claimed ring. The
review record carries the outcome (`RoutineReview.Layout`), the routines
API reports it as `layoutTidy` (the pending proposal with its explanation,
or why none stands), the dashboard's development panel shows it, and every
proposal, start, deletion and completion is a `layout`/`tidy` clock event.

Site selection compares up to the configured method-attempt limit using native
terrain/fertility, placement, danger, current stock and projected travel to each
candidate entrance. Comparisons retain refusal evidence without reserving
unselected sites.

The persisted spatial program describes habitable shelter, food services and
maintained capacity using existing functional goals and fresh native gates.
Population growth reopens shelter capacity. Routine storage development waits
for observed indoor sleeping capacity; urgent cooking, food acquisition, medical
care and temperature control retain their priorities. Future phases own no cells
or game orders.

When the initial shelter is due, the planner compares the open-site starter shell
with a staged excavation into a visible rock face. An excavation target is an ordered
cell set: a one-wide corridor of two to four cells from a known walkable access cell,
then an interior past the door cell generated by a deterministic shape — a rectangle
or an ellipse from the same integer formula as the hut templates — whose parameters,
not its cells, travel in the target key. Every target cell, whatever the shape, must
lie within the native roof-support radius of a cell left untouched, so the rock left
standing holds the roof. The planner tries its shapes at each face in a preference
order that follows the shelter style (a neolithic colony digs the round room first,
everyone else the rectangle) and proposes the first legal shape at the shortest legal
corridor; faces then rank by distance as before. The planning window carries no
reachability (it is a function of which colonists are undrafted, not of the map);
a site no colonist can reach is refused at `placement_preview`, and the planner
moves on. Every target cell must be visible rock
under a natural rock roof or fogged (unknown) inside the observed planning window;
any known open, indoor or protected cell inside or beside the room rejects it, and
nothing outside the observed window is planned. A verified site within the planning
reach of the colony anchor wins over any shell; farther sites win only when no clean
shell exists or when nearer than the shell. Work proceeds in stages of at most eight
visible, eligible, frontier-adjacent cells, each admitted only after a fresh native
site read reports the stage supported with a miner able to reach the access cell;
the next stage waits for the previous stage's observed completion and re-reads the
geometry rather than assuming the fogged interior. That review decides what a
change means. A cell that unfogs into something that is not rock and cannot be
cleared (an ancient wall, water, a vein the guard refuses) is kept: never staged,
dug around, and the room completes without it, so long as the corridor and the cell
past the door stay clear. A kept corridor or entrance cell, or an access cell no
miner can reach, blocks the way in; the project is dropped and the same review
re-sites the shelter, another verified face first, the shell otherwise, with the
stage count continuing under the new target. A whole-target read that is
unsupported with no collapse pending means the rock that held the roof is gone:
nothing sound can be finished there, and the project is dropped the same way. A
pending collapse, an unknown verdict or a missing miner only wait. A stage
admitted before such a change closes through its own executor (the native cancel
is an unsuccessful action); a stage action held not ready, unsupported or on
changed geometry for longer than the stall grace is cancelled so the closed plan
lets the review run. The door is built only after every target cell is observed
cleared or kept, and furnishing follows the ordinary indoor sleeping method. A restarted controller whose goal survives rediscovers the project
from its durable stage plans; when the goal was invalidated (for example by a control
hand-back before the restart) the successor goal resumes the most recently planned
target from those same plans, verified by a fresh native site read that still shows
rock to dig, before any new face is considered — the colony window follows the pawns
and may no longer show a half-dug room at all. Either way a cleared cell is never
re-designated; a project the read reports breached, blocked or fully cleared is
dropped and the review plans from the geometry the pawns actually opened.

Adoption can describe a connected nonrectangular interior of at most 3844 unique
cells inside the inspected bounds, with an exact boundary entrance and direction.
Both `interior_cells` and `entrance_cell` must be supplied together. Native room
geometry, roof and completed doorway must match, with a verified safe native pawn
route to both doorway approaches. The observed role and load/map
identity are retained. Furnishing keeps a connected entrance aisle and every
narrow connector. Native neutral structures can be reused without claiming them;
missing shell pieces need ordinary explicit construction and roof completion
before adoption. A load requires fresh adoption; an altered interior invalidates it.

## Zone and facing postconditions

New zone project targets retain expected patches, kind, crop and native settings.
Growing zones retain sow/cut permissions; stockpiles retain priority and exact
ThingFilter definition/special-filter allowances and condition, quality and
mental-break ranges. Stockpile expectations come from native previews, including
presets; truncated display samples cannot establish the contract. Matching labels
with conflicting settings require explicit editing. Fresh complete native list/grid
geometry and settings must match before an action can satisfy dependent work.
Building targets retain facing through blueprints, frames and completed buildings.
Native Rot4 integers remain invariant across languages; older native versions can
use recognized cardinal labels, while unavailable facing holds verification.
Native edits invalidate completion. An explicit retry of an invalidated project
requires fresh exact native restoration before preserving its confirmed slots and
resuming dependencies. Unknown observations can recover through reads without replay.
Legacy geometry/facing expectations are recovered only from a unique durable
action owning the exact project targets. Unassociated records retain their earlier
contracts; current map state cannot invent missing historical settings.

## Clearance observations

`Observations.GetClearanceTargets` is a read-only census of spawned,
visible, deconstructible non-player buildings whose occupied rectangle touches
Home. Each row records the complete occupied rectangle, nullable faction,
classification, whether every occupied cell is in Home, sealed ancient-danger
membership, and the current deconstruct designation with controller ownership.
Haulable rock chunks and slag are items, never targets: the same read lists
them as `chunks` (every chunk-category stack standing on a Home cell, with its
cell, forbidden state, vanilla's valid-storage test and whether ordinary
hauling already has a better store cell for it, bounded to 256) and, only
while some allowed, unstored chunk has no destination, `dump_sites`: one
connected footprint of up to 16 free Home cells (psychologically outdoors,
standable, unzoned, empty storage ground, not marked to collapse, reachable
and unforbidden for an eligible hauler) flooded from the nearest such cell
within 20 of those chunks' centroid, on which a dumping stockpile can be made.

A missing roof blocker means removing this building alone preserves roof
support; blocked or unknown geometry carries a reason. It does not authorize
removal, prove access, or establish safety for removing several holders together.
Ancient-danger rows remain visible as blocked candidates for future policy.
The native check uses sealed ancient-temple warning regions and enclosed roofed
rooms with occupied ancient caskets or fogged hives/hostile pawns. The room check
survives the warning trigger disappearing when a colonist approaches. It checks
the footprint and adjacent rooms so the enclosing wall is protected too.

For a roof-holding building, the counterfactual excludes every occupied cell and
seeds connected-roof searches from the union of the installed support-radius
neighborhoods of those cells. Non-holders cannot remove support. Fog, unknown
map-edge geometry or a pending collapse blocks the removal. The pure geometry
probe runs under `task probes:test` with a hand-built multi-cell rectangle.

The scan walks the Home cells rather than the map's building list, so the
natural rock of a hilly map never counts against it; only non-player buildings
standing in Home are bounded (8192). The complete census is bounded to 256 rows
and 1 MiB; overflow or an unreadable scan is unavailable, never sampled. The Go observation treats an explicit
unavailable native stub as an unknown fact, distinct from a complete empty
census. Required safety booleans cannot be omitted. The observation context
must match the expected load, map, generation and fresh tick. The read admits
nothing itself; `ClearHomeObstructions` consumes it
([upkeep contracts](upkeep-contracts.md)).

## Ancient shrine observations

`Observations.GetAncientShrines` is a read-only census of ancient-danger
rooms, one row per shrine rather than one per flagged wall (the clearance
census above still marks the individual walls `ancient_danger`). A row
carries the room rectangle, `sealed` (interior fogged, no open roof, not on
the map edge), whether any room cell is in Home, every cryptosleep casket
with its hit points, `has_contents` and player claim, the hostile pawns and
hives inside the room once the interior is unfogged (`guards_known`; a
sealed shrine never reports guards), and the perimeter walls the player may
deconstruct without a roof-support blocker, each with its definition
(`def_name`, #458) and the adjacent cell outside the room.
For a visible neutral wall on a sealed shrine, observation and deconstruction
inspect native roof structure inside that bounded room and its border despite
fog. Unsupported roofs, pending collapse and fog outside that footprint still
block. This structural check reveals no occupants and never unfogs the room.

The occupant of a filled casket is unknown until it opens; a casket under
20% hit points explodes, so hit points are a safety reading. The census is
bounded to 64 shrines, 32 caskets, 256 guards and 64 breach walls per
shrine and 1 MiB; overflow or an unreadable scan is unavailable, never
sampled, and the Go observation treats the unavailable stub as unknown.
Nothing in this read admits a breach, a casket order or a claim: readiness
(#457), the breach goal (#458) and casket handling (#459) decide.

The shrine census also carries optional heat facts for a visible roof-connected
interior of at most 256 cells, independent of building ownership: measured
inside/outside temperature, enclosure, colonist presence, one repairable door
site, heater sites and existing heaters, and paired doorway/retreat cells.
Ambiguous geometry or fog omits this section; unknown never means heat-ready.

## Related reading

Read [space and resources](../architecture/space-and-resources.md) for why planned
geometry is rechecked.

## Defense approach demand

`policy.DefenseLayout.Approaches` groups observed, Home-connected boundary
cells into contiguous sectors on each side of the bounded defense census. The
flood starts at Home, or at the layout's Entry when Home itself is not a
passable cell (the colony centre often lands on a building).
`EdgeReachable` proves a connection to some map edge; it does not identify
which edge a distant raid used. Arrival inputs therefore carry a distinct
raid ID, its observed local boundary crossing and its arrival tick. Duplicate
observations count once, arrivals older than three game days expire, and
unmatched crossings remain explicit. Sectors rank by recent raid count,
then shortest observed distance to Home, with deterministic coordinate ties.
A turret's `LastAttackTargetTick` is firing evidence, not a raid identity or
arrival location, and cannot by itself increment a sector's count.

Each sector has an observed passable route to Entry with proposed funnel
walls closed. Reachability to firing positions with both entry lanes closed
reports `route_bypasses_entry`; this is a layout finding, not clearance demand.
Cover demand uses the observed native cover fill threshold, strictly
exceeded, within the shortest defender range ahead of Entry or beside the
last six route cells. The game grants a block chance to any positive fill,
so the native census reports a threshold of zero: a tree (0.25) or a chunk
(0.5) is cover as much as a rock wall. Unknown range or threshold holds
selection. Accepted footprints, firing positions, both lanes and existing
rock supporting their flanks are protected. Map-edge rock, mountain
interiors and rock faces (natural rock touching impassable natural rock, the
outer course of a rock band) carry concrete holds; only free-standing rock
is mined. Demand within a sector ranks nearest Entry first. Ranking cover
demand does not change geometry or `LinesVerified`.

The defense site census supplies the inputs (#581). Each cell whose fill
comes from a thing the game's own designators could remove names it
(`cover_thing_id`, `cover_def_name`, `cover_kind` plant/chunk/mineable/
building, `cover_designated`) with a CAS token over identity, definition,
cell and designation state. `RaidArrivalState`, a map component, samples
every hostile lord every 60 ticks: its first pawn position is the spawn
(ground when within 14 cells of the map edge, where a walk-in raid stands at
its first sample) and the pawn nearest the home area adds one
trail cell per sample, up to 128 per lord and 32 lords per session. The
snapshot's `raids` rows carry those tracks; the controller takes the first
trail cell inside its census region as the crossing, and the policy snaps it
to the nearest sector edge cell within eight cells. Drop pods and tunnellers
are not ground arrivals.

Clearance is the `cover_clearance` action (`ClearCover` operation): one exact
thing by identity and token with the designation its kind takes (`CutPlant`,
`Haul`, `Mine`, and `Deconstruct` on an unowned building such as a ruin wall;
player-owned cover is held as `structure`).
The native side re-checks presence, cell, fog, fill, forbiddance, an existing
designation, roof and mining safety, a store for a chunk, the designator's
own acceptance (its refusal text is surfaced), a reachable free colonist with
the work type and the token before designating. `CutPlant` on a harvestable
tree designates `HarvestPlant` (chop wood), since the cut-plants designator
refuses harvestable trees; other plants take a forced `CutPlant`. The thing
gone, or a chunk hauled off its cell, is completed; still designated is pending
ordinary work, undesignated by the player is unsuccessful. The defense layout
planner orders up to eight clearances per method once every tier stands, at
most four methods per game day.
