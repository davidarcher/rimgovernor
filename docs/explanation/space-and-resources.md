# Why space and resources need fresh checks

[Documentation](../README.md) · [Plans and Hands](plans-and-hands.md)

A valid proposal can become invalid before its next order is issued. Pawns consume
materials, players edit zones, and completed buildings change the map. The shared plan
therefore validates both when it accepts work and when Hands dispatches it.

## A plan describes intent, not ownership of the map

Room templates propose useful geometry, but native definitions determine actual
footprints, costs and legal placement. Existing buildings, zones, doorways and walkways
remain constraints. Once an action is complete, its historic geometry does not
permanently reserve those cells against later player changes.

Consider a player who adds a growing zone inside a planned room between two construction
batches. Continuing from the original empty-map observation would ignore the player's
change. A fresh zone census lets dispatch refuse the now conflicting work while
retaining the receipts from the first batch.

## Local connectivity is a bounded claim

Spatial checks consider room interiors, entrances and nearby exterior access. They
reject known obstructions and unknown geometry rather than certifying a route from
incomplete data. A local connected patch still does not prove that a particular pawn can
reach it from anywhere on the map.

The distinction helps interpret tests. A fixture can prove that the doorway algorithm
rejects a blocked cell. Only native gameplay can establish that a pawn actually
traversed a route and completed the intended work under those conditions.

The native access audit extends this check to each mobile pawn's current safe
map component, including its allowed area and door-opening eligibility. Projected
walls cannot cut off previously reachable space, even when the room's original
construction actions have been archived. The projection uses completed native
definitions, so an unfamiliar wall is still an obstruction. It does not predict
future danger or certify that a pawn will perform the work.

## Development uses observed room functions

Shelter, food services and later capacity share the same maintained goals. A
phase advances when native indoor sleeping capacity and service gates verify its
function; issuing construction cannot advance it. Growth can reopen shelter work.
Future phases reserve no land. A player can adopt an inspected existing room,
including an irregular shelter, and furnish it while preserving connected access.
Repairs use normal construction before the room can count as habitable.

## Reservations prevent competing promises

At admission, material accounting considers accepted commitments together. At dispatch,
ready work is considered in a stable order against current stock. Unissued work reserves
its native costs; issued blueprints and frames contribute their native deficits instead
of being counted a second time.

Player reserves and spending policies further constrain what can be used. The native
production path checks actual recipe ingredients and consumption. A controller-side
stock estimate alone could not protect a reserve once an ordinary bill begins consuming
a different permitted ingredient.

## Production capacity is not current stock

A resource goal may designate mining or harvest work, or configure an ordinary
production bill. Existing bills count as continuing capacity only when their settings
cover the requested target. Player edits remain authoritative.

Material development follows the same distinction. Safe surface deposits lead to
bounded mining and exact-resource storage. Explicitly approved deep extraction stages
researched equipment at observed powered sites through shared construction commitments.
Completed drills remain subject to native stock limits, worker eligibility and the
game's infestation rules. A depleted seam leads to a fresh site observation; a cancelled
or removed facility requires renewed player direction. See the
[extraction contracts](../reference/mining-contracts.md).

These plans do not create resources. Actual output, material consumption and remaining
stock need native readback. This is why production acceptance tests wait for pawn work
rather than declaring success when a bill is accepted.

The relevant source is
[resource_accounting.py](../../controller/rimgovernor/resource_accounting.py),
[construction_preflight.py](../../controller/rimgovernor/construction_preflight.py) and
[production_policy.py](../../controller/rimgovernor/production_policy.py). Use [spatial
contracts](../reference/spatial-contracts.md) for exact geometry bounds and [command
contracts](../reference/command-contracts.md#provenance-and-resource-policies) for
resource-policy semantics.
