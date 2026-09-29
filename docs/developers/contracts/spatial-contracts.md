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

`Observations.ReadSpatialAccess` compares each mobile colonist's current safe, unfogged,
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
former cells exactly.

A composite footprint is the union of one to eight interior rectangles
(`UnionFootprint`), deduplicated, 4-connected and roof-supported like any
other, with its south door on the bounding box's entrance edge: the door's
inward cell is interior with interior on both sides and one cell deeper, so a
door never lands in a notch or on a connector's mouth, and the nearest such
cell to the centre line wins.

Every shell searches the same deterministic tiers.
The search tries the 9x9 rectangle at each candidate centre; when it does not fit, the concave templates
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

On a fresh site the initial shelter runs three rungs under one goal epoch
(#612): sleeping spots at the first review, one per colonist owed, on the
chosen layout's interior; then the wooden beds (`Bed`, north-facing 1x2; bedrolls in stocked
fabric or leather while `Bed` is locked, as many as the stock covers)
as the first construction, off the ring's corner cells, the entrance aisle
and the storage patch; then the ring around them. Each rung is one plan
(`routine-bunks-*`, methods `shelter-spots` and `shelter-beds`), admitted one
per review, and an open rung does not hold the next (#641): the spots, the
beds and the ring can all be open at once, so a stalled bed never keeps the
walls and door from starting. The store admits a method beside open bunk
rungs only when it is pure construction on no bunk cell; an open shell still
holds the goal, and the indoor furnishing step still waits for the bunks. Bunk cells are treated as free by the shell search
and the layout enclosing every bunk, with no bed on a corner, is preferred;
the dig is weighed only before any bunk is placed, and a ring already
standing is adopted without bunks. A bed rung the native previews refuse
whole falls through to the ring in the same review. Partial stock pays for
the enclosure first: the bed rung holds back the ring's previewed costs from
the observed stock and admits only the beds the remainder pays for, so short
wood admits fewer beds, or none, and the ring (whose frames wait natively for
materials) follows at the next review. A restart rereads the bound rungs and
sites the ring around them rather than placing new bunks.

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
shape whose south door lands there (the 9x9 rectangle, then the concave templates). Shapes at one door share their lowest courses, so the
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

## Layout geometry

The layout plan is the only colony geometry (#1175): there is no colony
grid, no snapping and no alignment term in any site search. Districts are
the layout plan's sectors (`LayoutPlan.District`, `DistrictAnchor`);
`RoomDistrict` maps a room role to its district. Each routine anchors its
site search on its district (`layoutAnchor`) and on the colony centre when
the plan has no free cell there.

### Layout tidy

`TidyLayout` (#611, #809) re-sites a settled colony's off-plan furniture
onto each room's derived interior plan, one room at a time. It is a
maintenance goal ranked below every production, upkeep and defense goal,
active only at tier >= `Masonry` while the colony has no unfilled
construction or hauling work. One re-site is in flight at a time and a
tidied piece is never moved again; the set is journaled per world
(`store.RecordLayoutTidy`). Stockpiles are MaintainStockpiles' (below).
The review record carries the outcome (`RoutineReview.Layout`), the
routines API reports it as `layoutTidy` and the dashboard's development
panel shows it.

### Stockpile maintenance

`MaintainStockpiles` (#725, family `stockpiles`) re-evaluates the colony's
own stockpiles (`store.ZoneClaims`, role-keyed) every review cycle, at any
tier. Fill is the share of a zone's planning cells whose storage-empty flag
is false. `policy.PlanStockpileMaintenance` proposes at most one edit per
zone: delete a zone whose role retired, patch one whose role's desired
filter or priority differs from the last applied (`store.StockpilePatches`
over the zone_create's settings), grow a zone at 85% fill onto the open
cells beside it (a quarter of its size, at least 4; twice that when full),
delete a same-role fragment into a sibling with room, and shed the empty
edge cells of a zone that sat at or under 25% for a game day (never under
twice its used cells or 4). Edits are admitted in that order while the haul
jobs each triggers (grow: cells added; delete, merge, retarget: used cells;
shrink: none) fit a budget of 8 per colonist; the first always fits. A
role-less legacy claim is resized and merged, never retargeted or deleted.
The desired state per role comes from the role's owning planner through
`buildingruntime.RegisterStockpileRole(prefix, source)`; a role no source
claims keeps its settings, and a source answers nothing while the fact it
judges by is unknown. The owners: `general` and `covered:<def>` (secure
supplies, fixed settings), `ingredients:<benchID>` (retired once the bench
census no longer lists the bench), `medicine:<roomID>` (retired once the
room census no longer shows the room as a hospital), and MaintainStockpiles'
own `apparel`, `weapons`, `dump:worn`, `dump:rotten` and `dump:corpses`,
`meals:<roomID>` and `rawfood:<roomID>`. A role that publishes `Fixed` keeps
the size it was sited at: never grown or merged, and shrunk only to its
site's size.

The room-bound roles (#917) are created whenever their room stands without
one, whatever other goal is in deficit. `meals:<roomID>` (#872, #936) is a
Critical meal stockpile where meals keep near the table, best first: the
standing meal closet, zoned whole for every meal; a 2x2 of every meal in the
standing planned freezer at its door into the standing planned dining room;
else, while the colonists eat at least 3 meals a day (their nutrition need
over 0.9), one cell of the one meal the colony cooks (`ObservedMealTier`) on
the free roofed cell nearest the census dining table, off its adjacent
cells. With none of these the role retires and its zone is deleted.
`rawfood:<roomID>` (#722) is a 2x2 Critical stockpile of raw meat and raw
plant food (never rotten) in the standing planned freezer, nearest its door
into the kitchen. RimWorld renumbers rooms, so a room counts as served when
any zone of the role's prefix has a cell in it; a zone of the prefix with no
cell in the site's room is deleted (the site moved), and one larger than the
site shrinks to it, keeping its stocked cells.

The v2 plan (#936) puts the dining room against the freezer's free side
wall with a door between them; a dining room with no freezer door gets a
2x2 (else 2x1) meal closet behind its back wall, its only door in that
wall and a cooling exhaust reservation like the freezer's and the tomb's.
MaintainRefrigeration shells the closet once the dining room stands and
coolers are available, then cools it to freezing like a filled tomb; its
cooler vents from the planned exhaust site, or `ventedWall`, never into the
dining room.

Built shelves (#721) inside an owned zone are MaintainStockpiles' too: a
shelf whose last applied patch differs from its zone's desired settings
(never patched: native defaults) is patched like the zone, role
`shelf:<buildingID>`, ranked with retargets and costing three hauls per
shelf cell.

The fixed roles (#724) are created when the colony has things for one and
no zone of it: serviceable stored apparel (apparel), unbiocoded weapons by
trade on the map (weapons), poor stored apparel and worn-out garments on
pawns (`dump:worn`, Low), spoiled items and rotting animal corpses
(`dump:rotten`, Low), unburied humanlike corpses (`dump:corpses`, Low). A
gear stockpile takes the free indoor roofed 2x2 patch cheapest to haul to
from the general store (`policy.RankSitesByHaul`); a dump takes the nearest
free outdoor 2x2 patch six cells clear of any living room
(`policy.OutdoorDumpSites`), never while the room census is unknown. A
create ranks after retargets and before grows; its hauls are the things
waiting for it.

`RoutineStockpilePlanner` commits the edits as one plan per cycle, each
action under its target's fresh CAS token: `zone_cell_edit`,
`stockpile_patch` (zone or shelf) or `zone_delete`. A create is a native
zone preview and a `zone_create` method admitted alone, once no other edit
stands. Each is a `layout`/`stockpiles` clock event.

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
or an integer-membership ellipse (`domain.EllipseInterior`) — whose parameters,
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
standing in Home are read, with no row cap (#320);
an unreadable scan is unavailable, never sampled. The Go observation treats an explicit
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
shrine; overflow or an unreadable scan is unavailable, never
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

Clearance is the `cover_clearance` action (`CoverIntent` on Actions/Apply):
one exact thing by identity and cell with the designation its kind takes (`CutPlant`,
`Haul`, `Mine`, and `Deconstruct` on an unowned building such as a ruin wall;
player-owned cover is held as `structure`).
The native side re-checks presence, cell, fog, fill, forbiddance, an existing
designation, roof and mining safety, a store for a chunk, the designator's
own acceptance (its refusal text is surfaced), a reachable free colonist with
the work type when it applies; the same designation already standing applies
again. `CutPlant` on a harvestable
tree designates `HarvestPlant` (chop wood), since the cut-plants designator
refuses harvestable trees; other plants take a forced `CutPlant`. Applied is
terminal; ordinary work removes the thing, and the census skips a designated
cover. The defense layout
planner orders up to eight clearances per method once every tier stands, at
most four methods per game day.
