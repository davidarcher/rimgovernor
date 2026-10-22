# Persistence contracts

The existing full-review admission evidence owns the observed work-resource view:
material census, exact building/recipe costs, native carried inputs and current
pawn availability. Method actions remain the sole owner of commitments; current
pawn, bench, target and material claims are derived from them inside admission
transactions. This extends the existing review row, not a second reservation
store. Native reservations and carried/embedded inputs are reconciled against
that observation; dispatch alone cannot release an unobserved cost.

[Documentation](../../README.md)

Vocabulary follows the [glossary](../glossary.md). Stored names: tables `plan_owner`,
`need_methods` and `rounds`; blob keys `family/*`. Finding strings are `unclear`/`unmet`/`met`
and situations `unclear`/`active`/`clear`; governor-state blobs version 7, and a version 6 blob
still loads (its `standard/<id>` blobs are ignored). Databases from other versions are replaced;
there is no adoption path.

The C# brain's journal (`RimGovernor.Brain.Store`, Microsoft.Data.Sqlite on the SourceGear `e_sqlite3` provider)
keeps the same ownership rules in its own file format: `PRAGMA user_version` 27 (`Store.SchemaVersion`) and the same `application_id`.
Domain payloads (actions, admissions, transitions, Rounds and the family records) are
JSON text written by hand-written codecs under `Codec/` with fixed field names and no reflection
serializer; the clock evidence records stay canonical wire blobs (`ClockCodec`). The per-kind column checks the brain kept in SQL `CHECK`
constraints live in the C# domain constructors a payload is rebuilt through. The journal is disposable on
upgrade: a file of another version is moved aside to `<path>.v<old>.bak` by `Store.OpenOrReplace` and a new one
is created, so there are no migrations.

The save keeps only player intent and `family/*` records
([durable state](../architecture/control-loop.md#durable-state)); Methods live in `need_methods`
(`plan_owner` kind `need`). Journal version 15 added the `admin_creep_joiners` table, the
session cache of the `family/admin/creep_joiners` record (#3264), and version 17 the
`economy_trade_missions` table, the session cache of the `family/economy/trade_missions` record (#3316).
Version 19 dropped the Standard and Incident tables and the `plan_methods` view, and version 20 renamed
`concern_transitions` to `need_transitions` (#3265). Version 24 added the `goals` table, the session cache of the `family/goals` record (#3489). Version 26 changed the pawn-activities action body (step `Targets` and `Cells`, task `PriorSteps` and `UseUntilTick`), and version 27 adds the practice-surgery variant `Practice.Surgery` (recipe, part index, medicine kind; #3637) to journaled activity steps, and version 28 drops `Surgeon` from the surgery action (#3638), and version 29 drops `Practice.Surgery` again (#3344), and version 30 drops the `Clean` action and the step's `WaitForFilth` (#3667), and version 31 drops the `Standby` step kind (#3669), and version 34 adds the step's `FullLoad` and `Collect` (#3046); `StoreSchemaPinTests` pins the codec shape to the version. An older save's `trade-mission` Standard blobs
are dropped on load (decision 12).

Every fact has exactly one home, chosen by what must happen to it when a save is reloaded. A second
copy of a fact is a bug, not a cache.

| Home | Holds | On reload |
|---|---|---|
| Native save, `GovernorState` blobs | Brain intent the world cannot show: family plans (`family/*`; the layout plan, `family/layout_plan`, keys each pen room with its herd's race in `Herd`, #2226) the soldier squad (`family/soldier_squad`), original temporary combat settings (`family/combat_restoration`), the creepjoiner inspection record (`family/admin/creep_joiners`: the pawns ordered and done, keyed by colony and map, so it is read back after a reload; #3264), the settlement trade missions under way (`family/economy/trade_missions`: each mission's minted id and intent, keyed by colony and map; a mission leaves it once delivered or ended; #3316) the ideology family's pending reform (`family/ideology/reform`, #3260) and the player's goals, themes and preferences (`family/goals`, #3489); written by the brain at save time, opaque to native | Follows the save's timeline; the brain rebuilds its in-memory views from the blobs |
| Native save, other components | Exact non-autonomous `Bill_GovernorRecipe` intents and native unfinished garments (recipe, stuff, ingredients, remaining work and bound bill); only Hands provisions these after selecting a Method, and only owned bills without unfinished work are cleaned up. Colony identity, per-target construction settings, the finishing-skill floor (the brain chooses once; native transfers blueprint to frame and enforces every finishing order) and the mutable [construction tier](construction-tiers.md), and native tick guards (the guarded designations of `GuardState`: enclosure, mine safety, wall upgrade and acquisition; home coverage), and in the same ledger the haul-out records of the reconcile's HaulOut on ordinary stacks (a removal with no designation, finished by its last carry or cancelled once its stack leaves its cell otherwise, #3625). Native observation rings, each one packed, versioned, append-only string: realized consumption (`ConsumptionState`, hourly spend per definition and reason, 60 days) and storage occupancy (`StorageOccupancyState`, hourly stacks held per player storage group by load id, 15 days); the brain derives rates from them and keeps no copy past the load | Follows the save; construction settings disappear with the exact cancelled/completed target, never transfer by cell |
| SQLite, one database per launch (`RIMGOVERNOR_STATE`) | The session journal: actions (including selected productive task service-time bounds), transitions, admissions, request-ID replay; no clock events (native buffers them in memory only, #3417) | Not restored; read across launches only by postmortem |
| memory, or SQLite tables replaced wholesale on every world change | Everything derivable: plans, receipts, snapshots, the definition catalog (read per load token), the def stat table (def-level stat values, allowed stuffs and adjusted costs; owned by the mod's `NativeStatTable`, built once per load on the game thread by the catalog read and read immutable by the brain catalog; [stat evaluation](stat-classes.md)), the animal race catalog derived from its race rows, the material budget (free stock less construction and live bill-job holds, `policy.MaterialBudget`), equipment-owned requirement estimates on transient clothing candidates (derived from the current census, not bill identity or reservations); the family tables are such views of the save blobs (`RebuildFamilies`; `RebuildOwners` retires the previous world's owned plans in one orphan pass) | Rebuilt from the save and the live world |
| Refusal budget ledger (`policy.RefusalBudget.Entries`) | A planner's refusals by subject and reason, each with its class and the `GenerationSnapshot` it was met in. A pure value: the store that owns the subject persists it. Every planner that retries a refused write (medical, repair, equip, mood, drill, clearance, husbandry, population, quest, ideology, ritual, permit, defense, firebreak, mine, sleeping, trade and psylink) derives its ledger from the refusals journaled on its plans each step and keeps nothing of its own | With its owner: a session-scoped planner's ledger is derived and rebuilt |
| OTLP files `otlp/traces.jsonl`, `logs.jsonl`, `metrics.jsonl` | All brain telemetry, one log record per thing that happened ([OTEL signals](otel-signals.md)), the only log: always on in the host, nothing else is written to stderr but the startup banner, fatals and panics; snapshot dumps and the acceptance harness's replay transcript are opt-in recordings | Diagnostics only |
| SQLite table `need_transitions` (session journal) | Player-facing explanation history: one row per change of a Need's winning typed cause ([below](#need_transitions)). Not telemetry, so it is not a telemetry row | Not restored; read by the nightly `explain/every-concern` case |
| Native memory, pawn activity execution | Exact authorized assignment/revision, current step, native job ownership and dependency references. The same row retains the last explicit release for idempotent receipt retry, without owning a pawn/job. Hands journals installation; typed pawn readback exposes execution. No second saved plan. | Invalidated on authority/world change or despawn; explicit release ends ownership immediately. A fresh assignment replaces the release row. |
| Native memory, pawn activity open intervals | At most 512 open colonist intervals; no completed history or second save. Completed intervals enter the existing OTLP ring; reports are derived from retained rows | Reset on game/load change or tick rewind; unobserved time stays unknown |
| Host memory, encounter prediction | One bounded request and its owned isolated worker (one transient arena map and its observed temperature/light inputs, discarded between samples); transient empirical replies and caller-supplied reviewed qualification records. No intent, reservation, execution progress or second forecast store. | Current request identity fences world/tick/build/mod/tactic/availability changes; no prediction is restored from a save or journal. Disposal ends the owned process. |

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
Rounds record: observed stock, known construction census, native ingredient holds,
catalog recipe inputs and the reviewed batch claims. Journal actions remain the
only commitment owner. Standing sites retain demand diagnostics but reserve no stock;
admitted construction supply tasks claim their remaining inputs once across revisions.
Admission re-reads actions inside the method transaction;
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
`GovernorLayoutPlanKey`), which follows the save's timeline; a blob that does not decode or is not `Valid()`
restores as no plan the same way. The runtime records a plan only
when it derives one, a replan succeeds (`reviewLayoutPlan`, `ColonyPlan.cs`; a replan keeps the defense tier nodes over the same killbox), the census finds retired ground cleared, or the defense round sites new defense tier nodes into it (`SiteTiersAsync`, #3583); a failed
replan keeps the saved plan. The `StationAdvice` that produced it (graph, groups, embedding,
comfort and matched rooms) is not persisted: the Rounder holds it in memory and recomputes it
at each layout review. The urgent builds waiting for a review (`UrgentBuilds`, #3537) and a failed carry's dropped
stack (native `Entry.Dropped`) are in memory only: a restart empties them, and the next review reads the placed sites afresh.

### need_transitions

The player-facing explanation history lives in the session SQLite store, not in
telemetry (epic #2778, #2788). `recordPlannerReasons`
(`PlannerReasons.cs`) appends one row per change of a
Need's winning typed cause, in the same step that files the Need's planner
note; `Store.RecordNeedTransitions` writes them and `Store.NeedTransitions` reads them. The
launcher's read-only reader was removed in #2803. The table is `need_transitions` (journal
version 20, #3265, which renamed `concern_transitions` and its `concern` column; a database of
another version is moved aside, never migrated):

| Column | Meaning |
|---|---|
| `sequence` | `INTEGER PRIMARY KEY AUTOINCREMENT`; monotonic, never reused, so a reader can follow by sequence |
| `wall_ms`, `tick` | wall clock (unix ms) and game tick when filed |
| `need` | the Need's filing key (`NeedId.FilingKey`), or the own name of a family or planner that files beside its Needs (no progress record, so `method` is empty), or for a bill purpose the ledger reports on (`food/cooking/bills`, `food-stores/storage/bills`, `food/supply/bills`, `food-stores/refrigeration/bills`) that purpose's subject key |
| `verdict` | the winning planner verdict's outcome (`refused`, `waiting`, `admitted`, `nothing_to_do`, `disabled`, `combat_orders`, `hold_fallback`) |
| `cause` | the new `policy.Cause` wire value, `''` when the note cleared (`admitted`, `nothing_to_do`); anything outside the vocabulary is refused on write |
| `subject`, `method` | bounded subject (may be empty); the full MethodId the Need works when filed, the `method_id` its `need_methods` row binds (empty when it works none: met, refused or unmethoded) |
| `previous_cause` | the cause replaced; `''` when it was a clear; `NULL` when unknown |
| `held_ticks` | game ticks the previous cause stood (whole across a subject-only change); `NULL` when unknown |

No row while the cause is unchanged or when only the subject changes; a key
first seen clear files no row. The standing state that decides "changed" is in
memory only, as before: after a restart a key's first non-clear cause has
`previous_cause` and `held_ticks` `NULL` (unknown, not zero), and `held_ticks` is
also `NULL` when the game clock went backwards (a save load). No free text.

Retention is a ring by count: each insert deletes rows more than
`Store.NeedTransitionRetention` (8192) sequences behind the newest, in the same
transaction. The history lasts as long as the `state-*.sqlite` file; a launch that
continues a state keeps it, a fresh state starts empty. Reader: the nightly case
`explain/every-concern` (#2702, through the case's own store handle). The
Observe tab reads log rows, which carry no explanation rows.

The derived `rounds` payload records its last inspection scope (`Immediate`) and
last full enabled inspection time (`OrdinaryTick`) separately from the latest
review `Tick`. Immediate reviews update only combat, critical care, worker
restoration, shelter and protective area needs. Other bindings,
methods, progress and derived histories retain their original observation times.
Disaster service history and recovery selections are not refreshed from missing
ordinary inputs. An active hunt retains its combat standing and prey until a review has
food-plan evidence or an observed hostile takes over. World changes and rewinds
discard these derived histories through the same canonical review transaction.

An outstanding comms request has no saved record: its Method's actions in the
session journal carry the selected faction, kind, console and negotiator. After a
load, native comms work, request tick, queue and seller facts reconcile it, and
nothing resends an order.
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

The `family/ideology/reform` record (#3260) carries pending reform intent: ideoligion id,
expected and target designs, expected reform count and the goal it serves (#3500), scoped to its colony and map. It
follows the save; an older (schema 6) save's `ImproveIdeoligion` `standard/<id>` blob is read into
it once at load when the record is absent, and is not kept;
Methods and action attempts remain in the session journal. Current design,
eligibility and scores are rebuilt from the keyed ideology section and catalog.

Outbound trade missions, in the `family/economy/trade_missions` record (#3316), own home/settlement target, crew, silver budget, definition demand and mission phase/return intent ([controllable trade](controllable-trade.md)). Native goodwill, cooldown, caravan inventory and routes stay observed facts; the action journal carries the typed participant and attempts.

The player's goals, themes and standing preferences are the colony-scoped `family/goals` record (#3489): minted goal and
theme ids (`NextId`, never reused), typed `GoalRecord`s of a closed `GoalKind` vocabulary (kinds with no pursuit still
validate and persist), themes that reference a preset by id with the player's removals, and the preferences owning
policies read as facts. An empty record deletes the row, and a save made before it simply lacks the key. The panel
reads it through `BrainHost.GoalsView` (a cached snapshot, never inference) and writes it through
`BrainHost.GoalsEditor` (validated, stored for the observed colony, wakes the `goals` planner; #3492). Both exist
whether or not routes are served, and there is no HTTP goals route.
`GoalsView.Current()` also returns the goals Family's last derived goal and theme statuses, handed over in memory through
the `GoalStatusBoard` that the family's full read fills (#3497); they are not stored or sent, and a goal added since the
last review has none yet.

The preferences reach the policies as `RoundsFacts.Preferences` (#3495), stamped once where the review facts are observed
(`ObserveRoundsAsync`, with the combat inputs and the break responders loading the same record). There is no veto layer:
the owning policy reads them and never proposes what they forbid. Diet overrides and `Forbidden` human butchery and insect
meat shape `FoodPolicyRules.DietOf`, `Forbidden` human butchery empties `SelectHumanButcher`, organ harvest and part
recovery return nothing under `Forbidden` organ harvest, slavery is refused under `Forbidden` slavery,
human leather leaves the stuff catalog and the apparel options, a protected thing is in no clearance, retire or bed-removal
step (reason `protected_thing`), and a protected pawn is held back from the draft while another eligible fighter can fight
(`defense_action` reason `protected_pawn_drafted_no_other_fighter` when none can). The apparel preference (#3498) is
stamped on each pawn's outfit census (`ApparelPolicyState.Preference`) and narrows the outfit filter, and the clothing
demand keeps only garments an outfit still allows; `Forbidden` drugs and the drug-policy preference are planned by
`DrugPolicyPlanRules` ([apparel](apparel-policy.md#preferences), [drug policy](action-contracts.md)), and `Forbidden` drugs
also empties the drug runways, the social crop fields and the social drugs' production targets.

Before accepting a settlement deal, the `economy/trade-mission` Need atomically records bounded
authorized return definitions/counts and purchase commitment with its Hands
Method (`CommitTradeMissionMethod`). These are intent, not an inventory baseline or purchase receipt. They
follow the `family/economy/trade_missions` blob and `pre_save` flush. A reload with commitment
returns conservatively and never buys again to recover a missing receipt. The Need's phase Methods are per load
token, so after a reload a phase may be attempted once more.

Native saves no the brain bookkeeping (receipts, lineage, purpose tags), and the brain keeps no durable copy of what
the save holds. The rest of this page details the session journal.

The Ledger view is built live each Round from the Rounder's ledger memory (`Rounder.WorkLedgerView`) and is not
persisted: the derived `ledger_rows` copy that #2805 wrote after every review (for a launcher Ledger tab and an
`/api/ledger` since removed) had no reader, and was deleted with its table under #3566. The in-memory ledger
stays the only input.

## What must survive

- **Receipts for uncertain writes.** Every native write is journaled before dispatch and settled by
  observation, never by transport replay. A reply lost after dispatch leaves the action uncertain; it
  is reconciled from the next observation, and its plan stays live until then.
- **Request-ID replay.** Player control intents are keyed by the caller's request ID within a world.
  Repeating an ID returns the recorded outcome; a changed body under the same ID is a conflict.
  Native keeps no action replay: the in-process game-thread hop returns the result of every body
  that ran, and cancellation reaches only a hop still queued. The brain journals each
  receipt (a stockpile's created zone IDs and footprint included) and derives ownership from it;
  no ownership metadata is saved natively.
- **The Rounds cursor and policy inputs.** Latches, recovery histories and the Needs' progress rows let the
  next review continue where the last stopped; population and resource policies and
  per-pawn decisions are what the reviewer reads.
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
- **Planned-room selection (memory only).** The Rounder remembers, per world, the latest known coupled
  comparison's assessed planned rooms and selected planned sites (`Rounder.RoomSelection`); the room
  reconcile gates unstarted rooms on it. It is not saved: a restart or a world change empties it, and
  unstarted rooms place as before until the next comparison
  ([planned rooms](../architecture/portfolio-selection.md#planned-rooms)).
- **Defense verification (memory only).** The Rounder remembers, per world (colony, load, map), whether
  the `layout/defense` step verified the stored layout complete, at which tick and against which fight,
  and when it last probed the turret siting (`Rounder.DefenseVerification`, #3008). It drives the Need's
  Verified fact and the re-verify and siting cadence. It is not saved: a restart or a world change
  empties it and the layout re-verifies. Combat does not read it.

## What is re-derived

Needs are re-derived from observation on every review; nothing about a Need is stored. A world change
(new load token, or a tick rewind in the same load) retires the previous world's owned plans
(`RebuildOwners`), cancels their pending work and starts a fresh review under the new world's root
plan; only already dispatched work keeps its recovery requirement. A pause in the same world leaves the
Needs' work open; the next enabled review continues it. The stores the families declare
(`policy.DeclareStores`, the desired room-bound stockpiles) are re-derived on every `admin/stockpiles`
review; the standing zones are their only record. Family records come back from the save blobs, so
nothing needs a restore step. Saves no longer write `standard/<id>` blobs; an older save's are ignored
on load except where a family reads its own legacy record once (the ideoligion reform).

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

A Method binds one Plan to one owner in `need_methods`, keyed by world, NeedId, MethodId and
`attempt`. `plan_owner(plan_id PRIMARY KEY, kind)` holds one row per bound plan (kind `need`), and
`need_methods` references it by `(plan_id, kind)`, so a plan has at most one owner. A released MethodId
admitted again is a new attempt row; the rows are history, and a rebuild retires the plans but keeps the
rows. A NeedId (`family/kind[/subject]`) also stands in the `need` column of `need_transitions` and the
`concern` attr of `planner_step` for a family entry.

## Bounded working set

Settled autopilot plans and superseded plans are marked retired, not deleted. Their
IDs, methods and receipts stay readable for `inspect`-style reads and duplicate prevention, but they
leave active capacity and cannot be modified or reused.

- A Need-owned Method continues while its MethodId is open and may be admitted again once its Need is
  unmet ([claim admission](../architecture/control-loop.md#method-journal-claim-admission)).
- Retirement records a per-world observation-tick floor so an older observation cannot make retired
  reservations spendable again.
- The database grows with completed work; each launch opens a fresh database by default, which is the
  retention bound.

## Combat batches and restoration

Combat decisions dispatch one immutable typed batch through Hands. Each batch is
a `military/combat` Need method beside the fight, linked to its fight plan. Ordered commands,
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
identity, outside the Need's active method list.

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

An area-only work assignment (a `disaster/sheltering` move, a creepjoiner's
isolation) claims only the pawn's allowed area (`allowed-area/<pawn>`), so it is
admitted beside open RecoveryService work and other work on the pawn. Its normal
typed payload, Safeguards and action journal remain authoritative; a second move
of the same pawn's area is held, and service work is neither canceled nor replayed.

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

Essential-service coverage, bounded stress checks and development proposals in
`RoundsFindings.EssentialServices` are likewise transient full-review evidence.
They reference existing Concern-owned predicates and shared demand, never own a
second stock target, saved goal queue or progress journal. Missing/partial review
evidence cannot establish satisfaction. See [essential services](essential-services.md).

Selected practice references live role requirements through the existing Method action payload. The session journal stores the admitted participants and exact targets; requirements, XP projections and rejected alternatives remain derived in memory. The existing native bout registry owns transient selected rosters; no skill target or additional save state is written.
