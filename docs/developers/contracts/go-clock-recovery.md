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
gate and cannot call Control or request a lease. A new acquire drains old owned
effects before requesting authority. Close joins writers, prioritizes clock pause
before draft cleanup, and retains the profile, transport and store until cleanup
succeeds. Lease-free cleanup remains usable after the command coordinator stops.

A fresh positive replacement world can durably retire the scope of an unknown
start. The attempt retains its original outcome uncertainty; retirement never
means refusal or no effect. Its immutable proof prevents late replies from
creating a new obligation. Same-world authority changes cannot retire that scope.

## Profile-wide event cursors

The native clock journal belongs to the game profile. A page's observation context
matches the requested current world; each event retains its original world and
epoch. A load or map change must not reset the profile's cursor or rewrite old
event contexts.

Event reads initialize or recover that journal before any owned start. Status
reads initialize the tick watchers so the scheduler can inspect readiness before
requesting a window. Neither operation advances time or acquires authority;
journal recovery and hook failures remain unavailable evidence.

`ValidateClockEventsPage` checks the typed request and page together. The native
reader scans at most the requested limit of cursor positions, including missing
event files. Therefore `next_cursor - after_cursor` equals the number of returned
events plus `lost_count`; `gap` is true exactly when loss is reported. Missing tail
files and entirely missing windows can advance `next_cursor` without a final event
at that cursor. Returned events are strictly ordered within the scanned window.

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
speed changes, requested pauses and tick-budget stops do not create interruption
holds; notification, injury, failure and other stop events remain conservative holds.
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
unknown plan evidence refuses as `unknown_facts`. With such a plan the decision
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
the fresh native status must still describe the admitted paused tick and cursor,
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
The routine reviewer runs first and its failure (or a mental-risk hold) aborts the
step; every other composed planner then runs as one concurrent wave whose failures
are isolated: a planner whose native read is refused or whose preview is stale
commits nothing and is reported in `ClockSchedulerResult.PlannerFailures` (the
clock worker logs each changed set once), but its peers finish and the window is
still evaluated on what they committed, so one broken family cannot keep the
clock from ever starting. Only the step's own context ending fails the wave.
The step itself has no polling loop; autonomous play attaches ClockWorker.

Every native observation a step issues (identity, the routine census and each
composed planner's own reads) goes through one `bridge.StepReadCache` attached to
the step's context and dropped at step exit. The first read of a
`(method, request)` pair crosses the bridge; identical reads later in the step,
or concurrent with the first, are served from it. Only `lifecycle_read_identity`
and `observations_*` replies carrying an `ObservationContext` are memoized, keyed
to that reply's (load token, tick, native generation): a reply from another
scope, or any write through the step's context, discards every row. Clock,
authority, presentation, receipt and preview reads are never cached, nor are
refusals or unavailability. A hit is decoded into a fresh reply, so the typed
adapters and the same-bracket identity guards validate it as they would a
native reply. `RIMGOVERNOR_CLOCK_DEBUG=1` logs the step's hit/miss/coalesced/
invalidation counts and the flight recorder reports hits per method (the
`cached` column of `rimgovernor phases`). Nothing is cached across steps.

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
poll/renew and 30s for the step, with the scheduler's `MaxAge` covering the whole
step). Unchanged scheduling decisions back off, except while a combat window is
admitted or running, which keeps the short poll. Autonomous play attaches the
worker; `--observe` does not.

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
