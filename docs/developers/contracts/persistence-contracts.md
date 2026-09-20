# Persistence contracts

[Documentation](../../README.md)

The Go controller keeps one SQLite database per run (`--state`). It is a
cache of the autopilot's own bookkeeping plus the journals that must survive a
restart; the colony itself lives only in the game and its saves. There are no
paired backups, manifests or archive tables.

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
  the last one stopped; population, expedition and resource policies and
  per-pawn decisions are what the reviewer reads.
- **Clock inbox and source cursors.** Native clock reads journal fetched
  events with their source cursor before advancing; delivery consumes the
  inbox atomically with the review. Colony-scoped cursors survive load
  changes; history beyond a bounded tail is retired once reviewed.

- **Colony extent history.** Established extent regions (with their
  provenance and the tick and native generation that first observed them)
  and explicitly selected expansion areas (with the reason recorded on add
  and on remove) are an append-only journal scoped to the world (colony,
  map) and to its saved timeline (`store.EstablishColonyExtent`,
  `AddExpansionArea`, `RemoveExpansionArea`). Each load token is one
  timeline segment. On its first observation (`ReconcileColonyExtent`,
  which every write also performs) a load forks from the segment of the
  same world whose observed span covered that tick, preferring the segment
  played most recently when several branches cover it, or continues the
  latest segment that ended before it; with no such segment it starts
  empty. A segment sees its ancestors' entries only up to each fork tick,
  so loading an older save restores exactly what its timeline had recorded
  by that tick, and territory established later or on another branch never
  leaks back; another colony or map sees nothing. A tick rewind within one
  load discards that load's entries past the tick. The reconciliation
  report names the parent segment and counts the entries restored, the
  parent's entries beyond the fork and any discarded, for the caller's log.
  Historical Home exclusions are not recorded here: they are current
  restorable state, not player vetoes. Ownership and the consumer contract:
  [colony extent contract](colony-extent.md).

- **Colony grid.** The layout grid (`store.EstablishColonyGrid`,
  `ColonyGrid`) is one row per world and timeline segment, sharing the
  colony extent's segments and reconciliation: a load sees the grid its
  lineage established at or before each fork, so an older save restores the
  grid that save knew (or none, and may fix its own), a later save restores
  its origin segment's grid, another colony or map sees none, and a tick
  rewind within one load past the grid's tick forgets it. A grid visible
  through the lineage is never replaced: `EstablishColonyGrid` returns the
  visible grid and reports nothing established.
- **Layout tidies.** The re-sites `TidyLayout` moved or is moving
  (`store.RecordLayoutTidy`, `LayoutTidies`, #611) share the extent's
  segments and reconciliation the same way: each status change (moving,
  done, abandoned) is a row on the recording load, the latest visible row
  per item through the lineage is the item's state, an older save restores
  what it knew and a same-load tick rewind past a tidy's tick forgets it.

## What is re-derived

Routine goals are re-derived from observation every review. A world change
(new load token, or a tick rewind in the same load) invalidates the previous
bindings, cancels their pending work and starts a fresh review under the new
world's root plan; only work already dispatched keeps its recovery
requirement. A pause in the same world suspends the bindings and leaves
their work open; the next enabled review reactivates the same goals. Missing facts cannot recover goals, so nothing here needs a
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
