# Colony upkeep contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

Upkeep uses the existing ColonyPlan, resource admission and Hands executor.
`home/colony_facts.upkeep` is versioned read-only native evidence. Each section is
independently nullable with an error; an empty successful census differs from an
unavailable read. The section tick must match the enclosing observation.
The repair structure census includes only native definitions with `useHitPoints`.
Non-damageable markers such as sleeping spots cannot become repair targets.

## Native capability audit

| Need | Available native evidence and action boundary |
| --- | --- |
| Supplies | Per-item identity, location, quantity, condition, deterioration stat, roof, valid storage, rot deadline and forbidden status. `home/order` hauling previews native storage access. `requireSafeStorage` rechecks enabled hauling, safe reach and covered storage during dispatch. |
| Storage | Existing slot cells, roof, occupancy and item-specific native filtered capacity. Native hauling separately decides worker access and delivery; cell count alone is not usable capacity. |
| Sleeping | Bed definition, slots, owners, current users, pawn-specific access, roof and temperature. Capacity does not establish actual use or suitable worn protection. |
| Home and structures | Exact occupied/protected cells with home coverage; building condition, material, roof and native construction lineage. `home/roof_support` checks existing roof connectivity with one exact wall excluded. This does not prove enclosure, escape routes or replacement admission. |
| Fire, cleaning, repair | Exact native targets and condition. `home/order` repair/clean use the installed WorkGivers and their normal eligibility. These methods require current home coverage, safe access and enabled work. The installed firefighting WorkGiver is not directly orderable: enabled workers respond normally while the controller watches at most three home fires of size at most one. |
| People | Current rest, recreation, mood, worn apparel condition and native comfortable temperature range. `home/list_pawns` supplies medical, work, settings and schedule reads. |
| Animals | Owned animal census, diet and food need; native rope-management eligibility, current enclosed pen and suitable pen identity. Pets have no pen-containment predicate. Reachable stored feed follows native eating eligibility and allowed-area access; it excludes drugs and does not count pasture or future harvest. Existing combined-demand food forecasts account for animal shares separately. |
| Medicine, season, power | Medicine is identified through native item definitions. Existing forecast/status tools supply patient, crop and power inputs; no future production or season is credited as stock. |

## Maintained jobs

`MaintainHomeCoverage` joins native construction lineage and confirmed stockpile
IDs to bounded facility geometry. A building may include adjacent fully roofed
rooms of at most 128 cells; the complete target is limited to 256 visible cells.
Stockpiles require their unchanged committed footprint. Missing geometry remains
unknown. Existing omissions when observation starts and subsequent player Home
removals are saved as exclusions, including Clear and Invert operations.
`home/upkeep_home` derives cells again and requires the same geometry hash and
area revision before adding Home. It does not paint arbitrary terrain or change
pawn allowed areas. Actual native Home cells establish completion; player
exclusions retain a visible blocker. Uncertain writes cannot be replayed.

Native construction lineage follows bridge-created blueprints into frames and
finished buildings, including the game's failed-construction blueprint recovery.
Blueprint material comes from its native intended-material field. A finished
identity is captured during the native frame completion call; coordinate matching
alone cannot transfer ownership. Records persist with the game and report missing
or ambiguous identities explicitly. Missing identities after load are not rebound
by coordinates. The bounded ledger retains at most 4,096 origins.

`construction_ownership.owned_buildings` additionally requires a confirmed
autonomous plan placement and native completed building with matching definition, material, rotation
and location. Reused player blueprints, player goals, cancelled goals and uncertain
receipts confer no autonomous ownership. Lineage is evidence, not authorization to
deconstruct a structure or an assurance that removing it is safe.

The roof-support preview checks connected existing roof cells within the installed
native support radius while excluding the specified wall as a holder. It changes
no roof or building. Fog, map-edge uncertainty, pending collapse and unsupported
cells refuse the certificate. Planned supports earn no credit. Replacement must
also preserve enclosure and escape access and repeat safety checks at execution.

`MaintainStoneShell` admits one wall upgrade after urgent needs. Only
confirmed autonomous construction can supply a demolition target. A complete
straight-wall bundle reserves three stone backup walls, guarded removal of the
original wall, one permanent stone wall, and guarded cleanup of each backup. The exterior cells
must be empty, side walls unchanged and the original interior enclosed and roofed.
Native spatial preflight preserves existing access. Installed material costs and
shared reservations cover all four walls before demolition; runtime estimates
cannot overwrite validated native allocations. Stone acquisition uses ordinary
resource recipes and can build one native stonecutter when a suitable site exists.

Observed native demolition transfers its original project slot to the exact
replacement construction reference. The old project waits for that replacement;
verified completion satisfies the transferred slot, and replacement loss reopens
it. Other slots retain their own native completion and ownership checks. This
handoff releases only the retired slot's planned footprint; edits to its action
or removal dependency invalidate the exemption. Completed removal references
remain readable through the shared immutable action archive. Furniture placement preserves
existing stockpile cells.

Site selection requires the support-check radius around every temporary wall to
be visible and within the map, so fog cannot be deferred until cleanup. Execution
still repeats the full support check against current native roofs and buildings.

`EnsureDefensiveLayout` (opt-in) commits one stored corridor layout per colony
and builds it tier by tier; a tier is `Built` only while every one of its
buildings is observed standing in the defense-site census. A `Complete`
record is re-verified after each ActiveCombat epoch and once per game hour of
simulation: a tier that lost a building (a breached wall, a sprung spike trap,
which is destroyed on springing) re-opens with a fresh retry budget and is
re-admitted as a new tier method, while `Complete` stays true so combat keeps
holding the line on the proven geometry. A missing building whose cell already
carries a blueprint or frame (the game's own trap auto-rearm) is not placed
again; the planner asks for a clock window so native construction finishes
it. Defenders are undrafted by ordinary draft cleanup once the recovered
ActiveCombat goal stops authorizing the hold plan.

Corners instead require verified existing roof support and both neighboring walls,
with clear exterior cardinal approaches reachable by enabled haulers. Only the
permanent stone wall is reserved. Those approaches remain open so ordinary hauling
can remove deconstruction salvage: native diagonal access to a wall does not grant
the same access to loose items. The enclosed interior and its construction approach
remain available; stonecutter placement preserves that approach.

`home/upkeep_wall` creates an ordinary native deconstruction designation. Completion
comes from the actual native deconstruction job, not disappearance of a wall. The
guard rechecks exact supporting identities, enclosure, roofs, remaining materials
and resource policies before completion. Jobs require active supervised simulation.
Native UI input, a load/map change or changed safety invalidates pending
demolition; Manual suspends it until control resumes. Player replacement of a designation relinquishes controller ownership.
Cleanup requires the completed permanent wall. Missing or uncertain outcomes stay
blocked. Stopping automation removes only controller-owned pending demolition
designations. Native evidence must confirm retirement, the surviving exact target
and the absent designation before the shared plan cancels unissued descendants.
Issued construction remains under observation. Retained cancellation history prevents
duplicate replacement; existing backup walls remain intact. Blocked demolition is
retired before further supervised simulation. Interrupted recovery beyond this safe
retirement and wider native acceptance remain listed in the backlog.

`SecureSupplies`, `MaintainEssentialRepairs`, `MaintainCleanFacilities` and
`MaintainFireSafety` retain their goal identities across recovery and recurrence.
Any observed target enters maintenance; recovery requires no remaining deficit
and no unresolved issued action. Missing evidence retains active risk, while a
first unavailable read creates an ordinary visible blocker rather than an invented
emergency. Fire risk preempts development. Unknown or oversized fire intervention
retains an emergency hold.

The typed item census (at most 256 rows) covers items in the home area, items
in valid storage anywhere, and deteriorating, perishable or medicine stacks
wherever they were dropped; a map's natural chunk and slag field outside the
home area never enters it, since a whole-map census exceeded the bound on every
real map and no upkeep goal may target that debris.

Methods inspect at most eight targets and eight enabled, available workers in a
review. Stable target and pawn IDs break ties. Medicine and rot deadlines rank
hauling; native medical beds, temperature controls and generators lead repairs,
followed by roof holders, beds and worktables. Relative damage ranks each category
before cosmetic repairs. Cleaning targets only filth inside a
workspace (enclosed kitchen, hospital, laboratory, or any enclosed room with a
cooking bench) whose native room Cleanliness stat has latched dirty (enter below
-1, release at -0.25; the per-room latch, keyed by the room's lowest cell
since native room IDs change on every region rebuild, and its entry tick
persist in `routine_review`), and only after a 30,000-tick grace with a Cleaning-enabled
colonist present or at once with none. A clean order is player-forced: any
colonist not incapable of Cleaning carries it whatever their Work-tab priority
says, and the native worker cleans the ordered filth plus whatever the
installed WorkGiver queues beside it. The typed filth census covers the home
area only (at most 256 rows), the only filth an upkeep order may target. Filth
outdoors, in other rooms, and in
inherently dirty rooms (barns, rooms holding a butcher bench) is ordinary
colonist work. Butcher placements never enter a cooking bench's room and cooking
placements never enter a butcher bench's room; a colony whose every butcher bench
shares a cooking room is admitted one more `ButcherSpot` outside; the butcher
bill waits until that `butcher-spot-separated` method has been tried (admitted,
completed or failed) and then prefers the separated bench, so a forever bill on
the shared bench never holds the food-supply goal open. Methods preserve forbidden items, player work
overrides, schedules, drafts, existing player-forced jobs, storage filters and home
areas. Native cleaning eligibility determines whether fresh filth can be worked;
the controller does not encode a filth-age threshold. Deterioration and growing
fires do not reset the progress watchdog. A newly oversized fire retains a hold
even when native firefighting was already underway. Fire monitoring uses Normal
speed and at most 60 game ticks between reviews; unavailable safe workers retain
an emergency hold.

Blocked upkeep reconsiders changed target eligibility, worker availability, research
and player overrides immediately. Position, rot-timer and temperature drift alone
do not reopen a failed method. A 2,500-tick review window catches changed capacity
or routes without retrying every observation; outstanding receipts and watchdog
holds remain authoritative.

Supplies require both roofing and valid storage. The native base deterioration rate
identifies vulnerable items even when their current rate becomes zero under a roof.
Current deterioration and rot deadlines remain separate observations.

Guarded hauling returns a native quantity-tracking ID. The saved `hauling` section
follows the entire source stack through native splits and merges. Mixing other
stock into a tracked stack expands the protection obligation to the whole mixture;
it never arbitrarily attributes surviving units to the original source. Held or
partially delivered pieces do not satisfy delivery. Native destruction before
protection and quantity changes outside verified transfers remain explicit failures.
The first tick at which every tracked piece is spawned in covered valid storage
records completed delivery, preserving that proof through later consumption.
Controller completion requires the exact receipt ID, source, worker, original
quantity and current load/direction. Unresolved issued work keeps the supply goal
open even when the source ID disappears from the loose-item census. Records use
saved string identities, never coordinate rebinding, with bounds of 512 orders and
128 pieces per order. Missing identities and exhausted tracking capacity cannot
prove delivery. Older receipts retain the stricter same-item quantity check.

`storageCapacity` reports each observed item's currently unreserved covered slot
capacity using native storage acceptance, stacking and cell limits. Reservations,
fire and native construction blockers exclude cells. This is physical capacity,
not a delivery route or a reservation: different items compete for the same cells,
so capacities cannot be summed. Worker-specific dispatch still validates access.

When hauling reports no storage, the supply method can create a filtered 2×2
stockpile in existing covered space. It preserves observed plants, items, buildings,
zones, committed geometry and reserved walkways, previews at most eight candidates,
and admits at most three such stockpiles. Filters allow only the observed target
definitions. An atomic native guard rechecks roof, occupancy and zone ownership
before creation. Exact geometry and filter readback complete the zone action;
the supply goal still requires observed protected supplies. Missing covered space
or repeated capacity failure remains a blocker. If no existing covered space fits,
one 6×6 supply shell may be admitted through ordinary shared construction. At most
three sites receive native footprint and projected-access previews. Existing
structures, zones and reserved geometry remain protected. Native enclosure and
complete roofing must be observed before the filtered zone is created; the entrance
aisle stays free. The controller does not build duplicate rooms after interruption
or change another stockpile's filters.

`MaintainSleeping` is declared by the `sleeping` family: with it enabled the goal
is a method-available deficit ranked like any other development row; without it
the deficit stays visible as method-unavailable. Each review re-derives the
sleeping targets (colonists without an owned suitable bed, or without observed
use of one) from the upkeep census against the retained use history. The method
assigns first: the lowest waiting colonist with a vacant suitable bed receives
the lowest such bed through the typed `bed_assign` operation, one assignment per
goal epoch, carrying that colonist's expected previous bed so a player change
since the review is refused rather than overwritten. An attempt the native side
never admitted (its CAS token moved between inspection and write, so the
observation is absent and nothing changed) is retried up to three times in the
epoch; an admitted attempt is final for it. The native side re-checks
vacancy, humanlike/non-medical/non-prisoner eligibility, roof, forbidden state,
allowed area, reach and the pawn's comfortable temperature band together before
transferring ownership; it never evicts another owner. Only when nobody can be
assigned is one `Bed` staged, through the shared building ladder, in a room that
can host a Bedroom (Bedroom, Barracks or generic Room) whose observed
temperature lies inside the comfortable band of every colonist still unhoused;
a room too cold or hot for them is not a site, and with no such room the ladder
falls to its starter shell, which waits while the initial shelter is owed.
Construction uses shared resource admission and exact native placement/access
previews, one bed per method, and the staged bed is assigned on a later review.
A sleeping spot is never a suitable bed and is never staged by this goal.

Assignment and construction receipts do not complete sleeping upkeep. Recovery
requires observed use by the assigned pawn in a suitable bed, retained only for
that bed and current load. Missing reads, changed assignments, access loss and
unsafe temperature reopen the deficit. Natural sleep uses the existing schedule;
the method does not force rest or change a player's timetable.

`MaintainMedicalReserves` starts below the configured medicine units per colonist
and remains active until the higher recovery reserve is observed. Defaults are one
and three respectively; these are planning policy, not forecasts of treatment demand.
Current usable, reachable medicine of any native medicine definition counts toward
the reserve. Forbidden, expired, unreachable and future stock cannot establish it.
Replenishment uses the native herbal-medicine definition and the existing resource
source/production method. Required PlantCutting work joins shared allocation while
player overrides remain authoritative. Native eligibility decides mature wild
healroot acquisition: when no bench recipe can produce the definition, the
planner harvests undesignated medicine-yielding wild plants from the acquisition
census, counting designated plants as pending. Plants below harvest growth
yield nothing and are not sources. Unavailable sources and recipes remain
explicit blockers.
The method preserves patient care, drug policies and player production bills.
Designations and bill receipts never prove replenishment.

`MaintainAnimalContainment` observes native pen membership for eligible starting
animals. Pets and animals marked for release or slaughter do not receive a pen
request. Existing suitable pens are reused through ordinary native handling;
Handling joins shared work allocation without overriding player-disabled work.
If no suitable pen exists, one bounded 6×6 fence/gate enclosure and its pen marker
can be constructed through the same native enclosure checks used for supply rooms.
Built fences or a marker do not complete the goal: the animal must be observed
inside a suitable native pen. No breeding, bonding, master or removal setting is
changed. Feed sufficiency is a separate observation and cannot be inferred from
containment or grazing space.

`MaintainAnimalFeed` starts below two days of observed reachable feed per animal
and recovers at four days, with configurable ordered thresholds. Each animal has
its own latch; missing census, demand or access evidence cannot clear it. The
combined forecast shares stock with every eligible eater, respects allowed areas
and current rot deadlines, and credits neither pasture nor future production.
Explicit player herd targets retain feed ownership, including cancelled targets;
starting-animal upkeep does not replace those choices.

Native feed definitions include non-human food and the installed kibble food-type
flag, excluding drugs and corpses. Definition nutrition sizes bounded acquisition;
actual reachable stock and demand determine recovery. At most eight eligible
resources are considered through the shared source/bill method. When no
covering feed is reachable the method falls back to the kibble bill. A recipe
slot that accepts several ingredient definitions is funded by the cheapest
alternative in stock. Existing adequate
bills are reused, player resource restrictions remain authoritative, and required
production work joins shared allocation. The method never changes diets, animal
areas, breeding or removal settings. Adequate global stock with insufficient animal
access or rot runway produces an explicit staging blocker rather than more bills.
Production receipts do not prove either access or ingestion. Both animal needs
rank for a development slot like every other optional goal: the declared
`animal-feed`/`animal-containment` capability admits them and a known unfed or
uncontained target is a full deficit. A kibble bill completes on its first
observed iteration; while the feed deficit persists afterwards the planner asks
for bounded clock windows (2500 ticks) so colonists keep working the standing
bill instead of leaving the clock refused as `no_work`.

`MaintainFoodStorage` tracks perishable, not-yet-rotted nutrition split between
stock already sitting in an adequately covered or enclosed/cold site and stock
that is not. It starts when unstored nutrition clears a small at-risk floor
(five units, avoiding thrash over a single dropped ration) and the stored share
of total perishable nutrition falls under the configured minimum fraction
(default one half), and it does not clear until that share recovers past the
higher target fraction (default nine tenths). Non-perishable and already-rotted
stock never contributes: it is not at risk of being lost to inadequate storage.
The method prefers relocating at-risk stock into an existing covered/enclosed
site with observed spare capacity, choosing deterministically among candidates;
only once every known site is unusable or exhausted does it fall back to a
StockTarget production bill for more preserved or non-perishable food, through
the same source/bill method other resource goals use. Relocation and bill
receipts never prove spoilage was averted; the census must observe the stock
as stored, or the runway as recovered, before the deficit clears. Native code
sets `FoodStock.roofed`, `temperature_c` and `room_id` per stock row; a stock
counts as stored when it is roofed and either chilled (at or under 10 C) or
has at least five days of rot runway, so a roofed but warm stockpile is not a
storage deficit unless the food is close to rotting.

`MaintainRefrigeration` (issue [#6](https://github.com/davidarcher/rimgovernor/issues/6)
slice 1) takes the warm side of that split: roofed perishable nutrition in a
known room, warmer than 10 C and under five days from rot. It latches at the
same five-unit at-risk floor and releases only once every such stock measures
5 C or colder; unknown temperature or room facts preserve the latch. Its
method needs an enclosed room, native `Cooler` availability, and a wall cell
with a straight inside-wall-outdoors line; an existing cooler whose cold side
faces the room is patched to the freezer target through the building-
temperature action rather than duplicated, an unpowered or disconnected one
defers to `EnsureBasicPower`, and one venting into another enclosed room is
reported blocked. A cooler receipt or completed build never clears the
deficit: native cooling must be observed on the stock itself. `EnsureBasicPower`
in turn holds on out-of-fuel or broken producers (refuelling and repair are
ordinary pawn work) and treats a powered network draining its batteries in
under a day as a deficit, sizing the next generator to connected load and
choosing its definition from native availability and fuel stock.

`MaintainLighting` (issue #6 slice 3) reasons from illumination at the cell a
pawn stands on, not from fixture counts. Native reports every colonist work
table and research bench's interaction cell with its measured ground glow
(`UpkeepFacts.lighting`), plus every glowing fixture with its radius, native
lit flag and service state. A roofed work cell measuring under 0.3 (RimWorld's
own lit threshold) latches its bench; unroofed cells are ignored because sky
glow would flap the latch with the day, and an unknown census preserves the
previous latch. The goal ranks as an ordinary development project. Its method
first looks for a fixture whose radius reaches the cell: one that is not lit
defers to power, refuelling, repair or flicking (`lamp_power_needed`,
`lamp_fuel_needed`, `lamp_repair_needed`, `lamp_switched_off`) rather than
doubling up, and a lit one that still leaves the cell dark is reported
blocked. Otherwise it places the first affordable lamp -- a `StandingLamp`
only while some network has an active source, else a `TorchLamp` -- on the
nearest free, walkable, unzoned cell of the same room within two cells of the
interaction cell (never the cell itself), each candidate validated by the
native placement preview. A build receipt never clears the deficit: the next
measured census must read the cell lit.

`MaintainFlooring` (issue #6 slice 4) reasons from the terrain under each
room cell and that terrain's native stats, never from what was last ordered.
Native reports every proper indoor home room (`UpkeepFacts.flooring`) with
its role, the terrain def under each cell and the floor already ordered there
(a blueprint or frame), alongside one table of every terrain named with its
cleanliness, beauty, path cost, flammability and `natural` flag; the planning
census reports a requested `TerrainDef` as a one-cell definition carrying the
same stats, its cost list and research availability. A clean workspace
(kitchen, hospital, laboratory, or an enclosed room holding a cooking bench,
the same classification the cleanliness slice uses) is deficient while any
cell's terrain cleanliness is negative; a living room (bedroom, barracks,
dining, recreation) while any cell is natural ground. Barns, butcher rooms
and every other role carry no requirement. There is no hysteresis: terrain
does not flap. A cell whose floor is already ordered still counts as
deficient, so the latch holds until the floor is laid, but it is never
ordered twice; an unknown census preserves the previous latch. The goal ranks
as an ordinary development project, clean workspaces before living rooms.
Its method chooses from the policy's floor list the known-available terrain
that meets the tier (non-negative cleanliness for clean, non-negative beauty
for living), preferring the one whose cost list the colony stock pays for
over the most cells of a bounded batch and then the tier's weighted score
over cleanliness, beauty, path cost, flammability and cost; no available
floor defers to research, no affordable one to materials, and a room whose
deficient cells are all ordered waits on them. Every cell is validated by
the native placement preview and admitted as its own action; the typed
construction path lays a `TerrainDef` through the ordinary blueprint and
frame, and completion is observed as the terrain grid reading the admitted
floor at the cell rather than a successor thing. A build receipt never
clears the deficit: the next measured census must read every cell of the
room floored. A third tier, traffic, joins the routes census (below): the
busiest natural home cells outside any tiered room are deficient once the
sampling window holds at least four times the per-cell minimum and the cell
itself at least that minimum; a floor with any path cost never qualifies. A
routes census that is unknown or unavailable leaves the whole flooring census
unknown rather than silently dropping the tier; a native without the routes
section measures the room tiers alone.

`MaintainRoutes` (issue #6 slice 5) reasons only from observed reachability
and travel, never from flood-fill connectivity or straight-line distance.
Native reports (`UpkeepFacts.routes`) every facility a colonist must reach:
player beds for humanlike non-prisoners, work benches at their interaction
cell, storage buildings, dining surfaces, turrets and stockpile zones (an
`EntityRef` `zone-<id>` of `Zone_Stockpile` at the zone's first standable
cell), each with the room it stands in and one travel row per mobile
colonist (spawned, not dead or downed, at most 32) carrying the game's own
`CanReach` answer from that colonist's current position with its door
permissions and `Danger.Some`, and, for the first 256 reachable pairs, the
total cost and node count of the path `FindPathNow` returns. A facility no
listed colonist reaches lists up to 16 breach cells: one-cell player walls
(`isPlaceOverableWall`) on its proper room's border that a door would pass
straight through (a room cell on one side, a standable outer neighbour some
colonist can reach on the other, so a corner never qualifies), ordered by
squared distance from the nearest such colonist, each naming the wall def and any door blueprint or
frame already on it. A reachable facility instead lists, as pending
breaches, every ordered door (blueprint or frame) a measured path crosses,
because a frame is walkable before the door stands. The section also carries observed traffic: a map
component samples, every 30 ticks, the cell under each free colonist whose
pather is moving; the 64 busiest cells are listed with their terrain, home
flag and any floor already ordered, with the window's total sample count and
start tick (the window restarts on load). The decoder requires every travel
row's reachability and every traffic cell's samples, terrain and home flag,
else the census is unknown; the bridge refuses unlisted pawns, path numbers
on an unreachable row, duplicate breach or traffic cells and out-of-map
cells. A facility is deficient when the census lists at least one mobile
colonist and none reaches it; it latches by ID and releases only on a
measured census that reads it reachable with no door still ordered on its
border (a latched facility reachable through its door frame waits for the
door, proposing no second breach), with no hysteresis because reachability
is the game's own answer. The goal ranks as an ordinary
development project. Its method serves storage and stockpiles first, then
benches, dining, beds and defence, and opens the facility's room with a
`Door` of `WoodLog` on one breach cell: the policy's nearest breaches are
previewed in order with north then east rotation, and the first native
reports legal with a one-cell footprint on the wall cell is admitted as a
single-action plan. Native marks a door over a wall unsafe because the wall
would be wiped; the planner treats exactly one wiped building with no
blueprint, frame or cancelled work at the cell as the deliberate replacement
it is and marks the placement safe itself (`Preview.Blockers` carries the
categories for this), leaving any other disturbance refused. A facility
whose breaches are all ordered waits for them (`route_door_pending`); one
with no breach at all is reported (`route_no_breach`) so the deficit stays
visible; an unavailable door defers (`route_door_unavailable`). A build
receipt never clears the deficit.

`EnsureComfort` maintains dining and recreation after startup survival work.
Its deficit remains visible during emergencies; admission waits rather than
claiming the facilities complete. Sleeping upgrades belong to `MaintainSleeping`.
Dining uses native eating surfaces with adjacent sittable furniture, an enclosed
roofed room and safe colonist access. Seat placement is restricted to the observed
surface's adjacent cells. Recreation reuses native joy buildings with safe access
and current power where required. Existing inaccessible furniture remains a blocker
instead of causing duplicate construction or player-area changes.

One table, seat and recreation object may be admitted through shared construction.
Recovery requires access for eligible colonists plus observed dining and recreation
use. The use evidence belongs to the current load and exact furniture identity;
access loss or removal reopens the deficit. Ordinary needs-driven behavior and the
existing timetable select use. No schedule rewrite or forced need job is introduced.
The shared watchdog bounds waiting for use, independently of research progress.

An `upkeep_target` action waits after the native job receipt. Hauling requires the
same item identity and at least its original quantity in roofed valid storage;
repair requires full observed target health; cleaning requires target absence
from a complete census. The fire goal separately requires no remaining home fire.
Disappearing hauled items, including stack merges,
remain explicit blockers. Partial delivery never proves complete protection.
Load or player-direction changes invalidate ownership; uncertain writes are not
replayed. The existing no-progress watchdog bounds waiting.

Verified completed upkeep orders may be retired through the existing action
archive when the same target needs maintenance again. Waiting, blocked, uncertain
or cancelled work cannot be retired to bypass duplicate-intent protection.

These maintenance predicates are separate from `FOOTHOLD_STABLE`. Their presence
does not certify the complete startup/upkeep matrix or sustained survival. The
per-deficit live acceptance is `go/internal/nativeaccept/cmd/upkeepaccept`
([B04h](https://github.com/davidarcher/rimgovernor/issues/2)): one staged
deficit per scenario, the owning families only, recovery observed on the native
postcondition (`-scenario a,b` picks scenarios; `-reuse` reloads the baseline
save into one running game between them instead of relaunching RimWorld).
`MaintainFireSafety` has no order to issue: its `fire` family only grants the
clock a bounded native-work window while `EvaluateFireSafety` finds a bounded
home fire with an eligible firefighter. Wall replacement and the sustained
multi-season campaigns are tracked separately.
