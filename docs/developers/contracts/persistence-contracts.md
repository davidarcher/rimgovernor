# Persistence contracts

[Documentation](../../README.md)

Vocabulary follows the [glossary](../agent-runbook.md#vocabulary-glossary-epic-1964). Stored names: tables
`standards`, `methods` (`standard_id`, `episode`) and `rounds`; blob keys `standard/<id>`; the
`standard` and `episode` JSON keys; status words `open`/`settled`/`voided` (Standards) and
`open`/`completed`/`voided` (Projects). Finding strings are `unclear`/`unmet`/`met` (Incident bindings
store `unclear`/`active`/`clear`); journal schema 206. Databases and saves from other versions are refused;
there is no adoption path.

Every fact has exactly one home, chosen by what must happen to it when a save is reloaded. A second
copy of a fact is a bug, not a cache.

| Home | Holds | On reload |
|---|---|---|
| Native save, `GovernorState` blobs | Go intent the world cannot show: Standards (`standard/<id>`, a Standard's own intent in its `Record`, e.g. ManageCreepJoiners' inspection record), Projects (`project/<id>`, `GovernorProjectBlob`; finished Projects stay as the record), family plans (`family/*`; the layout plan, `family/layout_plan`, keys each herd reservation, pen, barn and vet room, with its herd's race in `Herd`, #2226) and the soldier squad (`family/soldier_squad`); written by Go at save time, opaque to native | Follows the save's timeline; Go rebuilds its in-memory views from the blobs |
| Native save, other components | Colony identity and native tick guards (the guarded designations of `GuardState`: enclosure, mine safety, wall upgrade and acquisition; deep drilling; home coverage) | Follows the save |
| SQLite, one database per launch (`--state`) | The session journal: actions, transitions, admissions, clock inbox and cursors (native buffers clock events in memory only), request-ID replay | Not restored; read across launches only by postmortem |
| Go memory, or SQLite tables replaced wholesale on every world change | Everything derivable: plans, receipts, snapshots, the definition catalog (read once per load token), the animal race catalog derived from its race rows, the material budget (free stock less construction and live bill-job holds, `policy.MaterialBudget`); the `standards`, `projects`, `methods` and family tables are such views of the save blobs (`RebuildStandards` rebuilds Standards and Projects under one orphan pass, `RebuildFamilies`) | Rebuilt from the save and the live world |
| `flight.jsonl` | All controller telemetry, one row per thing that happened ([flight rows](flight-rows.md), schema v2), the only log: always on for `serve`, nothing else is written to stderr but the startup banner, fatals and panics; snapshot dumps and the acceptance harness's replay transcript are opt-in recordings | Diagnostics only |

The supply Standard's `Record` holds an outstanding comms request's selected
faction, kind, console, negotiator and expected request-tick fence. Its Method
and that intent commit atomically. After a load, native comms work, request tick,
queue and seller facts reconcile it; the saved record never resends an order.
Goodwill and cooldown remain native facts; action attempts remain the session journal.

The blobs reach the save only when it is made (#2352): every vanilla save, whether the player's,
vanilla's or Go's own, parks in native's `pre_save` handshake while Go flushes all blobs in one batched
put, and every Go-made save (the lifecycle save route, Go autosave) flushes first through the same path.
There is no continuous mirror. A save made while no Go is connected keeps the last flush, so it can lack
intent written since. Go's own autosave (`Autosave-1..5`, game-time interval) needs Go holding the clock,
so manual mode gets none; only a save the player makes there flushes. A Go crash loses intent back to the
last save. On a world change (load token, not a native generation bump) Go reads the save's blobs once
and rebuilds its views before the first review: the clock worker's step gate, the read poll and the
flusher all call the same once-per-world rebuild.

`ImproveIdeoligion.Record` carries pending reform intent: ideoligion id,
expected and target designs, and expected reform count. It follows the save;
Methods and action attempts remain in the session journal. Current design,
eligibility and scores are rebuilt from the keyed ideology section and catalog.

Outbound trade Projects own home/settlement target, crew, silver budget, definition demand and mission phase/return intent ([controllable trade](controllable-trade.md)). Native goodwill, cooldown, caravan inventory and routes stay observed facts; the action journal carries the typed participant and attempts.

Before accepting a settlement deal, the Project atomically records bounded
authorized return definitions/counts and purchase commitment with its Hands
Method. These are intent, not an inventory baseline or purchase receipt. They
follow the ordinary Project blob and `pre_save` flush. A reload with commitment
returns conservatively and never buys again to recover a missing receipt.

Native saves no Go bookkeeping (receipts, lineage, purpose tags), and Go keeps no durable copy of what
the save holds. The rest of this page details the session journal.

## What must survive

- **Receipts for uncertain writes.** Every native write is journaled before dispatch and settled by
  observation, never by transport replay. A reply lost after dispatch leaves the action uncertain; it
  is reconciled from the next observation, and its plan stays live until then.
- **Request-ID replay.** Player submissions (policies, decisions, building and research intents,
  control intents, clock acknowledgements) are keyed by the caller's request ID within a world.
  Repeating an ID returns the recorded outcome; a changed body under the same ID is a conflict.
  Native stockpile placement uses the immutable action ID as its replay key.
  The existing in-memory action replay retains those creation receipts for the
  load, including every created zone ID and actual footprint. Go journals the
  receipt and derives ownership from it; no ownership metadata is saved natively.
- **The Rounds cursor and policy inputs.** Latches, recovery histories, observed Concern progress and the current Standard
  bindings let the next Rounds continue where the last stopped; population and resource policies and
  per-pawn decisions are what the reviewer reads.
- **Clock inbox and source cursors.** Native clock reads journal fetched events with their source
  cursor before advancing; delivery consumes the inbox atomically with the review. Colony-scoped
  cursors survive load changes; history beyond a bounded tail is retired once reviewed.
- **Colony extent history.** Established extent regions (with provenance and the tick and native
  generation that first observed them) are an append-only session cache per world (colony, map)
  (`store.EstablishColonyExtent`). A read sees the world's entries at or before its tick. A world
  change empties it and the new session re-establishes the extent from the live world; another colony
  or map sees nothing. Historical Home exclusions are not recorded: they are current restorable
  state, not player vetoes. Ownership and consumer contract: [colony extent](colony-extent.md).
- **Pawn first-seen record (memory only).** The Rounder remembers, per world (colony, map, load),
  the tick, faction def, royal title, host faction and guest status at which each humanlike pawn
  (colonist, visitor, envoy, lodger) first appeared in the pawn table (#2383). It is not saved: a
  restart or a world change empties it, and a pawn present at reload looks new once.

## What is re-derived

Routine Standards are re-derived from observation every Rounds. A world change (new load token, or a
tick rewind in the same load) invalidates the previous bindings, cancels their pending work and
starts fresh Rounds under the new world's root plan; only already dispatched work keeps its recovery
requirement. A pause in the same world suspends the bindings and leaves their work open; the next
enabled review reactivates the same Standards. The stores the departments declare (`policy.DeclareStores`, the desired room-bound
stockpiles) are re-derived on every `MaintainStockpiles` pass; the standing zones are
their only record. Durable Standards come back from the save blobs, so nothing needs a restore step.

## Method ownership

A Method binds one Plan to one owner in `standard_methods` (keyed by Standard, Episode and MethodID),
`project_methods` or `incident_methods` (keyed by owner and MethodID). `plan_owner(plan_id PRIMARY
KEY, kind)` holds one row per bound plan and each method table references it by `(plan_id, kind)` with
a constant `kind`, so a plan has at most one owner across the three tables. Plan-to-owner lookups read
the `plan_methods` view (plan_id, kind, owner_id, episode, method_id, priority, reason; episode is NULL
outside Standards).

## Bounded working set

Settled autopilot plans and superseded invalidated Standards are marked retired, not deleted. Their
IDs, methods and receipts stay readable for `inspect`-style reads and duplicate prevention, but they
leave active capacity and cannot be modified or reused.

- A method that completed in the current Episode is not proposed again. A deficit measured after the
  Standard's settling, once no plan's effects are open, starts a new Episode so the same Method can
  repair a regression (a lamp removed behind a lit bench).
- Retirement records a per-world observation-tick floor so an older observation cannot make retired
  reservations spendable again.
- The database grows with completed work; each launch opens a fresh database by default, which is the
  retention bound.

## Related reading

[Sessions and recovery](../architecture/sessions-and-recovery.md) ·
[Save and resume](../../players/save-and-resume.md)
