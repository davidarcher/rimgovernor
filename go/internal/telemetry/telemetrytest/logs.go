package telemetrytest

import (
	"context"
	"sync"
	"testing"

	"go.opentelemetry.io/otel"
	sdklog "go.opentelemetry.io/otel/sdk/log"
)

// LogRecords is an in-memory log exporter.
type LogRecords struct {
	mu      sync.Mutex
	records []sdklog.Record
}

// Export keeps clones of the records.
func (l *LogRecords) Export(_ context.Context, records []sdklog.Record) error {
	l.mu.Lock()
	defer l.mu.Unlock()
	for _, r := range records {
		l.records = append(l.records, r.Clone())
	}
	return nil
}

func (l *LogRecords) Shutdown(context.Context) error   { return nil }
func (l *LogRecords) ForceFlush(context.Context) error { return nil }

// All is every exported record, in order.
func (l *LogRecords) All() []sdklog.Record {
	l.mu.Lock()
	defer l.mu.Unlock()
	return append([]sdklog.Record(nil), l.records...)
}

// Logs installs an in-memory logger provider as the global one for the test
// (records export synchronously on emit) and returns its exporter. The global
// is restored afterwards, so tests using it must not run in parallel.
func Logs(t testing.TB) *LogRecords {
	t.Helper()
	exporter := &LogRecords{}
	provider := sdklog.NewLoggerProvider(sdklog.WithProcessor(sdklog.NewSimpleProcessor(exporter)))
	previous := otel.GetLoggerProvider()
	otel.SetLoggerProvider(provider)
	t.Cleanup(func() {
		otel.SetLoggerProvider(previous)
		_ = provider.Shutdown(context.Background())
	})
	return exporter
}
