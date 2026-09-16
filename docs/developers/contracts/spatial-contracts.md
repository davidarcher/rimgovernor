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

The first shelter's shape follows the colony's observed player faction tech
level: a Neolithic colony tries the hut templates (circle r4, ovals 3x5 in the
four orientations, circle r3) at each candidate centre before the 9x9
rectangle; any other tech level, or an unknown one, keeps the rectangle. When
no template fits the lit free ground near the anchor, the routine grows a
connected footprint from the nearest free seed over cells whose eight
neighbours are all free, stopping at the rectangle's 49 interior cells or
sooner when the terrain runs out (minimum nine), and walls its ring; that is
how narrow connectors and concave rooms around rock arise. Site score,
reserved yard and indoor storage placement are computed from the footprint,
not a fixed rectangle.

Resuming control, like any clock interruption (a letter pause), invalidates
every routine goal and cancels its plans, and the executor cancels the
cancelled plan's native blueprints and frames; the walls and door already
completed natively are then the only durable record of a shell in progress.
Before siting a shell, the routine reads the player wall and door census
within 64 cells of the colony centre and, for each player door nearest the
centre first, tries every starter shape whose south door lands on that cell
(the hut templates for the hut style, then the 9x9 rectangle). A shape is
adopted when the door stands on it and every other ring cell either stands
already or is placeable now; the admitted plan holds only the missing cells,
and its walls do not wait for a door that already stands. A blocked ring
cell or a grown irregular shell (which has no template) is not adopted and
the routine sites afresh.

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
