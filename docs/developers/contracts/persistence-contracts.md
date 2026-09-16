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

## What is re-derived

Routine goals are re-derived from observation every review. A world change
(new load token, or a tick rewind in the same load) invalidates the previous
bindings, cancels their pending work and starts a fresh review under the new
world's root plan; only work already dispatched keeps its recovery
requirement. Missing facts cannot recover goals, so nothing here needs a
restore step.

## Bounded working set

Settled autopilot plans and superseded invalidated goals are marked retired
rather than deleted: their IDs, methods and receipts stay readable for
duplicate prevention (a method that completed in the current goal epoch is
not proposed again) and for `inspect`-style reads, but they leave active
capacity and cannot be modified or reused. Retirement records a per-world
observation-tick floor so an older observation cannot make retired
reservations spendable again. The database still grows with completed work;
each launch opens a fresh database by default, which is the retention bound.

## Related reading

[Sessions and recovery](../architecture/sessions-and-recovery.md) ·
[Save and resume](../../players/save-and-resume.md)
