# Go clock recovery evidence

[Subsystem contracts](README.md) · [Canonical clock schema](../../../contracts/proto/clock.proto)

The Go bridge and store validate clock commands and recovered evidence against the
canonical native producer. Runtime composition and gameplay acceptance are
tracked separately in [G01.10a](../../BACKLOG.md). These validators do not enable
the clock or restore permission after restart.

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
uncommitted. Processing, acknowledgement and retirement of history remain separate
runtime work; ingestion alone never clears a gap or an interruption.

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

The bounded review log validates its canonical head against complete operation and
inbox provenance on every load. Capacity refusal leaves state unchanged. Event
polling, current-state policy and retention are separate runtime gates.
