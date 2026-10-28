# Essential-service investment

[Documentation](../../README.md) · [Ownership](persistence-contracts.md) · [Shared demand](../architecture/colony-forecast.md)

Families derive service Needs from the current native frame. Selected service investments reference those Need predicates through `ServiceTarget`; production benches and power topology are shared through `ReviewFacts.EssentialInputs`. There is no separate coverage report, stress portfolio or development-proposal lifecycle. Observed recovery belongs to each family, and shared forecast comparison owns projected benefits.

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
A partially changed or unsupported shell remains unknown. `CompletedConstructionSites`
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
| `production/productive-cooking` | predecessor passes; native-bill revision unverified in game | the planner plans and cooks, a meal is produced, and the level-5 skill is observed rather than boosted |
| `production/development-reassignment` | predecessor passes; native-settings revision unverified in game | a far better cook joining takes the bills from the first cook's Method |
| `production/development-lifecycle` | predecessor passes; native-bill revision unverified in game | the predecessor finished current work, slept, resumed and retired its Method; the revised case checks native work without that ownership |
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
Routine cooking now executes through ordinary bills and numbered work priorities.
The productive cooking cases observe native job identities and actual learning and
output without exact pawn activities. The lifecycle case injects urgent rest and
withdraws the bill; the reassignment case raises another cook's skill and observes
whether normal work settings shift production to it. These revised cases have no
current gameplay result until rerun; earlier Method-owned passes below describe
the predecessor implementation. Forecast worker choices, learning, poison risk and
travel remain conditional planning estimates, not native execution promises.
## Facility choice ownership

`FacilityPlanningRules` owns the existing butcher spot/table ordering, cooking
facility demand, campfire retirement and heat classification, construction-worker
eligibility, powered recreation eligibility, advanced laboratory ordering and
generator availability. It reads the existing `ReviewFacts` frame; Runtime maps
its choice and prerequisite reasons to planner results and commits work. The
food plan remains the one owner of cooking demand. Heat campfires are excluded
from cooking retirement by the current construction claims. Required catalog and
construction reads retain their failures; no replacement fact or strategy is
introduced. Game identities supplied by Runtime use compiler-checked references.

`FacilityPlanningRules` also owns ordered definition prerequisites, the indoor
sleeping deficit and its 64-placement refusal, beer's barrel-before-wort
prerequisite, suite ring stock, shell definition and forbidden-kit stock gates,
and vent facing from the first live oriented site. Definition-only prerequisites
continue to reject any nonzero construction skill; worker-aware prerequisites
use the observed capable builders. `WorkRules.SelectWorkshopProduct` selects the
first buildable or existing product, otherwise the last research candidate ahead
of unavailable alternatives, and counts standing benches of the selected kind.
Runtime supplies the selected product followed by its alternatives lazily and
records only those the selection visits. A product with no usable candidate keeps
the unavailable result; an empty product sequence violates this caller contract.

`SleepingPlanningRules` owns the combined bedroom-owed predicate and the sleeping,
companion-bed, royal and child-room prerequisite branches it visits. It keeps the
existing short-circuit order: a bedroom deficit wins before later upgrade reads,
and absent Royalty requires no title-claim construction facts. A selected path
that needs the construction census, pawn profiles or planning window still fails
when that required read is absent. The same room targets, furniture definitions,
material prices and tier floor style serve review and execution; no second demand
or observation owner is introduced. Direct prerequisite tests cover the selection
order, stock boundaries and required-read failures. Crop field shrink is owned
with the layout geometry policy, separately from these facility prerequisites.

Workshop preparation calls the same `WorkRules.ExistingWorkshopBench` reuse rule
as full selection. It can settle an existing productive bench before candidate
definitions are read. Otherwise preparation discovers the hosting definitions,
the census reads them, and full selection decides staging or research from those
required rows. A further-capacity bench remains usable when no available host can
stage its kind. A provisional request that has not selected reuse is not recorded
as a full workshop decision; the later observed request is recorded for replay.
