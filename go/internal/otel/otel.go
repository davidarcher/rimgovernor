// Package otel builds RimGovernor's OpenTelemetry SDK: a TracerProvider, a
// MeterProvider and a LoggerProvider that feed an always-on local OTLP/JSON
// file sink and, when configured, an OTLP collector. The contract (file layout,
// drop, rotation and crash policy, the Go API) is
// docs/developers/contracts/otel-core.md.
//
// Nothing here is a state store and nothing here can block a caller: every
// export is asynchronous through a bounded queue that drops and counts when
// full.
package otel

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	otellog "go.opentelemetry.io/otel/log"
	"go.opentelemetry.io/otel/metric"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/trace"
)

// DurableAttr marks a span or log record that must reach the file sink
// promptly: its batch is exported at once and fsync'd.
var DurableAttr = attribute.Bool("rimgovernor.durable", true)

// DroppedMetric is the counter of items lost, with bounded attributes signal
// (trace|log|metric), sink (file|collector) and reason (queue_full|export_failed).
const DroppedMetric = "rimgovernor.telemetry.dropped"

// File names inside Config.Dir. One file (and rotation budget) per signal, so a
// busy signal cannot rotate another's records out.
const (
	TracesFile  = "traces.jsonl"
	LogsFile    = "logs.jsonl"
	MetricsFile = "metrics.jsonl"
)

// Defaults. File retention per signal is SegmentBytes x Segments (the active
// file included): traces 16 MiB x 8, logs 32 MiB x 16, metrics 8 MiB x 4,
// 672 MiB in all, pruned by count only.
const (
	DefaultQueueSize              = 16384
	DefaultBatchSize              = 512
	DefaultFileFlushInterval      = 250 * time.Millisecond
	DefaultCollectorFlushInterval = 2 * time.Second
	DefaultExportTimeout          = 10 * time.Second
	DefaultMetricInterval         = 10 * time.Second
	DefaultAttributeBytes         = 64 << 10
	defaultWarnEvery              = 10 * time.Second
)

// Limits bounds one signal's file ring.
type Limits struct {
	SegmentBytes int64
	Segments     int
}

// Config configures Start. The zero value of every optional field selects the
// default.
type Config struct {
	// Dir receives the OTLP/JSON files. Empty disables the file sink (only for
	// tools with no profile; serve always sets it).
	Dir string

	// Service identifies the process (service.name), for example
	// "rimgovernor-serve". Version and RunID fill service.version and
	// service.instance.id.
	Service, Version, RunID string

	// CollectorEndpoint enables the collector exporter ("http://localhost:4317";
	// a bare host:port is http). Empty falls back to the standard
	// OTEL_EXPORTER_OTLP_ENDPOINT (or a per-signal *_ENDPOINT) environment
	// variable; with neither the collector exporter is off.
	// CollectorProtocol is "grpc" (default) or "http/protobuf"; empty falls back
	// to OTEL_EXPORTER_OTLP_PROTOCOL.
	CollectorEndpoint string
	CollectorProtocol string

	Traces, Logs, Metrics Limits // file ring overrides

	QueueSize, BatchSize                      int
	FileFlushInterval, CollectorFlushInterval time.Duration
	ExportTimeout, MetricInterval             time.Duration
	// MaxAttributeBytes caps one attribute string value (default 64 KiB).
	MaxAttributeBytes int

	// Warn receives the rate-limited drop warning (default: none; the counter still moves).
	Warn func(msg string, args ...any)

	// SetGlobal installs the providers as the OpenTelemetry globals.
	SetGlobal bool
}

func (c *Config) defaults() {
	set := func(l *Limits, bytes int64, n int) {
		if l.SegmentBytes == 0 {
			l.SegmentBytes = bytes
		}
		if l.Segments == 0 {
			l.Segments = n
		}
	}
	set(&c.Traces, 16<<20, 8)
	set(&c.Logs, 32<<20, 16)
	set(&c.Metrics, 8<<20, 4)
	for _, d := range []struct {
		v *int
		d int
	}{{&c.QueueSize, DefaultQueueSize}, {&c.BatchSize, DefaultBatchSize}, {&c.MaxAttributeBytes, DefaultAttributeBytes}} {
		if *d.v == 0 {
			*d.v = d.d
		}
	}
	for _, d := range []struct {
		v *time.Duration
		d time.Duration
	}{{&c.FileFlushInterval, DefaultFileFlushInterval}, {&c.CollectorFlushInterval, DefaultCollectorFlushInterval}, {&c.ExportTimeout, DefaultExportTimeout}, {&c.MetricInterval, DefaultMetricInterval}} {
		if *d.v == 0 {
			*d.v = d.d
		}
	}
	if c.Warn == nil {
		c.Warn = func(string, ...any) {}
	}
	if c.Service == "" {
		c.Service = "rimgovernor"
	}
}

// Telemetry owns the providers and sinks. Build one with Start and call
// Shutdown once on clean exit.
type Telemetry struct {
	TracerProvider *sdktrace.TracerProvider
	MeterProvider  *sdkmetric.MeterProvider
	LoggerProvider *sdklog.LoggerProvider

	stats  []*dropStats
	files  []*rotatingFile
	paths  map[string]string
	closer []func(context.Context) error
}

// Tracer, Meter and Logger return named instrumentation from this Telemetry's
// providers.
func (t *Telemetry) Tracer(name string) trace.Tracer { return t.TracerProvider.Tracer(name) }
func (t *Telemetry) Meter(name string) metric.Meter  { return t.MeterProvider.Meter(name) }
func (t *Telemetry) Logger(name string) otellog.Logger {
	return t.LoggerProvider.Logger(name)
}

// FilePath is the active file of a signal ("traces", "logs" or "metrics"), or ""
// when the file sink is off.
func (t *Telemetry) FilePath(signal string) string { return t.paths[signal] }

// Drops reports cumulative loss per signal, sink and reason.
func (t *Telemetry) Drops() []DropCount {
	var out []DropCount
	for _, s := range t.stats {
		out = append(out, s.counts()...)
	}
	return out
}

// ForceFlush exports everything queued so far to every sink, or returns when
// ctx ends.
func (t *Telemetry) ForceFlush(ctx context.Context) error {
	return errors.Join(t.TracerProvider.ForceFlush(ctx), t.LoggerProvider.ForceFlush(ctx), t.MeterProvider.ForceFlush(ctx))
}

// Shutdown flushes and stops every pipeline and closes the files (fsync'd). If
// ctx ends first, an export stuck on a dead collector is abandoned and its batch
// lost. Items queued after Shutdown begins are dropped.
func (t *Telemetry) Shutdown(ctx context.Context) error {
	err := errors.Join(t.TracerProvider.Shutdown(ctx), t.LoggerProvider.Shutdown(ctx), t.MeterProvider.Shutdown(ctx))
	for _, c := range t.closer {
		err = errors.Join(err, c(ctx))
	}
	for _, f := range t.files {
		err = errors.Join(err, f.close())
	}
	return err
}

// Start builds the providers. It fails only for an unusable Config or file
// directory; an unreachable collector is never an error.
func Start(ctx context.Context, cfg Config) (*Telemetry, error) {
	cfg.defaults()
	res, err := resource.New(ctx,
		resource.WithFromEnv(),
		resource.WithAttributes(attribute.String("service.name", cfg.Service)),
	)
	if err != nil {
		return nil, fmt.Errorf("otel: resource: %w", err)
	}
	extra := []attribute.KeyValue{}
	if cfg.Version != "" {
		extra = append(extra, attribute.String("service.version", cfg.Version))
	}
	if cfg.RunID != "" {
		extra = append(extra, attribute.String("service.instance.id", cfg.RunID))
	}
	if res, err = resource.Merge(res, resource.NewSchemaless(extra...)); err != nil {
		return nil, fmt.Errorf("otel: resource: %w", err)
	}

	t := &Telemetry{paths: map[string]string{}}
	newStats := func(signal, sink string) *dropStats {
		s := &dropStats{signal: signal, sink: sink, warnEvery: defaultWarnEvery, warn: cfg.Warn}
		t.stats = append(t.stats, s)
		return s
	}
	openFile := func(signal, name string, l Limits) (*rotatingFile, error) {
		if cfg.Dir == "" {
			return nil, nil
		}
		path := filepath.Join(cfg.Dir, name)
		f, err := openRotatingFile(path, l.SegmentBytes, l.Segments)
		if err != nil {
			return nil, err
		}
		t.files = append(t.files, f)
		t.paths[signal] = path
		return f, nil
	}
	fail := func(err error) (*Telemetry, error) {
		for _, f := range t.files {
			f.close()
		}
		return nil, err
	}

	traceFile, err := openFile("traces", TracesFile, cfg.Traces)
	if err != nil {
		return fail(err)
	}
	logFile, err := openFile("logs", LogsFile, cfg.Logs)
	if err != nil {
		return fail(err)
	}
	metricFile, err := openFile("metrics", MetricsFile, cfg.Metrics)
	if err != nil {
		return fail(err)
	}

	col, err := newCollector(ctx, cfg)
	if err != nil {
		return fail(err)
	}

	// Traces.
	limits := sdktrace.NewSpanLimits()
	limits.AttributeValueLengthLimit = cfg.MaxAttributeBytes
	traceOpts := []sdktrace.TracerProviderOption{sdktrace.WithResource(res), sdktrace.WithRawSpanLimits(limits)}
	if traceFile != nil {
		st := newStats("trace", "file")
		sink := &fileSink{file: traceFile, stats: st}
		traceOpts = append(traceOpts, sdktrace.WithSpanProcessor(&spanProcessor{q: newQueue(st, cfg.QueueSize, cfg.BatchSize, cfg.FileFlushInterval, cfg.ExportTimeout, spanUrgent, sink.writeSpans)}))
	}
	if col != nil {
		st := newStats("trace", "collector")
		traceOpts = append(traceOpts, sdktrace.WithSpanProcessor(&spanProcessor{q: newQueue(st, cfg.QueueSize, cfg.BatchSize, cfg.CollectorFlushInterval, cfg.ExportTimeout, nil, func(ctx context.Context, b []sdktrace.ReadOnlySpan, _ bool) error {
			return col.traces.ExportSpans(ctx, b)
		})}))
		t.closer = append(t.closer, col.traces.Shutdown)
	}
	t.TracerProvider = sdktrace.NewTracerProvider(traceOpts...)

	// Logs.
	logOpts := []sdklog.LoggerProviderOption{sdklog.WithResource(res), sdklog.WithAttributeValueLengthLimit(cfg.MaxAttributeBytes)}
	if logFile != nil {
		st := newStats("log", "file")
		sink := &fileSink{file: logFile, stats: st}
		logOpts = append(logOpts, sdklog.WithProcessor(&logProcessor{q: newQueue(st, cfg.QueueSize, cfg.BatchSize, cfg.FileFlushInterval, cfg.ExportTimeout, logUrgent, sink.writeLogs)}))
	}
	if col != nil {
		st := newStats("log", "collector")
		logOpts = append(logOpts, sdklog.WithProcessor(&logProcessor{q: newQueue(st, cfg.QueueSize, cfg.BatchSize, cfg.CollectorFlushInterval, cfg.ExportTimeout, nil, func(ctx context.Context, b []sdklog.Record, _ bool) error { return col.logs.Export(ctx, b) })}))
		t.closer = append(t.closer, col.logs.Shutdown)
	}
	t.LoggerProvider = sdklog.NewLoggerProvider(logOpts...)

	// Metrics: pull-based, so instruments never wait on a sink. The periodic
	// readers run on their own goroutines.
	metricOpts := []sdkmetric.Option{sdkmetric.WithResource(res)}
	reader := func(e sdkmetric.Exporter) sdkmetric.Reader {
		return sdkmetric.NewPeriodicReader(e, sdkmetric.WithInterval(cfg.MetricInterval), sdkmetric.WithTimeout(cfg.ExportTimeout))
	}
	if metricFile != nil {
		st := newStats("metric", "file")
		metricOpts = append(metricOpts, sdkmetric.WithReader(reader(&guardedMetrics{inner: &fileMetrics{file: metricFile, stats: st}, stats: st})))
	}
	if col != nil {
		st := newStats("metric", "collector")
		metricOpts = append(metricOpts, sdkmetric.WithReader(reader(&guardedMetrics{inner: col.metrics, stats: st})))
	}
	t.MeterProvider = sdkmetric.NewMeterProvider(metricOpts...)
	if err := t.observeDrops(); err != nil {
		return fail(err)
	}

	if cfg.SetGlobal {
		otel.SetTracerProvider(t.TracerProvider)
		otel.SetMeterProvider(t.MeterProvider)
		otel.SetLoggerProvider(t.LoggerProvider)
	}
	return t, nil
}

// observeDrops exposes the drop counters as the DroppedMetric counter.
func (t *Telemetry) observeDrops() error {
	meter := t.MeterProvider.Meter("rimgovernor/otel")
	_, err := meter.Int64ObservableCounter(DroppedMetric,
		metric.WithUnit("{item}"),
		metric.WithDescription("Telemetry items lost before export, by signal, sink and reason."),
		metric.WithInt64Callback(func(_ context.Context, o metric.Int64Observer) error {
			for _, d := range t.Drops() {
				o.Observe(int64(d.Count), metric.WithAttributes(attribute.String("signal", d.Signal), attribute.String("sink", d.Sink), attribute.String("reason", d.Reason)))
			}
			return nil
		}))
	return err
}

// envOr is os.Getenv with a fallback.
func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
