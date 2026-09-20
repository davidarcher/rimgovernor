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

`MaintainHomeCoverage` joins the complete current player-faction building census
and owned stockpiles to observed facility geometry. Buildings placed in Manual
are eligible when Auto resumes; action history is optional provenance. Targets use the native unique load id: a
building's, or the `zone_id` returned by the owned `CreateZone` receipt.
A building anchors its occupied cells and connected visible, enclosed,
fully roofed rooms. Traversal crosses usable colony doors only when both
sides are enclosed rooms; outside doors and gaps do not extend coverage.
Stockpiles retain their exact committed footprint.

Each target proposes at most 256 cells, preferring missing cells in stable
coordinate order. Larger rooms and connected interiors progress through
successive batches. Covered cells cannot hide missing cells beyond the first
batch. The native census remains bounded; unavailable geometry stays unknown.

Play is autonomous: every missing scoped cell is restoration work, regardless
of how Home was removed. Native Home edits (Set, Clear, Invert) advance the
map revision. Legacy saved Home exclusions are ignored; the retained
`excluded_cells` wire field is zero. A method is identified by target, batch
shape and observed Home revision, so a later removal can admit fresh work.

Typed `ExtendHome` and legacy `home/upkeep_home` re-derive the batch and require
the observed shape and revision before writing. Receipts/progress inspect the
admitted cells, not the next batch; they must still belong to the connected
facility and actually be Home. A missing cell or disconnected admitted
geometry makes the outcome unsuccessful. Uncertain writes are not replayed.
See [apply-time checks](action-contracts.md#apply-time-preconditions-and-refusal-reasons).

`upkeep/home-coverage` builds two owned beds in connected chambers, checks
corridor coverage and an uncovered outdoor cell, rejects stale geometry/revision,
restores a removed corridor cell across service restarts and audits save/load
persistence. Remote resource work does not expand Home.

`upkeep/storage-missing` proves the stockpile side: the zone SecureSupplies
creates outside Home is extended over under its receipt identity. Vanilla's
auto home area play setting (on by default, `AutoHomeAreaMaker`) marks Home
four cells around every added zone cell and around player buildings, so with
it on a created stockpile is covered before the routine sees it; the fixture
turns the setting off so the controller's extension is observable.

Native construction lineage follows bridge-created blueprints into frames and
finished buildings, including the game's failed-construction blueprint recovery.
Blueprint material comes from its native intended-material field. A finished
identity is captured during the native frame completion call; coordinate matching
alone cannot transfer ownership. Records persist with the game and report missing
or ambiguous identities explicitly. Missing identities after load are not rebound
by coordinates. The bounded ledger retains at most 4,096 origins.

Planning ownership uses `policy.CurrentConstruction`: a complete bounded census
of built artificial player-faction buildings, with native identity, definition,
material, rotation, anchor and occupied cells. Home coverage and stone-shell
planning share this view; colony extent consumers must use the same contract.
Unknown or incomplete observations do not establish eligibility. A matching
completed action annotates provenance; missing history does not exclude a
player-built facility, and replacement geometry never inherits causal history.
Lineage does not authorize demolition or establish safe removal.

`policy.DeriveColonyExtent` is a pure current-territory model. It joins the
complete `CurrentConstruction` census, optional action provenance and exact
owned stockpiles into sorted four-neighbor regions. Each cell records `facility`,
`enclosed_interior`, `corridor` or `margin` provenance. A margin is an explicit
0–8 cell Chebyshev radius clipped to map bounds; overlapping margins never join
separate regions. No bounding rectangle or inferred path fills gaps between
facilities, wall fragments or islands.

Home repair batches are not complete geometry: their 256-cell selection can omit
covered cells and change as Home is restored. `HomeCoverageTarget.ExtentGeometry`
is the separate complete, unbatched interior/corridor fact for the pure model;
known empty means footprint only. It follows Home's enclosed roofed room and
internal-door geometry contract, and every supplied cell must connect to the
building footprint. Existing observations leave this field unknown. Missing
census, bounds, target or complete geometry keeps the extent unknown. Stockpiles
contribute only their exact footprints. This model neither assesses current
safety nor changes native Home; the established history and expansion areas it
feeds persist per saved timeline ([persistence contracts](persistence-contracts.md#what-must-survive)),
and runtime projection and consumers belong to the later colony-extent slices.

`policy.ResourceReach` limits resource candidate consideration to `base`,
`near`, `far` or `map`, with a reason on every decision. Base means exact extent
cells; near adds a 12-cell Chebyshev margin. Two armed colonists, positive hauling
capacity and storage headroom permit near work, unless threat facts are unknown,
a threat is present or raid points exceed 100 per armed colonist. Six armed
colonists and a quiet storyteller permit far with two free haulers, map with
three. These are conservative selection thresholds, not combat predictions.
`FilterResourceReach` intersects that ceiling with current map bounds and a
positive candidate eligibility verdict and observed passable route, including at
base/map. Distance never supplies route evidence. Candidate eligibility is the
narrow input boundary for the separate eligibility view; dispatch is unchanged.

The routines API exposes `resourceReach.stage/reason` alongside `extent` known,
region and cell counts. Its read-only projection uses held colony facts; complete
extent geometry and missing readiness (hauler capacity, destination headroom,
storyteller quietness) stay unknown until their observation producers supply them.
Unknown extent reports `base` / `extent_unknown` and admits no candidates.

The roof-support preview checks connected existing roof cells within the installed
native support radius while excluding the specified wall as a holder. It changes
no roof or building. Fog, map-edge uncertainty, pending collapse and unsupported
cells refuse the certificate. Planned supports earn no credit. Replacement must
also preserve enclosure and escape access and repeat safety checks at execution.

`MaintainStoneShell` admits one wall upgrade after urgent needs. Only
currently observed colony walls can supply a demolition target. A complete
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
buildings is observed standing in the defense-site census. The firing line
floors each shooter cell beside its barricade (#224): a floor is terrain,
so the census reads it from the cell's terrain rather than its edifice,
the access audit leaves it walkable, and nothing grows onto the position
the hold plan moves to; a shooter floor the native preview refuses is
dropped from the tier, leaving that position unfloored. A `Complete`
record is re-verified after each ActiveCombat epoch and once per game hour of
simulation: a tier that lost a building (a breached wall, a sprung spike trap,
which is destroyed on springing) re-opens with a fresh retry budget and is
re-admitted as a new tier method, while `Complete` stays true so combat keeps
holding the line on the proven geometry. A missing building whose cell already
carries a blueprint or frame (the game's own trap auto-rearm) is not placed
again; the planner asks for a clock window so native construction finishes
it. Defenders are undrafted by ordinary draft cleanup once the recovered
ActiveCombat goal stops authorizing the hold plan.

Combat holds the line only against an ordinary edge assault still in front
of it: every live raider carries a walk-in assault lord and stands short of
the firing line's cover row (its cell projected on the corridor direction,
`policy.BehindFiringLine`). A siege, a sapper or breach toil, a drop
arrival, a raider already within engaged distance or one already past the
cover row is answered with squad defense at the threat instead. A standing
hold is re-examined at every planner step against the same evidence (#118):
a live raider seen at or behind the cover row, or a raid whose lord evidence
turned into a breach or sapper toil, makes the planner cancel the hold's
in-flight orders (`hold_fallback`); the worker releases drafts a plan no
longer holds, and the next step admits squad defense on the intruders.
Unknown position or lord evidence never abandons a standing hold.

The `turrets` tier (#61) is added to a layout, fresh or already stored, only
when every gate is observed: the turret planning definition is available
(its research prerequisites finished in the research census, never an
assumed `GunTurrets`), a power network with generation reports spare watts
for every turret's draw, and the stock census covers the turrets and,
once routed, their conduits. Turrets stand three cells behind the shooters'
row in line with the trap lane (and three to either side), then on the
shooters' row outside the firing span, at least three cells from any
shooter or other turret (a destroyed turret explodes), never on a lane, a
reserved cell or unknown ground, each with a native line of sight to a
cell of the trap lane (the funnel walls hide most of the lane from the
flanks, which is why the row behind comes first); a conduit
chain is routed to each turret unless a transmitter already lies within
native connector reach (six cells), and a turret with no route is dropped.
A stored layout without turrets re-probes the gates once per game hour. A
turret tier counts as built by the same census as the others (conduits by
the power census, since they are not edifices); a standing turret without
power is a deficit the goal keeps reporting (`unpowered` in the scheduler
log) while its network is `EnsureBasicPower`'s and a lost tier conduit is
re-placed like any missing building. Damage is `MaintainEssentialRepairs`'
(turrets are home-area structures with repair priority 2). While every tier
stands, the goal reports a known zero deficit to development arbitration so
those upkeep goals take the free slot first; the periodic re-verification
still runs whenever a slot is free. The barrel (the mini turret's refuelable
comp, fed with steel) is read from the same power census as the turret's
power, since turrets are consumers: a turret observed out of fuel is a tier
deficit like an unpowered one (`unfuelled` in the scheduler log; the game's
own auto-refuel at half a barrel with steel in stock normally forestalls
it). While a barrel is empty and one of its fuel definitions is in stock,
the goal issues one forced refuel order per step (a `recovery_service`
action on the turret's census identity, carried by an available colonist
whose Hauling work is not disabled, the highest-priority enabled hauler
first; up to four attempts per turret and goal epoch), the same native
work-giver job a float-menu click issues, so a switched-off auto-refuel or
an idle hauling roster does not leave the line unarmed. When no fuel
definition is in stock the record carries the barrels' fuel gap as a
`FuelShortage` and the routine review raises it as a derived
`MaintainResource` floor (`RoutineFacts.ResourceNeeds`, merged by
`ResourceGoalTargets` without lowering an operator's floor), so the resource
policy sources steel (bench recipe, then native mineable sources) until the
census sees it; the gap is counted in fuel units, an estimate of the steel
(the native per-item multiplier is not observed). An unknown fuel state, or
a turret cell the power census does not carry, is neither a deficit nor an
order. Made-from-stuff definitions the planning read cannot build from wood
(the turret is metallic) are observed with the game's default material, so
their costs and stuff are known.

Corners instead require verified existing roof support and both neighboring walls,
with clear exterior cardinal approaches reachable by enabled haulers. Only the
permanent stone wall is reserved. Those approaches remain open so ordinary hauling
can remove deconstruction salvage: native diagonal access to a wall does not grant
the same access to loose items. The enclosed interior and its construction approach
remain available; stonecutter placement preserves that approach.

`Deconstruct { target: EntityPrecondition }` clears one exact building
through native `Designator_Deconstruct`. The entity carries no CAS token. Preview
and apply require player deconstructibility, visible geometry and safe remaining
roof support; apply resolves the exact occupant again. Enclosing colony walls
require guarded `RemoveWall`; generic deconstruction cannot bypass its enclosure
and replacement checks.

An explicit operation adopts a standing designation rather than placing a
second one; there is no designation ownership ledger. The receipt names the
target and designation.
`DeconstructEffect` carries `target_id`, `designation_id`, `worker_ids`,
`demolition_observed` and `site`. Workers are recorded when native demolition
finishes. Only the native deconstruct job establishes completion; disappearance
without that callback is unsuccessful. Receipt and designation ownership are
scoped to the loaded game, like the operation ledger; loaded designations without
a current receipt remain untouched until explicitly adopted. Manual releases
owned pending designations.
`ReleaseDeconstructions` retires pending work on stop and removes only the exact
controller-admitted designations, returning a separate `released_count` effect.
Player replacements survive release. The Go bridge rejects unknown evidence
fields and completion without observed demolition.

`home/upkeep_wall` creates an ordinary native deconstruction designation. Completion
comes from the actual native deconstruction job, not disappearance of a wall. The
guard rechecks exact supporting identities, enclosure, roofs, remaining materials
and resource policies before completion. Jobs require active supervised simulation.
Native UI input, a load/map change or changed safety invalidates pending
demolition; Manual suspends it until control resumes.
Cleanup requires the completed permanent wall. Missing or uncertain outcomes stay
blocked. Stopping automation removes the pending demolition designations its
removal records placed. Native evidence must confirm retirement, the surviving exact target
and the absent designation before the shared plan cancels unissued descendants.
Issued construction remains under observation. Retained cancellation history prevents
duplicate replacement; existing backup walls remain intact. Blocked demolition is
retired before further supervised simulation. Interrupted recovery beyond this safe
retirement and wider native acceptance remain listed in the backlog.

`ClearHomeObstructions` (the `clearance` routine family) admits one non-player
building inside Home at a time, nearest the colony center first. A fresh
clearance census must report deconstructible geometry with no roof blocker,
ancient danger or casket. A standing deconstruct designation is no hold: the
admitted Deconstruction adopts it, whoever placed it. Repairs precede clearance;
clearance precedes direct cleaning, and shared emergency admission still wins.
The durable review records each skipped target and its reason in `ClearanceHolds`.
Chunks are hauls, not deconstructions (#394): a chunk stack in Home that is
allowed, unstored and has no store cell ordinary hauling would take it to is a
clearance deficit too, and once no building target remains the planner admits
one low-priority `RimGovernor dumping` stockpile (allow list: the pending chunk
definitions plus `ChunkSlagSteel`, so a smelter bill draws from the same dump)
on the census's `dump_sites` footprint outside held building footprints, one
cell per pending stack between 4 and 16. The method is content-addressed by
cells and allow list; ordinary hauling then clears the stacks and the deficit
recovers as soon as every chunk is stored or has a destination. Forbidden
chunks are the supply safety policy's (#336).
`ClearAncientShrine` (the `shrine` family, #458) is the sealed-shrine
counterpart: it holds the clearance goal while it has work and is described
under the breach goal in `controller-contracts.md`.
Unknown observations preserve the previous need. Recovery requires no eligible
candidate and no unresolved issued action; recurrence keeps the goal identity.
Every dispatch rechecks eligibility, authority and emergency facts. Only the
native demolition callback completes its action. Stop calls
`ReleaseDeconstructions` before revoking authority, and native authority loss
also releases exact controller-owned designations. Issued actions remain under
observation. Salvage uses ordinary hauling and does not gate this goal.

`SecureSupplies`, `MaintainEssentialRepairs`, `MaintainCleanFacilities` and
`MaintainFireSafety` retain their goal identities across recovery and recurrence.
Any observed target enters maintenance; recovery requires no remaining deficit
and no unresolved issued action. Missing evidence retains active risk, while a
first unavailable read creates an ordinary visible blocker rather than an invented
emergency. Fire risk preempts development while the fire family is declared;
without it the review records the fire need but does not suspend the other
goals, since no method could clear a hold the clock would then never leave.
Unknown or oversized fire intervention retains an emergency hold. A pending, never-issued repair is cancelled on
the next repair step, before the need gate, once `MaintainEssentialRepairs`
has recovered (the colonists mended every target themselves) or its hold
says the structure is ineligible (already repaired, or gone), so the
recovered goal never keeps its development commitment on work that can no
longer matter.

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
area only (at most 256 rows), the only filth an upkeep order may target. A
latched room's filth is what its Cleanliness stat sums: the filth the census
places in the room plus home-area filth on a cell touching the room (8-way,
its doorway), which the game registers on both sides of the door. Other filth
outdoors, in other rooms, and in
inherently dirty rooms (barns, rooms holding a butcher bench) is ordinary
colonist work. Butcher placements never enter a cooking bench's room and cooking
placements never enter a butcher bench's room; a colony whose every butcher bench
shares a cooking room is admitted one more `ButcherSpot` outside. The spot and its
`ButcherCorpseFlesh` bill are foothold work owed whenever the food runway is under
`FoodTargetDays`, whether or not anyone is armed yet (#260): native offers no hunt
row until a usable bench carries the bill, so the bill precedes the first hunt rather
than waiting on the equip family. The butcher
bill waits until that `butcher-spot-separated` method has been tried (admitted,
completed or failed) and then prefers the separated bench, so a forever bill on
the shared bench never holds the food-supply goal open. Methods preserve forbidden items, player work
overrides, schedules, drafts, existing player-forced jobs, storage filters and home
areas. Native cleaning eligibility determines whether fresh filth can be worked;
the controller does not encode a filth-age threshold. Cleaning admission uses
the live preview tick: native revalidates pawn and filth tokens and eligibility,
while the executor bounds inspection age in wall time. Tick advance between
the pawn read and preview alone does not stale an accepted preview.
Deterioration and growing fires do not reset the progress watchdog. A newly oversized fire retains a hold
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
covering feed is reachable the method falls back to the kibble bill. A bill drops
its product at its bench, so the census names, per animal, the player work
tables it can reach inside its allowed area (`reachable_bench_ids`) and the
bill lands on a bench every covered animal reaches when one exists. With no
such bench, feed made elsewhere still counts once hauled where the animals
eat: the census also names, per animal, the stockpile zones it can reach with
the edible definitions each accepts (`reachable_storage`) and a connected free
roofed footprint inside its area where a zone could go (`storage_candidates`;
the zone operation only takes covered ground, so an unroofed area offers
none). When a
zone accepting the feed is reachable by every covered animal the bill may land
on any bench; otherwise the method first zones a feed-only, important-priority
stockpile on the footprint the covered animals share and bills on the next
step. With neither bench, zone nor footprint the production path is refused
for a bounded window rather than piling feed up out of reach (a bench, zone or
widened area is seen at the next step). A recipe
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

Feed acceptance follows an unsuccessful production bill to the bounded goal
recovery wait, then verifies native reachable feed. A changed bill remains an
unsuccessful order; produced items alone neither complete it nor prove recovery.

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
ordinary pawn work) and sizes a draining network by its daily energy budget
([power contracts](power-contracts.md)).

`EnsureTemperatureSafety` reuses the same wall search for a hot sleeping
room ([#406](https://github.com/davidarcher/rimgovernor/issues/406)). Hot
rooms are served hottest first (cold rooms coldest first); a room with no
thermal facility of its own gets one powered `Cooler` through the
lowest-sorted vented wall cell, cold side in, when native `Cooler`
availability is known true and a connected power network's nominal producer
capacity exceeds its demand by the cooler's declared draw
(`policy.TemperatureCooling`, `PowerTopology.SpareW`); a known solar flare,
unfinished research, unknown power facts or a room with no vented wall keep
the passive cooler inside the room. A `Cooler` the power census places on a
cell beside the room counts as that room's facility (wall buildings are
outside the room's cells and contents), so the room then waits on native
cooling rather than gaining a second unit. The cooler's setpoint is the
native default (21 C), inside the sleeping band; the temperature family patches
no setpoints. Its draw reaches `EnsureBasicPower`'s budget as every other
consumer's does: as pending demand while the plan is open, as measured demand
once built.

A solar flare switches every powered building off for hours, so neither goal
answers it with a build: while a `SolarFlare` condition with a native
remaining-duration read is observed (`policy.SolarFlareHold`) the review keeps
`EnsureBasicPower` open with `method_unavailable` (suspended, never cancelled,
and not extending the startup hold), and both planners report `solar_flare`
from the power topology's blackout read (`PowerWaitBlackout`,
`RefrigerationWaitBlackout`) with no cooling or power allowance lent. The
deficit and the refrigeration latch keep their measured state until the flare
ends and the next review can tell an outage from a shortfall.

`MaintainRefrigeration` keeps a method under the flare (#408): the warm
at-risk stock the dark coolers cannot save is eaten first. The cook-ahead bill
planner (`policy.CookAheadFood`, bound to the refrigeration goal, configured
with the refrigeration family and bill plans) raises one `CookMealSimple`-first
target bill on a bench native still reports usable -- a fuelled stove; the
unpowered electric one is excluded by the same usable flag -- for the latched
warm nutrition beyond what every existing target bill already reserves
(`ReservedFoodNutrition`), in meals. It is the one purpose allowed to add a
second bill of a recipe the bench already carries; a bench and recipe this load
already claimed is skipped (one claim per bench and recipe). Without the flare
hold or with the reserved targets covering the stock it proposes nothing.

While the flare holds, the defensive layout treats its turret tier as absent
rather than unserviced: `DefenseRearmTurrets` reports no `unpowered` cell for
the outage (an empty barrel is still counted and rearmed so the line is whole
when the flare ends), and the layout does not wait on the dark turrets.

`MaintainLighting` (issue #6 slice 3) reasons from illumination at the cell a
pawn stands on, not from fixture counts. Native reports every colonist work
table and research bench's interaction cell with its measured ground glow
(`UpkeepFacts.lighting`), plus every glowing fixture with its radius, native
lit flag and service state. A roofed work cell measuring under 0.3 (RimWorld's
own lit threshold) latches its bench; unroofed cells are ignored because sky
glow would flap the latch with the day, except under an eclipse (an `Eclipse`
condition with a remaining-duration read, `policy.EclipseHold`, #408), when
the day is as dark as the night and every work cell is measured so a dark
outdoor bench gets a torch of its own; the eclipse ending drops such benches
from the latch on the next review. An unknown census preserves the previous
latch. A cell whose room grows a plant native says dies to light
(cave fungus, `WorkLightCell.light_sensitive`: any such plant standing in the
room, or a growing zone set to one) is protected and never latches, however
dark it measures -- lighting it would kill the crop. The goal ranks as an
ordinary development project. Its method first looks for a fixture whose
radius reaches the cell: one that is not lit defers to power, refuelling,
repair or flicking (`lamp_power_needed`, `lamp_fuel_needed`,
`lamp_repair_needed`, `lamp_switched_off`) rather than doubling up; a lit one
already standing within the placement radius that still leaves the cell dark
is reported blocked; a lit one reaching only from further away has left the
cell partially lit (glow falls off with distance and stops at walls) and does
not stop the cell getting a lamp of its own. Otherwise it places the first
affordable lamp -- a `StandingLamp`
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

`EnsureBasicComfort` is the foothold-tier comfort goal (priority 2, #232): once
the initial shelter's roof and sleeping gates hold it wants one eating surface,
one adjacent seat and one recreation source that every colonist can reach, read
from the same native census before the hosting-room filter, so the starter hut
counts whatever room role it scores. Capacity alone recovers it; observed use is
`EnsureComfort`'s concern. While shelter is still owed the goal is
`method_unavailable` and its planner reports `initial_shelter_pending`, so it
never extends the startup hold nor competes with the shell. The table and chair
preview indoors, the horseshoes pin anywhere with accessible watch cells; an
existing facility nobody can reach stays a blocker rather than a duplicate.
Methods are `basic-comfort-<definition>` under plans `routine-basic-comfort-*`.

Once basic capacity holds, the same goal uses maintenance priority (3) for a
second reachable building-backed joy kind: colonies with multiple joy-needing
colonists request it immediately; a lone colonist requests it when native boredom
is set for the only accessible kind. Two kinds cap construction even if both are
bored. Duplicate buildings of the same JoyKindDef do not provide variety, and an
inaccessible second kind blocks duplicate construction. Native research and
builder availability gate selection: TubeTelevision with spare power at an indoor
site within connector reach of a running generator, then BilliardsTable, then
ChessTable or HorseshoesPin of a distinct kind. TV placement also requires native
watch-cell access. Ordinary construction, materials and placement guards apply.

The optional comfort joy census carries distinct usable building-backed kinds,
and per-colonist tolerance and native boredom vectors indexed by that kind list.
Pawns without a joy need are omitted. Native boredom preserves the game's tolerance
hysteresis. Bounds are 16 kinds, 256 pawns, 2048 pawn-kind entries, four candidate
definitions and 64 KiB of compact ProtoJSON, inside the existing colony envelope.
Exceeding a bound omits the entire joy census; basic capacity remains observable,
while variety stays unknown and cannot certify recovery or schedule construction.
The hosted-room comfort projection does not retain this unfiltered kind matrix.

`EnsureComfort` maintains dining and recreation once every startup survival goal
has a method on record or is monitoring-only.
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
per-deficit live acceptance is the `upkeep/<scenario>` case set
([B04h](https://github.com/davidarcher/rimgovernor/issues/2)): one staged
deficit per scenario, the owning families only, recovery observed on the native
postcondition (`-scenario a,b` picks scenarios; `-reuse` reloads the baseline
save into one running game between them instead of relaunching RimWorld).
`MaintainFireSafety` has no order to issue: its `fire` family only grants the
clock a bounded native-work window while `EvaluateFireSafety` finds a bounded
home fire with an eligible firefighter. Wall replacement and the sustained
multi-season campaigns are tracked separately.

## Corpse larder

`MaintainFoodStorage` also reviews fresh animal corpses when ordinary storage
has no deficit. Active larder handling runs at priority 2; the journal admits
only the corpse and forbid/allow direction selected by the review. It releases
forbidden corpses outside refrigeration, then
hauls eligible loose corpses into native-selected frozen storage. If no such
storage exists, it can admit an animal-corpse-only stockpile on observed free
cells in a frozen room; rotten corpses are excluded.

Only after a roofed corpse is observed at or below zero Celsius does it hold
animals yielding more than 225 meat, or body size at most 0.75 with more than
75 meat. The forever butcher bill stays active. The nearest rot deadline
(identity breaks ties) releases first when available raw meat falls below
one native ingredient batch per active cooking bill, or within 15000 ticks
of rot. Released corpses fund that same window while awaiting butchering,
preventing successive reviews from emptying the reserve. Unknown facts do
not authorize a hold. General event-loot handling excludes these corpses;
the larder uses the native safe-hauling census and the shared Hands actions.

`food/corpse-larder` verifies frozen reserves, live controller release, actual
native meat production and tile density. A corpse butchered before the first
hold is observed is an accepted loss of density, with its meat still available.
