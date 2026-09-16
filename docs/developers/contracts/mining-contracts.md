# Material extraction contracts

[Documentation](../../README.md) · [Space and resources](../architecture/space-and-resources.md)

`MaintainResource` uses the shared goal, plan, reservations and Hands path. A native
resource census ranks eligible sources by distance and stable identity. Each batch
contains one excavation target, or at most eight plant sources, and accounts for all eligible pending yield,
including sources beyond the displayed census. Estimated yield cannot satisfy a stock
target. Fresh observations select another deposit after depletion.

Surface mining requires a capable colonist with safe native reachability. Resource
mining refuses unknown cells or any roof within the installed game's roof-support
radius, pending roof collapse, adjacent structures, blueprints, frames, zones or home
area. This deliberately excludes supported tunnels as well as unsafe excavations. Native
eligibility and exact colony/load/map, source identity and coordinates are rechecked
while paused before designation.

## Excavation

Rooms and corridors are excavated through a separate contract that never relaxes the
resource guard above. An excavation is keyed by cell and rock definition, never by
ThingID, and completes when the cell no longer holds a mineable: yield is incidental.
The typed site read (`rimgovernor/observations_read_excavation_site`) reports, for up
to 64 cells, fog, rock, roof, designation and per-cell eligibility, plus a site-level
counterfactual roof-support verdict for removing the whole set, a pending-collapse
flag, miner availability and reachability of the access cell. A fogged cell is
unknown, never eligible and never a support witness, so support is reported unknown
until the pawns have opened enough rock to see. `ExcavateCell` prepares against that
read while paused, rejects any pending collapse or an unsupported single-cell removal,
adopts an existing Mine designation idempotently (reported as adopted), adopts a cell
the pawns already cleared as done (applied with cleared evidence and no designation),
and records a save-persistent excavation record. The excavation guard rechecks cell eligibility and
set-based support before each pick hit and ends the job with the blocker when the
surroundings change; it does not apply the resource radius rule. Designation, job
completion and clearance remain native outcomes; a receipt is not a certificate of a
usable room.

Owned designations retain a save-persistent mining record. A native guard rechecks
geometry before each pick hit, stopping the current job when the surroundings become
unsafe. Unowned mining follows ordinary player rules. The guard preserves normal
damage, labor, skill and yield calculations. Records distinguish designation from the
native destruction tick and actual output increase. Output is not a certificate of
hauling, continued stock availability or arbitrary mine safety.
Because RimWorld recreates compressed rocks with new ThingIDs on load, a pending
record rebinds only to the exact map, cell, definition and health verified while
writing that save. A stable source identity preserves interruption history across
this rebind; an arbitrary replacement rock cannot inherit the old authorization.

Interrupted sources cannot silently return in a differently composed batch. Explicitly
renewing the resource goal reopens its methods; Hands still checks current native
eligibility. Uncertain writes retain the shared execution safeguards.
Observed reductions in a designated rock's hit points refresh the shared progress
watchdog without crediting any stock. Unchanged health and designation receipts do
not count as progress. A fresh native read may reopen a watchdog hold, but cannot
reopen a player interruption or accept evidence from a different load or map.

Before a new mining batch, native storage reads conservatively count reachable empty
floor slots accepting the exact resource, with roofing for deteriorating materials.
When capacity is insufficient, the goal first schedules a new stockpile through the
existing zone action and its native geometry/filter postconditions. Its filter starts
empty and allows only the output definition. Player zones are never repurposed. Native
haul eligibility remains authoritative; storage capacity is not completed hauling.

The census also reports installed scanner/drill definitions, costs, research, existing
drill readiness and visible deep deposits when the native scanner overlay is available.
`CreateGoal(MaintainResource, deep_extraction=true)` requires explicit player direction
accepting native drilling infestation risk. After surface sources are exhausted, the
goal stages exact-resource storage and then researched scanner/drill facilities using
ordinary construction preflight, resource commitments and Hands. Candidate footprints
must be visible, empty and unroofed, with safe worker access and a native connection
to a grid whose observed surplus covers the equipment. Missing research, power,
labor or deposits blocks development with its evidence; it does not grant research,
generate power, reveal undiscovered deposits or suppress infestations.

Only confirmed new drill blueprints enter the supervised extraction policy. Native
blueprint-to-frame and frame-to-building transitions bind the exact owned drill;
existing player drills and unrelated replacement buildings cannot be adopted.
Before each native work interval, owned drills check
the exact resource, retained building identity, forbidden state and current stock
target. Depleted seams cannot fall through to stone-chunk production. The final native
portion may overshoot the target; work speed and yield remain native. Output counters
measure actual spawned stock increases and never certify hauling. Fresh observations
choose another eligible deposit after depletion without moving an existing building.
Depleted owned drills first receive a normal power-switch designation. An eligible
native worker must flick the switch before the released grid capacity can admit a
replacement. Receipts cannot certify this power release, and a cancelled switch
request cannot silently repeat.

Ownership and recovered output persist with the native save. Stock targets are
re-admitted from the paired controller state before supervised simulation and are not
restored as native authority. Cancelled goals suspend owned drilling during supervision;
ordinary player-controlled simulation remains under normal game rules. Drill progress
refreshes the shared watchdog without crediting stock. Surface and deep acceptance
must verify pawn outcomes separately from receipts and compilation.
