# OTEL core: providers, OTLP/JSON file sink, collector exporter

[Documentation](../../README.md) · [Observability stack](../observability.md)

`go/internal/otel` builds RimGovernor's OpenTelemetry SDK (epic #2778). The
spans, metrics and decisions children instrument call sites against it; the
reader children parse its files. This page is the contract both build on.

OTEL is never a state store (see the [persistence table](persistence-contracts.md)).
Everything it exports is telemetry that may be dropped.

## Go API

```go
tel, err := otel.Start(ctx, otel.Config{Dir: ".../otlp", Service: "rimgovernor-serve", SetGlobal: true})
defer tel.Shutdown(ctx)               // bounded ctx; fsyncs and closes the files

tel.Tracer(name)  tel.Meter(name)  tel.Logger(name)   // or the OTEL globals when SetGlobal
tel.ForceFlush(ctx)                   // everything queued so far reaches every sink
tel.Drops()                           // cumulative loss by signal, sink, reason
otel.ReadLines(path, fn)              // readers: complete valid lines of a ring, oldest first
otel.Segments(path)
```

Typed readers (`internal/otel/typed.go`) turn a ring's lines into Go values; the
acceptance oracles and the Readers child (#2790) share them:

```go
otel.Scan(path, func(pos otel.Position, line []byte) error)  // lines with file#line positions (pos.String())
otel.ReadLogs(path,    func(otel.Log) error)     // eventName, time, severity, body, trace/span ids, Attrs
otel.ReadSpans(path,   func(otel.Span) error)    // name, ids, parent, kind, status, attrs, events
otel.ReadMetrics(path, func(otel.Metric) error)  // name, unit, points (cumulative; the last export is the total)
otel.DecodeLog(line)                             // one logs.jsonl line, for a tail
otel.SiblingRing(ring, otel.TracesFile)          // the other signals' rings sit beside it
```

`Attrs` values are natively typed (string, bool, int64, float64, `[]any`,
`map[string]any`); `Attrs.Str/Int/Float/Bool` read them. Every function skips
torn or invalid lines. `bridge.RecordFromLog` maps a `Log` back to a flight-style
row ([flight rows](flight-rows.md)). Tests produce real rings with
`telemetrytest.WriteLogs` / `LogLines`.

`Telemetry.TracerProvider`, `MeterProvider` and `LoggerProvider` are the plain
SDK providers. Instrumentation uses the standard OTEL API; nothing RimGovernor
specific is needed except one attribute:

- `otel.DurableAttr` (`rimgovernor.durable=true`) on a span or log record makes
  its batch export immediately and fsync. Use it for records that must survive a
  machine crash (the slow-call marker today). Do not use it per step.

`Start` fails only for an unusable config or file directory. serve reports that
and runs without telemetry. An unreachable collector is never an error.

## Pipelines

Every sink has its own bounded queue and one exporter goroutine, so a dead
collector cannot starve the file sink and no producer ever waits:

| Signal | Producer side | File sink | Collector |
| --- | --- | --- | --- |
| traces | span processor, `OnEnd` | queue 16384, batch 512, flush 250 ms | own queue, flush 2 s |
| logs | log processor, `OnEmit` (record cloned) | same | same |
| metrics | pull: periodic reader, 10 s | cumulative, written by the reader | own reader |

Metrics need no queue: instruments aggregate in memory and the reader pulls on
its own goroutine.

## Drop policy

- **Queue full:** the item is dropped, never blocked on, and counted
  (`reason=queue_full`).
- **Export failed or timed out** (10 s per batch; a stalled collector holds only
  its own goroutine): the whole batch is dropped and counted
  (`reason=export_failed`). There is no retry buffer and no replay: a batch the
  collector missed is gone from the collector but remains in the file sink.
- **Counter:** `rimgovernor.telemetry.dropped` (unit `{item}`, cumulative) with
  bounded attributes `signal` (`trace|log|metric`), `sink` (`file|collector`) and
  `reason`. It is exported through the same sinks and by `Telemetry.Drops()`.
  For metrics, one failed export counts as 1.
- **Warning:** at most one WARN per pipeline per 10 s
  (`telemetry dropped items`, with `dropped_since_last_warning`).
- Items offered after `Shutdown` starts are dropped and counted.

## Crash recovery and durability

- Each batch is one `write` straight to the OS (no user-space buffer). A process
  crash loses at most the batches still queued or being encoded: up to
  `FileFlushInterval` (250 ms) of records, plus anything in a full queue.
- fsync happens on rotation, on close, and for a batch containing a
  `rimgovernor.durable` record. A machine crash can lose the unsynced tail; the
  recorder it replaces has the same property.
- Clean exit: `Shutdown` drains the queues, runs a final metric collection and
  fsyncs. If its context ends first, a stuck collector export is abandoned.
- After an unclean exit the active file may end in a partial line. On the next
  open a newline is written first, so only the torn line is lost. Readers skip
  any line that is incomplete or not valid JSON (`ReadLines` does).
- Spike #2781: plain write is about 9 us/row; the span path here measures about
  3.4 us per span end to end with no drops at 200k spans, so the sink has
  headroom well past 20k rows/s.

## File layout

Directory: `<flight dir>/otlp/` (serve: `<profile>/flight/otlp/`, beside
`flight.jsonl`; `--flight-recorder` moves both). One ring per signal, so a busy
signal cannot rotate another's records out:

| File | Content | Retention (segment x files, active included) |
| --- | --- | --- |
| `traces.jsonl` | one span per line | 16 MiB x 8 = 128 MiB |
| `logs.jsonl` | one log record per line (decisions, events) | 32 MiB x 16 = 512 MiB |
| `metrics.jsonl` | one metric (all data points) per line, every 10 s | 8 MiB x 4 = 32 MiB |

Rotation: when the active file would pass its segment size, it becomes
`name.1`, older ones shift up (`name.2` ...), and the file beyond the file count
is deleted. A batch is not split, so a segment may exceed its size by one
batch. Retention is by count only; total disk is bounded at 672 MiB. Reopening
continues the active file. Readers walk `name.N ... name.1, name` oldest first.

### Line format

Each line is one complete, valid OTLP/JSON request body with exactly one record,
per the [OTLP JSON mapping](https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding):

- `traces.jsonl`: `{"resourceSpans":[{"resource":{...},"scopeSpans":[{"scope":{...},"spans":[SPAN]}]}]}`
- `logs.jsonl`: `{"resourceLogs":[{"resource":{...},"scopeLogs":[{"scope":{...},"logRecords":[RECORD]}]}]}`
- `metrics.jsonl`: `{"resourceMetrics":[{"resource":{...},"scopeMetrics":[{"scope":{...},"metrics":[METRIC]}]}]}`

Rules readers can rely on: ids are lowercase hex (`traceId` 32, `spanId` 16);
64-bit integers (`startTimeUnixNano`, `intValue`, `count`, bucket counts) are
strings; `kind` and `status.code`, `severityNumber` and
`aggregationTemporality` are the OTLP enum numbers; attributes are
`{"key":..,"value":{"stringValue"|"intValue"|"doubleValue"|"boolValue"|"arrayValue"|"kvlistValue"|"bytesValue":..}}`.
NaN and infinities are strings. Metrics are cumulative; Gauge, Sum and
explicit-bucket Histogram are carried (exponential histograms are not configured
and would be counted as `export_failed`).
The resource carries `service.name`, `service.instance.id` (one per process
launch) and `service.version`. One record per line is deliberate: grep by
substring, one torn line loses one record, and each line is also valid input to
an OTLP file receiver. The cost is repeating resource and scope on every line.

An attribute string is capped at 64 KiB by the SDK limits (the recorder's old
payload cap).

## Collector exporter

Enabled when an endpoint is configured; absent that, there is no collector
pipeline at all.

| Setting | Meaning |
| --- | --- |
| `serve --otlp-endpoint URL` / `Config.CollectorEndpoint` | enables it; `http://localhost:4317` is the compose stack's gRPC port. Beats the environment. |
| `serve --otlp-protocol` / `Config.CollectorProtocol` | `grpc` (default) or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_ENDPOINT`, `_PROTOCOL`, per-signal `_TRACES_/_METRICS_/_LOGS_ENDPOINT` | standard variables, honored by the stock exporters |

For `http/protobuf` with an explicit endpoint the signal path (`/v1/traces`
and so on) is appended to the endpoint's path. The exporters are the stock OTLP
ones and connect lazily.

## Spans

Instrument with `telemetry.StartSpan(ctx, component, name)` and `telemetry.EndSpan(span, err)`
(`internal/telemetry/span.go`); context.Context carries the parent. `telemetry.Refuse`
marks a refusal (error status, `rimgovernor.refused` reasons); `telemetry.Remote` nests a
span under a span context published from another goroutine. With no provider installed
(a test, a failed `otel.Start`) spans are no-ops and rows carry no trace ids. Tests use
`telemetrytest.Spans(t)` (in-memory exporter; not parallel).

Span tree, one scheduler step:

```text
scheduler.step                      root; attr step_cause
  rounds.review                     the Round
  planner.<name>                    attrs class, concern, verdict, reason; error only when the planner failed
    admission                       plan admission; error status on a store error, refused status + reasons
    native.call (client)            under whichever span made the call
      native.execute (server)       synthesized from the companion's echoed queue/execute timing
worker.step                         child of the scheduler step that admitted the window
  hands.dispatch                    attrs action_kind, actions; error on a returned error,
    native.call ...                 refused status + reasons on a refusal
clock.poll, clock.renew             roots of their own
```

Critical protection is the `class=critical` planner spans. `native.call` covers the gate wait and the
call; attributes `tool`, `admission_class`, `gate_wait_ms`, `call_ms`, `decode_ms`,
`native_queue_ms`, `native_execute_ms`. Attribute keys are prefixed `rimgovernor.`.

### Wire to the companion

Typed bridge calls send the current span as a W3C `traceparent` (`00-<32 hex trace>-<16 hex span>-<flags>`,
55 characters) in the existing `trace` argument. The mod caps the argument at 64 characters,
treats it as opaque, stamps it on its `mod_log` lines and echoes it in `timing.trace`; Go reads
either that form or the older `<trace>/<span>` (`telemetry.ParseWireTrace`).

The mod does **not** ship `System.Diagnostics.DiagnosticSource` (spike #2781 was conditional on an
in-game check under RimWorld's Unity Mono, and the mod has no exporter, so an `Activity` there would
only be discarded). Go therefore synthesizes the `native.execute` child span from the echoed timing, so
a game that never loads DiagnosticSource behaves identically. If a C# `Activity` path is added later it
must fall back to this echo when the assembly cannot load, and never throw on the game thread.

### Temporary adapter

Flight rows logged under a span are stamped with `trace_id`/`span_id`/`parent_id`
(`telemetry.StampTrace`, `internal/telemetry/trace_adapter.go`) for the legacy flight.jsonl
readers (the launcher tabs). `rimgovernor trace` reads spans and OTLP log records instead. It is removed with the recorder by the Retire child (#2793).
