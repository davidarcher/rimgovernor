// Package telemetrytest captures the flight rows a test's code logs, so a
// test asserts on rows instead of rendered text.
package telemetrytest

import (
	"log/slog"
	"sync"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry"
)

// Row is one captured flight row.
type Row struct {
	Kind    string
	Context map[string]any
	Payload map[string]any
}

// Rows is a telemetry.Recorder that keeps every row in memory.
type Rows struct {
	mu   sync.Mutex
	rows []Row
}

// Event records one row.
func (r *Rows) Event(kind string, context map[string]any, _ bool, payload map[string]any) (uint64, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.rows = append(r.rows, Row{kind, context, payload})
	return uint64(len(r.rows)), nil
}

// All is every captured row, in order.
func (r *Rows) All() []Row {
	r.mu.Lock()
	defer r.mu.Unlock()
	return append([]Row(nil), r.rows...)
}

// Of is the captured rows of one kind, in order.
func (r *Rows) Of(kind string) []Row {
	var out []Row
	for _, row := range r.All() {
		if row.Kind == kind {
			out = append(out, row)
		}
	}
	return out
}

// Install makes the telemetry handler over a fresh Rows the default logger
// until the test ends.
func Install(t testing.TB) *Rows {
	t.Helper()
	rows := &Rows{}
	previous := slog.Default()
	slog.SetDefault(telemetry.New(rows))
	t.Cleanup(func() { slog.SetDefault(previous) })
	return rows
}
