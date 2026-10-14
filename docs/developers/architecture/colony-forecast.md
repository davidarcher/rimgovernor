# ColonyForecast

[Architecture](overview.md) · [Forecast facts](../contracts/forecast-contracts.md)

`PolicyFunctions.ForecastColony` is the pure owner of a baseline and a proposed
commitment delta evaluated against the same observed facts. It returns
`ColonyForecast`; it does not issue orders or certify native legality. The
first component accounts for supplied timed inventory changes. Pawn dynamics,
dependency scheduling and production selection are not integrated. A standalone
activity coordination proof below exercises supplied dependencies and pawn capacity.

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
second production scheduler. It is not yet called by `ForecastColony`, live
Method generation or dispatch; inventory effects remain conditional and the
forecast's unsupported-domain flags remain accurate. Integration must feed
selected activity completion/occupancy into that same baseline/delta forecast,
retain `ResourceDemandOf` as the demand owner, and dispatch via Hands.

A `ForecastActivityProblem` supplies activities, prerequisite IDs, start/end
locations, eligible pawn options, elapsed service durations, directed route
durations and one availability interval per pawn. Every activity is required
exactly once. Service may include pickup, loaded transit and delivery; those
stages must be included in the supplied duration and final location, not counted
again as approach travel. Durations are elapsed ticks, not native work units.
No activity ID or work-family name changes the algorithm. Each pawn shares one
serial occupancy timeline across all work types; different pawns can overlap.
Protected obligations bound availability, while optional baseline work supplies
an explicit displaced-labor rate. Intermittent protected obligations are not
represented by a single interval and must not be flattened into free time.

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
predictable prerequisite completion and divisible baseline work. The proof does
not model full material/equipment contention, coupled needs, interruptions,
production invalidation or dispatch. It establishes combination generation and
conditional valuation, not emergent expert play or production economic scheduling.
Hauling, cleaning and eating helpers remain until their integrated #3128
replacements establish the required native outcomes and deletion acceptance.
