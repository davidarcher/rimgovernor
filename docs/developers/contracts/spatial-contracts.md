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
door is placed first, then walls in north-facing, z-outer/x-inner order, so a
door dependency on placement zero holds for any shape. Rectangles keep their
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

The first shelter's shape follows the colony's observed player faction tech
level, in deterministic tiers. A Neolithic colony tries the hut templates
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
holds only that shape's missing cells; its walls do not wait for a door that
already stands, and a ring whose door was cancelled is reissued door first
with the walls gated on it as a fresh shell would be. When a missing cell of
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

## Site selection and development

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
corridor; faces then rank by distance as before. Indoor furnishing, here and everywhere, skips cells the planning window reports no
colonist can reach: a pocket sealed off behind walls is a room to nobody. Every
target cell must be visible rock
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

## Related reading

Read [space and resources](../architecture/space-and-resources.md) for why planned
geometry is rechecked.
