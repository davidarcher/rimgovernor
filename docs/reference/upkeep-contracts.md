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
| Storage | Existing slot cells, roof and occupancy; native hauling decides whether filters, priorities, reservations and capacity admit an exact item. Cell count alone is not usable capacity. |
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

When hauling reports no storage, the supply method can create a filtered 2×2
stockpile in existing covered space. It preserves observed plants, items, buildings,
zones, committed geometry and reserved walkways, previews at most eight candidates,
and admits at most three such stockpiles. Filters allow only the observed target
definitions. An atomic native guard rechecks roof, occupancy and zone ownership
before creation. Exact geometry and filter readback complete the zone action;
the supply goal still requires observed protected supplies. Missing covered space
or repeated capacity failure remains a blocker. This method does not build a new
storeroom or change another stockpile's filters.

`MaintainSleeping` reuses vacant eligible beds before building one affordable bed
beside a controller-created floor spot. Ordinary construction uses shared resource
admission, exact native placement/access previews and observed cell temperatures.
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
