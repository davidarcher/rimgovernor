# Material extraction contracts

[Documentation](../../README.md) · [Space and resources](../architecture/space-and-resources.md)

`MaintainResource` uses the shared concern, plan, reservations and Hands path. A native resource census
ranks eligible sources by distance and stable identity. Whether a mine opens at all is the Round's
[supply plan](../architecture/space-and-resources.md#resource-demand-and-acquisition-scoring): the
planner dispatches only the mine sources the plan opened for the deficit, in rank order, and a
resource the plan opened a bill or a chop for is not also mined past its deficit. Each batch holds one excavation target, or at
most eight plant sources, and accounts for all eligible pending yield, including sources beyond the
displayed census. Estimated yield never satisfies a stock target; fresh observations select another
deposit after depletion.

## Surface mining

- Reach is resource reach, not a fixed distance from a pawn. Visible deposits and meteorite rock inside
  it are candidates only while the selected resource target is unmet.
- Each method designates at most one rock. Existing designations reserve estimated yield and are
  never adopted or removed. Fresh stock stops further methods once the target is met; the last rock
  can overshoot by its native yield. No tunnel is opened to reach interior ore.
- Requires a capable colonist with safe native reachability. Refused: unknown cells, any roof within
  the game's roof-support radius, pending roof collapse, adjacent structures, blueprints, frames,
  zones or home area. This excludes supported tunnels as well as unsafe excavations.
- Native eligibility and exact colony/load/map, source identity and coordinates are rechecked while
  paused before designation.
- A deposit kept back is a hold with an explicit reason ([remote work
  holds](controller-contracts.md#remote-work-holds-and-resume)): `threat_present`,
  `urgent_competing_work`, `roof_support_risk` (not an open-surface rock), `route_unsafe`,
  `missing_storage`, or the reach stage. The planner logs the first hold when nothing is selected.

## Material runway and fabrication

Steel, ComponentIndustrial and Plasteel use journaled construction placements and completed
production batches over at most fifteen game days (at least one day of history). Stock above the
reserve and known safe surface ore determine the five-day runway deficit; ore is prospective yield,
never spendable stock. Plasteel construction costs join the history; bill consumption without a
Plasteel quantity stays unknown.

- Unknown consumption or ore keeps the forecast unknown. At a known zero consumption rate days left
  is unknown, and stock below reserve still raises a deficit.
- These targets reuse `MaintainResource`. The routines API and development panel expose the forecast,
  including unknown values.
- Component fabrication needs a measured component deficit, a researched native recipe and a suitable
  bench, staged through the workshop prerequisites. Each additional component budgets twelve usable
  steel while keeping the steel reserve plus five days of consumption. Prospective ore never funds a
  bill.
- Snapshot tests (`buildingruntime/rounds_ladder_snapshot_test.go`) cover the decisions from recorded
  step reads: the deep drill step sites the seeded steel lump, and the resource step funds a component
  bill on a standing fabrication bench. Native stock growth from either is not certified.

## Go deep-drill planning

A measured Steel or Plasteel runway deficit adds `DeepDrilling`, then `GroundPenetratingScanner`, to
the derived research needs.

- The planner requires both projects and a built ground scanner before considering positive scanned
  lumps of the needed resource, ordered by distance from the colony centre. It previews a drill at the
  reported resource cell and requires native reachability plus a completely observed, clear,
  unroofed footprint.
- Shared building admission keeps spending limits and footprint reservations; the power planner
  includes the pending drill's declared draw. An existing drill or drill blueprint prevents another
  placement.
- The deep resource census reports every colonist drill with its exact next deposit (resource,
  remaining units) and the native depletion verdict. Under autonomous play every colonist drill is the
  controller's, so there is no ownership ledger.
- While a metal runway is in deficit, a drill the census reads as depleted is removed even when no
  scanned lumps remain, through the shared Hands deconstruction path (a drill `Deconstruction`,
  bounded attempts per drill). The dispatch guard re-reads the census and designates only while the
  exact drill (id, definition, cell) is still present and depleted. A lump centre moving as it is
  mined, an unknown census or a still yielding seam never triggers removal; yielding and
  already-designated drills keep holding placement.

## Excavation

Rooms and corridors are excavated through a separate contract that never relaxes the resource guard
above. An excavation is keyed by cell and rock definition, never by ThingID, and completes when the
cell no longer holds a mineable; yield is incidental.

- **Site read** (`rimgovernor/observations_read_excavation_site`): for up to 64 cells, fog, rock,
  roof, designation and per-cell eligibility, plus a site-level counterfactual roof-support verdict
  for removing the whole set, a pending-collapse flag, miner availability and reachability of the
  access cell (a standing cell beside it a mobile colonist can path to; a walled-off mouth is
  unreachable even when the pocket behind it is walkable). A fogged cell is unknown, never eligible
  and never a support witness, so support stays unknown until the pawns have opened enough rock to see.
- **`MINE` Designate** under the `mine_safety` guard: checks the rock live, rejects an unsupported
  single-cell removal (a pending collapse elsewhere admits and holds the pick), adopts an existing
  Mine designation idempotently (reported as adopted), adopts an already cleared cell as done
  (applied with cleared evidence, no designation), and records a save-persistent excavation record.
- **Guard:** rechecks cell eligibility and set-based support before each pick hit. When the
  surroundings changed it records the blocker, cancels the designation and ends the job, so the next
  site read shows the rock undesignated with that blocker. The resource radius rule does not apply.
- Designation, job completion and clearance remain native outcomes; a receipt is not a certificate of
  a usable room.

## Owned designations

- Owned designations keep a save-persistent mining record. A native guard rechecks geometry before
  each pick hit and stops the job when the surroundings become unsafe. Unowned mining follows ordinary
  player rules. Normal damage, labor, skill and yield calculations are preserved.
- Records distinguish designation, the native destruction tick and the actual output increase. Output
  certifies neither hauling, continued stock availability nor mine safety.
- RimWorld recreates compressed rocks with new ThingIDs on load: a pending record rebinds only to the
  exact map, cell, definition and health verified while writing that save. A stable source identity
  preserves interruption history across the rebind; a replacement rock cannot inherit the old
  authorization.
- Interrupted sources cannot return in a differently composed batch. Explicitly renewing the resource
  concern reopens its methods; Hands still checks native eligibility. Uncertain writes keep the shared
  execution safeguards.
- Observed hit-point reductions of a designated rock refresh the shared progress watchdog without
  crediting stock; unchanged health and designation receipts do not. A fresh native read may reopen a
  watchdog hold but never a player interruption, and never accepts evidence from another load or map.

## Storage before mining

Before a new batch, native storage reads conservatively count reachable empty floor slots accepting
the exact resource (roofed for deteriorating materials). When capacity is insufficient the concern first
schedules a new stockpile through the zone action and its native geometry/filter postconditions. Its
filter starts empty and allows only the output definition. Player zones are never repurposed. Native
haul eligibility stays authoritative; storage capacity is not completed hauling.

## Deep extraction

`MaintainResource` with `deep_extraction` needs explicit player direction accepting native drilling
infestation risk. The census also reports installed scanner/drill definitions, costs, research,
drill readiness and visible deep deposits when the native scanner overlay is available.

- After surface sources are exhausted the concern stages exact-resource storage, then researched
  scanner/drill facilities through ordinary construction preflight, resource commitments and Hands.
  Candidate footprints must be visible, empty and unroofed, with safe worker access and a native
  connection to a grid whose observed surplus covers the equipment.
- Missing research, power, labor or deposits block development with their evidence; the concern grants
  no research, generates no power, reveals no undiscovered deposits and suppresses no infestations.
- Only confirmed new drill blueprints enter the supervised policy. Blueprint-to-frame and
  frame-to-building transitions bind the exact owned drill; player drills and unrelated replacement
  buildings are never adopted.
- Before each native work interval owned drills check the exact resource, retained building identity,
  forbidden state and current stock target. Depleted seams cannot fall through to stone-chunk
  production. The final portion may overshoot; work speed and yield stay native. Output counters
  measure actual spawned stock increases, never hauling. Fresh observations pick another deposit
  after depletion without moving an existing building.
- A depleted owned drill first gets a normal power-switch designation; an eligible native worker must
  flick the switch before the released grid capacity can admit a replacement. Receipts cannot
  certify this release, and a cancelled switch request cannot silently repeat.
- Ownership and recovered output persist with the native save. Stock targets are re-admitted from the
  paired controller state before supervised simulation, not restored as native authority. Cancelled
  concerns suspend owned drilling during supervision. Drill progress refreshes the shared watchdog
  without crediting stock.
- Surface and deep acceptance must verify pawn outcomes separately from receipts and compilation.
