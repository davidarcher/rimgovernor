# Measure brain throughput

[All docs](../../README.md) · [Developer guide](../README.md) · [Choose tests](choose-tests.md)

Record what the brain spends its time on against a live game, read the
numbers back, and prove a change to the clock loop or the step cache did what
it claims. The readers are read-only over the recording: they never touch the
game or its authority. Field-level definitions live in the code
(`PhaseSummary` in `brain/src/RimGovernor.Brain.Acceptance/CaseKit/PhaseSummary.cs`); this page holds only the
rules for reading them.

## Record

The in-process brain writes one OTLP log record per native call, marker and internal
sample to `otlp/logs.jsonl` under `RIMGOVERNOR_OTLP_DIR` (default
`<save-data>/RimGovernor/otlp`; each row's `run` is its launch's `service.instance.id`).
The acceptance runner sets the directory per case. Segments rotate beside the file; the reader picks them all up.

Row kinds a phase report reads:

- native call rows carry `timing`: gate wait (`class` is `control`,
  `observation` or `mirror`), round trip, receipt and reply decode, and,
  when present, main-thread queue and execute time. Absent blocks read
  unknown, never zero.
- `native_frame` with `outcome` `hit`: a read the snapshot frame stream served without a round trip.
- `clock_step`: one row per `ClockScheduler.Step` (round trips by tool, reason,
  hold-to-step latency, budgets against use: `budget`,
  `critical_wave_ms`, `missed_cutoff`, `held_by`).
- `clock_run`: one row per Run, Hold or refusal the brain made (verdict, reason, `coupled_orders`, `held_for`).
- `clock_read_events` replies: the ticks and paused status behind wall TPS and
  the paused fraction.

There is no text log or debug flag: everything is a log record in `otlp/logs.jsonl`.

## Service events

Every service event is a structured record that
becomes a row of its `kind`; a record with no kind writes nothing.
Kinds: `planner_step` (one per planner run; target `worker_step` is the clock worker's step, one per change of outcome), `clock_step`, `admission`,
`clock_run`, `clock_stop`, `authority`, `dispatch`, `rounds_review`, and native's one row per clock or colony event
(`clock_hold`, `clock_resume`, `clock_authority`, `clock_alert`, ...: [clock and colony
events](../contracts/otel-signals.md#clock-and-colony-events)). Grep the
emitting code for each one's
attributes.

## Traces

The spans are in `otlp/traces.jsonl` ([span tree](../contracts/otel-core.md#spans))
and the rows logged inside them are `otlp/logs.jsonl` records carrying the same
`trace_id` and a `span_id`. A scheduler step is a trace root (census,
planner reads, admission, its `clock_step` and `planner_step` rows); the Worker's
step and each dispatch are child spans, so `dispatch` rows and a dispatch's
native calls join the trace of the step that admitted them. Spans export when they end, so a
native call that never returned has no span: its `native_request` marker (written
once a call is 2 s outstanding) is the only trace of it.

## Reading the rows

The files are JSON lines; read them with `jq` ([OTEL signals](../contracts/otel-signals.md) is the contract).
Rotated segments sit beside `logs.jsonl`, oldest first.

## Report

The speed-matrix cases summarize a run's log ring with `PhaseSummary.Summarize` and write the result into `result.json`.
Sections: header, `clock`, `steps`, `holds`, `observation`, `frames`,
reads/step by tool, per-native-tool table.

Traps in the numbers:

- Wall TPS **includes paused time**: a run that is held often reads low even if
  the game ran fast between holds.
- The headline paused number is `paused_fraction_native` (native's own
  hold->run / run->hold account on a monotonic clock). `paused_samples` /
  `clock_samples` is a sampling diagnostic that over-represents pauses, because
  the service reads mostly while the clock is held.
- `step reasons`: a healthy running clock is mostly `wake`; mostly `timer`
  means the poll loop is not seeing events. `timer` runs only planners due on
  the queue, `wake` is committed journal evidence, `settled` and `full` re-plan
  everything.
- `resume_latency_ms_*` is the native time a hold lasted before a Run resumed it, the stall a held colony waits on the brain; it
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
- Low wall TPS with a high paused fraction: the game waits on the brain;
  check `resume_latency_ms_mean` and whether `reasons` is `timer`-heavy. With a low
  paused fraction the game itself is slow; the brain is not the bottleneck.
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

## Across clock speeds

The brain must make the same decisions per game tick and react to a hold
within one step interval at every speed.

**Native (about 2 minutes per speed):**

```bash
RimGovernor.cmd acceptance speedmatrix -root <abs root>
```

Needs a `ThroughputFixture` build. One staged colony is reloaded per row and
run with the `haul` and `work` families to a 30000-tick target, or until the
stage runs out of work. Rows are `DefaultSpeedMatrix`
(`Normal,Fast,Superfast,Ultrafast,uncapped,governor-off`);
`-speeds uncapped,governor-off` narrows a run. The command has its own driver
(`brain/src/RimGovernor.Brain.Acceptance/SpeedMatrix`) outside the case runner and the catalog; per-row
reports and `result.json` go to `-output` (default `<root>/acceptance/speedmatrix-<profile>`), `-timeout`
(15m) bounds the run, and exit 0 is a pass, 1 a failed matrix, 2 a usage error.
`governor-off` plays the same save natively at the uncapped speed with no
brain attached (the simulation ceiling): it reports only `ticks_advanced`,
`wall_seconds`, `wall_tps` and is outside the outcome comparison.

`result.json` carries per row `speed_metrics` (the keys are defined in
`brain/src/RimGovernor.Brain.Acceptance/Scenario/SpeedMatrix.cs`). Key rules:

- `paused_fraction_native` (with `paused_ms`/`running_ms`, reset on reload) is
  the paused number; `paused_fraction` is the sampling diagnostic. The native
  accounting (pause/run, `probe_ms`/`digest_ms`, the frame account,
  `max_probe_tick_gap`, `hazard_gaps`) is the difference between two
  `rimgovernor/acceptance_read_clock_accounting` reads, one when the row
  resumes and one when its wait ends (`ClockAccounting`).
- `holds` counts native's `clock_hold` rows, `holds_per_6000_ticks`
  is the rate comparable across speeds and `hold_reasons` splits them by
  reason (`letter_pause`, `review`, `coupled_order`, ...). A healthy run holds
  for a named reason and resumes at once.
- `resume_latency_ms_mean`/`_max` is native's own time from a hold to the Run
  that resumed it (the hold row's `stop_at_ms` to the `clock_resume` row's
  `observed_at_unix_ms`, both on native's clock).
- `effective_speed` is `wall_tps` over the speed's own rate (Normal 60, Fast
  180, Superfast 360, Ultrafast 9000: the boost native serves it with under
  the one ceiling), holds included: 1 means the clock never lost pace. `native_effective_tps_mean` is native's ten-second account.
- `hold_latencies` give each hold's legs: `detect_ticks` (hazard to
  detection), `hold_ticks` (detection to the hold taking) and `resume_ms`.
- `run_verdicts` counts the brain's Run, Hold and refusal rows;
  `coupled_runs`/`coupled_orders` count the Runs that carried coupled orders.
- `hazard_gaps` names each hazard class's declared bound, widest observed gap
  and direct-hook backing: [hazard detection
  bounds](../architecture/hazard-detection-bounds.md).

The run fails when a required row has no metrics row or advanced no ticks, or
a compared row has no outcome (`row_problems`; `SpeedRowProblems` is the
boundary), on pawn outcomes differing by more than 1 across speeds, an
unsuccessful plan stage, or nothing hauled or built. `max_paused_fraction` and
`min_ultrafast_tps_ratio` are written to the report and checked at zero:
reported, not enforced.

## Observation baseline

`acceptance speedmatrix -profile observations` is the repeatable rendered workload that
observation-cost numbers are read against; it reuses the speed matrix's staging
and reporting:

```bash
RimGovernor.cmd acceptance speedmatrix -profile observations -root <abs root>
```

It loads the generated `RimGovernor-tribal8-baseline` colony with
`test/throughput_prepare` applied, always windowed (not headless, since update
intervals are a player's only when the game draws), one clock speed and one
tick target. Rows: `governor-off` (ungoverned update-interval ceiling; its
accounts come off its own tick reads' envelopes), `uncapped` (ordinary governed
run), `observation-load` (the `uncapped` row plus three concurrent readers
polling `/api/state` every 250 ms; it adds `readers`, and a load row with no
successful read fails) and `player`. Every row asks for the same speed and
staged work, so differences are observation cost.

Each row's `speed_metrics` carries the full `observation` and `frames` blocks
plus flat headlines (`observation_hops`, `capture_p95_ms`, `format_p95_ms`,
`queue_p95_ms`, `execute_p95_ms`, `frame_*`, `observed_frame_*`,
`encode_*_p95_ms`); `provenance` records the revision, host, mod set, launch
arguments, rows, tick target, stage and camera, and the report's `world` block
the seed, save and fixture hashes. `observed_frame_*` histograms count only
updates that ran main-thread observation work: a tail living there is
observation-caused, one that does not is not.

Rules:

- The run refuses an incomplete report: a row without interval samples (no
  frame hook or older companion) or a governed row without observation hops
  fails as `observation report incomplete`, never reads as zero cost.
- Thresholds come from a baseline run of this profile on a declared runner, never
  from the candidate being judged; a unit test does not fail on a busy CI.
- To isolate emergency-only work from a full bundle, narrow the rows with
  `-speeds` and read the `sections` split; there is no
  diagnostic bypass.

Isolate the accounting itself, with no game, through the contract probe (all
clocks supplied; it also pins the 48-section per-hop cap, the 8-entry
worst-interval ring and the session reset):

```powershell
dotnet run --project contracts/tests/NativeContractProbes.csproj -- native-observation-work
```

## Clock baseline run

Fills the table in [brain host](../architecture/brain-host.md#live-clock-baseline). Needs a live game with the bot on,
run on the shared-machine rules in `AGENTS.md`. Record machine (CPU, GPU, RAM), revision and the run's `service.instance.id`
with the numbers. The frame budget, heartbeat tolerance, combat resume budget, Normal fallback and autosave cadence are
unmeasured guesses until this run is recorded (#3199).

1. Run `acceptance speedmatrix -profile observations` (above) for frame time at each speed: read `frames` p50/p95/p99 per row and the
   `clock_step` fields `pacing_reason`, `paced_tps`, `tps_ceiling` and `effective_tps` in `otlp/logs.jsonl` for
   how the one ceiling holds the rate and recovers.
2. Critical wave and review scope: take `critical_wave_ms` from the `clock_step` rows and `dur_ms` from `rounds_review` /
   `planner_step` rows of the same run; report p50/p95/max.
3. Hazard hold to resume: take `stop_latency_ms` from `clock_step` rows that follow a `clock_stop` or `clock_hold`;
   for combat read `resume_latency_ms` and `over_budget` on `clock_stop` rows and p50/p95 on `combat_summary` (stage
   a raid fixture so a decision event is armed).
4. Heartbeat: the driver sends `Heartbeat` from its own 1 s loop, independent of steps. Measure the longest gap native
   saw between contacts (a `Run` or `Heartbeat`) during a long step and while idle, against the 5 s tolerance.
5. Autosave: time a `Hold(Autosave)` from hold to the resuming `Run` on a representative colony.
6. Write the values, machine and run into the brain-host table and mark each recommended budget measured or guess.

Rerun the native check when the clock scheduler, step cache or
Ultrafast/acceleration path changes; the observation baseline when the capture, encoding or main-thread admission
path changes.
