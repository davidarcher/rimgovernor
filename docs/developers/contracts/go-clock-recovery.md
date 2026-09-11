# Go clock recovery evidence

[Subsystem contracts](README.md) · [Canonical clock schema](../../../contracts/proto/clock.proto)

The Go bridge validates clock commands and recovered evidence against the canonical
native producer. Runtime composition, durable storage and gameplay acceptance are
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
