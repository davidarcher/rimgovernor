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
Native UI input, Manual, a load/map change or changed safety invalidates pending
demolition. Player replacement of a designation relinquishes controller ownership.
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

`MaintainSleeping` reuses vacant eligible beds before building one affordable bed
beside a controller-created floor spot. Ordinary construction uses shared resource
admission, exact native placement/access previews and observed cell temperatures.
Unavailable bed research enters the existing admitted-capability research system.
The spot remains available throughout construction and after reassignment. An
upgrade requires its exact confirmed native placement identity; matching coordinates
alone cannot establish ownership. Player assignments and legacy receipts without
that identity remain protected. The native `home/upkeep_bed` operation checks the
previous assignment, vacancy, eligibility, allowed area, access and temperature
together before transferring ownership; it never evicts another owner.

Assignment and construction receipts do not complete sleeping upkeep. Recovery
requires observed use by the assigned pawn in a suitable bed, retained only for
that bed and current load. Missing reads, changed assignments, access loss and
unsafe temperature reopen the deficit. Natural sleep uses the existing schedule;
the method does not force rest, remove a floor spot or change a player's timetable.

`MaintainMedicalReserves` starts below the configured medicine units per colonist
and remains active until the higher recovery reserve is observed. Defaults are one
and three respectively; these are planning policy, not forecasts of treatment demand.
Current usable, reachable medicine of any native medicine definition counts toward
the reserve. Forbidden, expired, unreachable and future stock cannot establish it.
Replenishment uses the native herbal-medicine definition and the existing resource
source/production method. Required PlantCutting work joins shared allocation while
player overrides remain authoritative. Native eligibility decides mature wild
healroot acquisition; unavailable sources or recipes remain explicit blockers.
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
resources are considered through the shared source/bill method. Existing adequate
bills are reused, player resource restrictions remain authoritative, and required
production work joins shared allocation. The method never changes diets, animal
areas, breeding or removal settings. Adequate global stock with insufficient animal
access or rot runway produces an explicit staging blocker rather than more bills.
Production receipts do not prove either access or ingestion.

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
does not certify the complete startup/upkeep matrix or sustained survival. Remaining
implementation and gameplay acceptance belong in [B04h](https://github.com/davidarcher/rimgovernor/issues/2).
