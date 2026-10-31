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

## Native support assessment

Native-operation support vectors retain the current care/bench demands and
outstanding delivery requirements from the canonical support read. Their future
service is `ForecastSupportAssessment.NativeUnprojected`, with
`native_support_response_unprojected`; it has no numeric future care/delivery
result. Detailed scenario vectors remain `Conditional`, preserving treatment,
medicine, learning and material calculations. The shared comparator reads those
future support terms only between conditional scenarios. Native harm comparisons
hold the reference care scenario common, so choosing a hypothetical tend cannot
claim an avoided untreated-reference loss. This is still a conditional harm
calculation, not a projected native treatment schedule.

Ordinary tending and already-indicated surgery, like ingredient delivery, have
native execution. Reusing a selected conditional plan under a native comparison
cannot install their exact orders. Old support commitments hand remaining work
back at task boundaries. Unfinished cross-pawn dependencies end the attempt;
independent working participants retain their exact orders. The separate critical
medical Need still owns its existing interventions and receipts.

`SupportMethodTests` covers current native demand versus conditional future
completion, omitted delivery/treatment, changed demand, no fresh routine dispatch
and an independent supported thermal comparison. Conditional medicine, infection,
surgery and learning tests continue to exercise their what-if mechanics without
claiming exact dispatch. `NativeServiceContinuationTests` covers boundary handback
for tending, surgery and delivery with independent or dependent participants.
The required gate passed (1m15s): 4,250 domain tests and 30 workflow tests,
native production/fixture builds, probes and no contract orphans. Strict OpenSpec
validation passed 5/5. An earlier native build encountered a zero-filled cached
Runtime reference assembly; clearing only its disposable intermediate directory
under the native-build mutex allowed the final gate to pass. No native gameplay
run is claimed for this increment.

The shared food/reserve/output baseline, general meaningful-intervention policy,
remaining support reachability/output evidence and final adapter audit are still
incomplete. Tasks 3.3, 4.1 and 5.5 remain unchecked; progress remains 13/24.
Rules 7–9: existing demand, output and continuation owners remain authoritative;
the assessment distinguishes evidence rather than adding a scheduler, model
call, durable state or second demand owner. Complexity: added a typed two-state
support assessment and ten regression cases / removed native authority from
routine care scenarios and false final-support ranking / orphans left: none.

## Exact bill work evidence

Vanilla `JobDriver_DoBill.MakeNewToils` collects ingredients before
`Toils_Recipe.DoRecipeWork`. The latter resets and increments
`ticksSpentDoingRecipeWork`; `AnyIngredientsQueued` reads the live ingredient
queue. These compiler-referenced members were inspected analytically from the
installed RimWorld assembly before changing native reads. `JobEvidence.BillWork`
now reports actual recipe work already done in the current vanilla bill job,
pending ingredients when no recipe work has been observed, or unassessed for
other drivers/stages. This identifies neither an exact current toil nor finished
food, and creates no new sampling hook or model call.

The existing dispatch window retains that evidence only from the order's exact
bills. A measured output shortfall with pending ingredients and no observed recipe
work reports `ingredients_pending`; without supported recipe-work evidence it
reports `recipe_work_unassessed`. Neither can widen bench allocation. This fixes
the prior calibration path where a cook gathering ingredients could justify an
extra stove merely because its job carried the bill identity. Observed recipe
work permits the existing conditional capacity calibration; actual bill/product
creation counters still own output. Stocked ingredients or an idle bench cannot
prove a hauling bottleneck, and a pending ingredient queue alone does not prove
a stall. No exact order is authorized by these findings.

`NativeThroughputTests` exercises exact-bill attribution through observation and
calibration for pending ingredients, unassessed work, observed recipe work and
another bill. It checks opening evidence, retention across later gathering and
invalidation with the production basis. Existing production-versus-consumption
tests remain. The existing `DispatchMemory` owns the derived window, documented
in the persistence table before adding its evidence field. General shared
baseline/intervention and migration tasks remain incomplete; task 3.3 stays open.

Rules 7–9: native exposes a current typed fact; pure calibration owns the decision
and existing dispatch memory owns derived evidence. Unknown work cannot become
bench-capacity demand; no native trajectory, durable store or execution lane is
added. Complexity: added one consumed native bill-work fact and calibration
regressions / removed bill-job identity alone as widening authority / orphans
left: none. No new native gameplay result is claimed.

Verification: final required gate PASS (50s), 4,259 domain tests and 30 workflow
tests, native production/fixture builds, contract probes and no contract orphans.
The exact-bill regression also drives `DispatchMemory` through the next review's
bill-copy allocation. Strict OpenSpec passes 5/5 and diff checks pass. Confirmed
food requirements remain unchanged; overall progress remains 13/24.

## Self-service ownership independent of access

Ordinary eating, rest and recreation remain native even when the captured target
is absent, a pawn is ineligible, sleep is blocked, or need-task metadata is
missing. These facts remain relevant to need/access assessment; they cannot make
an old hypothetical service an exact order. Fresh shared binding removes the
service, its participant's later work and transitive dependencies. At a task
boundary, continuation releases its exact owner without asserting intake.
Independent running participants and execution-identity checks remain intact.
An earlier task feeding the same pawn's hypothetical meal no longer bypasses
the pawn's current urgent need check.

Both `StartSelected` and forecast interruption accounting already pass the
filtered plan to `OrderedAtStart`. The obsolete 600-tick service deferral, its
departure helper and unreachable continuation service-binding checks are removed.
The old primitive-only deferral/sleeper expectations no longer describe live
dispatch; context-aware sleeper, native binding and dependency tests remain.
`NativeExecutionBindingTests` covers missing access, blocked sleep, downed or
unread pawns, missing need metadata and unbound partial starts.
`NativeServiceContinuationTests`, `ActivityNeedBoundaryTests` and
`CoupledActivityIntegrationTests` cover handback regardless of hypothetical
provider binding or departure time. `SharedStackRemainderTests` stages historical
exact intents directly for journal quantity, predecessor receipt and reopen
invariants; it no longer asks current policy to authorize their routine service.

This closes ordinary service permission through missing evidence, not the general
intervention path. Tasks 4.1, 4.2 and 5.2 remain open; no new native gameplay result
or completed overall migration is claimed. Existing support delivery rows describe
ingredient-staging opportunities, not proof that vanilla cooking is blocked:
native cooks can collect ingredients themselves. Bottleneck assessment must use
that distinction in task 3.3.

Rules 7–9: canonical observations still own access and physiological facts; pure
binding owns permission and existing journal/execution identities own cleanup.
No new fact, scheduler, prediction or durable state is added. Complexity: added
missing-evidence binding regressions / removed timed service-deferral and
own-meal handback bypasses / orphans left: none.

Verification: required gate PASS (40s), 4,254 domain tests and 30 workflow tests,
native production/fixture builds, contract probes and no contract orphans. Strict
OpenSpec validation passes 5/5 and diff checks pass. The test-count reduction
removes obsolete primitive timed-service expectations; the unbound partial-start
check remains in `NativeExecutionBindingTests`. Confirmed food text is unchanged;
overall progress remains 13/24.

## Native food stock and recovery assessment

`NativeFoodAssessment` shares the food owner's current usable runway, seasonal
minimum, reserve findings and conditional recovery leads with normal comparison
vectors and work-demand planning. It uses the latest stock read even when a
cached plan still contains an older stock scenario. Unsupported leads remain
unconfirmed; absence of a captured recovery channel alone establishes neither
failure nor success. The reserve release and assessment use the same timing
classification. An outstanding reserve shortfall remains visible when insurance
has been released.

A supported stock/recovery risk can raise capable workers' observed harvest or
cooking work types to priority 1 and move competing ordinary primary roles to 2.
Native work-giver metadata supplies the mapping; missing metadata does not invent
an option or block unrelated settings. Pinned needs, childcare, Doctor, medical
rest and existing capability floors retain their protections. Coverage follows
the actual priority matrix. Vanilla still chooses jobs, and accepting settings
does not count as output or resolve the risk.

`NativeFoodAssessmentTests` covers stock/recovery thresholds, fresh stock against
an older plan, unconfirmed/absent recovery, missing facts/mappings, capability and
protected roles, coverage consistency and shared native comparison without an
invented exact rescue. `NativeSettingsHandsTests` takes the preventive priority
change through shared journal admission and real Hands into a fault-injected
typed settings boundary. It proves the settings path, not future vanilla output.
No native gameplay run is claimed for this increment.

Verification: required gate PASS (39s), 4,258 domain tests and 30 workflow tests,
native production/fixture builds, contract probes and no contract orphans.
Strict OpenSpec validation passes 5/5; confirmed food requirements are unchanged.

General intervention materiality, delivery-versus-cooking bottleneck assessment
and the remaining migrations remain in scope. Tasks 3.3 and 4.1 remain unchecked;
overall progress remains 13/24. Rules 7–9: food demand/reserve owners remain
authoritative, the shared assessment is pure derived evidence, and no model run,
native trajectory, durable state or execution lane is added. Complexity: added
shared stock/recovery assessment and preventive settings regressions / removed
duplicate recovery-timing classification / orphans left: none.

## Routine execution migration inventory

Task 3.1 uses the existing shared demand and worker census plus the bill
dispatcher's derived window. `NativeDeliveryLedger` now counts each recipe's
products by bill identity at creation. `ObserveOrder` reads only that order's
exact bills and product; food acquisition credit excludes recipe transformations.
Current-job evidence uses `JobEvidence.BillId`, read from the native job's bill,
rather than merely matching the target bench. Refuelling, repair, delivery and
another bill on the same bench do not count as work on the requested order.
An exact bill job may be gathering ingredients; completed recipe counters remain
the evidence of output. `NativeThroughputTests` exercises those distinctions
through observation and calibration, including unchanged output despite an active
bill job. The opening sample counts toward both observed work and bill inactivity,
preventing a false `work_unstarted` verdict after dropping that evidence.
The attribution regressions passed the required gate (2m3s): 4,240 domain tests,
30 workflow tests, native production/fixture builds, contract probes and no
contract orphans; strict OpenSpec validation passed 5/5. This is policy/boundary
evidence, with no new native gameplay claim. Task 3.3 remains incomplete and
overall progress remains 13/24. Rules 7–9: native supplies the bill identity,
pure policy owns calibration, and existing derived memory owns the window;
neither an active job nor missing samples become completed output. Complexity:
added one consumed current-job fact and eight regression cases / removed the
bench-target work heuristic and opening-sample loss / orphans left: none.
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

The next task 3.3 increment pins the native baseline boundary at the shared
comparison and removes the last strongest-evidence shortcut from throughput
calibration. `NativeExecutionOutcome` now leaves a pawn missing from the needs
read unassessed rather than decayed: a missing observation is not positive
absence and has no common bound, so its food/rest/joy terms and dependent mood
stay unprojected. Ineligible pawns (downed, babies, sleep-blocked rest) keep the
conditional metric because their own owners govern them. The new
`NativeBaselineComparisonTests` drive `CompareActivityOutcomes` under the normal
`NativeOperation` basis: accessible food with no selected meal leaves baseline
starvation unassessed and prunes nothing; omitted native production with observed
stock claims neither stall nor starvation and installs no bill or order; an
unprojected need response does not block an unrelated supported decision (the
capability-maintenance fixture is still selected while its urgent-zero floor stays
unassessed); and changed food availability moves the `NativeFood` assessment from
`NoDemonstratedShortfall` to `RecoveryGap` without manufacturing an eating
trajectory. The omitted-production work-demand half remains covered by
`NativeWorkShortfallTests`.

Throughput windows now count busy samples by exact-bill evidence
(`GatheringSamples`, `RecipeWorkSamples`) instead of retaining the strongest
sample. With stocked ingredients and gathering in at least
`DeliveryBoundGatheringShare` (0.75) of the busy samples, a short window reports
`delivery_bound` and does not widen: a further bench would also gather. This is
evidence of where the cook's sampled time went, not a hauling inference from
stock or an idle bench; `work_unstarted` still owns the idle stocked bench.
`ingredients_pending` now names only gathering below that share with no recipe
work; `recipe_work_unassessed` and `benches_exhausted` are unchanged. The
`UnmetReason` reader in `Workshop` still widens only on `NoBench`/`BenchesExhausted`;
no priority or hauling change is driven from this verdict yet. Earlier paragraphs
above describing `ingredients_pending` as the pending-ingredients verdict predate
this increment. Both pieces are policy/boundary evidence; no new native gameplay
claim is made. Task 3.3 remains incomplete: native-setting alternatives are not
yet ranked in the comparison vector, `NativeFood` is carried but not compared,
and `WorkShortfallRules` remains scheduler-derived; intervention materiality is
task 4.1. Rules 7–9: pure policy owns the baseline overlay and calibration;
Runtime supplies canonical facts; a missing observation names a state and
cannot finance decay, idle labor or widening. Complexity: added two sample
counters, one unmet reason and ten regressions / removed the strongest-evidence
window field and missing-pawn decay / orphans left: none.
Verification: required gate PASS (2m39s), 4,305 domain tests and 30 workflow
tests, native production/fixture builds, contract probes and no contract orphans;
strict OpenSpec validation 5/5. Overall progress remains 13/24.

Task 3.3 is closed on that evidence. The execution baseline under the normal
basis is now the colony as observed: current needs and access through
`NativeNeedServices` and the common no-service bound, usable supply, reserves
and conditional recovery through `NativeFoodAssessment`, native occupation
through `NativeLaborCapacity`, realized output through exact bill counters and
the dispatcher window, and support through `NativeUnprojected`. Work-creation
alternatives (bills, designations, investments) compete as portfolio roots;
ordinary priority and timetable settings change freely through `WorkRules` and
`PawnRules.RoutineTimetable` under decision 4 and need no place in the vector.
Detailed activity forecasts stay what-if evidence: conditional results install
nothing, routine-only winners are rejected, and routine bindings are removed.
Known limits, carried forward rather than hidden: the detailed stock walk still
draws continuous native consumption for consumers outside the needs read with
no modeled native production, so its `external_stock_shortfall` refusal and the
unread `food_consumer_shortfall` finding remain conditional what-if results;
`WorkShortfallRules` remains a scheduler-derived conditional demand estimate
read only by pawn valuation; the vector carries `NativeFood` for explanation and
task 4.1's premise without ranking on it. None of these is a native prediction or
dispatch authority. Overall progress is 14/24.

Task 4.1's first increment adds the pure intervention premise gate,
`InterventionRules.Justify`, inside the shared comparison. It runs only for a
`NativeOperation` winner that would install exact pawn orders (the dispatch that
survives `ForDispatch` and incumbent holding) or is ordinary service alone, and
replaces the former routine-only override as the single premise gate. A premise
is read from current observations of the work dispatched: an observed shortfall
(a pawn at or below its need threshold with no captured native service), the
food owner's credible risk (`NativeFoodAssessment.Reviewed` needing a response),
or an assigned capability requirement the dispatched practice trains, unmet now
or lapsing within the horizon. The native response is `Supported` when confirmed
recovery arrives before runway, `KnownTooLate` at or after it, otherwise
unassessed; displaced pawns are counted as native service, native work or
unread, never priced or treated as free. Refusals are named: routine-only
winners keep `native_service_response_without_intervention_premise`; a winner
beating the baseline only below the safety floors with no premise is
`marginal_gain_without_premise`; a floor-stage win without a premise is
`forecast_benefit_without_premise`; food dispatch whose advantage rests on an
unprojected native response is `no_modeled_service_rescue`; a supported timely
settings response refuses any premise except an observed harm with
`settings_response_supported`; missing premise evidence is
`premise_unassessed:<reason>`. Food work is classified by the catalog's item
nutrition, the same rule as the stock walk; a resource the catalog never read is
`nutrition_per_unit:<resource>`, not "not food". No numeric materiality
threshold or wait timeout was added; "marginal" is the comparator's own stage.
The justification is recomputed by every review and stored nowhere; a refused
winner leaves the native baseline selected, recorded as
`intervention_not_justified` with its reason, and `StartCoupledActivity` refuses
to bind a batch without this review's justified verdict. The `coupled_selection`
row exposes verdict, premise, response, stage, displaced counts and uncertainty.

`InterventionJustificationTests` covers the task's bullets through the real
comparison: marginal speedup under adequate conditions refused; a rescue from a
no-modeled-service baseline refused; prevention justified from supported
reserve/recovery evidence before harm; settings known too late permitting
immediate action while both vectors' food terms stay unassessed; a supported
timely response refusing a non-harm premise; fresh native progress (risk gone,
service in progress, skill restored) invalidating a stale premise with the old
plan throwing when carried forward; routine-only refusal preserved; assigned
practice dispatching only while its observed capability premise holds; and
food classification by nutrition rather than current targets. `PracticeHarmTests`
now records that a priced unassigned bout ranks but is not admitted. Practice
is the only coupled family still binding fresh exact orders; all food, support,
construction, clothing, research and sowing work is native execution. Remaining
for 4.1: a `DemonstratedStall` premise has no observed source in the comparison
context (the dispatcher's observed `UnmetThroughput` is not yet exposed there),
and the premise is assessed per portfolio rather than per intervention; both are
taken up with task 4.2's per-intervention purpose binding. Rules 7–9: pure policy
owns the gate; Runtime records it; Methods, claims and Hands are unchanged; a
missing fact names a state and authorizes nothing. Complexity: added one pure
justification rule, one telemetry attr group and ten regressions / removed the
routine-only override and the target-inferred edible set / orphans left: none.

Task 4.1 is closed and task 4.2 binds justified interventions to native work.
`DemonstratedStall` now has an observed source: the work dispatcher's
calibration (`ProductionCalibration.Observed`, read only while the work ledger
is reconciled, else `stall_calibration_unobserved`) supplies closed short
windows per product, and only a cause exact orders answer (ingredients pending,
delivery bound, work unstarted) is a premise; other causes are named
`stall_not_answered_by_orders:<product>:<cause>`. The premise is assessed per
intervention: each dispatched pawn's orders are one `InterventionAssessment`,
and the first refused pawn refuses the portfolio as `<reason>:<pawn>`. A
justified assessment gives its orders a typed `InterventionPurpose` (kind,
subject, subject pawn, evidence tick), which `PawnActivities.Validate` requires
on every non-release `coupled-` order. `InterventionPurposes` is the one pure
reader: continuation and review end a Method once each purpose completes or is
cancelled on the observation its kind names, never on elapsed ticks. One
classifier, `InterventionPurposes.Observe`, gives each order's `ObservedOutcome`
(accepted, running, completed, failed, replaced, released) to both readers:
continuation from its activity read (`coupled_observed`), and the review from
its native ownership rows, which now carry the execution's method and stop
reason (`WorkResourcePawn.Method`/`Reason`), so a `native_job_takeover` reads
replaced at both (`coupled_selection` attr `intervention_outcomes`). Admission
refuses a stale prerequisite (unavailable pawn, consumed observed input), a
second open purpose of the same pawn and aim, and a justification whose
evidence tick is not newer than that purpose's retirement. Every retirement
path now writes `plans.retired_tick` through one Store write, `RetirePlanIn`,
and the schema (67) rejects a retired plan without it: continuation,
resolution and the review's settled-plan retirement record their observation
tick, combat and load retirement the newest tick the plan observed.
Continuation names a vanished next target.

`InterventionHandsTests` exercises the real journal and Hands: outcomes are
distinguished and an accepted receipt is not completion; the review classifies
a native job takeover of an installed intervention as replaced, not failed,
and reports it; a drafted pawn, a consumed input and a vanished target are
refused at admission or continuation; an open purpose is not issued twice; an
ended purpose is not reissued from evidence at or before its end, whether
resolution or the review's plan retirement ended it, while newer evidence is
admitted; and the comparison-to-Hands path carries each order's justified
purpose, while a refused premise or a bare exact order cannot reach Hands.
`InterventionJustificationTests` adds the stall premise (answerable causes
justified, unanswerable named, unassessed and unobserved calibration
unassessed, no stall no premise), per-pawn premises, and purpose end
conditions. Read where two readings conflict, duplicates are keyed by pawn and
aim (design decision 5's bounded purpose), and a same-review reissue after a
release at that tick is stale. Two limits are known. A vanished target is
named only at continuation: the review's frozen admission evidence holds no
target read to check it against. And no separate input-debit record exists:
inputs are claimed once through the existing lot claims, so an open or retired
purpose cannot debit stock twice by construction. Rules 7–9: policy stays
pure; the Store owns admission, retirement and the retired-purpose record;
Hands is unchanged. Complexity: added one purpose record and kind, two outcome
enums, one pure purpose and outcome reader with its review row, one
calibration fact, two native ownership fields, one persisted column with its
check, one retirement write and ten regressions / removed the per-portfolio
premise, five `coupled-` string literals and nine ad hoc retirement updates /
orphans left: none.

Task 5.1 makes the justified food exception reachable and leaves routine food
execution native. The premise is now assessed per pawn before dispatch
filtering: `InterventionRules.Readmitted` takes the withheld allocations that
wait on no prerequisite and carry a binding, and keeps one only when that
pawn's assessment is Justified for the activity's purpose kind. A meal answers
a NeedShortfall of food or a FoodRisk; harvest and other food work answer
FoodRisk. `CoordinatedActivityBinding.ForDispatch` and `StartSelected` dispatch
only those re-admitted activities beside unchanged non-routine ones; nothing
else re-admits, so Supported still refuses and the RoutineOnly refusal stands
when nothing is re-admitted. The generator's routine food bindings stay as
comparison readers (social mode, composition, bill service and the outside
cost), so none was deleted. `food/ordinary-cooking` stages a fueled stove with
no bills, raw rice, a meal zone and a cook. It asserts that the bot creates a
target-count cooking bill and that meal units rise in the colony resources.
Verification: `InterventionHandsTests.ACredibleFoodRiskReachesHandsAsAJustifiedHarvestFromTheGenerator`
(a non-credible risk dispatches nothing and a credible one reaches Hands as a
Work order carrying its FoodRisk purpose),
`NativeBaselineComparisonTests.AModeledMealAloneIsNeitherACrisisNorADispatch`
and `NativeFoodAssessmentTests.ModeledNativeFoodProductionAloneForcesNoDispatch`,
3 regressions; `InterventionHandsTests` (a) and (e) stay green. The acceptance
case executed on 2026-10-31 (`RimGovernor.cmd acceptance run food/ordinary-cooking`
against the worktree's fixture build from `27f8c15c` with the 5.1/5.2 edits,
pinned save `5c4cf06d…`, fixture hash `60eca9a3c6e03991`): PASS in 38.0 s wall,
meals 0 → 4 with no exact pawn order; provenance in
`.rimgovernor/ordinary-cooking-0224/food/ordinary-cooking/result.json`. Rules 7–9: policy stays pure; re-admission has one pure owner;
Hands and persistence are unchanged. Known limits: the food premise needs a
native food assessment and catalog nutrition; formation hunting stays in its
own planners outside the comparison. Complexity: added one re-admission rule and its
purpose-kind reader, one dispatch parameter, one acceptance case and three
regressions / removed nothing in food execution / orphans left: none.

Task 5.2 leaves routine meals, sleep and recreation to native needs, schedules
and policies, and keeps the justified bounded exception. A pawn's need is
unanswered (`NativeNeedServices.Unanswered`) when a legal target exists, its
level is at zero and native operation is not serving it: the new observed
`ServedNeed` field reads the pawn's current ingestion, sleep or joy job. An
unanswered need keeps its conditional forecast and is the only premise
(`at_zero_without_native_service`) that re-admits that pawn's own service.
Under a ConditionalScenario selection `StartCoupledActivity` starts nothing.
Continuation hands back a pawn whose next task is a meal, sleep or recreation,
so the native rebind of those tasks in `NativeActivityTasks` is deleted. Its
only native case, `needs/recreation-shared-seat`, and fixture
`RecreationSeatFixture` are deleted with it. `InterventionPurposes.Ended`
cancels a batch holding an old-shape self-service order: one whose purpose is
not its own pawn's NeedShortfall of that need, including a preparation holding
ahead of one. Hands then releases it and never continues it. Verification:
`InterventionHandsTests.AnUnansweredHungerReachesHandsAsAJustifiedEatFromTheGenerator`
(an unanswered hunger reaches Hands as a justified Eat carrying its
NeedShortfall purpose; a served one re-admits nothing; a ConditionalScenario
selection starts nothing);
`NativeNeedServicesTests.AnUnansweredNeedAtZeroReadmitsOnlyItsOwnServiceUnderItsShortfall`
×3; and `NativeServiceContinuationTests.AJustifiedSelfServiceEndsOnItsNeedAndAnOldShapeOneIsReleased`
×3. The continuation theory shows that a running service stays open and owed
with no continuation, an answered need completes and releases, a native job
takeover releases, and old shapes (another pawn, another need, FoodRisk, a
preparing hold) are cancelled and released. That is 7 regressions. Domain
tests: 4334 passed, 0 failed. Rules 7–9: policy stays pure; the observed field
is added rather than assumed; no persisted state, scheduler or holding is
added. Known limits: joy at zero during work hours can justify recreation;
a re-admitted self-service that is not the pawn's first task is handed back
at continuation; the search's unbound check does not see re-admission, which
dispatch filtering covers. Complexity: added one observed field and its native
read, one unanswered reader, one conditional-kept outcome, one old-shape
guard and seven regressions / removed the native Eat/Sleep/Recreation rebind
branch, one native acceptance case, its fixture and its build switch /
orphans left: none.

Task 5.3 leaves bill work to native bills and work settings: no bill work is
an exact pawn order any longer. The exact recipe primitive
(`NativeActivityRecipe.cs`), its driver branch, its only native case
`control/pawn-activity-cooking` (`ControlCooking`), its fixture
`PawnCookingFixture` and build switch, the `CookingRead` contract and a task's
`ProductStuff` are deleted. A `Recipe` kind now names a comparison binding only;
`PawnActivities.Validate` refuses it as a step, and surgery's `Work`/`DoBill` is
the one step that carries bill arguments. With no exact recipe task left, the
incumbent reconsideration for a worker that finished a task between reviews is
deleted too: `TaskReviews`, `ActivityContinuation.FinishedTasks`,
`ColonyEventConsumer.NotifyTaskReview`, the scheduler's `task` cause, and the
unfinished-thing lines in `WorkResources`. Schema 68 replaces older journals.
The one meaningful exception is a closed short window of a product whose bill
jobs showed pending or delivery-bound ingredients (`ProductionStall`,
`ingredients_pending` or `delivery_bound`). It re-admits only the generator's
ingredient delivery feeding that bill (the binding's new `Feeds`) under a
`production_stall` purpose naming the product. `work_unstarted`, exhausted
benches and the bench causes are refused `stall_not_answered_by_orders`.
Ordinary clothing already creates native finite bills (`NativeProductionBinding`).
Verification:
`InterventionHandsTests.AStalledBillWithIngredientsPendingReachesHandsAsAJustifiedIngredientDelivery`
(the delivery alone reaches Hands with its purpose; its cooking is never
re-admitted);
`InterventionHandsTests.AStallNoOrderAnswersIsRefusedAndDispatchesNothing` ×2;
`PawnActivitiesTests.BillWorkRejectsMissingOrAmbiguousIngredientAuthorization`;
`ActivityNeedBoundaryTests.AFinishedIngredientDeliveryReturnsToNativeWhateverTheCarriersRest`
×2; and `FoodCookingContinuationTests.AnEndedIngredientDeliveryReturnsToNativeWithoutCreditingMeals`
×2. That is 8 regressions. The existing clothing, stock-target, partial-batch
and unavailable-ingredient regressions (`ClothingChoiceTests`,
`WorkResourcesTests`, `CoupledActivityIntegrationTests`) still pass unchanged
apart from the removed recipe shape. Rules 7–9: policy stays pure; the feeding
relation is added as a binding field rather than inferred; schema 68 is the
amended persisted shape, and no scheduler, holding or fallback lane is added.
Known limits: an unstarted bill is left to native work even when stocked
ingredients idle; only the generator's own ingredient delivery can answer a
stall; no native gameplay run is claimed. Complexity: added one binding field
(`Feeds`), one stall re-admission rule, one step guard and 8 regressions /
removed the exact recipe primitive and driver, one native case, its fixture
and build switch, `CookingRead`, `ProductStuff`, the cook binding's `FoodPoisonChance` and
the wire `FoodCooking.Cook.FoodPoisonChance` with its native fill (their last
reader was the deleted incumbent reconsideration), the task-review chain, the
unfinished-thing claims and 12 obsolete test methods / orphans left: none.

Task 5.4 leaves ordinary construction supply and frame completion to native
blueprints, frames and Construction/Hauling priorities, and makes the justified
exception observable. The construction read now reports each placed site's
progress (`ConstructionSiteProgress`: materials delivered of its cost, frame
work done, and whether a colonist's current job targets it), filled natively in
`NativeConstructionActivity.Read`. The review keeps one progress window per site
(`ColonyReview.ConstructionMemory`). `ConstructionStalls.Observe` reports a
stall only when the site's progress held and no job targeted it for
`ConstructionStalls.WindowTicks`, one game day and the dispatcher's throughput
window, so two readings at least a window apart are needed. The cause is
`supply_unstarted` while the site owes materials, otherwise `finish_unstarted`.
Changed progress, a first reading or an earlier tick reopens the window, and a
targeting job restarts the quiet span, so no stall is fabricated from ticks.
The new premise `StalledSite` holds for the pawn's supply of that site or its
supplied frame's finish. It gives a `construction_stall` purpose naming the
site, which completes once the site's progress changes after the stall closed
or the site is gone; a worked site without progress stays open. Material,
tier and minimum-skill constraints stay native settings
(`ConstructionTierGate`, `ConstructionSkillGuard`), and a held site stays its
order's (`NativePawnActivities.SiteHeld`, `CarryHeld`). An unsupplied site's
finish waits on its delivery and is never re-admitted. Verification:
`InterventionHandsTests.AStalledSiteReachesHandsAsAJustifiedSupplyWithItsPurpose`
(the supply alone reaches Hands with its purpose; the finish waiting on it does
not; the purpose's end conditions);
`InterventionHandsTests.AStalledSuppliedFrameReachesHandsAsAJustifiedFinish`;
`InterventionHandsTests.ASiteWithoutAnAnsweredStallReadmitsNothing` (no stall,
another site's stall, a cause the work does not answer, unobserved progress);
and `ConstructionStallsTests` ×5 (a whole-window stall of each cause, a single
reading, reopening on progress or rewind, a targeting job restarting the quiet
span, dropped sites and a missing read). That is 8 regressions. Existing
material, quality, skill, delivery-dependency, interrupted-work and
observed-completion regressions still pass:
`ConstructionNativeObservationTests.SiteReadsNativeIdentityDefinitionMaterialsAndFinishingGuards`,
`ConstructionCensusTests.NativeConstructionProjectionRetainsOwedMaterialsGateAndMinimum`,
`ConstructionHelpReadyTests.QualityFreeHelpersUseTheObservedNativeSkill`,
`CoordinatedConstructionTests.ASiteNoOneStackCoversIsSuppliedFromEveryStackOfItsSet`
and `NativeServiceContinuationTests.OldConstructionCommitmentReturnsToNativeWithoutCreditingDependentCompletion`
×6. Domain tests: 4324 passed, 0 failed. Rules 7–9: policy stays pure; the
progress fact is added to the wire rather than assumed; the window memory is
in-memory derived state recorded in the persistence contract, and the purpose
kind adds no schema shape. Known limits: a site a native worker abandons
without progress restarts its quiet span, so the next stall waits a further
window; a purpose never cancels on its own and relies on the order's deadline
or failure; standing removal bindings, the native set driver and the `control/`
removal cases stay until task 5.6 because their exact primitives are still
registered native evidence; no native gameplay run is claimed. Complexity:
added one observed field and its native read, one pure window observer with
its memory, one premise, one purpose kind and 8 regressions / removed nothing
beyond 5.3 / orphans left: none.

Task 5.5 leaves ordinary care, hauling, refill, research and sowing to native
work settings, and makes unmet care an observed exception. The support read
now reports, per demand, whether a colonist's current job serves it
(`SupportDemand.Served`: a `TendPatient` targeting the patient, or a `DoBill`
of the bill or at the bench), filled natively in `NativeSupportActivities`.
The review keeps one window per unserved tend demand
(`ColonyReview.CareMemory`). `CareStalls.Observe` closes a window on any
served reading, opens one on a first unserved reading or an earlier tick, and
reports a `CareStall` only when the demand was unserved at every reading
across `CareStalls.WindowTicks` (one game hour), so a single reading or a
patient merely needing tend is never a stall; surgery, bench and delivery
demands are native bills and are not tracked. The new premise `UnservedCare`
holds for the pawn's support tend of that demand. It gives an
`unserved_care` purpose naming the demand, which completes once the demand
is no longer read; a support tend answers no other purpose, and an unread
support read leaves it `premise_unassessed:care_service_unobserved`. The
emergency medical orders keep their own premise: RG-LABOR-004 applies to
them, and an observed emergency (downed, bleeding toward a finite death, or
life-threatening; `TendRules.Emergency`) is that premise, with the patient's
tended state as completion. A patient who only needs tend is no longer paired
for an exact tend (`MedicalRules.TendDoctorFor`), and the critical Need waits
with `ordinary_tend_native_doctor_work`; rescue is unchanged. Hauling and
refill add no stall read: every capable colonist keeps Hauling enabled (3
under a haul backlog), native bill workers gather ingredients, refuelable
buildings refuel natively, and an ingredient delivery is re-admitted only
under a production stall (task 5.3) or as food work. Practice is choice (b):
it stays the one standing justified exact binding, because native range and
sparring initiation is suppressed for controlled colonists and lifting that
needs a native path no case proves; a selected practice reaches Hands only
under `CapabilityOpportunity`. Research and sowing re-admit nothing (no food,
stall or care served), and an old-shape research or sowing batch now hands
back at its next task boundary (`CoordinatedActivityBinding`). Telemetry
gains the `stalled_site` and `unserved_care` premise attrs; `stalled_site`
was missing, so a justified construction stall would have failed its
selection record. Verification:
`InterventionHandsTests.AnUnservedCareDemandReachesHandsAsAJustifiedTendWithItsPurpose`
(the tend reaches Hands with its purpose; the purpose's end conditions);
`InterventionHandsTests.ACareDemandWithoutAnUnservedWindowReadmitsNothing`
(a served demand, an open window, another demand's stall, an unobserved read);
`CareStallsTests` ×7 (a whole-window stall, a served reading closing the
window, a first reading, rewind or dropped demand, bill demands untracked, a
missing read);
`MedicalFamilyTests.AnOrdinaryInBedTendIsNativeDoctorWorkAndOnlyAnEmergencyIsPairedForAnExactTend`;
`WorkOccupancyTests.OrdinaryCareAndHaulingAreReachableThroughNativeWorkSettings`;
`NativeServiceContinuationTests.OldResearchOrSowingCommitmentReturnsToNativeAtBoundary`
×4; and
`NativeExecutionBindingTests.ResearchAndSowingAreNeverReadmittedUnderAnyPremise`
×2. That is 17 regressions. Existing settings, safety and occupation
regressions still pass: `MedicalFamilyTests.APatientWhoseCareIsWithheldOrUnmanagedIsNotPaired`,
`MedicalFamilyTests.ACareOrderOnAPawnAnotherOrderHoldsIsAClaimConflict`,
`ClimateStatesTests.ARefuelSwitchOutranksTheRoomMethodAndNamesTheCampfire`,
`PowerMethodTests.OutOfFuelProducerWithCapacityHoldsForRefuel`,
`WorkOccupancyTests.UsefulNativeWorkIsOccupiedAndKeepsItsRoleWithoutInventingDuration`,
`NativeWorkShortfallTests.NativeOccupiedTimeCannotFinanceUnselectedWork`,
`ActivityNeedBoundaryTests.ARunningTaskIsNeverInterruptedByAUrgentNeed`,
`NativeServiceContinuationTests.OldSupportCommitmentReturnsToNativeAtBoundaryWithoutCompletingDependencies`
×6, `InterventionJustificationTests.AssignedPracticeDispatchesOnlyWhileItsObservedCapabilityPremiseHolds`
and `InterventionHandsTests.TheComparisonToHandsPathCarriesEachOrdersJustifiedPurpose`.
Domain tests: 4341 passed, 0 failed. Rules 7–9: policy stays pure; the served
fact is added to the wire rather than assumed; the window memory is in-memory
derived state recorded in the persistence contract, and the purpose kind adds
no schema shape. Known limits: the native `Served` read and the care window
are unexecuted in game, and no acceptance case was registered or run for
them; only a colonist's current job counts as service, so a tend queued behind
another job reads unserved until it starts; a purpose never
cancels on its own and relies on the order's deadline or failure; sowing has
no deadline premise, so a late crop is native work; the support what-if
bindings stay, since `ForecastSupport`, `ForecastCoupledEvaluation` and
`ForecastHospital` read them. Complexity: added one observed field and its
native read, one pure window observer with its memory, one premise, one
purpose kind, one emergency predicate, one handback clause, two premise attrs
and 17 regressions / removed nothing beyond 5.4 / orphans left: none.

The inventory follows `ForecastCoupledCandidates.GenerateCoupledActivities`, `ForecastCoupledSelection.StartCoupledActivity` and `ColonyReview.AdmitCoupledSelection`. The families feed the shared comparison; their execution migration status is recorded per row. A forecast can remain useful after its routine exact dispatch is removed. Each row must be closed with source and verification evidence; a planned target is not an implemented path.

| Current producer / dispatch responsibility | Target execution | Owning contract / verification required | Status |
| --- | --- | --- | --- |
| `FoodMethodCandidates.GenerateCoupled`: harvest/hunt, preparation and food transport | Native designations, bills, storage and priorities; direct work only for justified meaningful intervention | [Support work](../developers/contracts/support-work.md), [forecast](../developers/contracts/forecast-contracts.md), food evidence; provider/admission tests and short native usable-output claim | Partial: routine plant harvest, raw delivery and cooking chain stay native; justified meal and FoodRisk work re-admitted per pawn (task 5.1); `food/ordinary-cooking` executed PASS 2026-10-31 (meals 0 → 4 under bot-created bill and work settings); formation hunting stays in its own planners outside the comparison |
| `ForecastNeeds.GenerateNeedsAlternatives`: eating, rest and recreation | Native needs, timetable and policies; preserve comparison of adequacy/cost and bounded justified exceptions | [Pawn activities](../developers/contracts/pawn-activities.md), forecast; needs/continuation tests without future exclusive holding | Closed (task 5.2): ordinary binding, continuation and rebinding stay native; only an unanswered need at zero re-admits its own pawn's service; old-shape orders are released |
| `ClothingMethodCandidates.Generate`, effect-source composition | Native production bills and work settings; explicit inputs and conditional yields | [Equipment](../developers/contracts/equipment-upkeep.md), forecast; stock/ingredient/partial-output regressions | Closed (task 5.3): ordinary clothing runs through native finite bills and cooking through bot-created native bills; no bill work is an exact order; only an ingredients-pending or delivery-bound stall re-admits the ingredient delivery feeding it |
| `ConstructionMethodCandidates.Generate`: supply and build; `CoupledShelterCandidates.Generate`: roofing | Native blueprints, roof areas, material availability and construction/hauling priorities; justified direct exceptions | [Facilities](../developers/architecture/facilities.md), [continuation](../developers/contracts/method-continuation.md); material, quality, skill and dependency tests | Closed (task 5.4): ordinary supply/frame completion, roofing, standing removals and planned clearance stay conditional and install no pawn orders; previous commitments hand back at boundaries; native hauling is migrated; only an observed construction stall re-admits its site's supply or supplied finish, under a `construction_stall` purpose; standing removal bindings and the native set driver await the task 5.6 cleanup |
| `CoupledShelterCandidates.Generate`, `PlannedRoomCandidates.Generate` | Retain room/investment selection; realize selected work through native sites and ordinary work settings | Facilities, forecast; room prerequisites and actual build/output evidence | Closed (task 5.4): selected rooms realize through reconciled native blueprints and designations; unplaced planned-site activities are never dispatched or re-admitted (`InterventionRules.Readmitted`, `PlannedRoomActivitiesTests`); a placed site's stall follows the construction row |
| `SupportMethodCandidates.Generate`: care, delivery, hauling and refill | Native work/settings and observed current occupation; justified direct exceptions | Support work, [medical care](../developers/contracts/medical-care.md); reachable native support and stale-care dispatch tests | Closed (task 5.5): routine delivery, tending, hauling, refill and already-indicated surgery run through native work settings, care policies and bills and install no pawn orders; old commitments hand back at boundaries; only observed unserved care re-admits its own tend, under an `unserved_care` purpose; emergency tend/rescue stays the critical medical Need's, for an observed emergency only; the what-if bindings stay as forecast readers |
| `ResearchMethodCandidates.Generate`: conditional research blocks | Existing research goal/current project plus native priorities; fresh routine pawn binding removed | [Research](../developers/contracts/research.md), [work assignment](../developers/contracts/work-assignment.md); `NativeExecutionBindingTests` covers real selection without direct orders | Closed (task 5.5): fresh routine binding migrated; an old-shape research batch hands back at its next task boundary; research re-admits nothing under any premise (`NativeExecutionBindingTests.ResearchAndSowingAreNeverReadmittedUnderAnyPremise`) |
| `SowingMethodCandidates.Generate`: conditional sowing work | Crop/zone choices and native growing priorities; fresh routine pawn binding removed | Work assignment, forecast; `NativeExecutionBindingTests` covers native-ready sowing selection without direct orders | Closed (task 5.5): fresh routine binding migrated; an old-shape sowing batch hands back at its next task boundary; sowing re-admits nothing, and no deadline premise exists, so a late crop stays native work |
| `PracticeMethodCandidates.Generate`: range and sparring | Native practice affordances/settings where available; bounded direct activity only for justified capability need | [Capability maintenance](../developers/contracts/capability-maintenance.md), [facilities](../developers/architecture/facilities.md); capability demand, actual native completion and interruption tests | Closed (task 5.5, choice b): practice stays the one standing justified exact binding, since native range and sparring initiation is suppressed for controlled colonists; a selected practice reaches Hands only under a `CapabilityOpportunity` premise with a `capability_practice` purpose (`InterventionJustificationTests.AssignedPracticeDispatchesOnlyWhileItsObservedCapabilityPremiseHolds`) |
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

## Native cooking chain milestone

`FoodMethodCandidates.GenerateCoupled` retains ingredient supply, recipe conversion,
delivery, ingestion and learning as conditional scenarios, but ordinary supply,
cooking and output-delivery activities are marked for native execution. Shared
dispatch drops that chain and work depending on its unobserved completion; it
does not duplicate an existing production bill or claim a cook. The existing
food declaration/work-ledger/Hands path still owns ordinary cooking bills and
the normal work-settings path enables their workers. Native capability observations
no longer require the explicit recipe operation's drop-on-floor setting, and
cooks can gather ingredients even when independent hauling work is disabled.

Old coordinated recipe chains return a participant at a task boundary, including
when it is rested or has a valid binding. In-flight work is preserved and missing
output is not credited. Removed the now-unused exact recipe duration subclass,
binding read, JSON variants and codec fields through native observations, plus
the output-recipe continuation that had no producer. Session schema 64 replaces
older journals; no new persistence owner or compatibility lane was added.

Shared portfolio and productive-learning tests distinguish conditional scenarios
from normal dispatch and verify no routine cooking orders or duplicate bills.
Continuation regressions cover completed/failed input work and rested boundaries.
The productive cooking/lifecycle/reassignment fixtures now record actual native
job identities and require no exact activity owner. Their previous Method-owned
gameplay passes do not certify this revised behavior. No native gameplay pass is
claimed here. The explicit fixture-driven cooking primitive still has a native
adapter; its final removal or justified-intervention replacement remains in the
general adapter audit. Acquisition and the short native usable-food result also
remain pending, so task 5.1 and the overall labor change are incomplete.

Verification: the finished `RimGovernor.cmd test > test.out 2>&1` passed 4,207
domain tests and 30 workflow tests, native production/fixture builds, contract
probes and the orphan check (2m3s). Strict OpenSpec validation passed all five
items. The lifecycle fixture now observes native job starts separately from
completion XP, allowing only jobs already running when fatigue was staged to
finish. No native gameplay run is claimed; overall progress remains 13/24,
confirmed food requirements are unchanged and #4040 remains open.

Rules 7–9: existing food demand and bill/settings owners are reused; pure policy
controls binding and handback, and native observations name capability without
predicting the selected worker. No extra optimizer, scheduler or state store was
introduced. Complexity: added native cooking handoff and outcome-based fixture
reads / removed exact duration/output continuation plumbing and obsolete exact
continuation tests / orphans left: none.

## Native food acquisition

Plant harvest and raw-food delivery in `FoodMethodCandidates.GenerateCoupled`
are conditional native operations. Normal dispatch installs neither operation,
their dependent intake nor later work held behind their predicted completion.
The existing food plan still selects wild forage and lone hunting sources;
`AcquisitionPlanner` creates native designations through shared Method admission
and Hands. Growing zones expose routine grower work. The work-settings policy
already provides plant-cutting/hunting coverage. Formation hunting retains its
separate combat execution and still needs the intervention audit.

`NativeExecutionBindingTests` checks real generated harvest/delivery selection,
retained conditional benefit, no normal pawn order and no work-assignment
obligation once settings are settled. Existing forage, claim-admission and food
family tests retain designation selection and source/cell claims. Generic journal
and output-binding regressions explicitly install hypothetical graphs; they also
assert that normal food selection cannot install those graphs. This distinction
does not authorize a new exact food intervention.

`production/coupled-needs` now observes actual usable harvested rice, native job
identities and actual native intake/rest/recreation instead of exact Method
binding. Preparation removes loose, carried and inventory food; carried output
also counts as usable harvested food. The revised case is registered; its prior
exact-Method results do not certify these assertions. Native usable-output
execution and general intervention/baseline work remain pending. Confirmed food
requirements and task checkboxes are unchanged.

Verification: `RimGovernor.cmd test > test.out 2>&1` passed 4,208 domain tests,
30 workflow tests, production/fixture native builds, contract probes and the
orphan check (57s). Strict OpenSpec validation passed all five items. Rules 7–9:
existing food demand, designation, bill and settings owners remain authoritative;
pure binding policy removes redundant orders and observations distinguish actual
output from hypothetical yield. No new execution lane or persistence owner.
Complexity: added normal-selection regression and passive native-output evidence /
removed routine harvest/raw-delivery dispatch and the fixture's exact-apply hook /
orphans left: none. The general change remains 13/24 complete.

## First-meal planning deadlock

The native `production/productive-cooking` attempt at source `2e090cdda` was
stopped after 41s of unchanged tick 15000. No cooking job or meal occurred.
`first_plan_fact` named `layout_candidate_model:cycle: ... meal_service_unavailable`
for all three cooks: layout valuation rejected a known absence of ready meals,
so the first-plan clock gate prevented the production that would make them.
Evidence: `.rimgovernor/native-food-production-20261028/production/productive-cooking/result.json`
and its OTLP `clock_run`/`required_fact` rows. This is a failed attempt, not native
production evidence.

`ColonyModelInput` now accepts the engine's existing `PawnCycle.NoMealService`
state. That conditional scenario retains its named finding and supplies no
forecast labor. No new meal, work rate or execution prediction is invented.
The layout model can be valued while ordinary native cooking becomes available;
invalid mechanics continue to prevent valuation. Model-input and cycle tests
cover the real review boundary and retain the no-labor conditional result.
The first retry reached model valuation but exposed a second reader error:
`SkillValue.WorkPerXp` threw `free hours of <pawn>` while pricing the conditional
cooking learning. It was stopped after 41s at tick 15000, with no production;
the result is under `.rimgovernor/native-food-production-fixed-20261028/production/productive-cooking/`.
Skill credit, pawn values and caravan lost work now preserve unassessed hours
instead of throwing or supplying free labor. Reader regressions exercise those
boundaries. Native retry and verification are pending.
The complete reader audit also covers the prospective joiner's served-as cycle:
unsupported hours yield an unassessed recruitment value and a named finding,
without asserting that the recruit costs more than it contributes. Supported
recruitment arithmetic retains its assessed value. No additional state owner,
fallback service or second execution path was added.
The next retry cleared those readers, then named `layout_candidate_ledger:regroup:0`
while still at tick 15000; its stopped result is under
`.rimgovernor/native-food-production-ready-20261028/production/productive-cooking/`.
The runtime's redundant fully-priced-total requirement is removed. Evaluated
layout candidates now retain the existing partially assessed consequence vector
and use the same canonical term comparison as ordinary selection. The valuation
variant is named `Evaluated`, replacing the misleading `Priced` name through all
readers. Shared room-regroup tests cover an unassessed total alongside a supported
death-risk floor. Native output remains unverified pending retry.

Draft reconciliation now reads outstanding actions rather than evaluating unrelated
construction without a census. Monument claims similarly inspect only open marker
moves; the unused generic progress wrapper is removed. Journal/Hands and monument
regressions cover these boundaries. The preceding native run is recorded under
`.rimgovernor/native-food-production-evaluated-20261028/production/productive-cooking/`.

The retry under `.rimgovernor/native-food-production-journal-20261028/production/productive-cooking/`
cleared those faults and failed the no-exact-owner assertion: the journal installed
an ordinary `support/delivery/bill:Bill_CookMealSimple_0:RawRice` ingredient delivery.
No meal was produced. That remaining support migration is concrete follow-on work
within task 5.5, not a successful native cooking result. Overall progress stays
13/24 and confirmed food requirements remain unchanged.

Verification: the milestone gate passed 4,215 domain and 30 workflow tests, native
builds, contract probes and orphan checks (1m4s); strict OpenSpec validation passed
all five items. Rules 7–9: supported conditional states remain explicit, partial
values use the existing comparator, and journal readers retain their own action
semantics. No new demand owner, fallback capacity or execution lane. Complexity:
added uncertainty and journal regression coverage / removed redundant layout-total
guard, throwing hours adapter and unused progress wrapper / orphans left: none.

## Native ingredient handling

Routine support delivery/refill scenarios now retain `NativeExecution` through
dependency composition. Quantity and travel comparisons remain conditional; normal
binding installs no exact delivery, dependent service or pawn claim. Native bill
workers gather ingredients and native hauling follows storage/work settings.
`SupportMethodTests` covers small/batch quantities, composed native identity,
no exact tasks, material conservation and competing bill ingredients.

At source `aebfe9bf6` plus this milestone, the first native retry produced one meal
and a Cooking level gain with no exact owner, then failed the learning verifier.
The verifier was reading an in-progress enclosing interval while checking a
synchronous Learn call. Its callback-local clock now excludes that interval;
raw XP and resulting XP/level must still match, with actual/predicted diagnostics.

The subsequent run is under
`.rimgovernor/native-food-production-callback-20261028/production/productive-cooking/`.
Its cooking observations reached tick 16749 with two distinct native jobs, two
completions by one cook, usable output, Cooking level 6, no learning failures and
no exact owners. The overall case **failed** its required-fact audit: admin/rooms
layout-plan prerequisites, animal herd-room planning, layout climate envelope and
food/economy `planning Harvest` remain to resolve. These physical observations do
not certify the whole case or finish task 5.1/5.5. Overall progress remains 13/24.

Verification: final gate PASS (55s), 4,215 domain and 30 workflow tests, native
builds/probes and no contract orphans; strict OpenSpec 5/5. Rules 7–9: canonical
bill/material demand and existing native execution filter remain the owners;
no helper scheduler, capacity fallback or new persisted state. Complexity: added
composed native-delivery assertions and callback-local verification / removed
routine support-delivery dispatch / orphans left: none.

## First-layout prerequisites

Plan-dependent burial, climate envelope, room lighting/chairs and animal rooms
now remain unmet and held on `layout_plan` before the first plan is derived.
Other Needs in their families can proceed. Missing actual observations still
raise required-fact errors after that prerequisite is satisfied. These facts are
derived within the review, with no new persisted state. Field planning examines
harvest data only for definitions that actually have a harvest product; buildings
and non-harvestable plants do not require an invented harvest record.

The first retry cleared the animal/climate/harvest reports and again observed two
native meals with no exact owner, but still failed admin/rooms required-fact rows
under `.rimgovernor/native-food-production-prerequisites-20261028/production/productive-cooking/`.
The census cache returned before applying the remembered layout, whereas a fresh
read received it. Cached reads now apply the same existing layout overlay for
the same colony/map/load. The native census remains unchanged and no additional
read/model run is needed. `FirstLayoutPrerequisiteTests` covers that real cache
boundary, named pending holds, independent admin work, reassessment and retained
required-fact refusal. The next native retry still reported the two plan errors:
the family runner filed each pure hold and then invoked runtime work anyway.
It now skips runtime work for held Needs while other ready Needs proceed. A real
journal/family-runner regression proves the hold cannot reach the work delegate.
The finished native run **passed** `production/productive-cooking` in 52.014s,
including its required-fact audit, under
`.rimgovernor/native-food-production-held-needs-20261028/production/productive-cooking/result.json`.
Source is `0c3b01138` plus this milestone's dirty source tree, recorded by the
installed-package metadata. At tick 16800 there were two distinct native jobs,
two completed recipes by one cook, actual meal output, Cooking level 6, no learning
failures and no exact owners. This verifies the normal cooking/bill/priority path;
formation hunting, other routine migrations and general intervention justification
remain pending. No OpenSpec checkbox or #4040 closure follows from this one case.

Verification: final gate PASS (1m4s), 4,221 domain and 30 workflow tests, native
builds/probes and contract orphan checks; strict OpenSpec 5/5 and diff check pass.
Rules 7–9: pure families name prerequisites, the shared runner honors their holds,
and existing census/layout owners supply intent without new reads/model runs.
No new persistence, scheduler or capacity fallback. Complexity: added explicit
first-plan prerequisites and six boundary regressions / removed held-Need runtime
bypass and invalid harvest-section requirements / orphans left: none. Overall
progress remains 13/24; confirmed food requirements are unchanged.

## Native construction supply and frame completion

Placed-site supply and frame completion now remain conditional forecast work;
their normal selection installs no exact pawn orders. Existing blueprints,
frames, numeric work priorities and native construction skill/tier guards own
execution. Material availability and delivery dependencies still constrain the
conditional comparison, without crediting expected buildings as observed output.
An existing exact construction commitment returns to native work at its next
task boundary. In-flight and unrelated work remains intact; an uncompleted
cross-pawn prerequisite ends the coordinated attempt rather than becoming
fictitiously satisfied.

Regressions exercise real selected shelter/floor investments, planned-versus-
placed reselection, scarce materials, hypothetical specialist allocation and
boundary handback with independent/dependent participants. The final gate passed
in 40s: 4,227 domain tests, 30 workflow tests, native builds/probes and contract
orphan checks. No new native gameplay result is claimed for this milestone.
Removal/roof safety, clearance hauling and general intervention justification
remain pending; task 5.4 is not complete and overall progress remains 13/24.

Rules 7–9: shared material demand and existing blueprint/settings owners remain
authoritative; the pure execution filter and shared continuation path enforce
handback, with no new scheduler, persistence or invented output. Complexity:
added native construction selection/handback regressions / removed ordinary
supply and frame-finish dispatch / orphans left: none.

## Native roof building

Per-cell roof-building forecasts now use the same native execution boundary.
The existing room reconciliation path commits BuildRoof-area intent through
shared journal admission; Construction priorities leave that work available to
vanilla. An explicit roof-only hypothetical plan cannot install a routine pawn
order, even without supply/frame work or a needs service to filter it indirectly.
Existing BuildRoof commitments hand back at an observed boundary, preserving
uncompleted cross-pawn prerequisites and unrelated running work. Area designation
and forecast completion remain distinct from an observed finished roof.

The normal roof-only comparison may decline the portfolio as routine native
service; the isolated dispatch regression tests the hypothetical physical plan
separately, without changing investment ranking. Existing NeedBuildingTests cover
the real journal roof-area admission path. No new native gameplay result is
claimed. General intervention policy
remain in scope; task 5.4 and the overall change remain incomplete.

Verification: final gate PASS (40s), 4,230 domain tests and 30 workflow tests,
including native builds/probes and contract orphan checks. Strict OpenSpec and
diff checks pass. Overall progress remains 13/24.

Rules 7–9: existing area intent and shared demand remain the owners; the pure
native flag and shared continuation path enforce handback without new prediction,
persistence or a second executor. Complexity: added standalone roof dispatch and
boundary regressions / removed ordinary exclusive roof-building dispatch /
orphans left: none.

## Native construction clearance

Deconstruct, Uninstall, CutPlant, RemoveFloor and
Mine stay available to vanilla through native designations and numeric work
priorities. Planned footprint clearing is conditional investment evidence too;
it cannot reserve a worker merely because its targets already stand. Selected
sites still reach reconciliation: `CommitRemoval` and `GroundActions` create
removal intent, `CommitObstructions` creates cutting intent, and
`DigPlannedRoomAsync` admits room mining before ring placement. The designation
guards still recheck enclosure, roof and mining safety during governed execution.
Conditional forecasts retain exact targets, sets, material constraints and room
service valuation, without installing ordinary exact worker orders or claiming
unobserved completion.

At an observed task boundary, old routine removal commitments release their own
participants regardless of standing/planned task identity. Unfinished cross-pawn
prerequisites end the coordinated attempt; handback neither satisfies them nor
refunds consumed inputs. Running and unrelated work remain intact. Old exact
set failures end the attempt instead of retrying a remaining subset; a completed
physical subset cannot establish whole-task completion. Native satisfied-task
evidence remains authoritative. The producer-prefix classifier and partial-set
retry/rebinding helpers have no current readers and are removed.

`CoordinatedConstructionTests` exercises the shared comparison/dispatch boundary
for deconstruct, uninstall, mining and plant/floor sets. Conditional binding and
cell-set wire checks remain. `NativeServiceContinuationTests` covers all five
jobs with standing and planned identities, independent and dependent participants,
in-flight preservation and boundary handback.
`PlannedRoomActivitiesTests` retains selection and exact conditional target/set
mechanics without installing routine orders. `NeedBuildingTests` exercises
selected-footprint cutting/deconstruction through Family admission, the real
journal and Hands: accepted native intent is recorded once without a pawn order.
This proves intent plumbing, not physical completed clearance. General intervention justification remains to migrate. Task 5.4 and the overall change remain incomplete at 13/24.

Verification: final gate PASS (1m4s), 4,288 domain tests, 30 workflow tests,
native builds/probes and contract orphan checks. No new native gameplay result
is claimed. Rules 7–9: existing designations and shared material demand retain
ownership; pure generation and shared boundary cleanup add no orchestration
decision math, new state owner or inferred completion. Complexity: added planned
clearance selection and native intent integration regressions / removed routine
planned-clearance dispatch, producer classification and partial-set retries /
orphans left: none. Target/set fields retain conditional and native primitive
readers; no boundary field became unread.

The clearance read now also exposes proven removable roof cells for standing
demolition through `ClearanceTarget.RoofRemoval`. It reuses the existing native
roof-support calculation only for designated, blocked, non-dangerous targets.
Unproven geometry has a null result; safe removal has an empty cell set. Policy
does not invent a preparation option for fog, pending collapse or thick roofs.
The deduplicated cells count as current clearance demand even when the building
is already designated. The existing Family, journal and Hands expose RemoveRoof
area intent; vanilla owns worker choice and demolition waits on the existing
eligibility/completion guards. Accepted area intent is not removed roof or a
completed building removal. The Method identity includes the full cell set;
changed geometry cannot be hidden by an unchanged first cell and count.

Wire and policy tests cover proven/unproven, unselected and dangerous targets,
deduplicated geometry and fresh no-prerequisite reads. A clearance-Family test
reaches Hands with only RemoveRoof intent while the Need remains unmet. The
conditional comparison retains combined roof/demolition work without installing
either worker. Task prior-step state and its generation/binding, role-projection
and codec readers are removed; schema 65 replaces older journals. Primitive multi-step
orders retain their distinct fixture coverage and mechanics. No new gameplay
result is claimed for this native-read and coordination migration.

## Native item clearance and hauling

Items no longer create a separate room-removal operation or block the placement
wave until storage delivery finishes. Native preview decides placement; ordinary
construction selects haul-aside work for blocking items. Source inspection of
`WorkGiver_ConstructDeliverResourcesToBlueprints.HasJobOnThing`/`JobOnThing` and
`GenConstruct.HandleBlockingThingJob` establishes the path to
`HaulAIUtility.HaulAsideJobFor`, which uses non-storage hauling. Inspected local
`Assembly-CSharp.dll` SHA256:
`4E61D57205944C113631234BC55136A1A7C1679667B26008BEE5425BCFFA1463`.
This is analytical native-mechanics evidence, not a new executed gameplay case.

Planned hauling rows query `HaulAIUtility.CanHaulAside` per pawn, without an
accepting-stockpile prerequisite. A destination still inside the planned
footprint cannot establish whole-site clearance; absent legal rows remain named
unassessed evidence. Travel and multiple-carry costs remain conditional, not a
promise about vanilla's later choice. Standing chunk Haul designations retain
their storage costs and native work. Construction-clearance tasks carry their
existing site-definition field and hand back at boundaries without crediting
remaining quantities or cross-pawn prerequisites. Other delivery continuation
paths remain in the migration inventory for the general intervention audit.

The obsolete room operation, synthetic designation/ledger path, construction
read concat, carry-completion hook and specialized acceptance case are removed.
The explicit chunk delivery primitive and its registered case remain. Schema 66
replaces older journals containing unclassified removal deliveries; no native
queue or new persistence owner is introduced. `RoomFurnishingTests` cover unchanged
placement waves with interior/wall/door stacks, including forbidden items.
`PlannedRoomActivitiesTests` retain multi-carry costs with unaccepted storage and
no routine orders; `NativeServiceContinuationTests` cover construction-delivery
boundary handback, dependent cleanup and preservation of in-flight work.

Task 5.4 remains incomplete pending the shared intervention path; progress is
still 13/24. Confirmed food requirements are unchanged.

Verification: final gate PASS (1m37s), 4,293 domain tests and 30 workflow tests,
native builds, contract probes and orphan checks. Strict OpenSpec validation
passed. No new native gameplay result is claimed. Rules 7–9: existing native
settings, blueprints and designations own work; shared material demand is
retained; policy handles boundary handback, and unavailable native destinations
remain explicit. Complexity: added native haul-aside cost queries and placement/
handback regressions / removed the private haul-out operation, record, read,
completion hook and obsolete case / orphans left: none.

## Observed native recreation

The current-service observer recognizes ordinary stationary vanilla recreation
without requiring a governor execution handle. It retains the existing supported
driver, final-delay, movement, schedule, target and joy checks. Its remaining time
is explicitly a current-rate projection. Ownership tags are populated only when
the exact running job still matches the installed governor recreation step;
otherwise assignment/method are empty and revision is zero. Observation adds no
new store, native job, reservation or promised completion.

`ForecastIncumbentPreparationTests` verifies that native progress cannot lend
reconsideration/release authority to a journal Method. `NativeLaborCapacityTests`
verifies that its uncertain remainder cannot finance production capacity.
Existing service comparison and dispatch-boundary regressions retain the
separation between observed progress and guaranteed recovery. This is a native
read eligibility change; no new physical gameplay result is claimed. Task 3.3
and the overall change remain incomplete at 13/24.

Verification: final gate PASS (3m5s), 4,295 domain tests, 30 workflow tests,
native builds/probes and contract orphan checks. Strict OpenSpec and diff checks
passed. Rules 7–9: the existing native read owns activity observation, pure policy
consumes it, shared demand is unchanged and future timing remains explicitly
unassessed. Complexity: added native current-service visibility and ownership/
capacity regressions / removed the governor-owner prerequisite for observing
stationary recreation / orphans left: none.

## Native operation and conditional comparison evidence

The shared comparison now records its evidence basis. Normal reviews default to
`NativeOperation`; a currently uncaptured food target leaves future native
production, intake and recovery unassessed, just as unsupported future rest or
recreation does. Current need levels and service/access facts remain available.
No-service trajectories remain explicit counterfactuals, and a common bound that
needs no recovery can still establish zero deprivation. This neither declares
food provision adequate nor substitutes for the food plan's runway, reserves and
conditional recovery evidence.

`ConditionalScenario` uses the same comparison, scheduler, budget and cache
lifetime to rank detailed investment assumptions. Layout candidate valuation now
requests it explicitly. Conditional results cannot install pawn activities,
replace incumbents through coupled execution or supply production bills. The
portfolio mechanics, learning, ingredient thoughts, journal/material adapters and
optimizer qualification tests now identify their conditional scope explicitly.
Current native-operation regressions still cover uncertainty and retained work.
Faster hypothetical cooking or a missing modeled meal is not authority to replace
an incumbent. Generic exact-owner cleanup remains separately tested, including
preserving unrelated working participants.

Construction-only fixtures no longer inherit incidental forage opportunities
from a food fixture. This keeps their required facts and work-shortfall scenarios
about the construction they actually exercise. The missing-food-target regression
and a paired native/conditional comparison prove the new boundary without a
native gameplay run. General productive baseline assessment, concrete intervention
premises and the remaining caller migrations are still incomplete; task 3.3 and
task 4.1 remain unchecked and overall progress stays 13/24.

Rules 7–9: existing food demand, reserve review, observed-output accounting and
shared comparison remain the owners. Evidence basis changes the permitted use
of the existing forecast rather than adding a second optimizer, model call,
native trajectory, persistence owner or execution lane. Complexity: added explicit
comparison basis and authorization regressions / removed uncaptured-service
starvation authority and conditional incumbent-replacement permission / orphans
left: none.

Verification: final gate PASS (1m5s), 4,232 domain tests and 30 workflow tests,
native builds/probes and contract orphan checks; strict OpenSpec 5/5 and diff
check pass. No new native gameplay result is claimed.
