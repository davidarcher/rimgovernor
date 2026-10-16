# OTEL signals

[Documentation](../../README.md)

The brain's diagnostic stream is OpenTelemetry. Every decision and event
(a row) is an OTLP log record in `logs.jsonl`; spans go to `traces.jsonl` and
numbers to `metrics.jsonl` ([OTEL core](otel-core.md), [OTEL metrics](otel-metrics.md)).
These files are always written, with or without a collector. This contract
defines the row shapes, kinds, the record mapping, the readers and the retention.

A row has a `kind`, a context (`level` `INFO`/`WARN`/`ERROR`, `at`, `tick` when
the service has observed one, `component`, and the trace and span ids when it
was emitted under a span) and a payload. Oversized payloads are replaced by
`{truncated, original_bytes, sha256, preview}` plus the correlation keys
`request`, `tool`, `native_tool`, `category`, `timing` and the decision keys
`verdict`, `reason`, `target`, `dur_ms`. [Mapping](#mapping) gives the record
attributes; `bridge.RecordFromLog` rebuilds the row from a record.

## Decision rows

One row per planner run, method selection, admission outcome and action dispatch, emitted with
`telemetry.Decide(ctx, telemetry.Decision{...})`. The payload shape is fixed:

```json
{"verdict": "refused", "reason": "critical_wave_budget", "target": "step", "dur_ms": 1.5, "attrs": {}}
```

- `verdict`: the outcome word. Vocabulary: `admitted`, `refused`, `waiting`, `applied`, `failed`,
  `skipped`, `ok`, plus a kind's own words where its table says so.
- `reason`: a stable short word. No data inside it (the data goes in `attrs`): rows collapse on it.
- `target`: what it was about (planner, concern, method, plan, pawn, zone, tool).
- `dur_ms`: milliseconds, `0` when untimed.
- `attrs`: kind-specific fields; always an object. Durations render as milliseconds, errors as text.
- No free text: a decision row has no `msg`. The row's `level` is `WARN` only for a refusal or failure a
  reader should surface.

Repeat collapsing keys on `(kind, component, verdict, reason, target)`.

The helper writes the row only; nothing is rendered to stderr.

## Kinds

Register a kind and a consuming reader before adding an emission.

### Native bridge (`RimGovernor.Brain.Host/Client`)

| Kind | Shape and fields |
|---|---|
| `native_call` | event row, one per completed call (the native client writes it after typed reply decoding). `request` (sequence of the in-flight marker, only when one was written), `tool`, `native_tool`, `arguments` (a `games_call_tool` call keeps only the inner `request` string), `ok`, `error`, `refused_text`, `reply_type`, `result` (the reply without its native `timing` block, which `timing` already carries), `timing` (native timing phases plus `proto_decode_ms`, `payload_bytes`, `wire_bytes`, `proto_decode_error`; `native_frames`, the cumulative frame account, rides at most one row per 5 s). A payload over 64 KiB is replaced by a 4 KiB `preview` and its hash. Every call writes it, except a successful quiet poll (the lifecycle and clock heartbeats, the recipe and supply lists) after its first in each 5 s: those fold into `native_poll`. The hot-poll denylist otherwise applies to HTTP rows only. The acceptance postmortems read the error and failure cases from `native_call` rows. |
| `native_request` | slow or outstanding marker only, written (durable) once a call has run past `bridge.slowCallMarker` (2 s): `tool`, `native_tool`, `arguments`. The completed `native_call` names it in `request`; a hung call leaves the marker last, and `trace` prints `(no reply recorded)` |
| `native_frame` | event row. `outcome` (`decoded`, `miss`; a cache hit writes no row and counts in `native_poll`), `native_tool`, `frame`, `why` (miss), plus decoded-frame fields. The in-process client writes the `miss` row (warn) when a frame read gives up after `FrameReadTimeout` (30 s): `native_tool` is the read, `why` the unavailable detail, `waited_ms`, `latest_frame`, `latest_writes` and `expected_writes` (the read-after-write count it waited for). Reader: the OTLP timeline |
| `native_poll` | event row, at most one per 5 s, only when something folded: `window_ms`, `tools` (per `native_tool`: `wrapper`, `calls`, `native_timed` and the summed `gate_wait_ms`, `call_ms`, `decode_ms`, `total_ms`, `response_bytes`, `proto_decode_ms`, `native_queue_ms`, `native_execute_ms` of the folded quiet-poll calls), `frame_hits` (cache hits per `native_tool`). `SummarizePhases` adds them to the tool's calls, timings and cache hits; `trace` does not show them |
| `combat_order` (decision) | `target` pawn, `verdict` `applied`/`refused`, `reason` refusal, attrs `index`, `job` |
| `clock_step` (decision) | `verdict` step outcome (`planned`, `deferred`, `failed`), `reason` `step`, `deferred` or `step_error`, `target` the step's cause (`timer`, `wake`, `settled`, `full`), `dur_ms` step wall; attrs also `gate_wait_ms`, `journal_ms`, `stop` and `stop_latency_ms` (a step woken by a native hold: hold stamp to step start), the pacing fields (`pacing_reason`, `paced_tps`, `tps_ceiling`, `effective_tps`) and `error`; attrs `reads`, `tools`, `schema_fetches`, `running` (read tally) |
| `clock_run` (decision) | one per scheduler step that changed or refused the clock, filed by `ClockRunDriver`. `verdict` `run`, `adjust`, `hold`, `refused` or `failed`, `reason` `resumed`, `watch_changed`, `speed_changed`, `coupled_order`, `autosave`, `combat_event`, the first refused reason, or `run_error`; attrs `running`, `unwatched`, `coupled_orders`, `acknowledged_holds`, `held_for` (the native hold reason), `combat`, `refused`, `error` |
| `dispatch` (decision) | one per run that acted. `target` action, `verdict` outcome (`completed`, `refused`, `failed`, `waiting`, `held`), `reason` cause or error class; attrs `attempt`, `stage`, `stage_after`, `receipt`, `refusal_class` and `refusal_reason` (native's class and reason when the run left a refused receipt), `running`, `stale`, `error`, `repeated`, `reads`, `tools`, `schema_fetches`. Level `WARN` for a failure other than a dependency wait. Written for a run that reached native (carries `receipt`, `running` and the read tally; `rimgovernor phases` counts only these) or whose outcome changed (`changed` true, `repeated` the unrecorded runs that restated the previous outcome); an unchanged run that never reached native writes nothing. `reason` is the stage word for a wait, `dependency`, `stale`, `error`, the first refusal, or `completed`; `refused` (all refusal reasons) rides in attrs. The cancelled-settle of an undispatched action is `failed`/`cancelled_settled` with `attempts` |

### Clock and scheduler (`RimGovernor.Brain.BuildingRuntime`)

Immediate protection adds `scope: immediate` to its `planner_step` rows,
`observation_tick` on the Rounder row and `decision_tick` on planner rows.
`dispatch_tick` on a Worker or synchronous combat Hands dispatch records the
journal's native dispatch tick, including uncertain replies.
`clock_step.critical_wave_ms`
includes pre-wave review. The native acceptance `ResponseEvidence` reader uses
it to separate controller activity from native outcomes and never treats an accepted dispatch as
the first native protective effect.

| Kind | Shape and fields |
|---|---|
| `planner_step` (decision) | one per planner run. `target` planner, `verdict` outcome (`admitted`, `waiting`, `refused`, `unselected`, `failed`), `reason` the refusal or wait `policy.Cause` wire value (a closed set; free-text detail is not recorded); attrs `concern` (a Concern id, or the NeedId of a family entry, which writes one row per Need; read by ExplainAudit), `subject` (optional, what the cause concerns), `proposals`, `edits`, `cause`, `admitted`, `running`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `repeated`, `error`. Rounder targets `rounds` (`ok`/`reviewed`, `ok`/`cancelled` when an authority change cancelled the round, or `failed`/`error`; attr `partial`); the worker targets `worker_step`. Step journal failures target `reasons`, `waits` or `proposals` with `failed`/`journal_error`. |
| `admission` (decision) | `target` method or plan, `verdict` `admitted`/`refused`/`held`, `reason` the refusal (a method refusal; a held building method `claim_conflict` or `method_continued`); attrs `concern`, `refused` (all reasons), `held_by`, `mode`, `work`, `combat_plan`, `hostiles`, `actions`, `error` |
| `clock_hold` | event row, one per `HoldEntered` journal event the poll commits (a native hazard, a game pause or a brain hold). `reason` (the `HoldReason`), `evidence`, `cursor`, `observed_at_unix_ms`, `benign`, `tick` and the legs `detected_tick`, `hold_ticks`, `occurrence_tick`, `detect_ticks`, `age_at_reply_ms` |
| `clock_resume` | event row, one per `Resumed` journal event. `from` (the hold reason), `speed`, `cursor` |
| `clock_stop` | event row, one per combat hold the brain resumed from while a fight was open. `event` (the armed combat event), `tick`, `resume_latency_ms` (hold to the resuming `Run`), `over_budget` and `resume_budget_ms` when the resume-latency gate tripped, `ticks_since_stop` |
| `combat_summary` | event row at combat end. `stops`, `by_event`, `resume_latency_p50_ms`, `resume_latency_p95_ms`, `resume_budget_ms`, `gate_tripped`, `ticks_between_stops_p50`, `ticks_between_stops_p95` |
| `authority` | event row. `change` (`changed`, `lost`, `retaken`), `reason`, `active`, `generation`, `previous_generation`, `cursor`; `WARN` for `lost` |
| `clock_journal` | event row. `through_cursor`, `newest_cursor` |
| `alert` | event row. `key`, `label`, `priority`, `cursor` |


### Rounds, layout and colony (`RimGovernor.Brain.BuildingRuntime/Rounds`, `ColonyPlan`, `Layout*`)

| Kind | Shape and fields |
|---|---|
| `rounds_review` | event row. `revision`, `previous_revision`, `concerns`, `emergency`, the food attrs of `roundsFoodAttrs` |
| `coupled_selection.predicted_tasks.productive_learning` (nested attrs, same decision kind) | Conditional recipe learning: `skill`, `observed_rate_work_ticks`, `conditional_work_ticks`, callback `raw_xp`, projected `before_level`/`after_level`, cumulative `applied_gain`/`decay_loss`, native `provenance`. Absent mechanics are also named in selection uncertainty. Reader: Method-correlated `production/productive-cooking` case and OTLP timeline. Native callbacks/output, not these predictions, establish progress. |
| `coupled_selection.service_investments` (nested attrs, same decision kind) | Derived food/shelter investment explanations: existing Concern `owner` and `predicate`, `horizon_ticks`, `beneficiaries`, physical `tasks`, `metric` (`food_threshold_ticks` or `stationary_thermal_degree_ticks`), conditional `avoided_exposure`, and `retirement`. No second goal or material ledger. Readers: OTLP timeline and Method-correlated `shelter/service-investment`; native completion remains separate. |
| `coupled_selection` (decision) | Normal shared portfolio comparison. Target Method ID (or `continue`), verdict `selected`/`continue`/`deferred`, reason `shared_consequences`/`best_found_bounded`/`protected_method_ownership`/`method_not_admitted`. A refused proposal records `admission_failure` after journal admission rejects it; refusal does not revoke authority or interrupt installed work. Attrs `observed_tick`, `horizon_ticks`, `selected_ids`, `baseline`, `selected`, `rejected` consequence vectors, `beneficiaries` (need and mood consequences), `protected_continuation`, `uncertainty`, `predicted_tasks`, `planned_rooms` (per planned room with a site finished inside the horizon: `room`, `completed_sites`, `complete` alternatives; #3276), `composition_expansions`, `search_expansions`, generation/search/explanation truncation. A selection whose only new work is planned sites starts no order until placement (#3278) and records target `planned_unplaced`. Vectors also expose support first-care latency, pending care batches, unsupplied demand, production/care identities, conditional room effects and tend quality; see [support evidence](support-work.md#execution-and-evidence). Rejected explanations cap at 16 to retain selection and observation keys within the telemetry payload limit; omitted explanations set `explanation_truncated`. Readers: OTLP log timeline, prepared `production/coupled-needs` and support acceptance cases. Predictions are not completion evidence. |
| `coupled_observed` (decision) | Existing Method continuation evidence. Target Method ID, verdict continue/transition after admission, or deferred with reason work_resource_conflict_or_unknown when shared resources cannot admit the productive continuation; ordinary evidence uses reason native_task_evidence; attrs `observed_tick`, `satisfied_tasks`, `remaining_tasks`, `native_states`. Readers: OTLP log timeline, Method-correlated `production/coupled-needs` and prepared support cases. Native completion and subsequent physical need changes remain separate from accepted orders. |
| `gear_search` (decision) | `verdict` `ok`, `reason` `best_found_within_budget`, `target` the pawn, level `WARN`; attr `budget_nodes` (`policy.GearSearchBudget`). One per pawn per rounds review whose loadout search spent its node budget and kept the best ensemble found. Reader: the launcher Observe tab's Problems and Log views (every WARN) and `rimgovernor log` |
| `tech_tier` | event row. `tier`, `evidence` |
| `layout_plan` (decision) | target `plan`: `verdict` `planned` (a plan derived) or `replanned` (the tiled replan changed the plan; `reason` the triggers that fired, comma-joined), attrs `colonists`, `summary`; `skipped`/`facts_unknown` (attr `fact`) or `skipped`/`no_core` when the survey holds no core; `failed`/`chain_error` (attr `error`). Target `perimeter` (#2922): one row per defense node PlanPerimeter traced, when a plan is derived or a replan changes the ground (attr `node`): `place`/`ring_traced` the wall ring, `place`/`opening_most_routes` the killbox opening where the most raider routes cross the ring (a replan keeps it, and the reason), `unplaceable`/`opening_none` no route reached a ring position that opens. Target `perimeter_cut` (#2973, advisory): rows for the routing cut `PlanPerimeterCut` advises, on a derive or a perimeter-changing replan (attr `colonists`): `place`/`cut_traced` every route but the killbox opening is closed (attrs `cut` cells, `walls` cells thickened, `unclosable`, `cost`), `unplaceable`/`cut_unclosable` the cut crosses ground nothing can wall or a source touches the base (attr `why`); then one row per segment, `place` with reason `thickness_sapper_detour`, `thickness_capped_at_budget`, `thickness_no_route_to_opening` or `thickness_hp_unknown` (attrs `segment`, `cells`, `thickness`, `detour_ticks`, `hp`). Target `defense_tiers` (#2973, advisory): one row per defense tier node `PlanDefenseTiers` cut from the defense round's census, when the cut changes (attrs `node`, `nodes`): `place`/`tier_sited` the tier is sited from the census, `place`/`tier_pinned` it is built and stays where it stands, `place`/`tier_displaced_by_built` a tier still to build gave up a cell a built tier holds, `unplaceable`/`tier_layout_failed` (attr `why`) the census holds no corridor (only built tiers stand as nodes). Target `defense_tier_block` (#2973, advisory): one row per pinned tier a tree, chunk or new building blocks, when the price changes (attrs `tier`, `blocked`, `clear_w`, `clear_known`, `move_w`, `move_known`, `downtime` `unpriced`): `clear`/`tier_clear_cheaper` removing the blocker (work from the defs at the work-day price, net of yield) costs no more than moving, `move`/`tier_move_cheaper_downtime_unpriced` moving is cheaper (a lower bound: the downtime is not priced), `unpriced`/`tier_block_unpriced` neither could be priced. |
| `layout_interior` (decision) | the interior search (`SearchInterior`) of a derive or replan, component `layout`, target `interior`: one row per kept move, `verdict` `place`, `reason` `interior_search_move`; attrs `group`, `move` (slot:kind:terms improved). Written by `logInterior` after the `layout_stations` row (derive) or the `layout_replan_tiled` row (replan). |
| `layout_edit` (decision) | `target` zone, role, item or building, `verdict` `admitted`/`refused`/`abandoned`/`closed`/`proposed`, `reason` refusal code or detail; attrs `family` (`stockpile`, `field`, `building`), `kind`, `role` (stockpile create/delete),  `plan`, `crop`, `cells`, `moves`, `gain`, `owner`, `x`, `z` |
| `layout_regroup` (decision) | the regrouping search (`policy.Regroup`, #2878), component `layout`, target `groups`, written with each `layout_stations` run (skipped, and no rows, while its inputs are unchanged). One row per move applied: `verdict` `applied`, `reason` `regroup_merge_total_falls`, `regroup_split_conflict_priced`, `regroup_split_role_guard`, `regroup_move_need_total_falls` or `regroup_filth_merge_walk_beats_poison`; attrs `move` (`merge`/`split`/`move`), `a`, `b` (the nodes of the pair rule), `before`, `after` (the priced total in score points, lower is better), `walk`, `poison` and `clean` (the move's change of the embedding score, the food-poisoning price it adds and the change of the cleaning-labor price, in the same points; `clean` is zero for a filth merge because both groupings pay the same cleaning labor, #2993) and `lost_effects`. A butcher kept in its closet because the poisoning outweighs the walk saved is a `kept` row, `regroup_filth_merge_poison_beats_walk`, with the same attrs. With no move applied one row: `kept`/`regroup_no_move_improves`, or `skipped`/`regroup_work_price_unknown` while the work-day price is unknown. Reader: `rimgovernor log` |
| `layout_replan_tiled` (decision) | the replan (`Rounder.replanLayout`, `policy.ReplanLayout`, #3005, J4 of #2965): the tiled plan is the next plan. Component `layout`, target `plan`, one row per replan, `dur_ms` the whole run (`ReplanTileFacts`, the chain and the tiler; it runs in the review). `verdict` `replanned`/`reason` `tiled_plan`: attrs `changed` (the plan was recorded), `same`, `moved`, `new`, `dropped` (room counts of the tiled plan against the plan the colony held), `rooms` (each `room` the old plan's id, 0 for a new one, `tiled` the tiled plan's id, `role`, `verdict`, `reason`, `built`, and `short` when the room's group needs more floor than its standing rooms give, #3004), `unmatched_built` (each standing room the chain has no node to pin: `room`, `role`, `reason` `tile_outdoor`, `tile_no_native_role` (both skipped by `BuiltRegions` unless the room is a room node's, #3003), `tile_not_a_block` or `tile_no_node`), `built` (standing rooms) and `tiled_rooms`. A room `reason` is `tile_kept_built` (same), `tile_successor` (moved), `tile_sited_again` (an unbuilt room matched by role), `tile_unpinned_built` (an unmatched standing room the tiler builds its own room of), `tile_superseded` (dropped, its standing successor is the room), `tile_built_retired_by_old` (new: a tiled block continues a standing or replaced room the plan no longer holds), `tile_no_old_room` (new) or an unmatched reason (dropped). `skipped`: `station_advice_unavailable` (the review's chain made no advice) or `facts_unknown` (attr `fact`). `failed`/`tile_error`, `WARN`, attr `error` (the chain or the tiler could not plan; the colony keeps its plan and the next trigger tries again). Reader: `rimgovernor log` |
| `layout_role_guard` (decision) | `verdict` `mismatch`, `reason` `native_role_differs_from_predicted`, `target` the planned room id; attrs `native` (the census role), `predicted` (PredictRole of the room's observed stations), `scores` (role to score). One row per standing planned room whose native role differs, on each rounds building step that reads the plan, rooms, construction and sleeping census; a room with an unknown bed state or an ideo altar is not compared. The regrouping reads the rows as a cost input and a split move (`layout_regroup`). A guard error is `failed`/`guard_error`, `WARN`, attr `error`. |
| `layout_stations` (decision) | the station chain, one row per run at a layout review (`stationAdvice`; skipped, and no row, while its inputs are unchanged), component `layout`, target `plan`, `dur_ms` the chain's wall time. `verdict` `advised`/`reason` `station_chain`: attrs `nodes`, `edges`, `groups` (each `group`, `role` predicted, `stations` {node, count}, `requires`, `footprint`, `clearance`, `area`, `cost_ticks_per_day`, `lost_effects`, `rooms` matched built room ids, `built_area`, `outgrown`, `climate` {cells, max_c, best, options {opening, mean_w, peak_w, heating_w, cooling_w, build_w, total_w}} on a cold group when the curve is known, `block` when placed), `conflicts` ({a, b, conflict}), `embed` ({level, placed, issues, built {group, room, verdict `keep`/`move`, reason `embed_built_*`}, cells, total, soil, edge, centre, walk, unmet_links, switch, seed}), `sites` (each power site `node`, `x`, `z`, `anchored`, and for a turbine `rotation`, `blocked` wind-path cells, `output` the fraction of rated output left), `site_issues` ({node, verdict, reason}), `vents` (the tiled plan's vent sites {x, z, rotation, reason}) and `vent_issues` ({group, verdict, reason} for a `tile_vent*` decision that placed none) and `comfort` (the advised comfort tier of the sleep node and each worked bench, {node, def, stuff, comfort, priced, gain, benefit_w, cost_w}; empty while the mood or comfort model or the colonists' mood is unknown, `priced` false for a node that could not be priced). attr `seed_only` names the unread fact that left a derive-time graph at the seed rooms (empty otherwise). `skipped`/`facts_unknown`: attr `fact` names the unread fact. `failed`/`chain_error`, `WARN`, attr `error`. A derive writes the same row with attr `derive` true; `embed` also carries `fields` (each field's placed rectangle). The replan tiles the facts this review stored (`layout_replan_tiled`). Reader: `rimgovernor log` |
| `snapshot_skip` | decision, `WARN`. `target` the snapshot (`colony`, `combat`, `defense`, `layout`, `firebreak`, `shelter`), `verdict` `skipped`, `reason` `not_recorded`; attrs `error`, `tick` |
| `routine_skip` (decision) | `target` the subject, `verdict` `skipped`/`waiting`, `reason`; attrs `quest`, `script`, `detail`, `findings` |
| `placement_refused`, `foreign_held` | event rows, component `building-planner` (`RoundsRoomReconcile.cs`). `msg` only: `furniture cell refused: <def>@<x>,<z> blocked by <thing, ...>` (`ring cell refused:` for a wall or door cell) (the native preview's blockers, `unreported` when it names none) and `foreign thing held: <def>@<x>,<z>:<reason>` (`casket`, `ancient_danger`, `not_deconstructible`, `forbidden_item`: a forbidden haulable stack on a wanted door cell, named but not blocking). The same text is the room's wait key (`<room>_reconcile:blocked:...`, `<room>_reconcile:held:...`). Read by the shelter climate cases (`ServiceLogs.LogMessages`) |

### Defense, population and pawns

| Kind | Shape and fields |
|---|---|
| `defense_action` (decision) | `target` pawn, plan, incident, cell or tier, `verdict` `applied`/`refused`/`failed`/`waiting`, `reason` the refusal or issue; attrs `incident`, `plan`, `outcome`, `x`, `z`, `error`. An unbuilt tier emits `waiting` / `tier_unbuilt` when its census changes, targeting the tier with attrs `buildings`, `census`. A turret, mortar or IED tier cut short by a gate emits `refused` with the gate as `reason` (`turret_unavailable`, `turret_threat_budget`, `turret_power`, `turret_stock`, `turret_no_conduit_route`, `mortar_*`, `ied_*`; see [defense tier counts](spatial-contracts.md#defense-tier-counts)), targeting the tier. |

### Other event families

| Kind | Shape and fields |
|---|---|
| `http_access` | event row, one per HTTP request. `method`, `path`, `status`, `dur_ms`, `bytes`, `long_lived`; 2xx GETs of `/api/state`, `/api/routines`, `/api/health` and `/api/presentation/*` under 250 ms are dropped; errors, slow calls and every non-GET are kept. Pprof rows are written at finish with `long_lived: true` (`dur_ms` is the capture, not latency). Written by `httpapi.Config.Access` (the brain) or `HttpChannelServer` (C#, [HTTP channel](../architecture/http-channel.md)); component `httpapi`; level `WARN` for a 5xx |
| `fields_select` | decision, one per farm site-type selection (`RoundsFieldPlanner`). `target` the winning crop, `verdict` `selected`, `reason` the winning site kind (`outdoor`, `greenhouse-new`, `greenhouse-reuse`, `hydroponics`); attrs `cells`, `buildings`, `needed`, `urgent`, `candidates` (each `kind`, `crop`, `needed`, `cells`, `score`, `terms` {name: value}, `reason` for an unplantable one). Read by the `Farm` acceptance cases. Component `clock-scheduler` |
| `recovery_batch` | decision, one per home-upkeep family read with queued recovery removals (`HomeUpkeepReader`); it journals the batch the planner executes. `target` `clearance`, `verdict` `planned`/`held`/`failed`, `reason` the stage (`roof`, `removal`, `none`, or `roof_rules` for a failed row); attrs `targets`, `roof_cells`, `batch` (load ids), `held` (each `id`, `reason` a `RemoteHoldReason` word), `blocker` (the first joint roof-check word), `error`. Component `clock-scheduler` |
| `food_credit` | decision, one per ledger counter group when the credit factor moves by 0.1 or more or the group's state changes. `target` the group (`crop:<zone>`, `fish:<x,z>`, `forage:<def>`, `animal_product:<race>`), `verdict` `credited`, `reason` `window` (the trailing window moved it) or `rebaseline` (a load held it); attrs `expected`, `observed` (nutrition over the window), `factor`, `window_days`, `state` (`designated`, `delivering`). Component `routine`. Written by the food review (`RoundsFoodPlan.cs`); `logview` prints the factor and state |
| `mod_log` | event row for each `rimgovernor.log` event from the mod. Row context: `component` `mod`, the mod's `tick`, `at` (the mod's time, so a replayed line keeps it) and `trace_id`/`span_id` from the event's `trace` (a W3C `traceparent`, or the older `<trace_id>/<span_id>`: the brain span a main-thread hop carried); `level` `WARN`/`ERROR` for mod `warn`/`error`. Payload `seq` (mod ring order), `level`, `source` (the mod component, e.g. `watchdog`), `msg`, and when set `late` (written while nothing was subscribed, replayed on subscribe) and `suppressed` (lines the call site's rate limit dropped before this one). The C# helper is `RimGovernor.Host.Sdk.ModLog`: bounded ring of 512, 5 lines per call site per 10 s, reentrant writes dropped, never `Log.Error`, never blocks the game thread |
| `build_stalled` | decision, level `WARN`, component `building-planner` (`RoundsRoomStall.cs`): a room whose blueprints or frames have stood unfinished for `roomBuildStallTicks` (30000), repeated every window while it lasts. `target` the room, `verdict` `waiting`, `reason` the most common census reason (`awaiting_materials`, `no_eligible_builder`, a native `FinishingBlocker` word, `unreported`); attrs `blueprints`, `frames`, `reasons` {reason: count}, `tiers` {tier: count}, `eligible_finishers`, `max_min_skill`, `stalled_ticks`, `sites` (up to 8 `<def>@<x>,<z>`), `obstructions` (`<op>:<def>(<count>)@<x>,<z>` for pending claim, cut and haul targets; with no site standing `reason` is `obstructed`). The room wait key meanwhile reads `<room>_reconcile:obstructed:<first obstruction>` or `<room>_reconcile:awaiting_build:<n>_blueprints,<n>_frames:<reason>`. Reader: `rimgovernor log` (`logview.Entry.Words`) and the launcher Log panel (WARN) |
| `telemetry_drop` | event row, level `WARN`, component `otel`: the OTEL pipeline lost items (at most one per pipeline per 10 s). `signal` (`trace`/`log`/`metric`), `sink` (`file`/`collector`), `reason` (`queue_full`/`export_failed`), `dropped_since_last_warning`. The cumulative count is the `rimgovernor.telemetry.dropped` metric ([OTEL core](otel-core.md)). Reader: the launcher Observe tab's Problems and Log views (every WARN) and `rimgovernor log` |
| `log_overflow` | event row, level `WARN`: one summary of dropped diagnostics. `dropped`, `side` `mod` (the mod's ring overflowed while nothing was subscribed or the publisher lagged; `seq` is where the lines were) or `controller` (the reader goroutine's hand-off queue of 1024 was full) |

### Session and reader events

| Kind | Notes |
|---|---|
| `state_reset` | event row, level `WARN`, component `brain-host`: the state database came from another schema version and was moved aside (`path`, `aside`). Written once at startup. |

The OTLP log ring is the only log: the telemetry handler writes a row for a record that names a `kind`
and drops one that does not, there is no Debug level, and `telemetry.Event` takes the `kind` as a required
parameter so code logs through it, not `slog` directly. stderr carries only the startup banner, parse errors,
`the brain service:` failures, the clock fault-injection notice and the brain panics. A new emission adds its kind here with
a reader, or it is not added.

## Mapping

Every decision and event row is an OTEL log record in `logs.jsonl` and,
when the emitter runs under a recording span, an event on that span. The code is
`TelemetryEmitter` (`Telemetry/TelemetryEmitter.cs`): a `Decision`, every
host log call with a `kind`, and the direct emitters (`mod_log`, `log_overflow`,
`http_access`, `ReadTally.Publish`) all reach it, so the kinds registry and the
consuming-reader rule above apply unchanged: a kind is the record's `eventName`.
The native client's `native_call`, `native_request`, `native_frame` and `native_poll`
rows are mapped too, as ordinary event records: the
acceptance oracles read reply bodies (clock status, `failure.code`, startup and
speedmatrix replies) that only these rows carry, so they are rows, not derived
from spans or metrics. `native_request` is durable (fsynced) and its `request`
attribute is the id the answering `native_call` repeats (a per-process counter).
Recording is on for the host (`ProcessConfig.RecordRows`) and off for a
bare client.

This attribute schema is what log readers parse. Changing it is a contract change.

| OTEL field | Content |
| --- | --- |
| `eventName` | the row kind |
| `timeUnixNano` | the row's `at` |
| `severityNumber` / `severityText` | `INFO` 9, `WARN` 13, `ERROR` 17, from the row's `level` |
| `body` | a decision: `"<kind> <verdict> <reason> <target>"` (derived, never parse it); an event: its `msg`; absent when an event has none |
| `traceId`, `spanId` | the span the emitter ran under (`mod_log`: the brain span the mod echoed) |
| `rimgovernor.kind`, `rimgovernor.component` | strings, as the row context |
| `rimgovernor.tick` | int, the last observed game tick; absent before one was observed |
| `rimgovernor.row` | `decision` or `event` |
| `rimgovernor.verdict`, `.reason`, `.target` | strings, decisions only, always present (possibly `""`) |
| `rimgovernor.dur_ms` | double, decisions only, `0` when untimed |
| `rimgovernor.attr.<key>` | one attribute per decision `attrs` key, or per event payload key except `msg`. Native types: string, bool, int, double (durations in ms); slices and maps as OTLP `arrayValue` and `kvlistValue`; errors as text. Absent for a null value |
| `rimgovernor.truncated`, `.original_bytes`, `.sha256`, `.preview` | payload cap: when the encoded fields exceed 64 KiB (`telemetry.PayloadCapBytes`) they are dropped, the record carries these four (a 4 KiB preview) and only the small correlation fields `request`, `tool`, `native_tool`, `category`, `timing`. The fixed decision attributes always survive |

A span event is named after the kind and carries the same attributes. Logs are
emitted whether or not the trace is sampled, so a decision stays searchable when
its span is dropped.

Vocabulary, the no-free-text rule for decisions and the collapse key
`(kind, component, verdict, reason, target)` are unchanged; collapsing stays a
reader operation, now over the `rimgovernor.*` attributes.

## Readers

The launcher's Observe tab (`tools/launcher/Observe.cs`, which tails `logs.jsonl`)
and acceptance diagnostics consume these kinds; `RimGovernor.cmd launch-phases`
reads the launch spans (see [Launch spans](#launch-spans)).
`bridge.IsWorkerStep` and `bridge.StepFields` centralize
clock/worker interpretation. Combat `clock_stop` rows carry `event`; `clock_hold` rows
carry the journal's hold. Acceptance readers use the case's OTLP log ring
(`<output>/otlp/logs.jsonl*`, `<output>/service-N/otlp/...` per relaunch):
`OtlpTimeline.Read` rebuilds the row shape these
helpers expect from the typed log reader (`OtlpReader.ReadLogs`, see
[OTEL core](otel-core.md)); `ServiceLogs.ReadLogs`, `LogTail` and
`LogMessages` sit on top. A null attribute is absent in a record, not present
as nil. Postmortem evidence is `otlp/logs.jsonl.2#77`: the segment and its
1-based line (what `grep -n` prints).

## Retention

The host writes the three files into `RIMGOVERNOR_OTLP_DIR` (default
`<save-data>/RimGovernor/otlp`; the acceptance runner passes `<output>/otlp`
and `<output>/service-N/otlp` per relaunch). Each signal is its own rotating
ring: `traces.jsonl` 16 MiB x 8, `logs.jsonl` 32 MiB x 16 (512 MiB),
`metrics.jsonl` 8 MiB x 4. When the active file would pass its segment size it
becomes `name.1`, older segments shift up and the one beyond the count is
deleted; retention is by count only. Under overload the sink drops items
and counts them (`telemetry_drop` row, `rimgovernor.telemetry.dropped`
metric) rather than blocking the brain. The acceptance and postmortem readers use them.
[OTEL core](otel-core.md) has the file layout and line format.

Add a kind only with its reader. Keep stable reason words and structured attrs
so filtering, repeat collapse and diagnostics do not depend on prose parsing.

## Launch spans

Every in-process run writes the game-side launch waterfall to `traces.jsonl`,
regardless of DevMode. The span listener exists only once the brain starts, so
the host mod and the bridge buffer each phase as plain data
(`RimGovernor.Host.Sdk.LaunchPhases`: id, parent id, name, UTC start and end,
262144 phases at most, grown on demand). `BrainBootstrap` replays them through
`LaunchSpans.Replay` (`Telemetry/LaunchSpans.cs`) right after
`BrainHost.StartAsync`, with the recorded start and end times. Scope
`.../launch`.

```text
launch                              root; earliest phase start → brain started
  launch.pre_mods                   process start → host mod constructor (inferred: nothing can be patched earlier)
  rimworld/<DeepProfiler label>     a RimWorld load stage, e.g. rimworld/LoadModXML(), rimworld/Resolve references.,
    rimworld/<label>                rimworld/Static constructor calls; one nesting level below it
  mod-constructor.total             the host mod constructor
    main-thread.initialize, harmony.startup-patches, startup.on-mod-constructed
  bridge-start.total                bridge services after play data loaded
    capabilities.initialize, logs.initialize, application.run-in-background, debug.playback-defaults
  brain.defs_wait                   brain bootstrap waiting for loaded Defs
  brain.routes_wait                 brain bootstrap waiting for the HTTP channel's routes
  brain.host_start                  BrainHost.StartAsync; ends when the root ends
```

RimWorld stages come from a Harmony prefix on `Verse.DeepProfiler.Start`/`End`
installed in the mod constructor, so stages already open then (`LoadAllPlayData`,
`Load all active mods.`, `CreateModClasses()`) are not seen. Only the first two
levels of the stages opened afterwards on non-pool threads are recorded; the
prefixes are removed once play data has loaded. RimWorld's per-def and per-patch
stages (`Register*WantsCrossRef*`, `<PatchOperation type> Worker`, and queued
def-hook lambdas such as `Verse.ThingDef -> Void <PostLoad>b__…()`; thousands
per boot) are skipped with everything nested in them, by label pattern, so a new
coarse stage still appears. Spans may overlap (host phases
run inside RimWorld stages). A phase whose parent was not recorded sits under
the root. Phases past the buffer are dropped and the count is logged as a
warning.

The acceptance harness and the launcher time their side in their own process
(`RimGovernor.Brain.Acceptance.LaunchTrace`) and post it to the brain over
`POST /api/telemetry/spans` ([player API](player-api.md)), which replays each
post as another `launch` tree in the same ring; every span carries
`rimgovernor.launch.source` (`harness` or `launcher`). The harness posts once at
the end of a session open (`Session.OpenAsync`, also when it fails), the
launcher after Play connects (after the load when Play loads a save) and after a
New colony generation.

```text
launch.prepare          harness: profile prepare and package hashes
launch.process_start    harness, launcher: the detached game launch (not run for a kept game)
launch.connect          harness: endpoint, /api/health and a first native read; launcher: endpoint health
launch.to_main_menu     harness: a reused game unloaded to the main menu; launcher: New colony on the running game
start.load              harness: a save or cached-start load; launcher: Play's save load
start.generate          harness: the new-colony op of a debug, lab or scenario start; launcher: New colony
start.first_action      harness: started world → the case body can act (pause, quiet world, fixture op, frozen needs)
```

Acceptance `result.json` carries `boot_phases`, name → ms, read at Finalize
from the game's `traces.jsonl` (`BootPhases.Read`): every non-root span of each
`launch` tree whose root starts at or after the report's `started_at`, so a kept
game's earlier boot is not counted. A repeated name sums. Phases overlap, so the
map does not add up to `boot_ms`. A ring that cannot be read leaves
`boot_phases_error`; spans the game did not take leave `launch_spans_error`.

`RimGovernor.cmd launch-phases [-json] [<run> [<run>]]` (`tools/rg`, model
`LaunchWaterfall`) prints one run's trees as a waterfall: one line per span,
each tree depth first with siblings in start order, with its offset from the
game's own `launch` root (process start; the harness's and launcher's earlier
phases are negative), its duration and a bar, then the total and the three
largest phases. A run is an OTLP directory or its `traces.jsonl` (every tree of
the latest root's run id, `service.instance.id`), or an acceptance case output,
its `result.json` or an output directory holding one (the trees rooted between
`started_at` and `finished_at`, read from `launch_dir`, the game's channel
directory, which Finalize records beside `boot_phases`). With no run it
reads the newer of the launcher's latest game (`.rimgovernor/bridge/config/<game>/`)
and the latest `.rimgovernor/ev*` acceptance output. A second run prints the
per-phase delta (phases summed by name as in `boot_phases`, the total first,
then by largest change). `-json` prints the same as JSON.

## Pawn activity

The [pawn activity contract](../architecture/pawn-activity.md) owns these event kinds:

| Kind | Payload | Reader |
| --- | --- | --- |
| pawn_activity_execution | pawn, assignment, method, revision, step, state, reason, job_id; authorization/offer/start/termination are distinct | OTLP timeline; pawn observations ActivityExecution; control/pawn-activities |
| pawn_activity_interval | sequence, world (colony/load/map), pawn, start_tick, end_tick, job, job_id, phase, activity, purpose, targets, start_location, end_location, end_reason, nullable method/assignment | RimGovernor.cmd pawnactivity; observations/pawn-activity |
| pawn_activity_output | colony, load, nullable map/pawn/job_id, source, output_kind, resource, units, tick; observed production-site output, not job success or storage delivery | OTLP timeline; observations/pawn-activity |
| pawn_activity_fault | error; subsequent observations stop and report as missing time | OTLP timeline; pawnactivity gaps |
| pawn_activity_capacity | refused_observations; open interval cap reached | OTLP timeline; pawnactivity gaps |

These events use the existing bounded telemetry queue and rotating logs. Interval
boundaries are non-overlapping and captured by native transition hooks; there is no periodic phase sampling. Open intervals export only when changed or closed. Lost or rotated records are unknown time, not zero work.

The existing `coupled_selection` vectors also carry `maintenance` rows: actual requirement identity, conditional native XP shortfall and findings. The OTLP timeline reads these attrs; native `production/maintenance-range` and `production/maintenance-sparring` correlate the selected Method with real skills and restoration.
