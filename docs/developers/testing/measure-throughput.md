# Measure controller throughput

[All docs](../../README.md) · [Developer guide](../README.md) · [Choose tests](choose-tests.md)

Record what the controller spends its time on against a live game, read the
numbers back, and prove a change to the clock loop or the step cache did what
it claims. The tools are read-only over the recording: they never touch the
game or its authority. Field-level definitions live in the code
(`bridge.PhaseSummary`, `bridge.SummarizePhases`); this page holds only the
rules for reading them.

## Record

`serve` appends one JSON line per native request, response, error and internal
sample to a flight recorder: `<profile>/flight/flight.jsonl` by default (the
sequence continues across launches; each row's `run` names its launch), or
`--flight-recorder <absolute path>` (the acceptance runner's per-case path).
Segments rotate beside the path (8 x 8 MiB); the reader picks them all up.

```bash
go run ./cmd/rimgovernor serve --flight-recorder C:\path\to\run\flight-recorder.jsonl ...
```

Row kinds a phase report reads:

- native call rows carry `timing`: gate wait (`class` is `control`,
  `observation` or `mirror`), bridge round trip, receipt and reply decode, and,
  when the companion carries it, its main-thread queue and execute time plus
  `native_observation` (capture/format account) and `native_frames` (update
  intervals). Absent blocks read unknown, never zero.
- `native_frame_hit`: a read the snapshot frame stream served without a round trip.
- `clock_step`: one row per `ClockScheduler.Step` (round trips by tool, reason,
  stop-to-step latency, the window sized, budgets against use: `budget`,
  `critical_wave_ms`, `missed_cutoff`, `held_by`).
- `clock_read_events` replies: the ticks and paused status behind wall TPS and
  the paused fraction.

`serve --debug` (every acceptance launch) prints the per-step tally on stderr
(`[clock-scheduler] step reads: ...`) for a quick look without a recording.

## Service events

Every service log line is a structured record (`go/internal/telemetry`):
`<time> tick=<n|-> <LEVEL> [<component>] <message> k=v ...`. A record naming an
event `kind` is also a flight row of that kind (`phases` ignores them).
Kinds: `scheduler_step` (one per change of a step's outcome), `planner_step` (one per planner run), `admission`,
`scheduler_stop`, `authority_change`, `alert_row`, `worker_outcome`,
`worker_dispatch`, `rounds_review`. Grep the emitting code for each one's
attributes.

## Traces

Every row's context carries `trace_id` and `span_id`. A scheduler step is a
trace root (bundle, census, planner reads, admission, its `clock_step` and
`scheduler_step` rows); the Worker's step and each dispatch are child spans, so
`worker_dispatch`, `worker_outcome` and a dispatch's native calls join the trace
of the step that admitted their window. Typed bridge calls send `trace`
(`<trace_id>/<span_id>`); the companion echoes it so `queueMs`/`executeMs`
belong to the same trace.

```bash
go run ./cmd/rimgovernor trace C:\path\to\run\flight-recorder.jsonl
go run ./cmd/rimgovernor trace [--json] C:\path\to\run\flight-recorder.jsonl <trace_id>
```

Without an id it lists traces (find the step that took 4 s); with one it
renders a waterfall of rows with offsets, spans and per-call phases (`gate`,
`call`, `decode`, native `queue`/`exec`).

## Reading the rows

```bash
go run ./cmd/rimgovernor log --profile C:\path\to\profile --level WARN --since-run
go run ./cmd/rimgovernor log [--kind K] [--component C] [--tick a..b] [--trace ID] [--follow] [--json] C:\path\to\flight.jsonl
```

`rimgovernor log` prints the diagnostic stream (`--profile` resolves
`<profile>/flight/flight.jsonl`; rotated segments are read oldest first and a
`recording_gap` always shows). `--level` is a minimum, `--since-run` keeps the
newest launch. Decision rows (`verdict`, `reason`, `target`) that repeat collapse
into one line, `x N, first/last tick a/b`, keyed on (kind, component, verdict,
reason, target); other rows print one line each. `--follow` streams new rows
uncollapsed. `--json` is one entry object per line. The reader library is
`internal/logview`; [flight rows](../contracts/flight-rows.md) is the contract.

## Report

```bash
go run ./cmd/rimgovernor phases [--json] C:\path\to\run\flight-recorder.jsonl
```

Sections: header, `clock`, `steps`, `stops`, `observation`, `frames`,
reads/step by tool, per-native-tool table. `--json` is `bridge.PhaseSummary`.

Traps in the numbers:

- Wall TPS **includes paused time**: a run that stops often reads low even if
  the game ran fast between stops.
- The headline paused number is `paused_fraction_native` (native's own
  stop->start / start->stop account on a monotonic clock). `paused_samples` /
  `clock_samples` is a sampling diagnostic that over-represents pauses, because
  the service reads mostly while stopped.
- `step reasons`: a healthy running clock is mostly `wake`; mostly `timer`
  means the poll loop is not seeing events. `timer` runs only planners due on
  the queue, `wake` is committed journal evidence, `settled` and `full` re-plan
  everything, `live` plans under a running window.
- `stops.*latency` is the stall a paused colony waits on the controller; it
  should sit well under one `StepInterval`. Rows without a native stamp count
  but are not sampled.
- `journal_ms` growing over a long save means a catalog read stopped using its
  index. `gate_wait_ms` large means a slow Worker dispatch, not a slow planner.
- Per tool, `call ms` includes native queue and execute time; `queue ms`/`exec
  ms` are means over calls that carried them, `-` means absent (older
  companion), not zero. Sub-millisecond phases can read 0 on Windows' coarse
  monotonic clock.
- Observation quantiles are **nearest rank** (`ceil(q*n)`), so a small sample
  names an observed hop. `candidates` is `-` where a section has no candidate
  set (only `planningWindow` has one).
- Frame `updates` are Unity update intervals, not GPU presentation intervals;
  an unrendered batch-mode launch still updates. Intentional clock pauses are
  not frame stalls: a paused game keeps updating.
- A frame-tail quantile is its histogram bucket's upper edge (overstates by at
  most one bucket).

### Reading a report

- Slow steps, low reads/step: compare `call ms` with `queue ms` for the
  dominant tool. A large gap is transport; a large `queue ms` is a busy game
  main thread.
- Slow steps, high reads/step: a planner is re-reading; check `parent_hits`
  and the by-tool split.
- Low wall TPS with a high paused fraction: the game waits on the controller;
  check `stops` latency and whether `reasons` is `timer`-heavy. With a low
  paused fraction the game itself is slow; the controller is not the bottleneck.
- Slow bundle: compare `capture` with `format`. High `capture` p95 with a
  dominant `sections` row is that read; high `format` p95 with `format_passes`
  above `hops` is encoding paid twice by a size check. A detached bundle
  formats once on its encoder worker (`encode_*`), so its `format` is near
  zero; a large `encode_queue` is saturated encoders, which refuse a bundle
  rather than delay the main thread. `queue` p95 well above `exec` is a busy
  main thread, not an expensive observation.
- Hitch: read `frames` `slow` counts and `worst`. An interval whose
  `observation_ms` is most of its width was blocked by that hop and its `trace`
  names the request; an interval with little observation work is the game or
  the renderer.

## Profile

`serve --pprof` (off by default) serves `net/http/pprof` under `/debug/pprof/`
on the controller's own local-only listener. `DELETE /debug/pprof/profile`
stops a capture in flight and the pending `GET` returns the profile so far.

```bash
go tool pprof -http=: C:\path\to\run\<area>\<case>\service\cpu.pprof
```

The acceptance runner captures `cpu.pprof` and `heap.pprof` for every service
it launches (see [choose-tests](choose-tests.md#adding-a-case));
`RIMGOVERNOR_ACCEPT_PPROF=0` opts out.

## Across clock speeds

The controller must make the same decisions per game tick and react to a stop
within one step interval at every speed.

**Unit (seconds, no game)** - run first after any scheduler, `WakeSignal` or
worker change:

```bash
go test ./internal/buildingruntime -run TestClockSpeedMatrix
```

It drives the scheduler and worker against a wall-clock fake native at tick
multipliers 1, 3, 6, 15 and 150 and asserts an identical window-decision
sequence per game tick, every budget stop waking a step within `StepInterval`,
and `paused_fraction_native` agreeing with the fake's stop/start times within
one step interval.

**Native (about 2 minutes per speed):**

```bash
go run ./internal/nativeaccept/cmd/acceptance run speedmatrix/plain -root <abs root> -rimgovernor <abs path to rimgovernor.exe>
```

Needs a `ThroughputFixture` build. One staged colony is reloaded per row and
served with the `haul` and `work` families for a 6000-tick budget, or until the
stage runs out of work. Rows are `DefaultSpeedMatrix`
(`Normal,Fast,Superfast,Ultrafast,uncapped,regulated,governor-off`);
`RIMGOVERNOR_SPEED_MATRIX=uncapped,governor-off` narrows a run.
`governor-off` plays the same save natively at the uncapped speed with no
controller attached (the simulation ceiling): it reports only `ticks_advanced`,
`wall_seconds`, `wall_tps` and is outside the outcome comparison.

`result.json` carries per row `speed_metrics` (the keys are defined in
`go/internal/nativeaccept/speedmatrix.go`). Key rules:

- `paused_fraction_native` (with `paused_ms`/`running_ms`, reset on reload) is
  the paused number; `paused_fraction` is the sampling diagnostic.
- `budget_wall_tps` is `ticks_advanced` over the wait's own wall time.
- `window_ticks_*` should scale with the multiplier while
  `window_target_secs_max` stays put; `budget_stops_per_6000_ticks` is the
  comparable stop rate across speeds.
- `stop_latencies` correlate the two processes by cursor only (no Unix time of
  one is subtracted from the other's).
- `hazard_gaps` names each hazard class's declared bound, widest observed gap
  and direct-hook backing: [hazard detection
  bounds](../architecture/hazard-detection-bounds.md).

The case fails when a required row has no metrics row or advanced no ticks, or
a compared row has no outcome (`row_problems`; `SpeedRowProblems` is the
boundary), on pawn outcomes differing by more than 1 across speeds, an
unsuccessful plan stage, or nothing hauled or built. `max_paused_fraction` and
`min_ultrafast_tps_ratio` are written to the report and checked at zero:
reported, not enforced.

## Observation baseline

`speedmatrix/observations` is the repeatable rendered workload that
observation-cost numbers are read against; it reuses the speed matrix's staging
and reporting:

```bash
go run ./internal/nativeaccept/cmd/acceptance run speedmatrix/observations -root <abs root> -rimgovernor <abs path to rimgovernor.exe>
```

It loads the generated `RimGovernor-tribal8-baseline` colony with
`test/throughput_prepare` applied, always windowed (`Rendered`, since update
intervals are a player's only when the game draws), one clock speed and one
tick budget. Rows: `governor-off` (ungoverned update-interval ceiling; its
accounts come off its own tick reads' envelopes), `uncapped` (ordinary governed
run), `observation-load` (the `uncapped` row plus three concurrent readers
polling `/api/state` every 250 ms; it adds `readers`, and a load row with no
successful read fails) and `player`. Every row asks for the same speed and
staged work, so differences are observation cost.

Each row's `speed_metrics` carries the full `observation` and `frames` blocks
plus flat headlines (`observation_hops`, `capture_p95_ms`, `format_p95_ms`,
`queue_p95_ms`, `execute_p95_ms`, `frame_*`, `observed_frame_*`,
`encode_*_p95_ms`); `provenance` records the revision, host, mod set, launch
arguments, rows, tick budget, stage and camera, and the report's `world` block
the seed, save and fixture hashes. `observed_frame_*` histograms count only
updates that ran main-thread observation work: a tail living there is
observation-caused, one that does not is not.

Rules:

- The case refuses an incomplete report: a row without interval samples (no
  frame hook or older companion) or a governed row without observation hops
  fails as `observation report incomplete`, never reads as zero cost.
- Thresholds come from a baseline run of this case on a declared runner, never
  from the candidate being judged; a unit test does not fail on a busy CI.
- Stale-action safety is dependency-scoped: a proposal carries the section
  versions it planned from, and a changed relevant section refuses it once with
  the section named. A cell change outside the held planning window moves
  neither its value nor its version (`TestApplyRectangle`). Cell-level
  preconditions are revalidated natively on apply.
- To isolate emergency-only work from a full bundle, narrow the rows with
  `RIMGOVERNOR_SPEED_MATRIX` and read the `sections` split; there is no
  diagnostic bypass.

Isolate the accounting itself, with no game, through the contract probe (all
clocks supplied; it also pins the 48-section per-hop cap, the 8-entry
worst-interval ring and the session reset):

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-observation-work
```

Rerun the native check when the clock scheduler, step cache or
Ultrafast/acceleration path changes; the unit check on every scheduler change;
the observation baseline when the capture, encoding or main-thread admission
path changes.
