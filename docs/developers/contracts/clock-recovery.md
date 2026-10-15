# Clock recovery evidence

[Subsystem contracts](README.md) · [Clock wire contract](../../../contracts/wire/clock-lifecycle.md)

the brain bridge and store validate clock commands and recovered evidence against the
canonical native producer. Autonomous play (the in-process host, see [brain host](../architecture/brain-host.md)) composes the scheduler and workers;
validation never restores permission after restart.

> The clock is continuous run with named holds (epic #3168): `Run(target speed, policy)`, `Hold(reason)` and an
> explicit heartbeat. There is no clock attempt, epoch, lease, window or receipt, and no clock persistence in the store
> (see [Execute under supervision](../architecture/control-loop.md#execute-under-supervision) and the
> [clock wire contract](../../../contracts/wire/clock-lifecycle.md)).

## Immediate protection and ordinary review

The scheduler uses the same Concern store, due queue and Hands executor for
both review scopes. Immediate review updates only combat/cleanup, emergency
medicine, fire and protective-area state. Omitted Concerns retain their prior
inspection, Methods and progress; an urgent observation does not refresh an
ordinary fact. Ordinary review retains its own inspection tick.

Protective actions yield the scheduler gate to the existing Worker before
unrelated development reads. Pending actions remain ahead of ordinary review
until dispatch or an explicit current hold; a hold does not permanently veto
ordinary work. Native window admission continues through its existing safety,
authority, journal and lease checks. Ordinary review can run while that window
advances. Relevant invalidations select planners through catalog dependencies;
unrelated section changes do not invalidate a protective decision.
The independent event poll cancels an in-flight ordinary review when it captures
a native stop, releasing the same gate for fresh protection. It does not cancel
the immediate scope or revoke valid authority merely to interrupt development.

`clock_step.critical_wave_ms` includes review. `controller_pause_ms` measures
each scheduler interval from observing a paused native clock through returning
control, using the brain's monotonic clock; unobserved pause intervals stay unknown.
These are brain wall time,
distinct from the game ticks used for onset, observation, dispatch and native
outcomes. Combatlab metrics record a `response` report with runner, fixture,
speed, phase ticks, brain pauses and first observed damage to a hostile.
Hazard reports separately identify native-supervisor detection. Missing phases
remain null; an accepted order does not fill the native-effect field. These
records supply evidence for later calibration, without numeric latency gates.

## Session lifecycle

The optional complete clock capability shares Session authority and profile ownership. Startup stays disabled and
sends no `Run`; no constructor starts time or acquires native authority. Manual and Stop hold the clock for review
(`HOLD_REASON_REVIEW`) once, under the Control gate; a hold of an already held clock keeps the first reason. Nothing
about the clock is persisted: a restart finds native held (`NEVER_RUN` or the last reason) until the brain sends `Run`.

## Process event cursors

The native clock journal is an in-memory buffer of the game process (the durable
copy is the brain's clock inbox). Its cursors start past the process's start time in Unix
milliseconds, so a relaunched game's cursors exceed every cursor an older store
holds. A page's observation context matches the requested current world; each event
retains its original world and epoch. A load or map change does not reset the cursor
or rewrite old event contexts. Event reads initialize the journal before any owned
start; status reads initialize the tick watchers. Neither advances time or acquires
authority.

`ValidateClockEventsPage` checks the typed request and page together.
`oldest_cursor` is the process's first cursor: a read from below it starts at
`oldest_cursor - 1` with no loss. From that start the native reader scans at most
the requested limit of cursor positions, counting rows evicted from the buffer as
loss. Therefore `next_cursor - start` equals returned events plus `lost_count`;
`gap` is true exactly when loss is reported. Returned events are strictly ordered
within the scanned window. An empty journal can report `oldest_cursor = 0` and
`newest_cursor = 0`; an absent oldest cursor is also valid; a positive oldest cursor
requires a nonempty journal, and zero is invalid for a nonempty one.

Reads are never held: `EventsRequest` has no wait field.
The poll loop reads the page after its cursor unheld at `ClockRunConfig.PollInterval`. Gap and loss evidence is the
page's, as above.

Native journal events are `HoldEntered` (reason, evidence, detected and occurrence tick) and `Resumed` (speed, from),
plus the facts below. A brain-requested hold is not a decision event; a hold for a non-benign native hazard reason is.
`ObservationInvalidated` rows name one to eight distinct `FactFamily` values (never
unspecified) that changed under a running clock without a brain write or a
stop. A row may narrow the discard: `entity_ids` (at most 64 distinct) name the
changed rows, and `cells` is one rectangle of inclusive, ordered, nonnegative cell
bounds; past the id bound the family alone is named, and a native that cannot
attribute a change omits both. Like outcomes and authority changes they are facts,
not holds.

Valid loss evidence must be retained as a hold. Reading or storing an event does
not acknowledge an interruption, prove it was processed, or permit time to resume.
The brain must persist evidence before advancing its ingestion cursor.

`BindClockInbox` binds one canonical existing profile directory to the database.
`AppendClockEvents` atomically stores the request, page, surviving immutable events
and cursor progress. Exact retained request/page replay is a no-op. A changed stale
page must be refetched from the stored cursor; it cannot rewrite prior evidence.
Missing profile metadata with retained history is an error, not a new binding.

`ReadClockInbox` and `LoadClockInbox` validate complete retained history and derive
the cursor, sticky gap, loss count and catalog sizes. Storage is bounded to 4,096
pages, 4,096 events and 16 MiB of encoded requests/pages. Capacity failure leaves
the entire append uncommitted. Review and acknowledgement are separate durable
operations; ingestion alone never clears a gap or an interruption. Polling compacts
eligible reviewed history before ingesting another page.

## Review and acknowledgement

Captured and reviewed cursors are distinct. Review consumes only committed pages
and derives interruption and gap holds from that evidence. These never create
interruption holds:

- ordinary epoch starts, speed changes, hostiles-cleared, force-pause-cleared,
  force-pause waits (an autosave or transient pause native says is not a stop; one that
  outlasts its grace becomes a `FORCE_PAUSED` stop),
  operation outcomes and authority changes;
- game alerts (`Event_Alert`): planning evidence and an `alert` row;
- injury observations (`Event_InjuryObserved`): sub-threshold damage native coalesces;
- notifications (`Event_Notification`): only classes native never stops play for;
- the benign stops (`store/clock.BenignStop`): tick budget, requested pause, watch
  latched, and a letter pause for an informational letter.

Pause failures and other stop events remain conservative holds.
Cleared conditions cannot erase an earlier unacknowledged event.

Acknowledgements require an exact review revision and reviewed cursor. An exact
request replay returns its historical result and never acknowledges later data.
Consumers must read current review state before a new policy decision and require
the reviewed cursor to match the current captured cursor. Acknowledgement records
inspection only; current unsafe facts and missing native events still hold.

The bounded review log validates its canonical head against its checkpoint,
retained operations and inbox provenance on every load. Capacity refusal leaves
state unchanged. Compaction preserves exact acknowledgement results in an indexed
archive, queried only by request ID; later events cannot change those results.

## Run decision

`policy.EvaluateClockWatch` requires fresh same-authority status and emergency facts and a complete catalog. Reviewed
and captured cursors must match the native newest cursor, with no interruption or gap holds. Native tick boundaries
and durable events must be known (a never-run clock reports `HOLD_REASON_NEVER_RUN`). The decision names the watch
mode and the acknowledgements the next `Run` carries. There is no budget, window or admission: a refusal only keeps
a held clock held.

### Threat and stock rules

- A live, undowned hostile or hunting predator refuses the window (`unsafe_colony`)
  unless the ActiveCombat concern holds an admitted plan with open work; unknown plan
  evidence refuses as `unknown_facts`.
- A hostile building alone refuses only while the defense planner has not reported
  `no_worker:squad` for this stop (`ClockWatchFacts.SquadUnanswered`); after that
  it is watched and the colony window admits.
- A hostile or hunting animal, or hostile building, known to be at least
  `policy.DistantThreatCells` from every colonist is not an emergency (the native
  `hostile_within` radius stops a running window first). A humanlike or mechanoid
  threat, or one whose race or distance is unknown, holds at any distance.
- A skilled furniture or cooler build needs one available pawn with Construction
  enabled at the native skill minimum.
- With an active-combat plan the decision is a combat watch: it names, sorted, every
  live hostile the window acknowledges and uses the combat budget (never above the
  colony budget). Once every hostile is dead or downed it is colony mode again,
  acknowledging nothing. Colonist status, unknown threat status and every other hold
  apply in both modes.

## Scheduler step

`ClockScheduler.Step` uses the player's cancellation and serialization scope for one decision. It only plans: it
reads the bundle with the clock status, runs the planner wave and returns a `ClockSchedulerResult`; it starts and
stops nothing. `ClockRunDriver.AfterStepAsync` acts on the result (see Run driver). The step has no polling loop;
autonomous play attaches `ClockRunDriver`.


### Planner wave

The rounder runs first and its failure aborts the step; every other composed planner
then runs as one concurrent wave whose failures are isolated: a planner whose native
read is refused or whose preview is stale commits nothing and is reported in
`ClockSchedulerResult.PlannerFailures`, but its peers finish and the window is still
evaluated on what they committed. Only the step's own context ending fails the wave.
Planners are admitted in concern-priority order, at most `bridge.MaxConcurrentCalls`
at a time, so a tight budget is spent on the highest priorities first. Native reads
still execute one at a time on the game's main thread.

**Admission classes.** The bridge hands call slots out by `AdmissionClass`: `control`
(clock, authority, operations, receipts, lifecycle, placement previews),
`observation` (`observations_*`, presentation state, leases) or `mirror` (the clock
events read: one call at a time, outside the shared slots). One slot is
reserved for control and a waiting control call is admitted before any waiting read,
so a renew or stop never queues behind a burst of reads. The class rides beside
`request` and `trace` on the wire, and the companion's `MainThreadAdmission` runs
queued control hops before observation hops within a frame (untyped tools bypass
that ordering).

**Critical and optional planners.** The wave waits on the critical class
only. Each catalog entry is `critical` (combat, custody, medicine, recovery, fire safety and the like) or `optional` (the development reviews). The Rounder review completes first; `StepBudget.Wall` bounds the subsequent
planner wave; past it, planners still
evaluating are named on `ClockSchedulerResult.HeldBy` (`held_by` on the row) and the
step does not complete (`HeldException`; the next step evaluates again). Optional
planners run on the same snapshot, started after the critical ones, and are joined
up to the same wall budget; those still evaluating are cancelled, listed under
`missed_cutoff`, and their results discarded. Planners write into a private result
merged only if they made the cutoff.

**Wall-clock regulators.** These are kept sim-health tuning, not policy limits, and
are deliberately not configurable:

| Regulator | Why it is kept |
|---|---|
| `StepBudget` (`Reads`, `Wall`) | Bounds one step's planner waves; an overrun is reported on the `clock_step` row, and a wave past `Wall` holds admission naming its planners instead of failing the step. |
| `DefaultStepWall = 40s` | Under the 60 s step call so a wedged wave is named, not a context deadline. |
| `clockStopSpanLimit = 10 min` | Discards stop spans that cannot be one review's wait (a long-stopped clock, wall-clock skew). |
| `ConstructionHelpHoldTicks` (one game hour) | Keeps construction helpers across a brief gap in suitable work so they do not flap. |
| `TradeOffersMaxAgeTicks` (three game hours) | Rereads a trader's offers once the record is stale; a freshness bound, not a veto. |
| `ProgressCooldownMax` (three game days) | A failed situation is retried, never banned; it is the retry ceiling of every progress cooldown. |

**Read validity.** `domain.ReadValidity` is a step's scope (colony, map, load,
native generation), the plan revision it commits under, the observation tick, and an
`Invalid` reason when the whole view was invalidated (event gap, reload, unsupported
mutation). A read is stale only when its scope differs or the view is invalidated;
the read's tick never makes it stale, because native revalidates every action on
apply. Validity rides the step context and reaches the Worker
(`ClockScheduler.Validity`, `WorkerConfig.Validity`). Section versions live on
`facts.Store.Versions`: each moves on an invalidation naming the section, on a
whole-view invalidation and on a scope change, never on a refresh at cadence. A
`dispatch` run held on stale facts is marked `stale` (`stale_holds`).

### Fact sources

Every state read a step issues is served from the newest snapshot frame
(`FacadeNativeClient.Frames.cs`) past the client's last write; only parameterized reads no frame
carries and the tick read cross the native channel. The frame stream is the only cross-step memo
of wire replies. The sections of one frame share its tick, so readers apply no tick
check between rows: a routine read's boundary (`observation.sameColonyContext`)
compares load, map and native generation only.

The scheduler keeps one decoded `facts.Store` (`Facts/Store.cs`): state per
section (see `Section`), each with the tick its reply described (`AsOf`),
whether it covers the whole section and the producing method. The store's scope is
the load, map and native generation (a new one empties it). It drops sections on a
write, a scope change, `AuthorityChanged`, `EpochStarted`, a stop and the families
an `OperationOutcome` or `ObservationInvalidated` names (`Store.Apply`); a
narrowed rectangle that misses the held planning window leaves it held.
`/api/routines` lists the held sections.

The rounds read every continuous section (`research`, `population`, `rooms`,
`pawns`) from the frame on every review; the entity list reads (`zones`,
`buildings`, `bills`) are read whole on every review step (`refreshEntitySections`)
and filed as sections keyed by id (`buildingruntime.EntitySection`).

`planning_cells` has its own read: the window (colony centre +/- 22,
`bridge.PlanningWindowRect`) is cut by `bridge.ReadPlanningWindow` from the whole-map
cell grid every snapshot frame carries (`bridge/cellgrid`); a client without a stream
has no window. Fogged cells are never listed and the row carries no `reachable`
(`placement_preview` refuses an unreachable site). A failed read serves the held
window of the same region.

The review retains its census for the step's planners (`roundsCensus`): a planner of
the same load, map and native generation plans from it at any tick at or after the
review until committed clock evidence invalidates it. There is no paused review
bracket: CAS evidence on every write refuses a decision the world moved past.

## Run driver

`ClockRunDriver` runs three loops separately: the native journal poll, the heartbeat and the step loop. The step loop
calls `AfterStepAsync` after every scheduler step. Polling never takes the Player gate: observed interruptions
invalidate permission before persistence, and read or persistence failures also disable writes. Pages commit before
review; no event is automatically acknowledged.

The driver is the hold state machine. Native is Running at the target speed or Held for a named reason, and only a
`Run` ends a hold. After each step the driver diffs the watch policy the facts imply against the last one sent and
resends `Run` when it differs (`Run` is idempotent and doubles as the speed and policy change). It holds the clock
itself for a newly ready coupled order and for a due autosave, and resumes with `Run` when the decision allows it. A
refused decision never stops a running clock: native holds on what it sees. A refused resume is a `HeldException`
and is retried at the next step. The heartbeat loop beats every `HeartbeatInterval` (under the native tolerance of
five seconds); losing the beat lowers the native ceiling to Normal and is neither journalled nor a hold. When
authority ends under a `Run` this driver sent, the same loop holds the clock for review (`no_authority`).

The session attaches one clock driver before its loops start. Close cancels and joins the loops before releasing
native handles, the journal or profile owner. Concurrent Stop calls serialize, successful cleanup is cached, failed
cleanup is retryable; Stop drops authority and holds the clock for review. Autonomous play attaches the driver;
observe-only (`RIMGOVERNOR_OBSERVE`) does not.

### Wake, backoff and deferral

Unchanged scheduling decisions back off, except while a combat watch is running. A captured page wakes the step
loop through `WakeSignal` and resets the backoff, carrying invalidated families and the authority flag as the step's
`StepReason`. The poll also wakes the routine `Worker` to reconcile actions named by `OperationOutcome` evidence ahead
of its rotation and without retry backoff (at most 64 focused actions).

No admission waits for the stop between windows: every routine kind the worker
dispatches validates its preconditions natively at apply time (action-contracts.md
executor holds on `stale_facts` (`workerHeldStale`) is retried at once, off the
worker's backoff and once per hold; the clock is not held for the retry.

A running clock arms no watches: a completed order is not a reason to hold the clock, and the `OperationOutcome` row
wakes the worker. Dispatched construction and haul attempts are counted as evidence only
(`ClockSchedulerResult.Unwatched`).

A coupled order (`domain.ActionDependency.Coupled`, controller-contracts.md "Coupled orders") whose prerequisite has
completed and which is still undispatched (`PlanSpec.CoupledPending`) takes a brain hold (`HOLD_REASON_COUPLED_ORDER`):
the step plans against the held world and the driver sends Run again once it has.

### Live planning

The clock is Running whenever the brain is not holding it, so every step plans live: planners read the bundle's
snapshot (one main-thread hop) and commit plans the worker dispatches under the running clock. A `timer` step is
promoted to `live` once `FullStepEvery` has passed; a `wake` or `full` step goes live at once.


### Step causes and the due queue

`StepReason.Cause` is `timer`, `wake`, `settled`, `full` or `live`. The scheduler
queues the `plannerCatalog` entries `plannerSelection` picks over its due queue
(`plannerQueue`):

| Cause | Entries run |
| --- | --- |
| `full`, `settled` | every configured entry |
| `timer` | entries whose next review tick (`plannerEntry.reviewEvery`) has passed (see the code for per-class cadence); with nothing due the step runs the admission tail alone. Promoted to `full` once `FullStepEvery` has passed without a full wave |
| `wake` | entries whose dispatched `kinds` include a latched outcome's kind (an unremembered action selects all) or whose declared `sections` include one an `ObservationInvalidated` row dirtied (`facts.Invalidation.Sections`); all of them when authority changed |

An entry that reported `existing_work` waits on the open attempts of its kinds
(`plannerQueue.waits`): it is not selected again until one reaches its outcome row,
its next review tick passes, or a full step runs, and a step that skips it lists it
under `waiting`. The rounder runs before any wave; its retained census is retired by
any typed-event invalidation (`roundsCensusStore` generation).

### Routine verdicts

Every routine result embeds one `Verdict` (`Outcome.cs`): an
`Outcome` (`admitted`, `nothing_to_do`, `disabled`, `no_current_review`, `expired`,
`combat_orders`, `hold_fallback`, `waiting`, `refused`) and, when refused or waiting,
a `Refusal` with a closed `Kind`, a subject and optional detail. A refusal or wait
without a kind panics at construction; there is no catch-all kind, and routines
branch on outcome or kind, never text. The kind lists are the constants in
`Outcome.cs`.

`Verdict.String` is the machine token (`kind[:subject[:detail]]`, no spaces) for the
rows' `reason` and snapshot names. Persisted progress and the wire carry the
typed `policy.Cause` plus a subject bounded to `policy.MaxSubjectLen`
(`ConcernProgress.Planner`, `PlannerSubject`); English for a cause lives only in
`policy.Wording`, which the launcher renders.

A planner's catalog entry names the one concern it serves (`plannerEntry.concern`), and the
wave files its verdict there:

- a refusal files its cause as the block;
- a wait files its wait cause (`Cause.Waiting`; no warning);
- a disabled planner files the opt-out hold (`held:opt-in`);
- a `MaintainResource` floor whose resource has an empty source census (`ReadResourceSources`
  returns no deposit, tree or plant) files `held:no_source`, naming the resources
  comma-joined; the hold is not a production block;
- `already_working_on_it` waits on the earlier work;
- `combat_orders` and `hold_fallback` file their own wait causes (nothing failed);
- an admitted or nothing-to-do verdict clears the concern's refusal or wait;
- a verdict that says nothing about the concern (no review, a stale proposal) files nothing.

Siblings on one concern keep the strongest note: refusal, then wait, then clear, then
opt-out.

### Reopened state

A fresh driver over reopened state remains disabled: it acquires no authority and sends no `Run`. Native stays held
(`NEVER_RUN`, or the last hold) until the brain sends `Run` under a fresh grant. A native reply that ignores
cancellation keeps shutdown retryable and the profile locked until the call returns.

## Event and review maintenance

Event/review maintenance checkpoints the validated review head and retires a
contiguous reviewed prefix, retaining eight recent pages and every unreviewed or
unacknowledged interruption/gap. Cursors, revision, profile binding and cumulative
loss evidence survive compaction and restart. Deletion, checkpoint advancement and
acknowledgement archival commit atomically. Maintenance runs at 128 active
reviews/pages, 256 events or half the byte budget. Unresolved holds can still fill
the bounded inbox and fail closed; they are never discarded to make space.

With clock supervision enabled, GET /api/player/clock exposes durable review
revision, cursors and interruption/gap holds. POST /api/player/clock/acknowledge
requires the player session token, an explicit request ID, expectedRevision and
throughCursor; counters use canonical decimal strings. A changed revision returns a
conflict; exact replay uses the retained request ID. It only acknowledges inspected
evidence and never acquires authority or resumes time. The launcher preserves its
last review during refresh failures and offers an explicit retry of the same
acknowledgement after an uncertain response.

### Zone policy section

The `zones` section supplies farm capacity, raw per-crop growth facts (plant growth,
fertility, temperature, the crop's growth range, blight; policy computes growing
cells and the harvest lead from them and the calendar), food-stockpile suitability and the guarded zone-map token.
`observations_read_colony_facts` omits `farms`, `food_storage` and
`planning.zone_map_snapshot`; routine policy and zone creation read the zone
section. `observations_list_zones` reads the census whole, in pages of 16; every
reply carries `map_snapshot`, each growing row `farm`, each zone `food_storage`. A
stale colony invalidation or scope change rereads the census whole.
