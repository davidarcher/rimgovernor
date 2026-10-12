# OTEL signals

[Documentation](../../README.md)

The controller's diagnostic stream is OpenTelemetry. Every decision and event
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
{"verdict": "refused", "reason": "critical_wave_budget", "target": "window", "dur_ms": 1.5, "attrs": {}}
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

### Native bridge (`internal/bridge`)

| Kind | Shape and fields |
|---|---|
| `native_call` | event row, one per completed call (`Client.core` writes it after typed reply decoding). `request` (sequence of the in-flight marker, only when one was written), `tool`, `native_tool`, `arguments` (a `games_call_tool` call keeps only the inner `request` string), `ok`, `error`, `refused_text`, `reply_type`, `result` (the reply without its native `timing` block, which `timing` already carries), `timing` (native timing phases plus `proto_decode_ms`, `payload_bytes`, `wire_bytes`, `proto_decode_error`; `native_frames`, the cumulative frame account, rides at most one row per 5 s). A payload over 64 KiB is replaced by a 4 KiB `preview` and its hash. Every call writes it, except a successful quiet poll (`quietPolls` in `bridge/pollrollup.go`: the lifecycle and clock heartbeats, the recipe and supply lists) after its first in each 5 s: those fold into `native_poll`. The hot-poll denylist otherwise applies to HTTP rows only. `nativeaccept/postmortem` reads the error and failure cases from `native_call` rows. |
| `native_request` | slow or outstanding marker only, written (durable) once a call has run past `bridge.slowCallMarker` (2 s): `tool`, `native_tool`, `arguments`. The completed `native_call` names it in `request`; a hung call leaves the marker last, and `trace` prints `(no reply recorded)` |
| `native_frame` | event row. `outcome` (`decoded`, `miss`; a cache hit writes no row and counts in `native_poll`), `native_tool`, `frame`, `why` (miss), plus decoded-frame fields |
| `native_poll` | event row, at most one per 5 s, only when something folded: `window_ms`, `tools` (per `native_tool`: `wrapper`, `calls`, `native_timed` and the summed `gate_wait_ms`, `call_ms`, `decode_ms`, `total_ms`, `response_bytes`, `proto_decode_ms`, `native_queue_ms`, `native_execute_ms` of the folded quiet-poll calls), `frame_hits` (cache hits per `native_tool`). `SummarizePhases` adds them to the tool's calls, timings and cache hits; `trace` does not show them |
| `combat_order` (decision) | `target` pawn, `verdict` `applied`/`refused`, `reason` refusal, attrs `index`, `job` |
| `clock_step` (decision) | `verdict` step outcome (`admitted`, `deferred`, `refused`, `idle`, `failed`), `reason` the first window refusal or a stable word (`window_admitted`, `window_refused`, `deferred`, `no_window`, `step_error`), `target` the step's cause (`timer`, `wake`, `settled`, `full`, `live`), `dur_ms` step wall; attrs also `gate_wait_ms`, `journal_ms`, `window_ticks`, `stop`, `stop_latency_ms`, the pacing fields and `error`; attrs `reads`, `tools`, `schema_fetches`, `running`, `stop_pause_s` (read tally) |
| `dispatch` (decision) | one per run that acted. `target` action, `verdict` outcome (`completed`, `refused`, `failed`, `waiting`, `held`), `reason` cause or error class; attrs `attempt`, `stage`, `stage_after`, `receipt`, `refusal_class` and `refusal_reason` (native's class and reason when the run left a refused receipt), `running`, `stale`, `error`, `repeated`, `reads`, `tools`, `schema_fetches`. Level `WARN` for a failure other than a dependency wait. Written for a run that reached native (carries `receipt`, `running` and the read tally; `rimgovernor phases` counts only these) or whose outcome changed (`changed` true, `repeated` the unrecorded runs that restated the previous outcome); an unchanged run that never reached native writes nothing. `reason` is the stage word for a wait, `dependency`, `stale`, `error`, the first refusal, or `completed`; `refused` (all refusal reasons) rides in attrs. The cancelled-settle of an undispatched action is `failed`/`cancelled_settled` with `attempts` |

### Clock and scheduler (`internal/buildingruntime`)

Immediate protection adds `scope: immediate` to its `planner_step` rows,
`observation_tick` on the Rounder row and `decision_tick` on planner rows.
`dispatch_tick` on a Worker or synchronous combat Hands dispatch records the
journal's native dispatch tick, including uncertain replies.
`clock_step.controller_pause_ms` records the monotonic scheduler interval from
an observed paused clock through return; it excludes unobserved pause intervals.
`clock_step.critical_wave_ms`
includes pre-wave review. The native acceptance `ResponseEvidence` reader uses
these fields alongside `stop_pause_s`; it never treats an accepted dispatch as
the first native protective effect.

| Kind | Shape and fields |
|---|---|
| `planner_step` (decision) | one per planner run. `target` planner, `verdict` outcome (`admitted`, `waiting`, `refused`, `unselected`, `failed`), `reason` the refusal or wait `policy.Cause` wire value (a closed set; free-text detail is not recorded); attrs `subject` (optional, what the cause concerns), `proposals`, `edits`, `cause`, `admitted`, `running`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `window_ticks`, `repeated`, `error`. Rounder targets `rounds` (`ok`/`reviewed`, `ok`/`cancelled` when an authority change cancelled the round, or `failed`/`error`; attr `partial`); the worker targets `worker_step`. Step journal failures target `reasons`, `waits` or `proposals` with `failed`/`journal_error`. |
| `admission` (decision) | `target` method, window or plan, `verdict` `admitted`/`refused`/`held`, `reason` the refusal (`critical_wave_budget`, a `ClockWindowReason`, a method refusal) or, for `admitted` with target `window`, `latched_hold_escape` (the hold bound let a review through while the Worker still owed work; `WARN`, once per action); attrs `concern`, `refused` (all reasons), `held_by`, `mode`, `work`, `combat_plan`, `hostiles`, `clock_state`, `window_ticks`, `wall_budget_ms`, `actions`, `hold_max`, `error` |
| `clock_stop` | event row. `reason`, `evidence`, `cursor`, `observed_at_unix_ms`, `benign`, the stop legs, and for a combat stop `event`, `resume_latency_ms`, `ticks_since_stop` |
| `combat_summary` | event row at combat end. `stops`, `by_event`, `resume_latency_p50_ms`, `resume_latency_p95_ms`, `ticks_between_stops_p50`, `ticks_between_stops_p95` |
| `authority` | event row. `change` (`changed`, `lost`, `retaken`), `reason`, `active`, `generation`, `previous_generation`, `cursor`, `paused`, `pace`, `stop_reason`; `WARN` for `lost` |
| `clock_journal` | event row. `through_cursor`, `newest_cursor` |
| `alert` | event row. `key`, `label`, `priority`, `cursor` |
| `pace` | decision. `target` `backoff`, `reason` the `why`; attrs `ceiling`, `horizon_ticks`, `error` |
| `idle_stall` | decision, `WARN`. `reason` `no_work`; attrs `tick`, `refusals`, `lend_ticks`, `standing` |


### Rounds, layout and colony (`internal/buildingruntime/rounds*`, `colony_plan`, `layout_*`)

| Kind | Shape and fields |
|---|---|
| `rounds_review` | event row. `revision`, `previous_revision`, `concerns`, `emergency`, the stage and food attrs of `roundsStageAttrs` and `roundsFoodAttrs` |
| `gear_search` (decision) | `verdict` `ok`, `reason` `best_found_within_budget`, `target` the pawn, level `WARN`; attr `budget_nodes` (`policy.GearSearchBudget`). One per pawn per rounds review whose loadout search spent its node budget and kept the best ensemble found. Reader: the Grafana Problems and Log dashboards (every WARN) and `rimgovernor log` |
| `colony_stage` | event row. `stage`, `since`, `blocker`, `reason`, `held` |
| `tech_tier` | event row. `tier`, `evidence` |
| `layout_plan` (decision) | `verdict` `planned`/`replanned`/`claimed`/`refused` (`reason` `no_room`: a replan left rooms unplaced, attr `unplaced`, once per distinct set), `reason` replan reason, attrs `colonists`, `summary`, `claims`, and on a replan `tomb_short`; `skipped`/`no_core` when the survey holds no core; a suite-claims change is `claimed` with target `suites`. Target `successor` (#2863): `planned`/`successor_planned` a larger room was sited to replace an outgrown one (attrs `role`, `room`, `replaces`, `floor`, `old_floor`, `shortfall`), `retired`/`successor_retired` the replaced room left the plan once its successor stood (attrs `role`, `room` the successor, `replaces`, `floor`), `refused`/`successor_unsited` the successor could not be sited, nothing is built (attrs `role`, `replaces`, `floor`, `shortfall`, `why`), once per distinct set. Target `perimeter` (#2922): one row per defense node PlanPerimeter traced, when a plan is derived or a replan changes the ground (attr `node`): `place`/`ring_traced` the wall ring, `place`/`opening_most_routes` the killbox opening where the most raider routes cross the ring (a replan keeps it, and the reason), `unplaceable`/`opening_none` no route reached a ring position that opens. Target `perimeter_cut` (#2973, advisory): rows for the routing cut `PlanPerimeterCut` advises, on a derive or a perimeter-changing replan (attr `colonists`): `place`/`cut_traced` every route but the killbox opening is closed (attrs `cut` cells, `walls` cells thickened, `unclosable`, `cost`), `unplaceable`/`cut_unclosable` the cut crosses ground nothing can wall or a source touches the base (attr `why`); then one row per segment, `place` with reason `thickness_sapper_detour`, `thickness_capped_at_budget`, `thickness_no_route_to_opening` or `thickness_hp_unknown` (attrs `segment`, `cells`, `thickness`, `detour_ticks`, `hp`). Target `defense_tiers` (#2973, advisory): one row per defense tier node `PlanDefenseTiers` cut from the defense round's census, when the cut changes (attrs `node`, `nodes`): `place`/`tier_sited` the tier is sited from the census, `place`/`tier_pinned` it is built and stays where it stands, `place`/`tier_displaced_by_built` a tier still to build gave up a cell a built tier holds, `unplaceable`/`tier_layout_failed` (attr `why`) the census holds no corridor (only built tiers stand as nodes). Target `defense_tier_block` (#2973, advisory): one row per pinned tier a tree, chunk or new building blocks, when the price changes (attrs `tier`, `blocked`, `clear_w`, `clear_known`, `move_w`, `move_known`, `downtime` `unpriced`): `clear`/`tier_clear_cheaper` removing the blocker (work from the defs at the work-day price, net of yield) costs no more than moving, `move`/`tier_move_cheaper_downtime_unpriced` moving is cheaper (a lower bound: the downtime is not priced), `unpriced`/`tier_block_unpriced` neither could be priced. `planned`/`successor_relocated` (#2865): a built room was replaced by a sited one for another reason than an outgrown group (attrs `role`, `room`, `replaces`, `floor`, `why` `bad_ground` or `score_gain`); `reshaped`/`room_reshaped` (target `plan`): a built room changed region in place (attrs `role`, `room`, `floor`, `added`, `removed`, `walls_added`, `walls_removed`). |
| `layout_edit` (decision) | `target` zone, role, item or building, `verdict` `admitted`/`refused`/`abandoned`/`closed`/`proposed`, `reason` refusal code or detail; attrs `family` (`stockpile`, `field`, `building`), `kind`, `role` (stockpile create/delete),  `plan`, `crop`, `cells`, `moves`, `gain`, `owner`, `x`, `z` |
| `eat_trigger` (decision) | one per eat trigger setting in an admitted work plan (`rounds_assignments.go`). `target` the pawn, `verdict` `admitted`, `reason` `lowered` (below the vanilla 0.30) or `vanilla`; attrs `trigger` (the planned level), `food_days` (the runway when planned), `days_saved` (`policy.EatTriggerDaysSaved`: the days the lowered trigger adds to that runway, 0 at vanilla; about 6-7 for 57 meals). Component `routine-work`. Reader: `rimgovernor log` |
| `layout_regroup` (decision) | the regrouping search (`policy.Regroup`, #2878), component `layout`, target `groups`, written with each `layout_stations` run (skipped, and no rows, while its inputs are unchanged). One row per move applied: `verdict` `applied`, `reason` `regroup_merge_total_falls`, `regroup_split_conflict_priced`, `regroup_split_role_guard`, `regroup_move_need_total_falls` or `regroup_filth_merge_walk_beats_poison`; attrs `move` (`merge`/`split`/`move`), `a`, `b` (the nodes of the pair rule), `before`, `after` (the priced total in score points, lower is better), `walk`, `poison` and `clean` (the move's change of the embedding score, the food-poisoning price it adds and the change of the cleaning-labor price, in the same points; `clean` is zero for a filth merge because both groupings pay the same cleaning labor, #2993) and `lost_effects`. A butcher kept in its closet because the poisoning outweighs the walk saved is a `kept` row, `regroup_filth_merge_poison_beats_walk`, with the same attrs. With no move applied one row: `kept`/`regroup_no_move_improves`, or `skipped`/`regroup_work_price_unknown` while the work-day price is unknown. Reader: `rimgovernor log` |
| `layout_replan_tiled` (decision) | the tiler run beside a replan (`tiledReplan`, #3002, J1 of #2965), telemetry only: the old replan's plan stays the output. Component `layout`, target `plan`, one row per replan, `dur_ms` the whole run (`ReplanTileFacts`, the chain and the tiler; it runs off the review). `verdict` `compared`/`reason` `tiled_beside_replan`: attrs `same`, `moved`, `new`, `dropped` (room counts of the tiled plan against the old replan's), `rooms` (each `room` the old plan's id, 0 for a new one, `tiled` the tiled plan's id, `role`, `verdict`, `reason`, `built`), `unmatched_built` (each standing room the chain has no node to pin: `room`, `role`, `reason` `tile_outdoor`, `tile_no_native_role` (both skipped by `BuiltRegions` unless the room is a room node's, #3003), `tile_not_a_block` or `tile_no_node`), `built` (standing rooms) and `tiled_rooms`. A room `reason` is `tile_kept_built` (same), `tile_successor` (moved), `tile_sited_again` (an unbuilt room matched by role), `tile_unpinned_built` (an unmatched standing room the tiler builds its own room of), `tile_superseded` (dropped, its standing successor is the room), `tile_built_retired_by_old` (new: the chain keeps a standing room the old replan retired), `tile_no_old_room` (new) or an unmatched reason (dropped). `skipped`: `station_advice_unavailable` (the review's chain made no advice), `facts_unknown` (attr `fact`) or `tile_in_flight` (one tile runs at a time). `failed`/`tile_error`, `WARN`, attr `error`. Reader: `rimgovernor log` |
| `layout_role_guard` (decision) | `verdict` `mismatch`, `reason` `native_role_differs_from_predicted`, `target` the planned room id; attrs `native` (the census role), `predicted` (PredictRole of the room's observed stations), `scores` (role to score). One row per standing planned room whose native role differs, on each rounds building step that reads the plan, rooms, construction and sleeping census; a room with an unknown bed state or an ideo altar is not compared. The regrouping reads the rows as a cost input and a split move (`layout_regroup`). A guard error is `failed`/`guard_error`, `WARN`, attr `error`. |
| `layout_stations` (decision) | the station chain, one row per run at a layout review (`stationAdvice`; skipped, and no row, while its inputs are unchanged), component `layout`, target `plan`, `dur_ms` the chain's wall time. `verdict` `advised`/`reason` `station_chain`: attrs `nodes`, `edges`, `groups` (each `group`, `role` predicted, `stations` {node, count}, `requires`, `footprint`, `clearance`, `area`, `cost_ticks_per_day`, `lost_effects`, `rooms` matched built room ids, `built_area`, `outgrown`, `climate` {cells, max_c, best, options {opening, mean_w, peak_w, heating_w, cooling_w, build_w, total_w}} on a cold group when the curve is known, `block` when placed), `conflicts` ({a, b, conflict}), `embed` ({level, placed, issues, built {group, room, verdict `keep`/`move`, reason `embed_built_*`}, cells, total, soil, edge, centre, walk, unmet_links, switch, seed}), `sites` (each power site `node`, `x`, `z`, `anchored`, and for a turbine `rotation`, `blocked` wind-path cells, `output` the fraction of rated output left), `site_issues` ({node, verdict, reason}), `vents` (the tiled plan's vent sites {x, z, rotation, reason}) and `vent_issues` ({group, verdict, reason} for a `tile_vent*` decision that placed none) and `comfort` (the advised comfort tier of the sleep node and each worked bench, {node, def, stuff, comfort, priced, gain, benefit_w, cost_w}; empty while the mood or comfort model or the colonists' mood is unknown, `priced` false for a node that could not be priced). attr `seed_only` names the unread fact that left a derive-time graph at the seed rooms (empty otherwise). `skipped`/`facts_unknown`: attr `fact` names the unread fact. `failed`/`chain_error`, `WARN`, attr `error`. The advice feeds the successor rooms (`layout_plan` target `successor`); the chain itself changes nothing. Reader: `rimgovernor log` |
| `snapshot_skip` | decision, `WARN`. `target` the snapshot (`colony`, `combat`, `defense`, `layout`, `firebreak`, `shelter`), `verdict` `skipped`, `reason` `not_recorded`; attrs `error`, `tick` |
| `routine_skip` (decision) | `target` the subject, `verdict` `skipped`/`waiting`, `reason`; attrs `quest`, `script`, `detail`, `findings` |
| `placement_refused`, `foreign_held` | event rows, component `building-planner` (`rounds_room_reconcile.go`). `msg` only: `furniture cell refused: <def>@<x>,<z> blocked by <thing, ...>` (`ring cell refused:` for a wall or door cell) (the native preview's blockers, `unreported` when it names none) and `foreign thing held: <def>@<x>,<z>:<reason>` (`casket`, `ancient_danger`, `not_deconstructible`, `forbidden_item`: a forbidden haulable stack on a wanted door cell, named but not blocking). The same text is the room's wait key (`<room>_reconcile:blocked:...`, `<room>_reconcile:held:...`). Read by the shelter climate cases (`nativeaccept.LogMessages`) |

### Defense, population and pawns

| Kind | Shape and fields |
|---|---|
| `defense_action` (decision) | `target` pawn, plan, incident, cell or tier, `verdict` `applied`/`refused`/`failed`/`waiting`, `reason` the refusal or issue; attrs `incident`, `plan`, `outcome`, `x`, `z`, `error`. An unbuilt tier emits `waiting` / `tier_unbuilt` when its census changes, targeting the tier with attrs `buildings`, `census`. A turret, mortar or IED tier cut short by a Go gate emits `refused` with the gate as `reason` (`turret_unavailable`, `turret_threat_budget`, `turret_power`, `turret_stock`, `turret_no_conduit_route`, `mortar_*`, `ied_*`; see [defense tier counts](spatial-contracts.md#defense-tier-counts)), targeting the tier. |

### Other event families

| Kind | Shape and fields |
|---|---|
| `http_access` | event row, one per HTTP request. `method`, `path`, `status`, `dur_ms`, `bytes`, `long_lived`; 2xx GETs of `/api/state`, `/api/routines`, `/api/health` and `/api/presentation/*` under 250 ms are dropped; errors, slow calls and every non-GET are kept. Pprof rows are written at finish with `long_lived: true` (`dur_ms` is the capture, not latency). Written by `httpapi.Config.Access`; component `httpapi`; level `WARN` for a 5xx |
| `fields_select` | decision, one per farm site-type selection (`RoundsFieldPlanner`). `target` the winning crop, `verdict` `selected`, `reason` the winning site kind (`outdoor`, `greenhouse-new`, `greenhouse-reuse`, `hydroponics`); attrs `cells`, `buildings`, `needed`, `urgent`, `candidates` (each `kind`, `crop`, `needed`, `cells`, `score`, `terms` {name: value}, `reason` for an unplantable one). Read by `nativeaccept/farmselect`. Component `clock-scheduler` |
| `recovery_batch` | decision, one per clearance planner step with queued recovery removals (`RoundsClearancePlanner`); it journals the batch the planner executes. `target` `clearance`, `verdict` `planned`/`held`/`failed`, `reason` the stage (`roof`, `removal`, `none`, or `roof_rules` for a failed row); attrs `targets`, `roof_cells`, `batch` (load ids), `held` (each `id`, `reason` a `RemoteHoldReason` word), `blocker` (the first joint roof-check word), `error`. Component `clock-scheduler` |
| `food_credit` | decision, one per ledger counter group when the credit factor moves by 0.1 or more or the group's state changes. `target` the group (`crop:<zone>`, `fish:<x,z>`, `forage:<def>`, `animal_product:<race>`), `verdict` `credited`, `reason` `window` (the trailing window moved it) or `rebaseline` (a load held it); attrs `expected`, `observed` (nutrition over the window), `factor`, `window_days`, `state` (`designated`, `delivering`). Component `routine`. Written by the food review (`rounds_food_plan.go`); `logview` prints the factor and state |
| `mod_log` | event row for each `rimgovernor.log` event from the mod, written by `bridge/modlog.go`. Row context: `component` `mod`, the mod's `tick`, `at` (the mod's time, so a replayed line keeps it) and `trace_id`/`span_id` from the event's `trace` (a W3C `traceparent`, or the older `<trace_id>/<span_id>`: the controller span a main-thread hop carried); `level` `WARN`/`ERROR` for mod `warn`/`error`. Payload `seq` (mod ring order), `level`, `source` (the mod component, e.g. `watchdog`), `msg`, and when set `late` (written while nothing was subscribed, replayed on subscribe) and `suppressed` (lines the call site's rate limit dropped before this one). The C# helper is `RimGovernor.Host.Sdk.ModLog`: bounded ring of 512, 5 lines per call site per 10 s, reentrant writes dropped, never `Log.Error`, never blocks the game thread |
| `build_stalled` | decision, level `WARN`, component `building-planner` (`rounds_room_stall.go`): a room whose blueprints or frames have stood unfinished for `roomBuildStallTicks` (30000), repeated every window while it lasts. `target` the room, `verdict` `waiting`, `reason` the most common census reason (`awaiting_materials`, `no_eligible_builder`, a native `FinishingBlocker` word, `unreported`); attrs `blueprints`, `frames`, `reasons` {reason: count}, `tiers` {tier: count}, `eligible_finishers`, `max_min_skill`, `stalled_ticks`, `sites` (up to 8 `<def>@<x>,<z>`), `obstructions` (`<op>:<def>(<count>)@<x>,<z>` for pending claim, cut and haul targets; with no site standing `reason` is `obstructed`). The room wait key meanwhile reads `<room>_reconcile:obstructed:<first obstruction>` or `<room>_reconcile:awaiting_build:<n>_blueprints,<n>_frames:<reason>`. Reader: `rimgovernor log` (`logview.Entry.Words`) and the launcher Log panel (WARN) |
| `rule` | decision, one per native rule event ingested from the clock inbox (`clockPollEvents`, #2154). A firing: `verdict` `fired`, `reason` `prey_killed`, `target` the actor pawn; attrs `rule`, `target` (the prey), `params` (`job`, `radius`), `tick`, `cursor`. A lapsed lease: `verdict` `expired`, `reason` `lease_expired`, `target` `rules`; attrs `expires_at_tick`, `deactivated`, `cursor`. Reader: the launcher Log panel (`infoLogKinds`) and `rimgovernor log` |
| `telemetry_drop` | event row, level `WARN`, component `otel`: the OTEL pipeline lost items (at most one per pipeline per 10 s). `signal` (`trace`/`log`/`metric`), `sink` (`file`/`collector`), `reason` (`queue_full`/`export_failed`), `dropped_since_last_warning`. The cumulative count is the `rimgovernor.telemetry.dropped` metric ([OTEL core](otel-core.md)). Reader: the Grafana Problems and Log dashboards (every WARN) and `rimgovernor log` |
| `log_overflow` | event row, level `WARN`: one summary of dropped diagnostics. `dropped`, `side` `mod` (the mod's ring overflowed while nothing was subscribed or the publisher lagged; `seq` is where the lines were) or `controller` (the reader goroutine's hand-off queue of 1024 was full) |

### Session and reader events

| Kind | Notes |
|---|---|
| `state_reset` | event row, level `WARN`, component `serve`: the service database came from another schema version and was moved aside (`path`, `aside`). Written once at startup. |
| `observe_snapshot_failed` | event row, level `WARN`, component `serve`: `--observe-snapshot` could not create its directory (copies disabled) or one 5 s copy failed (retried next tick); `path`, `err`. Reader: `rimgovernor log` and the Grafana Loki logs. |

The OTLP log ring is the only log: the telemetry handler writes a row for a record that names a `kind`
and drops one that does not, there is no Debug level, and `telemetry.Event` takes the `kind` as a required
parameter so code logs through it, not `slog` directly. stderr carries only the startup banner, parse errors,
`Go service:` failures, the clock fault-injection notice and Go panics. A new emission adds its kind here with
a reader, or it is not added.

## Mapping

Every decision and event row is an OTEL log record in `logs.jsonl` and,
when the emitter runs under a recording span, an event on that span. The code is
`telemetry.EmitRow` (`internal/telemetry/otelrow.go`): `telemetry.Decide`, every
`slog` call with a `kind`, and the direct emitters (`mod_log`, `log_overflow`,
`http_access`, `ReadTally.Publish`) all reach it, so the kinds registry and the
consuming-reader rule above apply unchanged: a kind is the record's `eventName`.
The bridge's own `native_call`, `native_request`, `native_frame` and `native_poll`
rows are mapped too (`bridge.Client.nativeRow`), as ordinary event records: the
acceptance oracles read reply bodies (clock status, `failure.code`, startup and
speedmatrix replies) that only these rows carry, so they are rows, not derived
from spans or metrics. `native_request` is durable (fsynced) and its `request`
attribute is the id the answering `native_call` repeats (a per-process counter).
Recording is on for `serve` (`bridge.ProcessConfig.RecordRows`) and off for a
bare client.

This attribute schema is what log readers parse. Changing it is a contract change.

| OTEL field | Content |
| --- | --- |
| `eventName` | the row kind |
| `timeUnixNano` | the row's `at` |
| `severityNumber` / `severityText` | `INFO` 9, `WARN` 13, `ERROR` 17, from the row's `level` |
| `body` | a decision: `"<kind> <verdict> <reason> <target>"` (derived, never parse it); an event: its `msg`; absent when an event has none |
| `traceId`, `spanId` | the span the emitter ran under (`mod_log`: the controller span the mod echoed) |
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

`rimgovernor log`, `phases` and `trace` (read from the OTLP rings), the Grafana
dashboards (Loki, [README](../../../tools/observability/dashboards/README.md))
and acceptance diagnostics consume these kinds.
`bridge.IsWorkerStep`, `bridge.IsWindowStop` and `bridge.StepFields` centralize
clock/worker interpretation. Combat `clock_stop` rows carry `event`; ordinary
stop-event rows do not. Acceptance readers use the case's OTLP log ring
(`<output>/otlp/logs.jsonl*`, `<output>/service-N/otlp/...` per relaunch):
`bridge.ReadOTLPTimeline` and `bridge.RecordFromLog` rebuild the row shape these
helpers expect from the typed log reader (`otel.ReadLogs`, see
[OTEL core](otel-core.md)); `nativeaccept.ReadLogs`, `LogTail` and
`LogMessages` sit on top. A null attribute is absent in a record, not present
as nil. Postmortem evidence is `otlp/logs.jsonl.2#77`: the segment and its
1-based line (what `grep -n` prints).

## Retention

`serve` writes the three files into `--otlp-dir` (default `<profile>/otlp/`;
beside `--state` under `--observe`; the acceptance runner passes `<output>/otlp`
and `<output>/service-N/otlp` per relaunch). Each signal is its own rotating
ring: `traces.jsonl` 16 MiB x 8, `logs.jsonl` 32 MiB x 16 (512 MiB),
`metrics.jsonl` 8 MiB x 4. When the active file would pass its segment size it
becomes `name.1`, older segments shift up and the one beyond the count is
deleted; retention is by count only. Under overload the sink drops items
and counts them (`telemetry_drop` row, `rimgovernor.telemetry.dropped`
metric) rather than blocking the controller. A run with no collector keeps
exactly these files, and the acceptance and postmortem readers use them.
[OTEL core](otel-core.md) has the file layout and line format.

Add a kind only with its reader. Keep stable reason words and structured attrs
so filtering, repeat collapse and diagnostics do not depend on prose parsing.
