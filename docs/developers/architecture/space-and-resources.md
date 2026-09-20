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
using the existing goals and Hands path. Remote loot consumes it through the
[supply safety filter](../contracts/controller-contracts.md#remote-loot-and-resource-reach)
(#522). Surface mining uses the same reach ceiling and one-rock demand batches
([mining contract](../contracts/mining-contracts.md)); salvage reads the
[upkeep contract](../contracts/upkeep-contracts.md). All three report the same
[explicit holds](../contracts/controller-contracts.md#remote-work-holds-and-resume)
and revalidate safety at dispatch.

## Material runway

`MaintainResource` reviews Steel and ComponentIndustrial over the last 15 game
days of journal history (at least one day). Placed construction counts its
admitted material cost once; completed bills count observed iterations against
native recipe quantities. Ambiguous ingredients and older bills without this
evidence leave that material's rate unknown. This estimates controller-recorded
consumption, not every material transfer in the colony.

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
unknown values represented as null. The API is independent of dashboard
refreshes; React presentation is a separate change. History is scoped to the
colony, load and map, includes retired/player plans, ignores future events,
and counts each placement or completed bill only once.

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
recipe for the resource; the first candidate the planning census reports
available and buildable by a builder the colony has (unpowered first, powered
once a generator definition is buildable) is furnished into a Workshop-hosting
room (a Workshop, a generic Room, or the starter shell once the sleeping spots
have made it a Barracks — the bench keeps working there and a second ring would
split the same builders), or a starter shell is staged when no such room
exists or the only room is too full to site the bench (one sleeping spot per
colonist can fill the starter hut). A bench gated only by research records its projects as the derived
`EnsureResearch` target (`workshop_research_needed`); one needing a skilled
builder, or power no generator can supply, is an explicit
`workshop_bench_unavailable` prerequisite. Once a bench with the recipe exists
the workshop planner steps aside: an allow-list stockpile for the recipe's
ingredients is placed in the Workshop room, and the resource goal's bill path,
worker coverage for the bench's own work type, and native readback of the
rising item count carry the deficit to recovery. The full ladder and the
per-role matrix are in [facilities](facilities.md).

Stone blocks ride the same ladder without the operator naming the stone:
`--routine-stone-block-target N` is a floor for the block definition of
whichever Core stone the reachable chunk census counts most
(`policy.StoneBlockTarget`), merged into the operator's resource targets each
review and planner step (`RoutinePolicy.EffectiveResourceTargets`). The
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

The bedroom row works the same way for `MaintainSleeping` (the `sleeping`
family): a colonist without an owned suitable bed is first assigned a vacant
one through the typed `bed_assign` operation, and only when nobody can be
assigned is one `Bed` staged in a Bedroom-hosting room (Bedroom, Barracks or
generic Room) whose observed temperature lies inside the comfortable band of
every colonist still unhoused, one bed per method, assigned on a later review.
A sleeping spot is never suitable and never staged here; neither the
assignment nor the construction receipt clears the deficit, only the
colonist's observed sleep in the owned bed does.

The first shelter's shape is chosen from the native player-faction tech level:
Neolithic colonies raise a circular or oval hut (eight templates down to a
low 2x6 oval), others a 9x9 rectangle; when neither fits, a concave L or a
two-chamber connector template wraps the obstacle, and only then does
constrained terrain grow a connected irregular footprint
(see the room footprint contract in [spatial contracts](../contracts/spatial-contracts.md)).
A shell whose materials run out mid-build simply holds: dispatched wall
orders wait for stock with no attempt timeout, and the review neither sites
a second shell nor reissues an order while the plan is live.
Pausing and resuming control keeps the routine goal and its shell plan, so a
restart mid-construction simply waits on the open plan. Only a world change
(load token, map, tick rewind) invalidates the goal; the successor then
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

Player reserves and spending policies further constrain what can be used. A shell
(the initial shelter, expansion, a power shelter, the pen ring) is admitted as
`Shelter` work without a stock check: RimWorld places its blueprints regardless
and the frames hold natively for materials, which the wood upkeep then reads as
a deficit; only a spending policy or an operator reserve on the resource keeps
a shell unadmitted, at admission and again at dispatch (the purpose is recorded
with the admission). Furnishing and facility methods keep the stock budget, since
their open frames would strand hauling, and admit the candidates the stock covers
rather than refusing the whole method for one short of it; the rest stay pending
without a reservation and the worker admits each afresh when the census covers
it. The native
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
