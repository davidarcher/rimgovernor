# OTEL metrics: native call phases, scheduler, outcomes

[Documentation](../../README.md) · [OTEL core](otel-core.md)

Counters, histograms and one gauge recorded directly from the measurement at
its call site (epic #2778). They go through the OTEL `MeterProvider` of
[OTEL core](otel-core.md): the local file sink writes them to `metrics.jsonl`
every 10 s, and a configured collector receives them too. Nothing here is
derived by parsing logs or flight rows.

Code: `go/internal/telemetry/metrics.go` (instruments and vocabularies),
`go/internal/bridge/call_metrics.go`, `go/internal/buildingruntime/metrics.go`
(recording sites). With no provider installed the calls are no-ops.

## Instruments

All names are prefixed `rimgovernor.`; durations are milliseconds.

| Name | Kind, unit | Attributes | Recorded at |
| --- | --- | --- | --- |
| `native.phase.duration` | histogram, `ms` (native buckets) | `native_tool`, `phase` | each native call: `gate_wait`, `call`, `decode`, `native_queue`, `native_execute` (when the companion reported its split) in `Client.core`; `proto_decode` in `protoCall` |
| `native.calls` | counter, `{call}` | `native_tool`, `outcome` (`ok`, `refused`, `error`) | each native call |
| `scheduler.step.duration` | histogram, `ms` (scheduler buckets) | `step_cause` (`timer`, `wake`, `settled`, `full`, `live`) | end of every `ClockScheduler.StepWithReason` |
| `scheduler.stop_latency` | histogram, `ms` | none | a step woken by a native clock stop: stop stamp to step start |
| `scheduler.controller_pause` | histogram, `ms` | none | wall time the controller held the game paused in a step |
| `scheduler.stop_pause` | histogram, `ms` | none | window stop to the admission that resumed the game |
| `scheduler.critical_wave.duration` | histogram, `ms` | none | critical planner wave wall time |
| `game.ticks_per_second` | gauge, `{tick}/s` | none | the clock status's effective ticks per wall second, set by each step that read one |
| `action.outcomes` | counter, `{action}` | `action_kind`, `verdict` (`applied`, `refused`, `failed`), `reason` | each action in a Hands dispatch batch; one count per refusal reason |
| `admission.decisions` | counter, `{decision}` | `verdict` (`admitted`, `refused`), `reason` | each building-method admission; one count per refusal reason |
| `telemetry.dropped` | counter, `{item}` | `signal`, `sink`, `reason` | the export drop count, defined by OTEL core |

`scheduler.critical_wave.duration` is the controller's leg of "observed hazard to
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

## What remains of the hand-rolled aggregation

Nothing is deleted by this change, because readers still consume it (they move in
#2790 and #2791; final deletion is #2793):

- `bridge.SummarizePhases` and `ToolPhases` build `rimgovernor phases` and the
  acceptance harness's metrics, speed-matrix and serve checks from `native_call` and
  `clock_step` flight rows.
- `pollrollup` folds quiet polls and frame-cache hits into the `native_call` rows
  those readers sum. The metrics above count every call individually and need no
  rollup.
