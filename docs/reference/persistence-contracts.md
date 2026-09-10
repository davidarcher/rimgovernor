# Persistence and archive contracts

[Documentation](../README.md)

Archival reduces the live working set while preserving exact identities and evidence. It
does not bound the entire database.

## Actions and methods

Completed actions removed from the active specification move to a compressed,
colony-scoped SQLite archive. Their exact specification, progress/receipts and cost
metadata are committed atomically with the compact live snapshot before in-memory
removal. Current combat references remain live until released. The archive participates
in paired database backups and rejects reused identities after restart; missing archive
data blocks admission. `inspect_plan` retrieves archived records by exact ID. The last
twelve plan revisions remain in the live history; immutable archive storage grows with
completed work. Method-to-action references whose actions are all archived move into a
separate immutable SQLite table in the same snapshot transaction. Goal deduplication
checks both live method entries and that table. Each natural goal reopening advances a
method epoch, allowing new work without deleting archived associations. Pending methods
remain live, and a missing method archive blocks replay after restart.

## Player command capacity

When an append would exceed 72 live steps, eligible completed PLAYER commands
also retire. Active player goal references and combat references remain live;
unfinished work is never discarded to fit the 80-step limit. Player intent records
retain their exact request and archived action identity. Repeating an archived
intent observes its completed state; changed intent requires a new explicit ID.
An explicit new research, production-policy, pawn-setting or building-setting
command can restore an earlier value after the old command completed. The old
setting receipt archives before the new action executes. Pending settings and
non-idempotent operations retain duplicate-intent protection.

## Goal evidence and history indexes

Hunting target metadata follows its completed action into an immutable goal-evidence
table in the same snapshot transaction. Pending targets remain live; metadata from older
snapshots whose actions were already archived is migrated without altering the original
receipts. Per-action bounded recovery histories remain in their exact action archive.
These tables preserve evidence while the live working set compacts; their disk usage
still grows with completed work.

Recent event reads use colony/sequence indexes, including a partial index for
non-diagnostic history. Index migration preserves every event and its identity; it does
not bound ledger disk growth or discard method deduplication evidence.

## Event delivery

Chat request IDs deduplicate lost HTTP acknowledgments within a colony. The
dashboard retains the ID for an unchanged failed submission and sends its load
identity. History, player direction, runtime snapshot and acknowledgment commit
together in SQLite. Requests interrupted by reconnect/load remain visible with
an explicit interruption notice; they are not automatically replayed.
Native clock reads journal fetched events and source cursor together before
advancing the in-memory cursor. Delivery consumes that inbox atomically with the
runtime snapshot and history. In-memory archive compaction waits for the outermost
transaction commit. Failed delivery retains its inbox, enters Manual and invalidates
old writes. Colony-scoped source cursors and unconsumed inboxes survive load changes.

The native supervisor retains immutable, consecutively numbered XML events under
the private profile's `RimGovernorClockEvents` directory. Each row carries colony/map/load
identity. A flushed temporary row is published by rename; a complete staged row is
recovered after process restart. Partial rows, conflicting publication and sequence
gaps fail closed. A journal-write failure pauses the current game and disarms the
lease. Event reads page the retained files, including history beyond 128 entries.
The journal grows with events and must remain with its profile; it is not simulation
state and is not rolled back by loading a save. Older companions retain their
bounded source history and enter Manual if it overflows.
Hands remains the durable action outbox: uncertain game writes require observation,
never transport replay.

## Related reading

Read [sessions and recovery](../explanation/sessions-and-recovery.md) and [audit
retained evidence](../how-to/audit-retention.md).
