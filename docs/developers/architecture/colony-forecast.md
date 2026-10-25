# ColonyForecast

Supported care and demand-driven delivery participate in the normal coupled
portfolio through the same composition, forecast and Method binding. See the
[support-work contract](../contracts/support-work.md) for native consequence
limits, exact execution ownership and the replacement acceptance cases.

[Architecture](overview.md) · [Forecast facts](../contracts/forecast-contracts.md)

`ForecastCoupled.ForecastCoupledScenarioOf` is the pure owner of a commitment set's
forecast over the observed facts. It returns a `ColonyForecastScenario`; it does not issue
orders or certify native legality. The inventory walk accounts for supplied timed changes.
The coupled component below composes food, needs, construction and
clothing consequences on one timeline. [Normal portfolio selection](portfolio-selection.md)
uses these consequences before binding through the existing journal and Hands.

Forecasting is part of choosing and advancing a Method in the
[Concern/Method/action model](plans-and-hands.md#method-selection-and-binding).
Family policy generates alternatives and binds the selected outcome; shared
comparison evaluates them. Forecast results are derived evidence, not another
admission authority, Method lifecycle or stock owner. The
[live policy stage map](#live-activity-policy-stages) describes the current caller
and its compiler-enforced boundaries.

## Input and accounting

This section describes the conditional inventory accounting each coupled schedule is walked against.
The coupled trajectory below additionally validates task feasibility and yields.

`ColonyForecastRequest` identifies the observed tick, an
explicit horizon in game days, current facts and existing commitments.
`ColonyForecastFactsOf` assembles the observed stock, food, medicine and item facts from
`ReviewFacts`. Coupled review inputs feed normal portfolio selection, the only
selector of a controlled pawn's ordinary work: the voluntary-control cutover
(#3135) retired the competing routine writers, so no timetable or adviser
chooses a competing job ([voluntary control](../contracts/voluntary-control.md)).

A commitment identifies a Method's supplied inventory effects by stable ID.
Each effect has an exact resource, relative day and signed units, each numeric
effect/time supporting unknown. Positive units are a delivery; negative units
are a consumption. This is a conditional accounting model: delivery timing is
supplied, not a prediction of native worker completion. Unsupported effects
travel with the commitment as findings.

Duplicate IDs, negative times and nonfinite known quantities are rejected.

Every alternative begins at the observed resource census. Known empty means
zero inventory; unknown does not. Pending delivery is never added to observed
stock. Rows are deterministic by resource, time and commitment ID. At equal
times consumption precedes delivery so output cannot pay for its own inputs.
The day-zero point is the observation; additional day-zero events are subsequent
proposed effects.

Negative balances are accounting deficits, not usable stock. A later delivery
does not erase the earlier shortfall finding. Subsequent effects remain
conditional on the caller's supplied plan: this foundation does not postpone
jobs or stop dependent output when a prerequisite fails. Consumers must examine
findings and unsupported effects, not interpret a positive final balance as
proof that a plan can execute.

Unknown effect timing makes future balances for its resource unknown; the
observed point stays intact. Unknown quantity makes balances unknown from its
known time onward. Effects beyond the horizon are reported and do not enter
the trajectory; their benefit is unassessed, not intrinsically worthless.
No daily rates, target floors or diagnostic stock needs are silently converted
to consumption events. `ResourceDemandOf` remains the shared target owner;
maximum overlapping stock floors are not additive physical consumption.

For example, start with 10 units and a build consuming 15 on day 1. A delivery
of 10 on day 2 produces a day-1 deficit of 5. Replacing only that delivery with
one on day 0.5 removes the inventory deficit while leaving observed stock at
10 in both. It says nothing yet about which pawn can deliver it.
`ColonyForecastTests` exercises the inventory walk and the uncertainty boundaries.

## Model coverage

The five-day `MaintenanceHorizonDays` is independent of the requested plan horizon. The
constant serves the existing supply targets. Proposed inventory changes do not rewrite observations.

| Domain | Existing runway or demand and meaning | Coupled coverage or remaining boundary |
| --- | --- | --- |
| Inventory | Timed conditional balances, reported as shortfall findings | Timed task inputs/yields in the coupled trajectory |
| Food | Existing eligibility/rot-aware stock runway; no future delivery credit | Bounded acquisition, batches, delivery and recurring human ingestion |
| Power | Current network reserves at observed drain (`PowerMethod`) | Current-network heater supply; future generation changes unassessed |
| Temperature | Current sleeping extremes; current breach held over maintenance horizon | Conditional planned-room heat balance; future weather unassessed |
| Defense | Current usable turret DPS and explicit gaps | Hunt rosters priced from the qualified [encounter estimates](../contracts/encounter-prediction.md); raid outcomes unassessed |
| Construction | Exact-resource owed and admitted needs in units; no fabricated ETA or interchangeable stone pools | Shared construction/material/needs timeline |
| Fuel | Existing consumer burn and refill-demand diagnostic | Future scheduled supply/consumption |
| Drugs/materials/medicine | Existing realized-use runway diagnostics | Future timed activity effects |
| Animal feed | Existing herd/pasture/feed runway diagnostic | Future animal feeding behavior remains outside the coupled human-needs model |
| Pawn time, mood and needs | Explicitly unsupported by this component | Current-rate needs and future capacity; calibration through #3125 |
| Infrastructure and dependencies | Explicitly unsupported; supplied effects are conditional | Supported planned-room prerequisites; general infrastructure unassessed |

No row is a calibrated whole-colony trajectory. A schedule's findings
make the omitted effects visible even when all inventory balances are
positive. Preferences and safeguards stay separate from physical forecasts;
there is no learned weight or common scalar utility in this owner.

## Ownership and production migration

Forecasts are derived in memory from observed facts and existing saved intent.
They add no save, database table, telemetry stream or native writer. Runtime
gathers facts and sequences calls; all accounting remains pure policy.

The former C# `ProjectForward` and `ForwardInputsOf` had no C# call sites at
the audit; their types/functions are replaced rather than kept as wrappers.
The observation collector's additional facts now use `ForecastObserved`.
Independent maintenance functions remain because production supply consumes
them. No second projector exists.

Activity and needs forecasts compose in the coupled trajectory below.
Normal production compares and binds Method bundles through existing Colony review/Hands.
The supported food, needs, planned-room, clothing and support activity callers
use the shared portfolio path, and it is the only selector: no family keeps a
private winner, and nothing shadows the comparison.

## Coupled food, needs and production trajectory

`ForecastCoupledScenarioOf` evaluates a commitment set against one world, observation tick and horizon. Normal Colony review
assembles `CoupledAlternatives` from the native forecast census, existing shared
work-resource view, needs and saved planned-room intent. `CoupledForecastRequestOf`
requires these ticks to agree; a mixed frame cannot certify a consequence.
The supported horizon is at most one game day, matching the current-rate needs
model. This extends the existing forecast owner; it adds no scheduler, durable
graph, material ledger or order writer.

Family generation retains food batch, construction and clothing alternatives
with exact task bindings. Composition uses physical cell identities and directed
native route estimates so a pawn's next approach starts where its previous task
ended. Approach travel is separate from service travel such as carrying between
source and destination. Missing cross-cell routes remain unknown. Selected
commitment completions pull in their prerequisite closure; the existing bounded
activity search supplies complete schedules with shared pawn and bench occupancy.
Generation also proposes recovery-before-production dependencies: a comparator
cannot discover this combination if generation never supplies it.

Each evaluated schedule has its own timed stock findings, needs results,
shelter trajectory, thermal exposure and feasibility findings. These dimensions
retain their units: resource counts, need levels and deficit/exposure durations,
memory-adjusted mood target, available work intervals, temperatures and degree-ticks.
They are not summed into one price for food, mood, injury or development. An
empty selected set retains the baseline; missing capacity for other commitments
is an explicit gap. The same component evaluates normal production portfolios;
family providers generate alternatives and bind only the shared selected result.

### Conservation and recovery

Task inputs are charged at service start and yields become available at successful
completion. Failed or unknown prerequisites cannot finance dependent output.
Recipe batches contain individual iterations with their own ingredient debit,
bench occupancy and complete product yields. A harvest yields its observed source
quantity; one carrier's load does not redefine that yield. Supply and delivery
move physical lots rather than creating stock. Current food lots and future
production stay distinct, including their locations and freshness clocks.

Shared material availability reuses `WorkResourceView` and its existing promise,
construction and native-reservation evidence. A selected construction supply or
bill iteration receives only its exact owned input credit, capped by what that
selection consumes. Unrelated native holds remain protected; removing a proposed
commitment does not prove that an existing journal claim has been released.
Timed consumption from an existing non-activity commitment enters the same event timeline; it cannot be hidden until a final inventory report. Only its exact unlinked owner claim receives capped reservation credit. Unbound prospective output and ambiguous source consumption remain resource/lot-local unknowns.
Resource availability and local lot availability must both permit the task.
A positive final colony balance cannot pay for a missing earlier input or food
at a different ingestion location.

Eating requires eligible, delivered, unexpired food and sufficient remaining lot
quantity. Recurring ingestion alternatives spend that same stock; hunger continues
falling between meals. Need recovery occurs at its modeled service time, and the
resulting physiological work windows must actually contain later productive work.
If they do not, that work and its dependent yields are refused and consequences
are recomputed. Freed idle capacity alone never produces goods. Existing memory
expiry remains modeled; ingredient- and instance-dependent thoughts from future
food remain unassessed. The needs model's current-rate and mood limitations below
still apply.

### Planned shelter and occupancy

The concrete shelter alternative joins saved planned-room geometry to native
shell, roof, standing construction and heat-source facts (electric heaters and,
since #3673, fueled heat pushers such as a campfire). Missing shell or
source work uses the same construction prerequisites and shared materials.
Protection starts only after enclosure, roof and heat-source prerequisites
complete, followed by room warmup. A placed heater or roof alone earns no thermal
benefit.

A planned shelter is the planned room of the same id (`planned/room{R}`), so its
unbuilt shell and roof are that room's [planned sites](#planned-rooms) (#3277), not
a second construction path. Each open ring cell resolves in order:

- a solid cell needs nothing; a door counts as solid unless it is held open, since
  the door a pawn passes through shuts behind it (#3682);
- a cell with a placed native site requires that site's finish, as before;
- a cell inside a generated planned wall or door site requires one shared
  `CompletedPhysicalSite` demand per kind at `planned/room{R}`, met by the finish of
  whichever stuff's site the selection builds;
- a cell inside a planned site that was not generated (unvalued, infeasible or
  truncated) notes `planned_shell_unvalued`;
- any other open cell notes `unmodeled_shell_work`.

The roof follows the same rule: a generated `planned/room{R}/roof` site covers its
cells as one shared demand, and no per-cell roof work is added there. The per-cell
native roof labor (designated Build Roof cells only) remains for observed rooms and
for planned rooms without a generated roof site. Either way the room's bounds stay
within 10 cells. The composer shares the planned operations with the planned-room
roots, so they are never duplicated. A planned roof over a natively placed ring has
no ordering edge on that placed shell; this is a known approximation.

An unstarted room with nothing placed has no cells in the coupled capture, so Colony review
read it apart (#3323, the
[planned shelter thermal read](../contracts/forecast-contracts.md#planned-shelter-thermal-read)).
`PlannedThermalRequestOf` asks for up to four enclosed rectangular shelters that still
owe a planned site, with sides of at most 10 cells, lowest id first. It also asks for
each room's planned Build site of a catalog heater definition: the electric
`RoomFurniture.Heater` or one of `RoomFurniture.FueledHeaters` (#3673). `WithPlannedThermal`
then joins the answer into the coupled thermal read. The coupled capture keeps every
cell and source it has, and the planned rows fill only the gaps. The planned read
comes from a later frame of the same round. A planned heater is a source whose
`Target` is its planned site. The shelter requires that site's finish as one more
`heater/<site>` demand per planned source, and notes `planned_heat_source_unvalued` when the
site was not generated. Without the read, or past its bounds, the room stays
explicitly unknown (`missing_shelter_geometry` or `unknown_heat_source`). A planned
door's exchange is not read: a ring cell with nothing placed has no door definition.
This is the same approximation as a planned door beside a placed heater.

The supported estimate is a closed rectangular room with thin roof, under current
outdoor temperature and current network supply. It uses observed native heat
output and wall/roof/door exchange in a spatial-mean projection, limited to the
electric heater's full-efficiency regime at or below 20 C. Additional demand from
selected heaters shares each network's observed surplus. The supply assumption
holds that current network state constant for the horizon; future weather,
generator fuel changes, outages and other heat-source mechanics are not predicted.
A fueled source's supply is unlimited instead: its fuel is charged at material value
in the Investment term from the shell's completion to the horizon
([forecast value](forecast-value.md)), so refuelling is assumed. Every source in the
room heats it (#3679; a torch lamp beside a heater counts): each pushes its own heat
while the room is below its own target, as `CompHeatPusher` does, so between
consecutive targets the projection sums the sources still pushing, and the room holds
at a target it cannot rise past. Each source carries its own supply, network draw,
completion requirement and fuel charge; one unknown source leaves the room unknown.
Missing geometry, unsupported roofing or insufficiently known energy supply keeps
the affected shelter estimate unknown. Per-cell roof labor is conservative because
native roof jobs may cover neighboring cells.

Until its prerequisites finish, a room behind a closed ring is projected as it stands
(#3534): from its observed mean temperature, heated by its complete sources, with
`RoomTempTracker`'s coverage terms as its roof exchange. Each open cell exchanges at
the native no-roof rate (`NoRoofEqualizeRate`, 0.0007 a tick, against 0.00005 under
thin roof), so an open-roof room trends to outdoors at the game's rate. That standing
projection is an untouched room's baseline, the room before its shell completes, and
the temperature the finished projection starts from. A room with an open or unread
ring, or with an unknown standing estimate, keeps the earlier constants: the observed
cell temperature for the baseline, outdoors before completion. Heater duty and outage
margin are not yet priced.

A room's capability is separate from a pawn's exposure. Only modeled stationary
work/need occupancy at observed physical cells receives the corresponding ambient
or completed-room temperature. An outdoor worker gains no protection merely
because a shelter finishes elsewhere. Approach routes, moving services and idle
intervals lack a temperature trace and remain unassessed. Degree-ticks are exposure,
not injury probability, hypothermia onset or a physiological capacity model.
Similarly, clothing recipe and delivery completion supplies a garment; it does
not prove that a pawn wears it or gains physiological protection. The garment's
comfort shift (#3330) applies to its wearer from the delivery's completion as a
forecast assumption, which prices temperature mood only.

### Roof and room consequences

A planned room changes more than temperature once its ring and roof stand (#3339,
`ForecastRoofConsequences.cs`). Colony review ask native, in the same construction activity
read as the thermal read, for each unplaced planned room closed in each ring stuff
(`PlannedRoomPredictionRequestOf`, the
[room prediction read](../contracts/forecast-contracts.md#planned-room-prediction-read)).
`PlannedClosuresOf` joins each answer to the room's ring (wall and door sites of that
stuff) and roof sites. In a schedule a room closes when the last of its ring sites
finishes, earliest stuff first, and is roofed when its roof site (or the planned
shelter's roof cells) has finished. These physical closures drive the per-tick
reads inside the schedule only. A room's credit runs instead from its service day
(#3796, epic #3650 P2, `PlannedRoomCredits.ServiceDays`, replacing #3744's ring
projection): the day its one reconcile owes nothing. Its owed items, per stuff, are
its planned sites (ring, roof, furniture, floors), its placed ring pieces, and the
placed floor blueprints and removals on its interior or ring cells
(`Context.RoomItems`; a shared-wall cell's are both rooms'), with the owed items of each
connector serving it (#3815: an airlock its parent, a circulation corridor each room a
layout door joins it to, `Context.Connectors`; every way of the connector's, the room
keeping its own stuff), all one job, less its blocked pieces (#3827, `Context.BlockedItems`:
the round's read of the one predicate `PlannedRoomDone` uses, `BuildingPlanner.DefinitionBlockedOf`,
read evidence only, #3819), which delay nothing; a planned site of a blocked piece is never
generated (#3838, below). A room whose
every item of one stuff finished in the schedule serves at the last finish, earliest
stuff first. Otherwise it is owing when it is open (`Context.OpenRooms`: observed started, from
`PlannedRoomStartedOf`, and not done, `PlannedRoomDone` as its latest reconcile found,
`ColonyReview.DoneRooms`; #3754) or the schedule finished one of its items. Its remaining
work is the owed work (`Context.OwedWork`, by item id) of its unfinished items (a
planned site's least finish option (its `Finishes` ticks) or a roof's `RoofCellWorkTicks` times
its cells, plus the least option of each of its `PlannedSiteOperations.WorkGroups`,
deliveries and clearings; a placed item's least work over its bound tasks); the least
stuff wins, and a room with an item without an owed-work row is unassessed. The
builders (`Context.PlannedBuilders`: the needs census's free colonists capable of
Construction, `LayoutClusterRules.BuildersOf`; none, or their work facts unread, makes
every owing room unassessed) first spend the owed work the schedule finished, then the
open rooms, then the rooms the schedule opened, each group by its remaining work;
the k-th serves at the cumulative work over the builders (`RingClosure.ClosedAt`, the
estimate the bed merge sizes rooms with, #3748), never before the horizon. The credit
runs from the service day to tier 2's horizon (`RingClosure.CreditDays`), none at or
past it. Between those times a cell's state is the
mirrored `Room` formulas over the predicted cell and open-roof counts:
PsychologicallyOutdoors (300 open roof cells, or half open at the map edge) and
UsesOutdoorTemperature (a quarter open, or the map edge). Before closure the cell's
observed state holds.

- **Bed thoughts.** `ApplyBedThoughts` runs at each modeled rest's wake, as one
  `ForecastMemoryRenewal`: it ends every slept\_\* memory and gains SleptOutside when
  the bed's room is psychologically outdoors, and, for a non-Ascetic owner of a bed
  not for prisoners, SleptInBedroom or SleptInBarracks at the impressiveness stage.
  Role and stage come from the prediction when a room closed by the wake lists the bed,
  else from the bed's observed room. The renewal carries a chance (#3871): a sleep the
  pawn starts draws the onset from `Rand.Range(2500, 10000)`, so a sleep of `s` ticks
  gets the thoughts with chance (floor(s) - 2499) / 7500, none under 2500 and always
  from 10000. A sleep observed now continues the lie-down whose onset native reads
  (`NeedsForecastPawn.BedThoughts`: given already, due in some ticks, or none drawn).
  A pawn asleep now keeps that sleep only while the outcome sends it no order (#3882):
  an order interrupts the lie-down, and the forecast reads the binding's own rule for
  whom it orders at once (`CoordinatedActivityBinding.OrderedAtStart`, #3884), on the
  outcome as the review dispatches it (#3889): the outcome's commitments are its
  selection, and `ForecastIncumbentHold.Dispatched` (the context's `IncumbentHold`,
  the same step `ReviewCoupledExecution` takes) drops the incumbents' commitments,
  their tasks, their pawns' tasks and any task waiting on those. No one is ordered
  when nothing is left selected, else every pawn with a placed task but one whose
  first is a distant service left to vanilla. An outcome the review would start
  nothing of is refused, so the forecast never selects it: a dispatch with an unbound
  placed allocation (`dispatch_allocation_unbound`, `CoordinatedActivityBinding.Unbound`)
  and one that releases an incumbent the review cannot release
  (`incumbent_release_unavailable`: `ForecastObservedIncumbent.ReleasedBy` and
  `ForecastIncumbentHold.Release`, the review's own steps). Left
  alone it sleeps on (a rest
  from tick 0 at its `LyingIn` rate) until its rest reaches the wake threshold or the
  schedule has it leave for its first task: other work at its departure, a service at
  its planned start less travel, since no order holds it waiting there (its service
  then departs when the sleep ends); a hediff that blocks sleeping wakes it at
  once. A deathrest (#3883) runs to its own end instead, since its LayDown has
  `lookForOtherJobs` false and `ShouldWakeUp` is false while deathresting: `Wakes` ends
  at its remaining `MinDeathrestTicks`, `Held` (autoWake off or wake already notified)
  and `Paused` hold to the horizon. A `Sunlit` bed wakes it at that 150-tick check
  unless the outcome's planned roof (`CellStateAt`) covers every open bed cell first;
  weather, lightning and sky-affecting things are `deathrest_sunlight_weather_not_modelled`,
  an `Unlit` scan that ends before the wake is a `RequiredFactException`, and the
  DeathrestChamber thought that starts at the wake is
  `deathrest_chamber_thought_at_wake_not_modelled`. Woken at tick 0, its
  `FinalizeLayingJob` gives the bed thoughts again when they were given (from the
  lie-down's `LyingIn` place and the owned bed's room), and the mood timeline has it
  awake from then. A continuing sleep off the rest targets (on the ground) keeps its
  observed room (`sleep_room_held_at_observed_lie_down`).
  The timeline prices a renewal at chance p as its expected value: the memories it ends
  keep 1 - p of their weight and its gains carry p. SleptOnGround and the temperature
  sleep thoughts are held at their observed contribution
  (`sleep_ground_and_temperature_memories_constant`). A stage row, room role or
  observed thought the read lacks throws `RequiredFactException`; an unread room keeps
  the baseline with `sleep_room_thoughts_unread`.
- **Memory renewal.** The mood timeline ends an observed memory at the first renewal
  that names its def and runs each gained memory from its renewal to the next renewal
  of its def (or its duration). A renewal whose def is also a meal memory refuses with
  `unsupported_memory_renewal_or_stacking`. A meal memory of a stackLimit-1 def renews
  its def's earlier memory instead
  ([repeated and stacked memories](../contracts/forecast-contracts.md#repeated-and-stacked-memories-3598)).
- **Weather.** The weather thought (`Pawn_MindState`, every 120 ticks, asleep too)
  applies at stage 0 unroofed or, for a two-stage thought, stage 1 under a roof. Each
  joy or rest span takes the roof state at its start; the weather itself is held
  (`weather_held_constant`), and stage changes replace rather than stack.
- **Outdoors need.** `Need_Outdoors` is simulated over the awake joy and rest spans
  with the mirrored per-interval deltas (no, thin or thick roof; indoors or outdoors),
  frozen asleep and scaled in bed. Each category change from the observed one becomes
  a banded span of the NeedOutdoors stage offset less the observed contribution. A
  disabled need (PrefersIndoors) costs nothing. Spans with no known location, such as
  food, hold the level (`outdoors_need_constant_outside_known_location`).
- **Undergreview coordinator.** The trait's indoors, underground and outdoors thoughts follow the
  same spans' roof state as banded spans.
- **Deterioration.** A food lot with deterioration inputs loses hit points at its
  observed final rate (`SteadyEnvironmentEffects.FinalDeteriorationRate`): each tick
  visits `ceil(area * 0.0006)` cells and a visited stack loses one point with chance
  rate/36. A planned closure or roof over the lot rescales the rate by the
  environmental part's factor (unroofed and outdoors offsets, rain from 1 to 5,
  terrain, edifice protection). The lot is lost (`observed_lot_deteriorated`) when the
  expected loss reaches its hit points; an ingestion after that refuses
  (`food_deteriorated_before_ingestion`). Rain and the other stat parts are held
  (`deterioration_rain_and_other_stat_parts_held`); an unknown input notes
  `food_deterioration_unassessed`.
- **Bench speed.** A bench inside an unclosed planned room that the closed room would
  bring indoors (`StatPart_WorkTableOutdoors`, x0.8 outdoors) gets a closure-conditional
  copy of each single-completion task root that uses it, `{task}/closed/{room}/{stuff}`:
  the ring sites' finishes are prerequisites and the bench work takes 0.8 of the
  observed ticks. The root copy excludes the room's other-stuff roots, as the `/after/`
  variants do.

The location is known only at joy and rest targets; travel, work and food spans hold
the observed roof consequences. Bed thoughts land at the wake rather than at the
game's random onset during sleep; the onset only sets their chance.

### Bounds and evidence

The native census, family generation, recovery combinations and shared schedule
search are deterministic and bounded. Truncation exposes omitted candidates or
routes rather than certifying optimality. A schedule that cannot complete within
the horizon has unassessed later benefit, not zero value. Unknowns retain their
affected resource, task or consequence scope; they do not erase independently
known results.

`ForecastCoupledTests` uses ordinary review inputs to exercise conservation,
recovery-dependent production and changing food, labor, material and thermal
consequences. These are not timing calibration or observed dispatch: that
evidence is the nightly tier and the [colony review](../testing/colony-review.md)
season, reported on the owning epic; comparing each coupled prediction with the
game a day later is #3475.

## Bounded activity coordination proof

`CompareActivityPlans` is the pure activity scheduler under the coupled
trajectory (`ForecastCoupled`). It evaluates one
supplied outcome DAG, not colony demand or a second production scheduler. The [normal portfolio path](portfolio-selection.md) compares supported coupled activities through it and binds selected Methods. `ResourceDemandOf` remains the demand owner, and dispatch belongs to Hands.

A `ForecastActivityProblem` supplies activities, prerequisite IDs, start/end
locations, eligible pawn options, elapsed service durations, directed route
durations, a deadline and each pawn's `ForecastCapacityWindows`: `Read` windows when every capacity input was read, or
`Unread`, which plans no work for that pawn. Every activity is required
exactly once. Service may include pickup, loaded transit and delivery; those
stages must be included in the supplied duration and final location, not counted
again as approach travel. Durations are elapsed ticks, not native work units.
No activity ID or work-family name changes the algorithm. Each pawn shares one
serial occupancy timeline across all work types; different pawns can overlap.
Protected obligations bound availability, while optional baseline work supplies
an explicit displaced-labor rate. Tasks must fit wholly inside one free window; they cannot span a protected obligation. Unknown positions after protected time cannot reuse an earlier location.

The coupled portfolio search validates and indexes its observed routes once per
search, sharing that lookup across candidate schedules and capacity-recovery
retries. The existing search cache also owns the full activity index. Route and
pawn arrays are immutable during selection; replacing either array rebuilds and
revalidates the route lookup. Each candidate still validates its own tasks,
options and dependencies. Standalone scheduling calls validate fresh inputs,
and unknown routes remain unknown. No index persists into the next search.

The search enumerates dependency-ready tasks and eligible pawn assignments,
including self-service and delegation, with earliest and just-in-time approach
travel. Prerequisites gate service, not travel. Thus a builder can approach a
site while a carrier delivers, just as a retriever can approach before a
producer finishes. Joint combinations come from this enumeration; changing
weights cannot recover an omitted option. No squad template supplies them.
The serial list schedule does not insert work into earlier gaps, split tasks,
or enumerate arbitrary departure times. It is not a complete scheduling solver.

Each allocation exposes travel, waiting, service, displaced labor and expected
injury-recovery labor separately. Displaced labor is occupied time multiplied
by the supplied baseline labor rate; travel/work/wait are its decomposition,
not additional charges. Recovery is added once in the same labor-tick unit.
It is an explicit caller estimate, not a combat probability model, and does
not extend the pawn's occupancy or predict injury onset/needs. Such coupling
is outside this supplied-estimate activity proof. All participants' costs count. Finishing after the
supplied deadline or availability end refuses that combination. Zero is
valid; an unrouted walk, unread capacity (`unread_capacity`) or undeclared equipment excludes only the affected
branch with a finding. Option work, recovery and the deadline are plain numbers: a builder that cannot project an
option's work leaves the option out with its own finding (#3894).

Plans rank by total labor cost, completion time and a deterministic ordinal
schedule key. `ForecastActivityComparison.Alternatives` returns them cheapest first; the comparison selects nothing.
The caller decides: the coupled trajectory values each schedule against its consequences, so there is no
alternative labor budget here.

The iteration budget counts option visits (including unknown/refused options)
and departure trials. The default is 512, with a maximum of 4096 and at most
32 activities; these bound this small proof, not colony capacity. Exhaustion
sets `SearchTruncated` and a finding; any selection is best among evaluated
plans, never certified optimal. All distinct evaluated complete schedules are
returned, including more expensive and waiting alternatives. Ordinal ordering
makes input permutations deterministic even when truncated. Validation rejects
cycles, duplicate identities/options/routes, invalid estimates and overflow.

`ForecastActivitiesTests` proves choices change with supplied route distance,
carrying-trip duration, worker speed, helper obligations, deadline and recovery
cost, using hunting/retrieval and delivery/building through the same mechanism.
It also checks shared pawn occupancy, prerequisite joins, preparatory travel,
waiting costs, unknowns and bounded candidate generation. These are synthetic
policy scenarios: route durations are supplied rather than native observations;
carrying capacity and skill are encoded in supplied elapsed durations; opportunity
costs are assumptions. Just-in-time travel assumes
predictable prerequisite completion and divisible baseline work. The generic search
accounts for declared exclusive equipment below. Portable-tool transport and
interruptions remain unmodelled; coupled needs belong to the trajectory above and
dispatch to the Method journal and Hands. It establishes combination generation and
conditional valuation, not emergent expert play.


## Construction commitments and shared capacity

Construction work is generated by `ConstructionMethodCandidates` from the coupled census and compared in the coupled
trajectory on the same capacity and material frame as every other activity. The normal adapter observes supported
native estimates and exposes typed operations to the shared composer; missing estimates receive no distance-based or
zero-cost defaults. Standing journal claims protect current work, and selected alternatives use the existing
Method/Hands continuation path. See [normal portfolio selection](portfolio-selection.md) for supported scope and the
distinction between registered native cases and observed gameplay evidence.

The forecast no longer schedules a construction slice of its own (#3894): `ForecastConstructionContext`, its
per-commitment estimates and `Construction` results had no reader once the coupled trajectory took construction over,
so they were deleted with their tests rather than hardened.

A standing removal designation is construction work the same way (#3557, #3612,
#3613, #3614, #3615). The room reconcile stands a Deconstruct over an interior ruin
or a stale piece, an Uninstall over a piece it packs, a Haul over a stack it moves
out, a CutPlant over each plant of a Cut wave and a RemoveFloor over each cell of a
FloorOut wave, each claiming only the cells; under
voluntary control the designation gets no worker by itself. The coupled census
reads every such designation as a clearing op of its kind
([standing removals](../contracts/forecast-contracts.md#standing-removals)), and
`ConstructionMethodCandidates` binds each building as a pawn's Work task of that
kind on it (`removal-{n}`, Deconstruct as Work/Deconstruct, Uninstall as
Work/Uninstall; a roof-holder's Deconstruct op carries the roofed cells it leaves
unsupported in `Cells`, and its task runs a Work/RemoveRoof cell set step over them
before the Deconstruct in the same order, the same op and relief, #3617), each
stack as the pawn's Deliver of it (Count the stack, Destination the row's storage
cell, as a support delivery binds), each Cut wave as one Work/CutPlant set step
over its `Targets` and each FloorOut wave as one Work/RemoveFloor cell set step
over its `Cells` (Count the cells), exclusive outcome `construction/{op}`, with a
`removal/{op}` relief so taking it down, away, out or up is one unit of development
and its work is exempt from the thermal-relief gate like a floor's. The order's
claim is the op (the thing, the wave's lowest plant or the floor wave's
`removefloor/{x}/{z}/x{n}` id); an op whose thing or pawn another Method claims,
whose finish (a Haul's delivery) is unknown, or a Cut, Mine or RemoveFloor op naming
no members binds nothing. A stack larger than one carry is one Deliver task whose
order carries one `Capacity` at a time, the continuation repeating it over the rest
(#3625). A planned site's Cut clearing binds the same way before placement
(`PlannedRoomCandidates`, beside its Deconstruct and Haul clearings), and so does
its Mine clearing, one Work/Mine set step over the op's named rocks (#3616); a
planned room whose footprint holds a roof-holder is valued, not unknown, its
Deconstruct clearing binding roof-off-then-deconstruct the same way, and a roof
cell outside the planned room stays unroofed (RoofIn re-roofs the room's own
interior later). Up to 16 ops
a round across all kinds; more sets
`Truncated`.


## Planned rooms

`ReviewFacts.PlannedConstruction` is the material the layout plan still owes per room. It is the reconciler's own
diff, not a second one: `ColonyReview.PlannedConstructionOf` builds each room's `ReconcileInput` from the plan (the
stored `Pieces` as the template, the colony's standing buildings and floors as the census) and `PlannedOwedCost`
prices the `WallIn`, `DoorIn`, `VentIn`, `FloorIn` and `Build` operations that `Reconcile` says are owed, from
the def catalog. The price and the build therefore read the same diff. A wall two rooms share is owed by the
lowest room id. The shared construction demand reads it; the forecast holds no standing claim on it. A planned
room's material is claimed only through its delivery activities once selected (below). A wall, door, vent or
piece with a blueprint or frame already placed is left out: the standing site owes its outstanding material
through the construction census, so nothing is counted twice.

The plan also owes a roof (`RoofIn`): `PlannedConstructionOf` passes the ring input's open-sky cells, so a
roofed-set cell still open to the sky is owed once the room's ring stands. A roof costs no material, so its price
is unchanged. The planned sites (below) read the roof and the vents from a second, hypothetical reconcile in
`ColonyReview.PlannedOwedOf`, over `RingStandsGround` (the ground as if the owed ring stood): `PlannedSitedOps` keeps
the owed ring, floors and pieces and takes the vents and the roof from that pass. A room whose ring is not yet
built therefore still has a roof site.

Planned rooms are scheduled as construction activities in the coupled portfolio (#3276); see
[planned room activities](#planned-room-activities).

### Planned-site facts

Every round, `ColonyReview.PlannedSitesAsync` turns the same owed diff into `ReviewFacts.PlannedSites`
(`PlannedSiteFacts`: `Sites` and `Findings`). `PlannedSitesOf` makes one `PlannedBuildSite` per owed operation
and stuff:

- walls, doors and vents: one site per ring stuff, for up to `PlannedRingStuffCount` (2) stuffs per room. These
  are the stocked stuffs the wall and door can both be made from, cheapest first; with none stocked, the wood the
  window's standing trees give, else the blocks of its natural rock's stone (`ReviewFacts.RingSources`, #3747),
  else none.
  With every scope of the first stuff, that covers the 4 alternatives per room.
- floors: one site per floor def.
- pieces: one site per piece, in the bulk chooser's stuff. The round marks a piece's site `Blocked` (#3838,
  `ColonyReview.BlockedPiecesOf`) when the one blocked predicate (`BuildingPlanner.PieceReadinessOf`, read evidence
  only, #3819) reads its piece Blocked: research read unfinished or the definition read unavailable; no stuff the ring
  chooser finds; or a material of its cost list beside its stuff (a stove's Steel, a research bench's Steel, #3839)
  read unstocked and read unobtainable. A material is obtainable when the window offers it
  (`ReviewFacts.RingSources`, `RingStuffSourcesOf`): tree wood, rock blocks, what its natural rock is mined into
  (compacted steel's Steel), a stack lying on it, or what deconstructing an unowned, deconstructible building outside
  any ancient danger yields for certain (its adjusted cost list at `resourcesFractionWhenDeconstructed`, GenLeaving).
  Trade does not count. An unread stock, unread sources or a building whose yield the catalog cannot read leave the
  piece Unread, never Blocked.
- roof: one site.

Shared ring cells go to the lowest room id. Ring cells and pieces with a blueprint or frame placed are left out,
as `PlannedOwedCost` counts them. `Material` is the op's cost list times its pieces. The stockpile review's
unplaced `ShelfCandidates` are added as one-piece sites (`ShelfPlannedSites`, room 0, the candidate's zone).

`PlannedSiteRequest` asks native every site but the roof in one construction activity read (#3341). The read covers
every free colonist, since any may build, cut, mine or haul; native gives a pawn that cannot no row, and caps no
sites, rows, sources or positions. It searches each path once (`NativePathLegs`): a colonist's
leg to a stack or site once per (colonist cell, target), a stack's legs to a site and back once per (stack, site),
re-priced at each colonist's move ticks, since free colonists share the cell grid. `JoinPlannedSites` takes each
site's answer by id as its `Answer`: per pawn x source `Options`, `Verdict` (feasibility once cleared) and `Clearing` (its cut, haul, mine and deconstruct prerequisites). A site
has no `Answer` when no answer names it, or when no read came (`planned_site_read_unknown`); a read that fails throws (#3775).

The roof has no native placement and is never asked. Its work per cell is the coupled read's
`ThermalWorker.RoofCellWorkTicks`, which the generator joins.

Then the routes between the answered sites (#3313). `PlannedRouteProbes` builds, per room with a site native
answered with rows, one `PlannedRouteProbe` from each place an activity of the room ends to each place another
starts:

- ends: each probed site's location (its first position) and each clearing row's location;
- starts: each probed site's location, reaching its nearest position (`Reach`), each material source (a row's
  `Origin`), and each clearing's start (a haul's origin; a cut, mine or deconstruct row's location);
- the roof also from each pawn's own start (`FromStart`), since no native row routes to it;
- the need targets a pawn eats or sleeps at between two pieces of the room's work (#3676): the
  `PlannedNeedRouteTargets` (8) need-target cells of the needs read nearest the room's ends, each probed from every
  end and to every start. Without them a schedule with a meal inside the horizon has no route back to the room, every
  portfolio with the room is unscheduled, and the room is never selected.

The probed sites are those with rows, plus the roof. Every probe goes to one construction activity read (#3341).
Native answers a `CoupledRoute` per probe x free pawn, unknown where no path is found; a probe's path is searched
once for every colonist. `JoinPlannedRoutes` keeps the read's rows in `PlannedSiteFacts.Routes`; a missing or
unknown read adds none (`planned_route_read_unknown`), so those routes stay unknown.

The forecast's native reads cap nothing (#3341): every colonist, candidate and location pair. Measured once on
a lab with 32 free colonists (2026-10-16; main-thread ms per call, one call each a round):

| Read (one call a round) | Cold ms | Warm ms | Size |
| --- | --- | --- | --- |
| Planned sites (`NativeConstructionActivity`, `Planned`) | 288 | 269 | 20 sites (largest 144 cells), 4420 rows, 1275 path searches |
| Planned routes (`NativeConstructionActivity`, `Routes`) | 98 | 92 | 311 probes, 9952 routes, 451 path searches |
| Needs (`NativeNeedsForecast`) | 55 | 45 | 32 colonists, 240 targets |
| Coupled (`NativeCoupledForecast`, needs passed in) | 104 | 99 | all sub-reads, 16 locations, 8192 routes |
| Round total | 546 | 506 | |

A round stalls the main thread about half a second there; no limit is set from it
([kept constants](../contracts/kept-constants.md#forecast-native-reads-no-cap-measured-3341)).

The coupled read's route matrix (each free colonist's start to every location and its prey, and every location to
every other) is not searched pair by pair: `NativePathLegs.Floods` runs one Dijkstra flood per origin (#3685), the
game's own `PathFinderJob` with heuristic strength 0 and a destination off the map, fed the inputs `FindPathNow` builds
for the first colonist asking the origin's legs (its grid job's cost grid, doors, providers and blocking pawns, and the
map data's connectivity). The floods run together on the job workers, one calc grid and frontier per worker, and the
game thread waits for them; each wanted leg is read from the flood's calc grid (cost, steps and crossed cells) into
the same leg store, so the rows are built as before. A flood's cost is the shortest walk under the game's cost model,
at most a few ticks under the game's A* (whose heuristic overestimates a diagonal step); blocking pawns count on the
start side only. An origin whose legs the store holds is not flooded. The other sub-reads keep the batched A*
search (touch ends and thing targets). If a game update renames the pathfinder's private grid or doors members, one
warning names #3683 and the matrix is searched batched. `lab/route-floods` holds the floods to A* on a walled lab
(200 origins each to every other: 167 ms of game thread, at most 10 ticks under A* on 250 sampled pairs, 2026-10-20).
The `cf.fc.routes.floods` span carries the floods' time and count.

The legs stay in the map's leg store across reads, each with the cells it crosses, and a map change drops only the
legs it could alter (#3686), applied at the next read. A fog change or a game condition drops every leg. A region or
room change drops the no-path legs, which it can connect. A building, a path-relevant thing, a terrain or path-cost
change, a reservation or a haul en route dirties its cells; a dirty cell drops the no-path legs, the legs crossing it
(a wall or cost added on the path) and the legs a path through it could beat (a wall removed or a cell made cheaper):
those whose octile distance from the origin through the cell to the end, priced at the leg's own move ticks with no
cell cost, is under the leg's cost. A touch end counts any cell touching the target. More than 256 dirty cells in one
settle drop every leg. Legs have no time-to-live, and door open states are not watched (a leg is priced at the door's
state when searched). `lab/leg-invalidation` holds this on an open lab patch.

### Planned room activities

`GenerateCoupledActivities` turns the planned-site facts into construction activities when housing, shelter or
temperature safety is enabled (the placed-site gate; `planned_sites_unknown` when the facts are). The steps:

1. `PlannedRoomAlternatives` names each room's alternatives (one ring stuff and one scope each; below).
2. `PlannedRoomCandidates` (Methods generation) first leaves a `Blocked` piece site out of each alternative
   (`planned_room_site_blocked:{alt}:{site}`, #3838), which is then valued with the pieces that can be built; one
   left empty or the same as another of its room is dropped. It assesses every remaining site. A site with an
   `Illegal` verdict makes the alternative infeasible (`planned_room_alternative_infeasible:{alt}:{site}`). An
   `Unestimable` or unread verdict, no finish row, no delivery row for owed material, or a clearing with no
   routable row leaves it unvalued (`planned_room_alternative_unvalued:{alt}:{site}:{reason}`). A roof is
   valued only when the thermal read was made (else `roof_work_unread`), lists a roof worker (a colonist with a
   construction speed; else `no_roof_worker`), and a free one has a known route to the roof (else `roof_unrouted`).
   Nothing is emitted for an
   alternative that is not valued.
3. Each site of a valued alternative is generated in the placed-site shape: a delivery per material source
   (at most 4; `planned_site_sources_truncated:{site}`), one op per clearing prerequisite, and a finish. Whole-stack
   sources are alternative deliveries; a multi-stack set's stacks (#3673) are each delivered for their share and the
   finish requires all of them (a set over 4 stacks, or one not covered, leaves the site `no_delivery_option`).
   - A `Deconstruct` clearing binds (`Bindings`): its building stands now, so the op is the pawn's Work/Deconstruct
     task on it, at the planned deadline (#3557). A `Haul` clearing binds the same way as the pawn's Deliver of the
     stack to the row's storage cell (#3614), one carry at a time over a larger stack (#3625); the op keeps its
     `/cleared` output. Every
     other planned operation has no action template until placement (#3278), and a bound clearing is left out of
     the site's unplaced activities.
   - Ring finishes share one `ChoiceIdentity` per room and kind, so two stuffs of one wall cannot both be built.
   - A vent requires a completed wall site, and the roof a completed wall and door site, in whichever stuff.
     Completed planned sites are supplied at the room (`planned/room{id}`), not at their cells, so the link
     does not depend on where the dependent is worked.
   - The roof is worked at its own location, its first interior cell.
   - The planned-site routes of the free pawns join the generated routes, a `FromStart` row as the pawn's start.
4. Roots. Each site is its own comparison root, named by its site id and shared by every alternative that
   contains it. A site root's outcome Need (`ForecastPlannedSite.DevelopmentNeed`) marks it a planned site
   root, whose closure the thermal-relief gate exempts; it carries no relief. The evaluated outcome records the
   completed units (`RequestedDevelopment`): each planned site whose finish completes is one unit, whichever
   root built it, and a planned shelter's per-cell native roof cells together are the room's one
   `ForecastPlannedSite.RoofUnit` (`planned/room{id}/roof`). No planned room's unit counts as development, priced
   or not (#3828, `PlannedRoomCredits.PlannedUnits`): a room wins a portfolio only on the ledger, by the credit of
   a role (`Context.PlannedRoomRoles`, from `PlannedSiteFacts.Roles`) in `Context.RoomCredits` (#3796), or by the
   relief and mood its closure gives. Each alternative is also a root,
   `planned/room{id}[/{stuff}]/{scope}`, which requires all its sites' finishes and is not exempt;
   alternatives of a room exclude each other. Ring site roots of different stuffs, and the other stuffs'
   alternatives, are excluded from each other (`Outcome.Excludes`). Planned roots get no after-production
   variants.

A room over 96 generated operations is left out whole (`planned_room_candidates_truncated:{room}`).

With the routes between a room's sites, one builder can chain the whole room site after site. A route is still
unknown from where another family's activity ends (a meal, a harvest) to a planned site, so a pawn that switches
to other work in the middle of a room cannot be scheduled back onto it. A route is approximate where a site
spans several cells: it leaves from the site's first position and reaches the nearest position of the next.

`ForecastCoupledCandidates.Context.PlannedSites` (`ForecastPlannedSite`: site, room, stuff, composed activities,
finishes) and `Context.PlannedRooms` keep the mapping back. The evaluated outcome's `PlannedRooms` lists, per room
with a site finished in the horizon, the completed sites and the alternatives completed whole. A room left
partial names `planned_room_effect_unassessed` instead of a whole-room value, unless its open ring's closure is
projected (#3744, [roof and room consequences](#roof-and-room-consequences)), which values it.

The thermal-relief gate exempts allocations in the closure of a selected planned site root (#3651) and of a priced room's alternative (#3823). Binding skips unplaced planned
allocations (`CoordinatedActivityBinding.Start`): a planned site has no action template until placed, except its
bound `Deconstruct` clearings, which start as orders (#3557). A selection whose
only work is planned is compared again without the planned roots for the review's orders (#3557); one that then
starts no order records `coupled_selection` with target `planned_unplaced`. The room
reconcile then places exactly the sites the selected plan completes (`PlannedRooms`, not the development count) of an unstarted
assessed room, in their stuff, and holds an assessed room with none completed ([portfolio selection](portfolio-selection.md#planned-rooms)).

## Review inputs and pawn windows

`ResourceDemandOf` attaches `ForecastWorkInputs` to its existing `DerivedDemand`.
The normal observation path supplies the observed work pawns, and the review's
existing journal claims identify construction owners. Only claimed geometries
with a current standing site produce commitments; repeated historical claims at
the same geometry produce one outcome. Missing claims or an incomplete census
stay unknown, while completed, removed and cancelled claims stop generating
future work. There is no new demand calculation, journal or saved plan.

The coupled request consumes that exact demand value, its observed workers and
material reservations; it does not invent labor budgets, routes or free time.
Normal selection uses the coupled request and Method binding described in
[portfolio selection](portfolio-selection.md).

`ForecastPawnCapacityOf` derives one timeline per pawn from observed availability,
its 24-hour timetable, local time, remaining useful current-job time and declared
needs/protected occupancy. Its horizon is bounded to seven game days. Non-work
slots are protected; this consumes timetable constraints as observations and does
not install a timetable or make native priorities the new activity selector.
Needs transitions come from the [needs timeline](#needs-alternatives-and-future-capacity). `ForecastCurrentWorkOf`
is shared with the live work-priority planner: an observed non-idle job with no
remaining-time estimate makes capacity unknown rather than freeing the pawn. The
existing idle-job classification contributes zero productive work, while an unread
job remains unknown. A declared protected obligation can still occupy an idle pawn;
the occupancy census must be known even when the current job is idle. The input is
remaining work only, not sunk effort.

The live roster uses this shared current-job interpretation to protect eligible
ongoing work from competing primary roles of equal or lower native natural priority.
See [work assignment](../contracts/work-assignment.md#ongoing-work-and-shared-occupancy).
This connects observed occupancy to live priority decisions, but does not supply
route/rate/needs estimates, invoke timed joint selection from production, or turn
primary-role coverage into a completion forecast. Construction's raw skill ordering,
medical rest and higher-priority work retain their overrides.

Overlapping active obligations are unioned. Repeated identical IDs count once;
conflicting IDs fail. Completed/cancelled rows release their occupancy. Unavailable
pawns have known zero capacity; an unread schedule, job or active obligation stays
unknown. The output contains free windows and total free ticks. Each window after
protected time requires an explicit location estimate before approach travel can
be evaluated; unknown location is not the pawn's old position. The current input
can state a common endpoint assumption for these gaps; it does not infer where a
pawn will eat or sleep. Different work types use these same windows, and one task
cannot straddle a protected interval even when the total free time is sufficient.

Generic activities can also carry multiple typed outputs. Each completed activity
emits its outputs once, at its completion day (`ForecastActivities.StockChangesOf`
derives a plan's changes from its problem). Helpers and retrieval do not automatically get another copy. A stock
change is a plain day and quantity: deriving one from an unread yield throws `RequiredFactException`, and inside the
coupled evaluator an unread yield's output stays an unknown effect of its own schedule. The
plan's labor comparison still assumes the caller's declared outcome/alternative;
these effects are conditional, not observed goods or a new utility currency.

`ForecastWorkInputsTests` exercises shared cook/builder capacity, independent
workers, overlapping obligations, cancellation, known-zero versus unknown time,
changed timetables, remaining current jobs, protected gaps and missing endpoints,
multiple outputs with support, review claim deduplication and the shared demand
reference. Construction's existing skill/tier tests remain in force. These tests
do not establish general live work-family eligibility, native equipment claims
or resolution of every planner-local ingredient promise.

## Material promises and exclusive equipment

`ForecastCommitment.MaterialClaims` holds exact selected ingredient quantities
owned by that Method. These are promises against current stock, not target floors,
native reservations or inventory consumption. They protect stock throughout the
declared scenario. A commitment cannot carry both a material claim and a stock
effect for the same resource; replace the promise with its timed consumption when
that effect is known. Standing construction continues to derive its claim from
observed outstanding needs, so it cannot also carry supplied material claims.

`ForecastMaterialBudgetOf` extends the existing `MaterialBudget`: native bill holds
are deducted once, then declared promises and in-horizon negative stock effects.
Future outputs cannot finance these promises. The budget is free units, separate
from inventory trajectories; negative availability means the declared promises
exceed stock. An unknown promise quantity affects only that resource.

The observation projection retains each native reservation's bill identity.
`ReservationBill` is known empty for a promise not handed to a bill, the exact bill
ID when it covers that bill's current promise, or unknown when the association is
unverified. Claimed units include native-held units: a promise of 50 with 20 already
held by the same bill protects 50 in total. Another bill's 20 remains additional.
Only one commitment may link a given bill/resource; duplicate links fail. Every native
hold names its pawn and bill: one read without them is a `RequiredFactException`, and a
row without them fails the budget (#3881). The caller must supply the current outstanding promise, not a
historical full bill quantity or a demand target.

Baseline and delta rebuild independently. Removing a promise releases its forecast
claim, but an observed native hold remains until a fresh read shows it released.
This resolves double accounting at this declared forecast boundary. Committed
fixed batches also enter the live review as described below. Other production
still has the planner-local ingredient exception in architecture rule 8. These
derived claims do not change #2742's Method admission locks, acquire native
reservations, or introduce another demand store.

`ForecastActivityOption.Equipment` names exclusive equipment required throughout
service, with one `ForecastEquipmentCapacity` per physical identity. Its known
availability windows exclude external/native use. The equipment must be at the
activity's service location: this slice models stationary equipment or a tool
already supplied there, not portable-tool transport, pickup, wear or replacement.
Missing capacity or location mismatch refuses that option; options using other
equipment can continue. An empty requirement list means the declared activity
requires none, not that native requirements have been discovered automatically.

The same bounded activity search generates starts at equipment window openings
and prior users' release times, including early approach/wait and just-in-time
approach. Each departure attempt consumes the existing expansion budget. Service
intervals are half-open, so the next pawn may start when the prior user ends; the
whole service must fit a free window. Pawn occupancy still covers approach,
waiting and work. Selected plans expose equipment claims once per use.

Construction options use this mechanism directly. A selected plan removes its
equipment intervals before evaluating the next commitment. Declared external
`EquipmentClaims` protect ongoing obligations; overlapping intervals are unioned,
and an unknown endpoint makes only that equipment's availability unknown. Removing
a commitment releases its forecast intervals in the proposed scenario. Equipment
availability is an explicit supplied estimate, not a new native observation claim.

`ForecastClaimsTests` and the construction scenarios cover ingredient handoff,
cancellation with native holds still active, scarce material, resource-local
unknowns, one tool versus two, waiting and release candidates, deterministic
truncation, location refusal, overlapping equipment obligations and native bill-ID
projection. This proves conditional accounting and combination generation. Live
claim generation beyond fixed batches, native equipment reservation/dispatch and full production
coordination remain unfinished; no gameplay improvement is established here.

### Live fixed-batch material promises

Each Round projects committed `GearBatch` actions from the existing Method journal
into `ProductionBatch` inputs. This mode means a fixed number of recipe iterations;
it is not restricted to a recipe name. The bounded read covers at most 256 matching
plans and 256 batch actions in the current colony/load/map. Overflow is unknown,
not a truncated inventory reported as complete. Historical actions count toward
that bound; this is a small session-scoped integration, not an unbounded history scan.

Before dispatch, the action's count and ingredient filter constrain catalog recipe
slots. After placement, the receipt's exact native bill ID joins the current bill
census. Its remaining repeat count replaces the original count. Only a slot with
one allowed resource has an exact quantity; alternative resources stay individually
unknown. Pricing joins each batch with the catalog: a priced batch carries its
recipe's slots and unfinished-item flag, and an unpriced batch (its recipe absent from
the catalog), an unread owner census, or an edited bill whose filter may
have changed make the promise set unknown. No cheapest-ingredient demand estimate
is promoted to a reservation. Quantities use the catalog's per-iteration counts,
with exact bound unfinished work handled below; no future production output is credited.

Pending cancellation releases the promise. An applied bill remains a promise even
if its journal action is cancelled: native completion, suspension or disappearance
must release it. An unread native census cannot prove disappearance. Native job
holds remain independently protected, including after a future promise is released;
the exact bill/resource link reconciles their overlap once.

`ResourceDemandOf` owns the result. Acquisition targets add known production holds
to construction obligations, even when each obligation separately fits current
stock. Existing alternative-ingredient demand floors still merge by maximum.
Unknown quantities are not invented as acquisition targets. Trade's shared retained
stock becomes unknown when any production hold is unknown, preventing the
wealth-surplus path from treating it as saleable. This does not change every other
trade/export policy. `DerivedDemand.MaterialAvailability` subtracts construction
and the same reconciled holds; stone-wall upgrade proposals consume this budget
and refuse required unknown materials. Other construction planners are not cut over.
The construction forecast request includes these same production commitments from
`DerivedDemand.Work`, rather than asking callers to recreate them.

Shared planner publication occurs on the next review after commit. Fixed batches
also have the transaction-time admission check below. Pending building proposals,
indefinite stock-target/forever bills, production
surviving a new launch, selecting unbound unfinished work, portable equipment,
and full timed selection/dispatch remain outside this slice. The ordinary cooking
declarer emits stock-target bills, so a synthetic fixed cooking batch proves generic
material arithmetic, not a live cooking cutover. `ProductionPromisesTests` covers
the actual journal projection, shared construction/acquisition/trade values, partial
native handoff, remaining-count reduction, cancellation, suspension, disappearance,
unread facts, and alternative ingredients. No native gameplay result is established.

### Ingredients inside unfinished work

The native bill census always carries `UnfinishedWork`, a required union: `none`
means no live unfinished item, and `bound` names one initialized unfinished item
bound to that exact bill with its ingredients. Those ingredient quantities are embedded
inputs, not spawned stock or live job reservations. Native reads the non-mutating
`BoundUft` reference; it does not call `BoundWorker` or `BoundBill` getters that can
change bindings. An uninitialized item or inconsistent recipe/ingredient read
fails the bill read loudly. A destroyed item is `none` and gives no credit.

RimWorld gathers an iteration's ingredients before creating its initialized
unfinished item. The shared promise therefore reserves fresh inputs for
`remaining iterations - 1` when that exact bound item exists. It credits an
iteration, not arbitrary embedded units: an item made with alternate stuff cannot
discount the ingredients for later iterations. A sole remaining bound iteration
needs no fresh inputs even when future ingredient choices would be ambiguous.
Two active promises cannot credit the same unfinished identity.

Before placement, or with observed absence of bound work, the promise reserves
the full fresh-input requirement. This is a conservative plan: it does not predict
which unbound item native may later adopt. Unread binding on an applied bill keeps
affected quantities unknown. Embedded inputs never become free material or sale
stock. Starting work removes raw inputs from the stock census while reducing the
fresh promise by that iteration, so it does not charge twice. Ordinary interruption
retains the binding; bill suspension releases future demand and resume restores
it using the same item. Completion reduces the native remaining count and clears
the bound item. Cancelling the item alone restores the remaining bill's fresh
requirement; cancelling/removing the bill releases its future promise. Neither
operation earns an assumed refund: only newly observed loose stock can do that.
Native job holds remain protected independently.

The existing native production tracker recognizes all tracked production bills,
including non-meal fixed batches. Its immutable-configuration comparison excludes
the mutable remaining repeat count; current readback supplies that count, including
player changes. Ingredient-filter and other configuration edits still invalidate
the original promise. Full bill-stack CAS fingerprints continue to include the
remaining count. No new ownership store or game-order writer is introduced.

`UnfinishedMaterialTests` covers the shared acquisition/construction/trade values,
pause/resume, completion and cancellation, alternative embedded material,
remaining native holds, duplicate identity and wire unknowns. No acceptance case
covers the native unfinished-thing bindings (#3409 deleted the fixture-only probe).
Autonomous pawn crafting, recovery yield, route feasibility and full coordinated
dispatch are not demonstrated. Ordinary stock-target cooking remains outside this slice.

### Fixed-batch material admission

Every Method containing a fixed-count production batch checks material availability
inside the journal transaction that binds the Method and creates its plan. The
existing SQLite transaction serializes competing callers, including separate store
connections. A refused transaction leaves neither a plan nor a Method binding.
Two batches proposed from the same review cannot each claim the same remaining
inputs; multiple batches inside one proposal must fit together too.

The full review records replaceable material evidence in its existing Colony review row:
stock, observed construction deficit, native job holds, recipe ingredients and
reviewed production promises. Admission re-reads the bounded current-world journal
projection, combines reviewed promises with batches committed since that review,
and calls the same pure material-budget functions used by `ResourceDemandOf`.
It does not decrement a separate stock ledger. The proposal's exact ingredient
filter and catalog slots determine its costs; unknown quantities or missing
evidence refuse admission. The existing 256-action projection bound also bounds
admission, including historical batch actions; reaching it refuses further batches.

A never-dispatched cancelled commitment releases its claim immediately. Dispatch,
an accepted placement, or a later journal cancellation does not refund old stock:
post-review batches retain their full claim until a fresh review observes native
progress. Reviewed bills retain that review's remaining/unfinished-item accounting.
This avoids spending stale stock again after a job consumes inputs. Fresh reviews
replace evidence; immediate reviews preserve it within the same world, and world
changes or rewinds discard it. Disabled reviews cannot authorize admission.

`ProductionAdmissionTests` covers competition within and across Methods, a race
between independent database connections, atomic rollback and retry after
cancellation, stale dispatch evidence, exact native-hold overlap, unknown inputs,
and review/world lifecycle. These are policy/journal guarantees, not native
resource locks or evidence of autonomous gameplay. Pending placements and food activities share this boundary; see [shared live work resources](#shared-live-work-resources). Trade sales and indefinite production remain outside it. Native simulation can still change stock between reviews.

### Joint production material choices

Before the work ledger commits new fixed batches, `SelectLedgerMaterials`
expands each declared recipe's allowed ingredient slots into material alternatives.
It compares combinations across jobs against `ProductionAdmissionBudget`, the same
free-material calculation used by atomic admission. Every job also has a postpone
alternative. A flexible job can therefore choose timber so a metal-only job can
proceed, even when its deterministic standalone choice would have used metal.
Only the selected combination receives narrowed bill filters.

`CompareMaterialPlans` accepts supplied option values. Unclassified live orders receive **one unit of value per funded declared batch**, with stable ordinal ties. Clothing now carries the explicit requirement priorities described below. This is an explicit provisional objective, not calibrated
colony utility. Ordinary sculpture declarations retain all stocked materials that
meet their existing suitability floor instead of first picking the highest local
beauty/market score. Recipe size, bench and worker selection still happen upstream.
Other declarers' explicit ingredient restrictions remain binding. This slice does
not compare product quality, market returns, partial batch sizes, or all possible
craftable products.

Generation permits 1,024 recursive expansions and comparison permits 4,096 search
expansions. Both bounds, counts and unknown findings are returned; the existing
Method reason records counts, truncation and finding kinds. Results are best found
within these bounds, not proofs of global optimality. Missing recipe costs or
required material estimates postpone the affected options. A whole unknown budget
postpones all fixed batches. Indefinite/stock-target bills retain their existing
path and are not priced by this comparison.

Native bills have one global ingredient filter. A generated union that would
reopen ambiguous choices in any slot is rejected rather than treated as an exact
assignment. Mixed materials within one ingredient slot are not generated. The
selected filters are re-priced and rechecked inside the existing atomic Method
commit, so another journal claimant can still invalidate a selection. No extra
reservation store or demand owner is introduced. Postponed placements remain in
the existing pending ledger and are redeclared at full review. Reconciliation
recognizes a pinned material subset as serving the original broad declaration,
while preferring exact matches and preserving recipe, worker and target identity.
Hands remains the sole order writer.

`MaterialChoiceTests` proves joint alternatives outperform local pinning, changing
stock/allowed filters changes the feasible combination, supplied values can change
the winner, sculpture declarations retain alternatives, ambiguous filters and
unknowns cannot pretend to be exact, bounded search reports truncation, pinned
readback avoids duplicate bills, and selected filters become exact shared journal
claims. These are policy and journal tests. Native crafting completion is the
claim of the `production/clothing-activity*` cases; joint pawn/bench/material
scheduling with needs is the coupled trajectory's; sales admission stays outside
this boundary.

### Need-based production valuation proof

The existing material generator and bounded comparison also accept explicit
`ForecastProductionValuation` inputs. This is an opt-in policy proof: live clothing uses the explicit priority objective below, while unclassified producers still use batch count. No live producer supplies the comparable scalar utility estimates for this proof. Supplying a valuation switches the whole
comparison to it; an unknown estimate never falls back to batch count.

Each exact narrowed order key supplies the **whole batch's** need relief and labor
ticks. One shared needs map supplies remaining unmet units and utility per unit.
One shared valuation supplies utility cost per material unit and per labor tick.
All utility rates must use the same caller-defined scale. These are assumptions,
not game observations or calibrated prices. Different material variants may
supply different relief and work estimates; the generator still respects the
recipe and declaration filters.

For every candidate combination the comparison computes:

`net = sum(min(remaining need, combined relief) * need utility) - material opportunity cost - labor opportunity cost`

Relief is aggregated and capped by need identity across all selected jobs. Two
jobs serving the same remaining need cannot each claim its full benefit. Material
costs value consumed resources, while the existing shared stock budget separately
enforces physical affordability. Labor cost represents displaced work at the
supplied rate; it does not establish worker availability or a completion time.
The empty/postpone plan has value zero and wins a zero tie. Results expose the
selected combination's benefit, material cost and work cost separately.

Missing required effects, needs, rates or work estimates exclude the affected
options with explicit findings. Known zero rates must be supplied deliberately;
zero consumption/work needs no rate. Negative or nonfinite used estimates and
overflow are rejected. The same generation/search bounds and truncation flags
apply. Search findings describe encountered evidence; truncation can leave other
options unexamined. No journal store, second demand target or order writer is
introduced: all valuation inputs and results are caller-owned derived data.

`MaterialValueTests` demonstrates an urgent machine beating two optional jobs,
a satisfied need changing the winner, capped overlapping relief, better materials
winning only when their extra benefit pays for material/work costs, postponement
when net value is nonpositive, unread inputs that never count as zero, bounded search and hard stock
limits. An end-to-end pure selector scenario generates the exact material filters:
a sculpture uses better material alone but switches to wood alongside the urgent
machine. These tests prove choices under supplied assumptions, not observed
quality, calibrated colony utility, live valuation adoption or native completion.

The next integration must derive comparable estimates from owning need forecasts,
net existing commitments out of remaining needs once, and populate exact variant
estimates at a coherent review. Urgency, deadlines, uncertain outcomes, interactions
between different needs and joint worker/time capacity are not inferred here.

### Live clothing priorities

The [equipment owner](../contracts/equipment-upkeep.md#live-clothing-requirement-selection)
now attaches observed clothing requirement effects to exact material variants.
The same bounded comparison ranks required coverage and condition
before unclassified batch count (warmth is a comfort shift priced as break risk, #3330), then uses ingredient market value and base recipe
work as tie-break costs. Need relief is capped by stable need identity across
selected batches. This explicit priority objective is separate from the supplied
scalar utility proof; mixing them in one comparison is rejected.

Clothing-only optional upgrades with no required relief wait. Unknown variant
estimates do not revert to batch-count scoring. Existing shared stock budgets,
atomic journal admission, generation/search bounds and postponed placements are
unchanged. Method reasons report priority totals and clothing cost estimates.
Other producers still lack need classifications. This is live clothing selection,
not calibrated colony economics or joint pawn/bench/time scheduling; garment shape
and recipe selection remain upstream, and whole batches cannot split materials.

## Competing outcomes and shared labor

Outcomes compete in the coupled consequence ordering described in [portfolio selection](portfolio-selection.md).
A `ForecastActivityOutcome` names one existing need, its completion task, its requirement relief, retained
ongoing work (`ForecastRetainedWork`, the ticks until observed work completes) and its exclusions; activity names
carry no priority. The legacy activity-only `CompareActivityOutcomes` overload, which scored supplied avoided-loss
estimates against an alternative labor budget, was deleted in #3894 with its unread benefit, deadline and
choice-identity members: the coupled trajectory was its only consumer and never read them.

## Shelter and food selection

The coupled trajectory owns food and shelter selection, including the planned-room thermal envelope,
work-time estimation, construction and delivery adapters, and observed replanning. See the
[pawn activity contract](../contracts/pawn-activities.md).

## Food candidates from acquisition and recipe observations

Acquisition uses required numeric deficits and pending yields. Pending and undispatched work suppress duplicate selection without counting as delivered food; withdrawing a stalled source removes its pending credit. Residual supply labor charges each retained food channel once and refuses an unpriced commitment. Human-butcher candidates require their matching pawn row, traits, ideoligion and work eligibility. Animal-product views retain the native gatherable or egg-layer row; independent producing components add their yields and work, while feed is charged once per animal. Slaughter views retain the native slaughter row, and fishing queries use the existing region review and its native observations. Terminal stock evidence distinguishes an eligible quantity (including zero) from a lot with no observed food use. Animal feed is calculated directly from the complete food census and eligible herd, with an empty herd yielding zero shortfall. Missing kept-animal consumption or race growth inputs refuse that calculation. Seasonal hay demand uses the same required harvest-gap value as food reserves. The food family checks reserve and foothold runway, butcher facilities and campfire retirement as plain predicates; absent runway or butcher build definitions refuse the calculation. Caravan and quest packing read the same complete food census.

Normal Colony review generates its food alternatives through `FoodMethodCandidates.GenerateCoupled` (the
[live policy stages](#live-activity-policy-stages)). The supplied-input generator `GenerateFoodActivities` was deleted
in #3894: nothing outside its tests called it. A coupled harvest or cook candidate whose product has no read shelf
life is skipped with `food_shelf_life_unread:{supply}`, and two observed supplies under one id are refused with
`conflicting_food_supply:{supply}`; neither is guessed.
The [activity execution contract](../contracts/pawn-activities.md) includes an
exact one-trip delivery adapter alongside harvest, plus a one-iteration cooking
adapter for exact single-resource ingredient stacks and an existing selected bill.
Their native cases check physical output and explicit revision after refusal,
with fixture-selected assignments.
Those fixture cases prove execution, not selection; the nightly
`coordination/food-delegated` and `coordination/food-carried-recovery` cases
prove the selected chain, observed loose-food rebinding and carried-item recovery
under normal Method admission and Hands dispatch. Mixed-ingredient cooking is an
explicit unsupported-recipe finding.

The existing Method journal now checks activity payload attribution against its
committed Method and rejects repeated pawn revisions across actions in one plan.
Journal-to-Hands intent tests cover that boundary, including atomic refusal.
This guard does not supply live forecast selection, cross-owner pawn claims,
output rebinding or completed-work evidence.

Normal Colony review now compares the supported food, needs, construction and clothing
portfolio before journal admission. It keeps self-service and delegated
alternatives, binds selected exact actions, and advances through observed loose
food, bounded carried-input retry and native blueprint/frame rebinding. See
[normal portfolio selection](portfolio-selection.md) for supported mechanics,
search bounds and remaining independent consumers. Predicted yield is never
credited as actual stock.

### Live activity policy stages

These are steps in proposing and advancing a Method, not new domain actors:

`Concern -> candidate Method alternatives -> ColonyForecast comparison -> Plan/actions -> Method journal -> Hands`

The existing worker observes outcomes and advances or releases the same Method.
Only journal admission creates an owned Method; generated alternatives are
transient forecast inputs and concrete target bindings, not persistent Methods.

| Owner | Responsibility |
| --- | --- |
| Normal ColonyReview | Read native options and sequence admission of the policy-selected portfolio through the existing journal |
| `FoodMethodCandidates` / `ConstructionMethodCandidates` / `ClothingMethodCandidates` in `Methods.Generation` | Generate operation/provider alternatives and task/pawn binding templates without selecting sources or participants |
| `ForecastCoupledSelection.CompareActivityOutcomes` | Shared ColonyForecast alternative search, scheduling, deadlines, labor ceiling, postponement and selection |
| `CoordinatedActivityBinding` in `Methods.Binding` | Resolve shared selected allocations into exact activity actions and advance observed obligations; no independent ranking or food-family rebinding selector |
| `StartCoupledActivity` / `ContinueCoordinatedActivity` | Bind the shared selection, then apply pure ownership/lifecycle checks and observed continuation |
| Existing Method journal and Hands | Admission, progress/revision ownership, dispatch and release |

There is no separate generic activity-candidate or selection engine. Binding
consumes the existing `ForecastActivityOutcomeComparison`. The public lifecycle
functions also return that comparison for inspection, including findings and
search truncation. The planner includes bounded selection in its existing Method
reason; this does not introduce telemetry or persistence ownership.

The historical family helper APIs retain the comparator's explicit
`LowestLaborPerUnit` objective for their isolated tests. Normal production no
longer calls those APIs: its [coupled objective](portfolio-selection.md) compares
physical need trajectories, mood and requested development before participant
labor. The worker does not rerank cooking versus delivery after harvest.

The historical isolated family adapter enumerates at most sixteen eligible alternatives, matching the
shared outcome comparator, with a 32-task cap and one 512-expansion budget.
Cooking alternatives require more tasks and can reach that cap first. Both omitted native
options and policy generation limits produce `generation_truncated`; omitted
choices may be better. This is narrower than the former 128 independent trip
comparisons. Equal objective values use the shared comparator's lower-total-labor,
then earlier-completion tie break, followed by stable outcome ordering.

`RGARCH010` has no baseline. Generation and binding cannot call each other or
policy comparison helpers. Lifecycle entry points can call the shared outcome
comparator but cannot add arithmetic, use the lower-level allocator directly, or
construct orders. These stages cannot construct or clone a forecast selection.
Checks resolve aliases, method groups and target-typed construction. All Method
implementation types remain internal. This is a dependency fence, not proof of
complete candidate generation or correct arbitrary scoring; gate changes still
require review. Older planners are not all migrated into this structure.

The live adapter proposes raw ingestion, delivery and bounded repeated cooking
before comparison. Exact native recipe eligibility, shared material lots,
eater needs and delegated routes determine feasible chains. Shared Method
claims protect participants and exact resources through observed continuation.
See [live cooking](../contracts/pawn-activities.md#live-one-iteration-cooking)
for binding, failure and native-read limits.

### Planned room alternatives

`PlannedRoomAlternatives` (Policy, pure) names each planned room's alternatives from its planned sites. Each
`PlannedRoomAlternative` is one ring stuff and one scope, with the ids of the sites it builds. It does not price,
rank or select:

- **Scope**: `Shell` (walls, doors, vents and the roof), `ShellFloor`, and `Full` (also the stored pieces), by
  operation kind.
- **Stuff**: every scope of the room's first ring stuff, then each further ring stuff at `Full`, at most
  `PlannedAlternativesPerRoom` (4; `planned_room_alternatives_truncated` past it). A ring site belongs to its own
  stuff's alternatives; floors, pieces, the roof and a stuff-less vent belong to every alternative their scope
  covers.
- Ids are `planned/room{id}/{stuff}/{scope}` (`planned/room{id}/{scope}` for a stuff-less ring). An alternative
  with no site, or with the same sites as an earlier one of its room, is left out.

Postponing a room is not an alternative; the shared comparator owns it. The alternatives are compared as
activities in the coupled portfolio ([planned room activities](#planned-room-activities)); the family runs no
winner loop of its own.

Limits: a shared wall is attributed to the lowest room id under every alternative. Pens and yards are rooms of
the plan with ids (the tiler places both as room nodes), so they get alternatives like any other room; their
furniture is not in the plan's stored pieces, so only the ring, doors and floors are sited.


### Shared live work resources

The full Colony review review freezes `WorkAdmissionEvidence` in the existing review
row. `WorkResourcesOf` combines that observation with current Method actions;
it owns no mutable reservation table. Food activity selection and continuation,
fixed-batch material selection, work-priority assignment and Method admission
consume this projection. Admission reconstructs it inside the journal transaction.

The projection includes loose stock, native bill holds, outstanding construction,
exact building costs, remaining fixed batches, pawn timetables/current work and
native activity ownership/carried inputs. A placement is admitted whatever the
stock, since a blueprint spends nothing until delivery: once admitted it holds
its exact cost against the budget, so other work cannot spend it and its shortfall
is construction demand. Other work in the same plan is checked after the plan's
own placements; a truncated work census refuses placements. A matching observed
site transfers ownership to the construction deficit. Food
delivery protects its declared stock without consuming it in the forecast.
Carried units are outside loose stock and reduce the outstanding claim. Cooking
binds the ingredient definition as well as concrete stack IDs, and native checks
that definition. Exact bill/resource identity overlaps an iteration with its
remaining batch promise and native hold once. No prospective output adds stock.

One installed activity owns its pawn until release or observed ownership end.
Pending replacement retains the installed predecessor; accepted replacement
supersedes it. A refused replacement cannot erase its predecessor's claim.
Never-dispatched cancellation releases a promise, while dispatched work remains
protected until fresh evidence reconciles it. Cleanup can outlive the Concern
and does not require an affordable productive continuation.

Capacity uses the existing `ForecastPawnCapacityOf` timetable and occupancy
calculation, with a seven-day projection bound. Live current-job duration is
unknown, so occupied pawns do not acquire a fictional free day. A claimed pawn
is excluded from routine work assignment and unavailable to another activity;
disjoint pawns remain usable. Bench and target claims are exclusive while their
owning work remains outstanding; an observed food stack is the exception, owned
by the units its Method's remaining Eat tasks owe so that generation proposes
and admission shares its observed remainder with another Method (#3194). These are conservative availability constraints,
not calibrated completion estimates or native locks. Work can change between
review and dispatch; Hands and native legality remain authoritative.

The journal bounds work projection at 1,024 actions and fixed batches at 256;
exhaustion is explicit truncation with unknown dependent availability, never an empty census. Unknown quantities
remain resource-local where their resource is known. An unpriced commitment
with unknown material identity makes material availability unknown, while pawn
and target evidence remains separate. Neither missing observations nor accepted
orders prove completed work.

Remaining consumers outside this admission coverage are terrain work,
indefinite production and trade/export. Their planner-local accounting is still
an architecture-rule-8 exception. Coupled consequences and needs-derived capacity
use this owner as described below, and normal portfolio choice uses this view.
Supplied-estimate forecast APIs remain useful for tests, but are not evidence of live capacity.

## Needs alternatives and future capacity

The ordinary Colony review read carries `Forecast.Needs`: per-pawn food/rest/recreation
levels, native current fall/recovery rates, thresholds, food capacity, expiring
memories, authority state and bounded eligible food/bed/recreation-building targets.
`ReviewWorkResources` generates `NeedsAlternatives` from this frame and its shared
work view. The normal shared portfolio comparison selects supported needs services
and dispatches through the existing Method journal. No new scheduler,
state store or game-order writer is introduced.

Generation retains food/count, now/later, partial/full bed recovery and recreation
alternatives. Counts respect the observed single-ingestion limit and projected
nutrition wanted; a raw-food serving chews once, not once per item. Target travel
uses native route cost. Starts include now, the need threshold event, half the
bounded horizon and protected-window ends. These are finite search samples, not
proof of globally optimal timing. The policy expansion budget is 256 (32 per
pawn in coupled selection); every start/variant pair spends one unit before any
filter. Units go round-robin across need kinds, then across a kind's targets, so
many food targets and counts cannot starve rest (#3167). Truncation is reported,
with `needs_generation_truncated:<kind>` naming each kind cut; the native read
covers every colonist and every target (#3341).
Omitted choices have not been compared. Continue/defer is the
empty selected-needs set, not another mutable Method.

`ForecastNeedsTimeline` evaluates a selected set on the existing pawn timeline.
Travel and need service occupy time once; overlapping need actions, current work
and explicitly protected obligations are rejected. The legacy native timetable is
not a strategic constraint on this forecast. Explicit player work, drafted,
downed and mental-state authority remain protected, and journal ownership with
unknown remaining duration remains unknown.

Eating changes food at completed ingestion; an interruption before completion
credits no food. Interrupted rest/recreation credits elapsed service only.
Outputs separate final levels, integrated deficits, threshold exposure, excess
nutrition/recovery, memory effects and productive windows. Productive windows
exclude selected needs, travel and intervals at zero food/rest where physiological
work capability is unassessed. They are a conservative supported-work bound, not
a claim that RimWorld prohibits all labor at those levels. The returned capacity
feeds the same `ForecastActivityProblem` as production: a paired test demonstrates
that rest makes later production feasible, rather than merely changing a report.

### Validity and unknowns

Rates are observed at current health, need category, tolerance and bed conditions.
The projection is piecewise linear in time, not a per-tick copy of RimWorld.
Category-dependent fall, changing recreation tolerance, new health events,
weather, disease/relationship changes, room mood, table/chair travel, ingestion
outcomes and poisoning remain explicitly unassessed. Prediction values are
conditional estimates, not qualified risk or guaranteed completion times.
Sleep/recreation service uses native gain mechanics; ingestion service reads the
native chew duration and eating-speed stat. Arrival cost excludes a subsequent
native chewing-position or play-position choice, identified in findings.

Memory expiry/fading changes the observed mood target conditionally. Permanent
memories carry an explicit flag. Unknown expiry, repeated/grouped memories or
unsupported renewal/stacking make the memory result unknown. The
`MemoryAdjustedMoodTarget` remains the memory contribution applied to the current
target. The coupled [mood timeline](portfolio-selection.md#needs-and-observed-execution)
separately models supported conditional thoughts and actual seeker response,
plus memories a commitment grants at completion, and prices
[break risk](../contracts/forecast-contracts.md#coupled-mood-break-risk) for pawns
with an observed threshold. Unmodeled need-category/room changes remain unassessed.
Existing Mood/comfort functions are diagnostics, not a second forecast scheduler.

Pure tests cover equal current
mood with different thought expiry, meals, quantity waste, recovery, timing,
interruption, unknowns, protected work, distance and bounded generation.

### Clothing activity consumer

[Ordinary tailoring](../contracts/equipment-upkeep.md#live-clothing-requirement-selection)
uses these same stages. Generation retains garment/recipe/stuff/stack/worker and
destination alternatives. Evaluation compares capped equipment-owned requirement
relief and total labor under shared resource claims; binding emits only selected
exact recipes and observed-output delivery tasks. Conflicting wearables and
shared destinations exclude one another. Normal clothing alternatives feed the
coupled trajectory and shared portfolio comparison; the retired family-local
winner and continuation wrappers are not executable selection paths.

## Tier 2 colony model

The [tier-2 model](forecast-value.md#tier-2-event-driven-colony-model) (#3470) looks about a game year past the
one-day coupled trajectory and tells tier 1 what an hour of research or sowing is worth. It lives in
`Policy/ColonyModel` (pure) and runs from `ReviewColonyModel.Review`, once per review inside the ColonyReview, after the coupled
candidates are built and before the comparison; nothing is cached between reviews. The Devilstrand payoff chain (the research
gate, the crop's craft node and the garment they feed) is wired into real reviews by `ProjectGraph.Standing` (#3559); where the
exposure table is calibrated the garment's harm is priced and the chain's investment credit is negative (a loss avoided); the
in-play proof is `production/devilstrand-investment` (#3560: a research block with `value.investment < 0`). `production/devilstrand-investment-starving`
(#3532) proves the other half in play: while the urgent floor binds, the credit never outranks food.

**Inputs.** `ColonyModelInput` (`ColonyModelInput.cs`, assembled by `ReviewColonyModel.InputOf`) reads facts the review frame
already carries and makes no native call: the seasonal climate, the calendar, the growing zones and planned fields with
per-cell fertility, the crop catalog, the research census, the stocks (nutrition of the edible food census, materials,
medicine), the pawns (need mechanics, capacities, comfort range, research and plant work speed from the coupled research and
sowing reads), the recipes, and the planned construction sites. Every member is plain: a frame that lacks a section
yields `ColonyModelInputRead.Unready` naming each gap (and `Gaps()` a value outside its range) instead of an input, the review of
it has no run and no basis (`colony_model_input_unready`), and the `colony_model` event reports each gap as `input:<section>`.
A need's service by use is `ModelService.Observed` or `ModelService.NoneObserved`; a pawn who cannot research or plant has a work
speed of 0, and the research benches are a count that limits the researchers.

**Pawn cycle.** `PawnCycle.Year` derives, per pawn, twelve entries (one per twelfth of the year) of sleep, recreation, eating,
outdoor-capable and indoor labor hours from the pawn's need rates and the twelfth's daylight, and the skill gates of tasks
(devilstrand sowing needs Plants 10). Timetables are not modelled. Each entry is a scheduled day with measured hours,
`NoMealService` when a falling food need has no observed meal service, or `Overbooked` when the measured overheads exceed
24 hours. The latter states supply no modeled labor and are omitted from free-hours valuation. Missing need mechanics
or seasonal facts raise `RequiredFactException`; they cannot turn into zero work hours.

**Event loop.** `ColonyModel.Run` (`ColonyModel.cs`, `ColonyEvent.cs`, `ColonyTrajectory.cs`) steps stocks at piecewise-constant
rates and jumps from event to event: a twelfth boundary, a scheduled project completion or harvest, a stock reaching zero, the
food runway, the horizon. Its cost is the number of events (budget `ColonyModelPolicy.MaxEvents`, 4,096; a run that reaches it
is `truncated`), never ticks, and the same input gives the same events. Food-runway crossings and goal completion use
`ModelEventTime.AtDay` or `NotReached`. `NotReached` describes only that run, including a truncated run, and does not
claim that completion is impossible. The annual growing read and medicine tend rate are required inputs; missing pricing
facts such as the malnutrition table fail the computation.

**Project graph.** `ProjectGraph.Standing` (`ProjectGraph.cs`, `ProjectNode.cs`, `ProjectRun.cs`, `CropGrowthModel.cs`) builds the
nodes in labor-priority order: the current research chain, and per standing field with a crop the sow, grow and harvest nodes;
growth per twelfth of the tile's temperature, with `crop_will_not_mature:<node>` when no growth comes before the crop is due.
Labor goes to ready nodes in that fixed order; there is no search.

**Marginal value.** `ProjectMarginalValue.Of` runs the model as it is and once per research and sowing node with one hour of work
injected at time zero. `ColonyValue` prices each run in capability-days (starvation by the malnutrition stage table, apparel made
by a craft node by the harm model, unassessed with a named finding when the harm model or the gear option is unknown), and the
difference is the hour's value, `dV/dt`. A crop that cannot mature, or a chain whose payoff is unassessed, is worth nothing.

**How the credit reaches tier 1.** `ProjectMarginals.PerUnit` is the value of one research point or one cell sown. The ColonyReview
puts it in `ForecastCoupledContext.Investment`, keyed by `ForecastProjectProgress.Project`, which the research and sowing
candidates carry on their tasks (`ResearchMethodCandidates`, `SowingMethodCandidates`). `ForecastValue.Of` writes
`-(work ticks × progress per tick × value per unit)` to the ledger's `Investment` term, so the block competes with idle labor
in the same capability-days as food, rest and harm. It competes below the safety floors: an outcome that breaks the urgent-zero
floor loses to one that does not, whatever its investment.

**The `colony_model` event.** One row per review that had coupled candidates, emitted by the ColonyReview before the comparison:
horizon, events, `truncated`, `runway_day`, `value`, `nutrition_end`, `completions`, `arrivals` (each resource the run's ledger receives, #3622), `dvdt` per node, `findings` and `elapsed_ms`
([otel signals](../contracts/otel-signals.md#kinds)). The `coupled_selection` rows carry the term as `value.investment`.
