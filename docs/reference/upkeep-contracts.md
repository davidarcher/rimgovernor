# Colony upkeep contracts

[Documentation](../README.md) · [Controller contracts](controller-contracts.md)

Upkeep uses the existing ColonyPlan, resource admission and Hands executor.
`home/colony_facts.upkeep` is versioned read-only native evidence. Each section is
independently nullable with an error; an empty successful census differs from an
unavailable read. The section tick must match the enclosing observation.

## Native capability audit

| Need | Available native evidence and action boundary |
| --- | --- |
| Supplies | Per-item identity, location, quantity, condition, deterioration stat, roof, valid storage, rot deadline and forbidden status. `home/order` hauling previews native storage access. `requireSafeStorage` rechecks enabled hauling, safe reach and covered storage during dispatch. |
| Storage | Existing slot cells, roof, occupancy and item-specific native filtered capacity. Native hauling separately decides worker access and delivery; cell count alone is not usable capacity. |
| Sleeping | Bed definition, slots, owners, current users, pawn-specific access, roof and temperature. Capacity does not establish actual use or suitable worn protection. |
| Home and structures | Exact occupied/protected cells with home coverage; owned building condition, material, roof and support definition. A support definition does not prove a replacement batch is safe. |
| Fire, cleaning, repair | Exact native targets and condition. `home/order` repair/clean use the installed WorkGivers and their normal eligibility. These methods require current home coverage, safe access and enabled work. The installed firefighting WorkGiver is not directly orderable: enabled workers respond normally while the controller watches at most three home fires of size at most one. |
| People | Current rest, recreation, mood, worn apparel condition and native comfortable temperature range. `home/list_pawns` supplies medical, work, settings and schedule reads. |
| Animals | Owned animal census, diet and food need; native rope-management eligibility, current enclosed pen and suitable pen identity. Pets have no pen-containment predicate. Reachable stored feed follows native eating eligibility and allowed-area access; it excludes drugs and does not count pasture or future harvest. Existing combined-demand food forecasts account for animal shares separately. |
| Medicine, season, power | Medicine is identified through native item definitions. Existing forecast/status tools supply patient, crop and power inputs; no future production or season is credited as stock. |

## Maintained jobs

`SecureSupplies`, `MaintainEssentialRepairs`, `MaintainCleanFacilities` and
`MaintainFireSafety` retain their goal identities across recovery and recurrence.
Any observed target enters maintenance; recovery requires no remaining deficit
and no unresolved issued action. Missing evidence retains active risk, while a
first unavailable read creates an ordinary visible blocker rather than an invented
emergency. Fire risk preempts development. Unknown or oversized fire intervention
retains an emergency hold.

Methods inspect at most eight targets and eight enabled, available workers in a
review. Stable target and pawn IDs break ties. Medicine and rot deadlines rank
hauling; relative damage ranks repairs; kitchen, hospital and laboratory filth
ranks before other home cleaning. Methods preserve forbidden items, player work
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
implementation and gameplay acceptance belong in [B04h](../BACKLOG.md).
