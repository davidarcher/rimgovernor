# Go clock recovery evidence

[Subsystem contracts](README.md) · [Canonical clock schema](../../../contracts/proto/clock.proto)

The Go bridge and store validate clock commands and recovered evidence against the
canonical native producer. Autonomous play (`serve --profile`) composes the scheduler and workers;
validation never restores permission after restart.

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
control, using Go's monotonic clock; unobserved pause intervals stay unknown.
These are controller wall time,
distinct from the game ticks used for onset, observation, dispatch and native
outcomes. Combatlab metrics record a `response` report with runner, fixture,
speed, phase ticks, controller pauses and first observed damage to a hostile.
Hazard reports separately identify native-supervisor detection. Missing phases
remain null; an accepted order does not fill the native-effect field. These
records supply evidence for later calibration, without numeric latency gates.

## Original command correlation

`bridge.ClockExpectation` retains the original world, attempt key, authority owner,
native generation and exactly one start, renewal or speed command. A start retains
its speed, watch policy, lease duration and tick budget. Renewal and speed commands
retain the exact original epoch; their authority generation must match its origin.
Requested durations and observed remaining lease time are evidence, not live lease
tokens. Current authority must be acquired separately before any new command.

`ValidateClockReceipt` checks the full attempt and authorizing owner, admission
context and command-specific epoch facts. An immediately stopped start can be a
valid applied receipt. An uncertain receipt remains uncertain, including when it
contains a status observation; that observation cannot predate admission.

`ValidateClockControlReply` also accepts explicit pre-admission failures and
long-event deferrals. A refusal can describe a replacement world. Attempt conflict
does not establish that the original attempt had no effect. A lookup failure or
unknown result is not a control refusal and cannot authorize another dispatch.
Recovered receipts must be correlated against the original expectation.

`ValidateClockStatus` and `ValidateClockEpoch` check evidence shape and consistency,
not freshness, current ownership or verified pause. `Stopping` can describe a failed
pause that remains armed. Owned pause cleanup uses the original world and epoch owner
and remains separate from start/renew/speed attempts.

## Durable attempts

Start, renewal and speed intents are stored through `Store.PrepareClock`. Exact
request replay returns the existing native attempt. Each command gets a distinct
action ID and attempt number one; allocation and ordinary plan insertion check each
other's IDs in the same SQLite transaction because the native ledger shares one
attempt-key namespace.

`DispatchClock` persists dispatch once before the caller contacts native control.
`MarkClockUncertain` retains missing replies without permitting another dispatch.
`RecordClockReply` validates against the complete original expectation. Applied
and refused results are immutable except for exact replay. A correlated admitted
receipt cannot later be replaced by a pre-admission failure or deferral. A fully
correlated recovered receipt can resolve an in-flight uncertain result.

The catalog retains at most 4,096 attempts; capacity is checked before insertion
(exact replay still works at the limit). Loading fails rather than returning an
incomplete recovery set. Persisted values use bounded canonical encodings and are
validated again on read. No live lease token is stored, and opening a database does
not enable a coordinator.

## Explicit command coordinator

The runtime coordinator serializes commands and receipt recovery. It starts
disabled, binds each command to the complete current authority snapshot and obtains
a fresh lease immediately before dispatch. Authority changes cancel active and
queued calls; shutdown joins journal completion. Renewal and speed changes require
the original start's complete snapshot and a freshly observed matching running epoch.
Only a prepared attempt can dispatch. Recovery reads the exact original attempt;
unknown results never authorize replay or adoption from clock status.

An unknown start is resolved only by two independent native facts under the
original identity: the native attempt ledger (unsaved per load, so unknown under
the same load token means never admitted) and a clock status whose latest epoch
is not new (never started, owned by another session, or an epoch this journal
already holds). Then the journal records a `NOT_FOUND` refusal, which frees the
next start; the status is never adopted as an epoch. A status carrying a newer
epoch owned by this session keeps the attempt uncertain: only its receipt can
resolve it.

## Owned epochs and pause cleanup

An applied start and its required cleanup obligation are stored in one transaction.
The obligation's original epoch is derived from the immutable start receipt.
Uncertain status observations cannot establish ownership, and a missing obligation
for an applied start is an error replay cannot repair.

`BeginClockPause` increments a local sequence and records dispatch before a pause
call; completion must match that sequence. After an uncertain call, fresh evidence
must establish a running owned epoch before another pause can be dispatched. The
sequence is not a native attempt key; owned pause has none.

`AssessClockEpoch` compares immutable owner, origin, policy and tick bounds (speed,
remaining lease and observed tick may change). It distinguishes:

- **required:** the original epoch is still running;
- **uncertain:** pause is still armed, or current state is unavailable or unknown;
- **paused:** the original epoch is stopped and current pause is verified;
- **retired:** the original epoch is explicitly inactive, without claiming current pause;
- **superseded:** a positive world or epoch replacement ends the old obligation.

An inactive epoch must not be paused again merely because the player resumed time.
Retirement and supersession do not satisfy a save or dialog operation's separate
fresh-pause prerequisite. `Stopping` retains an obligation even when it reports a
temporarily paused game. Terminal cleanup evidence is immutable.

## Session lifecycle

The optional complete clock capability shares Session authority and profile ownership.
Startup stays disabled and leaves recovery to explicit cleanup or later worker
composition. Pending starts or owned epochs cannot be opened without clock recovery
capabilities. No constructor starts time or acquires native authority.

Manual invalidates commands before joining them. Cleanup runs under the Control
gate and cannot call Control or request a lease. It assesses each obligation from an
identity read and, under the origin identity, a `clock_read_status`; the scheduler
step that settles a stopped epoch passes the owned status its bundle just read
(`CleanupObserved`), and both reads are skipped for the obligations it describes. A
new acquire drains old owned effects before requesting authority. Close joins
writers, joins the owed clock commands (drafted pawns stay drafted), and retains the
profile, transport and store until cleanup succeeds. Lease-free cleanup remains
usable after the command coordinator stops.

A fresh positive replacement world can durably retire the scope of an unknown start.
The attempt retains its original outcome uncertainty; retirement never means refusal
or no effect. Its immutable proof prevents late replies from creating a new
obligation. Same-world authority changes cannot retire that scope.

## Process event cursors

The native clock journal is an in-memory buffer of the game process (the durable
copy is Go's clock inbox). Its cursors start past the process's start time in Unix
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
The mod announces journal advances on the `rimgovernor.clock` GABP channel
(`{type: advance, newest}`, coalesced; the current cursor is announced to each new
subscriber) and the poll loop reads the page after its cursor when the announcement
version moves (`ClockWorkerConfig.SignalWait`, at most 5 s, bounds a lost
announcement). Gap and loss evidence is the page's, as above. Without the channel
the loop polls unheld at `PollInterval`. Defaults: `serve` waits 4 s, polls at 1 s.

`Event.owner` is required for every event except an `AuthorityChanged` observed
outside an epoch (`epoch = 0`, no owner). `OperationOutcome` rows carry an attempt
key, the latching tick and exactly one receipts effect; one immediately precedes its
`STOP_REASON_WATCH_LATCHED` stop on the same tick, and that stop's `WatchLatched`
evidence repeats the outcome with the window's tick deadline.
`ObservationInvalidated` rows name one to eight distinct `FactFamily` values (never
unspecified) that changed under a running epoch without a controller write or a
stop. A row may narrow the discard: `entity_ids` (at most 64 distinct) name the
changed rows, and `cells` is one rectangle of inclusive, ordered, nonnegative cell
bounds; past the id bound the family alone is named, and a native that cannot
attribute a change omits both. Like outcomes and authority changes they are facts,
not holds.

Valid loss evidence must be retained as a hold. Reading or storing an event does
not acknowledge an interruption, prove it was processed, or permit time to resume.
The controller must persist evidence before advancing its ingestion cursor.

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
- game alerts (`Event_Alert`): planning evidence and an `alert` flight row;
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

## Finite window admission

`policy.EvaluateClockWindow` requires fresh same-authority status and emergency
facts, a paused inactive clock, known remaining work and a complete catalog with
no outstanding owned epoch or unknown start. Reviewed and captured cursors must
match the native newest cursor, with no interruption or gap holds. Native tick
boundaries and durable events must be known (a never-started clock may report
durability as false). The admitted budget is finite and cannot overflow its tick
deadline. Admission carries its snapshot and review revision for dispatch binding.

### Threat and stock rules

- A live, undowned hostile or hunting predator refuses the window (`unsafe_colony`)
  unless the ActiveCombat concern holds an admitted plan with open work; unknown plan
  evidence refuses as `unknown_facts`.
- A hostile building alone refuses only while the defense planner has not reported
  `no_worker:squad` for this stop (`ClockWindowFacts.SquadUnanswered`); after that
  it is watched and the colony window admits.
- A hostile or hunting animal, or hostile building, known to be at least
  `policy.DistantThreatCells` from every colonist is not an emergency (the native
  `hostile_within` radius stops a running window first). A humanlike or mechanoid
  threat, or one whose race or distance is unknown, holds at any distance.
- A method refused only for insufficient stock lends the window a bounded
  `stockWaitTicks` of native work (the census does not see a stack in a hauler's
  hands; without ticks the clearing haul never lands and the window is refused
  `no_work` for good). The same bounded window is lent when a planner's native
  preview is refused or a planner fails on `bridge.ErrRefused`. A transport or
  control failure lends nothing.
- A cooler method that completed on the tick the supervisor latched the window still
  reads `powerOn=false` until the power net ticks, so the refrigeration planner lends
  its cooling allowance to `cooler_power_needed` as well as `cooling`; a cooler still
  unpowered when it runs out is a genuine hold for the power family. The allowance
  keys on the method's dispatch scope and world, not the native generation. A concern
  epoch with no cooler method of its own lends the same allowance from the tick the
  refrigeration latch engaged (`RoundsLatches.RefrigerationSince`) but never covers
  `cooler_power_needed`. Either allowance is one game hour (2500 ticks) per window.
- A skilled furniture or cooler build needs one available pawn with Construction
  enabled at the native skill minimum.
- With an active-combat plan the decision is a combat watch: it names, sorted, every
  live hostile the window acknowledges and uses the combat budget (never above the
  colony budget). Once every hostile is dead or downed it is colony mode again,
  acknowledging nothing. Colonist status, unknown threat status and every other hold
  apply in both modes.

### Dispatch binding

Window starts retain their exact profile, snapshot, tick, review revision, captured
cursor and budget with the immutable clock intent. Durable dispatch checks the
current review in the same transaction: revision and captured/reviewed cursors must
still match, with no holds. Later review does not invalidate historical admission
needed for receipt recovery.

`CommandClockWindow` carries the policy facts through the serialized coordinator.
It checks age and authority after waiting and before dispatch and native execution;
the fresh status must still describe the admitted paused tick and cursor. That status
is the admitting step's own second bundle (`ClockWindowRequest.Status`, stamped
`StatusAt`) while within `MaxAge` (every game-host call is serial, so a
`clock_read_status` after the bundle would report the same paused tick); otherwise
the coordinator reads it natively. In either case the start policy must watch in the
admitted mode with exactly the admitted hostiles acknowledged: colony mode
acknowledges no pawn, combat mode only the decision's hostiles, and medical
suppression lists are never accepted. The ordinary command entry point cannot
dispatch a window-bearing intent without these checks. A dispatched attempt with
uncertain effects is recovered by its original key, never by a replacement start.

## Scheduler step

`ClockScheduler.Step` uses the player's cancellation and serialization scope for
one decision. Its explicit start configuration is colony watch mode without
acknowledgement or medical suppression lists; it derives a combat start (mode,
acknowledged hostiles, combat budget) from the window decision when the current
rounds binds an active ActiveCombat concern whose plan has open work, so a raid runs in
short windows re-planned between them. It checks the shared profile, current plan
work and complete attempt/epoch catalogs before collecting fresh native facts.
Unchanged decision inputs retain the same request ID across repeated calls; an
undispatched stale preparation cannot prevent a fresh decision. Disabled sessions
perform owned cleanup and cannot start. A valid running window is left unchanged.
The step has no polling loop; autonomous play attaches `ClockWorker`.

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

**Critical and optional planners.** The admission cycle waits on the critical class
only. Each catalog entry is `critical` (preempt and critical priority classes and
fire safety) or `optional` (the development reviews). The Rounder review completes first; `StepBudget.Wall` bounds the subsequent
planner wave; past it, planners still
evaluating are named on `ClockSchedulerResult.HeldBy` (`held_by` on the row) and the
step admits nothing (an `admission` row, verdict `refused`, reason `critical_wave_budget`). Optional
planners run on the same snapshot, started after the critical ones, and are joined
for one critical-wave duration more (floored by `StepBudget.OptionalGrace`, never
past the wall budget); those still evaluating are cancelled, listed under
`missed_cutoff`, and their results discarded. Planners write into a private result
merged only if they made the cutoff.

An unmet startup shelter promotes startup planners (`plannerEntry.startup`)
into the critical cycle. Other configured planners remain eligible.

**Latched-hold escape.** While a Worker is attached, admission defers a step on a
latched terminal outcome the Worker has not reconciled, or on queued watched-kind
work not yet dispatched. `clockLatchedHoldMax = 3` bounds those deferrals (per
outcome, per queued action, and for a run of consecutive deferrals). It is a
liveness escape, not a policy cap: a Worker that cannot reconcile or dispatch must
not park the clock. When the bound lets a review through while work is still owed,
the step journals one `admission` decision, verdict `admitted`, reason
`latched_hold_escape`, attrs `actions` (the released action ids) and `hold_max`,
once per action until it leaves the queue.

**Wall-clock regulators.** These are kept sim-health tuning, not policy limits, and
are deliberately not Go-configurable:

| Regulator | Why it is kept |
|---|---|
| `StepBudget` (`Reads`, `Wall`, `NativeWork`, `OptionalGrace`) | Bounds one step's planner waves; an overrun is reported on the `clock_step` row, and a wave past `Wall` holds admission naming its planners instead of failing the step. |
| `DefaultStepWall = 40s` | Under the 60 s step call so a wedged wave is named, not a context deadline. |
| `DefaultOptionalGrace = 1s` | Lets optional reviews finish after a quick critical wave. |
| `clockStopSpanLimit = 10 min` | Discards stop spans that cannot be one review's wait (a long-stopped clock, wall-clock skew). |
| `ConstructionHelpHoldTicks` (one game hour) | Keeps construction helpers across a brief gap in suitable work so they do not flap. |
| `TradeOffersMaxAgeTicks` (three game hours) | Rereads a trader's offers once the record is stale; a freshness bound, not a veto. |
| `ProgressCooldownMax` (three game days) | A failed situation is retried, never banned; it is the retry ceiling of every progress cooldown. |

**Proposals.** Migrated planners return proposals; the coordinator arbitrates them
after the cutoff by `(priority, urgency, id)` against the step's claim index. Before
commit a proposal is revalidated against the step's read validity
(`domain.ReadValidity`). A proposal that reached its arbiter after the cutoff is
carried to the next step's coordinator, where an expired one is reported `expired`
with the stale dependency named (`ProposalOutcome.Stale`) and never commits.

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
(`bridge/frames.go`) past the client's last write; only parameterized reads no frame
carries and the tick read cross GABP. The frame stream is the only cross-step memo
of wire replies. The sections of one frame share its tick, so readers apply no tick
check between rows: a routine read's boundary (`observation.sameColonyContext`)
compares load, map and native generation only.

The scheduler keeps one decoded `facts.Store` (`go/internal/facts`): state per
section (see `facts.Section`), each with the tick its reply described (`AsOf`),
whether it covers the whole section and the producing method. The store's scope is
the load, map and native generation (a new one empties it). It drops sections on a
write, a scope change, `AuthorityChanged`, `EpochStarted`, a stop and the families
an `OperationOutcome` or `ObservationInvalidated` names (`facts.Store.Apply`); a
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

## Independent clock workers

`ClockWorker` runs event polling, renewal and scheduling separately. Scheduling waits
for a successful initial poll. Polling never takes the Player gate: observed
interruptions invalidate permission before persistence, and read or persistence
failures also disable writes. Pages commit before review; no event is automatically
acknowledged. Cleanup uses the original owned epoch independently of live authority.

Renewal requires the exact retained epoch and complete current authority snapshot,
unchanged deadline and speed, and caught-up reviewed event evidence. Uncertain
renewals are read by their original attempt. All clock requests use a
namespace-bound monotonic sequence allocated atomically with the intent. A retired
epoch's historical uncertainty cannot authorize or block renewal in a replacement
scope. Renewal waits without extending the lease when event capture or review is
behind. A verified tick-budget stop defers to event review and scheduler cleanup,
preserving permission for the next eligible window. If the window finishes during
renewal preflight, the renewal stays prepared and undispatched. A fresh, matching
native observation of normal budget completion preserves authority for that handoff;
uncertainty or interruption does not.

The session attaches one clock worker before its loops start. Close cancels and
joins the loops and their cancellation handler before releasing native handles, the
journal or profile owner. Concurrent Stop calls serialize, successful cleanup is
cached, failed cleanup is retryable. Poll and renew intervals and call budgets are
bounded below a quarter of the native lease duration so a late renew can never let
the epoch lapse. The step budget is independent of the lease: a step holds the
Player gate, never `renewGate`, so a slow planner census cannot delay renewal
(`serve` budgets 7 s for poll/renew and 30 s for the step; the scheduler's `MaxAge`
bounds the admission reads). Autonomous play attaches the worker; `--observe` does
not.

### Wake, backoff and deferral

Unchanged scheduling decisions back off, except while a combat window is admitted or
running. A captured page wakes the step loop through `WakeSignal` and resets the
backoff, carrying outcomes, invalidated families and the authority flag as the
step's `StepReason`. The poll also wakes the routine `Worker` to reconcile actions
named by `OperationOutcome` or `WatchLatched` evidence ahead of its rotation and
without retry backoff (at most 64 focused actions).

A wake's terminal outcome for an attempt the plan still shows dispatched defers the
admission (`ClockSchedulerResult.Deferred`): at most three steps per outcome and
three deferred steps in a row, and not while another watched attempt is in flight or
a watched successor is queued but undispatched. The step loop steps again when a
worker step advances any action (`WorkerConfig.Advanced`) or a `StepInterval` later,
without backoff.

No admission waits for the stop between windows: every routine kind the worker
dispatches validates its preconditions natively at apply time (action-contracts.md
"Apply-time preconditions") and dispatches under the running window. A dispatch the
executor holds on `stale_facts` (`workerHeldStale`) is retried at once, off the
worker's backoff and once per hold; the clock is not held for the retry.

A routine window arms no watches: a completed order is not a reason to stop the
clock, and the `OperationOutcome` row wakes the worker under the running window.
An applied building on a plan not yet retired counts as clock work, so the window
runs until the construction census retires the plan. Dispatched construction and
haul attempts a running window does not watch are counted as evidence only
(`ClockSchedulerResult.Unwatched`).

A coupled order (`domain.ActionDependency.Coupled`, controller-contracts.md "Coupled
orders") does not stop the window: when its prerequisite has completed at the epoch's
current tick and the order is still undispatched (`PlanSpec.CoupledPending`), the
step reports `Coupled`, plans live for it whatever the wave's cadence says, and
leaves the epoch running.

### Live planning

A step that finds its own window running plans under it: planners read the bundle's
snapshot (one main-thread hop) and commit plans the worker dispatches live; nothing
is admitted, and the stop that ends the window reviews and admits as before. A
`timer` step is promoted to `live` once `FullStepEvery` has passed; a `wake` or
`full` step goes live at once.

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

Every routine result embeds one `Verdict` (`buildingruntime/outcome.go`): an
`Outcome` (`admitted`, `nothing_to_do`, `disabled`, `no_current_review`, `expired`,
`combat_orders`, `hold_fallback`, `waiting`, `refused`) and, when refused or waiting,
a `Refusal` with a closed `Kind`, a subject and optional detail. A refusal or wait
without a kind panics at construction; there is no catch-all kind, and routines
branch on outcome or kind, never text. The kind lists are the constants in
`outcome.go`.

`Verdict.String` is the machine token (`kind[:subject[:detail]]`, no spaces) for the
flight rows' `reason` and snapshot names. Persisted progress and the wire carry the
typed `policy.Cause` plus a subject bounded to `policy.MaxSubjectLen`
(`ConcernProgress.Planner`, `PlannerSubject`); English for a cause lives only in
`policy.Wording`, which the launcher renders.

A planner's catalog entry names the one concern it serves (`plannerEntry.concern`), and the
wave files its verdict there:

- a refusal files its cause as the block;
- a wait files its wait cause (`Cause.Waiting`; no warning);
- a disabled planner files the opt-out hold (`held:opt-in`);
- `already_working_on_it` waits on the earlier work;
- `combat_orders` and `hold_fallback` file their own wait causes (nothing failed);
- an admitted or nothing-to-do verdict clears the concern's refusal or wait;
- a verdict that says nothing about the concern (no review, a stale proposal) files nothing.

Siblings on one concern keep the strongest note: refusal, then wait, then clear, then
opt-out.

### Reopened state

A fresh worker over reopened state remains disabled while recovering original
attempts and pausing retained ownership; it acquires no authority and issues no new
Start or Renew. A native reply that ignores cancellation keeps shutdown retryable and
the profile locked until the call returns, its receipt is persisted and owned cleanup
joins.

## Clock history retirement

Clock request IDs bind the journal namespace and a positive monotonic sequence. The
scheduler stores its logical decision key separately and reuses retained exact
intents. Retirement never resets allocation: removed requests return ErrRetired, and
a missing retained row is corruption.

`RetireClockHistory` atomically removes eligible attempts and terminal epochs while
preserving unresolved writes, nonterminal ownership, retained commands' Start
provenance, the latest scheduling window and a bounded recent tail. Its expected
state checks namespace and allocation/retirement watermarks; the transaction
recomputes eligibility from current rows. A bounded retained index detects missing
pinned records without tombstones. Event polling invokes retirement after each 128
newly allocated requests, retaining the latest 128 attempts besides pinned evidence;
a concurrent allocation defers it to the next poll, and other maintenance failures
disable control and invoke owned cleanup. Maintenance does not take the player gate,
acknowledge events or restore authority.

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
