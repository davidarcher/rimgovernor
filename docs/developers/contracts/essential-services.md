# Essential-service development evidence

[Documentation](../../README.md) · [Ownership](persistence-contracts.md) · [Shared demand](../architecture/colony-forecast.md)

Ordinary `InspectRounds` publishes `RoundsFindings.EssentialServices`. The pure
`ReviewEssentialServices` reads that review's existing owner assessments and
`DerivedDemand`; observation projects existing production benches and power
topology through `RoundsFacts.EssentialInputs`. Immediate reviews do not refresh
ordinary development evidence. No action, admission, new goal store or native
operation is created by assessment.

## Coverage and ownership

| Service | Authoritative evidence |
| --- | --- |
| Food acquisition and access | the `food/supply` Need; FoodPlan shared allocation and replenishment gap; the food-stores/storage and food-stores/refrigeration Needs |
| Food preparation | the `food/cooking` Need; native production recipe availability and cooking floor, enabled available workers, shared fixed-batch ingredient feasibility |
| Medical care | the `medical/critical` census, `medical/surgery` and `medical/reserves` Needs; served procedures use the surgery owner's failure cap, native per-doctor chances and ingredient availability |
| Usable shelter and temperature | the `shelter/sleeping`, `shelter/safe-area` and `shelter/temperature` Needs and their predicates |
| Power | the `power/basic` Need and the power owner's same-network coverage calculation |
| Material and fuel dependencies | economy/resource's single merged demand; FuelRunway's native consumption gates, stocks and maintenance horizon |
| Logistics | the `layout/routes` Need and native per-facility reachability |

Baseline coverage and stress coverage are separate. Known baseline adequacy or
shortfall survives missing counterfactual evidence. A family with absent evidence
is explicitly unknown; no colonists makes colony services inapplicable. Fuel
consumers retain their own evidence even when no colonists are present.

Single-dependency checks remove one procedure doctor, cooking worker/bench,
reachable logistics worker or power generator at a time. Procedure alternatives
use the existing surgery acceptance rule; meal recipes use native skill floors
and the existing fixed-batch material feasibility owner. Multiple recipes or
workers can resolve recipe eligibility. Eligibility alone does not certify replacement
throughput: successful alternate eligibility leaves the stress
result unassessed until service capacity is established. Normal productive cooking comparison
uses [native skill-dependent durations](skill-learning.md#productive-cooking-in-normal-selection)
for actual scheduled meals. Journal pawn claims are unavailable to
cooking capability checks. Quest-protected patients produce no surgery goals.
Checks have a deterministic scenario budget (128 by default), expose truncation,
and never enumerate combinations or assign failure probabilities.

## Targets and lifecycle

A proposal references the owning Concern and subject. Its stable ID excludes
severity, tick and candidate choice. All requirements are retained when evidence
from several services has the same owner and subject; shared material/fuel demand
is not added again. Stress evidence identifies the unavailable dependency and
remains distinct from a currently failing service. Continue, defer, alternative
chains, capability development, facility restoration and acquisition are classes
of alternatives to consider, not approved actions or ranked winners.

The target is a named owner recovery or mechanical feasibility predicate, never
an arbitrary skill level, historical maximum or unlocked training ceiling.
Proposals are recomputed from fresh facts. `ReassessDevelopment` distinguishes
active, satisfied, obsolete and unknown using the current coverage. Missing
evidence, older reviews and missing stress results cannot establish satisfaction.
Native observations establish eligibility and owner recovery; projected learning
or accepted orders cannot do so. Live consumers must still compare consequences,
protect player obligations and establish actual capacity and legality.

## Explicit bounds

The cooking check establishes eligibility and materials for one ordinary-meal
iteration; it does not establish throughput, delivery completion or a future
food runway. FoodPlan remains the replenishment owner. Ingredient search limits
and incomplete facts cannot certify infeasibility. Medical eligibility uses
native procedure-specific evidence; it does not forecast diseases or create
generic doctor skill requirements.

The current observations do not bind food portfolio channels and thermal/shelter
service recovery to complete counterfactual physical dependencies. Those stress
results name missing owner dependency evidence. Power generator checks reuse
current network output, not a future weather simulation; consumer-to-essential-
service membership remains explicitly unknown. Decorative loads are not silently
classified as food or heat. Fuel per-use/rain-sensitive consumers retain the
fuel owner's unsupported-rate findings. These limits do not erase the baseline
assessments already provided by those service owners.

All evidence is transient and attached to the full review. Existing Concern
ownership and the Method journal remain the only owners of admitted attempts.
No assessment assigns a value to XP or authorizes speculative spending.

## Autonomous food and shelter investment

Normal coupled selection compares supported harvest, delivery, cooking and ingestion
chains and physical shelter completion against continuing/defer over one bounded
horizon. `ForecastCoupledSelection.ServiceInvestments` explains selected work using
existing Concern targets, actual beneficiaries, physical task identities, horizon,
measured avoided exposure and retirement conditions. These transient explanations
carry no extra demand or reservation and award no XP or construction-completion bonus.
All unfinished blueprints/frames participate regardless of origin. Unknown benefits,
unoccupied shelters, unavailable energy, missing materials or work finishing too late
cannot certify service relief. The same observed stock, production promises, journal
claims and native bill reservations constrain every family.

A standing site is optional demand, not a blanket material reservation. Its full
outstanding cost remains in construction diagnostics and acquisition demand; the
shared admission budget reserves materials only for admitted work. Selected supply
tasks publish exact remaining inputs through the existing journal claim owner, with
carried/delivered material removed by fresh native evidence. Native bill holds and
unresolved action claims remain protected. No site is cancelled or demolished when
its investment loses value. Explicit control settings, forbidden targets and native
player-job ownership remain authoritative.

Food work retires at a physical task boundary when fresh beneficiary trajectories
establish no hunger shortfall through the remaining selected horizon. This includes
harvest and ordinary cooking without skill-learning evidence. Missing or truncated
need evidence cannot establish recovery. Shelter work releases at a safe completed
work boundary and ordinary review compares the remaining physical chain again. This
release means reconsideration, not service satisfaction; busy participants keep their
ownership until a safe boundary. Newly observed completion, alternate capacity,
changed demand, costs and control can therefore revise the next choice without a
second goal store. Existing ownership-loss and authority cleanup still apply.

The supported shelter estimate remains conditional on observed weather/power and
modeled stationary use. An untouched planned room retains the observed location's
baseline: the room as it stands, open roof cells at the native no-roof rate (#3534),
else its observed temperature; absent future prerequisites do not erase present exposure.
A partially changed or unsupported shell remains unknown. `CompletedDevelopmentSites`
is physical forecast evidence only, never utility. Clothing delivery retains its
independently used coverage diagnostic; food runway, building placement/layout,
unsupported thermal equipment and medical/power stress diagnostics retain their
existing owners. No speculative crop/animal trajectory or new remedy is inferred.

`ServiceInvestmentTests` and the food/thermal portfolio tests cover paired benefit,
scarcity, timing, control and retirement. Prepared `shelter/service-investment` uses
ordinary review/journal/Hands and requires actual frame work, powered heat and warming
while unrelated construction stays in place. `production/coupled-needs` supplies the
corresponding normal harvest/ingestion evidence; `production/productive-cooking`
covers useful cooking. Registration and compilation do not establish gameplay success.

## Observed evidence and remaining gaps

Real-game runs under the normal planner (no fixture-selected winner) at the #3167 close-out:

| Case | Result | What it establishes |
| --- | --- | --- |
| `production/productive-cooking` | passes (ordinary crew, prepared level-5 cooking skill) | the planner plans and cooks, a meal is produced, and the level-5 skill is observed rather than boosted |
| `production/development-reassignment` | passes (#3167, #3624, #3626) | a far better cook joining takes the bills from the first cook's Method |
| `production/development-lifecycle` | passes (#3167, #3598) | the claim: a tired cook finishes the current meal, is released at the task boundary (never interrupted mid-task), sleeps, resumes the bill when rested, and the Method retires once the demand is satisfied |
| `production/maintenance-range`, `production/maintenance-sparring` | pass | an assigned role floor with a real deficit is maintained by existing range and sparring |
| `production/coupled-needs`, `lifecycle/load` | pass (coupled-needs 2 runs in 5, #3630) | normal harvest/ingestion and load preserve ownership |
| `shelter/service-investment` | passes (#3245): the cold-room heater frame is selected, built, powered and warms the room, with the paired decision explained | food/shelter investment is observed. The cause was search order: a construction root rejected for lacking supported service relief was never paired with the sleep that gives it relief, because hundreds of sleep-time roots sorted ahead of it and spent the budget. Need roots now sort last and an unserved construction root seeds the extension |
| `production/coupled-thermal` | passes (#3246): normal planner closes the roof of the first review's sleeping room, the room warms, and the occupant sleeps there | roof work is reassigned to rested pawns when the tired sleeper's recovered capacity cannot cover it, and each roof job claims its own cell so two pawns never refuse each other at admission. The case runs up to 45000 ticks because the rested sleeper tires about tick 30000; a pre-tired sleeper makes thermal exposure unassessed and the planner chooses no roof
| `service/restart` | passes (25 s; #3268 fixed so the case's routine families reach the game) | a restarted brain re-attaches to the in-game brain and keeps ownership |

Cooking ingredients are staged on a free cell near the bench, never on its interaction
cell, because the finished product drops there and a strict-exact-destination delivery
refuses any occupied cell; a coupled cooking conversion therefore stages only on an item-free cell,
even beside rice staged earlier. A refused delivery now names the blocking item or condition in
its receipt.

The maintenance fixture cannot hold an assigned role below its skill floor in vanilla:
the role is unassigned when its requirement fails. The fixture therefore suppresses that
recache for its own role so a real deficit exists; run it with an ordinary crew, since the
harness's default skill boost would erase the deficit. Predicted-versus-observed rows are in
`coupled_selection`/`coupled_observed` telemetry (see [OTLP signals](otel-signals.md)).
Urgent interruption and satisfaction are observed by `production/development-lifecycle`. The
remaining lifecycle claims are proved by pure paired tests, not real-game runs: stale evidence
is rejected atomically and a lost participant never releases its new owner
(`ActivityContinuationTests`), an unknown or stable need does not churn useful work
(`RecoveryReleasesButStableOrUnknownNeedDoesNotChurnUsefulWork`), a tired participant is released
at the task boundary while a running task is never interrupted, unless its next task is itself an eat,
sleep or recreation service, or leads to one of its own (harvesting the rice it then eats), which it keeps
(`ActivityNeedBoundaryTests`),
and changed skill or decay is covered by `ProductiveLearningTests` and `CapabilityMaintenanceTests`.
Retirement on changed demand is the lifecycle case's deleted bill. A lost participant is the
lost-participant unit test above. The brain's own reassignment is `production/development-reassignment`:
once the cook of the first meal is owned, a far better cook joins (another colonist's Cooking level is
raised to 15). It passes when the first cook's Method no longer owns that cook, the better cook completes
a meal under a coupled Method, and every completed repeat is exactly one Method-owned completion. A
Cooking work priority is not an input: activity cooking ignores work priorities. A worker of a coupled Method
with bills left is reconsidered at its own boundary (its task completed or holding for the next, nothing
carried, observed exactly at its order), whatever the Method's other participants are doing (#3624), and only
while a colonist outside the Method is offered one of its bills faster than its own cooks (#3167): in total ticks, the
walk to the ingredients (the cheapest delivery of them to the bench, else the walk to the bench) plus the
work, or equally fast with a lower known chance of poisoning the meal (the game's rule: room chance r plus
(1 − r) times the cook's `FoodPoisonChance`, on `CoupledFoodConversion.FoodPoisonChance`). The reconsidered
workers then have no retain commitment: the comparison sees them free and the participants still working
keep their claims, and only the reconsidered workers are released, when the selection gives one of their bills
to that colonist; the other participants keep their orders and tasks uninterrupted. A worker some working
participant's task waits on (a prerequisite, an input it produces, or a bill both hold) is not reconsidered
alone: releasing it would strand that participant, so it waits for the participant's boundary. A worker
finishing a task while it has bills left raises that review (`task`, #3624), one event per finished task and
no timer: the worker loop wakes the scheduler step at once, with the clock running (no hold), and the
Method binds that worker no next task until a rounds review has committed, so the review decides before the
worker starts. The colonists with no committed order, running vanilla work (#3669), are free to the comparison. A move never happens mid-recipe.
The assignment search tries for each task the pawns its placed prerequisites went to, then the cheapest
(its work plus its walk to the task, at its opportunity cost, #3624):
a truncated search reaches only the plans its first choices lead to, and trying pawns in id order gave
the first meals to whichever cook sorted first, a level-6 cook beside a level-15 one. A meal that waits on another
colonist's cooking defers its eater by its planned start, not its immediate departure. Doctors are not
trained by surgery on prisoners ([capability maintenance](capability-maintenance.md#no-practice-surgery-3344)).
