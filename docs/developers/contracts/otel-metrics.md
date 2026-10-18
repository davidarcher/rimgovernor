# OTEL metrics: native call phases, scheduler, outcomes

[Documentation](../../README.md) · [OTEL core](otel-core.md)

Counters, histograms and one gauge recorded directly from the measurement at
its call site (epic #2778). They go through the OTEL `MeterProvider` of
[OTEL core](otel-core.md): the local file sink writes them to `metrics.jsonl`
every 10 s. Nothing here is
derived by parsing logs or rows.

Code: `Metrics.cs` (instruments and vocabularies and recording sites). With no provider installed the calls are no-ops.

## Instruments

All names are prefixed `rimgovernor.`; durations are milliseconds.

| Name | Kind, unit | Attributes | Recorded at |
| --- | --- | --- | --- |
| `native.phase.duration` | histogram, `ms` (native buckets) | `native_tool`, `phase` | each native call: `gate_wait`, `call`, `decode`, `native_queue`, `native_execute` (when the companion reported its split) in `Client.core`; `proto_decode` in `protoCall` |
| `native.calls` | counter, `{call}` | `native_tool`, `outcome` (`ok`, `refused`, `error`) | each native call |
| `scheduler.step.duration` | histogram, `ms` (scheduler buckets) | `step_cause` (`review`, `wake`, `retry`, `full`) | end of every `ClockScheduler.StepWithReason` |
| `scheduler.stop_latency` | histogram, `ms` | none | a step woken by a native hold: hold stamp to step start |
| `scheduler.critical_wave.duration` | histogram, `ms` | none | critical planner wave wall time |
| `game.ticks_per_second` | gauge, `{tick}/s` | none | the clock status's effective ticks per wall second, set by each step that read one |
| `action.outcomes` | counter, `{action}` | `action_kind`, `verdict` (`applied`, `refused`, `failed`), `reason` | each action in a Hands dispatch batch; one count per refusal reason |
| `admission.decisions` | counter, `{decision}` | `verdict` (`admitted`, `refused`), `reason` | each building-method admission; one count per refusal reason |
| `telemetry.dropped` | counter, `{item}` | `signal`, `sink`, `reason` | the export drop count, defined by OTEL core |

`scheduler.critical_wave.duration` is the brain's leg of "observed hazard to
protective effect": from the start of the critical wave to its protective plans
being ready. The remainder (dispatch and the game carrying the order out) is not
measured as one interval; the `class=critical` spans and `action.outcomes` cover it
piecewise.

## Histogram buckets

The file sink carries explicit-bucket histograms only (exponential histograms
would be counted as `export_failed`), so every histogram sets its boundaries:

- **Native call phases** (`telemetry.NativeBuckets`): 0.5, 1, 2, 5, 10, 25, 50, 100,
  250, 500, 1000, 2500, 5000, 10000 ms. Anything under half a millisecond (a cached
  read, a decode) lands in the first bucket; a call that hit the 10 s timeout lands
  in the overflow bucket.
- **Scheduler** (`telemetry.SchedulerBuckets`): 1, 5, 10, 25, 50, 100, 250, 500, 1000,
  2500, 5000, 10000, 30000, 60000, 120000, 300000 ms. Steps are usually tens of
  milliseconds, but pauses and stop-to-readmit waits run to seconds and minutes.

## Cardinality

Attributes are `telemetry.Label` values, which only a `Vocabulary` can produce; a
value outside its closed set is recorded as `other`. No pawn id, request id, plan id
or free-form target can reach a metric.

| Attribute | Closed set |
| --- | --- |
| `native_tool` | the reviewed native methods (`reviewedNativeMethods`) and the transport wrappers (`wrapperTools`); an acceptance probe's ad-hoc tool is `other` |
| `phase`, `outcome`, `verdict`, `step_cause` | the fixed words in the table |
| `action_kind` | the kinds `roundsExecutableKind` accepts |
| `reason` | every `policy.Reason` (a test keeps the list equal to the constants in `package policy`), `none` for a non-refusal, `other` for anything else |

`TestMetricsInstrumentsUnitsBucketsAndCardinality` offers hundreds of distinct values
to every instrument through a manual reader and asserts the series and attribute
values stay inside these sets, with the documented units and bucket boundaries.

## Acceptance run metrics and drift series

Every acceptance `result.json` carries a flat `metrics` block, and `acceptance run`
and `acceptance suite` append one row per case to the append-only series
(`metrics.jsonl` beside the output directory, `-series`/`-baseline`; not the OTLP
ring of the same name). `Metrics.cs` and `MetricsOtel.cs`.
The block's native and pause readings come from the run's exported metric rings
(`otlp/metrics.jsonl` of the first launch and of each restart's `service-N/otlp`).
Instruments are cumulative, so each process's last export is its total (rows are
keyed by `service.instance.id`, instrument and attributes) and the run's value is the
sum over processes:

| Block key | Derived from |
| --- | --- |
| `native_calls` | sum of `native.calls` over all outcomes |
| `native_errors` | `native.calls` with `outcome=error` (refusals are not errors) |
| `native_queue_ms_mean`, `native_exec_ms_mean` | `native.phase.duration` sum / count for `phase=native_queue` and `native_execute` |

The remaining keys come from the report and output directory, not telemetry:
`wall_ms`, `boot_ms`, `ticks_advanced`, `wall_tps`, the wait statistics
(`waits`, `waits_stalled`, `max_quiet_ms`), `service_launches`, `evidence_bytes`.
Row format, flake rate (failures in a case's last 10 runs) and drift (value past a
per-key ratio and floor against the trailing median of the last 10 passing runs) are
unchanged. Differences from the earlier log-derived block: `paused_fraction`,
`native_bytes`, `cache_hit_ratio` and `reads_per_step_mean` are gone because no
instrument measures them, and they no longer drift-flag. A series recorded before
this change keeps its old rows. A run with no metric ring reads zero
for the OTEL-derived keys.

## What remains of the hand-rolled aggregation

Readers still consume the row-based aggregation:

- The acceptance harness's phase summary (`PhaseSummary.cs`) builds the speed-matrix checks from `clock_step`, `clock_run` and
  `dispatch` rows.
