# Material extraction contracts

[Documentation](../README.md) · [Space and resources](../explanation/space-and-resources.md)

`MaintainResource` uses the shared goal, plan, reservations and Hands path. A native
resource census ranks eligible sources by distance and stable identity. Each batch
contains one excavation target, or at most eight plant sources, and accounts for all eligible pending yield,
including sources beyond the displayed census. Estimated yield cannot satisfy a stock
target. Fresh observations select another deposit after depletion.

Surface mining requires a capable colonist with safe native reachability. Excavation
refuses unknown cells or any roof within the installed game's roof-support radius,
pending roof collapse, adjacent structures, blueprints, frames, zones or home area.
This deliberately excludes supported tunnels as well as unsafe excavations. Native
eligibility and exact colony/load/map, source identity and coordinates are rechecked
while paused before designation.

Owned designations retain a save-persistent mining record. A native guard rechecks
geometry before each pick hit, stopping the current job when the surroundings become
unsafe. Unowned mining follows ordinary player rules. The guard preserves normal
damage, labor, skill and yield calculations. Records distinguish designation from the
native destruction tick and actual output increase. Output is not a certificate of
hauling, continued stock availability or arbitrary mine safety.

Interrupted sources cannot silently return in a differently composed batch. Explicitly
renewing the resource goal reopens its methods; Hands still checks current native
eligibility. Uncertain writes retain the shared execution safeguards.

Before a new mining batch, native storage reads conservatively count reachable empty
floor slots accepting the exact resource, with roofing for deteriorating materials.
When capacity is insufficient, the goal first schedules a new stockpile through the
existing zone action and its native geometry/filter postconditions. Its filter starts
empty and allows only the output definition. Player zones are never repurposed. Native
haul eligibility remains authoritative; storage capacity is not completed hauling.

The census also reports installed scanner/drill definitions, costs, research, existing
drill readiness and visible deep deposits when the native scanner overlay is available.
These observations do not authorize deep drilling or certify its infestation risk.
Deep facility planning and acceptance remain tracked in [the backlog](../BACKLOG.md).
