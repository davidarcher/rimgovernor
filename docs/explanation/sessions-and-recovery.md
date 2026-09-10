# Sessions, interruptions and recovery

[Documentation](../README.md) · [System overview](overview.md)

RimBot must know which world a piece of work belongs to. A colony identity and map scope
durable intent, while a load token identifies the current loaded instance. Reloading the
same colony can preserve its goals without granting old in-flight operations permission
to act in the new load.

## There is more than one kind of change

A routine observation updates what the controller knows. Player direction changes what
it is allowed to pursue. A load or map change changes the native context in which object
identities and prepared work were valid. These revisions are kept distinct so that
background review does not impersonate a player instruction.

Before writing, the runtime rechecks context and current direction. If the player has
stopped automation or a native interruption has arrived, the old work must yield even if
it was valid when prepared.

## A checkpoint pairs the world with its intent

A RimWorld save contains the native colony. The controller database contains goals,
receipts, policies, conversation and other persistent state. Restoring only one side
could leave the controller believing an order exists in a world that never received it,
or forgetting an order already present in the save.

An owned checkpoint therefore pauses and verifies the game, saves through the native
path, backs up SQLite, and records hashes in a manifest. Resume validates the pair and
starts in Manual with a new load token. It does not enable automation as a side effect
of recovering the session.

The checkpoint is also different from a recording of every simulation step. It is a
restart boundary, not a guarantee of identical future pawn behavior or a complete trace
of how the colony reached that state.

## History can leave the active plan without disappearing

Completed actions and eligible method associations move into immutable archive tables.
This keeps the live plan smaller while retaining identities and exact receipts for
inspection and duplicate prevention. The full database still grows; a bounded live
working set is not a bounded historical ledger.

After restart, the archive continues to reject old identities being introduced as new
work. Pending and uncertain actions need their own recovery checks rather than being
treated as finished merely because a checkpoint was retained.

## Cleanup belongs to an owned process

Disposable workers own their controller, profile, database and GABS runtime. Cleanup
must stop those owned processes, not every process named RimWorld. Windows workers share
installed DLLs, so installation changes require every game to be stopped. Docker workers
stage private binary snapshots and retain their evidence in the output mount after
container removal.

For exact bounds, read [session contracts](../reference/session-contracts.md) and
[archive contracts](../reference/persistence-contracts.md). For an actual operation, use
[save and resume](../how-to/save-and-resume.md), [mixed checkpoint
acceptance](../how-to/checkpoint-acceptance.md), or [legacy
migration](../how-to/legacy-migration.md).
