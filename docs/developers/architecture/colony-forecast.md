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
predictable prerequisite completion and divisible baseline work. The generic search
accounts for declared exclusive equipment below; construction adds scoped material
accounting and reevaluates current observations. Portable-tool transport, coupled
needs, interruptions and dispatch remain unmodelled. It establishes combination generation and
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
effects do not finance delivery. An unknown native reservation census prevents
material-dependent scheduling. An unknown declared consumption affects only its
resource; unrelated materials and already supplied frames can still run. Exact
ingredient promises and equipment occupancy are accounted below.
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
selection. General work-family integration and live production of the declared
material/equipment claims below remain #3127 work; native assignment cutover
remains #3128/#3135.

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
Future outputs cannot finance these promises. `MaterialAvailability` exposes free
units separately from inventory trajectories; negative availability means the
declared promises exceed stock. Unknown quantities or consumption timing affect
only that resource. A missing whole reservation census leaves the budget unknown.

The observation projection retains each native reservation's bill identity.
`ReservationBill` is known empty for a promise not handed to a bill, the exact bill
ID when it covers that bill's current promise, or unknown when the association is
unverified. Claimed units include native-held units: a promise of 50 with 20 already
held by the same bill protects 50 in total. Another bill's 20 remains additional.
Only one commitment may link a given bill/resource; duplicate links fail. Missing
native bill identity prevents reconciliation for the affected resource rather than
granting credit. The caller must supply the current outstanding promise, not a
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
unknown. Missing slots, an unread owner census, or an edited bill whose filter may
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

The native bill census includes `UnfinishedWork`: null means unread, an empty
`Item` means observed absence, and a nonempty ID identifies one initialized
unfinished item bound to that exact bill. Its ingredient quantities are embedded
inputs, not spawned stock or live job reservations. Native reads the non-mutating
`BoundUft` reference; it does not call `BoundWorker` or `BoundBill` getters that can
change bindings. An uninitialized item or inconsistent recipe/ingredient read
stays unknown. A destroyed item gives no credit.

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
remaining native holds, duplicate identity and wire unknowns. The registered
`bills/unfinished-materials` case probes native object bindings and lifecycle
callbacks in a prepared lab, including the production ownership check. It does
not demonstrate autonomous pawn crafting, recovery yield, route feasibility or
full coordinated dispatch. Ordinary stock-target cooking remains outside this slice.

### Fixed-batch material admission

Every Method containing a fixed-count production batch checks material availability
inside the journal transaction that binds the Method and creates its plan. The
existing SQLite transaction serializes competing callers, including separate store
connections. A refused transaction leaves neither a plan nor a Method binding.
Two batches proposed from the same review cannot each claim the same remaining
inputs; multiple batches inside one proposal must fit together too.

The full review records replaceable material evidence in its existing Rounds row:
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
resource locks or evidence of autonomous gameplay. Pending construction, trade
sales, indefinite production and other consumers are not yet admitted through
this material check. Native simulation can still change stock between reviews.
The wider claim-overlap migration in #2742 and live timed selection in #3128 remain
open; no existing hauling, cleaning or eating helper is removed.

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
claims. These are policy and journal tests. Native crafting completion, calibrated
utility, joint pawn/time/bench/material scheduling, pending construction and sales
admission, and integrated needs forecasts remain unproved #3127/#3128 work.

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
when net value is nonpositive, explicit unknowns, bounded search and hard stock
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
The same bounded comparison ranks thermal safety, required coverage and condition
before unclassified batch count, then uses ingredient market value and base recipe
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

`CompareActivityOutcomes` extends the activity comparison with optional outcomes.
Each outcome names one existing need, a completion task, a deadline, and an
explicit estimate of avoided loss in baseline labor ticks. The caller owns the
need and its estimate; activity names carry no priority. Alternatives for the
same need are mutually exclusive. Distinct needs must represent independent
losses, not duplicate descriptions of the same benefit.

The comparison generates postponement, individual outcomes and combinations.
It closes each selected completion over its prerequisites and schedules the
union through `CompareActivityPlans`. Shared prerequisites execute once;
participants share the same availability, routes and equipment. It scores
completed, on-time outcomes by avoided loss minus displaced labor and injury
recovery cost. An outcome receives no partial credit. Zero or negative net
benefit retains postponement. Equal scores prefer lower labor cost, then earlier
completion; remaining ties follow stable outcome and activity ordering.

One deterministic expansion budget covers combination generation and all nested
assignment searches. Truncation means best found, not globally optimal; early
outcomes can consume the budget. Unknown benefit/deadline excludes that outcome
with a finding. Unknown routes, capacity or work remain scheduler findings, never
free work. At most sixteen supplied outcomes and the existing thirty-two-task
limit bound the input. This is an in-memory forecast component, not saved intent
or a second source of demand.

`ForecastActivityOutcomesTests` uses two pawns to compare shelter completion and
food harvesting. Protected builder time makes material delivery compete with
harvesting. Larger exposure loss favors shelter; larger hunger loss favors food.
Without that obligation, solo builder preparation leaves the helper harvesting
and both finish. Already-delivered materials eliminate preparation. The same
mechanism covers shared prerequisites, deadline failure, costly recovery,
postponement, unknown estimates and deterministic truncation.

These are supplied synthetic estimates, not calibrated exposure/hunger losses
or a live shelter policy. Candidate graphs and material inputs are
supplied; this comparison does not reserve stock, generate room designs, forecast
weather or eating, or dispatch work. Partial shelter has no modeled benefit.
Existing construction forecasting still evaluates declared commitments in its
own order; it is not automatically composed with this optional-outcome call.
Integration must use one coherent capacity/material frame and replace that
separate allocation, not spend the same pawn time twice. Live adoption also needs
construction/delivery adapters in planner-owned Hands activities, observed
completion and replanning. The construction-labor admission gate and existing
helpers remain until that replacement is demonstrated (#3127/#3128).

## Observed deadlines for shelter and food

`SurvivalNeedsOf` derives per-pawn deadline evidence from a `ColonyForecastRequest`.
The shared work roster and forecast origin must have the same tick. It reuses
`ForecastFood` allocation over the full food census, including competing eaters,
ownership, eligibility and rot. Its food deadline is eligible stock exhaustion,
not time to starvation injury: stomach contents, eating jobs and existing hunger
are not modeled. Incomplete food evidence leaves the deadline unknown.

Cold uses observed cell temperature, worn-apparel comfortable minimum and current
hypothermia with the existing `OutdoorBudgetFor` model and published tuning.
The limit is serious hypothermia, assuming unchanged temperature and clothing.
Already-serious hypothermia has a zero deadline even in a warm cell; this is an
immediate-attention finding, not evidence that treatment is unnecessary. A
known non-growing hazard or a limit beyond the requested horizon is marked not
due. Missing or invalid body readings remain unknown. Heat, weather transitions,
frostbite and recovery trajectories are outside this component.

`CompareSurvivalActivities` joins explicit candidate relief to these needs and
uses `CompareActivityOutcomes` with the `EarliestDeadline` objective. It compares
counts of prevented needs at each deadline, earliest first, then lower labor
cost and earlier completion. Alternatives for one need must agree on its
deadline. This is an explicit ordinal policy, not a physiological conversion of
cold or hunger into labor. It does not claim the two deadline bases have equal
medical severity. `NetBenefit` is unknown in this mode. The default scalar
objective remains available for the earlier supplied-loss proof.

The caller must supply a known labor-cost ceiling; plans at or above it wait.
This ceiling, task rates/routes/availability and material feasibility remain
estimates, not calibrated observations. Unknown ceilings refuse selection. Both
objectives share candidate generation, dependency scheduling and a single
expansion budget. No live shelter or food production caller dispatches this
comparison yet.

Typed candidate effects are assessed on each scheduled plan to establish that completing the terminal task
prevents its named shortfall through the assessed horizon. Unknown effects get
no credit. Finishing a roof alone does not establish thermal protection; harvest
alone does not establish sufficient edible food reaching the intended pawn.
The adapter does not infer either effect from task names. A missed deadline
cannot be credited as prevention; emergency mitigation after harm starts needs
its own candidate/model and remains outstanding. These diagnostics do not create
stock demand, reservations or another durable owner.

`ForecastSurvivalNeedsTests` uses native-shaped fact fixtures to show food stock,
competing consumption, rot, temperature and apparel changing the choice. It also
covers unknown evidence/effects, already-serious cold, postponement, labor caps,
frame consistency and bounded determinism. This proves the pure decision path,
not native reads or completed shelter work. Live candidate generation and temperature-envelope estimation, work-time
estimation, construction/delivery Hands adapters and observed replanning remain
#3127/#3128/#3132-#3135 integration work.

## Quantitative activity effects

`CompareSurvivalActivities` now evaluates typed food and shelter evidence on each
scheduled combination before ranking it. The supplied `PreventsShortfall` boolean
has been removed. The existing bounded outcome search invokes a pure assessment
of each complete plan; unknown/refused effects remain findings. There is no
second scheduler, demand store or order writer.

Food effects name a terminal activity output and its exact resource. Quantity
comes from the activity's declared output; nutrition per unit comes from the
native item catalog. Conditional eater/holder/expiry evidence is passed through
the existing food allocator, including all competing consumers. Each physical
terminal output is allocated once even when several outcomes name it. Starting
at each pawn's baseline food-exhaustion time, the assessment consumes its allocated
shares in expiry order and requires uninterrupted coverage through the horizon.
An output not yet delivered at the end of existing coverage gets no credit;
expiry is relative to the forecast origin. Missing eligibility, quantity,
nutrition or expiry is unknown. This checks conditional edible availability,
not ingestion, food poisoning or starvation physiology.

Activity `Inputs` consume exact quantities at service start; `Outputs` add exact
quantities at completion. The survival assessment uses the existing
`ForecastMaterialBudgetOf` over observed stock, native holds and the request's
committed claims, rebuilt for every alternative. All scheduled activities share
that balance. A positive-duration prerequisite's output can fund a dependent
input at the same tick; zero-duration output follows consumption and cannot fund
itself. This enables harvest/cook/delivery chains and shelter/food fuel contention.
Inputs and outputs also appear as signed conditional stock changes on the plan.
The general activity scheduler alone does not certify material feasibility;
this effect assessment owns that check for survival selection.

Food moved through a chain must be consumed and re-emitted rather than invented
again at each stage. An intermediate edible output consumed later by the selected
plan cannot also receive food-relief credit. Consuming resources already counted
in the baseline food census returns an explicit unknown pending timed food
reallocation; it cannot reuse the same nutrition as both baseline meals and recipe
inputs. Expiry-order allocation is conservative and does not revisit an early
skipped delivery after later food extends coverage. Partial relief receives no
full-horizon prevention credit.

Shelter effects declare enclosure/roof status, required completion activities,
a location and a minimum/maximum temperature estimate valid through the horizon.
Each beneficiary must have a known comfortable range, and the entire estimated
range must fit inside it. Required work must finish by the terminal completion;
the beneficiary's last scheduled activity must reach that shelter by then.
Missing heating dependencies, missing arrival, a cold roof or an unknown/short
thermal estimate cannot certify protection. Staying at that destination through
the horizon is an explicit candidate assumption; future occupancy changes are
not simulated. Heating fuel/power must be represented in the candidate's inputs
and prerequisites where required.

There is still no native room-heat trajectory estimator: enclosure, future room
temperatures, continued occupancy, output eligibility/expiry and recipe yields
remain conditional candidate evidence. This layer checks their sufficiency and
shared accounting; it does not establish that those estimates will occur. Live
candidate generation, room heat/power/fuel transitions, native completion and
Hands construction/delivery dispatch remain integration work. The existing
construction admission gate and helpers are unchanged.

Scenario tests cover insufficient nutrition, wrong eaters, rot before the horizon,
shared-output allocation, missing heating/arrival, unknown temperature/material
facts, shared fuel after commitments, and an actual harvest/cook/delivery DAG.
They prove policy decisions from fixtures, not observed gameplay completion.

## Food candidates from acquisition and recipe observations

`GenerateFoodActivities` builds candidate graphs from existing acquisition,
crop-catalog, production-bench and exact recipe-input observations. The shared
forecast roster and the candidate frame must have the same tick. An available,
unclaimed harvestable food plant creates one harvest task shared by its raw and
cooked alternatives. Trees, hunts, immature plants and already claimed/designated
sources are not harvested again. Source yield and the crop's native harvest work
supply the quantity and work; neither comes from a plant-name heuristic.

Raw delivery remains an alternative. For each usable observed bench and available
ordinary-meal recipe, the generator checks every exact ingredient slot against
the harvested resource. This slice supports recipes whose complete ingredient
set can be supplied by that single resource and which have one ordinary product
and no reusable unfinished item. It generates the maximum whole recipe batch
that this harvest can fund; it does not mix harvests or enumerate smaller batches.
The existing recipe catalog projection now includes native product counts and
base recipe work. Cooking quantities are product count times batches, not an
assumed one meal. Other ingredients and unsupported/unknown recipes are excluded
rather than guessed.

The generated chain is harvest, delivery to the bench, cooking, then delivery to
the requested destination. Exact input/output quantities preserve material flow;
the normal effect assessor checks joint stock and claims. Recipe variants and raw
delivery reach the same existing bounded comparison before one is chosen. Native
cook skill floors exclude unqualified workers; current worker availability is
required for every operation. Cooking claims the exact bench as shared equipment.
Task bindings retain source and bench/recipe identities for inspection; they are
not executable Hands orders.

The graph's service times derive from observed work and supplied worker rates.
Initial travel routes, native worker eligibility, per-resource carrying limits,
complete-trip costs, recovery estimates and bench availability windows remain
explicit inputs. Carrying work scales by the ceiling of quantity divided by
carrying capacity. A bench's current usable flag is not a guarantee of continued
power/fuel: the supplied equipment window must cover operation, and this slice
does not forecast those transitions. Destination eater/expiry evidence is still
conditional. Unknown estimates cannot become zero-time operations.

Generation has its own explicit inspection budget and respects the scheduler's
32-task and 16-outcome limits. It reports truncation; omitted candidates are not
proven inferior. Search retains its separate expansion budget. No graph is
returned when no relief candidate is generated. Missing kitchen information can
leave known raw alternatives available, while an unknown source census cannot
be treated as an empty known harvest. Findings are retained on the generation
result and must be considered alongside comparison findings.

`ForecastFoodCandidatesTests` exercises observed-input fixtures through generation
and selection: a small harvest requires cooking, a larger harvest favors cheaper
raw delivery, native product counts affect output, and unavailable workers,
missing transfer/bench evidence, carrying limits, immature/claimed sources,
unsupported ingredient combinations and truncation are explicit. The catalog
projection compiles against native definitions; these tests do not prove a live
crop census or native execution. The generator is callable policy, not yet wired
to live Rounds dispatch. Multi-source/multi-ingredient planning, variable batch
sizes, native worker yield/rate calibration, power/fuel transitions and Hands
harvest/hauling/cooking integration remain #3127/#3128/#3135 work. The
[activity execution contract](../contracts/pawn-activities.md) now includes an
exact one-trip delivery adapter alongside harvest, plus a one-iteration cooking
adapter for exact single-resource ingredient stacks and an existing selected bill.
Their native cases check physical output and explicit revision after refusal,
with fixture-selected assignments.
It does not close the live forecast loop: observed output rebinding, carried-item
recovery, mixed-ingredient cooking, calibrated live candidate inputs and normal Method
admission/Hands dispatch still need integration. The automatic hauling/cleaning/
eating helpers remain in place until that replacement is proven.
