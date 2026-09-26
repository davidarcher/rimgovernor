# Measure controller throughput

[All docs](../../README.md) · [Developer guide](../README.md) · [Choose tests](choose-tests.md)

How to record what the controller spends its time on against a live game,
read the numbers back, and prove a change to the clock loop or the step
cache did what it claims. The tools are read-only over the recording: they
never touch the game or its authority.

## Record

`serve` appends one JSON line per native request, response, error and
internal sample to a flight recorder: `<profile>/flight/flight.jsonl` by
default (#299; the ring outlives each launch, the sequence continues
across launches and each row's `run` names the launch that wrote it),
`--flight-recorder <absolute path>` to record elsewhere (the acceptance
runner's per-case path), `--no-flight-recorder` to run without one.
Segments rotate beside the path (8 x 8 MiB); the reader picks them all
up. A running service also serves the ring over `GET
/api/telemetry/events` and `GET /api/telemetry/metrics` (see [the
dashboard](../architecture/dashboard.md#telemetry)).

```bash
go run ./cmd/rimgovernor serve --flight-recorder C:\path\to\run\flight-recorder.jsonl ...
```

Every row a phase report reads:

- `timing` on a native call: gate wait, GABS round trip, receipt decode and
  ProtoJSON decode, and (when the companion carries it) its own
  main-thread queue wait and execute time. `class` is the admission class
  the call took a bridge slot under (`control`, `observation` or `media`,
  #631), `gate_wait_ms` the wait for that slot, `queue_depth` and
  `class_queue_depth` how many calls (of any class, of its own) were
  waiting when it asked, and `native_queue_depth` how many hops were
  pending for the game thread when the companion queued this one.
- `timing.native_observation` and `timing.native_frames` on a native call
  (#642): the companion's own account of where that hop's main-thread time
  went and of the update intervals its frame recorder measured. Additive and
  optional -- a recording written against a companion that predates them
  carries neither, which the report shows as unknown, never as zero work.
- `native_cache_hit`: a read the scheduler's per-step read cache served
  without a round trip.
- `clock_step`: one row per `ClockScheduler.Step` with the round trips it
  still issued by tool, the step cache and cross-step `FactCache` parent
  hits, the reason the step ran, whether a clock stop woke it and the
  stop-to-step latency (#112), the wall-sized window it sized (#126), and
  its budgets against what it used: `budget` (wall, native ticks, reads),
  `critical_wave_ms`, `missed_cutoff` and `held_by` (#623).
- `clock_read_events` replies: the clock reads whose ticks and paused
  status give wall TPS and the paused fraction.

`serve --debug` (which every acceptance launch passes) prints the same
per-step tally on stderr (`[clock-scheduler] step reads: ...`) with the
cache's hit/miss/parent-hit counts, for a quick look without a recording.

## Service events

Every service log line is a structured record (`go/internal/telemetry`):
stderr renders it as `<time> tick=<n|-> <LEVEL> [<component>] <message>
k=v ...`, where `tick` is the game tick the scheduler or the clock poll last
read, so a line lines up with an evidence file, a flight row and the game
clock. The clock trace (`serve --debug`) is the DEBUG level of the same
log and reaches stderr only. A record that names an event `kind`
is also a flight-recorder row of that kind, in sequence with the `native_*`
rows, its attributes as the payload and `tick`, `level` and `component` in
the context; `rimgovernor phases` ignores them. The kinds:

- `scheduler_step`: one per change of a step's outcome (`step done` or
  `step failed: ...` at WARN): error, isolated planner failures, cause,
  admitted/running/reconciled/cleaned/deferred/combat, the window sized,
  and how many unlogged steps repeated the previous outcome.
- `admission_refused`: the admission tail held the window: the refusal
  reasons, mode, clock state and whether work remained.
- `scheduler_stop`, `authority_change`, `alert_row`: one per Stopped,
  AuthorityChanged and Alert event a committed poll page carried, with
  the event's cursor and native stamp.
- `worker_outcome`: one per change of an action's reconciliation outcome
  (WARN when the run failed): action, attempt, stage before and after,
  outcome text, error. `worker_dispatch` (the per-dispatch read tally)
  is published by the Worker's read tally as before.
- `routine_review`: one per committed routine review: revision, tick,
  goal count, the needs that declared an emergency, the food runway it
  read (`food_days`, when known) with the seasonal thresholds it held it
  to (`food_min_days`, `food_target_days`) and the growing calendar they
  came from (`season`, `day_of_year`, `growing_days_remaining`,
  `growing_days_until`, `non_growing_days`, when known).

## Traces

Every row's context carries a `trace_id` and a `span_id` (#298). A
scheduler step is a trace root: the bundle, the census and planner reads,
the admission, its `clock_step` tally and its `scheduler_step` event all
share its id, and the stderr line for a kinded record ends in
`trace=<id>`. The Worker's step is a span under the latest scheduler step
(`parent_id`) and each dispatch a span under that, so the rows a dispatch
leaves (`worker_dispatch`, `worker_outcome`, its native calls) join the
trace of the step that admitted the window it ran in. A poll and a
renewal are traces of their own; a row written outside any traced unit of
work (the coverage row, a dashboard read) gets a single-row trace, so no
row is unaddressable. Typed bridge calls send `trace` (`<trace_id>/<span_id>`)
beside `request`, and the companion echoes it as `timing.trace`, recorded
as `native_trace` in the response row's timing, so `queueMs`/`executeMs`
belong to the same trace as the service's own phases.

```bash
go run ./cmd/rimgovernor trace C:\path\to\run\flight-recorder.jsonl
go run ./cmd/rimgovernor trace [--json] C:\path\to\run\flight-recorder.jsonl <trace_id>
```

Without an id the command lists the traces (offset, span, row count,
tick, and the step or outcome message that names it), which is where to
find the step that took 4 s. With an id it renders that trace as a
waterfall: one line per row in sequence with its offset from the trace's
first row, the span it ran under (indented by nesting), and for a native
call its duration and phases (`gate`, `call`, `decode`, the companion's
`native queue`/`exec`) with the reply folded into the request line; a
kinded event shows its message and attributes.

```
trace 5c0e1f2a9b3d4e6f: 14 rows over 412.7ms, tick 12000, sequence 1032..1045
    at ms   dur ms  span      row
      0.0     11.9  5c0e1f2a  native rimgovernor/observations_read_bundle  gate 0.0 call 11.5 decode 0.2 native queue 0.4 exec 9.1
     12.5        -  5c0e1f2a  cache hit rimgovernor/observations_list_pawns
    230.1      5.0  90faecc0      native rimgovernor/operations_execute  gate 0.0 call 4.9 decode 0.0
    235.2        -  90faecc0      worker_dispatch action=... receipt=accepted running=true
    412.7        -  5c0e1f2a  scheduler_step "step done" admitted=true running=true window_ticks=150
```

## Report

```bash
go run ./cmd/rimgovernor phases [--json] C:\path\to\run\flight-recorder.jsonl
```

Text output (the `--json` form is `bridge.PhaseSummary`, the same fields in
snake case):

```
records 4211 (gaps 0, untimed calls 3), wall 118.4s
clock: 6012 ticks over 117.9s = 51.0 wall TPS (240 tick samples, 0 resets), paused 31/240 status samples
steps: 41, reads/step mean 2.3 max 9, cache hits/step 1.8, parent hits/step 4.1, window ticks mean 150 max 600 (target up to 2.0s) over 40 sized steps, by reason full=1 timer=4 wake=36
stops: 36 woke a step, stop->step latency mean 6.2ms max 14.0ms over 36 samples
observation: 118 hops with a capture account, capture p50 3.1 p95 11.4 p99 24.0 max 31.2 ms over 118, format p50 0.8 p95 2.9 p99 6.1 max 7.0 ms over 118, queue p50 0.4 p95 2.2 p99 9.8 max 14.1 ms over 412, exec p50 3.9 p95 14.0 p99 28.7 max 33.9 ms over 412
  412 formatting passes, 38.2 MiB returned, outcomes ok=117 failure=1
  section                        hops        ms    max ms       rows candidates
  planningWindow                   41     212.6      14.9       8104      15000
  colonyFacts                      41      64.1       3.2       1230          -
frames: 7012 updates over 118.1s = 59.4/s (hooked, 412 samples), max update 214.0ms, observation 4.1s (3.5% of update wall) over 118 hops, recorder 12.4ms, >16.7ms 402, >33.3ms 61, >100.0ms 4, >250.0ms 0
  worst update 4193: 214.0ms (observation 188.2ms, tick 12450, trace 5c0e1f2a/90faecc0)
  reads/step by tool
  observations_read_bundle                              1.0
  ...
native tool            wrapper  calls cached err total ms gate ms call ms queue ms exec ms decode ms proto ms avg KiB
```

### What each number means

Header (`PhaseSummary`):

| Field | Meaning |
| --- | --- |
| `records`, `gaps` | Rows read; `gaps` counts rotated segments missing between the first and last. |
| `untimed_calls` | Calls with no `timing` block (an older service or a call that failed before timing). |
| `wall_seconds`, `first_wall_time`, `last_wall_time` | Span of the recording. |

Clock (`clock`, from `clock_read_events` reply ticks):

| Field | Meaning |
| --- | --- |
| `ticks_advanced`, `wall_seconds`, `wall_tps` | Game ticks gained over the sampled span; wall TPS **includes paused time**, so a run that stops often reads low even if the game ran fast between stops. |
| `resets` | Tick decreases (load, rewind); excluded from the tick sum. |
| `native_paused_ms`, `native_running_ms` | Native's own paused account (#621): the stop->start gaps and start->stop spans its supervisor measured on a monotonic clock, as the difference between the first and last status samples, so a gap no sample observed still counts. `PausedFractionNative()` is paused over paused plus running: **the headline paused number**. Zero with `native_pause_samples` 0 against a native that predates it. |
| `paused_samples` / `clock_samples` | How many status samples found the clock stopped: a sampling diagnostic, not the measure, since the service issues most of its reads when the clock is stopped. |

Steps (`steps`, from `clock_step` rows):

| Field | Meaning |
| --- | --- |
| `steps` | `ClockScheduler.Step` calls that ran (held steps publish too). |
| `reads`, `max_reads`, `tools` | Native round trips the steps issued; the report shows the mean and max per step and the split by tool. A rising reads/step means a planner lost its cache. A steady step is one bundle; a reviewing step is the bundle (carrying the census families, #180), the admission bundle and the window start. |
| `schema_fetches` | Describe (`games_tool_detail`) round trips the step paid for a tool's first call, counted apart from `reads` (#180): a session's startup cost, not a step's. |
| `cache_hits` | Reads served by the per-step read cache (same tool and arguments within one step). |
| `gate_wait_ms` | Wall the step waited on the player gate before it could run (#593); the usual holder is the Worker's dispatch step, so a large value is a slow dispatch, not a slow planner. Absent when the step entered at once. |
| `journal_ms`, `max_journal_ms` | Wall the step's own obligation reads spent in the journal (attempt and epoch catalogs, review, current plan, active catalog), summed over the steps and the slowest step's (#634). It is bounded by the attempt tail maintenance keeps (`clockHistoryTail`, ~14 ms in-memory), not by the save's retired history; a value growing over a long save means a catalog read stopped using its index. |
| `parent_hits` | Reads served by the cross-step `FactCache` from the previous step's facts; `> 0` with a lower reads/step is the evidence that the cache is working. |
| `windows`, `window_ticks`, `max_window_ticks`, `max_window_target_secs` | The wall-sized colony windows (#126): how many ticks each window was budgeted and the wall target it was sized to. |
| `reasons` | Steps by cause: `timer` (the cadence fired with nothing captured; planners run only when one is due on the queue, #625), `wake` (committed journal evidence — latched outcomes, an invalidated family, an authority change — shortcut the backoff), `settled` (the previous step reconciled or cleaned an epoch, so every planner re-plans), `full` (every planner ran, including the `FullStepEvery` promotion of a timer step). A healthy running clock is mostly `wake`; mostly `timer` means the poll loop is not seeing events. |
| `sections`, `waiting` | On a subset step's `clock_step` row (#625): the census sections the step's planners declare and read at cadence (absent when every planner ran), and the planners skipped because each still waits on the open work it reported. A wake that lists few `sections` and a falling reads/step is the routing working. |
| `stops.count` | Wake steps a clock stop caused: the game paused and the controller stepped to decide the next window. |
| `stops.mean_latency_ms`, `max_latency_ms`, `latency_samples` | Stop-to-step latency: from the companion's `observedAtUnixMs` on the Stopped event to the step beginning. This is the stall a paused colony waits on the controller; it should sit well under one `StepInterval`. Rows without a native stamp count in `count` but not in the samples. |

Per tool (`tools`, one row per native tool; the `ms` columns are sums, the
text report divides by `calls`):

| Column | Meaning |
| --- | --- |
| `calls`, `cached`, `err` | Round trips, per-step cache hits that avoided one, and errors. |
| `total ms` | End to end inside the service. |
| `gate ms` | Waiting for the bridge gate (another call in flight: the host runs one tool at a time). |
| `call ms` | The GABS round trip, including the native queue and execute time. |
| `queue ms`, `exec ms` | The companion's own split for `rimgovernor/*` tools whose single main-thread hop goes through `ProtoBoundary.OnMainThread`: waiting for the main thread, then running on it. Means over the calls that carried it; `-` means absent (older companion or multi-hop media capture), not zero. |
| `decode ms`, `proto ms` | Receipt decode and ProtoJSON decode in the service. |
| `avg KiB` | Mean response size. |

Describe (`games_tool_detail`) round trips are listed separately: paid once
per method per GABS session, not per call. Sub-millisecond phases can read 0
on Windows' coarse monotonic clock.

Observation capture (`observation`, from each hop's `native_observation`):

| Field | Meaning |
| --- | --- |
| `hops` | Replies that carried a capture account. `0` with a `queue`/`exec` distribution present means the recording predates the account: the split is unknown, not zero. |
| `capture`, `format` | Where the hop's main-thread time went: reading game state, and ProtoJSON formatting plus the UTF-8 size checks that precede it. Each is a `Quantiles`: `samples`, `p50_ms`, `p95_ms`, `p99_ms`, `max_ms`, `sum_ms`, **nearest rank** -- with `n` samples sorted ascending the `q` quantile is the sample at 1-based index `ceil(q*n)`, so a small sample set names an actual observed hop rather than an interpolated one. `samples` of 0 prints `unknown`. |
| `queue`, `exec` | The same distributions over the companion's `queueMs`/`executeMs` (#631), which every timed reply carries, so they cover more hops than `capture` does. `exec` is main-thread elapsed work; `queue` is the wait for the main thread. When the capture moves off the main thread these stay distinct: `exec` remains the main-thread leg. |
| `format_passes`, `payload_bytes` | Main-thread formatting passes paid for (a size check that reformats counts again) and the UTF-8 bytes actually returned. |
| `encode_hops`, `encode_queue`, `encode`, `encode_format_passes`, `encode_format_ms` | Detached replies (#644, the scheduler bundle): captured on the main thread, then formatted on a bounded encoder worker. Per hop, the wait for the worker and its wall, plus the worker's own formatting passes and time. None of it is in `exec`, and for these hops `format`/`format_passes` count only formatting still left on the main thread. A reply without the block formatted on the main thread, so older recordings read as before. The text report prints an `off-thread encode` line only when a reply carried the block. |
| `dropped_sections` | Sections the companion dropped to stay inside the 1 MiB envelope. |
| `sections` | Per requested section: `hops`, `ms`, `max_ms`, `rows` returned and `candidates` it could have returned. `candidates` is absent (`-`) where the section has no candidate set to compare against, never 0 -- only `colonistPawns` (the ids asked for) and `planningWindow` (the requested rectangle's cells) know theirs. |
| `outcomes` | Hops by outcome: `ok`, `failure`, `unavailable`, `error` (a hop that threw is accounted, not dropped). |

Frames (`frames`, from `native_frames`, cumulative for the loaded game
session and differenced between the recording's first and last sample):

| Field | Meaning |
| --- | --- |
| `hooked`, `samples` | Whether the recorder installed, and how many replies carried the block. `samples` 0 omits the whole section. |
| `updates`, `elapsed_ms`, `max_interval_ms` | Update-to-update intervals on a monotonic clock, and the widest any sample reported. **These are Unity update intervals, not GPU presentation intervals**: a stalled present or a dropped frame is not distinguishable here, and an unrendered batch-mode launch still updates. |
| `observation_ms`, `observations` | Main-thread wall that ran observation hops inside those updates, and how many ran. `ObservationShare()` is its share of the update wall -- not of one update, since a hop can outlast one. |
| `cancelled_hops` | Hops cancelled while queued: work never done, kept apart from work that ran. |
| `recorder_ms` | The recorder's own measured cost, so its overhead is visible rather than assumed. |
| `slow` | Per declared threshold (16.7, 33.3, 100, 250 ms) how many intervals exceeded it. |
| `worst` | The widest intervals a bounded 8-entry ring kept for the session, each with its update id, the observation work charged to it, the tick and the trace of its most expensive observation -- the link from a hitch to a request. |

Intentional clock pauses are **not** here: a paused game simply stops
ticking while its updates continue. The stop time and its reasons stay in
the clock block (`native_paused_ms`, `paused_fraction_native`) and in
`stop_reasons`, so a wide interval is frame blocking and a large
`paused_ms` is the governor deliberately holding the clock.

### Reading a report

- Slow steps with low reads/step: look at `call ms` versus `queue ms` for
  the dominant tool — a large gap between them is GABS transport, a large
  `queue ms` is a busy game main thread.
- Slow steps with high reads/step: a planner is re-reading; check
  `parent_hits` and the by-tool split.
- Low wall TPS with a high paused fraction: the game waits on the
  controller; check `stops` latency and whether `reasons` is `timer`-heavy.
- Low wall TPS with a low paused fraction: the game itself is slow at that
  speed; the controller is not the bottleneck.
- A slow bundle: compare `capture` with `format` for the same hops. A high
  `capture` p95 with a dominant row in `sections` is a read (that section);
  a high `format` p95 with `format_passes` above `hops` is encoding, paid
  twice by a size check. A detached bundle formats once on its encoder
  (`encode_format_passes` equal to `encode_hops` unless the envelope forced
  a drop), so its `format` is near zero; a large `encode_queue` is the
  encoders saturated, which refuses a bundle rather than delaying the main
  thread. `queue` p95 well above `exec` is a busy main
  thread, not an expensive observation.
- A hitch: read `frames` `slow` counts and `worst`. An interval whose
  `observation_ms` is most of its `interval_ms` was blocked by that hop, and
  its `trace` names the request; an interval with little observation work is
  the game or the renderer, and the observation path is not the cause.

## Case timeline

`dashboard/timeline.html` draws one acceptance case's output directory on
a single axis: the clock windows (`started` to `stopped`, with the stop
reason and the paused strip between status samples), the scheduler steps
with their elapsed time and the stop→step latency of a woken step, the
admissions and refusals (`clock_start`/`clock_renew` receipts, the
`EvaluateClockWindow` refusals and pause-bound holds from the aligned
stderr blocks, worker dispatches), the native events at the companion's
own stamp with a dashed connector to the reply that delivered them, every
native round trip as a span (a held long poll is the outlined bar), and the
case's `started_at`/`finished_at`/boot from `result.json`. A restarted
service (`service-2/`) is a second launch on the same axis.

```bash
pnpm --dir dashboard dev
# open http://127.0.0.1:5173/timeline.html and pick the case directory
```

The built page ships with the dashboard assets (`dist/timeline.html`, served
by `serve` at `/timeline.html`). It reads the picked files in the browser
and uploads nothing. Switch the axis to *ticks advanced* to collapse paused
wall time and compare windows by game time; hover shows the row, click
pins it. Harness evidence (`NNNN-*.json` and `service*/http-NNNN.json`,
one run-wide sequence) is placed by its `observed_at` stamp; a file
without one is listed beside the plot. The parsers live in
`dashboard/src/features/timeline/`.

## Profile

`serve --pprof` (off by default) serves `net/http/pprof` under
`/debug/pprof/` on the controller's own listener, local-only like every
other route. The CPU route keeps pprof's contract (`GET
/debug/pprof/profile?seconds=N`) and adds an early end: `DELETE
/debug/pprof/profile` stops the capture in flight and the pending `GET`
completes with the profile so far, so a profile started for a whole run
still arrives when the run ends sooner.

```bash
go tool pprof -http=: C:\path\to\run\<area>\<case>\service\cpu.pprof
```

The acceptance runner does this for every service it launches
(`cpu.pprof` over the case's budget, `heap.pprof` at stop; see
[choose-tests](choose-tests.md#adding-a-case));
`RIMGOVERNOR_ACCEPT_PPROF=0` opts out.

## Across clock speeds

The controller must make the same decisions per game tick and react to a
stop within one step interval at every speed (#10). Two checks:

**Unit (seconds, no game):**

```bash
go test ./internal/buildingruntime -run TestClockSpeedMatrix
```

Drives the scheduler and worker against a wall-clock fake native at tick
multipliers 1, 3, 6, 15 and 150 (Normal through the uncapped test
acceleration) and asserts an identical window-decision sequence per game
tick across multipliers, that every budget stop wakes a step within
`StepInterval` (#112), and that `paused_fraction_native` read from the
status agrees with the fake's known stop and start times to within one
step interval of the wall time (#621). Run it first after any scheduler,
`WakeSignal` or worker change.

**Native (about 2 minutes per speed):**

```bash
go run ./internal/nativeaccept/cmd/acceptance run speedmatrix/plain -root <abs root> -rimgovernor <abs path to rimgovernor.exe>
```

Needs a `ThroughputFixture` build; both profiles admit the uncapped case
(a rendered run reports a lower wall TPS for it, since every frame also draws). One staged colony is reloaded per row and served with the `haul` and
`work` families for a 6000-tick budget, or until the stage runs out of work
(every wall plan completed, no storage deficit pending; the clock admits no
window after that, #210); each governed row runs `serve` with the
flight recorder and the case reduces the recording with `SummarizePhases`
and `SummarizeStops`. The default matrix (`DefaultSpeedMatrix`)
is `Normal,Fast,Superfast,Ultrafast,uncapped,regulated,governor-off,viewer`;
`RIMGOVERNOR_SPEED_MATRIX=uncapped,viewer` in the runner's environment
narrows a run to the rows named.
The last two separate the governor's cost from the rest (#621):

| Row | What runs |
| --- | --- |
| `governor-off` | The same save and renderer played natively at the uncapped speed with no controller attached: the simulation ceiling. Nothing is submitted, so the row reports `ticks_advanced`, `wall_seconds` and `wall_tps` (`governor_off: true`, zeros for the governor's counters) and is outside the outcome comparison. |
| `viewer` | The uncapped governed row with one dashboard client attached for the whole run: a render demand, a screen video lease renewed every 10 s and the WebSocket stream drained at the server's default cadence, as the dashboard tile does, plus a second socket on the same source that is never read (a stalled or hidden tab, #631). Its `viewer` block reports `frames`, `bytes`, `frames_per_second`, `connects`, `stalled_connects` and `unavailable` (a game that cannot capture, such as batch mode, answers the lease unsupported and the row records that). Compare its `wall_tps` with `uncapped` for the viewing overhead; the stalled socket must cost neither. |

`result.json` carries, per row under `speed_metrics` (`metrics` is the
flat cost block every result carries, #297):

| Key | Meaning |
| --- | --- |
| `wall_tps`, `budget_wall_tps` | The phase report's clock numbers, including paused time on every row; `budget_wall_tps` is `ticks_advanced` over the wait's own wall time. |
| `paused_ms`, `running_ms`, `paused_fraction_native` | **The headline paused number**: native's own account of the supervisor's stop->start gaps and start->stop spans on a monotonic clock, reset when the loaded game changes so the reload between rows is not counted; `native_pause_samples` is how many status replies carried it (zero against an older native). This is what `max_paused_fraction` bounds where it was sampled. |
| `probe_ms`, `digest_ms`, `digest_share`, `probes`, `digests` | The supervisor's probe path split (#626), native's own main-thread account between the same first and last status samples: `probe_ms` is the hazard probe (letters, messages, alerts, pawns) over `probes` passes, `digest_ms` the fact-change digests (research, world, conditions, zones) over `digests` passes, `digest_share` the digests' share of the two. The digests run on their own cadence (600 ticks, or sooner when a zone, research, condition or faction hook marks them dirty), never inside the probe. |
| `max_probe_tick_gap`, `hazard_gaps` | The widest tick gap between consecutive hazard probes the session saw (the probe is tick-paced every 30 ticks at every speed, and wall-paced every 100 ms per frame), and per hazard class its declared bound, the widest gap observed and whether a direct game hook backs it: [hazard detection bounds](../architecture/hazard-detection-bounds.md). |
| `paused_fraction`, `paused_samples`, `clock_samples` | The status-sample ratio (`paused_fraction_sampling` says so in the row): a sampling diagnostic, not the measure; it over-represents pauses because the service reads mostly while stopped. |
| `stop_latencies` | One row per stop, correlated across the two processes by cursor only (no Unix time of one process is subtracted from the other's): `detect_ticks` occurrence to native detection where the stop carries an occurrence tick (a colonist injury's wound age), `stop_ticks` detection to the tick the clock stopped at, `observe_ms` native's own age of the stop row when the reply carrying it was composed plus the reply's transport residual (`call_ms` less the native queue and execute legs), `readmit_ms` the service's wall time from that reply to the next accepted `clock_start`. |
| `steps`, `reads_per_step`, `cache_hits`, `parent_hits` | As above. |
| `window_ticks_mean`, `window_ticks_max`, `window_target_secs_max` | The wall-sized windows at that speed; ticks per window should scale with the multiplier while the wall target stays put. |
| `stops`, `budget_stops`, `reactive_stops`, `stop_reasons`, `budget_stops_per_6000_ticks` | Stops classified from the `clock_read_events` replies: budget stops (the window's ticks ran out) versus reactive ones (a watch latch, a letter, a requested pause). Budget stops per 6000 ticks is the comparable rate across speeds. |
| `stop_latency_mean_ms`, `stop_latency_max_ms` | From the companion's `observedAtUnixMs` to the reply reaching the service (transport latency; the phase report's `stops` adds the step scheduling on top). |

The case fails when a required row has no metrics row or one that
advanced no ticks, or a compared row has no outcome (`row_problems` names
each; `SpeedRowProblems` is the boundary, since `CompareOutcomes` and
`CheckSpeedMetrics` accept an empty matrix), on pawn outcomes that differ
by more than 1 across speeds, an unsuccessful plan stage, or nothing
hauled or built; the `max_paused_fraction` and `min_ultrafast_tps_ratio`
thresholds are written to the report and checked at zero, so they are
reported, not enforced.

## Observation baseline

`speedmatrix/observations` (#642) is the repeatable rendered workload the
observation-cost numbers are read against, registered in the same matrix
tier and reusing the speed matrix's staging, rows and reporting:

```bash
go run ./internal/nativeaccept/cmd/acceptance run speedmatrix/observations -root <abs root> -rimgovernor <abs path to rimgovernor.exe>
```

The committed `RimGovernor-tribal8-baseline` colony -- eight tribal
colonists, the map's wildlife, its loose items and its standing buildings --
with `test/throughput_prepare` applied on top, staged once and reloaded per
row. It always runs windowed (`Rendered`), since update intervals are only
a player's intervals when the game draws and the viewer row's video capture
needs `Find.Camera`. Three rows, at one clock speed and one tick budget:
`governor-off` (no controller attached, the ungoverned update-interval
ceiling; its accounts come off its own tick reads' reply envelopes, since
nothing records a flight timeline), `uncapped` (the ordinary governed run)
and `viewer` (the same governed run with one dashboard client streaming
video). Every row asks for the same speed and the same staged work, so the
difference between rows is observation and viewing cost, not a different
workload.

Each row's `speed_metrics` entry carries the full `observation` and `frames`
blocks documented above, plus flat headlines for comparing rows:
`observation_hops`, `capture_ms`, `format_ms`, `capture_p95_ms`,
`format_p95_ms`, `queue_p95_ms`, `execute_p95_ms`, `format_passes`,
`payload_bytes`, `frame_updates`, `frame_updates_per_second`,
`frame_max_interval_ms`, `frame_observation_share`, `frame_recorder_ms`,
`frame_cancelled_hops`. The run's `provenance` block records what the
numbers were measured on: revision, host (os, arch, cpus), the mod set the
launch activated, the launch arguments (resolution and window mode), the
rows, the tick budget, the stage, the camera (the save's own, never
commanded), what the warmup excludes and what the measured interval is. The
world seed, the save and its hash and the fixture hash are the report's own
`world` block; the installed build's hashes are `package_files`.

### Recorded baseline

Revision `64a41a9d`, windowed 1280x720 on a 32-thread Windows host, four
active mods (RimWorld, Harmony, RimBridgeServer, RimGovernor), the three
rows at one clock speed over the same tick budget:

| | governor-off | uncapped | viewer |
| --- | --- | --- | --- |
| wall s | 13.3 | 16.3 | 14.2 |
| hops with a capture account | 7 | 122 | 125 |
| capture ms / format ms | 0.0 / 0.8 | 224.5 / 20.8 | 169.0 / 18.9 |
| capture p95 / format p95 ms | 0.00 / 0.51 | 0.79 / 0.83 | 0.50 / 0.56 |
| queue p95 / exec p95 ms | 84.5 / 0.9 | 81.6 / 183.9 | 80.3 / 150.9 |
| returned bytes | 1.5 KB | 411 KB | 440 KB |
| updates (per s) | 208 (15.9) | 319 (17.6) | 276 (18.3) |
| max update interval ms | 98.4 | **1067.7** | **825.4** |
| observation share of update wall | 0.003% | 34.6% | 31.6% |
| intervals >100 / >250 ms | 0 / 0 | 23 / 9 | 27 / 4 |
| recorder overhead ms | 0.61 | 1.04 | 0.76 |

Updates run at 16-18/s, not 60, because the row asks for an accelerated
clock and each update carries roughly a hundred ticks; the intervals are
still update-to-update wall, so they are directly comparable across rows.

The reported hitch reproduces on this workload, and the account attributes
it: the ungoverned row never blocks an update past 98 ms and charges
essentially no observation work to any of them, while both governed rows
block an update for most of a second with the observation work accounting
for nearly all of that interval (1067.7 ms of which 1050.7 is observation;
825.4 of which 762.9). The cost is a small number of very expensive hops,
not a broad tax: `capture` p95 is under 1 ms while the `emergency` section
alone spends 199.3 ms (uncapped) and 160.9 ms (viewer) in a single hop, and
`exec` p95 of 184/151 ms against a `format` sum of ~20 ms places the wall in
reading the colony rather than in encoding. The viewer row is not
measurably worse than the plain governed row, so video streaming is not a
contributor here.

To isolate emergency-only work from a full bundle, narrow the rows with
`RIMGOVERNOR_SPEED_MATRIX` and read the `sections` split, which already
separates `emergency` from every other family: the normal controller ships
no diagnostic bypass for this.

Isolate the accounting itself, with no game, through the contract probe:

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-observation-work
```

Every clock there is supplied, so the intervals, the slow-threshold counts
and the millisecond conversions are exact and no assertion measures the
machine it runs on; it also pins the storage bounds (the 48-section cap per
hop, the 8-entry worst-interval ring) and the session reset.

Rerun the native check when the clock scheduler, the step cache or the
Ultrafast/acceleration path changes; the unit check on every scheduler
change; the observation baseline when the capture, encoding or main-thread
admission path changes.
