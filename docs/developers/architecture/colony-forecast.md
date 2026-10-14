# ColonyForecast

[Architecture](overview.md) · [Forecast facts](../contracts/forecast-contracts.md)

`PolicyFunctions.ForecastColony` is the pure owner of a baseline and a proposed
commitment delta evaluated against the same observed facts. It returns
`ColonyForecast`; it does not issue orders or certify native legality. The
inventory component accounts for supplied timed changes. The construction slice below generates delivery/finish dependencies from observed standing sites and shares pawn/material capacity across commitments. Pawn dynamics and production dispatch remain unintegrated.

## Input and accounting

`ColonyForecastRequest` identifies the observed world/load scope and tick, an
explicit horizon in game days, current facts, existing commitments, a delta,
and the caller's assumptions. The world string must identify the loaded
timeline; it is provenance, not a replacement for native dispatch validation.
`ColonyForecastFactsOf` assembles the available diagnostic facts from
`RoundsFacts`. Runtime selection is connected in #3135.

A commitment identifies a Method's supplied inventory effects by stable ID.
Each effect has an exact resource, relative day and signed units, each numeric
effect/time supporting unknown. Positive units are a delivery; negative units
are a consumption. This is a conditional accounting model: delivery timing is
supplied, not a prediction of native worker completion. Unsupported effects
travel with the commitment as findings.

The delta removes IDs and adds commitments; replace by removing and adding the
same ID. An empty delta means continue. Duplicate IDs, missing removals,
negative times and nonfinite known quantities are rejected.

Both alternatives begin at the observed resource census. Known empty means
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
10 in both alternatives. It says nothing yet about which pawn can deliver it.
`ColonyForecastTests` exercises this comparison and the uncertainty boundaries.

## Diagnostics and model coverage

`ColonyForecast.Observed` retains current-condition diagnostics. Its five-day
`MaintenanceHorizonDays` is independent of the requested plan horizon. The
constant still serves existing supply targets; renaming it does not change
their behavior. Proposed inventory changes do not rewrite these observations.

| Domain | Current component and meaning | Next consumer/model |
| --- | --- | --- |
| Inventory | Timed conditional balances and shortfall findings | #3127 activities; #3133 coupled transitions |
| Food | Existing eligibility/rot-aware stock runway; no future delivery credit | #3133 food production, preparation and eating |
| Power | Current network reserves at observed drain | Future infrastructure/activity effects |
| Temperature | Current sleeping extremes; current breach held over maintenance horizon | Future climate transitions |
| Defense | Current usable turret DPS and explicit gaps | #3129 qualified encounter estimates |
| Construction | Exact-resource stock/need/shortfall in units; no fabricated ETA or interchangeable stone pools | #3127 dependencies and work capacity |
| Fuel | Existing consumer burn and refill-demand diagnostic | Future scheduled supply/consumption |
| Drugs/materials/medicine | Existing realized-use runway diagnostics | Future timed activity effects |
| Animal feed | Existing herd/pasture/feed runway diagnostic | #3133 shared food accounting |
| Pawn time, mood and needs | Explicitly unsupported by this component | #3132 individual transitions; #3125 observed calibration |
| Infrastructure and dependencies | Explicitly unsupported; supplied effects are conditional | #3133 coupled model |

No diagnostic is a calibrated whole-colony trajectory. The returned unsupported
list makes the omitted effects visible even when all inventory balances are
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

#3127 and #3132 provide activity and pawn state, #3133 composes the first
feedback loop, #3134 compares Method bundles and #3135 connects the result to
normal production selection through existing Rounds/Hands. The foundation
is callable and tested; it is not yet a replacement for live meal/supply
selection. No compatibility or permanent shadow layer is required.

## Bounded activity coordination proof

`CompareActivityPlans` is the pure activity component being developed for
#3127/#3128. It evaluates one supplied outcome DAG, not colony demand or a
second production scheduler. `ForecastColony` now calls it for the construction slice below. Live Method generation and dispatch remain unconnected; general pawn dynamics and activity dependencies remain unsupported outside that slice. `ResourceDemandOf` remains the demand owner, and eventual dispatch belongs to Hands.

A `ForecastActivityProblem` supplies activities, prerequisite IDs, start/end
locations, eligible pawn options, elapsed service durations, directed route
durations and known/unknown availability windows per pawn. Every activity is required
exactly once. Service may include pickup, loaded transit and delivery; those
stages must be included in the supplied duration and final location, not counted
again as approach travel. Durations are elapsed ticks, not native work units.
No activity ID or work-family name changes the algorithm. Each pawn shares one
serial occupancy timeline across all work types; different pawns can overlap.
Protected obligations bound availability, while optional baseline work supplies
an explicit displaced-labor rate. Tasks must fit wholly inside one free window; they cannot span a protected obligation. Unknown positions after protected time cannot reuse an earlier location.

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
belongs to #3132/#3133. All participants' costs count. Finishing after the
supplied deadline or availability end refuses that combination. Known zero is
valid; unknown route, capacity, service or recovery excludes only the affected
branch with a finding. Unknown deadline prevents evaluation; unknown alternative
cost prevents selection but preserves evaluated plans.

Plans rank by total labor cost, completion time and a deterministic ordinal
schedule key. A plan must strictly beat the supplied alternative labor budget;
ties retain the alternative, which can mean continuing other work or postponing
this outcome. This budget assumes comparable outcome value and is not a new
calibrated utility currency or a forecast of the alternative itself. A cheap
coordinated plan can still lose because the entire activity is too costly.

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
costs and the alternative budget are assumptions. Just-in-time travel assumes
predictable prerequisite completion and divisible baseline work. The generic proof does not model material/equipment contention, coupled needs, interruptions or dispatch; the construction slice below adds scoped material accounting and reevaluates against current observations. It establishes combination generation and
conditional valuation, not emergent expert play or production economic scheduling.
Hauling, cleaning and eating helpers remain until their integrated #3128
replacements establish the required native outcomes and deletion acceptance.


## Construction commitments and shared capacity

A `ForecastCommitment.Construction` references the geometry of a standing build
owned by the caller's Method. Its stock changes must be empty: material effects
come from that site's observed outstanding needs, so the caller cannot also
charge the build's preview cost. Duplicate commitments for the same geometry
are rejected. `ForecastConstructionContext` supplies the complete current census,
observed worker capability, explicit pawn availability/rates, route/service
estimates and live bill-job ingredient reservations. Nothing is persisted.

The observation projection retains native `WorkLeft` and exact `MaterialsOwed`
from blueprint/frame rows. Work units are divided by the supplied worker rate
to obtain elapsed ticks. Missing work or an incomplete material read stays
unknown; completed or absent sites release future work on the next evaluation.
A known zero-work frame is distinct from unknown work. Existing delivered frame
contents are not charged again. Neither this adapter nor forecast stock effects
change `ResourceDemandOf` or raise another material target.

For each baseline/delta scenario independently, policy generates a delivery
activity followed by finishing, or finishing alone for a supplied frame. It
uses `CompareActivityPlans` for self-service/delegation and preparatory travel.
Finishing requires observed availability, construction capability, native skill
prerequisite and the site's adopted quality floor. Unknown quality floors do
not relax eligibility. Explicit delivery/finish eligibility estimates must
include native legality and work prohibitions; a forecast is never a reservation.

Commitments are considered by tier (untiered first), then ordinal commitment ID.
This is a conservative bounded list schedule, not a search across commitment
orders. The selected allocations trim each participating pawn's remaining windows and update its location for later commitments. Different pawns can overlap; earlier
gaps are not reused. One expansion budget is shared across the scenario.
Truncation and exhaustion stay visible in comparisons/results. Unknown or
infeasible commitments produce per-commitment results and leave independent
known commitments eligible; this is not a colony-wide admission gate.

Material feasibility reuses `MaterialBudget` with bill-job holds and explicit
unknown checks. Standing construction deficits are not subtracted there: this
slice allocates its own exact needs once, sequentially. Other supplied negative
stock effects within the horizon are protected up front, and future positive
effects do not finance delivery. Unknown reservations or unknown consumption
prevent material-dependent scheduling; already supplied frames can still run.
A higher-tier site conservatively waits while any lower-tier census site needs
its material, even when that lower site is not in the requested bundle. Native
per-pawn reachability exceptions to this gate are not yet projected, so this can
reject work native would allow. Equal-tier construction outside the requested
bundle is not reserved; this is a conditional comparison of declared commitments,
not proof of exclusive access against every possible native consumer.

A selected delivery removes outstanding material from free inventory at arrival
at its explicit pickup location. Delivery never creates new colony stock, and
finishing never creates a duplicate inventory benefit. `Construction` results
expose completion day, the assignment comparison, material changes and refusal
reason; original commitments remain reusable input. Unknown/refused outcomes
have no certified completion and no scheduled material effect. Consumers must
read these results as well as inventory findings. Baseline and proposed share
observed stock but allocate independently, so cancellation in the delta releases
its pawn/material use without rewriting the baseline.

Routes, delivery duration (including carrying capacity, pickup/load and all trips),
per-activity recovery cost, availability and work rates are still supplied
estimates with caller assumptions. A delivery assumes one stated pickup location
for that site's outstanding materials. No adapter currently measures these
construction routes or derives a coupled needs schedule; missing estimates do
not receive distance-based or zero-cost defaults. The observed `WorkPawn` and
construction census are real boundary types. The shared review input now assembles standing construction commitments from journal claims, as described below; live selection and dispatch remain unconnected. Native dispatch and helper
replacement remain #3128. No native gameplay improvement is claimed.

`ForecastConstructionTests` covers decoding existing native fields, inventory
consumption once, baseline/delta cancellation, shared versus disjoint workers,
scarce materials and bill holds, skill/tier protection, changed/removed sites,
unknown routes, independent runnable work and the shared search budget.


## Review inputs and pawn windows

`ResourceDemandOf` attaches `ForecastWorkInputs` to its existing `DerivedDemand`.
The normal observation path supplies the observed work pawns, and the review's
existing journal claims identify construction owners. Only claimed geometries
with a current standing site produce commitments; repeated historical claims at
the same geometry produce one outcome. Missing claims or an incomplete census
stay unknown, while completed, removed and cancelled claims stop generating
future work. There is no new demand calculation, journal or saved plan.

`ConstructionForecastRequestOf` consumes that exact demand value, its observed
workers and material reservations. It rejects a tick mismatch and estimates for
unobserved pawns, and replaces copied worker facts with the current observations.
It does not invent labor budgets, routes or free time. The returned request and
forecast retain the same demand object used by supply consumers. This connects
input assembly; it does not call the comparison or dispatch from live assignment
selection. General work-family integration and material/equipment claims remain
#3127 work; native assignment cutover remains #3128/#3135.

`ForecastPawnCapacityOf` derives one timeline per pawn from observed availability,
its 24-hour timetable, local time, remaining useful current-job time and declared
needs/protected occupancy. Its horizon is bounded to seven game days. Non-work
slots are protected; this consumes timetable constraints as observations and does
not install a timetable or make native priorities the new activity selector.
Needs transition estimates remain #3132's responsibility. `ForecastCurrentWorkOf`
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
emits its outputs once in `ForecastActivityPlan.StockChanges`, at its completion
day. Helpers and retrieval do not automatically get another copy. Unknown yield
quantities remain unknown when those effects enter the inventory forecast. The
plan's labor comparison still assumes the caller's declared outcome/alternative;
these effects are conditional, not observed goods or a new utility currency.

`ForecastWorkInputsTests` exercises shared cook/builder capacity, independent
workers, overlapping obligations, cancellation, known-zero versus unknown time,
changed timetables, remaining current jobs, protected gaps and missing endpoints,
multiple outputs with support, review claim deduplication and the shared demand
reference. Construction's existing skill/tier tests remain in force. These tests
do not establish general live work-family eligibility, native equipment claims
or resolution of every planner-local ingredient promise.
