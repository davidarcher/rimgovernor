# Colony upkeep contracts

[Documentation](../../README.md) · [Controller contracts](controller-contracts.md)

Equipment and apparel are their own contracts: the [loadout model](equipment-upkeep.md),
the [apparel policy operation](apparel-policy.md) and the [weapon planner](weapon-planner.md).

Upkeep uses the existing ColonyPlan, resource admission and Hands executor.
`rimgovernor/observations_read_colony_facts` `upkeep` is versioned read-only native evidence, read whole:
every census list is total and a read native cannot make fails the observation. Only the routes
section stays nullable (#3782). An item's rot, a bed's quality, a person's royal standing and an
animal's roping are named states.
The repair structure census includes only native definitions with `useHitPoints`, so
non-damageable markers such as sleeping spots are never repair targets.

## Native capability audit

| Need | Available native evidence and action boundary |
| --- | --- |
| Supplies | Per-item identity, location, quantity, condition, deterioration stat, roof, valid storage, rot deadline, forbidden status. `HaulIntent` previews native storage access; `requireSafeStorage` rechecks enabled hauling, safe reach and covered storage at dispatch. |
| Storage | Slot cells, roof, occupancy and item-specific filtered capacity. Cell count alone is not usable capacity; native hauling decides worker access and delivery. |
| Sleeping | Bed definition, slots, owners, current users, pawn-specific access, roof, temperature. Capacity does not establish use or suitable worn protection. |
| Home and structures | Occupied/protected cells with home coverage; building condition, material, roof, native construction lineage. The brain's `RoofSupport` rules check roof connectivity with one wall excluded; they do not prove enclosure, escape routes or replacement admission. |
| Fire, cleaning, repair | `GiveJobIntent` Repair uses the installed WorkGiver and its normal eligibility, and needs home coverage and enabled work; the reach check uses the forced-order danger (`NativeOrderDanger`, Deadly, as vanilla's float menu), and a pawn needing tending is not refused. Firefighting is not directly orderable and the brain does not plan it: pawns fight fires natively, and work assignments keep Firefighter pinned at priority 1 for every pawn that can do it. |
| Pawns, animals, medicine | `observations_list_pawns` supplies medical, work, settings and schedule reads. Animal feed counts only reachable stored feed under native eating eligibility and allowed-area access (no pasture or future harvest); pets have no pen-containment predicate. Medicine is identified by native item definitions; no future production or season is credited as stock. |

## Maintained jobs

### Home coverage

The `layout/home-coverage` Need uses the complete native building and stockpile geometry
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

Acceptance: `upkeep/home-coverage` verifies corridor and margin maintenance,
exclusion beyond the margin, and restoration of a removed corridor cell.

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
  share it ([colony extent contract](colony-extent.md#consumer-contract)). The same record
  carries the standing blueprints and frames and the material they are still owed. Every
  routine frame reads it whole (a frame without its building table is a contract error);
  it is absent only from a projection no routine frame built. Missing history does not exclude
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

### Extent eligibility

- Remote work has no reach stage ladder. Safety vetoes are plain holds: a present or
  unknown threat, the outdoor hazard hold (an open dash window lifts extreme cold only)
  and the [remote work holds](controller-contracts.md#remote-work-holds-and-resume).
  Distance is the supply planner's path-distance price, never a gate.
- `policy.ExtentEligibility` overlays current evidence on stored history without
  mutating it. Per-region diagnostics keep origins and facility IDs, list active
  facilities separately and report holds: `threat_present`, `threat_unknown`,
  `facility_lost`, `facilities_unknown`, `route_unknown`, `route_impassable`. Losing any
  supporting facility holds the region; a map-wide threat holds every region; missing
  evidence never grants permission; recovery clears holds, not history. The routines
  API `extentEligibility` reads the current
  timeline's stored history and fresh held census without writing either. It neither
  paints Home nor changes selection or dispatch. The acceptance harness records
  `/api/routines` before shutdown for `acceptance why` and postmortems.

### Stone shell and deconstruction

The `layout/stone-shell` Need admits wall upgrades (one `wall-<digest>` Method per wall, side by side by claims) once the `layout/climate-envelope` Need is met, from currently observed
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
geometry, forbidden or burning state and any pending wall upgrade at apply; an enclosing
wall is admitted. A blueprint is not deconstructed (the deconstruct designator refuses it):
`Designate` CANCEL on the blueprint `target` (no guard) destroys it through `Designator_Cancel`,
and a blueprint already gone applies again (#3742). A frame is deconstructed, so its delivered
materials come back.

- Roof collapse is a hold at the work giver, not an admission refusal: the
  `WorkGiver_Deconstruct.HasJobOnThing` postfix keeps pawns off a standing designation while
  removal would drop a roof (unproven roof support or unknown support geometry). A
  completion-time check in `FinishedRemoving` is the backstop. The removal job resets its
  work on every start, so a hold at the work giver loses no progress; the hold lifts when the
  support is proven. Clearance first issues `remove_roof` over the rooms.
- `replace_with_wall` swaps a 1x1 player door for a Wall of the door's
  stuff when the door stands on a planned room's wall ring with no planned door: if
  `GenConstruct.CanPlaceBlueprintAt` accepts the wall over the door, native places it and the
  construct work giver removes the door; otherwise the door is designated, the nearest capable
  builder deconstructs it, then the wall blueprint is placed and queued first. Both orders are
  player-forced. `DeconstructEffect.replacement_id` names the wall blueprint.
- The intent adopts a standing designation rather than placing a second. Applied means
  designated, not demolished. Ownership is scoped to the loaded game; loaded designations stay
  untouched until adopted; revoking authority releases exactly the brain-owned pending
  designations and player replacements survive.
- `wall_upgrade` completion comes from the native deconstruction job, not a wall disappearing;
  the guard rechecks supporting identities, enclosure and roofs. the brain's material budget is the only stock check; native neither caps open guards nor vetoes on builder availability.
  Jobs need active supervised simulation. Native UI input, a load/map change or changed safety
  invalidates pending demolition; Manual suspends it. Cleanup requires the completed permanent
  wall; missing or uncertain outcomes stay blocked. Native evidence must confirm retirement,
  the surviving exact target and the absent designation before the shared plan cancels
  unissued descendants; issued construction stays observed; cancellation history prevents
  duplicate replacement.

### Defensive layout

`layout/defense` (opt-in, #3314; it replaced `EnsureDefensiveLayout`) commits one stored corridor layout per colony and builds
it tier by tier; a tier is `Built` only while every building is observed in the defense-site
census. Geometry is the `policy` layout planner's: a zigzag corridor kill zone with a single
defenders' doorway, sized from defenders and turrets, no door on the raiders' route.

- Traps sit on the raiders' cheapest line (a spike trap costs a faction pawn 800, raiders
  nothing) while the hallway keeps colonists a cheaper trap-free route
  (`policy.hallwayAvoidsTraps`, `policy.colonistRouteAvoidsTraps`). Shooter cell floors are
  terrain; a floor the native preview refuses is dropped from the tier.
- A `Complete` record is re-verified after each combat epoch (the latest `engage-<map>` plan) and once per game hour:
  a tier that lost a building re-opens under a new repair prefix, so earlier refusals do not bar it, as a new tier method while
  `Complete` stays true. A missing building whose cell carries a blueprint or frame is not
  placed again; the planner waits for the build. Defenders are undrafted once
  the `military/combat` standing stops authorizing the hold plan.
- Combat holds the line only against an ordinary edge assault in front of the cover row
  (`policy.BehindFiringLine`). Sieges, sapper or breach toil, drops, engaged raiders and
  raiders past the cover row get squad defense. A raider at or behind the cover row or lord
  evidence turning to breach/sapper cancels the hold's orders (`hold_fallback`); unknown
  position or lord evidence never abandons a hold.
- The `turrets` tier needs observed gates: turret definition researched in the census, a
  generating network with spare watts for every draw, stock covering turrets and conduits.
  Placement, spacing and conduit routing are the planner's; an unroutable turret is dropped.
  A layout without turrets re-probes once per game hour. A standing unpowered turret is a
  deficit (`unpowered`) owned by the `power/basic` Need's network; damage is
  the `home-upkeep/repairs` Need's. While every tier stands the Need reports a known zero
  deficit, and it is met while the verification is current and no fight waits.
- An observed empty barrel (`unfuelled`) is a tier deficit. With fuel in stock the concern
  waits (`unsupported_forecast_turret_refuel`): a routine refuel needs exact fuel, worker and
  outcome alternatives. With none, the fuel runway (below) already demands the refill.
  Unknown fuel state is neither deficit nor order.

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
projector's `Fuel` domain (`ShadowFuel`, under `economy/resource`). Difficulty's
maintenance cost factor, which scales `fuelMultiplier` for a `factorByDifficulty` comp
(turret barrels), is not observed; the def's own multiplier is used.

### Clearance

The `home-upkeep/clearance` Need (the `home-upkeep` family, #3253; composed by the
`clearance` toggle) is met when the clearance census holds no removable non-player
building inside Home and no planned-ground work is left; its deficit is that target
count. Its Method is the next clearance step, keyed by the step (`cancel-<id>[-xN]`, `deconstruct-<id>[-xN]`,
`pack-…`, `swap-door-…`, `roof-off-…`, `ground-walls-…`, `ground-floors-…`): the same
step continues its open Method, independent steps run side by side, and a step whose
cells or targets overlap an open Method is refused by claim (`claim_conflict`).

- A fresh clearance census must report deconstructible geometry with no roof blocker,
  ancient danger or casket. A standing deconstruct designation is no hold: the admitted
  Deconstruction adopts it, and a step whose targets are all designated proposes nothing.
  Clearance does not wait on repairs, and shared emergency admission still wins. Removals
  run through the recovery queue (tier 1 room obstructions first, then nearest); the
  roof-first batch takes a roof off before its holders. Each held target and reason is a
  held `RecoveryQueue` entry. Roof removal claims `roof/x/z` and floor removal `floor/x/z`.
- A door-to-wall swap is held as `unsupported_forecast_wall_replacement`. A refused step
  spends the shared retry budget under its MethodId before a new attempt is admitted.
- Useful chunk acquisition follows actual material demand through the
  [support portfolio](support-work.md#delivery-and-chunks). A store accepting chunks
  does not prove a reason to haul every chunk. Native definitions retain their
  normal haulability; forbidden items and native legality remain authoritative.
- An unknown census leaves the Need unknown, never met, and proposes nothing. The Need
  is derived fresh each run, so recurrence keeps its id. Native rechecks eligibility at
  apply; the action completes on its applied result and the census decides whether the
  Need is met. Salvage uses ordinary hauling and does not gate the Need.
- Remote clearance considers visible abandoned buildings outside Home only when
  their observed safe route is not held and native salvage yield scores
  against unmet resource demand with storage headroom. Roof-support blockers, caskets
  and sealed ancient-danger rooms stay held; a remote ruin's hold names its
  [reason](controller-contracts.md#remote-work-holds-and-resume) (`threat_present`,
  `urgent_competing_work`, `roof_support_risk`, `route_unsafe`, `missing_storage`). One
  removal is selected per fresh census; dispatch revalidates the census and emergency
  read and resumes without a duplicate designation. Selection never adds Home cells.

### Repairs, cleaning

- The `home-upkeep/repairs` Need (Wellbeing; composed by the `repair` toggle) is met
  when no player building inside Home is damaged; its deficit is the missing hit points.
  An unknown structure census leaves it unknown. Repairs are not yet a supported forecast
  outcome, so an unmet Need is held as `unsupported_forecast_repair` and commits nothing.
- No concern owns fire. A home fire neither raises a need nor pauses development work;
  native firefighting answers it.
- Methods inspect at most eight targets and eight enabled, available workers per
  review; stable IDs break ties. Medicine and rot deadlines rank hauling; native medical
  beds, temperature controls and generators lead repairs, then roof holders, beds and
  worktables; relative damage ranks within a category before cosmetic repairs.
- No concern owns cleaning. Vanilla cleans the home area under each colonist's Cleaning
  priority while no coupled order holds it (#3662); no bill hook or tend finalizer selects
  cleanup.
- Butcher placements never share a room with a cooking bench. The `food/supply` Need owns the
  butcher facility and owes it whatever the food runway: the planned butchery (beside the
  freezer) takes a `ButcherSpot` first, so hunts have a bench at once, then the real
  `TableButcher` once the wood and a builder allow, and the spot is deconstructed when the
  table stands. Neither waits for the room's walls: the plan must hold the butchery and its
  interior is cleared of obstructions like any planned interior; without a planned butchery
  the planner waits (`butchery_room`) and never places a bench elsewhere.
- Methods preserve forbidden items, player work overrides, schedules, drafts,
  player-forced jobs, storage filters and home areas. Deterioration and growing fires do
  not reset the progress watchdog.
- Blocked upkeep reconsiders at once on changed target eligibility, worker availability
  or research. Position, rot-timer and temperature drift alone do not reopen a failed
  method. A 2,500-tick review window catches changed capacity or routes; outstanding
  receipts and watchdog holds stay authoritative.
- An `upkeep_target` action waits after the native job receipt: hauling needs the same
  item identity and at least its original quantity in roofed valid storage; repair needs
  full observed health. Disappearing hauled items (including merges) and partial
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
  storage and survives later consumption; the brain also requires the exact receipt ID,
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

Housing is one Need of the shelter family (#3257, #3659): `shelter/sleeping` (bed
ownership and upgrades, no precondition), whose planner's Methods are admitted by claims
under it. No Need keeps a spare indoor place (#3749): the plan houses the colonists
exactly, and a colonist the beds do not yet house sleeps on a stand-in spot. There is no
starter shelter and no Construction labor hold.

- Day-one floor: while a sleeping and an indoor place do not both stand for everyone,
  `shelter/sleeping` first places the missing sleeping places as stand-in
  `SleepingSpot`s (`ShelterRules.StandInsOwed`, one Survive-tier `stand-in-sleeping-*`
  Method per shortfall, none while one is open) in the first selected sleeping room:
  its free interior, then the cells next to its ring, outdoors allowed. Each spot's
  whole footprint (1x2 at South) must lie on those cells, never on the ring or its
  door, and the picked spots and pending blueprints never share a cell
  (`ShelterRules.StandInAnchors`, `PickStandIns`). A spot houses
  no one in the bedroom census, so its sleeper is still walked into a bedroom, and a
  vacant spot is cleared once the beds in enclosed rooms take everyone.

- `shelter/sleeping` is composed by the `sleeping` toggle. Each review re-derives targets (colonists without an owned suitable bed or without
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
  comfortable band of every unhoused colonist. One bed per method; the
  staged bed is assigned on a later review.
- Bed rung: `Bed`, else a `Bedroll` whose stuff (first `stuff_options` entry the stock
  covers) is on hand. A bedroll is suitable only while `Bed` is unavailable, after which
  its owners are upgrade targets. A sleeping spot is never suitable. While every
  colonist owns some bed the bedroom ladder (shell with door, furnish, move) runs ahead
  of any barracks bed at every tier. Furnishing falls back to a `SleepingSpot` the owner
  moves into; a vacant stand-in spot is deconstructed once the indoor beds take everyone.
- Assignment and construction receipts do not complete the deficit. Recovery requires
  observed use by the assigned pawn in a suitable bed, kept for that bed and load.
  Missing reads, changed assignments, access loss and unsafe temperature reopen it.
  Natural sleep uses the existing schedule; rest and timetables are never forced.

### Medical reserves

The `medical/reserves` Need (#3258) is unmet while usable medicine is below the
recovery reserve per colonist (default three; planning policy, not demand forecasts),
with no entry bar: it is a plain threshold at the target. Current usable reachable medicine of any native definition counts;
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

The `animals/containment` Need (#3260) observes pen membership for eligible starting animals. Pets and
animals marked for release or slaughter get no pen request. Suitable existing pens are
reused through native handling (Handling joins shared allocation without overriding disabled
work); with none, one pen marker claims the yard inside the defensive wall once it is closed, built through
the supply-room enclosure checks. Fences or a marker do not meet the Need: the animal
must be observed in a suitable pen. No breeding, bonding, master or removal setting changes.

Herd layout: a pen per herd holding roamers (husbandry-contracts, Pens); there is no barn, for
animals sleep in the open. There is no vet room: the hospital holds
`AnimalBed` stations sized from the injured animals (`policy.WardBeds`), and an animal enters
it when carried or via the bot-owned `Hospital` allowed area (`HospitalAreaKey`).

- The family's work step reconciles each pen (`animals/containment`, `policy.NextPenStep`) and the
  hospital's animal ward (`animals/ward`, `policy.NextWardStep`, `policy.HospitalCells`) through the
  shared build side (`ReconcileRoomAsync` under the Need owner, a `HerdReconcile` step with the
  template: a pen's `PenMarker`, the ward's `AnimalBed`s), so a lost fence is rebuilt
  like a first ring and the beds are installed from packed stock first; each Need is unmet
  while its step is due. A pen no longer waits for an animal bed to be usable. A definition
  the live catalog lacks fails the family's read.
- Each standing ward bed is flagged medical by a `BedUse` patch (`herd-medical-<bed>`). Each bonded animal's master
  (`policy.CompanionMaster`) with a solo bedroom gets one `AnimalSleepingSpot` per animal
  (`policy.NextCompanionBed`, one per review). Pasture rotation is not built.

The animal feed runway (#2379, `policy.PlanAnimalFeedRunway`) replaces the old
`MaintainAnimalFeed` reserve. It is a forward-projector domain beside the fuel runway: per
race group the consumption is the sum of the food forecast's `NutritionPerDay`, the pens'
worst-quadrum pasture (per pen the lesser of demand and pasture, shared between groups in
proportion to consumption) offsets it, and the unheld edible stock every animal of the
group can eat covers the rest for a runway of days against `ProjectionHorizonDays`. A
shorter runway demands the items the missing nutrition takes (the stock's lowest-defName
item, else the cheapest `FeedItems` a bench makes) as a `economy/resource` stock level,
merged into the Rounder's construction memory like the fuel needs; kibble and hay are made
on any bench and the old bench-reachability and delivery-fallback constraints are gone.
Missing animals, pens, stock or forecast leave the projection unknown, never defaulted.
`HerdFeedShort`, which gates taming, is
true while the projection is short. Explicit player herd targets are untouched.

Herd growth from known events is in the same runway. Native sends per animal `pregnancy`
(`not_pregnant`, `gestating` with `ticks_to_birth`, or `stalled` while the body's growth speed is
zero), `life_stage_index` and `next_life_stage` (`in_ticks`, `last_stage` or `not_aging`) on `AnimalState`; the
race catalog row carries `LifeStages` (each stage's start tick and `hungerRateFactor`) and
`LitterSize`, the `Rand.ByCurveAverage` of `litterSizeCurve` (one child without a curve,
never below one). A pregnancy adds that mean litter eating as newborns from the due tick
(the race's `AdultFeedPerDay` scaled by the stage's hunger factor over the last stage's),
stepping up at each later stage start; a young animal steps up at its next stage tick by
the stage factors' ratio. The steps are `FeedStep`s on the group; the pasture credit
stays the present herd's. An unread stage, litter or stage row
leaves the projection unknown (a stalled pregnancy, the last stage and an animal that does not age add no step); a new conception stays reactive and the litter is an
estimate the census corrects once born.

Animal feed is held by the warehouse or the freezer; no room of the herd holds a stockpile of its own.
The
`reachable_stored_feed`, `reachable_benches`, `reachable_storage` and `storage_candidates`
census fields are removed.

### Food storage and refrigeration

The `food-stores/storage` Need (#3251, formerly `MaintainFoodStorage`) tracks perishable,
not-yet-rotted nutrition split into stored (adequately covered or enclosed/cold) and not.
It is unmet while unstored nutrition exceeds five units and the stored share of
perishable nutrition is under the target fraction (default 9/10), with no hysteresis,
and unknown while the census is unread. Non-perishable and rotted stock never counts.
Pending reserve access, a short reserve, a missing food stockpile and a due larder
change also leave it unmet.

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

The `food-stores/refrigeration` Need (#3280) takes the warm side: roofed perishable
nutrition in a known room, warmer than 5 C (`ChilledExitC`) and under five days from rot.
It is unmet while that nutrition reaches the same five-unit floor and met once it is
under it; it has no latch, and unknown temperature or room facts leave it unknown.

- The method needs an enclosed room, native `Cooler` availability and a wall cell with a
  straight inside-wall-outdoors line. An existing cooler facing the room is patched to
  the freezer target via the building-temperature action, not duplicated; an unpowered
  one defers to the `power/basic` Need; one venting into another enclosed room is blocked.
  A cooler receipt or build never clears the deficit; native cooling must be observed on
  the stock. The `power/basic` Need holds on out-of-fuel or broken producers (ordinary pawn
  work) and sizes a draining network by daily energy budget
  ([power contracts](power-contracts.md)).
- The `shelter/temperature` Need reuses the wall search for a hot sleeping room. Hot rooms are
  served hottest first, cold rooms coldest first. A room with no thermal facility gets
  one powered `Cooler` at the lowest-sorted vented wall cell, cold side in, when `Cooler`
  availability is known true and a connected network's nominal producer capacity exceeds
  demand by the cooler's draw (`policy.TemperatureCooling`, `PowerTopology.SpareW`).
  A known solar flare, unfinished research, unknown power facts or no vented wall keep
  the passive cooler inside the room. A `Cooler` the power census places beside the room
  counts as its facility. The setpoint is the native default (21 C); the family patches
  no setpoints; its draw reaches the `power/basic` Need as pending demand while open, measured
  once built.
- Solar flare: while a `SolarFlare` condition with a native remaining-duration read is
  observed (`policy.PowerOutageHold`) the power family holds `power/basic` without a
  Method (`power_outage`; its open work is kept, never cancelled), and
  the power and refrigeration planners report `solar_flare` (`PowerWaitBlackout`,
  `RefrigerationWaitBlackout`) with no allowance lent. The deficit keeps its measured
  state until the flare ends.
- The refrigeration Need keeps a method under the flare: the cook-ahead planner
  (`policy.CookAheadFood`) raises one `CookMealSimple`-first target bill, in meals, on a
  bench native still reports usable (a fuelled stove; an unpowered electric one is
  excluded) for the warm nutrition beyond `ReservedFoodNutrition`. It may add a
  second bill of a recipe the bench already carries, but one claim per bench and recipe
  per load. Without the flare hold or with reserved targets covering the stock it
  proposes nothing.
- During the flare the defensive layout treats its turret tier as absent rather than
  unserviced (`DefenseRearmTurrets` reports no `unpowered` cell; empty barrels are still
  rearmed).

### Lighting and flooring

`rooms/lighting` is a plain threshold over measured ground glow at each work table and research bench
interaction cell (`UpkeepFacts.lighting`, a total `LightingFacts` record the native read builds
whole or throws, with every glowing fixture's lit flag; the brain joins each fixture's radius and
service state from the catalog and building rows).

- A roofed work cell under 0.3 marks its bench dark. Unroofed cells are ignored except under an
  eclipse (`policy.EclipseHold`), when every cell is measured and such benches clear after
  it ends. A cell whose room grows a plant that dies to light (`WorkLightCell.plantDefs`
  against the catalog's `diesToLight`) is never dark.
- The method first looks for a fixture reaching the cell: one not lit defers
  (`lamp_power_needed`, `lamp_fuel_needed`, `lamp_repair_needed`, `lamp_switched_off`); a lit
  one within placement radius that leaves the cell dark is blocked; one reaching only from
  further away does not stop a lamp of its own. While a network has an active source, the
  layout plan's own lamps for the bench's room (`LayoutPlan.Lamps`, standing or wall, those no
  observed lamp stands on, in plan order) are previewed first, each as its planned definition
  and facing; a room the plan found darkness cheaper for has none. Otherwise it places the first affordable lamp
  (`StandingLamp` only while a network has an active source, else `TorchLamp`) on the nearest
  free, walkable, unzoned cell of the room within two cells of the interaction cell, each
  validated by native preview. A receipt never clears the deficit; the next census must read
  the cell lit.
- The plan is also a deficit input (`policy.PlanLitRooms`): a room the plan
  lights (`LayoutPlan.Lamps`, so lighting its dark cost less than leaving it, in the tiler's
  sustained watts) that stands built (an enclosed census room within its region) with a planned lamp
  no observed lamp stands on, while a network has an active source, keeps the project open with no
  dark bench (`LightingReview.Planned`) and the round builds its planned lamps, after any dark
  bench. A room the plan left dark, an unbuilt room or no active source yields none; unknown
  facts add none. The improvised lamp stays: it serves benches in rooms the plan lit nothing in.

`rooms/flooring` is a plain threshold over the terrain under each room cell and its native stats
(`UpkeepFacts.flooring`).

- A clean workspace (an enclosed kitchen, hospital, laboratory or cooking-bench room, never a butchery) is deficient while any cell's cleanliness is
  negative; a living room (bedroom, barracks, dining, recreation) while any cell is natural
  ground. Other roles carry no requirement. No hysteresis; an ordered floor still counts as
  deficient but is never ordered twice; an unknown census reads the Need unknown. Clean
  workspaces rank before living rooms.
- Method: the known-available floor meeting the tier (non-negative cleanliness for clean,
  beauty for living), preferring stock-payable cost over the most cells of a bounded batch,
  then the tier's weighted score. No available floor defers to research, none affordable to
  materials; a room whose deficient cells are all ordered waits. Each cell is validated by
  native preview and admitted as its own action; completion is the terrain grid reading the
  floor, never a receipt.
- A traffic tier joins the routes census: the busiest natural home cells outside tiered rooms
  are deficient once the window holds at least four times the per-cell minimum and the cell
  at least that minimum; a floor with any path cost never qualifies. Every traffic row is whole
  on the wire, and a routes census native cannot read fails the colony read (#3782, #3903).

### Routes

The `layout/routes` Need (#3265) reasons only from observed reachability and travel, never flood-fill or
straight-line distance. Native (`UpkeepFacts.routes`) reports every facility a colonist
must reach (player beds, work benches at their interaction cell, storage buildings, dining
surfaces, turrets, stockpile zones as `zone-<id>`) with one travel row per mobile colonist.
Each row is a `RouteTravel` state (#3903): `unreachable` (the game's `CanReach` says no),
`measured` (reachable, with the found path's cost and node count), `unpathed` (reachable, but
`FindPathNow` found no path) or `skipped`. Path costing is the one bounded part: the snapshot
stream request carries `route_path_budget_ms` (the brain sets `bridge.RoutePathBudgetMS`, 100;
absent measures every pair), and a reachable pair native did not path once the budget is spent
reads `skipped` (cost: [capped reads](#measured-cost-of-capped-reads-2567)).
It also reports every breach cell of an unreachable facility (one-cell player walls on its
room border a door would pass straight through, nearest the reaching colonist first),
pending breaches (ordered doors a measured path crosses, every one) and observed traffic
(every nonzero cell of each layer, busiest first, sampled every 30 ticks, with sample count
and window start; the window restarts on load).

- Every row is whole on the wire: only a breach or traffic cell's pending door or floor and a
  facility's room are absent relations. Native reads the traffic counter on every map, so the
  sample total is a plain count. The policy refuses unlisted pawns, negative path numbers and
  duplicate breach or traffic cells.
- A facility is deficient when at least one mobile colonist is listed and none reaches it,
  read fresh each run: no latch and no hysteresis (decision 16). An ordered door does not
  hold the Need open; its open Method continues by itself until it settles. A `skipped` or
  `unpathed` pair counts as reaching the facility.
- Ranks as development. Serves storage and stockpiles first, then benches, dining, beds,
  defence, opening the room with a `Door` of `WoodLog` on one breach cell (nearest breaches
  previewed north then east; first legal one-cell footprint admitted). The planner treats
  exactly one wiped building with no blueprint, frame or cancelled work as the deliberate
  replacement and marks it safe (`Preview.Blockers`); any other disturbance is refused.
  Reasons: `route_door_pending`, `route_no_breach`, `route_door_unavailable`. An unread door
  availability raises `RequiredFactException`. A receipt never clears the deficit.

### Comfort

The comfort Needs are plain thresholds chained by precondition, with no phase latch:
`rooms/comfort`, then `rooms/hosting`, then `rooms/party-spot` (see
[mood control](mood-control.md#the-partyspot)); `rooms/work-chairs` also follows `rooms/comfort`.

- `rooms/work-chairs` (#3039): unmet while a bench stands in a planned room that chose a work
  chair for its definition (`PlannedRoom.WorkChairs`, [spatial](spatial-contracts.md)) and the
  bench's interaction cell, inside the room, holds no building, blueprint or frame
  (`WorkChairRules.OwedSeats`, from the standing bench's def shape and rotation). Each seat is
  one Method `work-chair-<chair>-<x>-<z>`, placed only on that cell in `BuildStuff`. A room
  with no chair chosen, or a bench not yet standing, owes nothing; an unread plan or census is
  unknown.

- `rooms/comfort`: one
  eating surface, one adjacent seat and one recreation source every colonist can reach, read
  from the native census before the hosting-room filter. Capacity alone recovers it; observed
  use is `rooms/hosting`'s (from `StageDevelopment`).  An inaccessible existing facility is a blocker, not a duplicate. Methods are
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
  `shelter/sleeping`.
- Recovery needs access for eligible colonists plus observed dining and recreation use, tied
  to the current load and exact furniture identity; access loss or removal reopens it. No
  schedule rewrite or forced need job. The shared watchdog bounds waiting.

### Acceptance scope

These predicates are separate from `FOOTHOLD_STABLE` and do not certify the startup/upkeep
matrix or sustained survival. Live acceptance is the `upkeep/<scenario>` case set: one
staged deficit per scenario, owning families only, recovery observed on the native
postcondition (`-scenario a,b` picks scenarios; `-reuse` reloads the baseline save into
one running game between them).

## Measured cost of capped reads (#2567)

Each native read that bounds its own work, timed with its bound removed so a cap is
deleted or kept on evidence (epic #2533). Rule: delete the cap unless one main-thread hop
exceeds about 5 ms at stress size; such a read gets a brain-supplied budget request field and
an explicit skipped flag instead. Nothing here is a behaviour change; the downstream
children act on the verdicts.

Method: real game on the tribal8 baseline (250x250, 26.6k plants, fully revealed), paused,
each read timed on the main thread with the existing `ObservationWork` spans. Realistic is
14 colonists, 60 player buildings, 40 wild animals; stress is 40 colonists, 250 player
buildings, 250 wild animals (a temporary staging op, not landed). Steady cost is the best
of the repeated hops after the first; the first hop after staging ran 1.5-10x slower
(cold reachability caches). 32 logical cores, no peer RimWorld process, peer sessions
compiling at the time; steady iterations agreed within about 15%.

| Read (cap) | Realistic | Stress | Capped vs uncapped | Verdict |
| --- | --- | --- | --- | --- |
| Route path cost, `FindPathNow` per reachable facility x colonist pair (256) | 256 pairs 100 ms; 840 uncapped 340-365 ms | 256 pairs 96-115 ms; 10,000 uncapped 3.3-4.0 s | about 0.4 ms per pair | Budget and skipped flag (landed #2568: `route_path_budget_ms` + `path_skipped`; the traffic 128 and breach 16 x 2 caps were memory-only and are deleted) |
| Hunt `RouteSafe` per hunter x prey (reach 100) | 202 checks 33 ms; 320 uncapped 114-128 ms | 4,069 checks 0.8-1.0 s; 8,500 uncapped 3.0-4.0 s | 0.2-0.45 ms per check | Deleted (#3515): the read and its budget are gone; the hunt census carries no route verdict |
| Plant cutter reach (50) | 12-14 ms | 21-41 ms capped, 21-31 ms uncapped | the cap bounds nothing measurable | Deleted (#2569); the cost is the all-plants eligibility scan, not the reach |
| Pair reachability, tend page (64 rows) | 14 rows 0.06-0.14 ms | 40 colonist rows 0.43-0.48 ms; 297-row whole table 23-44 ms | quadratic, about 0.27 us per pair | Budget and skipped flag (landed #2570; since #3768 the opt-in tend read `TendDoctorsRequest.reach_budget_ms`, the brain 50 ms, + `TendReach` `reach_skipped`; the 64-row cap and its `LimitExceeded` issue are deleted; tend covers animals and prisoners, so no colonist-only filter); a skipped doctor has no reachability fact and is not proposed |
| Spatial access audit (32 colonists, 16,384 blocked, 128 targets) | 14 colonists 1.1-1.7 s | 40 colonists 2.9-3.3 s, 5.0 s with 16,384 blocked | about 70-80 ms per colonist, independent of blocked cells; 8 colonists 0.5-0.7 s; the 32 cap alone is about 2.5 s | Budget and skipped flag, per colonist (landed #2570: `SpatialAccessRequest.budget_ms`, the brain 2500 ms, + `PawnAccess.skipped`; the 16,384 / 128 / 32 caps are deleted, the 262,144-cell map cap stays with an explicit unavailable) |
| Cold-site scan, `FoodLarderFacts` (`Take(16)`) | 11-13 ms | 7-14 ms with or without `Take(16)` | the take stops early only once 16 sites exist; sparse maps scan every cell (0.12-0.2 us per cell) | Deleted (#2570): nothing in the brain read `FoodLarderFacts.cold_sites`, so the scan and field are gone rather than budgeted |
| Lines of fire (64 x 64 cells) | 10-15 ms | 128 x 128 34 ms; 256 x 256 145-185 ms | 2.5-3.6 us per trace | Budget and skipped flag (landed #2570: `LinesOfFireRequest.budget_ms` and `SnapshotStreamRequest.lines_of_fire_budget_ms`, the brain 15 ms, + `LinesOfFireSnapshot.skipped`; the 64 + 64 cell caps are deleted; pairs are read firing-major and a skipped snapshot holds a prefix, an absent pair being unknown) |
| Salvage census and refresher | not measured | not measured | already explicit time budgets (1.5, 5 and 250 ms) | Keep (landed #2574): the budgets and the four-hour cache age are brain-supplied `ClearanceTargetsRequest` fields (`salvage_*`, defaults `bridge.Salvage*`); `ClearanceTarget.salvage_skipped` marks rows past the budget |

For scale, a whole `SnapshotFrames.Capture` hop costs 100-175 ms on the 8-colonist
baseline, about 300 ms at realistic size and 1.7-2.1 s at stress with the caps in place
(hunt `RouteSafe` alone 0.8-1.0 s, a read deleted in #3515), so every capped route and hunt read already exceeds the
threshold on its own.

## Corpse larder

The `food-stores/storage` Need also reviews fresh animal corpses when ordinary storage has no
deficit. The journal admits only the corpse and
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
