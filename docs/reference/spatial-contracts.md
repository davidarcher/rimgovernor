# Spatial contracts

[Documentation](../README.md)

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
is not atomic with Python validation.

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

Read [space and resources](../explanation/space-and-resources.md) for why planned
geometry is rechecked.
