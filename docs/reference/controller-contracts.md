# Controller and colony contracts

[Documentation](../README.md)

These are the current routine-control rules and completion gates. For the reasoning
behind them, read [the control loop](../explanation/control-loop.md).

## Observation

Pause and read native state. Sequential observations are not an atomic snapshot.
`home/colony_facts` reports accessible shared-diet nutrition and fed consumption, viable
crop cells, indoor sleeping, temperatures, cooking, safe nearby wild-plant access,
starter terrain/support affordances and actual definition costs. Native growers retain
ownership of cultivated crop harvest timing. Unknown observations never certify
recovery.

## Chat entry

Only a new human chat revision invokes `planner.py`. Mode changes and routine native
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

Priority-class 3 goals for food storage, basic equipment defense, wood and maintained
player resource targets share a deterministic admission order. Scores combine a
0–100 observed deficit fraction, a 100-point player-target preference, one point per
2,500 waiting game ticks and a 20-point selection hysteresis bonus. Stable goal IDs
break ties. These weights are policy ordering, not measured benefit or time estimates.
Unknown resource stock cannot admit a new project. Emergencies retain precedence.

`max_development_projects` defaults to two and accepts integer values from one through
eight through the versioned player settings API. Available capacity is the smaller of
that limit and the freshly observed undrafted, living, non-downed workers without a
mental state whose work settings apply. This is a coarse concurrency bound, not a
profession-specific labor forecast. Accepted player projects and unfinished development
actions consume slots; unresolved issued actions remain counted even when blocked or
cancelled. Completed actions release their slot. Falling capacity never deletes or
rewrites accepted orders, and explicit player work is not rejected by this optional-work
limit. Native admission, material reservations and Hands dispatch guards still apply.

Methods that cannot produce new work yield their admission slot to the next eligible
candidate in the same review. Waiting age advances only with native ticks and resets
for committed work; context/direction changes and tick rewinds reset ranking history.
The shared plan retains the ranking, observed worker count and explicit deferral reasons
under `control.development`; the dashboard displays them. Native labor forecasts remain
evidence with unknown completion times. This ordering does not implement comfort,
research or expansion methods, or establish their native gameplay acceptance.

## Method compilation and work allocation

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
and electrical state. `home/recover_service` preserves work permissions, allowed
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
a load change. Any later area setter relinquishes ownership, including a change
and reversal between observations. Player overrides are preserved. Leases belong
to one map; a returning pawn's expired lease is released without changing another
map's area setting. These areas restrict work destinations; they do not make
travel paths or every environmental hazard
safe. No observed refuge produces a blocker. Outdoor acquisition and field
expansion pause during the roof-sensitive hazard and become eligible again after
expiry. Native growers retain existing fields and resow lost crops under normal
work permissions; forecast harvest never substitutes for stored food.

Stock snapshots describe accessible stock, not causal consumption accounting.
Service deficits observed during an event can predate it; the record does not
establish that the event caused them.

### Shared compilation

Methods compile small batches of semantic construction/zone/native actions. Starter
templates rank nearby legal shelter sites and disjoint fertile farm patches, then use
bounded native previews. Fragmented soil can use smaller patches within the same zone
budget; insufficient farmland does not reject an otherwise legal shelter. Selected field
capacity remains separate from observed growing cells and the production gate. Work
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
native clock events before using a captured direction and again after preparation. A
busy writer cannot defer a known player hold or danger event until after an old order
has been sent. External holds advance the durable player-direction counter independently
of routine observation and review revisions.

## Native execution

New growing zones validate crop identity and pollution compatibility before
registration, and configure the crop in the same native operation. Hands records intent
before writes, retains partial progress and verifies native outcomes. Routine execution
yields after 12 operations. An explicit current player request may dispatch through the
same Hands in Manual, while the clock stays paused; it does not dispatch unrelated
autonomous work.

## Foothold gates and forecast limits

`FOOTHOLD_STABLE` requires every gate: sufficient sleeping capacity in a roofed indoor
room, at least three stock days of food by default, at least ten observed growing cells per colonist summed across edible farms, indoor food
storage, usable cooking with a bill, safe sleeping temperature, sufficient power if
electrical thermal loads exist, no critical patient, two armed colonists (or everyone in
a smaller colony), no active threat, and verified work assignments. Accepted blueprints
cannot satisfy these gates. Stability is reversible when observations change. The food
forecast apportions shared nutrition by native demand among eaters permitted by diet,
policy and safe access, reserving animal shares and crediting held food only to its
observed holder. Earliest-expiry allocation uses native rot deadlines at the current
temperature; the lowest per-colonist runway drives the food gate. Invalid supply
observations remain unknown. Future harvest, changing temperatures, job selection and
food sharing are not guaranteed. Harvest ETA remains an optimistic lower bound.

The maintained food goal budgets each crop's capacity from native daily demand and
yield, covering consumption during its growth allowance plus the persisted food reserve.
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

Goals record selected methods, attempts, step IDs and observable progress. Invalid
templates have a bounded alternative-site search; unknown or failed native actions
become explicit blockers. A no-progress watchdog prevents silent indefinite waiting.
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
designation. Harmless, undesignated prey must be within 50 cells of the colony anchor
and more than 25 cells from live wild predators, using square-grid distance. Unknown
predator flags or positions prevent selection. The food goal retains candidate IDs and
predator rejection evidence. Compiled hunting methods retain the exact prey identity,
anchor and action signature. Immediately before writing, the shared runtime rechecks
wildlife, the planned cell, outstanding hunt count and paused native tick under its
writer lock. It then verifies the selected animal's hunt designation. Missing legacy
target metadata and changed observations block without a write; unconfirmed writes
remain uncertain. Native evidence requires an enabled, ranged hunter with a
Danger.None path avoiding predators by 25 cells and an ordinary prey death action.
Supervised play pauses when an active hunt loses that route. Future prey movement
and shooting positions remain uncertain; native external inputs are not atomic
with the Python checks.

## Wild-plant acquisition

Wild-plant acquisition limits new orders by the remaining per-colonist nutrition target
and already designated native harvest yield. Pending yield limits duplicate acquisition
but never counts as stored food or clears food risk. Individual plants are indivisible,
so a batch can exceed its remaining target by one plant's yield.

## Bounded combat response

A bounded squad method assigns at least two capable defenders per observed opponent,
up to four opponents and eight defenders. It supports manhunters and confirmed hunting
predators up to native body size four, and observed humanlike opponents; ranged
opponents require ranged defenders. Native previews decide attack legality. The clock
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
construction skill requirement. Construction assignment selects the strongest
available skill before balancing other work; player overrides remain authoritative.
Research assignment weighs native Intellectual skill and, in checkbox mode, removes
routine hauling/cleaning from the selected researcher so those earlier jobs cannot
starve research indefinitely. Explicit work overrides retain authority.
