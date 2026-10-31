# Design

## Context

See [proposal.md](proposal.md) for motivation and authority, and [the labor delta](specs/colony-labor/spec.md) for behavior. This is a cross-cutting control-contract change, so a design is required.

Inspection at the proposal checkpoint established the following starting behavior.
Some of that behavior has since been replaced by the completed tasks; the
[labor evidence page](../../../../docs/requirements/colony-labor.md) records landed
behavior and verification separately from this migration design.

| Existing owner | Behavior relevant to this change |
| --- | --- |
| `WorkAssignment`, `NativeWorkSettings` | Already propose and apply numbered priorities, schedules and allowed-area settings through typed intent. |
| `NativeProductionBills`, native designation operations | Already expose demand as native work, including target-count bills with pause/resume thresholds. |
| `PawnSchedule.WorkHours` | Counts Work/Anything hours for a capacity abstraction. Native `JobGiver_Work.GetPriority` instead biases work by the current timetable assignment; it does not guarantee work hours. |
| Coupled generation, comparison and `ColonyReview.AdmitCoupledSelection` | Select and bind exact ordinary food, self-service, construction, clothing and support tasks to claimed activity Methods. |
| `NativePawnActivities` | For owned pawns, replaces normal selection, suppresses constant/override/opportunistic/finalizer paths, rejects unoffered jobs and re-enters its own selection after a job ends. Unowned pawns run vanilla. |
| `NativePrioritizedJob`, `NativeDesignate.Prioritize` | Provide native forced/prioritized work and some queue support. The existing queued designation uses `EnqueueFirst`; this is not evidence of a general ordered multi-job queue API. |
| `PawnActivityTracker` | Passively observes starts, ends, carries and execution identity. Its telemetry is not itself a worker wakeup. |
| `CoordinatedActivitiesRules`, `ActivityContinuationRules`, journal continuation | Reconcile lost ownership, retire ended intent and release only still-owned participants. Actual takeover ends the coordinated attempt; it does not authorize releasing a new owner. |
| Unlanded baby implementation | Adds useful hunger/current-care observations and care admission checks, but also `MakeRoomForBabyCare`, caregiver pairing and advance release. These execution choices are superseded here. |

Installed vanilla code inspection found default Childcare feeding through work givers and Urgent autofeeding through the main think tree. Feeding is not in the constant think tree. A research work bout lasts 4,000 ticks before normal selection; simply allowing `CheckForJobOverride` misses selection at job completion. Native forced orders have their own interruption and queue semantics. These are code findings, not executed gameplay passes.

The starting contracts described exclusive routine ownership and exact future self-service; update those contracts as their readers migrate. Existing food outcome requirements do not conflict and remain unchanged.

Implementation inspection also exposed a comparison gap. `ForecastCoupledSelection`
builds its baseline from request commitments and retained incumbents;
`ForecastCoupledScenarioOf` includes their explicit activity completions. In
`ForecastNeeds.NeedsCoreOf`, food decays unless a modeled eating choice supplies
it. That is a valid conditional no-service calculation, but it does not predict
what vanilla will select next. Observed sleep is already retained in supported
cases; that does not supply a general native future trajectory. The revised
comparison must preserve this distinction rather than treating the empty
activity portfolio as evidence that native pawns will stop eating or working.

## Goals / Non-Goals

**Goals:** Retain shared outcome decisions and accountable bot writes while making native routine behavior the execution default. Account for real labor and native progress, justify bounded interventions, and reconcile takeover without immediately reasserting stale work. Deliver a complete baby-feeding milestone first, then migrate every existing ordinary-work caller affected by this contract.

**Non-Goals:** Reimplement RimWorld's work scanner, create a second colony simulation, promise exact labor hours, or replace every Family's domain logic. This does not redesign tactical combat or world travel. Existing native interruptions must continue to work at their supported boundaries; no new combat bypass is introduced. The first milestone is not certification of all food requirements or completion of the later migration.

## Decisions

### 1. Separate outcome planning from the mechanism used to execute it

Keep Families deriving Needs and proposing Methods. Extend the existing comparison/binding boundary so a proposed Method can realize an outcome through native settings/work creation, or through a justified direct intervention. Reuse existing typed settings, bills and designation intents; do not introduce a second dispatch loop. Every bot mutation is admitted/journaled through the appropriate shared Method and executed by Hands.

Routine work has no exclusive pawn assignment merely because its output is useful to a forecast. Native reservations and observed carried/input state still constrain shared admission. Domain demand, expected yields and actual output remain distinct. A future native output does not become an exact stock lot or a guaranteed reservation.

Alternative: retain current exact execution and add baby/fire/etc. exceptions. Rejected because it keeps the bot responsible for admitting ordinary native responses and multiplies advance-release policies. Alternative: remove shared Method admission. Rejected because accountability for bot writes and cross-goal resource decisions is still needed.

### 2. Make general takeover possible through the whole native job lifecycle

The target rule is that native selection and job replacement use the game's legality and interruption rules, including at ordinary job boundaries. A committed bot task is intent for an attempt, not a permanent prohibition on native choice. Remove the routine-owned interception from selection, constant evaluation, overrides, opportunistic work, job start/end and finalizer handling as affected callers migrate. Do not add a whitelist of feeding jobs or call the childcare job giver as a parallel selection path.

For a remaining bot intervention, bind its actual native job/queued job identities to its existing Method. When a different native job actually takes over, publish observed interruption, relinquish the old execution identity and allow the replacement to run. A proposed alternative job that is never started is not takeover. Sequence invalidation before end/start hooks could reinstall the old task; re-read current job identity to handle nested cleanup and pooled job reuse. Preserve native finalizers, including safe return after baby feeding.

Reuse the existing typed execution observations and journal ownership checks. Add a boundary field only for a specific consumed identity or outcome; remove obsolete assignment-only fields when their readers disappear. Keep native execution handles transient, journal intent in SQLite and game orders in the save. Amend the persistence table before changing ownership or adding retained state. This is not permission for a parallel durable queue or takeover ledger.

Alternative: flip only the override prefix. Rejected because `Select`, `Starting`, `Ending`/`Ended` and `Finalizer` would still suppress or undo the native response. Alternative: unrestricted ordinary jobs interrupt every forced job. Rejected because that invents semantics different from vanilla; native forced-order behavior remains authoritative.

### 3. Use the existing continuation path to reconcile a takeover

The native bridge observes physical execution changes, invalidates stale execution ownership and reports facts; it does not select the replacement colony Method. The worker re-reads all affected participants, jobs, carried items and output. `CoordinatedActivitiesRules` already ends an attempt on a lost participant and releases only remaining matching ownership; retain that conservative shared behavior instead of building a second dependency scheduler.

Resolve the old Method when ownership is positively absent. A missing observation is not positive absence. Release only exact still-owned work and its claims; never cancel the new native job. Productive continuation must pass current shared demand, resource admission and safeguards again. Actual progress is retained; consumed inputs cannot be replayed from predicted output.

Integrate meaningful execution changes with existing event/review invalidation and the worker's continuation loop. Reuse existing signals where they carry the necessary fact; otherwise add one typed event and its registered signal reader. Telemetry alone does not wake the worker. Do not replan on every movement/toil tick or automatically reissue an interrupted order from the same stale justification.

### 4. Use observed native conditions as the execution baseline

Use existing canonical observations, Food supply/runway, Work requirements/roster, current job/activity facts, needs and shared resource views to derive demand and capacity. Preserve physical units: useful nutrition or output per day, remaining work, travel effort and available worker time. Distinguish native observed progress, a conditional mechanical estimate and an unavailable estimate; neither unavailable nor preemptible means zero output or cost.

Start with observed current work, realized output and existing mechanical estimates. Use bounded, in-memory observation windows where throughput needs comparison across frames; reset them on load/world changes and invalidating production changes. Do not derive a production rate from net stock changes alone: consumption, trading and deliveries can change stock without production. When output attribution cannot be established, name that limitation and use observed runway/current bottlenecks rather than fabricating a rate. These derived windows are not a new stored accounting owner.

The execution baseline is the colony as observed under its current native
settings: current needs and care, usable supply and reserves, access, useful
native activity, realized output, known upcoming demand and supported recovery
lead times. Preserve useful ongoing work and its opportunity cost without
claiming its unobserved output. This is a shared policy assessment over canonical
facts, not another simulator, demand owner or dispatch loop.

Keep three kinds of evidence distinct in the existing comparison:

| Evidence | Permitted use | Limit |
| --- | --- | --- |
| Observed needs, access, reserves, activity and realized output | Establish current adequacy, unmet demand, progress and actual blockers | Applied settings, a running job and unconsumed inputs do not establish completion |
| Supported conditional estimates of demand, throughput, recovery and lead time | Compare settings, investments and preventive interventions, stating assumptions and displaced work | No guarantee of native job order, exact service time, future stock or idle labor |
| Detailed activity forecasts | Explore what a proposed service or finite intervention could achieve and its dependencies/costs | An empty modeled schedule is a no-modeled-service counterfactual, not the execution baseline; a better forecast is not dispatch authority |

Retain detailed forecasts where a current decision uses them. They may inform
benefits and costs, but do not nominate or hold every routine pawn job. A
forecasted hunger deficit caused solely by omitting future eating, or a work
deficit caused solely by omitting future native production, cannot establish a
meaningful intervention. Likewise, do not repair that counterfactual by
manufacturing an exact vanilla eating/work schedule or guaranteed product lots.

Unsupported future native service or production remains explicitly unassessed.
It establishes neither failure nor adequate service. Carry the uncertainty
through the affected ranking and explanations; do not silently substitute zero
output, zero cost or a successful future service. An unassessed component must
not defeat unrelated decisions supported by current evidence or prevent a
credible preventive response. Compare the alternatives on supported outcomes,
known risks and costs, naming what could not be compared. A direct order still
needs the justification in decision 5; uncertainty alone supplies none.

For example, accessible stored meals and an eligible pawn do not become a
starvation forecast merely because the governor selected no eating activity.
They also do not prove future intake. Observe need/intake and reassess access or
other blockers if service does not occur. Conversely, inadequate usable reserves,
a known loss of supply or a recovery lead time exceeding supported runway can
justify preventive action before anyone starves, without predicting each job.

Extend the existing shared comparison/admission boundary with this evidence
distinction. Reuse existing conditional mechanics and aggregate estimates where
they answer the decision; add prediction or execution constraints only for a
concrete demonstrated failure. Replace authoritative uses of raw timetable-hour
counts with estimates accounting for needs/care, travel, native activity and
other commitments. Timetable descriptors and supported capacity bounds never
credit completed production.

Work settings may change freely under full bot control. A routine priority reshuffle does not require the materiality rule for direct orders, and an old player setting is not exempt. Compare desired settings to current readback and dispatch differences through the existing writer.

Generate regular sleep, recreation and work blocks, as confirmed during apply.
Size service blocks in whole hours from observed fall/recovery rates and approach
time, retaining two flexible hours for meals, travel and other native choices.
Use a regular 22:00 sleep start unless modeled awake-only mood windows favor a
different phase. Recompute from fresh mechanics; do not shift the whole day to
chase a transient need level. Missing service mechanics leave that timetable
unassessed while independent priority changes remain possible. Current-rate
category/tolerance limits remain limits on prediction: a timetable preference
neither predicts exact service nor guarantees work. No schedule history store,
extra model call or exclusive execution commitment is introduced.

Alternative: retain the empty exact-activity portfolio as the native baseline.
Rejected because omitted native service can manufacture a crisis and justify
redundant control. Alternative: reconstruct vanilla's full future job sequence
to fill that portfolio. Rejected because it restores the prediction and
micromanagement burden this change removes. Alternative: wait for stock failure
before acting. Rejected because reserves, known upcoming demand and recovery
lead time require preventive decisions.

### 5. Admit direct orders only for a concrete meaningful reason

Pure policy derives an intervention justification from current shared Needs and observations: a relevant shortfall, credible risk, stalled essential work or worthwhile time-sensitive opportunity. Compare continuing native operation under current settings, useful settings/work-creation changes and the proposed intervention, including displaced useful work, travel and disruption. Use decision 4's execution baseline; never substitute the optimizer's empty activity schedule for continuing native operation. This is an extension of the existing comparison, not an adviser writing orders or a hard-coded second food selector.

A small speed improvement while outcomes are adequate is not sufficient. No universal numeric materiality threshold or fixed wait timeout is assumed; use domain outcomes, deadlines and consequence comparison. Settings changes need not be tried and allowed to fail first when their known response time is inadequate. Missing evidence is an explicit blocker to the affected comparison, not permission to assume a colony crisis or adequate service.

Record the concrete premise separately from any forecasted benefit: what observed
deficit, credible risk, demonstrated stall or worthwhile opportunity makes the
order meaningful now, what native/settings response is supported, and what
uncertainty remains. Do not require proof of an exact future vanilla failure or
wait for harm. Do not authorize or reissue an intervention solely because its
exact forecast beats a no-service counterfactual, a settings write succeeded or
the previous Method existed. Reassess the premise after material native progress
or changed availability before fresh admission.

Give each admitted intervention a bounded outcome and cancellation condition, fresh native prerequisites, concrete claims and an observable completion. Prefer existing prioritized native work operations. Extend the existing typed order path for finite queue ordering, readback, idempotency and cancellation only where existing supported operations lack required plumbing; do not assume the current `EnqueueFirst` helper supplies those guarantees. Cancel only exact remaining jobs belonging to the intervention, preserving unrelated/new native work. Use short queues; later jobs must not depend on unobserved products or retain a pawn through speculative future work. If an exceptional exact adapter remains necessary, justify its current reader and native cleanup behavior; it does not restore exclusive routine control.

No new LLM run is needed. These decisions remain deterministic C# policy inside the current review cycle. Richer estimates may increase that cycle's computation; use bounded existing comparison and scoped evidence instead of adding a second optimizer.

### 6. Use baby feeding to demonstrate the new default

Keep canonical baby hunger, current native care, usable nursing/food and observed intake. Keep feeding demand in Food and distinguish provisioning from feeding outcomes. Remove `MakeRoomForBabyCare` and `ReleaseForBabyCare`, and remove advance greedy caregiver matching/capacity withholding where it exists solely to control who vanilla must select. Active care remains observed occupied capacity, including carrying and safe return. Recheck it at direct-order admission so a stale review cannot interrupt it.

Continue to arrange missing suitable provision and change childcare/feeding settings through existing intent. An ordinary hungry baby with functioning native care does not require an intervention. Worsening unmet feeding triggers assessment of the actual care, supply or access blocker; a direct response is considered only when meaningful. Preserve normal bottle accumulation before ingestion as distinct from a genuine stall. Working nursing must not mandate unnecessary bottle reserves, while breastfeeding capability alone must not certify usable care or nutrition.

Replace the current research-dependent acceptance setup. A native fixture can stage a deterministic owned task as a precondition through the real journal/Hands path, rather than persuade the portfolio to choose an unrelated research goal. After setup it must neither release the pawn nor order feeding. Assert native work/feeding transitions and final physical outcomes. Use an ordinary interruptible activity at a known native selection boundary; a vanilla forced job is not a valid fixture if the test expects an interruption vanilla would not perform. Exercise true forced/queued semantics separately.

Code inspection already proves today's selection obstruction. Do not write or run a baseline test merely to rediscover that condition. Focus tests on changed behavior: takeover surviving the hooks, ownership/dependency reconciliation, care protection, actual intake and native return. Nurse and bottle cases remain separate physical claims.

## Risks / Trade-offs

- Native work is less predictable than exact schedules -> keep forecasts conditional, compare observed progress and retain unmet demand when supply cannot be established.
- An empty activity forecast can manufacture starvation or stalled work -> use observed native conditions as the execution baseline; keep unsupported future behavior unassessed and require a separate concrete intervention premise.
- Uncertainty can either disappear from ranking or stall the whole colony -> propagate it through affected comparisons and explanations while retaining unrelated supported decisions and credible preventive action.
- A direct order can itself delay care -> use actual native forced-order semantics, current care admission and meaningful-risk reassessment; do not claim all forced work automatically yields to feeding.
- Restoring selection only partially causes restart loops -> change and test selection, start/end, finalizer and worker reconciliation as one complete execution path.
- Migration can leave two competing routine selectors -> inventory every coupled dispatch caller and close each migration entry before claiming this change complete; temporary milestones remain explicitly partial.
- Rates can lag events or misattribute stock -> invalidate derived windows appropriately, retain units/provenance and never invent production from aggregate deltas.
- Interventions may churn after native takeover -> reconsider fresh evidence and current native progress; do not preserve a queue or retry simply because its old Method exists.

## Migration Plan

1. Inventory and reconcile the unlanded `coordinate-baby-feeding` checkpoint and later edits. Retain useful observations, boundary regressions and independently justified fixes; remove superseded caregiver preallocation/release. Preserve the confirmed food spec and historical evidence. Capture the supersession explicitly when applying; do not blindly reset the branch or mark the old tasks complete.
2. Deliver the first coherent milestone: native selection/takeover compatible with ordinary bot work, existing Method reconciliation, active-care admission, native nursing/bottle intake and safe return. Any legacy exclusive caller remaining must be identified; this milestone alone is not completion of the general change.
3. Migrate ordinary food acquisition/preparation/eating, self-service, production/clothing, construction/delivery and support callers to native work availability/settings. Retain exact tasks only as justified bounded interventions, with physical dependency checks. Remove routine binding/holding hooks and helpers after the last reader migrates; no permanent feature flag or shadow execution lane.
4. Replace the empty-portfolio execution baseline with decision 4's observed/conditional/unassessed assessment, isolate detailed what-if forecasts from dispatch authority, and complete intervention materiality and native queue identity/recovery across supported paths. Do not substitute documentation or follow-up issues for required plumbing. The existing completed capacity/output tasks remain valid preparation; they do not establish this remaining baseline change.
5. Update contracts/evidence and complete scoped verification, orphan removal and the normal commit/landing lane for each coherent milestone. Roll back through ordinary versioned changes if required; no compatibility shim or alternate scheduler is introduced.

## Evidence and completion

Implementation will create `docs/requirements/colony-labor.md` as the owning labor evidence page and link it from the labor spec, requirements collection and documentation map. Until then this section names the owner without a broken link to a nonexistent file. Preserve [food evidence](../../../../docs/requirements/food-security.md) and record only the feeding claims actually established there. Planning status, registered cases, executed results and passed outcomes must remain distinct.

Use focused xUnit tests for intervention decisions, native-work opportunity cost, bounded capacity, takeover ownership/claims, partial outcomes and stale-order rejection. Use short staged native cases for actual selection/takeover and cleanup, both feeding modes, native forced/queue lifecycle and representative ordinary production. Native cases use typed reads and registered telemetry, one physical claim each, about a minute expected wall time and under two including startup. No planner lottery, inflated tick budget or post-setup harness rescue.

Baseline regressions must exercise the shared comparison through admission:
accessible food plus no selected eating activity must not alone authorize forced
eating or report certain starvation; omitted native production must not alone
establish a stall. Also cover a concrete access/supply blocker, reserves and
recovery lead time supporting prevention, useful native progress invalidating a
stale premise, and uncertainty that neither becomes zero nor suppresses an
unrelated supported decision. Prove the false-baseline error analytically and
with focused policy tests; do not spend a native run merely to rediscover vanilla
self-service. Scoped native runs still establish actual execution and outcomes.

Run `openspec validate --all --strict --no-interactive` and the required `RimGovernor.cmd test > test.out 2>&1` on each finished implementation milestone. Registering cases or passing the code gate does not close #4040. Required feeding outcome verification must pass before claiming its acceptance met. Completion of this change additionally requires migration of all affected routine callers, working labor/intervention decisions and no unexplained obsolete readers. Planning validation and repository checks do not establish the newly specified runtime behavior; no new gameplay run is required for this artifact revision.

## Architecture Rules 7–9 Review

- Policy owns demand, capacity, comparison and intervention decisions; Runtime composes facts and sequences reviews; the journal owns bot intent and claims; Hands writes orders; native code supplies mechanics and execution observations.
- Work demand and actual occupancy feed shared admission. Native routine work is an observed opportunity cost and physical reservation, not a second bot commitment store. No per-domain takeover scheduler, milk allocator or competing priority writer is added.
- Typed facts name hunger, active care, current jobs, output and unavailable estimates. Missing facts do not become zero capacity, idle labor, completed work or guaranteed future stock. Any added estimate or constraint must serve an actual decision and have a current reader.
