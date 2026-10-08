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

v1 and v2 rows coexisted on disk until #2071. A legacy kind's payload is the v1 shape in the tables; a v2 kind
is a decision row or an event row as marked. Crash safety is unchanged: unbuffered writes, fsync only on
durable rows, rotation and close.

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

Repeat collapsing (the reader, #2053) keys on `(kind, component, verdict, reason, target)`.

The helper writes the row only; nothing is rendered to stderr.

## Kinds

"Old" is the kind a producer writes today; "Piece" is the child issue whose landing replaces the old kind,
moves its producer and removes it. Readers move in #2054 unless named. Until its piece lands an old kind keeps
its v1 payload and its readers.

### Native bridge (`internal/bridge`)

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `native_response`, `native_error`, `native_decode` | `native_call` | event row, one per completed call (landed, #2057; `Client.core` writes it, a typed call's row waits for its reply decode). `request` (sequence of the in-flight marker, only when one was written), `tool`, `native_tool`, `arguments` (a `games_call_tool` call keeps only the inner `request` string), `ok`, `error`, `refused_text`, `reply_type`, `result` (the reply without its native `timing` block, which `timing` already carries), `timing` (the v1 phases plus `proto_decode_ms`, `payload_bytes`, `wire_bytes`, `proto_decode_error`; `native_frames`, the cumulative frame account, rides at most one row per 5 s). A payload over 64 KiB is replaced by a 4 KiB `preview` and its hash. Every call writes it; the hot-poll denylist applies to HTTP rows only (#2055). `nativeaccept/postmortem` reads the error and failure cases from `native_call` rows (landed, #2065) | #2057 |
| `native_request` | `native_request` (kept) | slow or outstanding marker only, written (durable) once a call has run past `bridge.slowCallMarker` (2 s): `tool`, `native_tool`, `arguments`. The completed `native_call` names it in `request`; a hung call leaves the marker last, and `trace` prints `(no reply recorded)` | #2057 |
| `native_frame`, `native_frame_hit`, `native_frame_miss` | `native_frame` | event row. `outcome` (`decoded`, `hit`, `miss`), `native_tool`, `frame`, `why` (miss), plus the v1 decoded-frame fields | #2057 (landed) |
| `native_cache_hit` | removed | no writer; the dead reader branch in `cmd/launcher/problems.go` goes | #2054 |
| `combat_order` | `combat_order` (decision) | `target` pawn, `verdict` `applied`/`refused`, `reason` refusal, attrs `index`, `job` | #2067 |
| `clock_step` | `clock_step` (decision) | `verdict` step outcome (`admitted`, `deferred`, `refused`, `idle`, `failed`), `reason` the first window refusal or a stable word (`window_admitted`, `window_refused`, `deferred`, `no_window`, `step_error`), `target` the step's cause (`timer`, `wake`, `settled`, `full`, `live`), `dur_ms` step wall; attrs also `gate_wait_ms`, `journal_ms`, `window_ticks`, `stop`, `stop_latency_ms`, the pacing fields and `error`; attrs `reads`, `tools`, `schema_fetches`, `running`, `stop_pause_s` (the read tally's payload moves under `attrs`) | #2073 (landed; #2063 designed it) |
| `worker_dispatch`, `worker_outcome` | `dispatch` (decision) | one per run that acted. `target` action, `verdict` outcome (`completed`, `refused`, `failed`, `waiting`, `held`), `reason` cause or error class; attrs `attempt`, `stage`, `stage_after`, `receipt`, `running`, `stale`, `error`, `repeated`, `reads`, `tools`, `schema_fetches`. Level `WARN` for a failure other than a dependency wait. Written for a run that reached native (carries `receipt`, `running` and the read tally; `rimgovernor phases` counts only these) or whose outcome changed (`changed` true, `repeated` the unrecorded runs that restated the previous outcome); an unchanged run that never reached native writes nothing. `reason` is the stage word for a wait, `dependency`, `stale`, `error`, the first refusal, or `completed`; `refused` (all refusal reasons) rides in attrs. The cancelled-settle of an undispatched action is `failed`/`cancelled_settled` with `attempts` | #2073 (landed) |

### Clock and scheduler (`internal/buildingruntime`)

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `scheduler_step`, `stockpiles` ("stockpile step") | `planner_step` (decision) | one per planner run. `target` planner, `verdict` outcome (`admitted`, `waiting`, `refused`, `unselected`, `failed`), `reason` the refusal or wait cause; attrs `proposals`, `edits`, `cause`, `admitted`, `running`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `window_ticks`, `repeated`, `error` | #2063 (spine, landed: the row is written where the planner returns, `reason` is the refusal kind and `plan_admitted`/`no_verdict`/an outcome word otherwise, attrs `concern`, `class`, `subject`, `detail`, `error`, `late`; each Rounder step writes one row, target `rounds`, `ok`/`reviewed` or `failed`/`error` (`control_lost` for ErrControl), attr `partial` (#2066); a step's own failure to record a wave files `failed`/`journal_error` with target `reasons`, `waits` or `proposals`), #2073 (worker step, landed: target `worker_step` (`bridge.WorkerStepTarget`), `verdict` `admitted`/`waiting`/`failed`, `reason` `window_admitted`, `no_window`, `held`, `retry`, `epoch_reopen`, `planner_failures` or `step_error`; attrs `planner_failures`, `planner_unselected`, `admitted`, `running`, `window_ticks`, `proposals`, `cause`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `repeated`, `error`; `WARN` only for a real step error) |
| `admission`, `admission_refused`, `fight_admission` | `admission` (decision) | `target` method, window or plan, `verdict` `admitted`/`refused`/`held`, `reason` the refusal (`critical_wave_budget`, a `ClockWindowReason`, a method refusal); attrs `concern`, `refused` (all reasons), `held_by`, `mode`, `work`, `combat_plan`, `hostiles`, `clock_state`, `window_ticks`, `wall_budget_ms`, `error` | #2063 (window, method), #2067 (fight) |
| `scheduler_stop`, `combat_stop` | `clock_stop` | event row. `reason`, `evidence`, `cursor`, `observed_at_unix_ms`, `benign`, the stop legs, and for a combat stop `event`, `resume_latency_ms`, `ticks_since_stop` | #2073 (landed) |
| `combat_stops` | `combat_summary` | event row at combat end. `stops`, `by_event`, `resume_latency_p50_ms`, `resume_latency_p95_ms`, `ticks_between_stops_p50`, `ticks_between_stops_p95` | #2073 (landed) |
| `authority_change`, `authority_lost`, `clock_retaken` | `authority` | event row. `change` (`changed`, `lost`, `retaken`), `reason`, `active`, `generation`, `previous_generation`, `cursor`, `paused`, `pace`, `stop_reason`; `WARN` for `lost` | #2073 (landed) |
| `backlog_adopted` | `clock_journal` | event row. `through_cursor`, `newest_cursor` | #2073 (landed) |
| `alert_row` | `alert` | event row. `key`, `label`, `priority`, `cursor` | #2073 (landed) |
| `pace_backoff` | `pace` | decision. `target` `backoff`, `reason` the `why`; attrs `ceiling`, `horizon_ticks`, `error` | #2073 (landed) |
| `idle_stall` | `idle_stall` | decision, `WARN`. `reason` `no_work`; attrs `tick`, `refusals`, `lend_ticks`, `standing` | #2073 (landed) |
| `pacing_mode` | removed | no writer; the `internal/spectator/now.go` branch goes | #2054 |

### Rounds, layout and colony (`internal/buildingruntime/rounds*`, `colony_plan`, `layout_*`)

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `rounds_review` | `rounds_review` | event row (kept). `revision`, `previous_revision`, `concerns`, `emergency`, the stage and food attrs of `roundsStageAttrs` and `roundsFoodAttrs` | #2066 |
| `colony_stage` | `colony_stage` | event row. `stage`, `since`, `blocker`, `reason`, `held` | #2066 |
| `build_tier` | `build_tier` | event row. `tier`, `evidence` | #2066 |
| `layout_plan`, `layout_replan`, `suite_claims` | `layout_plan` (decision) | `verdict` `planned`/`replanned`/`claimed`/`refused` (`reason` `no_room`: a replan left rooms unplaced, attr `unplaced`, once per distinct set), `reason` replan reason, attrs `colonists`, `summary`, `claims`, and on a replan `tomb_short`; `skipped`/`no_core` when the survey holds no core; a suite-claims change is `claimed` with target `suites` (landed, #2066) | #2066 |
| `stockpiles` (edits; the `rounds_stockpiles.go` rows landed in #2066 as `layout_edit` family `stockpile`), `fields`, `building_retire` | `layout_edit` (decision) | `target` zone, role, item or building, `verdict` `admitted`/`refused`/`abandoned`/`closed`/`proposed`, `reason` refusal code or detail; attrs `family` (`stockpile`, `field`, `building`), `kind`, `role` (stockpile create/delete),  `plan`, `crop`, `cells`, `moves`, `gain`, `owner`, `x`, `z` | #2068 (fields, `layout_fields_shrink`), #2069 (stockpiles, tidy, `building_retire`) |
| `snapshot` ("not recorded" warnings from rounds, defense, defense-layout, firebreak) | `snapshot_skip` | decision, `WARN`. `target` the snapshot (`colony`, `combat`, `defense`, `layout`, `firebreak`, `shelter`), `verdict` `skipped`, `reason` `not_recorded`; attrs `error`, `tick` | #2066 (colony), #2067 (combat, defense), #2068 (firebreak) |
| `traffic_finding`, `entity_study_unread`, `odyssey_quest_skip` | `routine_skip` (decision) | `target` the subject, `verdict` `skipped`/`waiting`, `reason`; attrs `quest`, `script`, `detail`, `findings` | #2069 (`rounds_flooring`, `rounds_fishing`), #2066 (`rounds.go`) |
| (new) `placement_refused`, `foreign_held` | same names | event rows, component `building-planner` (`rounds_room_reconcile.go`). `msg` only: `furniture cell refused: <def>@<x>,<z> blocked by <thing, ...>` (the native preview's blockers, `unreported` when it names none) and `foreign thing held: <def>@<x>,<z>:<reason>` (`casket`, `ancient_danger`, `not_deconstructible`). The same text is the room's wait key (`<room>_reconcile:blocked:...`, `<room>_reconcile:held:...`). Read by the shelter climate cases (`nativeaccept.FlightMessages`) | #2271, #2269, #2278, #2304 |

### Defense, population and pawns

| Old kind | v2 kind | v2 shape and fields | Piece |
|---|---|---|---|
| `hold_refused`, `animal_clear`, `entity_capture_refused`, `entity_kill`, `entity_tend_unavailable`, `containment_upkeep_issue`, `containment_upkeep_exhausted`, `undraft`, defense tier unbuilt | `defense_action` (decision) | `target` pawn, plan, incident, cell or tier, `verdict` `applied`/`refused`/`failed`/`waiting`, `reason` the refusal or issue; attrs `incident`, `plan`, `outcome`, `x`, `z`, `error`. An unbuilt tier emits `waiting` / `tier_unbuilt` when its census changes, targeting the tier with attrs `buildings`, `census`. | #2067, #2369 |

### New in v2

| v2 kind | Shape and fields | Piece |
|---|---|---|
| `http_access` | event row, one per HTTP request. `method`, `path`, `status`, `dur_ms`, `bytes`, `long_lived`; 2xx GETs of `/api/state`, `/api/spectator/now`, `/api/routines`, `/api/health` and `/api/presentation/*` under 250 ms are dropped; errors, slow calls and every non-GET are kept. Pprof rows are written at finish with `long_lived: true` (`dur_ms` is the capture, not latency). Written by `httpapi.Config.Access`; component `httpapi`; level `WARN` for a 5xx | #2055 |
| `fields_select` | decision, one per farm site-type selection (`RoundsFieldPlanner`). `target` the winning crop, `verdict` `selected`, `reason` the winning site kind (`outdoor`, `greenhouse-new`, `greenhouse-reuse`, `hydroponics`); attrs `cells`, `buildings`, `needed`, `urgent`, `candidates` (each `kind`, `crop`, `needed`, `cells`, `score`, `terms` {name: value}, `reason` for an unplantable one). Read by `nativeaccept/farmselect`; replaces the multi-line `Fields select:` debug trace. Component `clock-scheduler` | #2062 |
| `recovery_batch` | decision, one per clearance planner step with queued recovery removals (`RoundsClearancePlanner`); it journals the batch the planner executes. `target` `clearance`, `verdict` `planned`/`held`/`failed`, `reason` the stage (`roof`, `removal`, `none`, or `roof_rules` for a failed row); attrs `targets`, `roof_cells`, `batch` (load ids), `held` (each `id`, `reason` a `RemoteHoldReason` word), `blocker` (the first joint roof-check word), `error`. Component `clock-scheduler` | #2298 |
| `food_credit` | decision, one per ledger counter group when the credit factor moves by 0.1 or more or the group's state changes. `target` the group (`crop:<zone>`, `fish:<x,z>`, `forage:<def>`, `animal_product:<race>`), `verdict` `credited`, `reason` `window` (the trailing window moved it), `rebaseline` (a load held it) or `lost` (dropped ledger rows held it); attrs `expected`, `observed` (nutrition over the window), `factor`, `window_days`, `state` (`designated`, `delivering`). Component `routine`. Written by the food review (`rounds_food_plan.go`); `logview` prints the factor and state | #2157 |
| `mod_log` | event row for each `rimgovernor.log` event from the mod, written by `bridge/modlog.go`. Row context: `component` `mod`, the mod's `tick`, `at` (the mod's time, so a replayed line keeps it) and `trace_id`/`span_id` from the event's `trace` (`<trace_id>/<span_id>`, the controller trace a main-thread hop carried); `level` `WARN`/`ERROR` for mod `warn`/`error`. Payload `seq` (mod ring order), `level`, `source` (the mod component, e.g. `watchdog`), `msg`, and when set `late` (written while nothing was subscribed, replayed on subscribe) and `suppressed` (lines the call site's rate limit dropped before this one). The C# helper is `RimGovernor.Host.Sdk.ModLog`: bounded ring of 512, 5 lines per call site per 10 s, reentrant writes dropped, never `Log.Error`, never blocks the game thread | #2058 |
| `rule` | decision, one per native rule event ingested from the clock inbox (`clockPollEvents`, #2154). A firing: `verdict` `fired`, `reason` `prey_killed`, `target` the actor pawn; attrs `rule`, `target` (the prey), `params` (`job`, `radius`), `tick`, `cursor`. A lapsed lease: `verdict` `expired`, `reason` `lease_expired`, `target` `rules`; attrs `expires_at_tick`, `deactivated`, `cursor`. Reader: the launcher Log panel (`infoLogKinds`) and `rimgovernor log` | #2154 |
| `log_overflow` | event row, level `WARN`: one summary of dropped diagnostics. `dropped`, `side` `mod` (the mod's ring overflowed while nothing was subscribed or the publisher lagged; `seq` is where the lines were) or `controller` (the reader goroutine's hand-off queue of 1024 was full) | #2058 |

### Unchanged or reader-side

| Kind | Notes |
|---|---|
| `coverage` | First row of each run; free text by design. Stays. |
| `recording_gap` | Synthetic, produced by `TimelineReader` for a corrupt line or sequence discontinuity; never written. Fields `reason`, `file`, `line`, `before`, `after`. |

| `state_reset` | event row, level `WARN`, component `serve`: the service database came from another schema version and was moved aside (`path`, `aside`). Written once at startup (#2071). |

The flight recorder is the only log (#2071): the telemetry handler writes a row for a record that names a `kind`
and drops one that does not, there is no Debug level, and `telemetry/kinds_test.go` fails any `slog` call in
non-test code with no `kind`. stderr carries only the startup banner, parse errors, `flight recorder:` and
`Go service:` failures, the clock fault-injection notice and Go panics. A new emission adds its kind here with
a reader, or it is not added.

## Readers

| Reader | Moves in |
|---|---|
| `bridge` phases, `cmd/launcher` problems and Log tab, spectator `now`, `TimelineReader` consumers | #2054 |
| `rimgovernor log` | #2053 |

The producers of the clock and worker family write only the v2 kinds (#2073): `clock_step`, `planner_step`
(target `worker_step` for the clock worker's step), `dispatch`, `clock_stop`, `combat_summary`, `authority`,
`clock_journal`, `alert`, `pace`, `idle_stall`. Their
readers match those names only (`bridge.IsWorkerStep`, `bridge.IsWindowStop`, `bridge.StepFields`; the combat
`clock_stop` rows carry `event`, the stop-event rows do not). The legacy branches that remain, and the piece
that deletes each:

| Legacy name read | v2 name | Deleted by |
|---|---|---|
| `native_response`, `native_error`, `native_decode`, `native_frame_hit` (`bridge/flightrows.go`, `flightrecorder_phases.go`, `flightrecorder_trace.go`, `cmd/launcher/problems.go`) | `native_call`, `native_frame` (`outcome`) | #2057 (the `native_error`/`native_response` names stay until #2065) |
| Log panel INFO kinds `hold_refused`, `animal_clear`, `entity_kill`, `entity_capture_refused` (`cmd/launcher/logtail.go`) | `defense_action` | #2067 |
| combatlab `ScanFlight` reads a `combat_order` `outcome` attr when `verdict` is absent | `combat_order` decision | #2067 |
| postmortem, `acceptance why` (raw `native_error` greps and service stderr) | `native_call` rows | #2065 |

The acceptance readers `stepevent`, `stepstall`, `failfast` (#2061), `farm/select` and `combatlab` (#2062) read the
v2 rows directly; the bundle's `combat_flight.jsonl` replaces `service.log`.

