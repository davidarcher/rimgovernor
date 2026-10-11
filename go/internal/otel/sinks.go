package otel

import (
	"context"

	"go.opentelemetry.io/otel/attribute"
	sdklog "go.opentelemetry.io/otel/sdk/log"
	sdkmetric "go.opentelemetry.io/otel/sdk/metric"
	"go.opentelemetry.io/otel/sdk/metric/metricdata"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
)

// spanProcessor hands ended spans to a bounded queue.
type spanProcessor struct{ q *queue[sdktrace.ReadOnlySpan] }

func (p *spanProcessor) OnStart(context.Context, sdktrace.ReadWriteSpan) {}
func (p *spanProcessor) OnEnd(s sdktrace.ReadOnlySpan)                   { p.q.offer(s) }
func (p *spanProcessor) Shutdown(ctx context.Context) error              { return p.q.shutdown(ctx) }
func (p *spanProcessor) ForceFlush(ctx context.Context) error            { return p.q.forceFlush(ctx) }

// logProcessor hands emitted records (cloned: the SDK reuses the original) to a
// bounded queue.
type logProcessor struct{ q *queue[sdklog.Record] }

func (p *logProcessor) Enabled(context.Context, sdklog.EnabledParameters) bool { return true }
func (p *logProcessor) OnEmit(_ context.Context, r *sdklog.Record) error {
	p.q.offer(r.Clone())
	return nil
}
func (p *logProcessor) Shutdown(ctx context.Context) error   { return p.q.shutdown(ctx) }
func (p *logProcessor) ForceFlush(ctx context.Context) error { return p.q.forceFlush(ctx) }

func spanUrgent(s sdktrace.ReadOnlySpan) bool {
	for _, kv := range s.Attributes() {
		if isDurable(kv) {
			return true
		}
	}
	return false
}

func isDurable(kv attribute.KeyValue) bool {
	return kv.Key == DurableAttr.Key && kv.Value.Type() == attribute.BOOL && kv.Value.AsBool()
}

func logUrgent(r sdklog.Record) bool {
	urgent := false
	r.WalkAttributes(func(kv attribute.KeyValue) bool {
		urgent = isDurable(kv)
		return !urgent
	})
	return urgent
}

// fileSink encodes batches as OTLP/JSON lines and appends them to one rotating
// file. Its methods run only on the owning queue's goroutine, so buf is unshared.
type fileSink struct {
	file  *rotatingFile
	stats *dropStats
	env   envelope
	buf   []byte
}

func (s *fileSink) writeSpans(_ context.Context, batch []sdktrace.ReadOnlySpan, durable bool) error {
	s.buf = s.buf[:0]
	for _, sp := range batch {
		line, err := s.env.encodeSpan(s.buf, sp)
		if err != nil {
			s.stats.add(ReasonExportFailed, 1)
			continue
		}
		s.buf = append(line, '\n')
	}
	return s.flush(durable)
}

func (s *fileSink) writeLogs(_ context.Context, batch []sdklog.Record, durable bool) error {
	s.buf = s.buf[:0]
	for i := range batch {
		line, err := s.env.encodeLog(s.buf, &batch[i])
		if err != nil {
			s.stats.add(ReasonExportFailed, 1)
			continue
		}
		s.buf = append(line, '\n')
	}
	return s.flush(durable)
}

func (s *fileSink) flush(durable bool) error {
	if len(s.buf) == 0 {
		return nil
	}
	return s.file.write(s.buf, durable)
}

// fileMetrics is the metric exporter of the file sink.
type fileMetrics struct {
	file  *rotatingFile
	stats *dropStats
	env   envelope
	buf   []byte
}

func (*fileMetrics) Temporality(sdkmetric.InstrumentKind) metricdata.Temporality {
	return metricdata.CumulativeTemporality
}
func (*fileMetrics) Aggregation(k sdkmetric.InstrumentKind) sdkmetric.Aggregation {
	return sdkmetric.DefaultAggregationSelector(k)
}
func (*fileMetrics) ForceFlush(context.Context) error { return nil }
func (*fileMetrics) Shutdown(context.Context) error   { return nil }

func (m *fileMetrics) Export(_ context.Context, rm *metricdata.ResourceMetrics) error {
	m.buf = m.buf[:0]
	for _, sm := range rm.ScopeMetrics {
		for _, met := range sm.Metrics {
			line, ok, err := m.env.encodeMetricLine(m.buf, rm.Resource, sm.Scope, met)
			if err != nil || !ok {
				m.stats.add(ReasonExportFailed, 1)
				continue
			}
			m.buf = append(line, '\n')
		}
	}
	if len(m.buf) == 0 {
		return nil
	}
	return m.file.write(m.buf, false)
}

// guardedMetrics counts a failed export and swallows the error: the periodic
// reader would otherwise print it through the global OpenTelemetry error handler
// on every interval during a collector outage.
type guardedMetrics struct {
	inner sdkmetric.Exporter
	stats *dropStats
}

func (g *guardedMetrics) Temporality(k sdkmetric.InstrumentKind) metricdata.Temporality {
	return g.inner.Temporality(k)
}
func (g *guardedMetrics) Aggregation(k sdkmetric.InstrumentKind) sdkmetric.Aggregation {
	return g.inner.Aggregation(k)
}
func (g *guardedMetrics) ForceFlush(ctx context.Context) error { return g.inner.ForceFlush(ctx) }
func (g *guardedMetrics) Shutdown(ctx context.Context) error   { return g.inner.Shutdown(ctx) }
func (g *guardedMetrics) Export(ctx context.Context, rm *metricdata.ResourceMetrics) error {
	if err := g.inner.Export(ctx, rm); err != nil {
		g.stats.add(ReasonExportFailed, 1)
	}
	return nil
}
