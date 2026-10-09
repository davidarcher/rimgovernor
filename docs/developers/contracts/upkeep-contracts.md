# Colony upkeep contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

Equipment and apparel are their own contracts: the [loadout model](equipment-upkeep.md),
the [apparel policy operation](apparel-policy.md) and the [weapon planner](weapon-planner.md).

Upkeep uses the existing ColonyPlan, resource admission and Hands executor.
`rimgovernor/observations_read_colony_facts` `upkeep` is versioned read-only native evidence. Each section is
independently nullable with an error; an empty successful census differs from an
unavailable read. The section tick must match the enclosing observation.
The repair structure census includes only native definitions with `useHitPoints`, so
non-damageable markers such as sleeping spots are never repair targets.

## Native capability audit

| Need | Available native evidence and action boundary |
| --- | --- |
| Supplies | Per-item identity, location, quantity, condition, deterioration stat, roof, valid storage, rot deadline, forbidden status. `HaulIntent` previews native storage access; `requireSafeStorage` rechecks enabled hauling, safe reach and covered storage at dispatch. |
| Storage | Slot cells, roof, occupancy and item-specific filtered capacity. Cell count alone is not usable capacity; native hauling decides worker access and delivery. |
| Sleeping | Bed definition, slots, owners, current users, pawn-specific access, roof, temperature. Capacity does not establish use or suitable worn protection. |
| Home and structures | Occupied/protected cells with home coverage; building condition, material, roof, native construction lineage. `Observations.ReadRoofSupport` checks roof connectivity with one wall excluded; it does not prove enclosure, escape routes or replacement admission. |
| Fire, cleaning, repair | `GiveJobIntent` Repair/Clean use the installed WorkGivers and their normal eligibility, and need home coverage, safe access and enabled work. Firefighting is not directly orderable: enabled workers respond normally while the controller watches at most three home fires of size at most one. |
| Pawns, animals, medicine | `observations_list_pawns` supplies medical, work, settings and schedule reads. Animal feed counts only reachable stored feed under native eating eligibility and allowed-area access (no pasture or future harvest); pets have no pen-containment predicate. Medicine is identified by native item definitions; no future production or season is credited as stock. |

## Maintained jobs

### Home coverage

`MaintainHomeCoverage` uses the complete native building and stockpile geometry
through `policy.PlanHomeArea`. A building anchors its occupied cells plus
connected visible, enclosed, fully roofed rooms; traversal crosses usable colony
doors only between enclosed rooms. A stockpile contributes its current footprint.
Targets use native unique load IDs, whoever built or zoned them.

The policy adds a four-cell Chebyshev margin, retains the largest extent region
and regions within `HomeAreaOutlier` (16 cells) of it, and computes the diff from
the current Home mask. Far outposts and other cells outside this base target are
cleared. Unknown geometry, Home cells or auto-expand state prevents a plan.

The journaled method disables vanilla auto-expand when needed, then sends
`AreaIntent` set_cells and clear_cells through Hands. A hash of the diff names
the method; applied results are terminal and the next review rereads native
state. Every missing target cell is restoration work, however Home was removed.
The native target census also carries bounded 256-cell batches, but the policy
uses its separate complete geometry to derive the base and margin.

Acceptance: `upkeep/home-coverage` and `upkeep/colony-extent`. The latter verifies
corridor and margin maintenance, exclusion beyond the margin, and a byte-identical
Home mask across a subsequent extent review.

### Construction lineage and extent

- Native lineage follows bridge-created blueprints into frames and finished buildings,
  including failed-construction recovery. A finished identity is captured during the
  native frame completion call; coordinates alone never transfer ownership. Records
  persist with the game, report missing or ambiguous identities explicitly and are
  never rebound by coordinates. The ledger keeps at most 4,096 origins. Lineage does
  not authorize demolition or establish safe removal.
- `policy.CurrentConstruction` is the planning-ownership view: a complete bounded census
  of built artificial player-faction buildings with identity, definition, material,
  rotation, anchor and cells. Home coverage, stone-shell planning and extent consumers
  share it ([colony extent contract](colony-extent.md#consumer-contract)). Unknown or
  incomplete observations do not establish eligibility; missing history does not exclude
  a player-built facility; replacement geometry never inherits causal history.
- `policy.DeriveColonyExtent` is a pure current-territory model joining
  `CurrentConstruction`, optional provenance and census stockpile footprints into sorted
  four-neighbor regions. Cell provenance is `facility`, `enclosed_interior`, `corridor`
  or `margin` (explicit 0-8 Chebyshev radius clipped to the map; overlapping margins
  never join regions). Nothing fills gaps between facilities, wall fragments or islands.
- Home repair batches are not complete geometry. `HomeCoverageTarget.ExtentGeometry` is the
  separate complete, unbatched interior/corridor fact (known empty means footprint only;
  every cell must connect to the footprint). Missing census, bounds, target or geometry keeps
  extent unknown. Persistence, the consumer contract and the rule that extent growth never
  paints Home are in the [colony extent contract](colony-extent.md) and
  [persistence contracts](persistence-contracts.md#what-must-survive).

### Resource reach and extent eligibility

- `policy.ResourceReach` limits candidate consideration to `base`, `near`, `far` or
  `map`, with a reason on every decision. Base is exact extent cells; near adds a
  12-cell Chebyshev margin. Near needs two armed colonists, positive hauling capacity
  and storage headroom, no unknown or present threat, and raid points at most 100 per
  armed colonist. Far needs six armed colonists, a quiet storyteller and two free
  haulers; map needs three. These are selection thresholds, not combat predictions.
- `FilterResourceReach` intersects the ceiling with map bounds, a positive candidate
  eligibility verdict and an observed passable route (also at base/map). Distance never
  supplies route evidence. Dispatch is unchanged.
- The routines API's `resourceReach.stage/reason` projects held colony facts. Unknown
  extent reports `base` / `extent_unknown` and admits nothing. A known empty extent
  (no facility or claimed stockpile yet) carries no readiness evidence: readiness alone
  stages reach and only missing geometry denies base and near.
- `policy.ExtentEligibility` overlays current evidence on stored history without
  mutating it. Per-region diagnostics keep origins and facility IDs, list active
  facilities separately and report holds: `threat_present`, `threat_unknown`,
  `facility_lost`, `facilities_unknown`, `route_unknown`, `route_impassable`. Losing any
  supporting facility holds the region; a map-wide threat holds every region; missing
  evidence never grants permission; recovery clears holds, not history. The routines
  API `extentEligibility` shows this separately from `resourceReach`, reading the current
  timeline's stored history and fresh held census without writing either. It neither
  paints Home nor changes selection or dispatch. The acceptance harness records
  `/api/routines` before shutdown for `acceptance why` and postmortems.

### Stone shell and deconstruction

`MaintainStoneShell` admits one wall upgrade after urgent needs, from currently observed
colony walls only.

- A straight-wall bundle reserves three stone backup walls, guarded removal of the original,
  one permanent stone wall and guarded cleanup of each backup. Exterior cells must be empty,
  side walls unchanged, the original interior enclosed and roofed. Installed costs and shared
  reservations cover all four walls before demolition; runtime estimates never overwrite
  validated native allocations. Stone comes from ordinary resource recipes and may build one
  native stonecutter. Site selection requires the support-check radius around every temporary
  wall to be visible and within the map; execution repeats the full check.
- Corners need verified roof support, both neighboring walls and clear exterior cardinal
  approaches reachable by enabled haulers; only the permanent wall is reserved.
- Observed native demolition transfers its project slot to the exact replacement construction
  reference; verified completion satisfies the slot and replacement loss reopens it. Editing
  the retired slot's action or removal dependency invalidates the exemption.
- The roof-support preview counts connected existing roof cells within the native support
  radius with the wall excluded; it changes nothing. Fog, map-edge uncertainty, pending
  collapse and unsupported cells refuse the certificate; planned supports earn no credit;
  replacement must preserve enclosure and escape access and repeat checks at execution.

`Designate` DECONSTRUCT on a building `target` under the `enclosure` guard clears one exact
building through `Designator_Deconstruct`; native rechecks deconstructibility, visible
geometry, remaining roof support and any pending wall upgrade at apply.

- Enclosing colony walls require the `wall_upgrade` guard. Otherwise `cleared_ground` allows
  a player wall or door whose every enclosed room (eight neighbours) lies inside the ground;
  a room reaching outside is refused; while those rooms keep a roof the designation stands
  with pawns held (`DeconstructEffect.waiting_for_roof`). Clearance first issues
  `remove_roof` over the rooms.
- `replace_with_wall` (no cleared ground) swaps a 1x1 player door for a Wall of the door's
  stuff when the door stands on a planned room's wall ring with no planned door: if
  `GenConstruct.CanPlaceBlueprintAt` accepts the wall over the door, native places it and the
  construct work giver removes the door; otherwise the door is designated, the nearest capable
  builder deconstructs it, then the wall blueprint is placed and queued first. Both orders are
  player-forced. `DeconstructEffect.replacement_id` names the wall blueprint.
- The intent adopts a standing designation rather than placing a second. Applied means
  designated, not demolished. Ownership is scoped to the loaded game; loaded designations stay
  untouched until adopted; revoking authority releases exactly the controller-owned pending
  designations and player replacements survive.
- `wall_upgrade` completion comes from the native deconstruction job, not a wall disappearing;
  the guard rechecks supporting identities, enclosure, roofs, materials and resource policies.
  Jobs need active supervised simulation. Native UI input, a load/map change or changed safety
  invalidates pending demolition; Manual suspends it. Cleanup requires the completed permanent
  wall; missing or uncertain outcomes stay blocked. Native evidence must confirm retirement,
  the surviving exact target and the absent designation before the shared plan cancels
  unissued descendants; issued construction stays observed; cancellation history prevents
  duplicate replacement.

### Defensive layout

`EnsureDefensiveLayout` (opt-in) commits one stored corridor layout per colony and builds
it tier by tier; a tier is `Built` only while every building is observed in the defense-site
census. Geometry is the `policy` layout planner's: a zigzag corridor kill zone with a single
defenders' doorway, sized from defenders and turrets, no door on the raiders' route.

- Traps sit on the raiders' cheapest line (a spike trap costs a faction pawn 800, raiders
  nothing) while the hallway keeps colonists a cheaper trap-free route
  (`policy.hallwayAvoidsTraps`, `policy.colonistRouteAvoidsTraps`). Shooter cell floors are
  terrain; a floor the native preview refuses is dropped from the tier.
- A `Complete` record is re-verified after each ActiveCombat epoch and once per game hour:
  a tier that lost a building re-opens with a fresh retry budget as a new tier method while
  `Complete` stays true. A missing building whose cell carries a blueprint or frame is not
  placed again; the planner asks for a clock window. Defenders are undrafted once
  ActiveCombat stops authorizing the hold plan.
- Combat holds the line only against an ordinary edge assault in front of the cover row
  (`policy.BehindFiringLine`). Sieges, sapper or breach toil, drops, engaged raiders and
  raiders past the cover row get squad defense. A raider at or behind the cover row or lord
  evidence turning to breach/sapper cancels the hold's orders (`hold_fallback`); unknown
  position or lord evidence never abandons a hold.
- The `turrets` tier needs observed gates: turret definition researched in the census, a
  generating network with spare watts for every draw, stock covering turrets and conduits.
  Placement, spacing and conduit routing are the planner's; an unroutable turret is dropped.
  A layout without turrets re-probes once per game hour. A standing unpowered turret is a
  deficit (`unpowered`) owned by `EnsureBasicPower`'s network; damage is
  `MaintainEssentialRepairs`'. While every tier stands the concern reports a known zero
  deficit so other upkeep concerns take the free slot.
- An observed empty barrel (`unfuelled`) is a tier deficit. With fuel in stock the concern
  issues one forced `recovery_service` refuel per step on an available Hauling-enabled
  colonist (up to four attempts per turret and Episode). With none, the fuel runway
  (below) already demands the refill. Unknown fuel state is neither deficit nor order.

### Fuel runway

Fuel is a runway, not a standing floor (`policy.PlanFuelRunway`). Every refuelable
building of the power census (generators, turret barrels) is a `FuelConsumer`; its def's
`CompProperties_Refuelable` gives the burn (`fuelConsumptionRate` fuel units a day,
`fuelMultiplier` fuel units per item; `bridge.RefuelBurn`). A comp burning continuously
(native `CompTick`) burns while switched on and, if `consumeFuelOnlyWhenPowered`, powered;
it needs the horizon's burn (`ProjectionHorizonDays`) less its tank, in items, as a stock
level kept in the Rounder's `constructionMemory`. A barrel already empty has runway zero
whatever its rate and needs its refill to target. A comp that burns per use
(`consumeFuelOnlyWhenUsed`, a turret per shot) holding fuel has no daily rate: it is
listed in `FuelProjection.Gaps`. Any consumer whose fuel level, burn, gate or fuel item is
unknown makes the projection unknown and asks for nothing. The shortfall is the
projector's `Fuel` domain (`ShadowFuel`, under `MaintainResource`). Difficulty's
maintenance cost factor, which scales `fuelMultiplier` for a `factorByDifficulty` comp
(turret barrels), is not observed; the def's own multiplier is used.

### Clearance

`ClearHomeObstructions` (the `clearance` family) admits one non-player building inside
Home at a time, nearest the colony center first.

- A fresh clearance census must report deconstructible geometry with no roof blocker,
  ancient danger or casket. A standing deconstruct designation is no hold: the admitted
  Deconstruction adopts it. Repairs precede clearance, clearance precedes direct
  cleaning, and shared emergency admission still wins. Removals run through the
  recovery queue (tier 1 room obstructions first, then nearest); the roof-first batch
  takes a roof off before its holders. Each held target and reason is a held
  `RecoveryQueue` entry.
- Chunks are hauls: an allowed, unstored chunk stack in Home with no store cell
  ordinary hauling would use is a clearance deficit; no stockpile is created for it.
  Forbidden chunks are the supply safety policy's. Once a store takes a chunk, ordinary
  hauling carries it: the native mod sets `alwaysHaulable` on the chunk defs at startup
  (#2513), so no Haul designation is ordered and the clearance goal only waits.
- `ClearAncientShrine` (the `shrine` family) holds the clearance concern while it has work
  (see the breach concern in `controller-contracts.md`).
- Unknown observations preserve the previous need. Recovery needs no eligible candidate
  and no unresolved issued action; recurrence keeps the concern identity. Native rechecks
  eligibility at apply; the action completes on its applied result and the census
  decides recovery. Salvage uses ordinary hauling and does not gate the concern.
- Remote clearance considers visible abandoned buildings outside Home only when
  resource reach permits their observed safe route and native salvage yield scores
  against unmet resource demand with storage headroom. Roof-support blockers, caskets
  and sealed ancient-danger rooms stay held; a remote ruin's hold names its
  [reason](controller-contracts.md#remote-work-holds-and-resume) (`threat_present`,
  `urgent_competing_work`, `roof_support_risk`, `route_unsafe`, `missing_storage`). One
  removal is selected per fresh census; dispatch revalidates the census and emergency
  read and resumes without a duplicate designation. Selection never adds Home cells.

### Repairs, cleaning, fire

- `MaintainEssentialRepairs`, `MaintainCleanFacilities` and `MaintainFireSafety` keep
  their concern identities across recovery and recurrence. Any observed target enters
  maintenance; recovery needs no deficit and no unresolved issued action. Missing
  evidence retains active risk; a first unavailable read is an ordinary visible blocker,
  not an invented emergency.
- Fire risk preempts development only while the fire family is declared; without it
  the review records the need without suspending other concerns. Unknown or oversized fire
  intervention retains an emergency hold, including a fire that grows oversized while
  firefighting is under way. Fire monitoring uses Normal speed and at most 60 ticks
  between reviews; unavailable safe workers retain the hold. `MaintainFireSafety` has no
  order to issue: the `fire` family only grants the clock a bounded native-work window
  while `EvaluateFireSafety` finds a bounded fire with an eligible firefighter.
- A pending, never-issued repair is cancelled on the next repair step, before the need
  gate, once the concern recovered or its hold says the structure is ineligible.
- Methods inspect at most eight targets and eight enabled, available workers per
  review; stable IDs break ties. Medicine and rot deadlines rank hauling; native medical
  beds, temperature controls and generators lead repairs, then roof holders, beds and
  worktables; relative damage ranks within a category before cosmetic repairs.
- Cleaning targets only home-area filth in a workspace (enclosed kitchen, hospital,
  laboratory, or enclosed room with a cooking bench) whose native Cleanliness stat has
  latched dirty (enter below -1, release at -0.25; latch keyed by the room's lowest cell,
  with its entry tick, persisted in `rounds`), after a 30,000-tick grace when a
  Cleaning-enabled colonist is present, at once with none. A room's filth is the census
  filth in the room plus home-area filth on a cell touching it (8-way). Filth elsewhere
  and in inherently dirty rooms (barns, butcher benches) is ordinary colonist work.
  A clean order is player-forced: any colonist not incapable of Cleaning carries it, and
  the worker also cleans what the WorkGiver queues beside it. The controller encodes no
  filth-age threshold. Admission uses the live preview tick (native revalidates tokens;
  the executor bounds inspection age in wall time; tick advance between pawn read and
  preview alone does not stale a preview).
- Opportunistic bench cleaning is native and needs no Go data (`BenchCleaningGuard`, #2515):
  under `Supervisor.IsActive`, a Harmony postfix on `WorkGiver_DoBill.JobOnThing` replaces an
  unforced bill job with one `Clean` job when home-area filth the clean WorkGiver accepts and
  the pawn can reach lies within 6 cells of the bench, so a pawn cleans before starting a bill.
  Caps: at most 5 filth per trip; never for a pawn whose Cleaning work type is disabled or at
  priority 0; never for a drafted, downed, mentally broken, bleeding, tend-needy,
  player-forced or priority-work pawn; forced bill orders are untouched. The same
  `CleanJob` helper is meant to serve the after-tending and before-surgery triggers.
  It overlaps the Go `MaintainCleanFacilities` planners only in effect; neither consults the other.
- Butcher placements never share a room with a cooking bench; a colony whose every butcher
  bench shares a cooking room is admitted one more `ButcherSpot` outside. The spot and its
  `ButcherCorpseFlesh` bill are foothold work owed whenever the food runway is under
  `FoodTargetDays`, armed or not; the butcher bill waits until `butcher-spot-separated` has
  been tried, then prefers the separated bench.
- Methods preserve forbidden items, player work overrides, schedules, drafts,
  player-forced jobs, storage filters and home areas. Deterioration and growing fires do
  not reset the progress watchdog.
- Blocked upkeep reconsiders at once on changed target eligibility, worker availability
  or research. Position, rot-timer and temperature drift alone do not reopen a failed
  method. A 2,500-tick review window catches changed capacity or routes; outstanding
  receipts and watchdog holds stay authoritative.
- An `upkeep_target` action waits after the native job receipt: hauling needs the same
  item identity and at least its original quantity in roofed valid storage; repair needs
  full observed health; cleaning needs target absence from a complete census; fire needs
  no remaining home fire. Disappearing hauled items (including merges) and partial
  delivery stay blockers. Load or player-direction changes invalidate ownership;
  uncertain writes are not replayed; the no-progress watchdog bounds waiting. Verified
  completed orders may be retired through the action archive when the target needs
  maintenance again; waiting, blocked, uncertain or cancelled work cannot be.

### Supplies and storage

- The typed item census covers home-area items, items in valid storage anywhere, and
  deteriorating, perishable or medicine stacks wherever dropped. Natural chunk/slag
  fields outside the home area never enter it (a whole-map census exceeded the bound).
  Item, flooring and medical-reserve censuses read each def's rows from the catalog; a
  def or terrain with no row or stat fails the colony read with an error naming the
  census and def.
- Supplies need both roofing and valid storage. The base deterioration rate identifies
  vulnerable items even when the current rate is zero under a roof; current deterioration
  and rot deadlines are separate observations.
- Guarded hauling returns a native quantity-tracking ID; the saved `hauling` section follows
  the whole source stack through splits and merges, and mixing other stock in extends
  protection to the whole mixture. Held or partial delivery does not satisfy delivery;
  destruction before protection and quantity changes outside verified transfers are explicit
  failures. Completion is recorded at the first tick every tracked piece is in covered valid
  storage and survives later consumption; the controller also requires the exact receipt ID,
  source, worker, original quantity and load/direction. Unresolved issued work keeps the concern
  open when the source ID leaves the loose-item census. Records use saved string identities
  (never coordinates), at most 512 orders and 128 pieces per order; missing identities and
  exhausted capacity cannot prove delivery.
- `storageCapacity` reports each item's unreserved covered slot capacity using native
  acceptance, stacking and cell limits, excluding reserved, fire and construction-blocked
  cells. It is physical capacity, not a route or reservation; different items compete
  for cells, so capacities cannot be summed.
- When hauling reports no storage the method can create a filtered 2x2 stockpile in
  existing covered space, preserving plants, items, buildings, zones, committed geometry
  and walkways; it previews at most eight candidates, admits at most three such
  stockpiles and filters only the observed target definitions. An atomic native guard
  rechecks roof, occupancy and zone ownership; exact geometry and filter readback
  complete the zone action; the concern still needs observed protected supplies. With no
  fitting covered space, one 6x6 supply shell may be admitted through shared
  construction (at most three sites previewed, entrance aisle free, enclosure and full
  roof observed before the zone). No duplicate rooms after interruption; other
  stockpiles' filters never change.

### Housing

`MaintainHousing` is one concern with ordered phases recorded as `Latches.Housing`:
`shelter` (first sleeping places and roofed shell, foothold priority), `sleeping` (bed
ownership and upgrades), and from `StageReserves` `expansion` (one spare indoor place).
Only the current phase's planner acts.

- The sleeping phase is declared by the `sleeping` family; with it the deficit is
  method-available and ranks like any development row, without it method-unavailable.
  Each review re-derives targets (colonists without an owned suitable bed or without
  observed use of one) from the upkeep census and retained use history.
- Assign first: the lowest waiting colonist with a vacant suitable bed gets the lowest
  such bed through the typed `assign` operation, one per Episode, carrying the colonist's
  expected previous bed so a player change is refused, not overwritten. An attempt the
  native side never admitted (CAS token moved) is retried up to three times in the
  epoch. Native re-checks vacancy, humanlike/non-medical/non-prisoner eligibility, roof,
  forbidden state, allowed area, reach and comfortable temperature together and never
  evicts another owner.
- Only when nobody can be assigned is one `Bed` staged through the shared building
  ladder in a Bedroom, Barracks or generic Room whose observed temperature lies in the
  comfortable band of every unhoused colonist; with no such room the ladder falls to its
  starter shell, which waits while initial shelter is owed. One bed per method; the
  staged bed is assigned on a later review.
- Bed rung: `Bed`, else a `Bedroll` whose stuff (first `stuff_options` entry the stock
  covers) is on hand. A bedroll is suitable only while `Bed` is unavailable, after which
  its owners are upgrade targets. A sleeping spot is never suitable. While every
  colonist owns some bed the bedroom ladder (shell with door, furnish, move) runs ahead
  of any barracks bed at every tier. Furnishing falls back to a `SleepingSpot` the owner
  moves into; the vacated shelter-shell spot is deconstructed.
- Assignment and construction receipts do not complete the deficit. Recovery requires
  observed use by the assigned pawn in a suitable bed, kept for that bed and load.
  Missing reads, changed assignments, access loss and unsafe temperature reopen it.
  Natural sleep uses the existing schedule; rest and timetables are never forced.

### Medical reserves

`MaintainMedicalReserves` starts below the configured medicine units per colonist and
stays active until the higher recovery reserve (defaults one and three; planning policy,
not demand forecasts). Current usable reachable medicine of any native definition counts;
forbidden, expired, unreachable and future stock cannot. Replenishment uses the native
herbal-medicine definition and the shared source/production method; PlantCutting work
joins shared allocation. When no bench recipe can produce it, the planner harvests
undesignated medicine-yielding wild plants from the acquisition census (designated plants
count as pending; plants below harvest growth are not sources). Unavailable sources and
recipes are blockers. Patient care, drug policies and player bills are preserved;
designations and bill receipts never prove replenishment.

Medicine is also a resource runway: the herbal definition is forecast with a zero
reserve beside the configured resource targets (`RoundsPolicy.RunwayReserves`), its rate
the observed `medicine_tend` spend over the 15-day window. Below five days of runway
the resource ladder is asked for five days of use; an unread rate or stock leaves the
runway unknown, and a quiet colony demands nothing. There is no per-colonist production
floor beyond the reserve latch above.

### Animals

`MaintainAnimalContainment` observes pen membership for eligible starting animals. Pets and
animals marked for release or slaughter get no pen request. Suitable existing pens are
reused through native handling (Handling joins shared allocation without overriding disabled
work); with none, one pen marker claims the yard inside the defensive wall once it is closed, built through
the supply-room enclosure checks. Fences or a marker do not complete the concern: the animal
must be observed in a suitable pen. No breeding, bonding, master or removal setting changes.

Herd layout (`HerdPlan.PenAnimals`, summed population ceilings, at least six):
a roofed barn with a sleeping spot per animal and an animal flap into the paddock (at
least eight; placed sites never move), and a vet room with animal beds (one per 10 animals, at least two), all inside
the outer ring; a herd the rooms cannot hold gets an extra reservation, never a resized one.
The vet room is outside animal areas (animals enter when carried or via the bot-owned
`VetRoom` allowed area, `VetRoomAreaKey`).

- Containment reconciles each barn and vet room (`policy.HerdRooms`)
  through the shared build side (`policy.NextHerdStep` returns `HerdReconcile` with the
  template: the `AnimalSleepingSpot`s or `AnimalBed`s and the barn's heater;
  `buildingruntime/rounds_herd_rooms.go` calls `reconcileRoom`), so a lost wall is rebuilt
  like a first shell and the beds are installed from packed stock first; the concern stays
  open while a step is due
  (`HerdRoomsOwed`). A definition the live catalog lacks fails the review.
- The barn owes one optional powered heater (`RoomFurniture.Heater`, planned by `planBarn`
  on the floor furthest from the door, wanted beside the beds); `TemperatureCooling.Conditioned`
  counts a room with one as heated. No barn cooler is planned.
- The barn interior is the bot-owned `Barn` allowed area (`BarnAreaKey`) that pen animals
  enter while exposure endangers their race ([husbandry contracts](husbandry-contracts.md)).
  Each standing vet bed is flagged medical by a `BedUse` patch. Each bonded animal's master
  (`policy.CompanionMaster`) with a solo bedroom gets one `AnimalSleepingSpot` per animal
  (`policy.NextCompanionBed`, one per review). Pasture rotation is not built.

The animal feed runway (#2379, `policy.PlanAnimalFeedRunway`) replaces the old
`MaintainAnimalFeed` reserve. It is a forward-projector domain beside the fuel runway: per
race group the consumption is the sum of the food forecast's `NutritionPerDay`, the pens'
worst-quadrum pasture (per pen the lesser of demand and pasture, shared between groups in
proportion to consumption) offsets it, and the unheld edible stock every animal of the
group can eat covers the rest for a runway of days against `ProjectionHorizonDays`. A
shorter runway demands the items the missing nutrition takes (the stock's lowest-defName
item, else the cheapest `FeedItems` a bench makes) as a `MaintainResource` stock level,
merged into the Rounder's construction memory like the fuel needs; kibble and hay are made
on any bench and the old bench-reachability and delivery-fallback constraints are gone.
Missing animals, pens, stock or forecast leave the projection unknown, never defaulted. A
short group no item feeds is listed in `Gaps`. `HerdFeedShort`, which gates taming, is
true while the projection is short. Explicit player herd targets are untouched.

Herd growth from known events is in the same runway. Native sends per animal `ticks_to_birth`
(pregnant only), `life_stage_index` and `ticks_to_next_life_stage` on `AnimalState`; the
race catalog row carries `LifeStages` (each stage's start tick and `hungerRateFactor`) and
`LitterSize`, the `Rand.ByCurveAverage` of `litterSizeCurve` (one child without a curve,
never below one). A pregnancy adds that mean litter eating as newborns from the due tick
(the race's `AdultFeedPerDay` scaled by the stage's hunger factor over the last stage's),
stepping up at each later stage start; a young animal steps up at its next stage tick by
the stage factors' ratio. The steps are `AnimalFeedGroupRunway.Steps`; the pasture credit
stays the present herd's. An unread pregnancy, event tick, stage, litter or stage row
leaves the projection unknown; a new conception stays reactive and the litter is an
estimate the census corrects once born.

Each planned barn declares one small Important feed stockpile (`FeedStoreWidth` x
`FeedStoreHeight`, filtered to the races' feed items and hay) in its free floor beside the
sleeping spots (`animalOwner`); animals with no barn use the warehouse or freezer. The
`reachable_stored_feed`, `reachable_benches`, `reachable_storage` and `storage_candidates`
census fields are removed.

### Food storage and refrigeration

`MaintainFoodStorage` tracks perishable, not-yet-rotted nutrition split into stored
(adequately covered or enclosed/cold) and not. It starts when unstored nutrition exceeds
five units and the stored share of perishable nutrition is under the minimum fraction
(default 1/2), and clears only past the target fraction (default 9/10). Non-perishable
and rotted stock never counts.

- Preferred method: relocate at-risk stock into an existing covered/enclosed site with
  observed spare capacity (deterministic choice); only when every known site is unusable
  or exhausted, a StockTarget bill for preserved or non-perishable food through the
  shared source/bill method. Receipts never prove spoilage was averted; the census must
  see the stock stored or the runway recovered.
- Native sets `FoodStock.roofed`, `temperature_c` and `room_id` per row. Stock is stored
  when roofed and either chilled (at or under the catalog's `full_rot_rate_c`) or with at
  least five days of rot runway; a roofed warm stockpile is no deficit unless close to
  rotting.

Spoilage preference (#2520): while the supervisor is active the colony uses its oldest
stock first. Two native Harmony patches (`SpoilagePreference`) only reorder candidates
vanilla already accepts; legality (allowed, reachable, unforbidden, policy, filter) stays
with vanilla, and non-perishables keep vanilla's order. Bill ingredients: the non-mixing
pick (`WorkGiver_DoBill.TryFindBestIngredientsInSet_NoMixHelper`) sorts candidates by
least `TicksUntilRotAtCurrentTemp`, then distance; mixing recipes keep vanilla's value
order. Meals eaten: a postfix on `FoodUtility.FoodOptimality` adds up to 24 points to a
fresh perishable stack as it nears rotting (none beyond 60000 ticks), skipped when taking
food to inventory. Hauling is unchanged. The `lab/spoilage-pick` case proves both picks.

`MaintainRefrigeration` takes the warm side: roofed perishable nutrition in a known room,
warmer than 10 C and under five days from rot. It latches at the same five-unit floor and
releases only when every such stock measures 5 C or colder; unknown temperature or room
facts preserve the latch.

- The method needs an enclosed room, native `Cooler` availability and a wall cell with a
  straight inside-wall-outdoors line. An existing cooler facing the room is patched to
  the freezer target via the building-temperature action, not duplicated; an unpowered
  one defers to `EnsureBasicPower`; one venting into another enclosed room is blocked.
  A cooler receipt or build never clears the deficit; native cooling must be observed on
  the stock. `EnsureBasicPower` holds on out-of-fuel or broken producers (ordinary pawn
  work) and sizes a draining network by daily energy budget
  ([power contracts](power-contracts.md)).
- `EnsureTemperatureSafety` reuses the wall search for a hot sleeping room. Hot rooms are
  served hottest first, cold rooms coldest first. A room with no thermal facility gets
  one powered `Cooler` at the lowest-sorted vented wall cell, cold side in, when `Cooler`
  availability is known true and a connected network's nominal producer capacity exceeds
  demand by the cooler's draw (`policy.TemperatureCooling`, `PowerTopology.SpareW`).
  A known solar flare, unfinished research, unknown power facts or no vented wall keep
  the passive cooler inside the room. A `Cooler` the power census places beside the room
  counts as its facility. The setpoint is the native default (21 C); the family patches
  no setpoints; its draw reaches `EnsureBasicPower` as pending demand while open, measured
  once built.
- Solar flare: while a `SolarFlare` condition with a native remaining-duration read is
  observed (`policy.PowerOutageHold`) the review keeps `EnsureBasicPower` open with
  `method_unavailable` (suspended, never cancelled, not extending the startup hold), and
  the power and refrigeration planners report `solar_flare` (`PowerWaitBlackout`,
  `RefrigerationWaitBlackout`) with no allowance lent. The deficit and latch keep their
  measured state until the flare ends.
- `MaintainRefrigeration` keeps a method under the flare: the cook-ahead planner
  (`policy.CookAheadFood`) raises one `CookMealSimple`-first target bill, in meals, on a
  bench native still reports usable (a fuelled stove; an unpowered electric one is
  excluded) for the latched warm nutrition beyond `ReservedFoodNutrition`. It may add a
  second bill of a recipe the bench already carries, but one claim per bench and recipe
  per load. Without the flare hold or with reserved targets covering the stock it
  proposes nothing.
- During the flare the defensive layout treats its turret tier as absent rather than
  unserviced (`DefenseRearmTurrets` reports no `unpowered` cell; empty barrels are still
  rearmed).

### Lighting and flooring

`MaintainLighting` reasons from measured ground glow at each work table and research bench
interaction cell (`UpkeepFacts.lighting`, with every glowing fixture's radius, lit flag and
service state).

- A roofed work cell under 0.3 latches its bench. Unroofed cells are ignored except under an
  eclipse (`policy.EclipseHold`), when every cell is measured and such benches unlatch after
  it ends. An unknown census preserves the latch. A cell whose room grows a light-sensitive
  plant (`WorkLightCell.light_sensitive`) never latches.
- The method first looks for a fixture reaching the cell: one not lit defers
  (`lamp_power_needed`, `lamp_fuel_needed`, `lamp_repair_needed`, `lamp_switched_off`); a lit
  one within placement radius that leaves the cell dark is blocked; one reaching only from
  further away does not stop a lamp of its own. Otherwise it places the first affordable lamp
  (`StandingLamp` only while a network has an active source, else `TorchLamp`) on the nearest
  free, walkable, unzoned cell of the room within two cells of the interaction cell, each
  validated by native preview. A receipt never clears the deficit; the next census must read
  the cell lit.

`MaintainFlooring` reasons from the terrain under each room cell and its native stats
(`UpkeepFacts.flooring`).

- A clean workspace (cleaning's classification) is deficient while any cell's cleanliness is
  negative; a living room (bedroom, barracks, dining, recreation) while any cell is natural
  ground. Other roles carry no requirement. No hysteresis; an ordered floor still counts as
  deficient but is never ordered twice; an unknown census preserves the latch. Clean
  workspaces rank before living rooms.
- Method: the known-available floor meeting the tier (non-negative cleanliness for clean,
  beauty for living), preferring stock-payable cost over the most cells of a bounded batch,
  then the tier's weighted score. No available floor defers to research, none affordable to
  materials; a room whose deficient cells are all ordered waits. Each cell is validated by
  native preview and admitted as its own action; completion is the terrain grid reading the
  floor, never a receipt.
- A traffic tier joins the routes census: the busiest natural home cells outside tiered rooms
  are deficient once the window holds at least four times the per-cell minimum and the cell
  at least that minimum; a floor with any path cost never qualifies. An unknown routes census
  makes the flooring census unknown; a native without the routes section measures room tiers
  alone.

### Routes

`MaintainRoutes` reasons only from observed reachability and travel, never flood-fill or
straight-line distance. Native (`UpkeepFacts.routes`) reports every facility a colonist
must reach (player beds, work benches at their interaction cell, storage buildings, dining
surfaces, turrets, stockpile zones as `zone-<id>`) with one travel row per mobile colonist
carrying the game's `CanReach` answer and, for the first 256 reachable pairs, path cost and
node count. It also reports up to 16 breach cells per unreachable facility (one-cell player
walls on its room border a door would pass straight through, nearest the reaching colonist
first), pending breaches (ordered doors a measured path crosses) and observed traffic (the
64 busiest cells sampled every 30 ticks, with sample count and window start; the window
restarts on load).

- The decoder requires every travel row's reachability and every traffic cell's samples,
  terrain and home flag, else the census is unknown; the bridge refuses unlisted pawns,
  path numbers on unreachable rows, duplicate breach or traffic cells and out-of-map cells.
- A facility is deficient when at least one mobile colonist is listed and none reaches it.
  It latches by ID and releases only on a measured census reading it reachable with no door
  still ordered on its border. No hysteresis.
- Ranks as development. Serves storage and stockpiles first, then benches, dining, beds,
  defence, opening the room with a `Door` of `WoodLog` on one breach cell (nearest breaches
  previewed north then east; first legal one-cell footprint admitted). The planner treats
  exactly one wiped building with no blueprint, frame or cancelled work as the deliberate
  replacement and marks it safe (`Preview.Blockers`); any other disturbance is refused.
  Reasons: `route_door_pending`, `route_no_breach`, `route_door_unavailable`. A receipt
  never clears the deficit.

### Comfort

`EnsureComfort` has two phases recorded as `Latches.Comfort`.

- `basic` (foothold, priority 2): once initial shelter roof and sleeping gates hold, one
  eating surface, one adjacent seat and one recreation source every colonist can reach, read
  from the native census before the hosting-room filter. Capacity alone recovers it; observed
  use is the `ranked` phase's (from `StageDevelopment`). While shelter is owed the concern is
  `method_unavailable` and reports `awaiting_plan:initial_shelter`, never extending the
  startup hold. An inaccessible existing facility is a blocker, not a duplicate. Methods are
  `basic-comfort-<definition>` under plans `routine-basic-comfort-*`.
- Maintenance priority (3) covers a second reachable building-backed joy kind: immediately
  for multiple joy-needing colonists, for a lone colonist when native boredom is set for the
  only accessible kind. Two kinds cap construction; duplicates of one JoyKindDef give no
  variety; an inaccessible second kind blocks duplicates. Selection is gated by research and
  builder availability over `DefinitionCatalog.JoyBuildings` in order (a powered one only
  at an indoor site within connector reach of a running generator, of a distinct kind).
  `DefinitionCatalog.RecreationFoothold` is the cheapest joy building needing no power or
  research; `DefinitionCatalog.WatchBuildings` also need native watch-cell access.
- The optional joy census (distinct usable kinds, per-colonist tolerance and boredom
  vectors) is bounded at 16 kinds, 256 pawns, 2048 pawn-kind entries, four candidate
  definitions and 64 KiB. Exceeding any omits the whole census: variety is unknown and
  cannot certify recovery or schedule construction.
- Dining and recreation are maintained once every startup survival concern has a method on
  record or is monitoring-only; the deficit stays visible during emergencies and admission
  waits. Dining needs a native eating surface with adjacent sittable furniture in an
  enclosed roofed room with safe access; seats go only on the surface's adjacent cells. At
  most one table, seat and recreation object per admission. Sleeping upgrades belong to
  `MaintainHousing`.
- Recovery needs access for eligible colonists plus observed dining and recreation use, tied
  to the current load and exact furniture identity; access loss or removal reopens it. No
  schedule rewrite or forced need job. The shared watchdog bounds waiting.

### Acceptance scope

These predicates are separate from `FOOTHOLD_STABLE` and do not certify the startup/upkeep
matrix or sustained survival. Live acceptance is the `upkeep/<scenario>` case set: one
staged deficit per scenario, owning families only, recovery observed on the native
postcondition (`-scenario a,b` picks scenarios; `-reuse` reloads the baseline save into
one running game between them).

## Corpse larder

`MaintainFoodStorage` also reviews fresh animal corpses when ordinary storage has no
deficit. Larder handling runs at priority 2; the journal admits only the corpse and
forbid/allow direction the review selects. It releases forbidden corpses outside
refrigeration, then hauls eligible loose corpses into native-selected frozen storage. With
none, it can admit an animal-corpse-only stockpile on observed free cells in a frozen room;
rotten corpses are excluded.

- A roofed corpse observed at or below zero Celsius is held only for animals yielding more
  than 225 meat, or body size at most 0.75 with more than 75 meat. The forever butcher
  bill stays active. Unknown facts never authorize a hold.
- The nearest rot deadline (identity breaks ties) releases first when available raw meat
  falls below one native ingredient batch per active cooking bill, or within 15,000 ticks
  of rot. Released corpses fund that same window while awaiting butchering, so successive
  reviews cannot empty the reserve.
- General event-loot handling excludes these corpses; the larder uses the native
  safe-hauling census and shared Hands actions. A corpse butchered before the first hold
  is an accepted loss of density.

`snapshot.TestCorpseLarderReleasesOneFrozenCorpse` replays the release decision over a
recorded snapshot.

## Personal wealth shares

`policy.PersonalShares` answers how much a colonist may still direct at their own bedroom,
gear and bionics. Nothing is persisted; it is recomputed from the wealth fact each review.

- Pool: `PersonalPool` is `WealthFacts.Items + Buildings`, pawns excluded. Unknown unless
  the fact is known, finite and non-negative.
- `PersonalShareFraction` f is 0.2 and must stay below 1 (a test enforces it): personal
  spend raises the pool, so the total converges at f/(1-f) times the starting pool.
- Weight per free colonist: base 1, +0.25 soldier, +0.25 doctor, +0.5 Greedy, +0.25
  Jealous; an Ascetic is 0.5. Share = f x pool x weight / total weight. Slaves and
  prisoners get a zero share (necessities only).
- `PersonalShare{Share, Spent, Remaining}`: Remaining is `max(Share - Spent, 0)`. Unknown
  pool or spent leaves Remaining unknown and `Allows` refuses any charged upgrade; a
  zero-value `PersonalShare` is ungated. A delta of zero or less (a necessity) always
  passes.

### Held shares and the accessor

Every routine reading computes per-colonist shares into the held
`observation.ColonyProjection.Facts.PersonalShares` (`observation/colony_personal.go`; the
one holder, so detection's elective surgery gate reads the same map) from data it already
holds; nothing calls native. Gates read one accessor:

```go
func (r ColonyProjection) PersonalShareOf(pawn policy.PawnID) policy.PersonalShare
```

Use `share.Allows(delta)` with the upgrade's market-value delta. A colonist with no held
share (no routine reading, unread roster or wealth, a prisoner) gets
`policy.UnknownPersonalShare`: gated, every part unknown, only necessities pass. A slave
has a known zero share and unknown spent, with the same effect.

Inputs: pool from `Facts.Wealth`; weights from `WorkPawns` trait effects; Soldier is a gear
role of soldier; Doctor is `policy.ShareDoctors` (the `DoctorsWanted` most skilled capable
Medicine colonists). Spent prices beds, worn apparel (loadout model `Worn` options) and the
primary weapon from catalog MarketValue rows (unobserved quality reads as Normal), and is
unknown for everyone while the sleeping census is, and for a colonist whose gear model,
weapon or installed parts (`CarePawn.InstalledParts`) was not read.

The same shares are the read-only `share`, `spent` and `remaining` per roster row of
`/api/player/colony` (`go-player-api.md`), taken from the held projection only while it is
the current native generation.

### Acceptance

`upkeep/personal-share-rich` and `upkeep/personal-share-poor` stage the same bare greedy
bedroom and gear (`test/gear_fixture` `share_setup`) in a rich and a stripped colony: the rich
one reaches the upgrade rungs, the poor one does not, yet its bare colonist is dressed and
its bed stays owned (necessities are never charged).

### Wealth pool and spent attribution

- Worn apparel, equipped weapons and carried inventory are in `WealthItems`, so the pool
  already contains the gear `Spent` charges, intentionally. Installed bionics land in
  `WealthPawns`, outside the pool. `WealthBuildings` is not halved.
- `policy.PersonalSpent` derives each free colonist's `Spent` from the sleeping census and
  carried gear every review. Slaves and prisoners get no entry; any unread part (room
  census, wealth, bed quality or price, gear) makes spent Unknown, never zero.
- `ItemMarketValue(base, quality, condition)` reproduces the MarketValue stat parts
  (quality multiplier with gain caps, then the hit-point curve); `base` is the catalog
  (def, stuff) value at Normal quality (`GearOption.Cost`).
- Bed: priced via `BedPrice` from `SleepingBed.Definition/Stuff/Quality` at full health,
  split among the bed's owners. Room contents proxy: `RoomQuality.Wealth` less bed values,
  floored at 0, split among the room's owners; medical, prisoner and slave beds and
  owner-less rooms charge no free colonist.
- Installed parts: item market price times `PartDiscount(Tier)` (`PartRestoreDiscount`
  0.25 up to `PartBionicTier`, `PartUpgradeDiscount` 0.75 above). A part with no item, no
  price or a failed read (`PartsUnread`) makes spent Unknown.

### Bedroom upgrade gates

`RoomTarget.Min` from `RoomQualityTargets` is the tier ceiling (Greedy, Jealous and a title
raise it, Ascetic caps it); the owners' share is the gate. `bedroomGate` builds
`policy.RoomGate` per review (`Stage`, `Shares` = `PersonalShareOf`) for `NextRoomUpgrade`,
`NextBeautyUpgrade` and, via `BedMaterials.Gate`, `NextBedReplacement`. The zero `RoomGate`
is ungated.

- A step is charged its market-value delta: a template piece or plant pot at the catalog's
  `(def, stuff)` value in the stuff `upgradeBedroom` builds it in (`pieceStuff`); a bed
  rebuild at the new bed (Normal quality) less the owned bed; a floor at material cost per
  cell, laying only the cells the share pays for. An unpriced step is refused while gated.
- Never charged: a delta of zero or less, a room with no owners (common and throne rooms
  stay on the baseline), the royal title's bed and furniture, and the assign and remove
  steps finishing a started bed replacement.
- Charged steps begin at the Reserves stage (`stage < Reserves` refuses them), the previous
  review's stage as `Rounder.stage` carries it.
- A shared room spends its owners' combined remaining share; an unknown remaining refuses
  the room.
- A refused step is not due, so `bedroomsOwed` (`MaintainHousing`) closes once no
  affordable step is left; raising the share or stage reopens it.

### Suite and sculpture gates

`SuiteClaims` and `NextSculpture` take the same `RoomGate` (`bedroomGate`).

- A suite is priced by furnishing only: the new bed (`RoomGate.SuiteBed` in `SuiteBedStuff`)
  less the claimant's owned bed, plus the available template pieces
  (`RoomFurniture.BedroomUpgradePieces`, in `pieceStuff`). Walls, door ring and floor are
  not charged.
- Claims are priced in `orderSuiteClaims` order against the vacant suite they would take: a
  suite whose bed stands has only the move left (free); an unbuilt or empty one, and any
  claim past the vacant suites, is charged full furnishing. An unaffordable or unpriced
  claim is dropped: `SuiteTargets` grows nothing, `UpgradeTargets` leaves the owner's room
  to the in-place ladder and `MaintainHousing` closes.
- `SuiteClaims` covers solo bedrooms only (the claimant is the lone owner). The royal-title
  and bed-replacement assign/remove steps stay ungated.
- The sculpture install (`NextSculpture`) charges the packed item's `MarketValue` against
  the room owners' combined share. `SculptureRoomsOwed`, `NewArtDemand` and the art sale
  are unchanged.
