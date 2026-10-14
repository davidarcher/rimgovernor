# OTEL core: OTLP/JSON file sink

[Documentation](../../README.md) · [Observability stack](../observability.md)

`brain/src/RimGovernor.Brain.Telemetry` writes RimGovernor's telemetry (spans, metrics, log rows) as OTLP/JSON
files and reads them back. This page is the contract the writer, the readers and the Grafana dashboards share.
There is no collector exporter: the files are the only sink, and a collector's file receiver can be pointed at
the rings.

OTEL is never a state store (see the [persistence table](persistence-contracts.md)).
Everything it exports is telemetry that may be dropped.

## Writer

`OtlpFileWriter` is the production `ITelemetrySink`. The brain host ([brain host](../architecture/brain-host.md))
starts it with `OtlpFileWriter.Start(OtlpFileConfig)`: the directory (`RIMGOVERNOR_OTLP_DIR`), the service name and
the run id (`RIMGOVERNOR_RUN_ID`). `Start` throws `OtlpException` for an unusable directory, and the host then
runs without telemetry. The writer listens to every `ActivitySource` and `Meter` under
`github.com/davidarcher/RimGovernor/` and receives log rows as the `TelemetryEmitter` sink. `ForceFlushAsync`,
`ShutdownAsync` (final metric collection, fsync, close) and `Drops()` complete the surface.

Each signal has a bounded queue (`OtlpFileConfig.QueueSize` 16384, `BatchSize` 512, `FileFlushInterval` 250 ms)
and one writer task; `Offer` never blocks. Metrics are pulled every `MetricInterval` (10 s) from a `MeterListener`
into cumulative sums, last-value gauges and explicit-bucket histograms (bounds from the instrument's advice, the
SDK default set otherwise).

## Drop policy

- **Queue full:** the item is dropped, never blocked on, and counted (`reason=queue_full`).
- **Counter:** `rimgovernor.telemetry.dropped` (unit `{item}`, cumulative) with bounded attributes `signal`
  (`trace|log|metric`), `sink` and `reason`. It is exported through the same files and by `Drops()`.
- **Warning:** at most one WARN per pipeline per 10 s (`telemetry dropped items`, with
  `dropped_since_last_warning`), through `OtlpFileConfig.Warn`.
- Items offered after `ShutdownAsync` starts are dropped and counted.

## Crash recovery and durability

- Each batch is one `write` straight to the OS (no user-space buffer). A process crash loses at most the batches
  still queued or being encoded: up to `FileFlushInterval` (250 ms) of records, plus anything in a full queue.
- fsync happens on rotation, on close, and for a batch containing a `rimgovernor.durable` record
  (`TelemetryRecord.LogDurable`). A machine crash can lose the unsynced tail.
- Clean exit: `ShutdownAsync` drains the queues, runs a final metric collection and fsyncs.
- After an unclean exit the active file may end in a partial line. On the next open a newline is written first, so
  only the torn line is lost. Readers skip any line that is incomplete or not valid JSON.

## File layout

Directory: `RIMGOVERNOR_OTLP_DIR` (default `<save-data>/RimGovernor/otlp`, see [brain host](../architecture/brain-host.md)).
One ring per signal, so a busy signal cannot rotate another's records out:

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
explicit-bucket Histogram are carried.
The resource carries `service.name`, `service.instance.id` (one per process
launch) and `service.version`. One record per line is deliberate: grep by
substring, one torn line loses one record, and each line is also valid input to
an OTLP file receiver. The cost is repeating resource and scope on every line.

An attribute string is capped at 64 KiB (`OtlpFileConfig.MaxAttributeBytes`).

## Readers

`OtlpRing` (`Segments`, `Scan`, `ReadLines`), `OtlpReader` (`ReadLogs`, `ReadSpans`, `ReadMetrics`, `DecodeLog`)
and `OtlpTimeline.Read(path)` (rows with `Kind`, `Sequence`, `Run`, `WallTime`, `Context`, `Payload`; numbers are
`double`). `OtlpFiles.SiblingRing` finds the other signals' rings beside one. Attribute values are natively typed
(`OtlpAttrDictionary`). All readers skip torn or invalid lines and open segments shared, so they follow a ring a
live writer owns.

## Spans

Instrument with `Spans.StartSpan(component, name)` and `Spans.EndSpan(span, error)`
(`Telemetry/Spans.cs`); `Activity` carries the parent. `Spans.Refuse` marks a refusal (error status,
`rimgovernor.refused` reasons); `Spans.RemoteHex` nests a span under a span context published from another
thread. With no listener installed spans are null and rows carry no trace ids.

Span tree, one scheduler step:

```text
scheduler.step                      root; attr step_cause
  rounds.review                     the Round
  admission                         plan admission; error status on a store error, refused status + reasons
worker.step                         child of the scheduler step that admitted the window
clock.poll                          root of its own
```

Critical protection is the `class=critical` planner spans. Attribute keys are prefixed `rimgovernor.`.

### Wire to the companion

Typed bridge calls send the current span as a W3C `traceparent` (`00-<32 hex trace>-<16 hex span>-<flags>`,
55 characters, `Spans.Traceparent`) in the existing `trace` argument. The mod caps the argument at 64 characters,
treats it as opaque, stamps it on its `mod_log` lines and echoes it in `timing.trace`; the brain reads
either that form or the older `<trace>/<span>` (`Spans.TryParseWireTrace`).
