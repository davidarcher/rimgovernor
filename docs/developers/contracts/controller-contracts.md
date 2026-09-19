# Controller and colony contracts

[Documentation](../../README.md)

These are the current routine-control rules and completion gates. For the reasoning
behind them, read [the control loop](../architecture/control-loop.md).

## Observation

Pause and read native state. Sequential observations are not an atomic snapshot.
`home/colony_facts` reports accessible shared-diet nutrition and fed consumption, viable
crop cells, indoor sleeping, temperatures, cooking, safe nearby wild-plant access,
starter terrain/support affordances and actual definition costs. Native growers retain
ownership of cultivated crop harvest timing. Unknown observations never certify
recovery.

## Chat entry

Only a new human chat message invokes the interpreter. Mode changes and routine native
events do not invoke inference. Model failure is reported to the player while
deterministic operation can continue.

## Priority evaluation

The priority tree evaluates combat, critical medicine, food, shelter, temperature,
cooking, work coverage, power, storage, defense, wood and [equipment upkeep](equipment-upkeep.md). Food, wood and temperature use
separate entry/recovery thresholds. Emergencies suspend lower priority routine goals.
Methods, blockers, provenance and progress evidence live in the existing SQLite-backed
ColonyPlan.

[Mood relief](mood-control.md) adds per-pawn corrective goals from native thresholds,
thought pressure and needs. Active breaks hold routine execution; eligible relief
uses ordinary native need jobs and completes only from observed need recovery.

### Development admission

Startup supplies use the first known native forbidden-supply census. Later
observations can remove released cells but cannot add later player forbids or
revive released cells. The Go reviewer persists that cohort through Manual
and restart; unavailable reads leave it unresolved. A different
world or rewound tick starts a new cohort. This need history does not establish
item ownership, reserve those cells, or authorize an Allow order.

Go comfort reviews require a complete native census of eligible people, dining
surfaces, seating and recreation access. Each kind needs capacity for every
eligible person and observed use of a still-accessible facility. Use history
survives Manual and restart; world changes and tick rewinds
reset it. Replacement furniture cannot inherit a previous facility's use.
The opt-in compiler builds a table, an adjacent chair or recreation furniture
through shared building admission after startup and development selection permit
it. Existing inaccessible furniture blocks duplicate construction. A finite wait
after observed construction allows ordinary use without asserting need recovery.

Required food storage runs at startup survival priority, alongside cooking and
shelter. It waits for verified indoor sleeping capacity before fitting the starter
room, but does not wait for optional development slots held by interrupted gear.
Placement uses current native room/building geometry and zone previews, preserving
the entrance aisle and existing zones. Nine valid cells may form several patches
when service furniture prevents a complete rectangle. Native readback still
establishes the storage gate. A stockpile takes roofed, walkable, storage-empty
floor: no plant, building, blueprint, frame or item, the same rule the cell census
reports as storage-empty; filth or a standing pawn never refuses a cell. The
storage planners (food storage, workshop ingredient storage) preview a bounded,
ordered list of candidate patches one at a time: a zone preview reports refused
ground as an evaluation that is not accepted, so a refused patch gives way to the
next, while a stale map snapshot or an unresolvable configuration is a failure.

Optional goals (priority class 3 and 4: basic equipment defense, wood, comfort,
expansion, maintained research and resource targets) share a deterministic admission
order. Scores combine a 0–100 observed deficit fraction, a 100-point player-target
preference, one point per 1,000 waiting game ticks, a 20-point selection hysteresis
bonus, a bottleneck penalty of up to 30 points and a risk penalty of up to 40 points
(`policy.DefaultDevelopmentWeights`). Stable goal IDs break ties. These weights are
policy ordering, not measured benefit or time estimates. Emergencies retain precedence,
and comfort waits for startup-survival goals until each is served (a method on
record) or monitoring-only; a food latch that stays open while planted fields
grow no longer holds a table back. The comfort shell rung waits like the
workshop's while the initial shelter is still owed.

The age weight is the starvation bound: an eligible optional goal overtakes any
persistently larger deficit within 100,000 waiting ticks (under two game days), and the
gap is usually smaller because committed work resets the incumbent's waiting age. The
bound is verified by replayed ranking simulations (`development_simulation_test.go`)
covering competing constant deficits, capacity loss and recovery, player interruption,
uncertain cancelled writes and load/map/tick resets. Those replays establish bounded
admission and retained waiting identities, not pawn progress or completion times.

The bottleneck penalty is lead-time evidence: it scales with how contested a goal's
labor profile is (free pawns of its work types after commitments, against the eligible
goals sharing those types), so an uncontested goal can be admitted ahead of one that
would wait on the same scarce builder. Risk is observed exposure of outdoor work
(construction, mining, plant cutting): an active cold or hot latch halves that work's
priority weight, and an observed outdoor hazard condition such as toxic fallout defers
it with `risk_deferred`. Neither term is a safety guard; native danger checks and Hands
dispatch guards still apply.

Every ranked deficit is measured from the review's native facts. A configured research
target is a full deficit while the research tab is idle and the target unfinished; any
current project (including one the player chose) or a finished target counts as
recovered. A resource target's deficit is the worst-covered target's shortfall against
the reachable, unforbidden item census. Unknown stock or research state cannot admit a
new project and never counts as recovery. The production-policy push is configuration,
not development work: it is admitted without a ranking row and holds no slot.

`max_development_projects` defaults to two and accepts integer values from one through
eight through the versioned player settings API. Available capacity is the smaller of
that limit and the freshly observed undrafted, living, non-downed workers without a
mental state whose work settings apply. Within that bound, each goal declares a labor
profile of native work types its methods put pawns to (construction for comfort,
expansion, defense and repairs; research; mining, plant cutting or crafting for resource
targets; hauling, cleaning, handling or firefighting for upkeep). The same pawns are
counted per enabled work type, committed work occupies one pawn of its profile, and a
candidate whose every profile type is occupied defers with `labor_unavailable` naming
the bottleneck work type instead of taking a slot. An unknown work-settings census
leaves only the coarse worker bound; an empty profile is never labor-gated. This is a
scheduling bound on concurrent projects, not a labor forecast or completion estimate.
Accepted player projects and unfinished development actions consume slots; unresolved
issued actions remain counted even when blocked or cancelled. Completed actions release
their slot. Falling capacity never deletes or rewrites accepted orders, and explicit
player work is not rejected by this optional-work limit. Native admission, material
reservations and Hands dispatch guards still apply.

A selected goal whose planner has no method left this review (its retry bound is spent
or every fallback refused: SecureSupplies, MaintainStorage, MaintainCleanFacilities and
EnsureDefensiveLayout report this) yields its admission slot to the next
capacity-deferred candidate in the same review. The yield rewrites the review's rows
under the revision the planner loaded: the yielder reads `method_unavailable` and is
idle for the next ranking, and the recipient is selected (planners queued later in the
same wave see it, and method admission accepts it) but is not judged idle by the next
review, since its planner may not have run under the grant. Labor-deferred candidates
wait for the next review's fresh census. Waiting age advances only with native ticks and resets
for committed work; world changes and tick rewinds reset ranking history. A review
without authority (Manual, a player interruption, a restart before authority returns)
keeps the last ranking with nothing selected and `control_disabled` on the rows it
un-selects, so waiting ages survive it.
The shared plan retains the ranking, observed worker and per-work-type labor counts and
explicit deferral reasons in the routine review's development record. `GET /api/routines`
returns that record under `development` (null until a review has ranked): reviewed tick,
capacity, nullable worker count, sorted free-labor rows, committed goal IDs and one row
per optional goal with score, nullable deficit and risk, `waitingSince`, selection and
commitment flags, the deferral reason and, for `labor_unavailable`, the bottleneck work
type. The dashboard's Work view renders it read-only as "Development priorities"; the
panel hides itself when routine diagnostics are disabled. The same route returns the
roster planner's last report under `roster` (`policy.WorkRosterReport`, null until an
enabled review planned work; a disabled review or an unknown census keeps the last one):
the reviewed tick, `WorkCoverage` rows (demand, owners, capable per work type), the
`DecayingSkill` rows and every work pawn's `PawnProfile` with its trait effects, learn
factor per skill and forbidden and incapable work types, which the Colony view's
dossier joins by pawn id (#448). Native labor forecasts remain
evidence with unknown completion times. The `service/development` case samples this record
from a resumed controller across a kill-and-restart pair and asserts the bounds,
reasons, review-time research measurement and retained waiting ages above; pawn
progress on the admitted projects is campaign evidence from the `sustained/matrix-*` cases,
tracked in [issue #9](https://github.com/davidarcher/rimgovernor/issues/9).

## Method compilation and work allocation

Work allocation reserves the highest-skilled native builder first, then separates
growing, cooking and hunting among other available workers before sharing
intermittent medical roles. Extra hunters exclude the primary grower and cook
so native job order does not prevent sowing in small colonies. Capability reads,
native checkbox/manual-priority mode and explicit player work overrides still apply.
Additional growers exclude assigned hunters and player-disabled Growing work.
Small or capability-limited workforces may still require shared roles; assignments
alone do not establish completed sowing or food replacement.

An open assignment plan does not freeze the decision. Each work-planner step
recomputes the allocation and cancels any undispatched assignment whose premise
moved: the pawn's settings token or manual mode changed (the write could never
dispatch), the pawn left the decision, or the policy now wants a different
priority for a work type it sets. Assignments the fresh decision still agrees
with stay open; the method identity carries the before-token, so the same
settings against a moved pawn are a fresh method rather than a retired one.

### Environmental disruption

`home/colony_facts.environment.conditions` reports current-map native condition
IDs, definitions, implementation types, labels, permanence and remaining ticks.
Permanent conditions have no remaining-tick estimate; reading their duration can
itself trigger a native error/pause and is deliberately avoided.

`ColonyPlan.control.disaster_recovery` retains load-scoped service deficits and
stock observations. Active conditions distinguish `disrupted` service from
`temporary_survival`; expiry leaves `recovering` until every tracked service gate
passes. Missing observations retain `unknown`, and a load change or tick rewind
discards the old episode. Restored episodes do not absorb unrelated later shortages.
Food, production, shelter, sleeping, temperature, cooking, power and storage use
the existing controller gates. Affected services and needed wood acquisition are
promoted to survival priority without superseding combat or medical emergencies.
This is coordination within the existing goals, not an additional executor.

An unusable cooking bench permits one campfire construction method when no
campfire already exists. Unusable benches receive no new cooking bills.
Replacement cooking searches at most eight nearby observed free cells with native
previews; it does not require a new starter-house footprint.
Starter heating is suppressed only by a usable campfire inside the selected room,
not by an arbitrary stove or a campfire elsewhere on the map.
An existing unfueled campfire is not duplicated. `RecoverDisasterServices` selects
bounded refueling, structural repair and breakdown-repair jobs through native
WorkGivers. `home/recovery_state` observes exact building health, fuel, breakdown
and electrical state. Native definitions that do not use hit points, including
sleeping and butcher spots, do not require structural repair; unknown definitions
retain damage risk. `home/recover_service` preserves work permissions, allowed
areas, forbidden supplies, reservations and player-forced jobs. Hands previews
again at dispatch; `service_recovered` requires fresh target health, breakdown or
fuel evidence. Missing targets and interrupted labor never count as completion.
Unavailable supplies resume selection only after observed prerequisites change.
An unreconstructed destroyed building remains a deficit requiring accepted
rebuilding work. Power recovery requires actual service from enabled consumers,
excluding player-switched-off loads and generators. Solar flares defer generation changes while the cooking fallback remains
available.

During native toxic fallout, `home/recovery_area` can lease an existing wholly
roofed, reachable allowed area. It refuses unsafe areas and any widening of a
player restriction. The saved lease expires after 600 ticks, condition expiry or
a load change. Any later area setter drops the stale claim immediately, including
a change and reversal between observations; there is no permanent "player owns
this pawn's work area" record any more, so the controller may issue a fresh lease
for that pawn right away. Leases belong to one map; a returning pawn's expired
lease is released without changing another map's area setting. These areas
restrict work destinations; they do not make travel paths or every environmental
hazard safe. No observed refuge produces a blocker. Outdoor acquisition and field
expansion pause during the roof-sensitive hazard and become eligible again after
expiry. Native growers retain existing fields and resow lost crops under normal
work permissions; forecast harvest never substitutes for stored food.

Stock snapshots describe accessible stock, not causal consumption accounting.
Service deficits observed during an event can predate it; the record does not
establish that the event caused them.

### Shared compilation

Methods compile small batches of semantic construction/zone/native actions. Starter
templates rank nearby legal shelter sites, then use bounded native previews. Starter
farms and food-supply expansion share one farm site score (`policy.PlanFarmSites`):
each square patch (4x4 down to 2x2) is rewarded by the crop's fertility-adjusted
nutrition rate and charged, as fractions of a normal cell's daily output, for walked
travel from the anchor, hauling to storage and fragmentation (per patch and per edge
cell), so distant rich soil loses to suitable local soil. A patch adjoining a
controller-created zone growing the same crop is a contiguous addition and earns a
credit instead of the fragment charge; player zones, occupied, roofed, protected,
unreachable and undescribed cells never become free land. Isolated 1x1 cells are used
only when no larger patch meets the crop's fertility floor. Each selected patch keeps
its scored terms (`FarmSitePlan.Explain`) so acceptance evidence can say why a site
won. Expansion chooses crop and patches jointly (`policy.PlanField`): every available
edible crop with complete native facts is planned over its own fertility floor and ranked
by net nutrition per needed cell, so a fertility-tolerant crop wins on poor soil; a
remaining season under 2.5 grow cycles excludes a crop, an unknown remaining season while
sowing is possible is treated as short, and a stored-food runway under 2.5 cycles of the
fastest crop is urgent — both prefer the fastest crop that can plant. Existing zones are
never re-cropped. Open hunting, foraging or a butcher-spot build under EnsureFoodSupply does
not block a field batch and a sown field does not block acquisition (the store exempts each from the
other's open work, mirroring the acquisition-over-bill exemption); a second field batch
still waits for the first to resolve. The butcher spot and the goal's bills are exempt the
same way (#260): a spot waits only for a pending spot, a bill only for an open bill, and
neither blocks the fields, foraging or hunts beside it. Each batch previews at most six patches inside the
shared step budget. The field planner also requests `SunLamp`, `HydroponicsBasin` and
`Heater` definitions, plans sites over the planning window (`ColonyProjection.Cells`,
read on demand through `observations_get_cells`; see the state store in
[go-clock-recovery](go-clock-recovery.md)) and decodes `PlanningFacts.environment` into
`ColonyProjection.Environment` (`policy.ControlledEnvironment`: lamps with native growth
cells, growers with sow tags, indoor rooms, per-network headroom with
`NightHeadroomW`/`CalmNightHeadroomW`); it is observed only and unknown when the native
side withholds it. Site-type selection (`policy.PlanSiteType`) ranks every crop under
outdoor, greenhouse-reuse (roofed soil a running lamp lights), greenhouse-new (roofed
indoor soil plus one lamp placed where its growth disc covers the most soil),
hydroponics (new basins on lit roofed floor, for every crop with the `Hydroponic` sow
tag) and
dark-room (unlit roofed indoor soil for a zero-glow crop) by the same net-nutrition-per-
needed-cell score, charging construction per 100 steel-equivalent of the building's
cost list (a component priced as 17 steel by market value) and power per added kilowatt
(a lamp on its 55% day schedule) against the best network's day headroom (lamps) or
night/calm-night headroom (basins, heaters), and a heater per room or outdoors below
6C, the native optimal-growth minimum; the default weights
(`policy.DefaultSiteTypeWeights`) are the game's prices amortised over a 60-day year,
so a sun lamp costs about 7.4 cells of output and a basin pays only for a crop that
gains from its fertility. A kind without the power,
heater, infrastructure or crop compatibility it needs stays in the candidate list with
its reason, and an unknown environment leaves only outdoor candidates. Controlled kinds
ignore the outdoor season, and native growing-zone creation checks each cell's own
growing season (its room temperature) rather than the map's, so a heated roofed
greenhouse sows in winter. Candidates are enacted in score order: zone kinds preview
growing zones, construction kinds preview the lamp or basins as building actions
(the lit soil is planted by a later batch once the game reports it; a building batch stops
at what the preview's observed stock can pay for, and a basin's footprint is the native
centred 1x4 rect), and a candidate the game refuses to place or the store cannot reserve
falls through to the next, at most three per step. Open farm-infrastructure work blocks the next batch like open zone work,
and the store's field exemption covers `SunLamp`/`HydroponicsBasin`/`Heater` plans.
A built basin sows its definition's default crop, so before selecting new sites each
step ranks every observed grower that can sow (`policy.PlanGrowerCrops`: the available
edible crops carrying the grower's sow tag by nutrition rate over its fertility, the
fastest first under urgency) and commits a one-shot `grower_crop` patch
(PatchBuilding `plant_def`, CAS-gated on the grower's current crop) for a grower not
on the winner, once per grower per goal epoch. The `farm/select-*` cases
assert the traced selection kind/crop, the winner's term breakdown and every loser's
reason; `farm/select-greenhouse` and `farm/select-hydroponics` stage a lit, heated room under a cold
snap through `FarmEnvironmentFixture` and audits the zones or basin placements inside
it, and `-environment hydroponics -unavailable-crops Plant_Rice -expect-crop
Plant_Potato` proves a built basin re-cropped to the winner. `farm/calendar` holds the typed read's growing
calendar to `home/status` and `home/world` and records the seasonal thresholds a review derives from it.
Expansion also charges native harvest work per nutrition against the observed
number of enabled growers and the delay before the first harvest: scarce growers
favor corn, while sufficient growers favor rice. With an observed absence of
capable cooks, crops natively preferred raw (such as strawberries) earn a cooking
credit. Unknown worker facts add no such terms. Crop and grower re-cropping use
the same labor and cooking terms.

Each edge shared with an existing growing zone or an earlier patch in the same
batch pays a blight penalty. An intervening roofed empty cell or occupied impassable
cell earns a firebreak credit; this is a placement preference, not proof against
all fire or blight spread. Pollution is checked per cell against the crop's native
requirements, including toxipotatoes. Zero-glow crops require observed darkness
and an affirmative native diet allowance; colonist ideology penalties exclude
nutrifungus. Planning cell delta reads compare pollution and glow before omitting
unchanged cells.

Feasible indoor sites earn explicit `risk-fallout` and `risk-frost` credits for
observed ToxicFallout and the seasonal harvest gap. These credits never bypass
power, heating or crop compatibility checks. A zero-day outdoor season does not
exclude an indoor crop. The score trace carries labor, harvest-delay, cook,
blight, firebreak and risk terms when applicable.

Insufficient farmland does not reject an otherwise legal
shelter. Selected field capacity remains separate from observed growing cells and the
production gate. Work
allocation uses observed capabilities/skills, job load and stable identity tie breaks;
it respects the game's checkbox versus manual-priority modes and explicit player
overrides.

## Admission, budgets and dispatch guards

Both entry paths commit through revision/context guards, geometry/native preflight and
shared resource accounting. Unissued slots reserve native costs; issued blueprints use
native deficits instead of a second reservation. Admission reserves all accepted
projects. Dispatch budgets in stable ready priority order, allowing affordable earlier
work to proceed after stock is consumed. Later and dependency-gated projects yield; the
selected remaining batch, earlier ready work, player reserves and uncertain writes stay
protected. Dispatch rechecks current stock and player resource policies. Changed or
unknown costs require validation. Native bill ingredient selection and final consumption
enforce persisted player resource floors and stopped inputs. Transient unissued
construction commitments apply only during a supervised clock lease; native
blueprint/frame deficits are counted directly. Player bill filters and suspension
settings remain unchanged. Exact native ingredient alternatives include quantity
conversion and are not truncated with display rows. Dispatch ingests buffered and fresh
native clock events before using a captured load token/tick and again after
preparation. A busy writer cannot defer a known player hold or danger event until after
an old order has been sent. External holds are recorded as clock holds independently of
routine observation and review revisions; there is no player-direction counter.

### Coupled orders

Every routine order dispatches under the running clock window (#243, #244); the
count of orders in a step is never a reason to stop the clock, and batching
independent orders at speed is expected. A planner marks a plan dependency
coupled (`domain.ActionDependency.Coupled`) exactly when the later order is
written against the result of the earlier one in the same plan: an id the
earlier write produced (create a zone, then set its settings), a position it
reached (draft, then move). An ordering-only dependency, where the later order
merely waits for the earlier one to complete, is not coupled, and no routine
planner emits a coupled dependency today. Only a coupled order requests a
stop: when its prerequisite completes under a running window the scheduler
stops the window at that completion (`ClockSchedulerResult.Coupled`), the
worker prepares the order against the stopped map, and the next step admits a
window again.

## Native execution

New growing zones validate crop identity and pollution compatibility before
registration, and configure the crop in the same native operation. Hands records intent
before writes, retains partial progress and verifies native outcomes. Routine execution
yields after 12 operations. Nothing dispatches while paused: player submissions are
guidance executed under the world's root plan only while the bot is running, the same
way routine methods are.

## Foothold gates and forecast limits

`FOOTHOLD_STABLE` requires every gate: sufficient sleeping capacity in a roofed indoor
room, at least three stock days of food by default, at least ten observed growing cells per colonist summed across edible farms, indoor food
storage, usable cooking with a bill, safe sleeping temperature, sufficient power if
electrical thermal loads exist, no critical patient, two armed colonists (or everyone in
a smaller colony), no active threat, and verified work assignments ([work planner](work-assignment.md)). Accepted blueprints
cannot satisfy these gates. Stability is reversible when observations change. The food
forecast apportions shared nutrition by native demand among eaters permitted by diet,
policy and safe access, reserving animal shares and crediting held food only to its
observed holder. Earliest-expiry allocation uses native rot deadlines at the current
temperature; the lowest per-colonist runway drives the food gate. Invalid supply
observations remain unknown. Future harvest, changing temperatures, job selection and
food sharing are not guaranteed. Harvest ETA remains an optimistic lower bound.

The maintained food goal budgets each crop's capacity from native daily demand and
yield, covering consumption during its growth allowance plus the persisted food reserve.
That reserve, the food latch's thresholds and the wood thresholds are seasonal: the
colony read carries the tile's growing calendar (`policy.Calendar`: growing days per
year, days until the seasonal temperature leaves and next re-enters the crop range,
the length of the current or coming non-growing stretch on the same daily walk, the
native sowing flag, season and day of year), and each review widens the configured
policy by its harvest gap (`RoutinePolicy.Seasonal`). The gap is the wait until growth
resumes plus one rice cycle while nothing grows, and the coming non-growing stretch
plus that cycle, phased in over the gap plus one field cycle and complete on the last
growing day, while crops grow, so the thresholds are the same on both sides of the
frost; a year-round tile has none and an unknown calendar keeps the flat thresholds. An
observed growth pause extends the gap: a `VolcanicWinter` or `ColdSnap` condition
with a native remaining-duration read (`RoutineFacts.DisasterConditions`,
`policy.GrowthPauseDays`) adds its remaining days while crops grow, stands in for a
shorter seasonal wait while they do not, and is the whole gap on an unknown
calendar; a condition without that read contributes nothing. Food
minimum and target both grow by the gap (capped at one year) and the wood minimum,
target and maximum by the target's factor, so a runway is measured to the next
possible harvest rather than to stock exhaustion and fields, larder and woodpile fill
before the first frost. The foothold food gate keeps its flat minimum.
Capacity includes native demand from colony animals permitted to eat that crop or
preserved product; future grazing is not credited against this budget.
Rice, potatoes and corn are ranked by native yield, soil response and remaining
seasonal temperature window. A stock buffer shorter than the fastest crop's growth
allowance prioritizes that faster crop. Expansion preserves existing zones and shelter access;
unknown capacity cannot complete the goal. Projected yield never counts as stock.
When rot risk limits runway, available long-lived native recipes can receive a
target-count bill. A bill receipt cannot certify preserved food. Food stockpiles
use a distinct label from crop zones.
See [forecast contracts](forecast-contracts.md) for animal feed, crop/construction
labor, medical, mood and power projections and their input limits.

## Progress, capacity and bootstrap dialogs

Goals record selected methods, attempts, step IDs and observable progress.
Native events or a pause arriving during method selection retain a pending
review; neither a refusal nor a no-op acknowledges newer evidence from an old read.
Invalid templates have a bounded alternative-site search; unknown or failed native actions
become explicit blockers. A no-progress watchdog prevents silent indefinite waiting.
Structured `home/order` refusals from unapplied dry-run previews block the affected
goal while subsequent reviews continue. Changed worker jobs, cargo, health, work
settings, equipment, stock or upkeep evidence permit a fresh selection; a
2,500-tick window also rechecks routes. Unknown failures and dispatched writes
retain their existing reconciliation requirements. This never retries an uncertain
write or relaxes native interruption guards. A write the transport proves it never
issued (it failed before the native call, `domain.ErrWriteUnsent`) is not uncertain:
it records `ReceiptUnsent`, a no-effect proof the same authority may retry, rather
than reconciling against a native ledger entry that never existed. A write that was
issued but never answered (the native call timed out on the controller side) records
`ReceiptUnknown` and reconciles through `receipts_lookup`: the native ledger admits
an attempt on the game's main thread before scheduling any effect and serves lookups
on that same thread, so a lookup under the attempt's own load that finds no entry
proves the write was dropped before admission. That lookup is complete no-effect
evidence at its own tick and the action returns to `Pending` for a fresh attempt,
instead of holding forever (#71). An admitted entry still in flight keeps holding
until its receipt is recorded.
Need-recovery admission previews likewise retain native refusals on the mood goal
and can consider another measured need. GABS errors retain the requested tool identity
even when the native payload omits it. Dispatch failures remain subject to Hands'
existing uncertainty and observed-recovery contracts.
Above eight colonists, starter sleeping uses verified room and native footprint fitting,
preserving its entrance aisle and three service rows. The starter uses its available
sleeping capacity before proposing another nearby shell on observed free ground with
native placement previews. Existing rooms, zones and their entrances remain intact.
Additional farm batches use native crop requirements and current usable capacity,
bounded to 32 disjoint patches per action. Controller fitting checks do not establish
larger-colony gameplay acceptance. Watchdog holds retain their tick, reason and
completed action identities. A newly observed completion of tracked work can release
that exact hold and continue the existing goal without replacing methods or receipts.
Unchanged state, rewinds, cancelled or failed actions, different blockers and Manual
retain the hold; emergencies still suspend lower-priority work. The initial
faction/settlement naming prompt is a maintained bootstrap goal. Its semantic native
action validates the exact observed generated suggestions, uses the native naming
callbacks and verifies the names and dialog closure. Other forced dialogs retain the
normal hold behavior.

## Hunting admission and dispatch

Autonomous hunting screens current wild-animal observations before compiling a
designation. Harmless, undesignated prey must be within 100 cells of the colony anchor
and more than 25 cells from live wild predators, using square-grid distance. Unknown
predator flags or positions prevent selection. The food goal retains candidate IDs and
predator rejection evidence. The hunting budget (two outstanding) is zero while the
roster is known and no [hunter](work-assignment.md#situational-roles) (`HunterFor`:
Shooting, a ranged primary, never a Brawler) is on it, for stock and pest hunts alike.
Compiled hunting methods retain the exact prey identity,
anchor and action signature. A hunt follows its animal (#321): the planned cell is
the hint native echoes in the evidence, the census row is matched by the animal
wherever it now is, the snapshot token binds the animal, its corpse and its
designation but not its position or health, and native waives the expected-cell
rule for an animal. Immediately before writing, the shared runtime rechecks
wildlife, outstanding hunt count and paused native tick under its
writer lock. It then verifies the selected animal's hunt designation. Missing legacy
target metadata and changed observations block without a write; unconfirmed writes
remain uncertain. Native evidence requires an enabled hunter with an ordinary ranged
weapon, or a melee weapon or bare hands against meleeable prey (safe prey of body size
at most 1.0, which flees rather than retaliates; #260), a Danger.None path avoiding
predators by 25 cells and an ordinary prey death action.
Supervised play pauses when an active hunt loses that route. Future prey movement
and shooting positions remain uncertain; native external inputs are not atomic
with controller checks.

Hunt acquisition reports revenge chance, same-race herd size within 25 cells
(including the prey), melee eligibility, downed state and the longest ordinary
weapon range among eligible hunters. Route safety remains authoritative. Food
selection preserves forage priority, then prefers downed animals and lower
revenge chance times herd size; equal costs retain native order. Hunt channels
cap that exposure at one for Revenge risk and estimate pursuit work as
7500 / (1 + range / 25) pawn ticks, or 2500 for downed prey. These are planning
estimates, not inventory or proof of a kill. Incendiary weapons are excluded.
Already-dead fresh corpses remain pending butcher material rather than hunts.

## Pest clearance

A recognised pest (#247) is a wild animal hunted for what it destroys rather than
for meat: the native pest list (`NativeHuntAcquisition.PestDefinitions`) and the
controller's (`policy.PestDefinition`) name the same definitions, today only
`Alphabeaver`. Alphabeavers arrive factionless and never hostile, so no emergency
census answers them. `ClearPests` opens at foothold priority (2) with a deficit of
one whenever the wild-animal census (known) counts a pest anywhere on the map, and
recovers when it counts none; an unknown census neither opens nor recovers it. The
hunt census offers every eligible pest on the map as a hunt row after the food
prey (nearest the colony first): a hunt of one unit of the pest's corpse, `food`
false and no nutrition, which the food and wood selections pass over. Native waives
the 100-cell prey distance, the safe-prey rule and the butcher-bill rule for a pest;
the hunter must still have Hunting active, an ordinary ranged weapon and a safe
route, the two-outstanding-hunts bound still applies, and a pest in a mental state
(manhunter) is not offered, the defense family answers it instead. The pest planner
admits one hunt per pest up to the hunting budget and the census count, under
methods `pest-hunt-*` and plans `routine-pest-hunt-*`; the method id is salted
with the goal's admission count so a re-plan of the same animal at the same cell
is a fresh method. Unlike the stock goals, `ClearPests` plans animal by animal:
a hunt already dispatched holds its own animal and counts against the two
outstanding hunts, but neither the planner nor the store's open-work rule holds
the next animal's method behind it. A pending or prepared pest hunt whose animal the wild-animal
census no longer lists is cancelled before the next selection; one whose animal
wandered off follows it (#321). A dispatched pest hunt is finished natively when
the animal is dead (completed, its corpse the output in whatever state it lies)
or has left the map (unsuccessful, nothing to show), not when a fresh unforbidden
corpse is observed as a food hunt is. The hunt-stall rule never cancels a
dispatched pest hunt (#455): the goal has no other prey to try for that animal,
and withdrawing the designation only re-plans it. A withdrawal of a cancelled
acquisition under a later same-world authority generation keeps the plan
loadable: the dispatch admission agrees with the withdrawn progress on the
world, plan and revision, not on the native generation (#455). Pest hunts never
count toward edible stock.

## Wild-plant acquisition

Wild-plant acquisition limits new orders by the remaining per-colonist nutrition target
and already designated native harvest yield. Pending yield limits duplicate acquisition
but never counts as stored food or clears food risk. Individual plants are indivisible,
so a batch can exceed its remaining target by one plant's yield.
Food and wood methods use `home/acquire_resource` with the observed plant identity,
output resource, location and colony/load/map. Native eligibility is checked again
before designation. A fresh regrowth observation can renew a confirmed completed
designation under the same maintained goal; its prior action and receipt remain in
history. Pending, uncertain and cancelled acquisition orders prevent renewal at
their location. A new designation still does not certify harvesting or stored food.
A harvest completes once its output landed spawned, unforbidden and unfogged on the
map; a stack a colonist then eats or hauls away stays proven (#260), so a starving
colony's forage never blocks the hunt that follows it. A hunt-only food plan is
planned and admitted while the goal's plant harvests stay open; an open hunt still
blocks the next hunt, and a second forage waits for the first.

## Bounded combat response

A bounded squad method assigns at least two capable defenders per observed opponent,
up to four opponents and eight defenders. It supports manhunters and confirmed hunting
predators up to native body size four, and observed humanlike opponents; ranged
opponents require ranged defenders, and the roster's [line split](work-assignment.md#situational-roles)
orders who takes which. Native previews decide attack legality. The clock
acknowledges only the exact inspected opponents after dispatch; new threats and severe
injury retain their guards. Medical triage can run alongside defense. Blocked emergencies
prevent routine waiting work from restarting time; unsupported encounters remain explicit holds.
Automatic rescue, firefighting and heat-escape orders require a danger-aware native
route method; they remain outside these bounded emergency methods. Explicit player
rescue continues through its separate directed action and native outcome checks.

One observed melee-only humanlike
raider requires three capable equipped colonists with health at least 85% and no tending need.
While the enemy is distant, an unarmed defender can fetch a native-approved ground
weapon within 12 cells. The shared action waits for the exact equipped identity;
held weapons and existing equipment are preserved. Missing equipment near the
threat retains a hold. Selection prefers equipped defenders and weapon-relevant skills.
Confirmed downed raiders remain faction hostiles in the native census but no
longer require combat. The controller subtracts only exact observed incapacitated
identities; unknown and unlisted threats retain risk. Supervised play ignores
incapacitated enemies for hostile pauses and checks them again if they stand.
This allows ordinary treatment and owned stand-down without attacking downed pawns.

Downed permanent manhunters do not keep active combat open; any later recovery is
observed as a fresh threat.

Hostile buildings (#246) are threats in their own right: the native threat census
lists every spawned insect hive and every hostile-faction building with hit points
and combat power (crashed ship parts) under `hostileBuildings`, each with its
definition, hit points and a snapshot token derived from the thing itself. A
listed building within `policy.DistantThreatCells` (50) of a colonist keeps the
active-combat deficit open and holds the clock as an unsafe threat exactly like a
hostile pawn: the fight is planned under the stopped clock and run under watched
combat windows; one further out is neither a deficit nor a hold, only a squad
target while the goal is open for something else (#340: a map-gen hive in a
cave held every window for good); the building's id is never
acknowledged to the native watcher, which resolves every acknowledged id as a
spawned pawn and only stops for unacknowledged hostile pawns, so a lone building
admits a combat window with an empty acknowledgement list. A building the
planner cannot answer -- every colonist downed or incapable of violence, so
`RoutineDefensePlanner` reports `no_eligible_squad` at this stop -- is watched
rather than held (#326): the deficit stays open, colony windows run around it,
and a plan admitted at a later stop makes the next window a combat one. A
hostile pawn with no squad still holds; a raid never auto-advances. Squad defense assigns buildings only once no eligible hostile pawn remains (a
hive's insects and a ship part's guards are the live danger). Each census row
carries the building's occupied rect (`occupiedCells`); the defense planner
reads native lines of fire from every ranged-equipped defender's cell to those
cells and a defender with a line of sight to one of them within its weapon's
range is planned as a shooter (`RangedAttack`, fired from where it stands, as
the native ranged predicates need the target in range now), the rest walk in
with `MeleeAttack` (#327). The melee and ranged executors read the building's
token from the census row instead of a pawn snapshot, and the native attack
operation accepts a census-listed building as its target under that token in
either mode, with destruction as the completing outcome.
Combat compilation, dispatch and clock admission inspect colonist health. Unknown
health or a colonist at the native half-health limit produces an explicit hold;
an unchanged injury cannot repeatedly rearm the combat clock. The native injury
thresholds and zero injury cooldown remain unchanged.
Autonomous orders also request the native main-thread health guard, so changed health
between a preview/read and dispatch cannot admit another combat order.

Development methods use native completed furniture, electrical topology, research
availability/prerequisites and observed placement cells. They build generators and
bounded conduit batches, provide basic laboratories through the shared research
controller, fit indoor beds/dining furniture and outdoor recreation, and
extend shelter capacity through the shared room-shell method. Native previews and
shared material admission govern every construction action. Only completed buildings,
powered connected loads, observed research progress/completion and indoor capacity
establish outcomes; blueprint receipts do not. Changed native prerequisites can release
a development blocker without discarding existing projects or player selections.
Development placement requires an available assigned builder meeting the native
construction skill requirement; a definition requiring no skill needs only one
available pawn with Construction enabled. Construction assignment selects the strongest
available skill before balancing other work; player overrides remain authoritative.
Research assignment weighs native Intellectual skill and, in checkbox mode, removes
routine hauling/cleaning from the selected researcher so those earlier jobs cannot
starve research indefinitely. Explicit work overrides retain authority.

### Shrine breach readiness

Breaching a sealed ancient shrine releases its guards at once, so
`policy.ShrineBreachReadiness` judges the gate before any wall goes (#457).
It is a decision, never an order: `ColonyStatus` reads it for
`/api/player/colony` and the dashboard, and the breach goal (#458) will
re-read the facts before drafting anyone. Every hold is a reason, in this
order: `not_sealed`, `no_breach_wall`, `emergency_active`,
`squad_too_small` (fewer than two eligible armed colonists, one under
Peaceful), `no_ranged` (no ranged weapon of 20 cells or more),
`no_traps` (fewer than three built spike traps within 12 cells of the
chosen wall's outside cell; none under Peaceful), `threat_unknown` and
`threat_too_high` (raid points over 300 for a squad of two, 500 for
three, 800 for four or more; Peaceful ignores raid points). The chosen
wall is the deconstructible perimeter wall nearest the colony centre;
the squad lists every eligible defender, shooters first. Squad eligibility
is `SelectSquadDefense`'s plus being armed. The storyteller is not
observed yet, so the Peaceful softening stays off. The colony status read
fetches combat pawns, the emergency census and a 27x27 defense-site
window around the wall only while a sealed shrine shows a breach wall.

### Ancient shrine breach goal

`ClearAncientShrine` (the `shrine` routine family, #458) owes work on every
shrine whose room touches Home that is still sealed, or open with a guard
seen standing; an open shrine nobody has looked into is not its target.
The review journals one `ShrineHolds` row per censused shrine: the
readiness reason above, `ready` with the chosen `wall`, `guards_alive`
once the wall is down, or `readiness_unknown` under a native without the
readiness reads. Repairs precede the breach and the breach precedes
`ClearHomeObstructions` (a shrine wall is `ancient_danger` to the clearance
census, which never touches it). The planner re-judges readiness live and
admits one method for the first ready shrine: an `OwnedDraft` for each
drafted defender, a `Movement` to a standing cell behind the trap line
(at least five cells straight out from the wall's outside cell, within the
trap radius, never a trap cell, nearest eight cells out first; a defender
with no cell stands where it is), and last a breach `Deconstruction` of the
chosen wall that depends on every draft and move. One colonist is always
left undrafted for the deconstruct job: the squad's last defender when the
colony is no larger than the squad. The breach deconstruction is
inspected against the shrine census, not the clearance census, and is
eligible only while the shrine is still sealed; a wall that vanishes or
changes definition is absent. The wall falling ends the method: the plan
has no open work, the worker releases the owned drafts and `ActiveCombat`
answers the guards, which the goal then holds `guards_alive` until they are
dead or downed. Eight attempts per wall and goal epoch; Stop and Manual
release the drafts and controller-owned designations as for any owned
draft. Ranged breaching and deliberate casket opening (#460) are not
composed.

Caskets follow the default never-open policy (#459). `policy.CasketDecision`
names each casket's row in `ShrineHolds`: `shrine_sealed` and
`guards_alive` while the breach is owed, `leave_sealed` for a casket that
holds anything (the ancients inside are a risk with no upside), `claimed`
for one already the player's, and `claim` for an empty unowned casket. An
open, guard-free shrine touching Home with a `claim` casket is still the
goal's target: before any readiness read the planner reads each such
casket's claim token and admits one method of `claim_building` actions
(PatchBuilding claim, one per casket; a casket the fresh read already shows
as the player's is skipped). The claim is a one-shot CAS write like a bed's
medical flag: no pawn, no simulation window, refused natively as the
action's own unsuccessful outcome. Filled caskets keep the clearance
census's `casket` hold; `ClearAncientShrine` never designates a casket,
and a casket under 20% hit points explodes, so the census's hit points are
a safety reading only. The deficit recovers once every casket is filled or
the player's.

## Autonomous supply safety

`ManageSupplySafety` owns both directions of the forbid flag for visible,
player-owned or unowned haulable items, including later event drops. A complete
`event_loot` census reports each item's identity, cell, current forbid flag and
native hauling safety. There is no first-seen or player-forbid exemption. Unknown
or over-limit censuses do not authorize changes.

Safety checks the item cell, each reachable eligible colonist's native approach
path, and the return path to the native storage choice. Fire within two cells,
traps on the path, native region danger, and visible hostiles exposing the path
with line of sight prevent Allow. Hostile exposure uses the native weapon range
with a five-cell margin and a twelve-cell minimum for melee threats. This is a
conservative current observation, not a prediction of enemy movement. The native
boundary repeats the check immediately before applying Allow or Forbid.

Unsafe items are forbidden before safe items are allowed, in batches of eight.
Forbid changes no pawn orders and may execute during an emergency; Allow retains
the emergency gate. A changed safety census cancels stale undispatched proposals.
The census is bounded to 4096 items. Each later review may reverse a prior decision
when danger clears or returns. A designation receipt proves the flag only; native
storage observations prove hauling completed. `supply/loot-safety` exercises a
mid-run distant drop, danger removal, both flag changes and stockpile delivery.
