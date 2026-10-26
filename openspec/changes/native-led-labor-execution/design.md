# Design

## Context

See [proposal.md](proposal.md) for motivation and authority, and [the labor delta](specs/colony-labor/spec.md) for behavior. This is a cross-cutting control-contract change, so a design is required.

Inspection of the current worktree established:

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

Current contracts describe exclusive routine ownership and exact future self-service; that conflicts with the confirmed new direction. Update those contracts as their behavior changes. Existing food outcome requirements do not conflict and remain unchanged. No code, older change artifacts or standing specifications are modified by this proposal.

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

### 4. Compare settings and interventions against useful native activity

Use existing canonical observations, Food supply/runway, Work requirements/roster, current job/activity facts, needs and shared resource views to derive demand and capacity. Preserve physical units: useful nutrition or output per day, remaining work, travel effort and available worker time. Distinguish native observed progress, a conditional mechanical estimate and an unavailable estimate; neither unavailable nor preemptible means zero output or cost.

Start with observed current work, realized output and existing mechanical estimates. Use bounded, in-memory observation windows where throughput needs comparison across frames; reset them on load/world changes and invalidating production changes. Do not derive a production rate from net stock changes alone: consumption, trading and deliveries can change stock without production. When output attribution cannot be established, name that limitation and use observed runway/current bottlenecks rather than fabricating a rate. These derived windows are not a new stored accounting owner.

Use the existing shared forecast/comparator to compare useful settings changes, investments and interventions. Its native-work baseline must include supported ongoing production and displaced work, and must expose unsupported effects instead of treating all unassigned labor as idle. Avoid simulating every native future job. Replace authoritative uses of raw timetable-hour counts with estimates that account for native schedule preferences, current needs/care, travel and other known commitments. Retain such counts only where explicitly labeled as a rough descriptor with no guaranteed production credit.

Work settings may change freely under full bot control. A routine priority reshuffle does not require the materiality rule for direct orders, and an old player setting is not exempt. Compare desired settings to current readback and dispatch differences through the existing writer.

Alternative: a complete deterministic daily pawn schedule. Rejected as the default because exact execution is fragile under native needs and events. Alternative: wait for stock failure before acting. Rejected because reserves, known upcoming demand and recovery lead time require preventive decisions.

### 5. Admit direct orders only for a concrete meaningful reason

Pure policy derives an intervention justification from current shared Needs and observations: a relevant shortfall, credible risk, stalled essential work or worthwhile time-sensitive opportunity. Compare no direct action, useful settings changes and the proposed intervention, including displaced useful work, travel and disruption. This is an extension of the existing comparison, not an adviser writing orders or a hard-coded second food selector.

A small speed improvement while outcomes are adequate is not sufficient. No universal numeric materiality threshold or fixed wait timeout is assumed; use domain outcomes, deadlines and consequence comparison. Settings changes need not be tried and allowed to fail first when their known response time is inadequate. Missing evidence is an explicit blocker to the affected comparison, not permission to assume a colony crisis or adequate service.

Give each admitted intervention a bounded outcome and cancellation condition, fresh native prerequisites, concrete claims and an observable completion. Prefer existing prioritized native work operations. Extend the existing typed order path for finite queue ordering, readback, idempotency and cancellation only where existing supported operations lack required plumbing; do not assume the current `EnqueueFirst` helper supplies those guarantees. Cancel only exact remaining jobs belonging to the intervention, preserving unrelated/new native work. Use short queues; later jobs must not depend on unobserved products or retain a pawn through speculative future work. If an exceptional exact adapter remains necessary, justify its current reader and native cleanup behavior; it does not restore exclusive routine control.

No new LLM run is needed. These decisions remain deterministic C# policy inside the current review cycle. Richer estimates may increase that cycle's computation; use bounded existing comparison and scoped evidence instead of adding a second optimizer.

### 6. Use baby feeding to demonstrate the new default

Keep canonical baby hunger, current native care, usable nursing/food and observed intake. Keep feeding demand in Food and distinguish provisioning from feeding outcomes. Remove `MakeRoomForBabyCare` and `ReleaseForBabyCare`, and remove advance greedy caregiver matching/capacity withholding where it exists solely to control who vanilla must select. Active care remains observed occupied capacity, including carrying and safe return. Recheck it at direct-order admission so a stale review cannot interrupt it.

Continue to arrange missing suitable provision and change childcare/feeding settings through existing intent. An ordinary hungry baby with functioning native care does not require an intervention. Worsening unmet feeding triggers assessment of the actual care, supply or access blocker; a direct response is considered only when meaningful. Preserve normal bottle accumulation before ingestion as distinct from a genuine stall. Working nursing must not mandate unnecessary bottle reserves, while breastfeeding capability alone must not certify usable care or nutrition.

Replace the current research-dependent acceptance setup. A native fixture can stage a deterministic owned task as a precondition through the real journal/Hands path, rather than persuade the portfolio to choose an unrelated research goal. After setup it must neither release the pawn nor order feeding. Assert native work/feeding transitions and final physical outcomes. Use an ordinary interruptible activity at a known native selection boundary; a vanilla forced job is not a valid fixture if the test expects an interruption vanilla would not perform. Exercise true forced/queued semantics separately.

Code inspection already proves today's selection obstruction. Do not write or run a baseline test merely to rediscover that condition. Focus tests on changed behavior: takeover surviving the hooks, ownership/dependency reconciliation, care protection, actual intake and native return. Nurse and bottle cases remain separate physical claims.

## Risks / Trade-offs

- Native work is less predictable than exact schedules -> keep forecasts conditional, compare observed progress and retain unmet demand when supply cannot be established.
- A direct order can itself delay care -> use actual native forced-order semantics, current care admission and meaningful-risk reassessment; do not claim all forced work automatically yields to feeding.
- Restoring selection only partially causes restart loops -> change and test selection, start/end, finalizer and worker reconciliation as one complete execution path.
- Migration can leave two competing routine selectors -> inventory every coupled dispatch caller and close each migration entry before claiming this change complete; temporary milestones remain explicitly partial.
- Rates can lag events or misattribute stock -> invalidate derived windows appropriately, retain units/provenance and never invent production from aggregate deltas.
- Interventions may churn after native takeover -> reconsider fresh evidence and current native progress; do not preserve a queue or retry simply because its old Method exists.

## Migration Plan

1. Inventory and reconcile the unlanded `coordinate-baby-feeding` checkpoint and later edits. Retain useful observations, boundary regressions and independently justified fixes; remove superseded caregiver preallocation/release. Preserve the confirmed food spec and historical evidence. Capture the supersession explicitly when applying; do not blindly reset the branch or mark the old tasks complete.
2. Deliver the first coherent milestone: native selection/takeover compatible with ordinary bot work, existing Method reconciliation, active-care admission, native nursing/bottle intake and safe return. Any legacy exclusive caller remaining must be identified; this milestone alone is not completion of the general change.
3. Migrate ordinary food acquisition/preparation/eating, self-service, production/clothing, construction/delivery and support callers to native work availability/settings. Retain exact tasks only as justified bounded interventions, with physical dependency checks. Remove routine binding/holding hooks and helpers after the last reader migrates; no permanent feature flag or shadow execution lane.
4. Complete shared demand/capacity/native-baseline comparisons, intervention materiality and native queue identity/recovery across the affected supported paths. Do not substitute documentation or follow-up issues for required plumbing.
5. Update contracts/evidence and complete scoped verification, orphan removal and the normal commit/landing lane for each coherent milestone. Roll back through ordinary versioned changes if required; no compatibility shim or alternate scheduler is introduced.

## Evidence and completion

Implementation will create `docs/requirements/colony-labor.md` as the owning labor evidence page and link it from the labor spec, requirements collection and documentation map. Until then this section names the owner without a broken link to a nonexistent file. Preserve [food evidence](../../../docs/requirements/food-security.md) and record only the feeding claims actually established there. Planning status, registered cases, executed results and passed outcomes must remain distinct.

Use focused xUnit tests for intervention decisions, native-work opportunity cost, bounded capacity, takeover ownership/claims, partial outcomes and stale-order rejection. Use short staged native cases for actual selection/takeover and cleanup, both feeding modes, native forced/queue lifecycle and representative ordinary production. Native cases use typed reads and registered telemetry, one physical claim each, about a minute expected wall time and under two including startup. No planner lottery, inflated tick budget or post-setup harness rescue.

Run `openspec validate --all --strict --no-interactive` and the required `RimGovernor.cmd test > test.out 2>&1` on each finished implementation milestone. Registering cases or passing the code gate does not close #4040. Required feeding outcome verification must pass before claiming its acceptance met. Completion of this change additionally requires migration of all affected routine callers, working labor/intervention decisions and no unexplained obsolete readers. No tests or game runs are executed as part of this planning request.

## Architecture Rules 7–9 Review

- Policy owns demand, capacity, comparison and intervention decisions; Runtime composes facts and sequences reviews; the journal owns bot intent and claims; Hands writes orders; native code supplies mechanics and execution observations.
- Work demand and actual occupancy feed shared admission. Native routine work is an observed opportunity cost and physical reservation, not a second bot commitment store. No per-domain takeover scheduler, milk allocator or competing priority writer is added.
- Typed facts name hunger, active care, current jobs, output and unavailable estimates. Missing facts do not become zero capacity, idle labor, completed work or guaranteed future stock. Any added estimate or constraint must serve an actual decision and have a current reader.
