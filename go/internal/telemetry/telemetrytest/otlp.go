package telemetrytest

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"go.opentelemetry.io/otel"

	rgotel "github.com/davidarcher/RimGovernor/go/internal/otel"
	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// LogLines returns the OTLP log lines (one record each, newline-free) the
// service would write for rows, for tests that place lines in a ring file
// themselves (rotation, a torn tail, several segments).
func LogLines(t testing.TB, rows ...Row) []string {
	t.Helper()
	ring := WriteLogs(t, t.TempDir(), rows...)
	data, err := os.ReadFile(ring)
	if err != nil {
		t.Fatalf("read %s: %v", ring, err)
	}
	var out []string
	for _, line := range strings.Split(string(data), "\n") {
		if line != "" {
			out = append(out, line)
		}
	}
	return out
}

// WriteLogs writes rows into dir as the OTLP log ring the service writes
// (through the real file sink and telemetry.EmitRow) and returns the ring's
// path, dir/logs.jsonl. A reader of OTLP output sees the rows exactly as it
// would from a service. The global logger provider is swapped for the call, so
// tests using it must not run in parallel.
func WriteLogs(t testing.TB, dir string, rows ...Row) string {
	t.Helper()
	tel, err := rgotel.Start(context.Background(), rgotel.Config{Dir: dir, Service: "rimgovernor-test", RunID: "test-run", MetricInterval: time.Hour})
	if err != nil {
		t.Fatalf("start telemetry: %v", err)
	}
	previous := otel.GetLoggerProvider()
	otel.SetLoggerProvider(tel.LoggerProvider)
	for _, row := range rows {
		telemetry.EmitRow(context.Background(), row.Kind, row.Context, row.Payload)
	}
	otel.SetLoggerProvider(previous)
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if err := tel.Shutdown(ctx); err != nil {
		t.Fatalf("shutdown telemetry: %v", err)
	}
	return filepath.Join(dir, rgotel.LogsFile)
}
