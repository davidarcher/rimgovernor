# Audit retained plan and event evidence

[Documentation](../README.md)

Measure retention and history behavior using copies of existing evidence; choose a
native restart check when persistence must be verified against the game.

Run commands from the repository root. Keep generated evidence outside commits and
preserve failed results.

For receipt retention, run `scripts/plan_retention_audit.py --evidence
<native-result.json> --output <fresh-directory> --steps 1000`. The source must contain
completed plan receipts. This synthetic workload uses their shape without executing
native orders, checks SQLite round trips, and records active/retired counts,
snapshot/database size and serialization time. It measures retention overhead, not
native recovery. The audit uses the production archive transaction, reloads the compact
plan every hundred actions and checks all live/archived receipts at the end. Use
`--steps 10000` to distinguish a bounded live working set from the growing immutable
ledger. Add `--lifecycle` to retain a continuously active goal's method evidence and
interleave meaningful/diagnostic events from two colonies. Samples include method bytes,
event bytes, recent-history timings and query plans. This is a synthetic growth
workload, not evidence that a native colony survived that many actions.

Run `scripts/history_query_audit.py --source <controller.sqlite> --output
<fresh-directory>` to measure an actual campaign database. It opens the source
read-only, backs it up, applies current schema/index migration to the copy, and verifies
the full event digest and exact recent history before/after migration. Both diagnostic
modes and an absent colony are checked. Add `--unindexed-baseline` to remove only the
copy's history indexes before the comparison. Source databases and their receipts remain
unchanged.

`scripts/goal_method_archive_audit.py --source <controller.sqlite> --output
<fresh-directory>` retires completed native action records only on a read-only source's
copy, archives eligible method associations, and verifies exact mappings, deduplication,
goal reopening and event preservation after SQLite backup. This does not run the game.
For a real paired save/stop/load check, add `--archive --methods` to
`scripts/session_checkpoint_acceptance.py`; it verifies the archived method alongside
its exact native hauling outcome, a new load token, Manual mode and zero replayed
actions. Installed DLL replacement/restoration still requires every game to be stopped.

## Related reading

[Choose tests](choose-tests.md) · [Test evidence explained](../explanation/testing.md) ·
[Backlog](../BACKLOG.md)
