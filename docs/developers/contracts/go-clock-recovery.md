# Go clock recovery evidence

[Subsystem contracts](README.md) · [Canonical clock schema](../../../contracts/proto/clock.proto)

The Go bridge and store validate clock commands and recovered evidence against the
canonical native producer. Autonomous play (`serve --profile`) composes the scheduler and workers;
validation never restores permission after restart.
See native service acceptance
for the game-level verification procedure.

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

`ValidateClockStatus` and `ValidateClockEpoch` check evidence shape and consistency.
They do not establish freshness, current ownership or verified pause. In particular,
`Stopping` can describe a failed pause that remains armed. Owned pause cleanup uses
the original world and epoch owner and remains separate from start/renew/speed
attempts.

## Durable attempts

The fresh Go schema stores start, renewal and speed intents through
`Store.PrepareClock`. Exact request replay returns the existing native attempt.
Each command gets a distinct action ID and attempt number one; allocation and
ordinary plan insertion check each other's IDs in the same SQLite transaction
because the native ledger shares one attempt-key namespace.

`DispatchClock` persists dispatch once before the caller contacts native control.
`MarkClockUncertain` retains missing replies without permitting another dispatch.
`RecordClockReply` validates against the complete original expectation. Applied
and refused results are immutable except for exact replay. A correlated admitted
receipt cannot later be replaced by a pre-admission failure or deferral. A fully
correlated recovered receipt can resolve an in-flight uncertain result.

The catalog retains at most 4,096 attempts. Capacity is checked before insertion;
existing request replay still works at the limit. Loading a catalog fails rather
than returning an incomplete recovery set. Persisted command and reply values use
bounded canonical encodings and are validated again on read. No live lease token
is stored, and opening a database does not enable a coordinator.

## Explicit command coordinator

The gated runtime coordinator serializes commands and receipt recovery. It starts
disabled, binds each command to the complete current authority snapshot and obtains
a fresh lease immediately before dispatch. Authority changes cancel active and
queued calls; shutdown joins journal completion. Renewal and speed changes require
the original start's complete snapshot and a freshly observed matching running epoch.

Only a prepared attempt can dispatch. Recovery reads the exact original attempt;
unknown results never authorize replay or adoption from clock status.

An unknown start is resolved only by two independent native facts under the
original identity: the native attempt ledger (unsaved per load, so unknown under
the same load token means never admitted) and a clock status whose latest epoch
is not new — never started, owned by another session, or an epoch this journal
already holds. Then the journal records a `NOT_FOUND` refusal, which frees the
next start; the status is never adopted as an epoch. A status carrying a newer
epoch owned by this session keeps the attempt uncertain: only its receipt can
resolve it.

## Owned epochs and pause cleanup

An applied start and its required cleanup obligation are stored in one transaction.
The obligation's original epoch is derived from the immutable start receipt.
Uncertain status observations cannot establish ownership, and a missing obligation
for an applied start is an error rather than something replay can repair.

`BeginClockPause` increments a local sequence and records dispatch before a pause
call. Completion must match that sequence. After an uncertain call, fresh evidence
must establish a running owned epoch before another pause can be dispatched.
This sequence is not a native attempt key; owned pause has no native attempt key.

`AssessClockEpoch` compares immutable owner, origin, policy and tick bounds. Speed,
remaining lease and observed tick may change normally. It distinguishes:

- **required:** the original epoch is still running;
- **uncertain:** pause is still armed, or current state is unavailable or unknown;
- **paused:** the original epoch is stopped and current pause is verified;
- **retired:** the original epoch is explicitly inactive, without claiming current pause;
- **superseded:** a positive world or epoch replacement ends the old obligation.

An inactive epoch must not be paused again merely because the player resumed time.
Retirement and supersession do not satisfy a save or dialog operation's separate
fresh-pause prerequisite. `Stopping` retains an obligation even when it reports a
temporarily paused game. Terminal cleanup evidence remains immutable.

## Session lifecycle

The optional complete clock capability shares Session authority and profile ownership.
Startup stays disabled and leaves recovery to explicit cleanup or later worker
composition. Pending starts or owned epochs cannot be opened without clock recovery
capabilities. No constructor starts time or acquires native authority.

Manual invalidates commands before joining them. Cleanup runs under the Control
gate and cannot call Control or request a lease. It assesses each obligation
from an identity read and, under the origin identity, a `clock_read_status`;
the scheduler step that settles a stopped epoch passes the owned status its
bundle just read (`CleanupObserved`) and both reads are skipped for the
obligations that status describes (#200). A new acquire drains old owned
effects before requesting authority. Close joins writers, joins the owed clock
commands (drafted pawns stay drafted, #939), and retains the profile, transport and store until cleanup
succeeds. Lease-free cleanup remains usable after the command coordinator stops.

A fresh positive replacement world can durably retire the scope of an unknown
start. The attempt retains its original outcome uncertainty; retirement never
means refusal or no effect. Its immutable proof prevents late replies from
creating a new obligation. Same-world authority changes cannot retire that scope.

## Process event cursors

The native clock journal is an in-memory buffer of the game process (the
durable copy is Go's clock inbox). Its cursors start past the process's start
time in Unix milliseconds, so a relaunched game's cursors exceed every cursor
an older store holds. A page's observation context matches the requested
current world; each event retains its original world and epoch. A load or map
change does not reset the cursor or rewrite old event contexts.

Event reads initialize the journal before any owned start. Status reads
initialize the tick watchers so the scheduler can inspect readiness before
requesting a window. Neither operation advances time or acquires authority.

`ValidateClockEventsPage` checks the typed request and page together.
`oldest_cursor` is the process's first cursor: a read from below it starts at
`oldest_cursor - 1` with no loss. From that start the native reader scans at
most the requested limit of cursor positions, counting rows evicted from the
buffer as loss. Therefore `next_cursor - start` equals the number of returned
events plus `lost_count`; `gap` is true exactly when loss is reported.
Returned events are strictly ordered within the scanned window.

`EventsRequest.wait_ms` (0–5000) turns a read into a long poll: a page that
already has rows or loss answers at once; an empty page holds the call until a
row lands past the cursor or the wait lapses, then is read again without
waiting. The native side registers the waiter under the same lock that found
the page empty, so an append between the read and the wait cannot be missed,
and keeps at most four waiters so waiting readers never starve other tools.
`ClockWorkerConfig.PollWait` must leave one second of the poll call budget for
the two main-thread hops and is applied only while the scheduler reports a
window it admitted running (`ClockScheduler.WindowRunning`); otherwise the
read is not held, because a held read would queue ahead of the review's own
reads. `serve` holds for 4 s (less when a short call timeout leaves no room):
the companion dispatches its tools off the GABP reader (issue #227), so the
routine worker's dispatch calls and the lease renew no longer queue behind
the held poll (the issue #162 stall). Between windows it polls unheld at
`PollInterval` (1 s). A poll that returns early with nothing (a native build
ignoring `wait_ms`) falls back to that cadence instead of spinning.

`Event.owner` is required for every event except an `AuthorityChanged` observed
outside an epoch, which the native supervisor publishes from the authority
generation seam with `epoch = 0` and no owner, and a `PlayerRequest` (#957),
published when the player presses a status-panel button: an action id the
controller offered on its status strip and a fresh request id, both 1-64
printable ASCII characters. The poll hands requests past the history watermark
from the current world to the routine reviewer, which answers at its next
review; a press under Manual authority waits for Auto. `OperationOutcome` rows carry an
attempt key, the latching tick and exactly one receipts effect; an
`OperationOutcome` immediately precedes its `STOP_REASON_WATCH_LATCHED` stop on
the same tick, and the stop's `WatchLatched` evidence repeats the outcome with
the window's tick deadline. `ObservationInvalidated` rows name one to eight
distinct `FactFamily` values (never unspecified) that changed under a running
epoch without a controller write or a stop; the native supervisor publishes
them from its probe when a research project finishes (`research`,
`definitions`), a faction's relation, goodwill or defeat state moves
(`world`), the set of game conditions affecting the map changes (`colony`,
whose environment census carries them) or a zone's cell set changes
(`colony`). A row may narrow the discard (#359): `entity_ids` (at most 64
distinct identifiers) name the changed rows of the families, and `cells` is
one rectangle of inclusive, ordered, nonnegative cell bounds the change
touched; a zone edit sends the changed zones' ListZones ids and the
rectangle their old and new cells span, and past the id bound the family
alone. A native that cannot attribute a change omits both, and an older
native never sends them. Like outcomes and authority changes they are
facts, not holds.

An empty journal can report `oldest_cursor = 0` and `newest_cursor = 0`. An absent
oldest cursor is also valid. A positive oldest cursor requires a nonempty journal;
zero is not a valid oldest cursor for a nonempty journal.

Valid loss evidence must be retained as a hold. Reading or storing an event does
not acknowledge an interruption, prove that it was processed, or permit time to
resume. The controller must persist evidence before advancing its ingestion cursor.

`BindClockInbox` binds one canonical existing profile directory to the database.
`AppendClockEvents` atomically stores the request, page, surviving immutable events
and cursor progress. Exact retained request/page replay is a no-op. A changed stale
page must be refetched from the stored cursor; it cannot rewrite prior evidence.
Missing profile metadata with retained history is an error, not a new binding.

`ReadClockInbox` and `LoadClockInbox` validate complete retained history and derive
the cursor, sticky gap, loss count and catalog sizes. Storage is bounded to 4,096
pages, 4,096 events and 16 MiB of encoded requests/pages. Duplicate event copies
are checked against their pages. Capacity failure leaves the entire append
uncommitted. Review and acknowledgement use separate durable operations;
ingestion alone never clears a gap or an interruption. Polling compacts eligible
reviewed history before ingesting another page.

## Review and acknowledgement

Captured and reviewed cursors are distinct. Review consumes only committed pages
and derives interruption and gap holds from that evidence. Ordinary epoch starts,
speed changes, hostiles-cleared, force-pause-cleared, operation outcomes,
authority changes, game alerts (`Event_Alert`: planning evidence and an
`alert_row` telemetry event, never a hold, since the native supervisor never
stops play for one, #244), injury observations (`Event_InjuryObserved`:
sub-threshold damage native coalesces so it never stops play, #318),
notifications (`Event_Notification`: only the letter and message classes
native never stops play for arrive as one, #325) and the benign stops
(`store/clock.BenignStop`: tick budget, requested pause, watch latched, and
a letter pause for an informational letter) do not create interruption
holds; pause failures, force-pause waits and other stop events remain
conservative holds.
Cleared conditions cannot erase an earlier unacknowledged event.

Acknowledgements require an exact review revision and reviewed cursor. An exact
request replay returns its historical result and never acknowledges later data.
Consumers must read current review state before making a new policy decision and
require the reviewed cursor to match the current captured cursor. Acknowledgement
records inspection only; current unsafe facts and missing native events still hold.

The bounded review log validates its canonical head against its checkpoint,
retained operations and inbox provenance on every load. Capacity refusal leaves
state unchanged. Compaction preserves exact acknowledgement results in an indexed
archive, queried only by request ID; later events cannot change those results.

## Finite window admission

`policy.EvaluateClockWindow` requires fresh same-authority status and emergency
facts, a paused inactive clock, known remaining work and a complete catalog with
no outstanding owned epoch or unknown start. Reviewed and captured cursors must
match the native newest cursor, with no interruption or gap holds. Native tick
boundaries and durable events must be known; a never-started clock may report
durability as false. The admitted budget is finite and cannot overflow its tick
deadline. Admission carries its snapshot and review revision for dispatch binding.

A live, undowned hostile or hunting predator refuses the window (`unsafe_colony`)
unless the facts say the ActiveCombat goal holds an admitted plan with open work;
unknown plan evidence refuses as `unknown_facts`. A hostile building alone
refuses only while the defense planner has not reported `no_worker:squad`
for this stop (`ClockWindowFacts.SquadUnanswered`); reported, the building is
watched and the colony window admits (#326). A hostile or hunting *animal*,
or a hostile building (#246, #340), known to be at least
`policy.DistantThreatCells` (50) from every colonist is not an emergency at all: no planner answers it, and the native supervisor's own
radius (`hostile_within`, 20 cells in serve; 40 for a predator hunt) stops a
running window before it can reach anyone, at which point it is an ordinary
close threat. A humanlike or mechanoid threat, or one whose race or distance
is unknown, holds at any distance. A method refused only for insufficient
stock lends the window a bounded `stockWaitTicks` of native work: the census
does not see a stack in a hauler's hands, so without ticks the haul that would
clear the refusal never lands and the window is refused as `no_work` for good.
The same bounded window is lent when a planner's native preview is refused
or when a planner fails outright on a native refusal (`bridge.ErrRefused`):
the refusal describes the world at this tick and game time may change it,
while a transport or control failure lends nothing (#219).
Likewise a cooler method that completed on the tick the supervisor latched the
window still reads `powerOn=false` until the power net ticks once, so the
refrigeration planner lends its cooling allowance to `cooler_power_needed` as
well as to `cooling`; a cooler still unpowered when the allowance runs out is a
genuine hold for the power family. The allowance keys on the method's dispatch
scope and world, not the native generation, which moves with every stopped
window. A goal epoch with no cooler method of its own (a freezer that settled
in an earlier epoch and re-latched when the season warmed, or a cooler the
player set) lends the same allowance from the tick the refrigeration latch
engaged (`RoutineLatches.RefrigerationSince`), so a second cooler is still
proposed once it elapses; that lent time never covers `cooler_power_needed`,
since no cooler is settling. Either allowance is lent one game hour (2500
ticks) per window: a cooler exchanges heat every 250 ticks and the review
re-reads the stock at each stop, so the two-day allowance elapses in a few
dozen windows rather than a thousand. A skilled furniture or cooler build needs one available pawn with
Construction enabled at the native skill minimum; it no longer waits for the
whole colony's checkboxes to match a Construction-only allocation the work
planner never applies. With such a plan the decision
is a combat watch: it names, sorted, every live hostile the window acknowledges
and uses the combat budget (never above the colony budget). Once every hostile
is dead or downed the decision is back in colony mode, acknowledging nothing.
Colonist status, unknown threat status and every other hold apply in both modes.

Window starts retain their exact profile, snapshot, tick, review revision, captured
cursor and budget with the immutable clock intent. Durable dispatch checks the
current review in the same transaction: its revision and captured/reviewed cursors
must still match, with no holds. Later review does not invalidate historical
admission needed for receipt recovery.

`CommandClockWindow` carries the policy facts through the serialized coordinator.
It checks age and authority after waiting and before dispatch and native execution;
the fresh status must still describe the admitted paused tick and cursor. That
status is the admitting step's own second bundle (`ClockWindowRequest.Status`,
stamped `StatusAt`) while it is within `MaxAge`: the bundle was the step's last
native round trip and every game-host call is serial, so a `clock_read_status`
issued after it would report the same paused tick (#200); without one, or past
`MaxAge`, the coordinator reads the status natively. In either case
and the start policy must watch in the admitted mode with exactly the admitted
hostiles acknowledged: colony mode acknowledges no pawn, combat mode acknowledges
only the decision's hostiles, and medical suppression lists are never accepted.
The ordinary command entry point cannot dispatch a window-bearing intent without
these checks. A dispatched attempt with uncertain effects is recovered by its
original key, never by issuing a replacement start.

The internal `ClockScheduler.Step` uses the player's cancellation and serialization
scope for one decision. Its explicit start configuration is colony watch mode
without acknowledgement or medical suppression lists; the step derives a combat
start (mode, acknowledged hostiles, combat budget) from the window decision when
the current routine review binds an active ActiveCombat goal whose plan has open
work, so a raid runs in short windows re-planned between them. It checks the shared profile,
current plan work and complete attempt/epoch catalogs before collecting fresh native
facts. Unchanged decision inputs retain the same request ID across repeated calls;
an undispatched stale preparation cannot prevent a fresh decision. Disabled sessions
perform owned cleanup and cannot start. A valid running window is left unchanged.
The routine reviewer runs first and its failure aborts the
step; every other composed planner then runs as one concurrent wave whose failures
are isolated: a planner whose native read is refused or whose preview is stale
commits nothing and is reported in `ClockSchedulerResult.PlannerFailures` (the
clock worker logs each changed set once), but its peers finish and the window is
still evaluated on what they committed, so one broken family cannot keep the
clock from ever starting. Only the step's own context ending fails the wave.
The wave admits planners in goal-priority order (naming and active combat, then
critical medicine and recovery, then foothold needs, then maintenance, then
comfort and expansion), at most `bridge.MaxConcurrentCalls` at a time with a
slot taken before the next planner starts, so a tight step budget is spent on
the highest priorities first. Native reads still execute one at a time on the
game's main thread; the wave only overlaps their round trips, so a wider
session pool would not help.

The bridge hands those slots out by admission class (#631): every reviewed
method is `control` (clock, authority, operations, receipts, lifecycle,
placement previews) or `observation` (`observations_*`, presentation state and
leases). One slot is reserved for control, observation may hold the rest,
and a waiting control call is admitted before any waiting read when a slot
frees, so a renew or stop never queues behind a burst of bundle reads. The
class rides beside `request` and `trace` on the wire and
the companion's `ProtoBoundary.OnMainThread` runs queued control hops before
observation hops within a frame (`MainThreadAdmission`); the few remaining
untyped tools call the host's main thread directly and stay outside that
ordering. `bridge.WithAdmissionClass` overrides a call's class for a caller
whose use differs from the method's default.

The admission cycle waits on the critical class only (#623). Each catalog
entry is `critical` (the preempt and critical priority classes and fire
safety: authority, emergency evidence and the verdicts the window decision
reads) or `optional` (the development reviews). The step joins the routine
review and the critical planners under the wall budget
(`StepBudget.Wall`, 20 s by default): past it, the critical planners still
evaluating are named on `ClockSchedulerResult.HeldBy` and the `clock_step`
row's `held_by`, and the step admits nothing (`admission_refused` with
`critical_wave_budget`). The optional planners run on the same snapshot,
started after the critical ones, and are joined for one critical-wave
duration more (floored by `StepBudget.OptionalGrace`, 1 s by default, and
never past the wall budget); those still evaluating at that cutoff are
cancelled, listed under `missed_cutoff` on the row and
`ClockSchedulerResult.MissedCutoff`, and their results discarded. Planners
write into a private result merged only for those that made the cutoff, so
a late return writes nothing the step reads.

While the colony stage holds development for an unmet shelter
(`ColonyStageRecord.HoldsDevelopment`, #630), the step also drops the
comfort-class planners and promotes the startup planners -- the shelter's
own, `plannerEntry.startup` -- into the critical cycle for that step (#658).
Siting a starter shell walks the bunk rungs and previews a ring, seconds of
native round trips, so at the optional grace its work was discarded on every
step and the shelter goal the whole stage waits for never took a method.

Migrated planners (#622) return proposals, and the coordinator arbitrates
them after the cutoff by `(priority, urgency, id)` against the step's claim
index. Before a proposal commits it is revalidated against the step's read
validity (`domain.ReadValidity`, #624: the world, load and native
generation, the plan revision, the fact store's version of every section
the proposal's families cover, and the tick it was planned from within the
inventory bound): a proposal that reached its arbiter after the cutoff is
carried to the next step's coordinator, where an expired one is reported
`expired` with the stale dependency named (`ProposalOutcome.Stale`) and its
commit never runs, so nothing reaches the journal.

The validity replaces the process-global live drift (#345) as the bound a
step's facts are judged by. `livePace` measures the running window's pace
from the previous step's status tick; once the step's scope, tick and pace
are fixed (`readValidity`) the validity rides the step context and is
published for the Worker (`ClockScheduler.Validity`, carried on each
dispatch by `WorkerConfig.Validity`). Each fact names its age class
(`domain.AgeClass`): a stable fact (definitions) is never tick-bound and
serves until the view is invalidated; an inventory fact (construction,
bills, stock, the cached emergency census a dispatch pairs with its
inspection) is fresh by section version where the invalidation stream
moved one, else within `PlanningTickTolerance` plus the pace over the
step's `MaxAge`; a dispatch precondition (the live re-read a dispatch
boundary pairs with its inspection) is bound to the pace over one
dispatch's reads (`domain.DispatchReadWall`, 250 ms), and the native
operation revalidates it at application time, so a fresh census never
certifies an earlier read. Section versions live on the fact store
(`facts.Store.Versions`): each moves on an invalidation that names the
section, whole or narrowed, on a whole-view invalidation (an event gap, an
unattributed mutation) and on a scope change, never on a refresh at
cadence. `domain.SetLiveDrift` remains a compatibility shim the scheduler
keeps in step with the inventory bound for the freshness checks not yet
carrying a validity (`Tick.FreshFor`, the fact caches' tolerances); a
migrated check reads its bound from the context (`domain.FreshIn`,
`domain.CoversIn`). The `worker_dispatch` flight row marks a run held on
stale facts (`stale`), summarised as `stale_holds` and reported by
`speedmatrix/plain` as `stale_facts_holds` per row. The row also carries the step's budgets
(`budget.wall_ms`, `budget.native_ticks`, `budget.reads` when set, with
`reads_over_budget` on an overrun) beside what it used (`reads`,
`elapsed_ms`, `critical_wave_ms`, `native_work_ticks`).
The step itself has no polling loop; autonomous play attaches ClockWorker.

Every state read a step issues (the bundle, the routine census and each
composed planner's own reads) is served from the newest snapshot frame
(#858, `bridge/frames.go`) past the client's last write; only parameterized
reads no frame carries and the tick read cross GABP. The frame stream is the
only cross-step memo of wire replies. The sections of one frame share its
tick, so readers apply no tick check between rows: a routine read's boundary
(`observation.sameColonyContext`, #306) compares load, map and native
generation only, whatever the distance from its anchor. The decoded store
(`facts.Store`) drops sections on a write, a scope change, `AuthorityChanged`,
`EpochStarted`, a stop and the families an `OperationOutcome` or an
`ObservationInvalidated` names (`facts.Store.Apply`); a narrowed rectangle
that misses the held planning window leaves it held (#656).

Beside the cache the scheduler keeps one `facts.Store` (`go/internal/facts`,
#354): decoded state per section (`colony`, `planning_cells`, `population`,
`research`, `pawns`, `emergency`, `rooms`, `zones`, `buildings`), each held
with the tick its reply described (`AsOf`), whether it covers the whole
section and the method that produced it. Every routine review files the
sections it decoded (`observation.RoutineReading.Sections`) and the
admission's emergency census is filed from the step's bundle; the store
follows the cache's scope rule (a new (load, generation) empties it) and
the same typed-event discards, by family (`facts.Section.Family`). Every
section but `rooms` carries the routine frame's tick, and `/api/routines`
lists the held sections (`sections`).

`planning_cells` is the first section with its own read (#356). A current
native's colony facts carry no `planning.cells`; the window (the site
cells at the colony centre +/- 22, `bridge.PlanningWindowRect`) is cut
by `bridge.ReadPlanningWindow` from the whole-map cell grid every snapshot
frame carries (#1345, `bridge/cellgrid`): a keyframe, or a delta Go applies
to the held keyframe (cumulative, never chained; a delta against a keyframe
Go skipped asks for a new one). Grid glow is artificial light; the window
raises an unroofed cell's glow to the frame's `sky_glow`, so `SiteCell.Glow`
is total light; a client without a stream has no window. Fogged cells
are not held. `observations_get_cells` answers the same grid over a
requested rectangle (#1346), with foundation bytes for the map survey and
thing rows on request.
The step attaches a
refresher to its context (`observation.WithPlanningWindow`); a planning
colony read whose reply lists no cells asks it, and every ask cuts the
window from the newest frame's grid; a failed read serves the held window
of the same region. The section files with the frame's own tick. The
row carries no `reachable` (`placement_preview` refuses an unreachable
site) and never lists a fogged cell (`Completeness.filtered` counts them).

When the planning-window view (#650) cannot serve a held window, it is
reread whole. The #795 `mirror_poll` cell grid and the #357
`changed_since_tick` delta on `get_cells` were removed (#858);
`CellTracking.cs` only feeds the view's change ledger. The CellGrid format
is the frame's whole-map grid (#1345) and the snapshot recorder's
planning_cells line encoding.

The routine review reads every continuous section (`research`,
`population`, `rooms`, `pawns`) from the frame on every review; none is
served from the store in its place (#884).

The entity list reads (`zones`, `buildings`, `bills`) are read whole on
every review step (`refreshEntitySections`) and filed in the store as
sections keyed by id (`buildingruntime.EntitySection`). The #358/#795
changed-since machinery was removed in #858: the native `EntityTracking.cs`
digests and tombstones, `mirror_poll`, `SectionDelta` and the
`changed_since_tick` request fields. Mirror reads now come from the
snapshot frame stream.

The colony mirror is the keyed tables of the facts store
(`go/internal/facts`, #795, #1349): per section, the keyed rows of the
last whole frame (#858) and the tick they describe (`facts.PutTable`),
held beside the decoded sections and their invalidation versions. The
store's scope is the load, map and native generation, so a reload, a map
change or an authority generation flip empties both. Tables
are immutable once published, and each goes to the snapshot recorder.
The pawn, building and thing tables are persistent (`bridge.Table`, a
hash trie, #1578): the snapshot stream's hold applies each row delta to
the held version in O(changed rows), consumers read the versions directly
(a version never changes, so one may be kept across frames), and the pawn
and thing sections publish as versions (`facts.PutKeyed`), which the
recorder diffs against the last without walking shared rows. The review
publishes the frame's colony facts (one section per
`bridge.ColonySections` name), the frame's pawn table (section `pawns`,
every spawned pawn keyed by pawn id, #1343), its things table (section
`things`, every thing a food stock references, keyed by thing id) and the bench census (`benches`, each bench's
bills and recipes, keyed by bench thing id), and retains its census for
the step's planners (`routineCensus`): a planner of the same load, map
and native generation plans from it at any tick at or after the review,
paused or running, until committed clock evidence invalidates it; the
work planner serves the review's bench table the same way. There is no
paused review bracket: CAS evidence on every write refuses a decision the
world moved past.

## Independent clock workers

`ClockWorker` runs event polling, renewal and scheduling separately. Scheduling waits
for a successful initial poll. Polling never takes the Player gate: observed
interruptions invalidate permission before persistence, and read or persistence
failures also disable writes. Pages commit before review; no event is automatically
acknowledged. Cleanup uses the original owned epoch independently of live authority.

Renewal requires the exact retained epoch and complete current authority snapshot,
unchanged deadline and speed, and caught-up reviewed event evidence. Uncertain
renewals are read by their original attempt. All clock requests use a namespace-bound
monotonic sequence allocated atomically with the intent. A retired epoch's
historical uncertainty cannot authorize or block renewal in a replacement scope.
Renewal waits without extending the lease when event capture or review is behind;
the poller invalidates permission for interruptions. A verified tick-budget stop
defers to event review and scheduler cleanup, preserving permission for the next
eligible window. If the window finishes during renewal preflight, the renewal stays
prepared and undispatched. A fresh, matching native observation of normal budget
completion preserves authority for that same handoff; uncertainty or interruption
does not.

The session attaches one clock worker before its loops start. Close cancels and
joins the loops and their cancellation handler before releasing native handles,
the journal or profile owner. Concurrent Stop calls serialize, successful cleanup
is cached, and failed cleanup remains retryable. Poll and renew intervals and call
budgets are bounded below a quarter of the native lease duration so a late renew
can never let the epoch lapse. The step budget is independent of the lease and
bounded only by the Player's call timeout: a step holds the Player gate, never
`renewGate`, so a slow planner census cannot delay renewal (`serve` budgets 7s for
poll/renew and 30s for the step; the scheduler's `MaxAge` bounds the admission
reads, which follow the planners, and the planner facts are bound by tick
through `ClockWindowFacts.FactsTick`). Unchanged scheduling decisions back
off, except while a combat window is admitted or running, which keeps the
short poll. A captured page wakes the step loop through the worker's own
`WakeSignal` and resets that backoff, carrying the outcomes, invalidated
families and authority flag as the step's `StepReason`; the poll notifies the
shared signal too, which wakes the routine `Worker` to reconcile the actions
named by any `OperationOutcome` or `WatchLatched` evidence ahead of its
rotation and without their retry backoff (at most 64 focused actions).
Autonomous play attaches the worker; `--observe` does not. A wake's terminal
outcome for an attempt the plan still shows dispatched defers the admission
(`ClockSchedulerResult.Deferred`, at most three steps per outcome and three
deferred steps in a row over every hold and action, and not
while another watched attempt is in flight, since a window admitted then has
that one to latch; likewise while a watched successor is queued but not yet
dispatched): the
step loop leaves the player gate to the worker and steps again when a worker
step advances any action (`WorkerConfig.Advanced` nudges it) or a
`StepInterval` later, without backoff. No admission waits for the stop
between windows (#244): every routine kind the worker dispatches validates
its preconditions natively at apply time (#242,
action-contracts.md "Apply-time preconditions") and dispatches under the
running window, so the step that settles a stop reviews and admits in the
same pass without holding for the worker; the pause-drain hold of #129/#211
(`clockPauseDrainMax`, `clockPauseDrainTotal`, `WakeSignal.PauseDrained`)
is retired with the pause-bound set. A dispatch the executor holds on
`stale_facts` (its inspection ran under a generation the current one had
outrun, or its preview tick fell behind the prepared or minimum tick,
`workerHeldStale`, #288, #306) is retried at once, off the
worker's backoff and once per hold, so the order re-inspects before the
game's own work scanner takes its target; the clock is not held for the
retry. A routine window arms no watches:
a completed order is not a reason to stop the clock, and the
`OperationOutcome` row the event poll carries wakes the worker under the
running window. The step counts the dispatched construction and haul
attempts a running window does not watch (`ClockSchedulerResult.Unwatched`,
the `clock_step` row's `unwatched`), evidence only; the pause-and-rearm
cycle of #207 is gone with the routine watches. A coupled order
(`domain.ActionDependency.Coupled`, controller-contracts.md "Coupled
orders") no longer stops the window either (#584): when its prerequisite
has completed at the epoch's current tick and the order is still
undispatched (`PlanSpec.CoupledPending`), the step reports `Coupled` with
`CoupledOrders`, plans live for it whatever the wave's cadence or the pace
skip would say, and leaves the epoch running; the order's own CAS evidence
refuses a read the world has left behind.

A step that finds its own window running plans under it (#243): the
planners read the bundle's snapshot (one main-thread hop, so its sections
describe one tick) and commit plans the worker dispatches live; nothing is
admitted, since the window already runs and the stop that ends it reviews
and admits as before. The step reports the `live` cause; a `timer` step
plans live only when a planner is due on the queue and `LiveWaveEvery`
(`DefaultLiveWaveEvery`, 30 s) has passed since the last timer-driven wave,
a `wake` or `full` step at once, so a running window costs one planner
wave per `LiveWaveEvery` at most, and none while nothing is due. The wave is also bounded by pace (#598): when
the ticks the window's measured pace covers in the previous `live` step's
wall time exceed `LivePlanningTicks` (`DefaultLivePlanningTicks`, 6000, a
tenth of a game day), the step reconciles, admits nothing, leaves the
worker's dispatch and reports `live_planning=skipped_pace` on its
`scheduler_step` and `clock_step` rows; the wave waits for the stop, one
window away at most. The bound keys on the ratio, not the speed: capped
Ultrafast (900 ticks/s over a 5 s step) plans live, an uncapped game at
1000+ ticks/s under a 10 s wave does not.

`StepReason.Cause` is `timer`, `wake`, `settled`, `full` or `live`. The planners the
scheduler queues are the `plannerCatalog` entries `plannerSelection` picks
over the scheduler's due queue (`plannerQueue`, #625): every configured
entry for `full` and `settled`; for a `timer`, the entries whose next review
tick (`plannerEntry.reviewEvery`: 2 500 ticks for the critical and foothold
classes, 7 500 for maintenance, 15 000 for comfort, recorded when the entry
last ran) has passed, so a timer with nothing due runs the admission tail
alone (promoted to `full` once `FullStepEvery`, 2 min, has passed without a
full wave: the coarse reconciliation for missed invalidations); for a
`wake`, the entries whose dispatched `kinds` include a latched outcome's kind
(as the scheduler remembered arming it; an unremembered action selects all)
or whose declared `sections` include one the `ObservationInvalidated` row
dirtied (`facts.Invalidation.Sections`: a whole family names every section
it holds, entity ids the entity sections, a rect the planning cells), and
all of them when authority changed. Each entry declares the
snapshot frame sections it consumes. An entry that reported `existing_work` waits on the open
attempts of its kinds it found (`plannerQueue.waits`): it is not selected
again until one of them reaches its outcome row, its own next review tick
passes, or a full step runs, and a step that skips it lists it under
`waiting`. The routine reviewer runs before any planner wave; its retained
census is retired by any typed-event invalidation (`routineCensusStore`
generation) so a same-tick reuse never serves facts an event made stale.

Every routine result embeds one `Verdict` (`buildingruntime/outcome.go`): an
`Outcome` (`admitted`, `nothing_to_do`, `disabled`, `no_current_review`, `expired`,
`combat_orders`, `hold_fallback`, `waiting`, `refused`) and, when refused or
waiting, a `Refusal` with a closed `Kind` (refusals: `collapse_pending`, `no_worker`,
`awaiting_plan`, `field_unavailable`, `no_space`, `shared_admission_refused`, `rock_not_dug`; waits:
`method_already_used`, `already_working_on_it`, `shelter_bunks_open`, `breach_held`, `waiting_for_native_comfort_use`,
`existing_facility_needs_bill_or_upkeep`, `hospital_bed_convert_pending`,
`sleeping_use_needed`, `butcher_separation_pending`, `dialog_not_interactive`,
`waiting_on_claim`), a subject and optional detail. A refusal or wait without a kind panics at
construction and is rejected when filed; there is no catch-all kind, and routines branch on the
outcome or kind, never on text. `Verdict.String` is the machine token
(`kind[:subject[:detail]]`, no spaces) for the service log's `reason=` (the dashboard timeline
parses it) and snapshot names; `Verdict.Text` is the one plain-English sentence per outcome and
kind, filed on `GoalProgress.Planner` for the status strip and the journal. A planner's catalog
entry names the one goal it serves (`plannerEntry.goal`), and the wave files its verdict there:
a refusal files as a block (`planner:` reason), a wait files as a wait (`waiting:` reason,
`GoalProgress.PlannerWaiting`; shown without a warning), a disabled planner files the opt-out
hold, a planner that found its earlier work still standing (`already_working_on_it`) waits on it, a planner whose fight is running orders (`combat_orders`: "combat orders are running") or fell back to squad defense (`hold_fallback`: "the hold line fell back to squad defense") files that sentence as a wait, since the fight is under way and nothing failed, and an admitted or nothing-to-do verdict clears the goal's refusal or wait.
A verdict that says nothing about the goal (no review, a stale proposal) files nothing.
Siblings on one goal keep the strongest note: refusal, then wait, then clear, then opt-out.

No window watches attempts: the `watched_attempts` policy field is retired
(#856), since a building intent settles on its Apply receipt and the census
says when it is built. An applied building on a plan not yet retired counts
as clock work, so the window runs until the construction census retires the
plan.

A fresh worker over reopened state remains disabled while recovering original
attempts and pausing retained ownership; it does not acquire authority or issue a
new Start or Renew. A native reply that ignores cancellation keeps shutdown
retryable and the profile locked until the call returns, its receipt is persisted
and owned cleanup joins. Renewal does not wait for ordinary player work.

## Clock history retirement

Clock request IDs bind the journal namespace and a positive monotonic sequence.
The scheduler stores its logical decision key separately and reuses retained exact
intents. Retirement never resets allocation: removed requests return ErrRetired,
and a missing retained row is corruption. The current Go schema requires disposable state.

RetireClockHistory atomically removes eligible attempts and terminal epochs while
preserving unresolved writes, nonterminal ownership, retained commands' Start
provenance, the latest scheduling window and a bounded recent tail. Its expected
state checks namespace and allocation/retirement watermarks; the transaction
recomputes eligibility from current rows. A bounded retained index detects missing
pinned records without tombstones. Event polling invokes retirement after each
128 newly allocated requests, retaining the latest 128 attempts in addition to
pinned evidence. A concurrent allocation defers retirement until the next poll;
other maintenance failures disable control and invoke owned cleanup. Maintenance
does not take the player gate, acknowledge events or restore authority.
Event/review maintenance checkpoints the validated review head and retires a
contiguous reviewed prefix, retaining eight recent pages and every unreviewed or
unacknowledged interruption/gap. Captured/reviewed/acknowledged cursors, revision,
profile binding and cumulative loss evidence survive compaction and restart.
Deletion, checkpoint advancement and acknowledgement archival commit atomically.
Maintenance runs at 128 active reviews/pages, 256 events or half the byte budget.
Unresolved holds can still fill the bounded inbox and fail closed; they are never
discarded to make space. The acknowledgement archive grows on disk with explicit
player acknowledgements, while ordinary windows keep bounded active history.

With clock supervision enabled, GET /api/player/clock exposes durable review
revision, cursors and interruption/gap holds. POST /api/player/clock/acknowledge
requires the player session token, an explicit request ID, expectedRevision and
throughCursor. Counters use canonical decimal strings. A changed revision returns
a conflict; exact acknowledgement replay uses the retained request ID. This only
acknowledges inspected evidence and never acquires authority or resumes time.
The dashboard preserves its last review during refresh failures and offers an
explicit retry of the same acknowledgement after an uncertain response.

### Zone policy section

The `zones` section supplies farm capacity, planted/growing counts, harvest
lower bounds, food-stockpile suitability and the guarded zone-map token.
`observations_read_colony_facts` omits `farms`, `food_storage` and
`planning.zone_map_snapshot`; these fields remain in the schema only for
retained captures. Routine policy and zone creation read the zone section.

`observations_list_zones` reads the census whole, in pages of 16. Every
reply carries `map_snapshot`, each growing row carries `farm`, and each
zone carries `food_storage`. The #795 changed-since ask
(`changed_since_tick`, `as_of_tick`, `unchanged`, `removed_ids`) and the
native zone tracker were removed along with `mirror_poll` (#858). A stale
colony invalidation or a scope change rereads the census whole.