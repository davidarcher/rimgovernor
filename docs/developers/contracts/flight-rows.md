# Flight rows (schema v2)

[Documentation](../../README.md)

`flight.jsonl` is the diagnostic stream. Each row records an event or decision.
This contract defines the envelope, decision shape, kinds and their consumers.

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

Writes are unbuffered; durable rows, rotation and close are fsynced.

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
| `native_call` | event row, one per completed call (`Client.core` writes it after typed reply decoding). `request` (sequence of the in-flight marker, only when one was written), `tool`, `native_tool`, `arguments` (a `games_call_tool` call keeps only the inner `request` string), `ok`, `error`, `refused_text`, `reply_type`, `result` (the reply without its native `timing` block, which `timing` already carries), `timing` (native timing phases plus `proto_decode_ms`, `payload_bytes`, `wire_bytes`, `proto_decode_error`; `native_frames`, the cumulative frame account, rides at most one row per 5 s). A payload over 64 KiB is replaced by a 4 KiB `preview` and its hash. Every call writes it; the hot-poll denylist applies to HTTP rows only. `nativeaccept/postmortem` reads the error and failure cases from `native_call` rows. |
| `native_request` | slow or outstanding marker only, written (durable) once a call has run past `bridge.slowCallMarker` (2 s): `tool`, `native_tool`, `arguments`. The completed `native_call` names it in `request`; a hung call leaves the marker last, and `trace` prints `(no reply recorded)` |
| `native_frame` | event row. `outcome` (`decoded`, `hit`, `miss`), `native_tool`, `frame`, `why` (miss), plus decoded-frame fields |
| `combat_order` (decision) | `target` pawn, `verdict` `applied`/`refused`, `reason` refusal, attrs `index`, `job` |
| `clock_step` (decision) | `verdict` step outcome (`admitted`, `deferred`, `refused`, `idle`, `failed`), `reason` the first window refusal or a stable word (`window_admitted`, `window_refused`, `deferred`, `no_window`, `step_error`), `target` the step's cause (`timer`, `wake`, `settled`, `full`, `live`), `dur_ms` step wall; attrs also `gate_wait_ms`, `journal_ms`, `window_ticks`, `stop`, `stop_latency_ms`, the pacing fields and `error`; attrs `reads`, `tools`, `schema_fetches`, `running`, `stop_pause_s` (read tally) |
| `dispatch` (decision) | one per run that acted. `target` action, `verdict` outcome (`completed`, `refused`, `failed`, `waiting`, `held`), `reason` cause or error class; attrs `attempt`, `stage`, `stage_after`, `receipt`, `running`, `stale`, `error`, `repeated`, `reads`, `tools`, `schema_fetches`. Level `WARN` for a failure other than a dependency wait. Written for a run that reached native (carries `receipt`, `running` and the read tally; `rimgovernor phases` counts only these) or whose outcome changed (`changed` true, `repeated` the unrecorded runs that restated the previous outcome); an unchanged run that never reached native writes nothing. `reason` is the stage word for a wait, `dependency`, `stale`, `error`, the first refusal, or `completed`; `refused` (all refusal reasons) rides in attrs. The cancelled-settle of an undispatched action is `failed`/`cancelled_settled` with `attempts` |

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
| `planner_step` (decision) | one per planner run. `target` planner, `verdict` outcome (`admitted`, `waiting`, `refused`, `unselected`, `failed`), `reason` the refusal or wait cause; attrs `proposals`, `edits`, `cause`, `admitted`, `running`, `reconciled`, `cleaned`, `deferred`, `retaken`, `combat`, `window_ticks`, `repeated`, `error`. Rounder targets `rounds` (`ok`/`reviewed` or `failed`/`error`, attr `partial`); the worker targets `worker_step`. Step journal failures target `reasons`, `waits` or `proposals` with `failed`/`journal_error`. |
| `admission` (decision) | `target` method, window or plan, `verdict` `admitted`/`refused`/`held`, `reason` the refusal (`critical_wave_budget`, a `ClockWindowReason`, a method refusal); attrs `concern`, `refused` (all reasons), `held_by`, `mode`, `work`, `combat_plan`, `hostiles`, `clock_state`, `window_ticks`, `wall_budget_ms`, `error` |
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
| `colony_stage` | event row. `stage`, `since`, `blocker`, `reason`, `held` |
| `build_tier` | event row. `tier`, `evidence` |
| `layout_plan` (decision) | `verdict` `planned`/`replanned`/`claimed`/`refused` (`reason` `no_room`: a replan left rooms unplaced, attr `unplaced`, once per distinct set), `reason` replan reason, attrs `colonists`, `summary`, `claims`, and on a replan `tomb_short`; `skipped`/`no_core` when the survey holds no core; a suite-claims change is `claimed` with target `suites`. |
| `layout_edit` (decision) | `target` zone, role, item or building, `verdict` `admitted`/`refused`/`abandoned`/`closed`/`proposed`, `reason` refusal code or detail; attrs `family` (`stockpile`, `field`, `building`), `kind`, `role` (stockpile create/delete),  `plan`, `crop`, `cells`, `moves`, `gain`, `owner`, `x`, `z` |
| `snapshot_skip` | decision, `WARN`. `target` the snapshot (`colony`, `combat`, `defense`, `layout`, `firebreak`, `shelter`), `verdict` `skipped`, `reason` `not_recorded`; attrs `error`, `tick` |
| `routine_skip` (decision) | `target` the subject, `verdict` `skipped`/`waiting`, `reason`; attrs `quest`, `script`, `detail`, `findings` |
| `placement_refused`, `foreign_held` | event rows, component `building-planner` (`rounds_room_reconcile.go`). `msg` only: `furniture cell refused: <def>@<x>,<z> blocked by <thing, ...>` (the native preview's blockers, `unreported` when it names none) and `foreign thing held: <def>@<x>,<z>:<reason>` (`casket`, `ancient_danger`, `not_deconstructible`). The same text is the room's wait key (`<room>_reconcile:blocked:...`, `<room>_reconcile:held:...`). Read by the shelter climate cases (`nativeaccept.FlightMessages`) |

### Defense, population and pawns

| Kind | Shape and fields |
|---|---|
| `defense_action` (decision) | `target` pawn, plan, incident, cell or tier, `verdict` `applied`/`refused`/`failed`/`waiting`, `reason` the refusal or issue; attrs `incident`, `plan`, `outcome`, `x`, `z`, `error`. An unbuilt tier emits `waiting` / `tier_unbuilt` when its census changes, targeting the tier with attrs `buildings`, `census`. |

### Other event families

| Kind | Shape and fields |
|---|---|
| `http_access` | event row, one per HTTP request. `method`, `path`, `status`, `dur_ms`, `bytes`, `long_lived`; 2xx GETs of `/api/state`, `/api/spectator/now`, `/api/routines`, `/api/health` and `/api/presentation/*` under 250 ms are dropped; errors, slow calls and every non-GET are kept. Pprof rows are written at finish with `long_lived: true` (`dur_ms` is the capture, not latency). Written by `httpapi.Config.Access`; component `httpapi`; level `WARN` for a 5xx |
| `fields_select` | decision, one per farm site-type selection (`RoundsFieldPlanner`). `target` the winning crop, `verdict` `selected`, `reason` the winning site kind (`outdoor`, `greenhouse-new`, `greenhouse-reuse`, `hydroponics`); attrs `cells`, `buildings`, `needed`, `urgent`, `candidates` (each `kind`, `crop`, `needed`, `cells`, `score`, `terms` {name: value}, `reason` for an unplantable one). Read by `nativeaccept/farmselect`. Component `clock-scheduler` |
| `recovery_batch` | decision, one per clearance planner step with queued recovery removals (`RoundsClearancePlanner`); it journals the batch the planner executes. `target` `clearance`, `verdict` `planned`/`held`/`failed`, `reason` the stage (`roof`, `removal`, `none`, or `roof_rules` for a failed row); attrs `targets`, `roof_cells`, `batch` (load ids), `held` (each `id`, `reason` a `RemoteHoldReason` word), `blocker` (the first joint roof-check word), `error`. Component `clock-scheduler` |
| `food_credit` | decision, one per ledger counter group when the credit factor moves by 0.1 or more or the group's state changes. `target` the group (`crop:<zone>`, `fish:<x,z>`, `forage:<def>`, `animal_product:<race>`), `verdict` `credited`, `reason` `window` (the trailing window moved it), `rebaseline` (a load held it) or `lost` (dropped ledger rows held it); attrs `expected`, `observed` (nutrition over the window), `factor`, `window_days`, `state` (`designated`, `delivering`). Component `routine`. Written by the food review (`rounds_food_plan.go`); `logview` prints the factor and state |
| `mod_log` | event row for each `rimgovernor.log` event from the mod, written by `bridge/modlog.go`. Row context: `component` `mod`, the mod's `tick`, `at` (the mod's time, so a replayed line keeps it) and `trace_id`/`span_id` from the event's `trace` (`<trace_id>/<span_id>`, the controller trace a main-thread hop carried); `level` `WARN`/`ERROR` for mod `warn`/`error`. Payload `seq` (mod ring order), `level`, `source` (the mod component, e.g. `watchdog`), `msg`, and when set `late` (written while nothing was subscribed, replayed on subscribe) and `suppressed` (lines the call site's rate limit dropped before this one). The C# helper is `RimGovernor.Host.Sdk.ModLog`: bounded ring of 512, 5 lines per call site per 10 s, reentrant writes dropped, never `Log.Error`, never blocks the game thread |
| `rule` | decision, one per native rule event ingested from the clock inbox (`clockPollEvents`, #2154). A firing: `verdict` `fired`, `reason` `prey_killed`, `target` the actor pawn; attrs `rule`, `target` (the prey), `params` (`job`, `radius`), `tick`, `cursor`. A lapsed lease: `verdict` `expired`, `reason` `lease_expired`, `target` `rules`; attrs `expires_at_tick`, `deactivated`, `cursor`. Reader: the launcher Log panel (`infoLogKinds`) and `rimgovernor log` |
| `log_overflow` | event row, level `WARN`: one summary of dropped diagnostics. `dropped`, `side` `mod` (the mod's ring overflowed while nothing was subscribed or the publisher lagged; `seq` is where the lines were) or `controller` (the reader goroutine's hand-off queue of 1024 was full) |

### Session and reader events

| Kind | Notes |
|---|---|
| `coverage` | First row of each run; free text by design. |
| `recording_gap` | Synthetic, produced by `TimelineReader` for a corrupt line or sequence discontinuity; never written. Fields `reason`, `file`, `line`, `before`, `after`. |
| `state_reset` | event row, level `WARN`, component `serve`: the service database came from another schema version and was moved aside (`path`, `aside`). Written once at startup. |

The flight recorder is the only log: the telemetry handler writes a row for a record that names a `kind`
and drops one that does not, there is no Debug level, and `telemetry/kinds_test.go` fails any `slog` call in
non-test code with no `kind`. stderr carries only the startup banner, parse errors, `flight recorder:` and
`Go service:` failures, the clock fault-injection notice and Go panics. A new emission adds its kind here with
a reader, or it is not added.

## Readers

`rimgovernor log`, `phases` and `trace`, the launcher's Problems and Log tabs,
spectator views and acceptance diagnostics consume these kinds.
`bridge.IsWorkerStep`, `bridge.IsWindowStop` and `bridge.StepFields` centralize
clock/worker interpretation. Combat `clock_stop` rows carry `event`; ordinary
stop-event rows do not. Acceptance readers use the retained flight bundle.

Add a kind only with its reader. Keep stable reason words and structured attrs
so filtering, repeat collapse and diagnostics do not depend on prose parsing.
