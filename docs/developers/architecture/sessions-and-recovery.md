# Sessions and recovery

[Architecture](overview.md) · [Session contracts](../contracts/session-contracts.md)

Colony identity and map scope durable intent; a load token identifies the loaded
instance. Reloading can preserve goals while invalidating old in-flight operations.
Observation revisions, player direction and native context are separate: background
refreshes cannot authorize new work. Recheck context and direction before writes.

## Paired checkpoints

A checkpoint pauses and verifies the game, saves through the native path, backs up
SQLite and records hashes in a manifest. The save contains the colony; the database
contains goals, receipts, policies and conversation. Restore them together to keep
intent aligned with issued game orders.

Resume validates the pair and starts in Manual with a new load token. Pending and
uncertain actions retain their recovery requirements. A checkpoint is a restart
boundary; it does not record every simulation step or guarantee identical future
pawn behavior. Attached sessions have a separate unchanged-game reconnect contract.

See [save and resume](../../players/save-and-resume.md),
[checkpoint tests](../testing/checkpoint-acceptance.md) and
[session migration](../legacy-migration.md).

## Archives and cleanup

Completed actions and eligible method associations move to immutable archives.
Their identities and exact receipts remain available for inspection and duplicate
prevention after restart. The active plan stays smaller; the database still grows.
See [persistence contracts](../contracts/persistence-contracts.md).

Workers own their controller, private profile, database and GABS process. Cleanup
stops owned processes only. Windows workers share installed DLLs, so all games must
stop before replacing them. Docker workers stage private binary snapshots and
retain output after container removal.
