# Colony labor: implementation and evidence

[Collection](README.md) · [Labor requirements](../../openspec/changes/native-led-labor-execution/specs/colony-labor/spec.md) · [Design](../../openspec/changes/native-led-labor-execution/design.md)

## Intent and evidence ownership

The maintainer confirmed native-led routine execution, full control of settings and meaningful direct interventions in the 2026-10-26 discussion recorded in the [proposal](../../openspec/changes/native-led-labor-execution/proposal.md#decision-provenance-and-review-boundary). RG-LABOR-001–008 live in that change's delta pending explicit synchronization. This page owns implementation inspection and proof limits, not new requirements. Other requirements retain their IDs and ownership.

## Current evidence

Native need assessment now applies the same evidence standard to selected and
omitted ordinary services. Previously a selected hypothetical meal, sleep or
recreation allocation could certify recovery while the omitted service remained
unprojected. `NativeNeedBounds` computes the existing no-service calculation once
per comparison from current needs and the horizon. Every portfolio uses this
common deprivation bound: zero exposure remains supported where no service is
needed, while future need levels and recovery remain unassessed. Unsupported
service-dependent mood cannot become a rescue, and healthy bounds preserve
independent supported mood/outfit comparisons. The detailed scenarios remain
unchanged. Six new `NativeNeedServicesTests` cases compare perfect hypothetical
recovery against omitted service under safe/unsafe bounds; its three existing
shared-selector cases now check selected recovery too. The incumbent regression
retains its exact two-service composition proof while checking that comparison
keeps only the current commitment. Existing cold-clothing and mood-effect
selection tests still pass. The final gate passed 4,207 domain tests and 30
workflow tests, with native build/probe/orphan checks (42s); strict OpenSpec passed
all five items. No new gameplay result is claimed. Task 3.3 remains incomplete
for the general productive baseline and settings alternatives; this does not
implement general intervention materiality or complete a migration checkbox.
Rules 7–9: comparison owns the pure common bound, binding retains only current
legality classification, no new model run/state owner/demand owner, and neither
selected nor omitted future service is credited as observed recovery.
Complexity: added a once-per-comparison bound and six regressions / removed
allocation-dependent recovery credit and the redundant future-service selection
expectation / orphans left: none. Confirmed food requirements remain unchanged.

Existing exact Methods now return ordinary services to native selection at task
boundaries using the same current-response classifier as fresh binding. A waiting
or terminal participant releases its exact ownership without retaining later
work; independent running orders remain identical. When remaining work for
another participant depends on it, the coordinated attempt ends through existing
exact-owner cleanup, without erasing an unobserved prerequisite. Running work is
not interrupted. Unlisted demand permits native handback and terminal cleanup,
but no new work/preparation or unchanged-order revision. Seventeen new
`NativeServiceContinuationTests` cases cover available food/rest/recreation,
uncaptured rest/recreation, missing/access observations, in-flight work,
dependent cleanup and both waiting/terminal unlisted boundaries. Four old
exclusive-service expectations now assert native handback. The final gate passed
4,201 domain tests, 30 workflow tests, native builds, probes and orphan checks
(1m21s); strict OpenSpec passed all five items. No native physical path changed
or new gameplay pass is claimed. General baseline/intervention tasks and
productive migrations remain open; no task checkbox is completed by this partial
self-service migration. Rules 7–9: policy reuses canonical needs and the existing
continuation/journal cleanup owner, adding no state or optimizer and crediting no
unobserved service. Complexity: added boundary service reconciliation,
cleanup-only handling for unlisted demand and regressions / removed unconditional
unlisted continuation exit and obsolete native-service hold expectations /
orphans left: none. Confirmed food requirements remain unchanged.

Fresh ordinary self-service now stays native inside productive portfolios too.
The shared dispatch projection removes routine eating, rest and recreation,
later work for those participants, and transitive dependencies; independent work
remains eligible. An observed service already in progress sets that participant's
boundary to now, so a later hypothetical service allocation cannot authorize an
earlier interruption. The original detailed plan remains conditional evidence.
Production binding requires the observed colony context; the former nullable
context entry and its test-only helper are removed. Eight new
`NativeExecutionBindingTests` cases cover available/in-progress food, rest and
recreation, plus rest/recreation without a captured service object. Existing
sleeper and ordering tests now distinguish native-response projection from the
lower-level ordering rule. The gate passed 4,184 domain tests, 30 workflow tests,
native builds, contract probes and orphan checks (1m36s); strict OpenSpec passed
all five items. No new native gameplay result is claimed. Tasks 3.3, 4.1, 4.2
and the remaining productive/incumbent migrations remain unfinished. Rules 7–9:
one pure binding projection reads canonical observations and is shared by
interruption accounting and owed-work derivation; no new optimizer or state
owner, and current service is no guarantee of future completion. Complexity:
added per-allocation service projection, current-service boundary and regressions
/ removed the portfolio-only dispatch guard, nullable-context binding path and
obsolete test helper / orphans left: none. Confirmed food requirements are unchanged.

Ordered-work claims and retirement now follow observed physical execution. The
shared census previously omitted acknowledged queue actions, and normal review
retired their Methods from the dispatch receipt. It now freezes canonical native
queue observations in the existing admission row (journal schema 63), retains
only remaining matching targets and their pawn, and keeps the Method while those
claims remain. Missing pawn rows, malformed matching records or same-tick observations retain known claims
without inventing material consumption. Finished/replaced identities release
claims; neither that release nor retirement credits food or predicted output.
`OrderedWorkHandsTests` exercises real admission, dispatch, fresh review/codec
replay, partial claim retention, competing-target refusal, physical retirement
and subsequent admission. `OrderedWorkTests` covers partial, stale, missing,
malformed and replaced observations. The full admission census retained an actual
loaded queue in `control/ordered-work-cancel-reload` (PASS, 23.420s;
`.rimgovernor/labor-queue-admission-census/`). The final package passed the normal
journal/Hands `control/ordered-work-lifecycle` case in 20.635s
(`.rimgovernor/labor-queue-retirement-final/`); that case proves physical execution,
while the journal regression proves retirement. Both results retain package
hashes and dirty source based on `1dbbac4fe`. The gate passed 4,176 domain tests,
30 workflow tests, native builds, probes and orphan checks (1m23s); strict OpenSpec
passed all five items. Task 4.2 remains partial: intervention selection and
policy-driven cancellation/continuation are unfinished. Rules 7–9: policy derives
claims, the existing journal owns retirement, and native owns actual jobs; no new
state owner or scheduler. Complexity: added one canonical observed queue field,
its journal codec and lifetime regressions / removed dispatch-only claim release
and premature queue Method retirement / orphans left: none.

Finite native queue mechanics are now exercised through normal journal/Hands
dispatch in `control/ordered-work-lifecycle`. The finished case passed in 28.458s
on 2026-10-28 UTC: exact jobs 5 and 6 started in that order, both ended
`Succeeded`, both designated walls were removed, and the pawn returned to an
unforced native job with an empty queue. The ordinary pawn read retained those
identities/outcomes; repeating the request under a fresh action key returned
`Issued=false` and the same completed queue. The fixture stages legal independent
jobs and a concrete purpose, so this proves native mechanics, not policy choice
or intervention materiality. Result:
`.rimgovernor/labor-ordered-work-final/control/ordered-work-lifecycle/result.json`.
It records dirty source based on `d8e3bc439393`, package file hashes, the `work`
family and the three named fixture operations. Tasks 4.3 and 4.4 are complete;
4.1–4.2 remain open, including intervention premises and policy-driven continuation
or cancellation.

Two further native cases establish partial cancellation and actual save/load
reconciliation. `control/ordered-work-cancel-reload` passed in 19.381s: jobs
7/8/9 retained `Succeeded`/`Running`/`Queued` across a real save/load, alongside
unrelated queued job 11. Cancellation retained job 7's success, cancelled only
8 and 9, and started unrelated job 11. `control/ordered-work-replaced-reload`
passed in 12.453s: replacement made job 8 `Gone`; reload preserved that outcome,
job 9 remained queued, and cancellation removed only 9 while unrelated current
job 11 survived. Repeated cancel and install requests changed neither execution
nor outcomes. Results are under `.rimgovernor/labor-queue-cancel-reload/` and
`.rimgovernor/labor-queue-replaced-reload/`, respectively, with package hashes
and dirty source based on `a2a678e67`. These cases use the typed native order
boundary and stage unrelated jobs explicitly; the earlier lifecycle case and
Hands regression establish the journal dispatch route. They make no planner
selection or intervention-justification claim. The gate passed 4,175 domain and
30 workflow tests, native builds, probes and the orphan check (2m50s); strict
OpenSpec validation passed all five items. No production execution or persistence
owner was added. Rules 7–9 retain the existing owners and distinguish missing
execution from success. Complexity: added two scoped native cases and fixture
readback for loaded identities / removed stale fixture object reads / orphans
left: none.

`OrderedWorkTests` checks immutable request sequencing, stale/altered/reordered
requests, partial completion, missing/replaced identities, serialized outcome
reconciliation, journal/wire codecs and pending pawn/target claims.
`OrderedWorkHandsTests` proves real admission, claim conflict, fresh inspection,
typed transport and duplicate journal-receipt protection. The saved provenance
owner is one latest native record per pawn; vanilla alone owns the physical queue.
Routine portfolio selection does not generate this action.

The native run exposed and repaired two normal-review obstructions: construction
pricing now reads native material MarketValue even when its display is hidden
(the observed Gravcore case), and forced/queued work retains accessible service
mechanics without gaining voluntary-dispatch eligibility. Otherwise a finite
order could hide meals and deadlock first-layout planning at tick 0. Focused
regressions cover both distinctions; the final native result demonstrates the
clock progressing through the real review. An unrelated MonoMod/CoreCLR startup
crash before setup is tracked by existing #3190; that failed attempt established
no queue outcome. The final gate passed 4,175 domain tests, 30 workflow tests,
native builds, contract probes and the orphan check (1m21s); strict OpenSpec
validation passed all five items. Rules 7–9: shared admission owns claims, no new
optimizer or demand owner was added, and dispatch/unknown disappearance cannot
become physical completion. Confirmed food requirements remain unchanged.

Fresh routine research and sowing bindings now stop at native execution. Project
selection, configured growing zones/planters and numeric settings make the work
available; their detailed task graphs remain conditional investment scenarios.
`NativeExecutionBindingTests` takes both real providers through shared comparison
and binding: a valuable research/sowing forecast can be selected without a pawn
order or an owed assignment. Mixed-plan checks retain independent earlier work,
drop later participant work and transitive dependencies, and reject a redundant
meal left behind after native work is removed. The same dispatch projection feeds
forecast interruption accounting and the work-assignment Need. Existing active
Methods still retain their observation/reconciliation lifecycle; no physical
research/sowing output, general intervention gate or complete migration is claimed.
The finished checkpoint passed 4,168 domain tests, 30 workflow tests, native builds,
contract probes and the orphan check (45s); strict OpenSpec validation passed five
items. The sleeper dispatch regression now supplies the same context as production
and retains native rest when only a redundant service remains. Rules 7–9: one pure
dispatch projection, existing research/field demand owners, no new state store,
and no assumed native output or completion. The previous external-work projection
and duplicate public service guard were replaced through every reader.

The shared comparison now also treats eligible rest/recreation with no captured
target as an unprojected native response. `NativeNeedServicesTests` exercises
both through the real selector: the explicit no-service scenario still has
deprivation, while the native comparison retains unassessed exposure and mood
rather than predicting failure or inventing recovery. The target census enumerates
beds and recreation objects; `NativeNeedsForecast.SleepOf` separately captures
the game's bed-or-ground rest choice. Known sleep blockers remain ineligible.
This closes another uncertainty case within task 3.3, not the general productive
baseline or intervention migration, and makes no new native gameplay claim.
Verification passed 4,164 domain tests, 30 workflow tests, native compilation,
contract probes and the orphan check (1m41s), plus all five strict OpenSpec items.
Rules 7–9: the shared comparator owns this assessment; no second demand owner,
store or execution lane was added, and absent targets become neither recovery
success nor a predicted native failure.

Reserve prevention now uses accessible runway and supported conditional recovery
leads, including native-committed crops too late for current demand credit.
`FoodReserveStateTests` covers recovery before/at/after runway, irrelevant closed
uncertainty, relevant committed uncertainty and current shortages without a known
recovery. `FoodStoresFamilyTests` exercises a preventive reserve release above the
seasonal minimum through current-review admission, journal and real Hands, with a
boundary acknowledgment and duplicate-batch check. Loss of the stock-protection
channel prevents new holds but cannot prevent release. These checks prove access
coordination, not crop completion, recovery rate adequacy or pawn intake. Task 3.3
remains partial; the general productive baseline and intervention migration remain
unfinished. No new native gameplay result or food requirement change is claimed.
The finished reserve checkpoint passed 4,162 domain tests, 30 workflow tests,
native compilation, contract probes and the orphan check (1m8s); strict OpenSpec
validation passed all five items. Rules 7–9: policy owns the recovery comparison,
existing food demand and shared admission own access, and uncertainty is scoped
without inventing native output. No store, second planner or native trajectory
was added; the former Runtime recovery classifier was removed.

Shared comparison now withholds future deprivation attributed solely to omitting
captured available/running native need service, rather than using a final binding
guard alone. `NativeNeedServicesTests` checks food/rest/recreation uncertainty
through the real selector while preserving the explicit conditional no-service
scenario and rejecting supplied redundant bindings. Physiological zero-penalty
bounds remain usable; possible native sleep can still invalidate all-awake mood
before exhaustion. `ForecastConditionalMoodTests` and `ForecastObservedMealTests`
exercise the separate conditional calculations. Native rest uncertainty disables
the starvation search bound; the pruning equivalence and thermal investment
regressions retain independent supported comparisons. Room, recruitment, pawn,
classroom and productive-learning checks pass without deleting their conditional
investment mechanics.

`WorkShortfallRules` no longer treats a need-service alternative's whole
harvest/cook/delivery chain as independently demanded production merely because
the service was unselected. `NativeWorkShortfallTests` covers that false demand
and retains independently demanded delivery competing for capacity. The food
family and stock-target bills remain demand owners. The required gate passed
4,155 domain tests, 30 workflow tests, native compilation, contract probes and
the orphan check (1m27s). This remains partial task 3.3: uncaptured responses,
supply/runway and recovery-time comparison, productive/dependent execution and
the meaningful-intervention boundary remain unfinished. No additional native
gameplay result is claimed, and confirmed food requirements are unchanged.

The shared selector and binding entry point now refuse a portfolio consisting
solely of ordinary eating/rest/recreation when current observations show native
access or service in progress. `NativeNeedServicesTests` exercises shared
comparison, direct binding and a supplied detailed counterfactual, including
unsupported future mechanics, absent observations and actual current service.
Detailed service scenarios remain conditional search evidence for complementary
investments; they cannot independently justify these redundant service orders.
The meal regressions retain nutrition, expiry and repeated-intake calculations
as what-if evidence. Explicitly non-rotting stored food now participates in that
calculation; missing expiry still refuses intake. The required gate passed
4,154 domain tests, 30 workflow tests, native compilation, contract probes and
the orphan check (1m20s). These are partial task 3.3 checks, with no new native
gameplay claim: general native baseline, productive dependencies, incumbent
migration and intervention materiality remain incomplete. Shared malformed native
reference-cache files encountered during verification were repaired; the tooling
defect is tracked separately in [#4042](https://github.com/davidarcher/rimgovernor/issues/4042).

Inspection at local checkpoint `ff5cfc77b` plus the existing worktree edits established native settings and work-creation operations, strict coupled-job interception, passive activity observations and shared lost-owner continuation. The first milestone below replaces the strict lifecycle interception and proves scoped feeding outcomes. All general routine migration entries remain pending. [Food evidence](food-security.md) retains earlier fixture failures and the distinct executed bottle/nursing results. #4040 remains open until the verified implementation lands.

The existing `ActivityContinuationTests.LostParticipantDoesNotAuthorizeReleasingItsNewOwner` and `KnownOwnershipEndRetiresIntentWithoutTouchingAnotherAssignment` exercise pure ownership/journal semantics. They do not prove that the native hooks permit takeover or that worker invalidation reaches the live game. Existing numeric priority, bill and designation operations establish available mechanisms, not their successful composition under this new design.

The shared consequence comparator ranks a safety floor only when both outcomes
assess it. An unread urgent-zero, death or break-risk floor cannot win as a zero
risk or prevent supported investment comparisons. Goal-route findings distinguish
an unassessed route from a proven open or infeasible route. `ForecastValueTests`
covers both ranking directions for all three floors; `GoalValueTests` exercises
goal selection and explicit route uncertainty through the production comparator.
This corrects uncertainty handling within task 3.3; it does not yet replace the
empty-portfolio execution baseline or complete intervention admission. No new
native gameplay claim follows from these policy checks. The required gate passed
4,136 domain tests, 30 workflow tests, native compilation, contract probes and
the orphan check (1m39s); strict OpenSpec validation passed five items.

Settings reconciliation checks all written fields, including the exact timetable
and allowed-area intent, in pure `WorkRules.WorkAssignmentStale`. The journal
regression in `WorkAssignmentsNeedTests` changes a timetable while keeping its
priority and total sleep slots constant: stale Pending/Prepared writes cancel,
their claims release, and a fresh Method dispatches the new typed timetable and
numeric priorities. Unchanged writes retain their claims. The required gate
passed 4,141 domain tests, 30 workflow tests, native compilation, probes and the
orphan check (2m17s). This is partial task 3.4 evidence.

The automatic planner now generates regular sleep/recreation/work preferences
through `PawnRules.RoutineTimetable` and `WorkRules.NativeSettingsChanges`.
Observed recovery rates and approach time size whole-hour service blocks;
modeled awake-mood windows can shift their phase. Missing service evidence leaves
the timetable unassessed while independent priorities remain available.
`NativeTimetableTests` covers sizing, phase, exact readback, numeric mode and a
timetable-only difference waking the existing Need. `WorkAssignmentsNeedTests`
passes generated preferences through shared admission, journal dispatch and
typed intent, without claiming completion from dispatch. `NativeSettingsHandsTests`
exercises real Hands and `ExecutorJournal` with boundary fault injection: a stale
generation inspection writes nothing, a fresh retry inspects again, an exact
accepted settings receipt completes without duplicate writing, and a transient
refusal releases the claim so the same Method can admit recalibrated preferences.
Existing `WorkOccupancyTests` covers changing work demand and useful existing
assignments. Native authority enables numeric mode; policy waits for its readback.
Native `WorkSettingsActionHandler` already validates live prerequisites and
reads back exact timetable application; the settings intent has no separate
settings CAS field. Stale world/generation guards remain in Hands. These checks
complete task 3.4, not native execution or the general migration. The required
gate passed 4,149 domain tests and 30 workflow tests, native compilation, contract
probes and the orphan check (52s). No new native gameplay result is claimed.

The first implementation removes advance caregiver release and holds capacity only
for actual care. It removes the strict constant/override/opportunistic/finalizer
interception and indefinite dependency-wait driver. An actual replacement job
detaches the exact execution handle and sends the existing pawn invalidation.
`NativeTakeoverReconcilesDependentWorkThroughTheJournalWithoutReleasingTheNewOwner`
and `NativeTakeoverInvalidationWakesTheWorkerAndDispatchesOnlyExactRemainingRelease`
cover journal cleanup and synchronized worker wake, including remaining-owner
revisions. The required gate passed 4,064 domain tests and 30 workflow tests,
production/fixture compilation and contract probes. This is not a completed
routine migration. Native results follow; [food evidence](food-security.md) records
the first bottle attempt's partial outcome and the passing bottle/nursing runs.

All of these native cases executed on 2026-10-26 against the worktree's private
package at dirty `ff5cfc77b`, source tree
`f8b7bc3f1f433ae51a7c934204ff9350eb3268d579aa27363e0aedf2ff054b0d`:

| Scoped native claim | Executed result / local provenance |
| --- | --- |
| Considering an alternative retains the handle; actual takeover detaches it and ending the replacement does not restart the old attempt | `control/activity-native-takeover`: PASS, 17.54 s; `.rimgovernor/labor-native-takeover/control/activity-native-takeover/result.json` |
| Native involuntary sleep, vomiting, imposed sleep and subsequent ordinary replacement use actual takeover | `control/activity-native-overrides`: PASS, 2.033 s; `.rimgovernor/labor-native-overrides/control/activity-native-overrides/result.json` |
| A dependency wait offers no indefinite governor job and permits native takeover | `control/activity-dependency-yields-native`: PASS, 1.808 s; `.rimgovernor/labor-native-dependency/control/activity-dependency-yields-native/result.json` |
| Initial harvest yields to native ingestion and sleep | `control/pawn-activities`: PASS, 2.724 s; `.rimgovernor/labor-native-sequence/control/pawn-activities/result.json` |
| Bottle feeding and nursing after real journal/Hands-owned competing harvest; actual intake without malnutrition and protection through post-feeding return | Separate PASS results, 24.94 s and 23.751 s, with complete provenance in [food evidence](food-security.md) |

These cases establish lifecycle mechanics and the staged feeding response. They
do not establish a native throughput baseline, justify direct interventions, prove
ordered native queues or close any routine migration entry. Existing exact
generators and timetable-based throughput sizing remain
identified work for the later groups. The unrelated observed layout logging
exception is tracked as [#4041](https://github.com/davidarcher/rimgovernor/issues/4041).

## Routine execution migration inventory

Task 3.1 uses the existing shared demand and worker census plus the bill
dispatcher's derived window. `NativeDeliveryLedger` now counts each recipe's
products by bill identity at creation. `ObserveOrder` reads only that order's
exact bills and product; food acquisition credit excludes recipe transformations.
`NativeThroughputTests` covers consumption versus production, imported/delivered
stock, missing observations and rebaselining on load, rewind, counter regression,
order, bench, bill, worker availability, priority and speed changes. Rates are
product units per game day; the existing world reset owns derived windows and the
existing native load token owns counter epochs. No new durable ledger or model
run was added. The gate passed 4,114 domain tests and 30 workflow tests (1m23s),
native production/fixture compilation, contract probes and the orphan sweep;
strict OpenSpec validation passed five items on 2026-10-26. This is policy/boundary
evidence; no new native gameplay case is claimed for these counters yet. Routine
settings/output acceptance remains in group 5. Rules 7–9: policy owns calibration;
Runtime passes the canonical ledger; missing production is `CensusUnread`, never
zero. Complexity: added one recipe-output kind and typed derived production basis /
removed net-stock-as-production inference / orphans left: none.

The next capacity increment removes interruptibility-as-free accounting and
reservation-holder eviction. Useful observed native work is occupied; without a
remaining-time estimate its capacity is unread. Every observed job target stays
claimed. Native acquisition uses vanilla reservation checks and cannot end another
holder's job. `UsefulNativeWorkIsOccupiedAndKeepsItsRoleWithoutInventingDuration`
and `UsefulNativeWorkOccupiesCapacityAndRetainsItsTargetReservation` cover the
policy consequences. The obsolete flag is deleted through the native observation,
typed facts and admission codec, with journal schema 61. The required gate passed
on 2026-10-26 (59 s), including native production/fixture compilation and contract
probes. Earlier native gameplay results above precede this increment. Task 3.2's
conditional replacement of timetable throughput sizing follows below.
Rules 7–9: occupancy is a pure shared decision, native reservations retain legality,
and unavailable duration stays named rather than becoming zero. Complexity: added
0 execution paths / removed target-holder preemption and one boundary flag /
orphans left: none.

Task 3.2 replaces timetable-only hours in bill sizing, crafting arrival, quest
deadline capacity and construction work-day pricing with `NativeLaborCapacity`.
It reuses the canonical needs census and `NeedServiceRules`' existing steady
service assumptions, sharing the continuous/meal mechanics with `PawnCycle`.
Service approach, immediate recovery and observed continuous-service remainder
consume potential labor. Active care and competing untimed work are named
unassessed states. A same-type native worker can have conditional production
potential while remaining occupied for shared admission; potential is not idle
capacity. Unsupported craft sources retain findings without discarding supported
trade; missing deadline capacity abstains. The old profile-hour property and
schedule-count extension are removed. Slots remain native facts because settings
and explicit timetable protection still read them.

`NativeLaborCapacityTests` verifies 24 scheduled work slots cannot finance a
24-hour production day, current hunger/rest and service travel reduce potential,
ongoing continuous service consumes time above its trigger, active care/unknown
duration do not become free labor, and bench/craft consumers use the same estimate
with skill gates. The required gate passed 4,121 domain tests and 30 workflow tests
(1m10s), native compilation, contract probes and the orphan check; strict OpenSpec
validation passed five items on 2026-10-26. No additional native gameplay result is
claimed for this policy increment. These are conditional estimates, not a native
future schedule or completed output. Native-baseline comparison, settings
admission, intervention materiality and routine migration remain later tasks.
Rules 7–9: policy owns capacity math; Runtime only supplies canonical facts;
unread inputs name a state and cannot finance work. Complexity: added one shared
conditional estimate / removed timetable-only sizing, the profile-hour property
and its extension / orphans left: none.

The next settings increment removes the execution-claim exemption from ordinary
priority planning. An open execution Method does not freeze its pawn's settings;
the settings Method continues to claim its separate settings resource. The journal
regression admits priorities and a 24-slot timetable for the same pawn while its
execution Method remains open. Numeric-mode readback still gates planning. The
unused duplicate `ReviewFacts.WorkClaims` view was removed; canonical shared
claims remain in `WorkView`. Native `SetPriority` can interrupt ordinary matching
work when a priority becomes zero; observed takeover reconciles that result.
Applied settings are not output. Task 3.4 remains incomplete pending the remaining
comparison, stale-read and retry integration. The required gate passed 4,120
domain tests, 30 workflow tests, native compilation, probes and the orphan check
(1m38s). No new native gameplay claim is made. Rules 7–9: policy chooses settings,
Runtime supplies facts and admits Methods, and occupation remains independently
observed. Complexity: added same-pawn settings admission evidence / removed the
claim-based priority freeze and duplicate claims view / orphans left: none.

The next task 3.3 increment retains native recipe-output windows when conditional
capacity changes with current need debt. Calibration uses the least supported
sampled rate; an unassessed capacity sample preserves observed output but cannot
authorize a shortfall or widening verdict. Unsized stock-target orders still read
their exact bill counters. The former idle-bench `haul_bound` diagnosis is now
`work_unstarted`: stored ingredients and an idle bench do not prove a delivery
bottleneck. The unused sizing stock field was deleted. `NativeThroughputTests`
covers changing capacity, unassessed capacity, actual unsized output and the real
dispatcher retaining its window while dropping unsupported widening. The gate
passed 4,123 domain tests, 30 workflow tests, native compilation, probes and the
orphan check (1m36s). No new native gameplay claim is made. Task 3.3 remains
incomplete: shared outcome baseline/settings alternatives and supported future
shortfall are still required. Rules 7–9: calibration is pure policy over canonical
output; existing derived memory owns the window; unassessed capacity is not zero.
Complexity: added conditional-capacity window regressions / removed volatile-rate
resets, unsupported hauling attribution and duplicate stock field / orphans left:
none.

The subsequent capacity-bound increment removes whole-horizon idle credit from
`WorkShortfallRules`. Deferred work can use only unallocated supported capacity
windows, with overlapping windows and allocations counted once. Unread capacity
of a relevant worker produces named `WorkShortfall.Unassessed` reasons, rather
than either zero capacity or an idle day. Unrelated dependents do not invalidate
the estimate. Pawn/skill/classroom/prison valuation withholds work-dependent
credit; caravan lost-work uncertainty reaches the total capability ledger.
The existing review signal exposes the unassessed reasons and absent pawn values.
`NativeWorkShortfallTests` covers these bounds and telemetry; the settlement
mission regression exercises unavailable lost work through the total value.
The gate passed 4,127 domain tests, 30 workflow tests, native compilation, probes
and the orphan check (1m18s). No native gameplay result is claimed. This remains
partial task 3.3 work: useful ongoing production and setting alternatives still
need integration into the shared baseline. Rules 7–9: existing policy capacity
and demand are reused; Runtime only formats the existing signal; unknown native
duration cannot finance labor. Complexity: added explicit shortfall uncertainty
and consumer propagation / removed whole-horizon idle credit / orphans left: none.

The inventory follows `ForecastCoupledCandidates.GenerateCoupledActivities`, `ForecastCoupledSelection.StartCoupledActivity` and `ColonyReview.AdmitCoupledSelection`. The families feed the shared comparison; their execution migration status is recorded per row. A forecast can remain useful after its routine exact dispatch is removed. Each row must be closed with source and verification evidence; a planned target is not an implemented path.

| Current producer / dispatch responsibility | Target execution | Owning contract / verification required | Status |
| --- | --- | --- | --- |
| `FoodMethodCandidates.GenerateCoupled`: harvest/hunt, preparation and food transport | Native designations, bills, storage and priorities; direct work only for justified meaningful intervention | [Support work](../developers/contracts/support-work.md), [forecast](../developers/contracts/forecast-contracts.md), food evidence; provider/admission tests and short native usable-output claim | Pending |
| `ForecastNeeds.GenerateNeedsAlternatives`: eating, rest and recreation | Native needs, timetable and policies; preserve comparison of adequacy/cost and bounded justified exceptions | [Pawn activities](../developers/contracts/pawn-activities.md), forecast; needs/continuation tests without future exclusive holding | Partial: fresh routine services and boundary continuation stay native; unresolved access and justified exceptions remain |
| `ClothingMethodCandidates.Generate`, effect-source composition | Native production bills and work settings; explicit inputs and conditional yields | [Equipment](../developers/contracts/equipment-upkeep.md), forecast; stock/ingredient/partial-output regressions | Partial: ordinary clothing creates native finite bills; other production and meaningful interventions remain |
| `ConstructionMethodCandidates.Generate`: supply and build | Native blueprints, material availability and construction/hauling priorities; justified direct exceptions | [Facilities](../developers/architecture/facilities.md), [continuation](../developers/contracts/method-continuation.md); material, quality, skill and dependency tests | Pending |
| `CoupledShelterCandidates.Generate`, `PlannedRoomCandidates.Generate` | Retain room/investment selection; realize selected work through native sites and ordinary work settings | Facilities, forecast; room prerequisites and actual build/output evidence | Pending |
| `SupportMethodCandidates.Generate`: care, delivery, hauling and refill | Native work/settings and observed current occupation; justified direct exceptions | Support work, [medical care](../developers/contracts/medical-care.md); reachable native support and stale-care dispatch tests | Pending |
| `ResearchMethodCandidates.Generate`: conditional research blocks | Existing research goal/current project plus native priorities; fresh routine pawn binding removed | [Research](../developers/contracts/research.md), [work assignment](../developers/contracts/work-assignment.md); `NativeExecutionBindingTests` covers real selection without direct orders | Partial: fresh routine binding migrated; incumbent reconciliation and justified exceptions still require the general migration |
| `SowingMethodCandidates.Generate`: conditional sowing work | Crop/zone choices and native growing priorities; fresh routine pawn binding removed | Work assignment, forecast; `NativeExecutionBindingTests` covers native-ready sowing selection without direct orders | Partial: fresh routine binding migrated; deadline intervention and incumbent reconciliation remain |
| `PracticeMethodCandidates.Generate`: range and sparring | Native practice affordances/settings where available; bounded direct activity only for justified capability need | [Capability maintenance](../developers/contracts/capability-maintenance.md), [facilities](../developers/architecture/facilities.md); capability demand, actual native completion and interruption tests | Pending |
| Existing observed incumbent retention / `ForecastCoupledExecution` | Account for useful native work and actual intervention ownership; remove routine exclusive waiting/restarts | Pawn activities, forecast, continuation; native opportunity cost and lost-owner/dependency regressions | Pending |
| `AdmitSelectedTrade` and world/mission alternatives | Keep existing trade/journey decisions; account for unavailable home labor without redesigning travel execution | [Trade](../developers/contracts/controllable-trade.md), forecast; no fabricated home capacity or duplicate inventory promises | Boundary audit pending |

All routine activity types above share `StartCoupledActivity` / `CommitClaimedMethod` / Hands dispatch. Removing one family's exact binding does not remove another's. Trace final readers before deleting shared task types, native observations, codecs or helpers. Additional readers found while migrating belong in this inventory, not an undocumented exemption.

## First milestone and remaining proof

The first milestone must permit native takeover across selection/start/end/finalizer boundaries, reconcile actual ownership loss and let nursing/bottle feeding finish through safe return. It removes baby-specific advance pairing/release. Cases must stage deterministic ordinary owned work through the real journal/Hands path; they must not depend on the portfolio choosing an unrelated research goal. The harness must not release the caregiver or assign feeding after setup.

Later milestones complete capacity/native-baseline comparisons, meaningful interventions, finite native queue identity/recovery and every ordinary execution migration above. A passing feeding case alone does not implement the general labor requirements. The first milestone's executed claims are recorded above; no later-group behavior is certified by them.

Rules 7–9 review for the first milestone: policy owns finite provisioning and
lost-owner decisions; Runtime sequences the existing journal/Hands path; native
code observes actual jobs and emits the existing invalidation. Only actual care
withholds worker capacity, consumed inputs are not refunded, and missing participant
reads defer cleanup. No new scheduler, durable queue or care ledger was added.
The orphan sweep found no source readers of the removed advance-release helpers,
custom wait driver or source-scoped job permissions. Complexity: added one internal
live-journal fixture seam and one takeover invalidation producer; removed advance
caregiver release, the indefinite wait driver and five lifecycle interception paths;
orphans left: none. Existing routine-exclusive readers remain in the inventory,
not as orphan exemptions or claims of completed migration.

## Ordinary clothing bill milestone

Selected ordinary textile/leather recipes now create finite unrestricted native
bills through `NativeProductionBinding`, the work-ledger Need, shared admission,
the Method journal and Hands. The existing gear declarer retains active observed
bills. Native priorities cover prospective clothing products before a bill exists.
Existing bills and unfinished work create no duplicate bill. Recipe, material,
input allocations, learning, travel and outfit effects remain conditional planning
scenarios; the bill does not pin a worker, input stack or output destination.
A bill receipt is not garment or delivery evidence.

Fresh routine tailoring and its dependent delivery install no pawn activity.
Old coordinated attempts hand back at a tailoring boundary while preserving
independent in-flight work; unobserved dependencies are not marked complete.
The private nonautonomous bill, exact tailoring resolver, cleanup/provisioning,
continuation input rebinding, ledger exemption and sole-purpose bill-class wire
field have been removed through their readers. Ordinary native bills/UFTs remain
the save's canonical state; regenerate saves containing the retired private class.
Exact cooking still reads the shared recipe driver and remains in the inventory.

`ClothingChoiceTests` exercises shared generation/comparison, bill binding, journal
admission, typed Hands dispatch, replay, absence of pawn claims and no output
credit; existing material/scarcity/unfinished/learning tests now explicitly inspect
conditional scenarios. Native boundary handback and prospective clothing work
demand have focused regressions. The two registered clothing cases now observe
ordinary vanilla `DoBill`, selected-stuff output, conservation and interrupted UFT
resumption without an exact pawn activity. Compilation is not an executed gameplay
pass. No native gameplay run is claimed for this milestone.

Verification: `RimGovernor.cmd test > test.out 2>&1` passed 4,210 domain tests,
30 workflow tests, native production/fixture compilation, contract probes and
the orphan check (2m6s). Strict OpenSpec validation passed all five items.
Task 5.3 remains incomplete for the other production paths and meaningful
intervention integration; overall progress remains 13/24. Confirmed food
requirements are unchanged and #4040 remains open.

Rules 7–9: the shared comparator still owns the choice; pure binding translates it
into existing bills, and Runtime only sequences admission. Existing work demand,
material promises, ledger ownership and save state are reused. Native worker and
completion timing remain conditional; no trajectory simulator, new optimizer,
reservation store or duplicate demand owner was added. Complexity: added native
bill binding and admission plumbing / removed private exact tailoring execution,
ledger exemption and dead wire field / orphans left: none.
