# Measure controller throughput

[All docs](../../README.md) · [Developer guide](../README.md) · [Choose tests](choose-tests.md)

How to record what the controller spends its time on against a live game,
read the numbers back, and prove a change to the clock loop or the step
cache did what it claims. The tools are read-only over the recording: they
never touch the game or its authority.

## Record

`serve` takes `--flight-recorder <absolute path>` and appends one JSON line
per native request, response, error and internal sample (opt-in; off by
default). Segments rotate beside the path; the reader picks them all up.

```bash
go run ./cmd/rimgovernor serve --flight-recorder C:\path\to\run\flight-recorder.jsonl ...
```

Every row a phase report reads:

- `timing` on a native call: gate wait, GABS round trip, receipt decode and
  ProtoJSON decode, and (when the companion carries it) its own
  main-thread queue wait and execute time.
- `native_cache_hit`: a read the scheduler's per-step read cache served
  without a round trip.
- `clock_step`: one row per `ClockScheduler.Step` with the round trips it
  still issued by tool, the step cache and cross-step `FactCache` parent
  hits, the reason the step ran, whether a clock stop woke it and the
  stop-to-step latency (#112), and the wall-sized window it sized (#126).
- `clock_read_events` replies: the clock reads whose ticks and paused
  status give wall TPS and the paused fraction.

`RIMGOVERNOR_CLOCK_DEBUG=1` prints the same per-step tally on stderr
(`[clock-scheduler] step reads: ...`) with the cache's hit/miss/parent-hit
counts, for a quick look without a recording.

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
| `paused_samples` / `clock_samples` | The paused fraction: how many status samples found the clock stopped. The speed matrix reports this against `max_paused_fraction`. |

Steps (`steps`, from `clock_step` rows):

| Field | Meaning |
| --- | --- |
| `steps` | `ClockScheduler.Step` calls that ran (held steps publish too). |
| `reads`, `max_reads`, `tools` | Native round trips the steps issued; the report shows the mean and max per step and the split by tool. A rising reads/step means a planner lost its cache. A steady step is one bundle; a reviewing step is the bundle (carrying the census families, #180), the admission bundle and the window start. |
| `schema_fetches` | Describe (`games_tool_detail`) round trips the step paid for a tool's first call, counted apart from `reads` (#180): a session's startup cost, not a step's. |
| `cache_hits` | Reads served by the per-step read cache (same tool and arguments within one step). |
| `parent_hits` | Reads served by the cross-step `FactCache` from the previous step's facts; `> 0` with a lower reads/step is the evidence that the cache is working. |
| `windows`, `window_ticks`, `max_window_ticks`, `max_window_target_secs` | The wall-sized colony windows (#126): how many ticks each window was budgeted and the wall target it was sized to. |
| `reasons` | Steps by cause: `timer` (the cadence fired with nothing captured; planners run only if the tick moved), `wake` (committed journal evidence — latched outcomes, an invalidated family, an authority change — shortcut the backoff), `settled` (the previous step reconciled or cleaned an epoch, so every planner re-plans), `full` (every planner ran, including the `FullStepEvery` promotion of a timer step). A healthy running clock is mostly `wake`; mostly `timer` means the poll loop is not seeing events. |
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
tick across multipliers, and that every budget stop wakes a step within
`StepInterval` (#112). Run it first after any scheduler, `WakeSignal` or
worker change.

**Native (about 2 minutes per speed):**

```bash
go run ./internal/nativeaccept/cmd/acceptance run speedmatrix/plain -root <abs root> -rimgovernor <abs path to rimgovernor.exe>
```

Needs a `ThroughputFixture` build; both profiles admit the uncapped case
(a rendered run reports a lower wall TPS for it, since every frame also draws). One staged colony is reloaded per speed and served with the `haul` and
`work` families for a 6000-tick budget, or until the stage runs out of work
(every wall plan completed, no storage deficit pending; the clock admits no
window after that, #210); each speed runs `serve` with the
flight recorder and the case reduces the recording with `SummarizePhases`
and `SummarizeStops`. `report.json` carries, per speed under `metrics`:

| Key | Meaning |
| --- | --- |
| `wall_tps`, `budget_wall_tps`, `paused_fraction` | The phase report's clock numbers; `budget_wall_tps` is `ticks_advanced` over the wait's own wall time. |
| `steps`, `reads_per_step`, `cache_hits`, `parent_hits` | As above. |
| `window_ticks_mean`, `window_ticks_max`, `window_target_secs_max` | The wall-sized windows at that speed; ticks per window should scale with the multiplier while the wall target stays put. |
| `stops`, `budget_stops`, `reactive_stops`, `stop_reasons`, `budget_stops_per_6000_ticks` | Stops classified from the `clock_read_events` replies: budget stops (the window's ticks ran out) versus reactive ones (a watch latch, a letter, a requested pause). Budget stops per 6000 ticks is the comparable rate across speeds. |
| `stop_latency_mean_ms`, `stop_latency_max_ms` | From the companion's `observedAtUnixMs` to the reply reaching the service (transport latency; the phase report's `stops` adds the step scheduling on top). |

The case fails on pawn outcomes that differ by more than 1 across speeds,
an unsuccessful plan stage, or nothing hauled or built; the
`max_paused_fraction` and `min_ultrafast_tps_ratio` thresholds are written
to the report and checked at zero, so they are reported, not enforced.

Rerun the native check when the clock scheduler, the step cache or the
Ultrafast/acceleration path changes; the unit check on every scheduler
change.
