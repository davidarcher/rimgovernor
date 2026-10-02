# Persistence contracts

[Documentation](../../README.md)

Every fact has exactly one home, chosen by what must happen to it when a
save is reloaded. A second copy of a fact is a bug, not a cache.

| Home | Holds | On reload |
|---|---|---|
| Native save, `GovernorState` blobs | Go intent the world cannot show: goals (`goal/<id>`), family plans (`family/*`) and the soldier squad (`family/soldier_squad`), written by Go, opaque to native | Follows the save's timeline; Go rebuilds its in-memory views from the blobs |
| Native save, other components | Colony identity and native tick guards (wall removal, mining, home coverage, the guarded designations of `GuardState`, #1350) | Follows the save |
| SQLite, one database per launch (`--state`) | The session journal: actions, transitions, admissions, clock inbox and cursors (native buffers clock events in memory only), request-ID replay | Not restored; read across launches only by postmortem |
| Go memory, or SQLite tables replaced wholesale on every world change | Everything derivable: plans, receipts, snapshots, the definition catalog (read once per load token, #1340); the material budget (free stock less construction and live bill-job holds, `policy.MaterialBudget`, #1354); the `goals`, `goal_methods` and family tables are such views of the save blobs (`RebuildGoals`, `RebuildFamilies`) | Rebuilt from the save and the live world |
| `flight.jsonl` | All controller telemetry; `--debug` goes to stderr only; snapshot dumps and the acceptance harness's replay transcript are opt-in recordings | Diagnostics only |

Native saves no Go bookkeeping (receipts, lineage, purpose tags), and Go
keeps no durable copy of what the save holds. Decided 2026-10-02 (#1355).
The rest of this page details the session journal.

## What must survive

- **Receipts for uncertain writes.** Every native write is journaled before
  dispatch and settled by observation, never by transport replay. A reply
  lost after dispatch leaves the action uncertain; it is reconciled from the
  next observation, and its plan stays live until that happens.
- **Request-ID replay.** Player submissions (goals, policies, decisions,
  building and research intents, control intents, clock acknowledgements,
  chat) are keyed by the caller's request ID within a world. Repeating an ID
  returns the recorded outcome; a changed body under the same ID is a
  conflict.
- **The routine review cursor and policy inputs.** Latches, recovery
  histories and the current goal bindings let the next review continue where
  the last one stopped; population and resource policies and
  per-pawn decisions are what the reviewer reads.
- **Clock inbox and source cursors.** Native clock reads journal fetched
  events with their source cursor before advancing; delivery consumes the
  inbox atomically with the review. Colony-scoped cursors survive load
  changes; history beyond a bounded tail is retired once reviewed.

- **Colony extent history.** Established extent regions (with their
  provenance and the tick and native generation that first observed them)
  and explicitly selected expansion areas (with the reason recorded on add
  and on remove) are an append-only session cache per world (colony, map)
  (`store.EstablishColonyExtent`, `AddExpansionArea`,
  `RemoveExpansionArea`). A read sees the world's entries at or before its
  tick. A world change empties it and the new session re-establishes its
  extent from the live world (#1009, #976 U4b); another colony or map sees
  nothing.
  Historical Home exclusions are not recorded here: they are current
  restorable state, not player vetoes. Ownership and the consumer contract:
  [colony extent contract](colony-extent.md).

- **Layout tidies.** The re-sites `TidyLayout` moved or is moving
  (`store.RecordLayoutTidy`, `LayoutTidies`, #611) are a session cache per
  world (colony, map), rebuilt from the save's `family/tidies` blob on a
  world change (#1005): each status change (moving, done, abandoned) is a
  row, and the latest row per item at or before the tick is its state.

## What is re-derived

Routine goals are re-derived from observation every review. A world change
(new load token, or a tick rewind in the same load) invalidates the previous
bindings, cancels their pending work and starts a fresh review under the new
world's root plan; only work already dispatched keeps its recovery
requirement. A pause in the same world suspends the bindings and leaves
their work open; the next enabled review reactivates the same goals. Durable goals come back from the save blobs, so nothing here needs a
restore step.

## Bounded working set

Settled autopilot plans and superseded invalidated goals are marked retired
rather than deleted: their IDs, methods and receipts stay readable for
duplicate prevention (a method that completed in the current goal epoch is
not proposed again; a deficit measured after the goal's recovery, once no
plan's effects are open, starts a new epoch so the same method can repair a
regression such as a lamp removed behind a lit bench) and for
`inspect`-style reads, but they leave active
capacity and cannot be modified or reused. Retirement records a per-world
observation-tick floor so an older observation cannot make retired
reservations spendable again. The database still grows with completed work;
each launch opens a fresh database by default, which is the retention bound.

## Related reading

[Sessions and recovery](../architecture/sessions-and-recovery.md) ·
[Save and resume](../../players/save-and-resume.md)
