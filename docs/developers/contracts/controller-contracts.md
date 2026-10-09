# Controller and colony contracts

[Documentation](../../README.md)

Current routine-control rules and completion gates. For the reasoning, read
[the control loop](../architecture/control-loop.md). Policy code lives in
`go/internal/policy`; planners in `go/internal/buildingruntime`.

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
loot holds. The [player API contract](go-player-api.md) owns its response shape.
**Concern progress** (`policy.ConcernProgress`): each active concern carries the method in play,
the observable it should move, the tick native evidence last moved it, the blocker
inspection tick and the blocker. Progress is native outcome, never dispatch. Blocker
reads: `blocked:no_worker` (designation dispatched, no capable pawn), `native_ineligible`,
`reconcile_write` (unknown receipt; reconciled by action identity before any retry),
`no_method`. `domain.TicksPerDay` (and the hunt, haul and harvest instances
`RoundsPolicy.HuntProgress`/`HaulProgress`/`AcquisitionProgress` of `ProgressContract`)
keys a stalled situation out for a bounded cooldown (`ProgressCooldownMax`, never
permanent) and planners rotate method or target (`Store.RecordProgressCooldown`). The cooldown is a separate mechanism from the shared refusal budget (`policy.RefusalBudget`): it keys out a situation that stalled (an accepted order that moved nothing), not a native refusal, so it needs no refusal class and always expires; the budget bars a subject native refused, by class and world. The
food concern walks acquire, cook, store, grow, naming `prerequisite:EnsureCooking` while
cooking is known missing. Prerequisites remain explicit dependencies; pawn work priorities schedule the
orders without reserving a builder for a Concern.

**Colony stage** (`policy.ColonyStage`: Foothold, Reserves, Stable, Development) is a
pure function of colony facts and progress records (`ReviewColonyStage`), never of
research. Exit criteria via `StageColonyFacts`:

| Stage | Exits when |
| --- | --- |
| Foothold | Roofed sleeping for every colonist, an active meal bill, an owned food stockpile (a standing meals, raw food, perishables or ingredients zone the store claimed; a player-flagged food zone does not count, and unread claims are unknown, not missing), runway at `FootholdFoodDays`, two armed fighters. |
| Reserves | Runway at `FoodTargetDays`, growing field sown, wood latch clear, research bench built, no production concern blocked. |
| Stable | Power online, season's climate answered, a doctor-capable pawn, production concerns unblocked for `StableTicks` (two days), Stable held `DevelopmentTicks` (three days), runway at twice target. |

Exits are laxer than entries (`ColonyStagePolicy`: stages above Foothold drop only under
two thirds of `FootholdFoodDays`, Development only under the target, Stable only after a
production concern stays blocked `StableExitTicks`). The stage climbs one step per review,
drops cascade, and unknown facts neither advance nor drop it.

Inspection-specific stage and prerequisite checks determine useful work.
There is no stage-based worker allocation or generic development admission queue.

The record (`ColonyStageRecord`: stage, since, first unmet criterion as blocker+reason,
held) lives on `Rounds.Stage` and sets the next review's budgets
(`policy.StageRoundsPolicy`): the research ladder walks 2/5/8/all rungs at
Foothold/Reserves/Stable/Development; food reserve and wood targets scale 1.5x at
Stable and 2x at Development. A Foothold colony with shelter unmet promotes shelter
planning into the critical planner wave. The stage
adds no action kind; the `rounds_review` timeline event carries
`stage`/`stage_blocker`/`stage_reason`/`stage_held` and `colony_stage` records each
change. `GET /api/spectator/now` (a pure read: no journal row, no speed request) adds
the pacing reason, effective TPS and last clock-stop latency split.

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

Throne-room requirements are not a wire field: the client builds a Go view from each
`RoyalTitleDef.throneRoomRequirements` in the def mirror (`DefinitionCatalog.ThroneRequirements`,
`WithThroneRequirements`) into `RoyalRung.Throne` before projection. A missing
`RoyalTitleDef` row or an unset or malformed requirement is an error wrapping
`bridge.ErrThroneRequirements` naming the title and requirement; the review logs it and
leaves royalty unknown. `disablingPrecepts` is not modelled. `policy.NextThroneNeed`
picks the first rung above the holder's with a throne.

- `MaintainPsylink` (`policy.PsylinkCandidates`): gives a psylink to each available
  colonist with a needs block and no psylink level. The neuroformer is acquired by
  MaintainResource (stock floor of one, `policy.NeuroformerNeeds`, while a candidate
  waits, none is held and royalty marks it `craftable` or `tradeable`). With one held,
  `UseItem` targets the colonist, at most twice per colonist per Episode. Level-ups
  (`policy.PsylinkLevelUps`: psycaster below level 6, no royal title) follow
  no-psylink colonists and never raise the stock floor. Psyfocus is kept by the
  meditation schedule, not this concern.
- Mood psycast (`policy.SelectMoodCast`): the mood planner commits an `Ability` on the
  mood incident when measured need relief is spent or absent (`no_measured_correctable_need`,
  `bounded_methods_exhausted`, `unowned_thought_pressure`; never while a facility concern
  owns the pressure). Caster: another available colonist whose royalty read lists
  `WordOfJoy` as a pawn-targeted psycast and whose Psyfocus stays >= 0.25 above the
  cost; most Psyfocus wins; any unread fact holds. Neural heat and cooldown are not
  read: native refuses (guards `entropy`, `cooldown`) and the shared refusal budget
  retries each caster-psycast pair per incident by the refusal's class. Combat, destination, healing and social psycasts
  are not used.
- `MaintainIdeoRoles` (`policy.RoleAssignments`): see
  [ideology contracts](ideology-contracts.md#role-assignment).
- `MaintainRituals` (`policy.PlanRituals`): see
  [ideology contracts](ideology-contracts.md#ritual-scheduling).
- `HoldGatherings` (`policy.PlanGathering`): see
  [mood control](mood-control.md#holding-a-party).
- `MaintainPermits` (`policy.NextPermit`): each review ranks every untaken permit of
  every titled colonist (`policy.RankPermits`): acting permits by worker class (aid and
  laborer calls, trade, drop-pod and shuttle access; psycast permits ahead of trade for
  a psycaster), takeable before blocked. Open while the best permit is takeable; commits
  one `choose_permit` `PawnSettingsIntent`. Using a permit is the `Ability` action's job.

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
affected services and needed wood acquisition are promoted to survival priority without
superseding combat or medical emergencies. This is coordination within existing concerns,
not an executor.

- Unusable cooking bench: one campfire method when none exists; no new bills on it.
  Replacement cooking searches at most eight nearby free cells with native previews.
  Starter heating is suppressed only by a usable campfire in the selected room.
- `RecoverDisasterServices` selects bounded refuel, structural repair and
  breakdown-repair `GiveJobIntent` jobs (Repair, FixBrokenDownBuilding, Refuel) through
  native WorkGivers, preserving work permissions, allowed areas, forbidden supplies,
  reservations and player-forced jobs; Hands previews again at dispatch.
  `service_recovered` needs fresh health, breakdown or fuel evidence; missing targets and
  interrupted labor never count. Definitions without hit points (sleeping and butcher
  spots) need no repair; unknown definitions keep damage risk. A destroyed building
  stays a deficit until accepted rebuilding work. Power recovery needs actual service
  from enabled consumers, excluding player-switched-off loads and generators. Solar
  flares defer generation changes while the cooking fallback is available.
- Toxic fallout: `RecoverDisasterServices` can move a colonist restricted outside every
  roof into an existing wholly roofed, reachable allowed area via an allowed-area
  `WorkSettingsIntent`, re-derived each 600-tick window (no native lease). Areas restrict
  work destinations; they do not make paths safe. Go never assigns an unroofed area or
  clears a restriction while fallout lasts; native reports no hazard veto. No refuge produces a blocker. Outdoor
  acquisition and field expansion pause during the hazard and resume after expiry.

### Shared compilation

Methods compile small batches of semantic construction/zone/native actions. Starter
templates rank nearby legal shelter sites, then use bounded native previews.

**Farm sites.** Starter farms and expansion share one site score: each square patch (4x4
down to 2x2) is rewarded by the crop's fertility-adjusted nutrition rate and charged
(as fractions of a normal cell's daily output) for walking from the anchor, hauling to
storage and fragmentation. A patch adjoining a controller-created zone of the same crop
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

**Open work exemptions.** Open hunting, foraging or butcher-spot build under
`EnsureFoodSupply` does not block a field batch, and a sown field does not block
acquisition. A spot waits only for a pending spot, a bill only for an open bill. A second
field batch waits for the first to resolve.

**Site types** (`policy.PlanSiteType`, over the planning window `ColonyProjection.Cells`
and `ColonyProjection.Environment` / `policy.ControlledEnvironment`, observed only and
unknown when withheld; see [go-clock-recovery](go-clock-recovery.md)):

| Site type | Meaning |
| --- | --- |
| outdoor | Open-air growing zone. |
| greenhouse-reuse | Roofed soil a running lamp already lights. |
| greenhouse-new | Roofed indoor soil plus one lamp placed to cover the most soil. |
| hydroponics | New basins on lit roofed floor, for crops with the `Hydroponic` sow tag. |
| dark-room | Unlit roofed indoor soil for a zero-glow crop. |

All use the same net-nutrition-per-needed-cell score, charging construction per 100
steel-equivalent and power per added kW against network day headroom (lamps) or
night/calm-night headroom (basins, heaters), plus a heater per room or outdoors below
6C (`policy.DefaultSiteTypeWeights`). A kind lacking power, heater, infrastructure or
crop compatibility stays in the candidate list with its reason; an unknown environment
leaves only outdoor. Controlled kinds ignore the outdoor season (native does not refuse a zone for
season, fertility or roof; those decide usefulness and stay Go crop and cell choice). Candidates are enacted in score order, at most
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
unissued construction commitments apply only during a supervised clock lease.

Dispatch ingests buffered and fresh native clock events before using a captured load
token/tick and again after preparation, so a busy writer cannot defer a known player
hold or danger event past an old order. External holds are recorded as clock holds
independent of observation and review revisions; there is no player-direction counter.

### Coupled orders

Every routine order dispatches under the running clock window; order count never stops
the clock. A plan dependency is coupled (`domain.ActionDependency.Coupled`) exactly when
the later order is written against the earlier one's result (an id it produced, a
position it reached). Ordering-only dependencies are not coupled, and no routine planner
emits a coupled dependency today. When a coupled prerequisite completes under a running
window the step reports the ready orders (`ClockSchedulerResult.Coupled`, `CoupledOrders`)
and plans them live at once; the window runs on. Staleness is refused natively via the
CAS evidence admission carries, and the planner prepares again.

## Native execution

New growing zones validate crop identity and pollution compatibility before registration
and configure the crop in the same native operation. Hands records intent before
writes, retains partial progress and verifies outcomes. Routine execution yields after
12 operations. Nothing dispatches while paused; player submissions run under the root
plan only while the bot is running.

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
The wood latch (`WoodMin`, `WoodTarget`) is a MaintainResource floor: it enters the Round's
[resource supply plan](../architecture/space-and-resources.md#resource-demand-and-acquisition-scoring)
as the wood deficit beside every other floor and is served by whichever chop, harvest or bill
the plan opens, never by a wood-only path.
Reserve, food-latch thresholds and wood thresholds are seasonal: the colony read carries
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
| Issued but never answered (controller-side timeout) | `ReceiptUnknown`, reconciled via `receipts_lookup`. The native ledger admits an attempt on the main thread before any effect and serves lookups there, so a lookup under the attempt's own load finding no entry proves it was dropped: no-effect evidence, action returns to `Pending`. |
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
state: `safe` true or false (a Danger.None path avoiding predators by the request's `hunt_predator_margin_cells` (Go sends `policy.HuntPredatorMarginCells`, 25) and an ordinary
death action, `RouteSafe`), or `skipped` once the frame's `hunt_route_budget_ms` (Go sets
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
(`fogged`, `not_safe_prey`, `no_butcher_bill`, `no_colonist`, `no_hunter` with each
colonist's `downed`, `mental_state`, `hunting_inactive`, `no_hunting_weapon`, `ranged_blocking_shield`, `too_far`,
`no_safe_route` (evaluated, unsafe), `route_skipped` (budget spent) or `route_unevaluated` (no verdict)).
The 100-cell reach and the plant cutter's former 50-cell reach are no longer native filters:
the 100 cells live only in the gate above, and a plant needs only a colonist with Plant Cutting
enabled who can reach it. Selection designates as many prey as the nutrition gap (deficit less pending nutrition) needs, meatiest safe prey first, bounded by the budget; an open hunt does not block the next hunt method. Supervised play pauses when an
active hunt loses its route.

Acquisition reports revenge chance, same-race herd size within the request's `herd_radius`
(Go sets `bridge.HerdRadius`, 25; the prey included; absent radius leaves `herd_size` unset,
unknown, never 0, and the bridge refuses a hunt row without it), melee eligibility and downed state; Go derives the longest weapon range among the
eligible hunters from the census, and the animal's leather and butcher products from its
race row. Food selection keeps forage priority, then downed animals, then lower revenge
chance times herd size.

One Hunt candidate stands per animal or prey group (`policy.HuntCandidates`), with a mode:
`lone` (a designation; id the animal's) or `formation` (id `squad:<first prey>`, run as a
`HuntRequest` of drafted gunners). A formation is required for prey that retaliates
(a predator, or revenge chance above 0.2) and for a group the clustering marks worth a
squad (12-cell linkage, three or more standing animals); every other animal is lone. The
formation takes every eligible gunner, at least three; no upper size is enforced.
A gunner is a colonist whose primary weapon hunts (`WeaponDef.Hunts`: ranged, not
explosive, not incendiary); the candidate and `huntFormation` share that predicate, and the
three-gunner minimum applies to formation prey only (a lone bow hunter is never gated).
A formation candidate is `Designated` once the plan opens its prey (`HuntRequest`, which
raises the hunt origin; `policy.HuntAdmission`, in memory) and, like every hunt channel,
`Delivering` from the first credited ledger KILL ([hunting](../architecture/supply-model.md#hunting));
Delivering keeps the hunt open while its fight runs, and stall and timeout handling stay
on `HuntProgress`. Every hunt channel is a finite source (`StockCap`, the nutrition of the
animals in reach) whose rate is capped by the hunters' kill throughput.
Below three gunners a formation is a Hold with a `needs_gunners` term (the gunners it
lacks, plus the `held_nutrition_per_day` it would deliver) and no yield. A candidate yields
the meat nutrition and the animal's leather and butcher products; labor is charged once.
Hunt candidates cap exposure at one for Revenge risk and estimate pursuit as
7500 / (1 + range / 25) pawn ticks per animal (2500 for downed prey, halved for sleeping
prey, scaled up for bad weather on a formation): planning estimates, not proof of a kill.
Incendiary weapons are excluded; already-dead fresh corpses stay pending butcher
material.

## Pest clearance

A recognised pest is a wild animal hunted for what it destroys
(`NativeHuntAcquisition.PestDefinitions` and `policy.PestDefinition` name the same
set, today `Alphabeaver`). Pests arrive factionless, so no emergency census answers them.

- `ClearPests` opens at foothold priority (2) with deficit one while the known
  wild-animal census counts a pest anywhere; recovers at none; an unknown census neither
  opens nor recovers it.
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

- Turret budget from observed raid points: unknown or below 300 allows two, below 800
  four, else six. Power, stock, spacing and lines of fire still gate placement; armed
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
combat view and retained fight memory. Go owns tactics; native owns job legality,
pathing and weapon execution. See [combat responsibilities](../architecture/combat-game-ai.md)
and [hazard bounds](../architecture/hazard-detection-bounds.md).

The fight retains its roster and acknowledged threats. New threats, changed
authority and severe injury still require current evidence. Stationary ranged
holders may use native `Wait_Combat` targeting; specialized roles keep explicit
orders. Combat extensions below cover drugs, hunting, permits and psycasts.

Combat batches currently dispatch directly through the bridge; routing the exact
batch through journaled Hands is tracked in
[#2502](https://github.com/davidarcher/rimgovernor/issues/2502).

### Shrine breach readiness

Breaching a sealed ancient shrine releases its guards at once, so
`policy.ShrineBreachReadiness` judges the gate first. It is a decision, never an order;
`ColonyStatus` reads it for `/api/player/colony` and the concern re-reads before drafting.
Hold reasons, in order: `not_sealed`, `no_breach_wall`, `emergency_active`,
`squad_too_small` (fewer than two eligible armed colonists; one under Peaceful),
`no_ranged` (no ranged weapon of 20+ cells), `no_traps` (fewer than three built spike
traps within 12 cells of the wall's outside cell; none under Peaceful), `threat_unknown`,
`threat_too_high` (raid points over 300 for a squad of two, 500 for three, 800 for four+;
Peaceful ignores points). The wall is the deconstructible perimeter wall nearest the
colony centre; the squad is every eligible defender (`SelectSquadDefense`'s rules plus
armed), shooters first. The storyteller is not observed, so Peaceful softening stays off.
The status read fetches combat pawns, the emergency census and a 27x27 defense-site
window only while a sealed shrine shows a breach wall.

### Ancient shrine breach concern

`ClearAncientShrine` (the `shrine` family) owes work on every shrine whose room touches
Home that is still sealed, or open with a guard seen standing. The review journals one
`ShrineHolds` row per shrine: the readiness reason, `ready` with the chosen `wall`,
`guards_alive` once the wall is down, or `readiness_unknown` under a native without the
readiness reads. Repairs precede the breach; the breach precedes `ClearHomeObstructions`
(a shrine wall is `ancient_danger` to the clearance census).

The planner re-judges readiness and admits one method for the first ready shrine: an
`OwnedDraft` per drafted defender, a `Movement` to a standing cell behind the trap line
(at least five cells straight out from the wall's outside cell, within trap radius,
never a trap cell; no cell means stand in place), then a breach `Deconstruction` of the
wall depending on every draft and move. One colonist is always left undrafted for the
deconstruct job. The deconstruction is inspected against the shrine census and eligible
only while sealed. The wall falling ends the method: the undraft sweep undrafts the squad
and `ActiveCombat` answers the guards, with the concern holding `guards_alive` until they
are dead or downed. A refused breach follows the shared refusal budget per wall; Stop and Manual leave the squad
drafted. Ranged breaching is not composed.

Filled caskets stay sealed unless the opening gate holds. `policy.CasketDecisionUnder`
names each casket's `ShrineHolds` row: `shrine_sealed` / `guards_alive` while the breach
is owed, `leave_sealed` (holds anything with no shrine decision), the gate's hold reason,
`open`, `claimed` (already the player's), `claim` (empty, unowned). An open, guard-free
shrine touching Home with a `claim` casket is still a target: before any readiness read
the planner admits one method of `claim_building` actions (`BuildingPatchIntent` claim,
one per casket, one-shot CAS write, no pawn or window). `ClearAncientShrine` never
designates a casket; a casket under 20% hit points explodes, so census hit points are a
safety reading only. The deficit recovers once every casket is filled or the player's.

Opening gate (`policy.ShrineOpenReadiness`, judged each review) opens only when all hold:
a melee lock of colonists at health >= 0.8 covers every filled casket (`lock_understaffed`,
`open_lock_injured`), one armed ranged backup (`open_no_ranged_backup`), a prisoner bed in
`JoinerCapacity` (`open_no_prisoner_bed`), medicine per casket (`open_no_medicine`), a
doctor (`open_no_doctor`), no downed or bleeding colonist (`open_emergency`), no hostile
threat (`open_combat_active`), and raid points under the breach ceiling for lock plus
backup (`open_threat_unknown`, `open_threat_too_high`). A held gate leaves the shrine off
the targets; the concern finishes with caskets sealed and re-arms next review.

Once the gate holds, an open, guard-free shrine with a filled casket is a target after
claims: `policy.ShrineMeleeLock` staffs one violence-capable, non-ranged colonist per
filled casket (healthiest first) or holds `lock_understaffed`. The method is an owned
draft and a move to the casket's interaction cell per locker, then one `open_casket` by
the opener (the lowest casket's locker) depending on all of them. Opening one casket
ejects the group at the lockers' feet. The worker releases the drafts once the opening
resolves and `ActiveCombat` and custody planners take over, the concern holding
`guards_alive` while a hostile stands. The census then lists each released humanlike or
corpse as an occupant; `policy.OccupantDecision` names its row: `bury` (MaintainBurial),
`fight` (ActiveCombat), `capture` (downed hostile or standing neutral while
`JoinerCapacity` has room), `release`, `captured`. Neutral arrest uses the Capture
variant in [population contracts](population-contracts.md).

## Autonomous supply safety

`ManageSupplySafety` owns both directions of the forbid flag for visible, player-owned or
unowned haulable items, including later event drops. A complete `event_loot` census
reports each item's identity, cell, forbid flag and native hauling safety. There is no
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
This is a current observation, not a prediction. The planner re-reads every review and
cancels a pending action the fresh census no longer supports. The native boundary applies
Allow and Forbid with no safety rule of its own, because the flag has other owners (the
reserve food and frozen corpses `MaintainFoodStorage` holds).

Release is selective. A pawn death drops forbidden items and the census releases them
one stack at a time. The review keeps a safe forbidden stack forbidden, recorded as a
hold, when its def is one a native spawner forbids on purpose
(`CompProperties_Spawner.spawnForbidden`, e.g. insect jelly) or it lies within
`ThreatReachCells` of a danger seed (live discovered hostile pawn or any hostile building,
passive hives included; `policy.DangerSeeds`). The same seeds feed the hauler gate:
while a hostile is live and for an hour after, `MaintainShelter` restricts haulers to the
`NoDanger` allowed area (home minus killbox and cells near accumulated seeds).

Unsafe items are forbidden before safe ones are allowed, in batches of eight. Forbid
changes no pawn orders and may run during an emergency; Allow keeps the emergency gate.
A changed census cancels stale undispatched proposals; later reviews may reverse a
decision. A designation receipt proves the flag only; native storage observations prove
hauling.

### Remote loot and resource reach

Allow is additionally a reach and demand filter; Forbid is not. A safe forbidden stack on
a cell of the derived colony extent is allowed as above. Outside the extent it is
allowed only when `FilterResourceReach` admits its cell at the current
[reach stage](upkeep-contracts.md) and it is
ranked by the recovery queue (`policy.RankRecovery` over the usable stock census);
otherwise it stays forbidden and is recorded under
`EventLoot.Held` with an [explicit hold](#remote-work-holds-and-resume)
(`threat_present`, `urgent_competing_work`, `missing_storage`), or the reach reason
(`outside_base:insufficient_defense`, `outside_near:...`). The routines API reports these as
`lootHolds`. Beside each item's stack count, safe route length and storage headroom the
census supplies `free_haulers` (free colonists with Hauling active) and
`storyteller_quiet` (zero threat scale or no incident generators). Unknown readiness or
demand holds remote stacks; it never widens reach. Reach changes no Home cell.

### Remote work holds and resume

Remote loot, ruin salvage and surface mining share one hold vocabulary
(`policy.RemoteHoldReason`), reported on `EventLoot.Held`, held `RecoveryQueue` entries and the
resource planner's log:

| Reason | Meaning |
| --- | --- |
| `threat_present` | Known hostile on the map (reported ahead of the route verdict it causes). |
| `urgent_competing_work` | Urgent patient or disrupting disaster (`policy.UrgentWorkCompeting`); outranks every demand priority in `PlanSupply`. |
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
tolerance are Go policy (carry planning excludes children and risky chemicals)
and vanilla's own refusal of the Ingest job surfaces as `native_refused`. The fight's `Dosed` memory
limits each pawn to one attempted dose, including refused attempts; it does not
assert ingestion. Unavailable inventory produces neither an order nor an attempt
mark. Policy and runtime tests cover selection; `combatlab/drugs` covers the
native inventory projection and `combatlab/combat_drug` covers ingestion.

**Squad hunt.** A squad hunt is the `ActiveCombat` incident's hunt origin: while no
hostile stands and the food plan opens a formation `Hunt` candidate (a group of three or
more wild animals, or any animal a lone hunter must not designate, with three colonists
wielding a hunting weapon (`WeaponDef.Hunts`, the one predicate for the candidate's gunner count and the formation) able to form the squad; with fewer it Holds as `needs_gunners`), the review asserts the deficit with the channel's
prey as the occurrence's payload (`store.HuntPrey`). The fight runs through
`admitFight` with `CombatView.Hunt` set and the prey as its threats; the frame's
detail rows cover the open hunt census rows. A hostile in the frame, or the plan no
longer opening the hunt, ends the origin. An open hunt fight runs in combat windows
with the live prey as the window's acknowledged ids (`ClockWindowFacts.HuntPrey`),
so the combat budget backstop wakes its next decision; no hostile exists to make
the window a combat one otherwise.

**Royal permits** (`combat_permit.go`). An outmatched fight calls the permits its
colonists hold. With the royalty read known (`CombatView.Royalty`, the review's
`RoundsFacts.Royalty`), a held acting aid or strike permit that is off cooldown and
affordable (favor at least its cost) yields a `permit_call` order, once per holder
and permit per fight (`CombatMemory.Permitted`). Aid lands on the defender nearest
the squad's centre; a strike lands on the densest hostile clump no colonist stands
within the mortar safe radius of. Unread favor, cooldown or royalty facts hold the
call. The order is no `combat.orders` entry: the defense planner commits it as an
`ability` action (permit source, cell target) on its own incident method, and the
fight's stops wait while it is open. The admission stop makes no call.

**Psycasts** (`combat_cast.go`). With the royalty read known and a live hostile,
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
