# Why space and resources need fresh checks

[Documentation](../../README.md) · [Plans and Hands](plans-and-hands.md)

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

Room functions are the game's own `Room.Role`, read from the typed room census;
the controller never assigns a role, it only observes which one the game scored.
The facility catalog (`policy.FacilityCatalog`) is the per-role matrix over every
installed RoomRoleDef: which roles a planner pursues, which other roles may host
the same function as a shared room, and the furniture that gives the room its
role. Each implemented role follows one ladder — reuse a room the game already
scores as hosting the function, then furnish an existing hosting room, then stage
a starter shell and furnish it once roofed. Dining, recreation, workshop and hospital
are the implemented rows; every other role is an explicit pending row, and
content-gated roles are pursued only when their definitions exist in the
planning census.

The workshop row is the first production facility on that ladder. A
`MaintainResource` deficit that no existing bench can produce first discovers,
from the native recipe catalog, which player-buildable bench definitions host a
research-available recipe for the resource; the first candidate the planning
census reports available, unpowered and buildable without construction skill is
furnished into a Workshop-hosting room (a Workshop, a generic Room, or the
starter shell once the sleeping spots have made it a Barracks — the bench keeps
working there and a second ring would split the same builders), or a starter
shell is staged when no such room exists. Powered and research-gated benches are reported as an explicit
`workshop_bench_unavailable` prerequisite rather than staged. Once a bench with
the recipe exists the workshop planner steps aside and the resource goal's bill
path, worker coverage for the bench's own work type, and native readback of the
rising item count carry the deficit to recovery.

The hospital row is a hosted function rather than a room of its own: the game
scores a room as Hospital only when every bed in it is medical, so a colony's
first medical bed stands in a Bedroom, Barracks or generic Room, and the
catalog lists those as its hosts. Under a `MaintainMedicalCare` deficit the
ward is sized by the living colonists who should seek medical rest (a bad
condition such as a scar keeps the deficit but asks for no bed), counted
against the medical, humanlike, non-prisoner beds in hosting rooms. Short of that count it flags an
existing hosted bed medical through a one-shot CAS-gated `bed_medical` patch
(an unowned bed first, then a bed only patients own — the game drops the owner,
who then rests there as a patient; a healthy colonist's bed is never taken),
and only when no bed can be spared does it walk the same ladder as the other
rows, furnishing a bed or sleeping spot into a hosting room or staging the
starter shell, converting the new bed on a later review. Tending, rescue, the
medicine reserve and doctor coverage stay their own families; the bed patch
completing never clears the deficit.

The first shelter's shape is chosen from the native player-faction tech level:
Neolithic colonies raise a circular or oval hut, others a 9x9 rectangle, and
constrained terrain grows a connected irregular footprint when no template fits
(see the room footprint contract in [spatial contracts](../contracts/spatial-contracts.md)).
Pausing and resuming control keeps the routine goal and its shell plan, so a
restart mid-construction simply waits on the open plan. Only a world change
(load token, map, tick rewind) invalidates the goal; the successor then
recognises the half-built shell from the walls and door standing natively and
reissues only its missing cells rather than siting a second shell.

Digging into a mountain is chosen, not configured. Rock holds its own roof and
costs no wall material, so a verified rock face within reach of the colonists beats
the wooden starter shell deterministically. Fog is the reason excavation is staged:
the game only reveals rock as neighbouring rock is cleared, so each stage designates
what pawns can currently see and reach, and the next stage is planned from the
geometry they actually exposed. Support is a property of the whole removed set, not
of one cell, which is why the site read evaluates the counterfactual removal and why
an unknown verdict holds the project instead of failing it.

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
production bill. Any ordinary recipe whose products are all items may carry such a
bill, not only food; the pawn work type a bill needs is the type of the
`WorkGiver_DoBill` giver serving that bench (`RecipeState.work_type`), and native
admission requires an assigned pawn with that type enabled and the recipe's skill
floors. Open bills contribute that work type to deterministic work coverage alongside
construction. Existing bills count as continuing capacity only when their settings
cover the requested target. Player edits remain authoritative.

Material development follows the same distinction. Safe surface deposits lead to
bounded mining and exact-resource storage. Explicitly approved deep extraction stages
researched equipment at observed powered sites through shared construction commitments.
Completed drills remain subject to native stock limits, worker eligibility and the
game's infestation rules. A depleted seam leads to a fresh site observation; a cancelled
or removed facility requires renewed player direction. See the
[extraction contracts](../contracts/mining-contracts.md).

These plans do not create resources. Actual output, material consumption and remaining
stock need native readback; a bill on an ordinary item recipe completes only when the
native placement observation counts the produced items. This is why production acceptance tests wait for pawn work
rather than declaring success when a bill is accepted.

The relevant source is [go/internal/policy](../../../go/internal/policy) and
[go/internal/store](../../../go/internal/store). Use [spatial
contracts](../contracts/spatial-contracts.md) for exact geometry bounds and [the Go player
API](../contracts/go-player-api.md#colony-configuration) for resource-policy
semantics.
