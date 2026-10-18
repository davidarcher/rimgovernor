# Brain and colony contracts

[Documentation](../../README.md)

Current routine-control rules and completion gates. For the reasoning, read
[the control loop](../architecture/control-loop.md). Policy code lives in
`brain/src/RimGovernor.Brain.Policy`; planners in `brain/src/RimGovernor.Brain.BuildingRuntime`.

## Observation

Read a coherent native frame. Independently fetched observations still require world and authority checks.
`rimgovernor/observations_read_colony_facts` reports shared-diet nutrition and fed
consumption, viable crop cells, indoor sleeping, temperatures, cooking, safe nearby
wild-plant access, starter terrain/support affordances and actual definition costs.
Native growers keep ownership of cultivated crop harvest timing. Unknown observations
never certify recovery.

## Priority evaluation

The priority tree evaluates combat, critical medicine, food, shelter, temperature,
cooking, work coverage, power, storage, defense, wood and [equipment upkeep](equipment-upkeep.md).
Food, wood and temperature use separate entry and recovery thresholds. Emergency
findings guide response planning and reporting; ordinary work may queue alongside
them and vanilla pawn priorities schedule execution. Manual pause vetoes new routine
orders (PauseSafeguard). Methods, blockers, provenance and progress evidence live in the
SQLite-backed ColonyPlan.

[Mood relief](mood-control.md) adds per-pawn corrective concerns. Active breaks hold
routine execution; relief uses ordinary native need jobs and completes only from
observed need recovery.

### Development admission

- **Startup supplies:** the first known native forbidden-supply census is the cohort.
  Later reads may remove released cells, never add later player forbids or revive
  released cells. The cohort persists through Manual and restart; a different world or
  rewound tick starts a new one. It is not ownership, a reserve or an Allow authority.
- **Comfort reviews** need a complete census of eligible people, dining surfaces,
  seating and recreation access; each kind needs capacity for every eligible person and
  observed use of a still-accessible facility. Use history survives Manual and restart,
  resets on world change or tick rewind, and replacement furniture inherits nothing. The
  compiler builds a table, adjacent chair or recreation furniture through shared
  building admission; existing inaccessible furniture blocks duplicates; a finite wait
  after construction allows use without asserting need recovery.
- **Food warehouse** is the planned freezer (shelves and perishables catch-all, from plan time);
  the kitchen keeps only its bench ingredient stockpiles. Siting, sizing and deletion rules:
  [storage](../architecture/storage.md).

Routine Concerns queue work through shared admission; vanilla pawn priorities
schedule it. There are no exclusive development slots or generic weighted
development queue. The planner catalog and due queue control review scheduling;
see the [control loop](../architecture/control-loop.md).

`GET /api/routines` exposes the last review, roster, progress, stage and remote
loot holds. The [player API contract](player-api.md) owns its response shape.
**Concern progress** (`policy.ConcernProgress`): each active concern carries the method in play,
the observable it should move, the tick native evidence last moved it, the blocker
inspection tick and the blocker. Progress is native outcome, never dispatch. Blocker
reads: `blocked:no_worker` (designation dispatched, no capable pawn), `native_ineligible`,
`reconcile_write` (unknown receipt; reconciled by action identity before any retry),
`no_method`. `domain.TicksPerDay` (and the hunt, haul and harvest instances
`RoundsPolicy.HuntProgress`/`HaulProgress`/`AcquisitionProgress` of `ProgressContract`)
keys a stalled situation out for a bounded cooldown (`ProgressCooldownMax`, never
permanent) and planners rotate method or target (`Store.RecordProgressCooldown`). The cooldown is a separate mechanism from the shared refusal budget (`policy.RefusalBudget`): it keys out a situation that stalled (an accepted order that moved nothing), not a native refusal, so it needs no refusal class and always expires; the budget bars a subject native refused, by class and world. The
food concern walks acquire, cook, store, grow, naming `prerequisite:food/cooking` while
cooking is known missing. A migrated family's Needs file rows keyed by NeedId in the
same list ([Need progress](../architecture/control-loop.md)). Prerequisites remain explicit dependencies; pawn work priorities schedule the
orders without reserving a builder for a Concern.

Inspection-specific prerequisite checks determine useful work.
There is no stage and no generic development admission queue. Every planner runs; none is held by name. While the
initial shelter is owed (the housing latch at `shelter`) the shelter owns the Construction
labor, and method admission (`AdmitConstructionLabor`, over `ConstructionLaborRules.ConstructionLaborHeld`)
refuses a plan holding an action that draws on it (`DrawsConstructionLabor`: building, roofing,
deconstruction and floor, foundation and wall removal, moving or uninstalling a building, cover
clearance) with `awaiting_plan:construction_labor`. The housing goal is exempt, and so is a plan of other kinds: gathering, growing, zoning, bills
and settings never wait, so the supply the shelter's own demand asks for (`ConstructionDemand`,
the `DependencyRecord`) is not held behind it (#3147). The defense layout's killbox digs (#2818)
also wait on a raid: no `military/combat` fight in the journal files `awaiting_plan:raid`, and
the first raid releases them. An item on a ring cell is never an obstruction (the
builder hauls it aside when the wall goes over it), so a ring cell waits on no haul. The
shelter room is one continuous reconcile: the first review admits the spots rung and the ring
together, and later reviews go on up the ladder (the clearing, then the beds) while the ring's
wave is open (disjoint cells, admitted by claims). A ring is not
placed in a stuff with no accessible stock while the start kit is still forbidden
(`awaiting_plan:start_kit`); the admin/supply-safety Need releases the kit like any safe forbidden
stack (one plan allows every pending stack), with or without storage, so the
wait is short. The `clock_step` row carries
the pacing reason, effective TPS and last clock-stop latency split (shown by the Observe tab's Now view (the `clock_step` rows)).

## Method compilation and work allocation

Allocation reserves the highest-skilled native builder first, then separates growing,
cooking and hunting among other available workers before sharing intermittent medical
roles. Extra hunters exclude the primary grower and cook; additional growers exclude
assigned hunters and player-disabled Growing. Small workforces may share roles;
assignments alone never establish sowing or food replacement. Capability reads, native
checkbox/manual-priority mode and explicit player overrides still apply.

An open assignment plan does not freeze the decision: each work-planner step recomputes
the allocation and cancels any undispatched assignment whose premise moved (settings
token or manual mode changed, pawn left the decision, or policy wants a different
priority). The method identity carries the before-token, so the same settings against a
moved pawn are a fresh method.

### Royalty facts

There is no royalty read; the Royalty-applicable gate is the presence of
`ColonyFactsSnapshot.royalty` (as for other DLC sections). Title ladder and permit
catalog are def-mirror rows; per-colonist holdings and known psycasts are
`PawnState.royalty`; neuroformer stock, bestowing ceremonies and thrones are the colony
section (see observations.md). Absent scalars are unknown, never zero.

Throne-room requirements are not a wire field: the client builds a view from each
`RoyalTitleDef.throneRoomRequirements` in the definition catalog (`DefinitionCatalog.ThroneRequirements`,
`WithThroneRequirements`) into `RoyalRung.Throne` before projection. A missing
`RoyalTitleDef` row or an unset or malformed requirement is an error wrapping
`bridge.ErrThroneRequirements` naming the title and requirement; the review logs it and
leaves royalty unknown. `disablingPrecepts` is not modelled. `policy.NextThroneNeed`
picks the first rung above the holder's with a throne.

- `royalty/psylink` (`policy.PsylinkCandidates`, `RoyaltyFamily.cs`): owed while an available
  colonist with a needs block and no psylink level waits on a held neuroformer. Neuroformer use is
  not yet a supported forecast outcome, so the unmet Need is held as `unsupported_forecast_neuroformer`. The neuroformer is acquired by
  economy/resource (stock floor of one, `policy.NeuroformerNeeds`, while a candidate
  waits, none is held and royalty marks it `craftable` or `tradeable`). With one held,
  `UseItem` targets the colonist, at most twice per colonist per Episode. Level-ups
  (`policy.PsylinkLevelUps`: psycaster below level 6, no royal title) follow
  no-psylink colonists and never raise the stock floor. Psyfocus is kept by the
  meditation schedule, not this Need.
- Mood psycast (`policy.SelectMoodCast`): the mood planner commits an `Ability` on the
  mood incident when measured need relief is spent or absent (`no_measured_correctable_need`,
  `bounded_methods_exhausted`, `unowned_thought_pressure`; never while a facility concern
  owns the pressure). Caster: another available colonist whose royalty read lists
  `WordOfJoy` as a pawn-targeted psycast and whose Psyfocus stays >= 0.25 above the
  cost; most Psyfocus wins; any unread fact holds. Neural heat and cooldown are not
  read: native refuses (guards `entropy`, `cooldown`) and the shared refusal budget
  retries each caster-psycast pair per incident by the refusal's class. Combat, destination, healing and social psycasts
  are not used.
- `ideology/roles` (`policy.RoleAssignments`): see
  [ideology contracts](ideology-contracts.md#role-assignment).
- `ideology/rituals` (`policy.PlanRituals`): see
  [ideology contracts](ideology-contracts.md#ritual-scheduling).
- `rooms/gatherings` (`policy.PlanGathering`): see
  [mood control](mood-control.md#holding-a-party).
- `royalty/permits` (`policy.NextPermit`, `RoyaltyFamily.cs`): each family read ranks every untaken permit of
  every titled colonist (`policy.RankPermits`): acting permits by worker class (aid and
  laborer calls, trade, drop-pod and shuttle access; psycast permits ahead of trade for
  a psycaster), takeable before blocked. Unmet while the best permit is takeable; its Method
  `permit-<holder>-<permit>` commits one `choose_permit` `PawnSettingsIntent`, claiming the holder's settings. Using a permit is the `Ability` action's job.

### Environmental disruption

`observations_read_colony_facts` `environment` reports current-map condition IDs,
definitions, implementation types, labels, permanence and remaining ticks. Permanent
conditions have no remaining-tick estimate (reading it can trigger a native error and
is avoided).

The ColonyPlan retains load-scoped service deficits and stock observations. Active
conditions distinguish `disrupted` from `temporary_survival`; expiry leaves
`recovering` until every tracked service gate passes. Missing observations stay
`unknown`; a load change or tick rewind discards the episode. Food, production,
shelter, sleeping, temperature, cooking, power and storage use the existing gates;
affected services and needed wood acquisition stay unmet until their gate passes.
This is coordination within existing concerns, not an executor.

- Unusable cooking bench: one campfire method when none exists; no new bills on it.
  Replacement cooking searches at most eight nearby free cells with native previews.
  Starter heating is suppressed only by a usable campfire in the selected room.
- Room heat: a cold sleeping room gets one campfire, or one powered Heater instead
  when wood is a finite stock (nothing sows it), the Heater is placeable and a power
  network has spare watts for its nominal draw. A standing campfire does not hold the
  room against the Heater; a standing Heater does. The power family connects and
  budgets the Heater as any consumer (nominal draw, conservative against the 44-164 W
  average of spike #2769).
- The `disaster/services` Need selects bounded refuel, structural repair and
  breakdown-repair `GiveJobIntent` jobs (Repair, FixBrokenDownBuilding, Refuel) through
  native WorkGivers, preserving work permissions, allowed areas, forbidden supplies,
  reservations and player-forced jobs; Hands previews again at dispatch.
  `service_recovered` needs fresh health, breakdown or fuel evidence; missing targets and
  interrupted labor never count. Definitions without hit points (sleeping and butcher
  spots) need no repair; unknown definitions keep damage risk. A destroyed building
  stays a deficit until accepted rebuilding work. Power recovery needs actual service
  from enabled consumers, excluding player-switched-off loads and generators. Solar
  flares defer generation changes while the cooking fallback is available.
- Toxic fallout: the `disaster/sheltering` Need can move a colonist restricted outside every
  roof into an existing wholly roofed, reachable allowed area via an allowed-area
  `WorkSettingsIntent`, re-derived each 600-tick window (no native lease). Areas restrict
  work destinations; they do not make paths safe. The brain never assigns an unroofed area or
  clears a restriction while fallout lasts; native reports no hazard veto. No refuge produces a blocker.
  Outdoor hazards (extreme cold at or below -20 C, extreme heat at or above 45 C, toxic fallout) are a typed
  fact (`OutdoorHazardsOf`); any known hazard, including an unrecognised kind, holds remote loot, salvage and
  mining, recovery, and chop, hunt and forage acquisition with reason `outdoor_hazard` (acquisition reports it as a `waiting_for_native_temperature` wait
  with that subject). An unknown fact never
  holds. Field expansion is not gated.
  Planned dashes (#2765): while extreme cold is the only hazard and no hostile is up, `policy.PlanDash`
  picks one colonist's trips from the loot census, ordered by market value per exposed tick, each an
  out-and-back leg plus a pickup dwell, walked at the pawn's real move speed slowed by the hypothermia
  stages it crosses, and cut where hypothermia would reach `DashSeverityCap` (0.35, the serious floor) by the
  last return (the formula of `OutdoorBudgetFor`; trips counted from home with no rest between). The result
  is `RoundsFacts.Dash` (`DashWindow`: the pawn, trips and `Until`), planned once by `policy.ReviewDash` and
  held in `RoundsLatches.Dash` until `Until` (re-planning from the severity built up out in the cold would
  close the window under a pawn partway out). At `Until` the review closes it for one review, so the pawn is
  recalled before a new window can plan; the recovery step reads the persisted latch. `DashWindow.OpenAt(tick)`
  is the only close rule: open before `Until`, closed from it. `DashMarginIntervals` of growth stay under the cap. While open it lifts the extreme-cold hold (and only that) in
  `recoveryHold` and `remoteThreatHold`, and `PlanSheltering` lets the named pawn out of Safe; at the bound
  it shelters the pawn again. Heat, fallout and unrecognised kinds stay held. The per-colonist inputs
  (comfortable temperature range, move speed, hypothermia and other severities, capacities) are the native
  `PawnSettings.body` block, read with work detail (`WorkPawn.Body`).

### Shared compilation

Methods compile small batches of semantic construction/zone/native actions. Starter
templates rank nearby legal shelter sites, then use bounded native previews.

**Farm sites.** Starter farms and expansion share one site score: each square patch (4x4
down to 2x2) is rewarded by the crop's fertility-adjusted nutrition rate and charged
(as fractions of a normal cell's daily output) for walking from the anchor, hauling to
storage and fragmentation. A patch adjoining a brain-created zone of the same crop
earns a credit instead; player zones and occupied, roofed, protected, unreachable or
undescribed cells never become free land; 1x1 cells only when no larger patch meets the
fertility floor. Each patch keeps its terms (`FarmSitePlan.Explain`). Each batch previews
at most six patches (at most 32 disjoint patches per action) inside the step budget.
Insufficient farmland does not reject an otherwise legal shelter; selected capacity
stays separate from observed growing cells and the production gate.

**Field planning** (`policy.PlanField`) chooses crop and patches jointly: every
available edible crop with complete native facts is planned over its own fertility floor
and ranked by net nutrition per needed cell. A remaining season under 2.5 grow cycles
excludes a crop; an unknown remaining season while sowing is possible counts as short;
a stored-food runway under 2.5 cycles of the fastest crop is urgent; both prefer the
fastest plantable crop. Existing zones are never re-cropped. Terms also include harvest
labor against enabled growers and first-harvest delay, a cooking credit for raw-preferred
crops when no capable cook exists, a blight penalty per edge shared with an existing
zone or batch patch, a firebreak credit for an intervening roofed empty or impassable
cell (a preference, not fire proof), per-cell pollution against native requirements,
and `risk-fallout`/`risk-frost` credits for feasible indoor sites. Zero-glow crops
require observed darkness and a native diet allowance; ideology penalties exclude
nutrifungus. Unknown worker facts add no terms.

**Claim admission** (#3254). The `food/supply` Need's fields, fishing zones, grower crops,
hunts, forage and butcher bench are Methods of the Need, admitted by their
[claims](../architecture/control-loop.md#method-journal-claim-admission): a second field
batch is admitted beside the first on other cells, hunting and foraging run beside open
fields, and a batch on a cell an open Method holds is a claim conflict. The food family's
work step places only the Need's fields; the `fields` clock slot keeps the hay, social and
`economy/resource` fields.

**Site types** (`policy.PlanSiteType`, over the planning window `ColonyProjection.Cells`
and `ColonyProjection.Environment` / `policy.ControlledEnvironment`, observed only and
unknown when withheld; see [clock-recovery](clock-recovery.md)):

| Site type | Meaning |
| --- | --- |
| outdoor | Open-air growing zone. |
| greenhouse-reuse | Roofed soil a running lamp already lights. |
| greenhouse-new | Roofed indoor soil plus one lamp placed to cover the most soil. |
| hydroponics | New basins on lit roofed floor, for crops with the `Hydroponic` sow tag. With no sun lamp on the map it is one soil-free unit: a lamp, a heater when the room or outdoors is below 6 C, and up to the needed basins inside the lamp's growth disc on roofed indoor floor. The count is the largest one network's day and night headroom and the accessible stock (steel, components) pay for; if not even one basin fits it holds with the reason (`not enough <resource> ...`, `no power headroom ...`). Wiring the unit to the network is not part of the plan. |
| dark-room | Unlit roofed indoor soil for a zero-glow crop. |

All use the same net-nutrition-per-needed-cell score, charging construction per 100
steel-equivalent and power per added kW against network day headroom (lamps) or
night/calm-night headroom (basins, heaters), plus a heater per room or outdoors below
6C (`policy.DefaultSiteTypeWeights`). A kind lacking power, heater, infrastructure or
crop compatibility stays in the candidate list with its reason; an unknown environment
leaves only outdoor. Controlled kinds ignore the outdoor season (native does not refuse a zone for
season, fertility or roof; those decide usefulness and stay the brain crop and cell choice). Candidates are enacted in score order, at most
three per step: zones preview growing zones, construction kinds preview the lamp or
basins (lit soil is planted by a later batch once the game reports it). A refused or
unreservable candidate falls through. Open farm-infrastructure work blocks the next
batch. A built basin sows its default crop, so each step ranks observed growers
(`policy.PlanGrowerCrops`) and commits a one-shot `grower_crop` patch
(`BuildingPatchIntent` `plant_def`) once per grower per Episode.

## Admission, budgets and dispatch guards

Both entry paths commit through revision/context guards, geometry/native preflight and
shared resource accounting. Unissued slots reserve native costs; issued blueprints use
native deficits instead. Admission reserves all accepted projects. Dispatch budgets in
stable ready priority order, letting affordable earlier work proceed after stock is
consumed; later and dependency-gated projects yield, while the selected remaining batch,
earlier ready work, player reserves and uncertain writes stay protected. Dispatch
rechecks stock and player resource policies; changed or unknown costs need validation.
Native bill ingredient selection and consumption enforce persisted player resource
floors and stopped inputs; bill filters and suspension settings are untouched. Exact
ingredient alternatives include quantity conversion and are not truncated. Transient
unissued construction commitments apply only while the brain holds the clock in Run.

Dispatch ingests buffered and fresh native colony events before using a captured load
token/tick and again after preparation, so a busy writer cannot defer a known player
hold or danger event past an old order. External holds are recorded as clock holds
independent of observation and review revisions; there is no player-direction counter.

### Coupled orders

Every routine order dispatches under the running clock; order count never stops
the clock. A plan dependency is coupled (`domain.ActionDependency.Coupled`) exactly when
the later order is written against the earlier one's result (an id it produced, a
position it reached). Ordering-only dependencies are not coupled, and no routine planner
emits a coupled dependency today. When a coupled prerequisite completes under a running
clock the step reports the ready orders (`ClockSchedulerResult.Coupled`, `CoupledOrders`);
the run driver holds the clock (`COUPLED_ORDER`) so they are planned against a quiet world, and
resumes it after. Staleness is refused natively via the
CAS evidence admission carries, and the planner prepares again.

## Native execution

New growing zones validate crop identity and pollution compatibility before registration
and configure the crop in the same native operation. Hands records intent before
writes, retains partial progress and verifies outcomes. Routine execution yields after
12 operations. Nothing dispatches while paused.

## Foothold gates and forecast limits

`FOOTHOLD_STABLE` requires every gate: sleeping capacity in a roofed indoor room, at
least three stock days of food by default, at least ten observed growing cells per
colonist across edible farms, indoor food storage, usable cooking with a bill, safe
sleeping temperature, sufficient power if electrical thermal loads exist, no critical
patient, two armed colonists (everyone in a smaller colony), no active threat, and
verified work assignments ([work planner](work-assignment.md)). Accepted blueprints never
satisfy a gate; stability is reversible.

The food forecast apportions shared nutrition by native demand among eaters permitted by
diet, policy and safe access, reserves animal shares and credits held food only to its
holder. Earliest-expiry allocation uses native rot deadlines at current temperature; the
lowest per-colonist runway drives the gate. Invalid supply observations stay unknown;
harvest lead is computed in policy from native growth, temperature and calendar facts; projected yield never counts as stock.

The maintained food concern budgets each crop's capacity from native daily demand and
yield, covering consumption during its growth allowance plus the persisted reserve
(including colony animals permitted to eat the crop; future grazing is not credited).
Wood has no floor of its own: it is demanded only through construction, bills and the other
derived sources, and the [resource supply plan](../architecture/space-and-resources.md#resource-demand-and-acquisition-scoring)
serves it by whichever chop, harvest or bill it opens, never by a wood-only path.
Reserve and food-latch thresholds are seasonal: the colony read carries
the growing calendar (`policy.Calendar`) and each review widens the policy by its
harvest gap (`RoundsPolicy.Seasonal`): the wait until growth resumes plus one rice cycle
while nothing grows, or the coming non-growing stretch plus that cycle (phased in over the
gap plus one field cycle) while crops grow. A year-round tile has no gap; an unknown
calendar keeps flat thresholds. An observed `Eclipse`, `VolcanicWinter` or `ColdSnap` with a native
remaining-duration read (`policy.CropPauseDays`) extends the gap; one without the read
contributes nothing. Food minimum and target grow by the gap (capped at one year); wood
minimum, target and maximum by the target's factor. The foothold food gate keeps its flat
minimum. Crops rank by native yield, soil response and remaining seasonal window; a
stock buffer shorter than the fastest crop's growth allowance prioritizes it. Unknown
capacity cannot complete the concern. When rot limits runway, long-lived native recipes may
receive a target-count bill (a bill receipt never certifies preserved food). See
[forecast contracts](forecast-contracts.md) for animal feed, labor, medical, mood and
power projections.

## Progress, capacity and bootstrap dialogs

Concerns record methods, attempts, step IDs and observable progress. Native events or a
pause during method selection retain a pending review; neither a refusal nor a no-op
acknowledges newer evidence from an old read. Invalid templates get a bounded
alternative-site search; unknown or failed native actions become explicit blockers; a
no-progress watchdog prevents silent waiting. Structured `GiveJobIntent` refusals from
unapplied dry-run previews block the concern while later reviews continue; changed worker
jobs, cargo, health, work settings, equipment, stock or upkeep evidence permit a fresh
selection, and a 2,500-tick window rechecks routes. Nothing retries an uncertain write
or relaxes native interruption guards. Watchdog holds retain tick, reason and completed
action identities; a newly observed completion of tracked work releases that exact
hold. Unchanged state, rewinds, cancelled or failed actions, different blockers and
Manual retain it; emergencies still suspend lower-priority work.

Write receipts:

| Situation | Receipt and outcome |
| --- | --- |
| Transport proves the write never issued (`domain.ErrWriteUnsent`) | `ReceiptUnsent`: no-effect proof the same authority may retry; no ledger reconciliation. |
| Issued but never answered (brain-side timeout) | `ReceiptUnknown`; native keeps no attempt ledger, so the outcome stays unknown until a later observation settles it. |
| Admitted entry still in flight | Holds until its receipt is recorded. |

Other rules: need-recovery previews retain native refusals on the mood incident and may
consider another measured need; bridge errors retain the requested tool identity.
Above eight colonists, starter sleeping uses verified room and footprint fitting
(entrance aisle and three service rows preserved), uses its available capacity before
proposing another shell on free ground, and leaves existing rooms, zones and entrances
intact. The initial faction/settlement naming prompt is a maintained bootstrap concern: its
native action validates the exact generated suggestions, uses the native naming
callbacks and verifies names and dialog closure. Other forced dialogs retain the normal
hold.

## Hunting admission and dispatch

Autonomous hunting screens current wild-animal observations before designating.
Harmless, undesignated prey must be within 100 cells of the colony anchor and more than
25 cells (square-grid) from live wild predators; unknown predator flags or positions
prevent selection. The hunting budget (three outstanding per hunter, at most 48) is zero while the roster is known
and no [hunter](work-assignment.md#situational-roles) (`HunterFor`: Shooting, ranged
primary, never a Brawler) is on it, for stock and pest hunts alike. A hunt follows its
animal: the planned cell is only a hint, the census row matches the animal wherever it
is, the snapshot token binds the animal, corpse and designation but not position or
health, and native waives the expected-cell rule. Before writing, the runtime rechecks
wildlife, outstanding hunt count and paused tick under the writer lock, then verifies
the designation. Changed observations or missing target metadata block without a write;
unconfirmed writes stay uncertain.

Native states raw hunt facts and decides nothing: a hunt row is every spawned, living wild
animal that bears a corpse (food prey or a pest race), with fogged and mental-state flags,
and the colony facts carry a `HuntCensus`: per free colonist the position, downed, drafted
and mental-state flags, Hunting work state (priority, active, disabled), Cooking active,
the primary weapon (ranged, verb range, projectile kind bullet / arrow / other, blast
radius, damage def and worker, warmup), the game's own `HasHuntingWeapon` and
`HasShieldAndRangedWeapon` answers, the butcher benches reachable, and one `HuntRoute` per
unfogged hunt row for each colonist with Hunting active who is neither downed nor in a mental
state: `safe` true or false (a Danger.None path avoiding predators by the request's `hunt_predator_margin_cells` (the brain sends `policy.HuntPredatorMarginCells`, 25) and an ordinary
death action, `RouteSafe`), or `skipped` once the frame's `hunt_route_budget_ms` (the brain sets
`bridge.HuntRouteBudgetMS`, 50; absent evaluates every pair) is spent. Native applies no reach:
pairs are evaluated pests first then nearest first, so the pairs a spent budget skips are the
far ones policy refuses as `too_far` before it reads a route (cost:
[capped reads](upkeep-contracts.md#measured-cost-of-capped-reads-2567)). A pair with no row was
not evaluated; per butcher bench its usability and butcher-flesh
bills (suspended, paused, repeat mode, counts, corpses the filter allows). `policy.HuntGate`
decides from them, in this order: fogged; safe prey (not in a mental state, edible; a pest
waives it); a usable bench with a running bill accepting the corpse and a Cooking worker (a
pest waives it); a colonist; a colonist who can hunt it: not downed, not in a mental state,
Hunting active, vanilla's hunting weapon (a ranged primary whose attack verbs are all damaging
projectiles, bullets and arrows alike, with no blast radius and no flame damage, and no
ranged-blocking shield) or a melee weapon or bare hands against meleeable prey
(body size <= 1.0, which flees), within 100 cells (a pest anywhere) and a safe route. A
held row is not a source; the projection's `HuntHolds` names the first failing gate
(`fogged`, `not_safe_prey`, `no_colonist`, `no_hunter` with each
colonist's `downed`, `mental_state`, `hunting_inactive`, `no_hunting_weapon`, `ranged_blocking_shield`, `too_far`,
`no_safe_route` (evaluated, unsafe), `route_skipped` (budget spent) or `route_unevaluated` (no verdict)).
The 100-cell reach and the plant cutter's former 50-cell reach are no longer native filters:
the 100 cells live only in the gate above, and a plant needs only a colonist with Plant Cutting
enabled who can reach it. Selection designates as many prey as the nutrition gap (deficit less pending nutrition) needs, meatiest safe prey first, bounded by the budget; an open hunt does not block the next hunt method. Supervised play pauses when an
active hunt loses its route.

Acquisition reports revenge chance, same-race herd size within the request's `herd_radius`
(the brain sets `bridge.HerdRadius`, 25; the prey included; absent radius leaves `herd_size` unset,
unknown, never 0, and the bridge refuses a hunt row without it), melee eligibility and downed state; the brain derives the longest weapon range among the
eligible hunters from the census, and the animal's leather and butcher products from its
race row. Food selection keeps forage priority, then downed animals, then lower revenge
chance times herd size.

One Hunt candidate stands per lone animal, plus at most one formation candidate (`policy.HuntCandidates`), with a
mode: `lone` (a designation; id the animal's) or `formation` (id `roster:<gunners>><prey>`, run as a `HuntRequest`
of the selected gunners). The formation is the roster the hunt comparison chose (`policy.PlanHunt`, see
[forecast-contracts](forecast-contracts.md)): its gunners and prey are named on the channel, with no minimum
roster, animal count or clustering radius. A gunner is a colonist whose primary weapon hunts (`WeaponDef.Hunts`:
ranged, not explosive, not incendiary). A formation candidate is `Designated` once the plan opens its prey
(`HuntRequest`, which raises the hunt origin; `policy.HuntAdmission`, in memory), held without re-comparison until a
release trigger (a gunner downed, breaking or gone, an animal gone, downed, out of the window or joined, or a gunner the
estimate no longer describes; [forecast-contracts](forecast-contracts.md)) and, like every hunt channel,
`Delivering` from the first credited ledger KILL ([hunting](../architecture/supply-model.md#hunting));
Delivering keeps the hunt open while its fight runs, and stall and timeout handling stay
on `HuntProgress`. Every hunt channel is a finite source (`StockCap`, the nutrition of the
animals in reach) whose rate is capped by the hunters' kill throughput.
A formation candidate yields the roster's estimated meat nutrition (net of the chance the prey is not downed) and
the products of the prey it downed; its labor is the participants' walk plus the estimated encounter, and its risk
the share of predicted samples with serious harm to a gunner. A roster without a current prediction is not offered.
A lone candidate yields the meat nutrition and the animal's leather and butcher products; labor is charged once.
Hunt candidates cap exposure at one for Revenge risk and a lone hunt estimates pursuit as
7500 / (1 + range / 25) pawn ticks per animal (2500 for downed prey, halved for sleeping
prey): planning estimates, not proof of a kill.
Incendiary weapons are excluded; already-dead fresh corpses stay pending butcher
material.

## Pest clearance

A recognised pest is a wild animal hunted for what it destroys
(`NativeHuntAcquisition.PestDefinitions` and `policy.PestDefinition` name the same
set, today `Alphabeaver`). Pests arrive factionless, so no emergency census answers them.

- The `military/pests` Need is unmet, deficit the pest count, while the known wild-animal
  census counts a pest anywhere or a hunt of its own has lost its animal; met at none; an
  unknown census leaves it unknown.
- The census offers each eligible pest as a hunt row after food prey (nearest first):
  one unit of the pest's corpse, `food` false, no nutrition. Native waives the 100-cell
  distance, safe-prey and butcher-bill rules; the hunter still needs Hunting active, a
  ranged weapon and a safe route, the hunting budget applies, and a pest in a
  mental state is left to the defense family.
- Methods `pest-hunt-*`, plans `routine-pest-hunt-*`, salted with the concern's admission
  count; one hunt per pest up to budget and census count. Planning is animal by animal:
  a dispatched hunt holds its own animal and counts against the bound but never holds
  the next animal's method.
- A pending or prepared hunt whose animal left the census is cancelled; a dispatched one
  finishes natively when the animal is dead (completed) or off the map (unsuccessful),
  not when a corpse is observed. The hunt-stall rule never cancels a dispatched pest hunt.
  Withdrawal under a later same-world authority generation keeps the plan loadable.
  Pest hunts never count toward edible stock.

## Wild-plant acquisition

Wild-plant acquisition limits new orders by the remaining per-colonist nutrition target
and already designated native harvest yield. Pending yield limits duplicates but never
counts as stored food or clears food risk; plants are indivisible, so a batch may exceed
its target by one plant. Food and wood methods issue `DesignateIntent` (`HARVEST_PLANT`)
on the exact observed plant, re-checked natively. A fresh regrowth observation can renew a
confirmed completed designation under the same concern; pending, uncertain and cancelled
orders block renewal at their location. A harvest completes once its output landed
spawned, unforbidden and unfogged; a stack later eaten or hauled stays proven. A
hunt-only food plan is admitted while plant harvests stay open; an open hunt blocks the
next hunt and a second forage waits for the first.

## Raid-point awareness

The native threat section supplies wealth split, storyteller wealth, current raid
points, adaptation and difficulty scale; absent or invalid readings are unknown. They are
storyteller inputs, not a raid prediction.

- Turret budget from the observed raid shortfall (raid points minus defense capacity): the turrets
  standing plus the shortfall over the points a standing powered turret adds; one is built when none stands
  to measure. Power, stock, spacing and lines of fire still gate placement; armed
  colonists size the firing line, never wealth.
- Over the configured item-wealth share, routine trade offers excess Steel, Plasteel,
  Gold, Uranium and Jade, retaining the greatest of target, economic floor and
  configured minimum (see [Trades](action-contracts.md#trades)). Silver and components
  are excluded; unknown wealth or a zero share adds no surplus; existing target surplus
  takes precedence.
- Art is renewable income: artists sculpt for sale whenever the silver runway is short,
  with no wealth-headroom gate. Unreserved art still sheds (`shed_art`) while
  `WealthBudget` headroom is negative.
- [Joiner admission](population-contracts.md) uses its own optional raid threshold.

## Combat response

`policy.DecideCombat` selects formation and reaction orders from the current
combat view and retained fight memory. The brain owns tactics; native owns job legality,
pathing and weapon execution. See [combat responsibilities](../architecture/combat-game-ai.md)
and [hazard bounds](../architecture/hazard-detection-bounds.md).

The fight retains its roster and acknowledged threats. New threats, changed
authority and severe injury still require current evidence. Stationary ranged
holders may use native `Wait_Combat` targeting; specialized roles keep explicit
orders. Combat extensions below cover drugs, hunting, permits and psycasts.

Combat batches currently dispatch directly through the bridge; routing the exact
batch through journaled Hands is tracked in
[#2502](https://github.com/davidarcher/rimgovernor/issues/2502).

## Autonomous supply safety

The `admin/supply-safety` Need (#3304) owns both directions of the forbid flag for visible, player-owned or
unowned haulable items, including later event drops. A complete `event_loot` census
reports each item's identity, cell, forbid flag, native hauling safety and storage
headroom; a zero-headroom unforbidden row also names the storage groups that accept it
([stranded stock](../architecture/storage.md#stranded-stock)). There is no
first-seen or player-forbid exemption; unknown or over-limit censuses authorize nothing.
The scenario's starting stacks are in that census and are released like any other safe
forbidden stack. The reach and demand stage is skipped for a forbidden stack on the
world's first review and while it stays in the previous review's pending cohort, so the
starting stacks (forbidden before the colony has an extent) are released over the following
reviews, eight actions per plan; later forbids pass the stage. Danger and spawner holds still apply.

Safety checks the item cell, each reachable eligible colonist's approach path and the
return path to the native storage choice. The verdict is the best route: one exposed
colonist cannot veto an item another reaches safely, and an item nobody reaches is
unknown, not unsafe. Routes price the colony's own trap cells out. Fire within two
cells, traps on the path, native region danger and visible hostiles with line of sight
prevent Allow (hostile exposure: weapon range plus five cells, twelve minimum for melee).
This is a current observation, not a prediction. The admin family re-reads every review and
cancels a pending action the fresh census no longer supports. Each forbid or allow batch is
one Need-owned Method named by its stacks, so batches over different stacks are admitted
side by side and a stack two batches share is a claim conflict. The native boundary applies
Allow and Forbid with no safety rule of its own, because the flag has other owners (the
reserve food and frozen corpses the `food-stores/storage` Need holds).

Release is selective. A pawn death drops forbidden items and the census releases them
one stack at a time. The review keeps a safe forbidden stack forbidden, recorded as a
hold, when its def is one a native spawner forbids on purpose
(`CompProperties_Spawner.spawnForbidden`, e.g. insect jelly) or it lies within
`ThreatReachCells` of a danger seed (live discovered hostile pawn or any hostile building,
passive hives included; `policy.DangerSeeds`). The same seeds feed the hauler gate:
while a hostile is live and for an hour after, the `shelter/safe-area` Need restricts haulers to the
`NoDanger` allowed area (home minus killbox and cells near accumulated seeds).

Unsafe items are forbidden before safe ones are allowed, each in one plan carrying every pending target. Forbid
changes no pawn orders and may run during an emergency; Allow keeps the emergency gate.
A changed census cancels stale undispatched proposals; later reviews may reverse a
decision. A designation receipt proves the flag only; native storage observations prove
hauling.

### Remote loot and resource reach

Allow is additionally a safety and demand filter; Forbid is not. A safe forbidden stack on
a cell of the derived colony extent is allowed as above. Outside the extent it is
allowed unless a safety hold applies; otherwise it stays
forbidden and is recorded under `EventLoot.Held` with an
[explicit hold](#remote-work-holds-and-resume) (`threat_present`, `threat_unknown`, the
outdoor hazard hold). Allow dispatches no pawn and only clears a flag, so storage headroom
and urgent work do not hold it: with no storage a released item is simply not hauled yet,
and construction and bills can use it where it lies. The routines API reports the holds as
`lootHolds`. There is no distance
ceiling or readiness threshold: the supply planner prices each stack by its safe route
length (`haul = dist * (1 + 2*trips)`), so a solo colony's scattered steel 40 cells out is
a candidate like any other. Unknown threat or demand holds remote stacks. Remote work
changes no Home cell.

### Remote work holds and resume

Remote loot, ruin salvage and surface mining share one hold vocabulary
(`policy.RemoteHoldReason`); loot Allow uses only the safety reasons, and
`urgent_competing_work` and `missing_storage` hold dispatched work, not an Allow. Reported on `EventLoot.Held`, held `RecoveryQueue` entries and the
resource planner's log:

| Reason | Meaning |
| --- | --- |
| `threat_present` | Known hostile on the map (reported ahead of the route verdict it causes). |
| `urgent_competing_work` | Urgent patient or disrupting disaster (`policy.UrgentWorkCompeting`); holds `PlanSupply` candidates unless a demand they serve is an emergency. |
| `roof_support_risk` | The deposit is not open surface, or a recovery removal the mirror roof check still refuses after the thin roofs are down (thick roof, unknown cell). A ruin whose removal merely drops a thin roof is not held: `PlanRecoveryBatch` removes the roofs first, then the ruin; native's per-building roof verdict no longer holds it in the queue and native re-checks at admission. |
| `route_unsafe` | Native walked no safe route to the target and back to storage. |
| `missing_storage` | No accepting headroom. |
| `salvage_skipped` | Native ran out of salvage time budget before computing the ruin (`ClearanceTarget.salvage_skipped`); the refresher fills it for a later read. Distinct from `salvage_unknown`, a ruin carrying no evidence for any other reason. |

Reach-stage and demand reasons keep their own text. Unknown urgency holds nothing at
selection; the dispatch emergency check holds on the same unknown facts.

Selection never dispatches. A pending remote `Deconstruction` is revalidated at every
dispatch against the fresh clearance census and emergency read; a target that turned
unsafe holds with reasons on record (`unsafe_threat`, `unsafe_route`,
`roof_support_risk`, `missing_storage`, `critical_medical`). A mine acquisition holds on
its native preview and the same emergency read. A late threat therefore never becomes a
stale designation, and foreign designations are never touched. Holds clear on the next
admitted dispatch, which designates exactly once: the plan stays open while held, the
clearance planner admits no second method, and a restarted service observes the
in-flight attempt through the native ledger. A designation released with lapsed authority
ends its plan unsuccessfully and the next review admits one replacement.

## Disease care

Disease care projects game days to lethal severity and full immunity from the
native per-day rates. When immunity loses or leads by less than one day, the
work planner enables Patient and bed rest at highest priority and disables
other work for that pawn. Checkbox mode enables only those rest work types.

- The durable medical history holds rest until every triggering disease is
  observed immune or absent from a complete census; missing reads and improved
  forecasts do not release it. World replacement and tick rewind reset the hold.
- On recovery, normal roster allocation resumes, including saved player work
  preferences. The temporary hold never rewrites those preferences.
- RimWorld selects a reachable medical bed for medical rest, falling back to the
  pawn's ordinary bed under native rules. Medical beds cannot be assigned by the
  bed-ownership operation. The hospital planner supplies them (the Medical
  department declares their medicine store); the sleeping planner assigns ordinary beds.


## Combat extensions

**Combat drugs.** A threatened defender receives a dose order only for a combat
drug in its observed carried inventory, with catalog preference breaking ties.
The combat pawn row carries a complete drug definition set; absent inventory
facts hold dosing and a present empty set orders no dose. Native checks only that the def is a
drug and is carried (`not_a_drug`, `no_drug`); age, an active high, addiction and
tolerance are policy (carry planning excludes children and risky chemicals)
and vanilla's own refusal of the Ingest job surfaces as `native_refused`. The fight's `Dosed` memory
limits each pawn to one attempted dose, including refused attempts; it does not
assert ingestion. Unavailable inventory produces neither an order nor an attempt
mark. Policy and runtime tests cover selection; `combatlab/drugs` covers the
native inventory projection and `combatlab/combat_drug` covers ingestion.

**Roster hunt.** A roster hunt is the `military/combat` Need's hunt origin: while no
hostile stands and the food plan opens the formation `Hunt` candidate the comparison chose, the review asserts the
deficit with the channel's prey and selected gunners on the review's combat standing (`RoundsCombat.Prey`,
`RoundsCombat.Hunters`). The fight runs through
`admitFight` with `CombatView.Hunt` and `CombatView.HuntHunters` set and the prey as its threats; `huntFormation`
sends only the selected hunters that are eligible defenders with a hunting weapon, however many that is. The frame's
detail rows cover the open hunt census rows. A hostile in the frame, or the plan no
longer opening the hunt (its formation released, [forecast-contracts](forecast-contracts.md)), ends the origin: the
standing then names no prey or hunters and the Need is met until the next comparison opens a formation; the tactic
never drafts a replacement or extends the hunt on its own. An open hunt fight runs a combat watch
with the live prey as the watch's acknowledged ids (`ClockWatchFacts.HuntPrey`);
no hostile exists to make the watch a combat one otherwise.

**Royal permits** (`CombatPermit.cs`). An outmatched fight calls the permits its
colonists hold. With the royalty read known (`CombatView.Royalty`, the review's
`RoundsFacts.Royalty`), a held acting aid or strike permit that is off cooldown and
affordable (favor at least its cost) yields a `permit_call` order, once per holder
and permit per fight (`CombatMemory.Permitted`). Aid lands on the defender nearest
the squad's centre; a strike lands on the densest hostile clump no colonist stands
within the mortar safe radius of. Unread favor, cooldown or royalty facts hold the
call. The order is no `combat.orders` entry: the defense planner commits it as an
`ability` action (permit source, cell target) on its own incident method, and the
fight's stops wait while it is open. The admission stop makes no call.

**Psycasts** (`CombatCast.cs`). With the royalty read known and a live hostile,
each standing colonist yields at most one `psycast_cast` order per stop: the ready
psycast of the highest family (heal, then stun, burst, defensive).

- Ready means its psyfocus cost fits the caster's psyfocus, its neural heat fits
  under the ceiling and its cooldown has run out, all from the royalty read
  (`PsycasterState`, `Psycast.CooldownRemaining`); an unread fact holds the cast.
- The read is slow, so each cast is remembered for the fight (`CombatMemory.Casts`):
  its cooldown, psyfocus cost and heat count against the caster until the fight ends.
- The read carries no effect category, so `psycastFamilies` classifies by vanilla def
  name and never casts an unlisted def.
- Targets (within reach of the caster): heal a defender under 60% health; stun and
  pawn-targeted burst the nearest hostile; area burst the densest hostile clump no
  colonist stands near (the strike rule); defensive the caster while a hostile is
  in reach.
- The order is no `combat.orders` entry: it commits as an `ability` action (psycast
  source) on the permit calls' incident method, native owning the guards.

## Fixed values that stay

`raidGiveUpTicks` (38000, the vanilla window raiders leave within) and the other surviving
constants are listed once, with reasons, in [kept constants](kept-constants.md).
