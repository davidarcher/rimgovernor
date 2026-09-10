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

## Related reading

Read [sessions and recovery](../explanation/sessions-and-recovery.md) and [audit
retained evidence](../how-to/audit-retention.md).
