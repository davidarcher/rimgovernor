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
cooking, work coverage, power, storage, defense and wood. Food, wood and temperature use
separate entry/recovery thresholds. Emergencies suspend lower priority routine goals.
Methods, blockers, provenance and progress evidence live in the existing SQLite-backed
ColonyPlan.

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
remain uncertain. This does not certify a hunter's route or monitor threats after
designation. Native external inputs are not atomic with these Python checks.

## Wild-plant acquisition

Wild-plant acquisition limits new orders by the remaining per-colonist nutrition target
and already designated native harvest yield. Pending yield limits duplicate acquisition
but never counts as stored food or clears food risk. Individual plants are indivisible,
so a batch can exceed its remaining target by one plant's yield.

## Bounded combat response

A bounded combat method prepares two capable colonists for one small manhunting animal
or confirmed small predator hunting colony members. One observed melee-only humanlike
raider requires three capable colonists with health at least 85% and no tending need.
Unknown weapons, ranged raiders and multiple enemies retain a hold. Native auto mode
uses equipped weapons and individual firing previews. An obstructed shooter stays
drafted while other defenders engage; unavailable previews retain the hold.
Standing-target and current-hostility
guards protect dispatch. Defense uses threat readback, treatment and owned-draft
cleanup. Its clock acknowledges only that inspected
target after orders dispatch; other threats and severe injury remain guarded. Blocked
emergencies prevent routine waiting work from restarting time. Larger threats and
electrical generation still report explicit blockers.

Combat compilation, dispatch and clock admission inspect colonist health. Unknown
health or a colonist at the native half-health limit produces an explicit hold;
an unchanged injury cannot repeatedly rearm the combat clock. The native injury
thresholds and zero injury cooldown remain unchanged.
