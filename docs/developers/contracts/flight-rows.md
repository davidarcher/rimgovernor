# Flight rows (schema v2)

[Documentation](../../README.md)

`flight.jsonl` is the one diagnostic stream (epic #2038, schema piece #2050). One row is one thing that
happened. This page is the contract the producer and reader pieces follow: the envelope, the decision-row
shape, and every kind (existing and new) with the child issue that moves it.

## Envelope

One JSON object per line, written by `bridge.FlightRecorder.Event`:

| Field | Meaning |
|---|---|
| `version` | `2` (`bridge.FlightSchemaVersion`). Readers never gate on it; v1 rows decode the same way. |
| `run` | Recorder run id (a launch). |
| `sequence` | Monotonic across runs sharing the file. |
| `wall_time` | Unix seconds. |
| `kind` | A name from the tables below. |
| `context` | Always: `level` (`INFO`/`WARN`/`ERROR`), `at` (RFC 3339 UTC), `tick` (when the service has observed one), `component`, `trace_id`, `span_id` (`parent_id` under a span). The recorder fills what the producer left out (`INFO`, now, last observed tick, component `bridge`, a fresh single-row trace); a producer's own values win. |
| `payload` | Per kind. Oversized payloads are replaced by `{truncated, original_bytes, sha256, preview}` plus the correlation keys `request`, `tool`, `native_tool`, `category`, `timing` and the decision keys `verdict`, `reason`, `target`, `dur_ms`. |

v1 and v2 rows coexist on disk until #2071. A legacy kind's payload is the v1 shape in the tables; a v2 kind
is a decision row or an event row as marked. Crash safety is unchanged: unbuffered writes, fsync only on
durable rows, rotation and close.

## Decision rows

One row per planner run, goal or method selection, admission outcome and action dispatch, emitted with
`telemetry.Decide(ctx, telemetry.Decision{...})`. The payload shape is fixed:

```json
{"verdict": "refused", "reason": "critical_wave_budget", "target": "window", "dur_ms": 1.5, "attrs": {}}
```

- `verdict`: the outcome word. Vocabulary: `admitted`, `refused`, `waiting`, `applied`, `failed`,
  `skipped`, `ok`, plus a kind's own words where its table says so.
- `reason`: a stable short word. No data inside it (the data goes in `attrs`): rows collapse on it.
- `target`: what it was about (planner, goal, method, plan, pawn, zone, tool).
- `dur_ms`: milliseconds, `0` when untimed.
- `attrs`: kind-specific fields; always an object. Durations render as milliseconds, errors as text.
- No free text: a decision row has no `msg`. The row's `level` is `WARN` only for a refusal or failure a
  reader should surface.

Repeat collapsing (the reader, #2053) keys on `(kind, component, verdict, reason, target)`.

The helper also renders a one-line text summary to the stderr sink until #2071 retires it.

## Kinds

"Old" is the kind a producer writes today; "Piece" is the child issue whose landing replaces the old kind,
moves its producer and removes it. Readers move in #2054 unless named. Until its piece lands an old kind keeps
its v1 payload and its readers.

### Native bridge (`internal/bridge`)

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `native_response`, `native_error`, `native_decode` | `native_call` | event row, one per completed call. `request` (sequence of the in-flight marker, when one was written), `tool`, `native_tool`, `ok`, `error`, `refused_text`, `reply_type`, `result`, `timing` (the v1 phases plus `proto_decode_ms`, `payload_bytes`, `wire_bytes`). Slow or failed calls only for the hot-poll denylist (#2038) | #2057 |
| `native_request` | `native_request` (kept) | slow or outstanding marker only: `tool`, `arguments` | #2057 |
| `native_frame`, `native_frame_hit`, `native_frame_miss` | `native_frame` | event row. `outcome` (`decoded`, `hit`, `miss`), `native_tool`, `frame`, `why` (miss), plus the v1 decoded-frame fields | #2057 |
| `native_cache_hit` | removed | no writer; the dead reader branch in `cmd/launcher/problems.go` goes | #2054 |
| `combat_order` | `combat_order` (decision) | `target` pawn, `verdict` `applied`/`refused`, `reason` refusal, attrs `index`, `job` | #2067 |
| `clock_step` | `clock_step` (decision) | `verdict` step outcome (`admitted`, `deferred`, `refused`, `idle`), `reason` cause, `target` step reason, `dur_ms` step wall; attrs `reads`, `tools`, `schema_fetches`, `running`, `stop_pause_s` (the read tally's payload moves under `attrs`) | #2063 |
| `worker_dispatch`, `worker_outcome` | `dispatch` (decision) | one per run that acted. `target` action, `verdict` outcome (`completed`, `refused`, `failed`, `waiting`, `held`), `reason` cause or error class; attrs `attempt`, `stage`, `stage_after`, `receipt`, `running`, `stale`, `error`, `repeated`, `reads`, `tools`, `schema_fetches`. Level `WARN` for a failure other than a dependency wait | #2064 |

### Clock and scheduler (`internal/buildingruntime`)

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `scheduler_step`, `stockpiles` ("stockpile step"), the surviving `clockSchedulerLog` Debug sites | `planner_step` (decision) | one per planner run. `target` planner, `verdict` outcome (`admitted`, `waiting`, `refused`, `unselected`, `failed`), `reason` the refusal or wait cause; attrs `proposals`, `edits`, `cause`, `admitted`, `running`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `window_ticks`, `repeated`, `error` | #2063 (spine, landed: the row is written where the planner returns, `reason` is the refusal kind and `plan_admitted`/`no_verdict`/an outcome word otherwise, attrs `concern`, `class`, `subject`, `detail`, `error`, `late`; each Rounder step writes one row, target `rounds`, `ok`/`reviewed` or `failed`/`error` (`control_lost` for ErrControl), attr `partial` (#2066); a step's own failure to record a wave files `failed`/`journal_error` with target `reasons`, `waits` or `proposals`), #2064 (worker) |
| `admission`, `admission_refused`, `fight_admission` | `admission` (decision) | `target` method, window or plan, `verdict` `admitted`/`refused`/`held`, `reason` the refusal (`critical_wave_budget`, a `ClockWindowReason`, a method refusal); attrs `concern`, `refused` (all reasons), `held_by`, `mode`, `work`, `combat_plan`, `hostiles`, `clock_state`, `window_ticks`, `wall_budget_ms`, `error` | #2063 (window, method), #2067 (fight) |
| `scheduler_stop`, `combat_stop` | `clock_stop` | event row. `reason`, `evidence`, `cursor`, `observed_at_unix_ms`, `benign`, the stop legs, and for a combat stop `event`, `resume_latency_ms`, `ticks_since_stop` | #2064 |
| `combat_stops` | `combat_summary` | event row at combat end. `stops`, `by_event`, `resume_latency_p50_ms`, `resume_latency_p95_ms`, `ticks_between_stops_p50`, `ticks_between_stops_p95` | #2064 |
| `authority_change`, `authority_lost`, `clock_retaken` | `authority` | event row. `change` (`changed`, `lost`, `retaken`), `reason`, `active`, `generation`, `previous_generation`, `cursor`, `paused`, `pace`, `stop_reason`; `WARN` for `lost` | #2064 |
| `backlog_adopted` | `clock_journal` | event row. `through_cursor`, `newest_cursor` | #2064 |
| `alert_row` | `alert` | event row. `key`, `label`, `priority`, `cursor` | #2064 |
| `pace_backoff` | `pace` | decision. `target` `backoff`, `reason` the `why`; attrs `ceiling`, `horizon_ticks`, `error` | #2064 |
| `idle_stall` | `idle_stall` | decision, `WARN`. `reason` `no_work`; attrs `tick`, `refusals`, `lend_ticks`, `standing` | #2064 |
| `pacing_mode` | removed | no writer; the `internal/spectator/now.go` branch goes | #2054 |

### Rounds, layout and colony (`internal/buildingruntime/rounds*`, `colony_plan`, `layout_*`)

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `rounds_review` | `rounds_review` | event row (kept). `revision`, `previous_revision`, `concerns`, `emergency`, the stage and food attrs of `roundsStageAttrs` and `roundsFoodAttrs` | #2066 |
| `colony_stage` | `colony_stage` | event row. `stage`, `since`, `blocker`, `reason`, `held` | #2066 |
| `build_tier` | `build_tier` | event row. `tier`, `evidence` | #2066 |
| `layout_plan`, `layout_replan`, `suite_claims` | `layout_plan` (decision) | `verdict` `planned`/`replanned`/`claimed`, `reason` replan reason, attrs `colonists`, `summary`, `claims`, and on a replan `tomb_short`, `unplaced`; `skipped`/`no_core` when the survey holds no core; a suite-claims change is `claimed` with target `suites` (landed, #2066) | #2066 |
| `stockpiles` (edits; the `rounds_stockpiles.go` rows landed in #2066 as `layout_edit` family `stockpile`), `fields`, `tidy`, `building_retire` | `layout_edit` (decision) | `target` zone, role, item or building, `verdict` `admitted`/`refused`/`abandoned`/`closed`/`proposed`, `reason` refusal code or detail; attrs `family` (`stockpile`, `field`, `tidy`, `building`), `kind`, `plan`, `crop`, `cells`, `hauls`, `moves`, `gain`, `owner`, `x`, `z` | #2068 (fields, `layout_fields_shrink`), #2069 (stockpiles, tidy, `building_retire`) |
| `snapshot` ("not recorded" warnings from rounds, defense, defense-layout, firebreak) | `snapshot_skip` | decision, `WARN`. `target` the snapshot (`colony`, `combat`, `defense`, `layout`, `firebreak`, `shelter`), `verdict` `skipped`, `reason` `not_recorded`; attrs `error`, `tick` | #2066 (colony), #2067 (combat, defense), #2068 (firebreak) |
| `traffic_finding`, `entity_study_unread`, `odyssey_quest_skip` | `routine_skip` (decision) | `target` the subject, `verdict` `skipped`/`waiting`, `reason`; attrs `quest`, `script`, `detail`, `findings` | #2069 (`rounds_flooring`, `rounds_fishing`), #2066 (`rounds.go`) |

### Defense, population and pawns

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `hold_refused`, `animal_clear`, `entity_capture_refused`, `entity_kill`, `entity_tend_unavailable`, `containment_upkeep_issue`, `containment_upkeep_exhausted`, `undraft` | `defense_action` (decision) | `target` pawn, plan, incident or cell, `verdict` `applied`/`refused`/`failed`/`waiting`, `reason` the refusal or issue; attrs `incident`, `plan`, `outcome`, `x`, `z`, `error` | #2067 |

### New in v2

| v2 kind | Shape and fields | Piece |
|---|---|---|
| `http_access` | event row, one per HTTP request. `method`, `path`, `status`, `dur_ms`, `bytes`, `long_lived`; 2xx GETs of `/api/state`, `/api/spectator/now`, `/api/routines`, `/api/health` and `/api/presentation/*` under 250 ms are dropped; errors, slow calls and every non-GET are kept. Pprof rows are written at finish with `long_lived: true` (`dur_ms` is the capture, not latency). Written by `httpapi.Config.Access`; component `httpapi`; level `WARN` for a 5xx | #2055 |
| `fields_select` | decision, one per farm site-type selection (`RoundsFieldPlanner`). `target` the winning crop, `verdict` `selected`, `reason` the winning site kind (`outdoor`, `greenhouse-new`, `greenhouse-reuse`, `hydroponics`); attrs `cells`, `buildings`, `needed`, `urgent`, `candidates` (each `kind`, `crop`, `needed`, `cells`, `score`, `terms` {name: value}, `reason` for an unplantable one). Read by `nativeaccept/farmselect`; replaces the multi-line `Fields select:` debug trace. Component `clock-scheduler` | #2062 |
| `mod_log` | event row for each `rimgovernor.log` event from the mod: `seq`, `level`, `source`, `msg`, `dropped` (ring overflow count) | #2058 |

### Unchanged or reader-side

| Kind | Notes |
|---|---|
| `coverage` | First row of each run; free text by design. Stays. |
| `recording_gap` | Synthetic, produced by `TimelineReader` for a corrupt line or sequence discontinuity; never written. Fields `reason`, `file`, `line`, `before`, `after`. |

Producers with no kind above (an `slog` call with no `kind`) write no row; #2071 removes the text sink they
reach. A new emission adds its kind here with a reader, or it is not added.

## Readers

| Reader | Moves in |
|---|---|
| `bridge` phases, `cmd/launcher` problems and Log tab, spectator `now`, `TimelineReader` consumers | #2054 |
| `rimgovernor log` | #2053 |

The #2054 readers (`bridge/flightrows.go`) accept a kind under its legacy name and its v2 name until the
producer piece lands. Each legacy branch is deleted by the piece that moves its producer:

| Legacy name read | v2 name | Deleted by |
|---|---|---|
| `native_response`, `native_error`, `native_decode`, `native_frame_hit` | `native_call`, `native_frame` (`outcome`) | #2057 (the `native_error`/`native_response` names stay until #2065) |
| `clock_step` legacy payload | `clock_step` decision (`StepFields`) | #2063 |
| `scheduler_step` (spectator `now`, trace roots) | `planner_step` (attrs `admitted`, `running`, `window_ticks`) | #2064 (worker step; the #2063 planner_step rows are per planner and do not carry them) |
| `worker_dispatch`, `worker_outcome` | `dispatch` | #2064 |
| `scheduler_stop`, `authority_change` | `clock_stop`, `authority` | #2064 |
| Log panel INFO kinds `combat_stops`, `hold_refused`, `animal_clear`, `entity_kill`, `entity_capture_refused`, `authority_lost`, `clock_retaken` | `combat_summary`, `defense_action`, `authority` | #2064, #2067 |
| acceptance `stepevent`, `stepstall`, `failfast` | #2061 |
| acceptance `farm/select` (reads `fields_select`), `combatlab` metrics and run-end stop (read `combat_stop`/`clock_stop`, `combat_order`, `worker_outcome`/`dispatch`, `combat_stops`/`combat_summary`; the bundle's `combat_flight.jsonl` replaces `service.log`) | #2062 (legacy names deleted with #2064, #2067) |
| postmortem, `acceptance why` | #2065 |
