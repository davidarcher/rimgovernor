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

## Resource demand and acquisition scoring

`policy.BuildResourceDemand` combines MaintainResource stock targets, the output
of `EconomicReserves`, and remaining planned construction bills of materials.
Overlapping stock targets use their maximum; construction costs are additive.
Do not supply a commitment twice through both economic floors and planned costs.
Exact-stuff requirements consume usable stock before definition-wide requirements;
each stock unit is counted once. Unknown stock leaves demand unknown. Estimated
acquisition yield is not inventory. Priorities are 1–100, higher first; economic
floors default to 1 and a merged row retains its highest priority.

`RankResourceCandidates` is the shared pure scoring contract for loot, salvage
and mining integrations. It values only unmet demand that fits observed destination
headroom, weighted by priority and native unit value. Path distance, total source
labor and rounded hauling trips reduce the score. Unknown cost facts withhold a
candidate; unknown storage supplies no headroom. Higher-priority urgent work
(`policy.RemoteCompetition`: an urgent patient or a disrupting disaster) holds
acquisition, while an unrelated routine deficit does not. Positive scores sort by
score descending, then kind and stable source ID; input order cannot break ties.
These are alternatives, not a batch allocation or permission to dispatch. Consumers
must bound selected work and refresh demand, reach, native safety and storage before
using the existing concerns and Hands path. Remote loot consumes it through the
[supply safety filter](../contracts/controller-contracts.md#remote-loot-and-resource-reach).
Surface mining uses the same reach ceiling and one-rock demand batches
([mining contract](../contracts/mining-contracts.md)); salvage reads the
[upkeep contract](../contracts/upkeep-contracts.md). All three report the same
[explicit holds](../contracts/controller-contracts.md#remote-work-holds-and-resume)
and revalidate safety at dispatch.

## Material runway

`MaintainResource` reviews Steel and ComponentIndustrial over the last 15 game
days of journal history (at least one day). No consumption is charged to that
window: a bill intent's receipt is terminal, so nothing observes what its
iterations spent, and the rate reads zero. A deficit is then stock below
the reserve floor.

Usable stock is stock above the larger configured acquisition/reserve floor.
`stockDays` divides usable stock by daily consumption; `daysLeft` also includes
the mining census's safe open-surface ore. Unknown ore is not zero, and a zero
consumption rate has no finite days-left value. Below five days, the review
raises a maintenance deficit and an ordinary `MaintainResource` target covering
five days plus the existing floor (bounded by the target limit of 10,000).
Existing resource methods consume that target; the forecast issues no orders.

A component deficit uses the ordinary workshop prerequisite ladder to stage a
fabrication bench. Once the native MakeComponent recipe is research-available
and usable on that bench, the resource production method proposes a StockTarget
bill. Its target is capped at current components plus one per 12 surplus steel,
retaining the Steel reserve and five days of observed consumption in stock.
The budget uses the lower of fresh colony and usable ingredient stock; ore is
never spendable steel. Unknown consumption or stock blocks fabrication, and
an existing active component bill prevents a duplicate.

The durable review retains both materials' stock, ore, rate, window, reserve,
target and deficit. `/api/routines` exposes them as `resourceRunways`, with
unknown values represented as null. The API is independent of launcher
refreshes. History is scoped to the
colony, load and map, includes retired/player plans, ignores future events,
and counts each placement or completed bill only once.

## Development uses observed room functions

Shelter, food services and later capacity share the same maintained concerns. A
phase advances when native indoor sleeping capacity and service gates verify its
function; issuing construction cannot advance it. Growth can reopen shelter work.
Future phases reserve no land. A player can adopt an inspected existing room,
including an irregular shelter, and furnish it while preserving connected access.
Repairs use normal construction before the room can count as habitable.

Room functions are the game's own `Room.Role`, read from the typed room census; the
controller never assigns a role. Each planner pursues a role through the facility
ladder (reuse, furnish a hosting room, stage a starter shell and furnish it once
roofed); the ladder and the per-role matrix (`policy.FacilityCatalog`) are in
[facilities](facilities.md).

The workshop row is the first production facility on that ladder: a
`MaintainResource` deficit that no existing bench can produce discovers, from the
native recipe catalog, which buildable benches host a recipe for the resource.
The bench is furnished into a Workshop-hosting room (a Workshop, a generic Room, or
the starter shell once sleeping spots have made it a Barracks, since a second ring
would split the same builders), or a starter shell is staged when no such room
exists or the only room is too full. Research-gated and unbuildable benches are
explicit prerequisites. The full ladder, reason codes and per-role matrix are in
[facilities](facilities.md).

Stone blocks ride the same ladder without the operator naming the stone:
the default floor `policy.DefaultStoneBlockTarget` (150) is kept for the block definition of
whichever Core stone the reachable chunk census counts most
(`policy.StoneBlockTarget`), merged into the default resource targets each
review and planner step (`RoundsPolicy.EffectiveResourceTargets`). The
ladder then researches Stonecutting, stages a stonecutter's table in the
Workshop room and keeps a do-until bill on it fed from the map's chunks —
the block supply `MaintainStoneShell` and the stone flooring and defense
tiers spend. A map without stone chunks derives no target; the ladder does
not mine rock for chunks. The chunk census that funds the bill counts every
stack: a `list_supplies` stock row's units and ownership buckets cover all
the definition's things even when the map strews more than 256 of them,
and only the listed `items` (with `holders` and `corpses`) are then a
256-entry prefix, flagged by `items_completeness` (`page.complete=false`,
`matched` the true count) for the per-cell readers that need each entity.

The hospital row is a hosted function rather than a room of its own: the game
scores a room as Hospital only when every bed in it is medical, so a colony's
first medical bed stands in a Bedroom, Barracks or generic Room, and the
catalog lists those as its hosts. Under a `MaintainMedicalReserves` care-phase deficit the
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

The bedroom row works the same way for `MaintainHousing` (the `sleeping`
family): a colonist without an owned suitable bed is first assigned a vacant
one through the typed `assign` operation, and only when nobody can be
assigned is one bed staged in a Bedroom-hosting room (Bedroom, Barracks or
generic Room) whose observed temperature lies inside the comfortable band of
every colonist still unhoused, one bed per method, assigned on a later review.
The bed is the best rung available: `Bed`, else a `Bedroll` built from
the first native stuff option the colony has in stock (a couple's `DoubleBed`
or `BedrollDouble` first); a bedroom furnished for a spot owner falls back to a
`SleepingSpot`. A bedroll is a suitable bed only while `Bed` is
unavailable and a spot never, so spot owners walk into bedrooms first; neither the
assignment nor the construction receipt clears the deficit, only the
colonist's observed sleep in the owned bed does.

Every colony's first shelter is a 9x9 rectangle; when it does not fit, a concave L or a
two-chamber connector template wraps the obstacle, and only then does
constrained terrain grow a connected irregular footprint
(see the room footprint contract in [spatial contracts](../contracts/spatial-contracts.md)).
A shell whose materials run out mid-build simply holds: dispatched wall
orders wait for stock with no attempt timeout, and the review neither sites
a second shell nor reissues an order while the plan is live.
Pausing and resuming control keeps the routine concern and its shell plan, so a
restart mid-construction simply waits on the open plan. Only a world change
(load token, map, tick rewind) invalidates the concern; the successor then
recognises the half-built shell from its own earlier shell plans and the
walls and door standing natively (whatever its shape, template or grown) and
reissues only its missing cells rather than siting a second shell.

Digging into a mountain is chosen, not configured. Rock holds its own roof and
costs no wall material, so a verified rock face within reach of the colonists beats
the wooden starter shell deterministically. Fog is the reason excavation is staged:
the game only reveals rock as neighbouring rock is cleared, so each stage designates
what pawns can currently see and reach, and the next stage is planned from the
geometry they actually exposed. Support is a property of the whole removed set, not
of one cell, which is why the site read evaluates the counterfactual removal and why
an unknown verdict holds the project instead of failing it. What the fog reveals is
reviewed, not assumed away: an obstacle inside the room is kept and dug around, an
obstacle in the way in or a lost roof holder ends the project, and the shelter is
re-sited from the geometry that remains.

## Reservations prevent competing promises

At admission, material accounting considers accepted commitments together. At dispatch,
ready work is considered in a stable order against current stock. Unissued work reserves
its native costs; issued blueprints and frames contribute their native deficits instead
of being counted a second time.

Building admission never checks stock: RimWorld places blueprints regardless
and the frames hold natively for materials, which the upkeep concerns then read as
a deficit. Admission guards geometry, safety and freshness only. The native
production path checks actual recipe ingredients and consumption. A controller-side
stock estimate alone could not protect a reserve once an ordinary bill begins consuming
a different permitted ingredient.

## Active plan commitments bound what a step can claim

Planners that return proposals do not commit anything
themselves: the step's coordinator ranks the wave's proposals by (planner
priority, concern urgency, proposal ID) and checks each one's pawn, entity and
quantity claims before its commit runs. Quantities are checked against the
stock the rounds observed less what earlier proposals in the step
claimed and less the `ActivePlanCommitments` view (`store.LoadPlanCommitments`):
for every admitted plan bound to an active concern, the admitted costs of its
next work segment, the actions whose prerequisites have completed in the
current world or that are already dispatched. Costs still waiting on a
prerequisite are the plan's remainder and are exposed as demand, never held,
so a long project cannot deadlock development by reserving everything it will
eventually need. A held quantity expires one in-game day after its latest
evidence without a dispatch and joins the demand; a dispatched order holds
until it settles natively. The view is derived from the plan, admission and
progress rows each step, so consumption, cancellation, generation changes and
lost work release a commitment through the rows that record them, and nothing
is stored beside them.

A proposal the free stock cannot cover is refused as demand with its
shortfall on the step row, unless less urgent commitments can release it: a
strictly more urgent proposal retires the smallest set of undispatched,
less urgent plans that covers the shortage through `store.PreemptMethod`
(the ordinary cancellation rows, as a recovered concern's undispatched methods
retire), and claims what they held in the same step. The preempted concern stays
active and its planner re-evaluates it at the next review. Dispatched work is
never preempted. Commitments are a software view: dispatch revalidates every
order against the live game, and the admission path beneath each commit still
checks its own stock and reservations.

## Production capacity is not current stock

A resource concern may designate mining or harvest work, or configure an ordinary
production bill. Any ordinary recipe whose products are all items may carry such a
bill, not only food; the pawn work type a bill needs is the type of the
`WorkGiver_DoBill` giver serving that bench (read from the definition catalog's
`WorkGiverDef` rows, with the recipe's products, ingredients and skill floors), and native
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
contracts](../contracts/spatial-contracts.md) for exact geometry bounds and the [durable policy
contract](../contracts/durable-policy.md) for the autopilot-owned production
policy.
