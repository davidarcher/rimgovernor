# Persistence contracts

The existing full-review admission evidence owns the observed work-resource view:
material census, exact building/recipe costs, native carried inputs and current
pawn availability. Method actions remain the sole owner of commitments; current
pawn, bench, target and material claims are derived from them inside admission
transactions. This extends the existing review row, not a second reservation
store. Native reservations and carried/embedded inputs are reconciled against
that observation; dispatch alone cannot release an unobserved cost.

[Documentation](../../README.md)

Vocabulary follows the [glossary](../glossary.md). Stored names: tables
`standards`, `methods` (`standard_id`, `episode`) and `rounds`; blob keys `standard/<id>`; the
`standard` and `episode` JSON keys; status words `open`/`settled`/`voided` (Standards, Once included).
Finding strings are `unclear`/`unmet`/`met` (Incident bindings
store `unclear`/`active`/`clear`); governor-state blobs version 6. Databases and saves from other versions are refused;
there is no adoption path.

The C# brain's journal (`RimGovernor.Brain.Store`, Microsoft.Data.Sqlite on the SourceGear `e_sqlite3` provider)
keeps the same ownership rules in its own file format: `PRAGMA user_version` 7 and the same `application_id`.
Domain payloads (actions, admissions, transitions, Standards, Incidents, Rounds and the family records) are
JSON text written by hand-written codecs under `Codec/` with fixed field names and no reflection
serializer; the clock evidence records stay canonical wire blobs (`ClockCodec`). The per-kind column checks the brain kept in SQL `CHECK`
constraints live in the C# domain constructors a payload is rebuilt through. The journal is disposable on
upgrade: a file of another version is moved aside to `<path>.v<old>.bak` by `Store.OpenOrReplace` and a new one
is created, so there are no migrations.

Every fact has exactly one home, chosen by what must happen to it when a save is reloaded. A second
copy of a fact is a bug, not a cache.

| Home | Holds | On reload |
|---|---|---|
| Native save, `GovernorState` blobs | Brain intent the world cannot show: Standards (`standard/<id>`; each carries the Concern it serves as `Kind`; a Standard's own intent in its `Record`, e.g. ManageCreepJoiners' inspection record), family plans (`family/*`; the layout plan, `family/layout_plan`, keys each pen room with its herd's race in `Herd`, #2226) the soldier squad (`family/soldier_squad`) and original temporary combat settings (`family/combat_restoration`); written by the brain at save time, opaque to native | Follows the save's timeline; the brain rebuilds its in-memory views from the blobs |
| Native save, other components | Exact suspended `Bill_GovernorRecipe` intents and native unfinished garments (recipe, stuff, ingredients, remaining work and bound bill); only Hands provisions these after selecting a Method, and only owned bills without unfinished work are cleaned up. Colony identity, per-target construction settings, per-pawn eat-trigger overrides (`EatTriggerState`, #2794; set through `PawnSettingsIntent.eat_trigger`, 0.30 or more stores nothing), the finishing-skill floor (the brain chooses once; native transfers blueprint to frame and enforces every finishing order) and the mutable [construction tier](construction-tiers.md), and native tick guards (the guarded designations of `GuardState`: enclosure, mine safety, wall upgrade and acquisition; home coverage) | Follows the save; construction settings disappear with the exact cancelled/completed target, never transfer by cell |
| SQLite, one database per launch (`RIMGOVERNOR_STATE`) | The session journal: actions, transitions, admissions, clock inbox and cursors (native buffers clock events in memory only), request-ID replay | Not restored; read across launches only by postmortem |
| memory, or SQLite tables replaced wholesale on every world change | Everything derivable: plans, receipts, snapshots, the definition catalog (rows read once per game fingerprint and shared by later loads; context and stat environment read per load token), the animal race catalog derived from its race rows, the material budget (free stock less construction and live bill-job holds, `policy.MaterialBudget`), equipment-owned requirement estimates on transient clothing candidates (derived from the current census, not bill identity or reservations); the `standards`, `methods` and family tables are such views of the save blobs (`RebuildStandards` rebuilds Standards under one orphan pass, `RebuildFamilies`) | Rebuilt from the save and the live world |
| Refusal budget ledger (`policy.RefusalBudget.Entries`) | A planner's refusals by subject and reason, each with its class and the `GenerationSnapshot` it was met in. A pure value: the store that owns the subject persists it. Every planner that retries a refused write (medical, repair, clean, equip, mood, drill, clearance, husbandry, population, quest, ideology, ritual, shrine, permit, defense, firebreak, mine, sleeping, trade and psylink) derives its ledger from the refusals journaled on its plans each step and keeps nothing of its own | With its owner: Standards' records follow the save, a session-scoped planner's ledger is derived and rebuilt |
| OTLP files `otlp/traces.jsonl`, `logs.jsonl`, `metrics.jsonl` | All brain telemetry, one log record per thing that happened ([OTEL signals](otel-signals.md)), the only log: always on in the host, nothing else is written to stderr but the startup banner, fatals and panics; snapshot dumps and the acceptance harness's replay transcript are opt-in recordings | Diagnostics only |
| SQLite table `concern_transitions` (session journal) | Player-facing explanation history: one row per change of a concern's winning typed cause ([below](#concern_transitions)). Not telemetry, so it is not a telemetry row | Not restored; read by the nightly `explain/every-concern` case |
| SQLite table `ledger_rows` (session journal) | A derived, replaceable copy of the latest work-ledger review (`policy.LedgerView`), one row per row of each Ledger table, the whole set replaced in one transaction after every review ([below](#ledger_rows)). Not a second store: the brain never reads it back as truth and may delete it any time | Not restored; the next review rewrites it |
| Native memory, pawn activity execution | Exact authorized assignment/revision, current step, native job ownership and dependency references. The same row retains the last explicit release for idempotent receipt retry, without owning a pawn/job. Hands journals installation; typed pawn readback exposes execution. No second saved plan. | Invalidated on authority/world change or despawn; explicit release ends ownership immediately. A fresh assignment replaces the release row. |
| Native memory, pawn activity open intervals | At most 512 open colonist intervals; no completed history or second save. Completed intervals enter the existing OTLP ring; reports are derived from retained rows | Reset on game/load change or tick rewind; unobserved time stays unknown |
| Host memory, encounter prediction | One bounded request and its owned isolated worker; transient empirical replies and caller-supplied reviewed qualification records. No intent, reservation, execution progress or second forecast store. | Current request identity fences world/tick/build/mod/tactic/availability changes; no prediction is restored from a save or journal. Disposal ends the owned process. |

Fixed production batch promises add no store: each review projects existing
session-journal Method actions, receipt bill IDs and current native remaining
counts into `DerivedDemand.Work`. Reconciled material holds and free stock are
derived by `ResourceDemandOf` for the shared acquisition, trade-retention and
stone-wall funding slice ([scope and unknowns](../architecture/colony-forecast.md#live-fixed-batch-material-promises)).
These promises are rebuilt each review and do not survive a fresh journal as
independent reservations.
Unfinished item bindings and embedded ingredients remain owned by RimWorld's save.
The review derives their material credit from native readback; it does not persist
an embedded-material ledger. The existing native production record's configuration
fingerprint excludes mutable remaining count; no second tracker is added.

Fixed-batch admission uses a replaceable material-evidence field in the existing
Rounds record: observed stock, construction deficit, native ingredient holds,
catalog recipe inputs and the reviewed batch claims. Journal actions remain the
only commitment owner. Admission re-reads them inside the method transaction;
it never maintains a mutable remaining-stock ledger. Full reviews replace this
evidence, immediate reviews retain it only in the same world, and world changes
or rewinds discard it. Missing evidence refuses new fixed batches.

### Layout plan record

The layout plan (`policy.LayoutPlan`, see [spatial contracts](spatial-contracts.md#layout-planner))
has two homes. In the session database it is the table `colony_layout_plans`
(`LayoutPlan.cs`): `RecordLayoutPlan` refuses a plan that is not `Valid()` and writes
one row per world (colony, map) and tick, and `LayoutPlan` reads the newest row at or before
the tick. A row that does not decode or is not `Valid()` reads as none (the record comes back
`Invalid`), so the review derives a fresh plan (`DeriveLayoutPlan`) rather than adopting it. The
stored JSON is the `LayoutPlan` fields (Rooms, Links, Entrances, Owed, Trips, OutsideTrips, Zones,
Reservations, Sites, RetiredGround, Roofed, Lamps, Pieces, Conduits, Cold, Hot, NextRoomID) and decoding
rejects an unknown field (Pieces is optional on read, so a plan saved before it still decodes and gains it at the next replan), so a plan written with a field since removed reads as none. In
the save it is the blob `family/layout_plan` (`GovernorState.cs`,
`GovernorLayoutPlanKey`), which follows the save's timeline. The runtime records a plan only
when it derives one or a replan succeeds (`reviewLayoutPlan`, `ColonyPlan.cs`); a failed
replan keeps the saved plan. The `StationAdvice` that produced it (graph, groups, embedding,
comfort and matched rooms) is not persisted: the Rounder holds it in memory and recomputes it
at each layout review.

### concern_transitions

The player-facing explanation history lives in the session SQLite store, not in
telemetry (epic #2778, #2788). `recordPlannerReasons`
(`PlannerReasons.cs`) appends one row per change of a
concern's winning typed cause, in the same step that files the goal's planner
note; `store.RecordConcernTransitions` writes them and `store.ConcernTransitions` reads them. The
The launcher's read-only
reader was removed in #2803. The table is `concern_transitions` (schema version 216;
a database of another version is moved aside, never migrated):

| Column | Meaning |
|---|---|
| `sequence` | `INTEGER PRIMARY KEY AUTOINCREMENT`; monotonic, never reused, so a reader can follow by sequence |
| `wall_ms`, `tick` | wall clock (unix ms) and game tick when filed |
| `concern` | the concern id, or for a planner that serves no single concern (fields, supplies, workshop, hospital, tend, rescue, armory, moodRelief, recovery, husbandry, prisonerInteraction, populationCustody, populationJoiner, dialog, trade) the planner's own name (`plannerEntry.filingKey`; no progress record, so `method` is empty), or for a food bill purpose the ledger reports on (`cookingBills`, `preservationBills`, `butcherBills`, `cookAheadBills`) that purpose's subject key (`RoundsBillPlanner.reportTarget`) |
| `verdict` | the winning planner verdict's outcome (`refused`, `waiting`, `admitted`, `nothing_to_do`, `disabled`, `combat_orders`, `hold_fallback`) |
| `cause` | the new `policy.Cause` wire value, `''` when the note cleared (`admitted`, `nothing_to_do`); anything outside the vocabulary is refused on write |
| `subject`, `method` | bounded subject (may be empty); the concern's current method on its progress record when filed (empty when none) |
| `previous_cause` | the cause replaced; `''` when it was a clear; `NULL` when unknown |
| `held_ticks` | game ticks the previous cause stood (whole across a subject-only change); `NULL` when unknown |

No row while the cause is unchanged or when only the subject changes; a concern
first seen clear files no row. The standing state that decides "changed" is in
memory only, as before: after a restart a concern's first non-clear cause has
`previous_cause` and `held_ticks` `NULL` (unknown, not zero), and `held_ticks` is
also `NULL` when the game clock went backwards (a save load). No free text.

Retention is a ring by count: each insert deletes rows more than
`store.ConcernTransitionRetention` (8192) sequences behind the newest, in the same
transaction. The history lasts as long as the `state-*.sqlite` file; a launch that
continues a state keeps it, a fresh state starts empty. Reader: the nightly case
`explain/every-concern` (#2702, through the case's own store handle). The
Observe tab reads log rows, which carry no explanation rows.

The derived `rounds` payload records its last inspection scope (`Immediate`) and
last full enabled inspection time (`OrdinaryTick`) separately from the latest
review `Tick`. Immediate reviews update only combat, critical care, worker
restoration, fire safety, shelter and protective area needs. Other bindings,
methods, progress and derived histories retain their original observation times.
Disaster service history and recovery selections are not refreshed from missing
ordinary inputs. An active hunt retains its incident and prey until a review has
food-plan evidence or an observed hostile takes over. World changes and rewinds
discard these derived histories through the same canonical review transaction.

The supply Standard's `Record` holds an outstanding comms request's selected
faction, kind, console, negotiator and expected request-tick fence. Its Method
and that intent commit atomically. After a load, native comms work, request tick,
queue and seller facts reconcile it; the saved record never resends an order.
Goodwill and cooldown remain native facts; action attempts remain the session journal.

The blobs reach the save only when it is made: every vanilla save, whether the player's,
vanilla's or the brain's own, parks in native's `pre_save` handshake while the brain flushes all blobs in one batched
put, and every brain-made save (the lifecycle save route, the brain autosave) flushes first through the same path.
There is no continuous mirror. A save made while no the brain is connected keeps the last flush, so it can lack
intent written since. the brain's own autosave (`Autosave-1..5`, game-time interval) needs the brain holding the clock,
so manual mode gets none; only a save the player makes there flushes. A brain crash loses intent back to the
last save. On a world change (load token, not a native generation bump) the brain reads the save's blobs once
and rebuilds its views before the first review: the clock worker's step gate, the read poll and the
flusher all call the same once-per-world rebuild.

`ImproveIdeoligion.Record` carries pending reform intent: ideoligion id,
expected and target designs, and expected reform count. It follows the save;
Methods and action attempts remain in the session journal. Current design,
eligibility and scores are rebuilt from the keyed ideology section and catalog.

Outbound trade-mission Standards own home/settlement target, crew, silver budget, definition demand and mission phase/return intent ([controllable trade](controllable-trade.md)). Native goodwill, cooldown, caravan inventory and routes stay observed facts; the action journal carries the typed participant and attempts.

Before accepting a settlement deal, the trade-mission Standard atomically records bounded
authorized return definitions/counts and purchase commitment with its Hands
Method. These are intent, not an inventory baseline or purchase receipt. They
follow the ordinary Standard blob and `pre_save` flush. A reload with commitment
returns conservatively and never buys again to recover a missing receipt.

Native saves no the brain bookkeeping (receipts, lineage, purpose tags), and the brain keeps no durable copy of what
the save holds. The rest of this page details the session journal.

### ledger_rows

The Ledger view is built live each Round from the Rounder's ledger memory
(`reviewLedger`, `Rounder.WorkLedger`) and, since #2805, copied into the session
store (the launcher's Ledger tab and `/api/ledger` were removed).
The copy is derived and replaceable: after each review `store.ReplaceLedgerRows`
deletes every row and inserts the review's rows in one transaction, so the table
always holds exactly the latest review of the current brain. Nothing reads
it back into policy; the in-memory ledger stays the only input. Columns (schema
version 216; a database of another version is moved aside, never migrated):

| Column | Meaning |
|---|---|
| `section` | `round` (one row: status, tick, grace, abstained), `order`, `orphan`, `excluded`, `unmet`, `window`, `declarer`, `export_gap`, `export_candidate`, `export_drop` |
| `position` | row order within the section (the Ledger tab's order) |
| `tick` | game tick of the review |
| `cells` | a JSON object of the row's bounded fields (names in `policy.LedgerView.Rows`); lists are comma-joined strings; no free text beyond a native refusal reason already journaled |

## What must survive

- **Receipts for uncertain writes.** Every native write is journaled before dispatch and settled by
  observation, never by transport replay. A reply lost after dispatch leaves the action uncertain; it
  is reconciled from the next observation, and its plan stays live until then.
- **Request-ID replay.** Player submissions (policies, decisions, building and research intents,
  control intents, clock acknowledgements) are keyed by the caller's request ID within a world.
  Repeating an ID returns the recorded outcome; a changed body under the same ID is a conflict.
  Native stockpile placement uses the immutable action ID as its replay key.
  The existing in-memory action replay retains those creation receipts for the
  load, including every created zone ID and actual footprint. The brain journals the
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
  (colonist, visitor, envoy, lodger) first appeared in the pawn table. It is not saved: a
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

Selected coordinated activities keep their remaining tasks, dependency identities,
participant bindings and bounded recovery obligations in the existing journaled
`PawnActivities` payload. This is selected intent, not a saved forecast or a second
workflow store. Successful task progress is derived from exact native ownership
and terminal readback before a successor journal revision removes the obligation.
Running participants retain their identical installed order across revisions.
Fresh resource/site observations bind dependent work; predicted quantities never
become inventory. A consuming operation is never replayed after uncertain or failed
execution. World/load changes discard the disposable execution context.

A Method binds one Plan to one owner in `standard_methods` (keyed by Standard, Episode and MethodID),
or `incident_methods` (keyed by owner and MethodID). `plan_owner(plan_id PRIMARY
KEY, kind)` holds one row per bound plan and each method table references it by `(plan_id, kind)` with
a constant `kind`, so a plan has at most one owner across the two tables. Plan-to-owner lookups read
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

## Combat batches and restoration

Combat decisions dispatch one immutable typed batch through Hands. Each batch is
an Incident method beside the fight, linked to its fight plan. Ordered commands,
dispatch state and individual native outcomes live only in the session journal.
An accepted batch may contain individual refusals; an uncertain batch is never
reported as applied and is never blindly retried. Observable settings and
positions can settle its current effect; historical attacks and drug doses
remain uncertain when no observation can establish them.

A later recorded tactical decision can supersede the execution responsibility
of older batches once every temporary setting has a saved restoration obligation.
`plans.superseded_by` identifies that decision. Retirement removes the old batch
from active capacity while retaining its original receipt and uncertainty. A
mere dispatch return does not retire uncertain work. Fight closure requires
restoration to finish first. Retired combat batch methods stay readable by plan
identity, outside the active Incident method list.

Before a temporary door or animal restriction changes, the brain records its original
hold-open/forbidden settings or allowed-area identity. Unknown originals cannot
authorize a temporary change. These restoration obligations alone follow the
save. A reload creates fresh Hands cleanup from those settings; it never restores
old attacks, receipts or tactical memory. Cleanup uses the saved originals,
including an animal's prior restricted area, rather than assuming unrestricted.
The ordinary current-world authority and Safeguards still apply. A cleanup
method may run after the combat Situation clears only when its exact actions
match the saved restoration intent. A fresh cleanup decision transfers those same
originals and retires the prior execution responsibility; uncertain historical
receipts remain unchanged. Pending cleanup does not block unrelated protection.
For animal cleanup, native deletes the exact `Combat <pawn ID>` area even
when loaded-map lookup proves the animal absent, dead or no longer player-owned.
Only the completed clear's `not_found` or `not_ours` result discharges that
animal's paired original-area obligation. Unknown, stale or lost replies retain
it; a fresh cleanup method must obtain its own result, including after reload.
Historical uncertain actions remain uncertain, never relabeled as restored.
## Related reading

[Sessions and recovery](../architecture/sessions-and-recovery.md) ·
[Save and resume](../../players/save-and-resume.md)

Protective area-setting methods on RecoverDisasterServices may coexist with open
RecoveryService actions on that same Incident. Their normal typed payload,
Safeguards and action journal remain authoritative; any other open action still
blocks a second method, and service work is neither canceled nor replayed.

Harvest/delivery activity obligations live in their existing journaled
`PawnActivityOrder.OutputDelivery` payload, including resource/source area,
quantity cap, destination, original deadline and attempt budget. Native holds
the installed execution copy. A selected cooking continuation additionally
records the bench/bill/recipe, one-resource input count, output definition/count
and final destination in this same payload. Ingredient identities are rebound
from observations after supply; after cooking authorization only the output
delivery obligation remains. Failed cooking is never automatically replayed.
Fresh observations are ephemeral. Predecessor
plans retain cleanup responsibility after a positively refused replacement.
This adds no independent stock ledger or saved production workflow.

Needs candidates and projected timelines are derived from the current Rounds
frame and selected forecast inputs, in memory only; `RoundsFacts.NeedsAlternatives`
is not admitted intent. It adds no persistence owner or second progress record.
The existing Method/Plan journal owns any subsequently selected execution.
