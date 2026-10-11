package nativeaccept

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/davidarcher/RimGovernor/go/internal/telemetry/telemetrytest"
)

func TestFlightTailReadsAppendedRowsOnly(t *testing.T) {
	path := filepath.Join(t.TempDir(), "logs.jsonl")
	write := func(s string) {
		f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0644)
		if err != nil {
			t.Fatal(err)
		}
		if _, err := f.WriteString(s); err != nil {
			t.Fatal(err)
		}
		f.Close()
	}
	call := telemetrytest.LogLines(t, telemetrytest.Row{Kind: "native_call", Context: map[string]any{}, Payload: map[string]any{}})
	dispatch := telemetrytest.LogLines(t, telemetrytest.Row{Kind: "dispatch", Context: map[string]any{"tick": 4200},
		Payload: map[string]any{"verdict": "completed", "reason": "completed", "target": "a", "dur_ms": 0.0, "attrs": map[string]any{"changed": true}}})
	step := telemetrytest.LogLines(t, telemetrytest.Row{Kind: "planner_step", Context: map[string]any{}, Payload: map[string]any{"verdict": "ok"}})
	write(call[0] + "\n")
	tail := NewFlightTail(path)
	rows, err := tail.Next()
	if err != nil || len(rows) != 0 {
		t.Fatalf("history is not a wake: rows=%v err=%v", rows, err)
	}
	write(dispatch[0] + "\n")
	write(`not json` + "\n")
	write(step[0][:len(step[0])-1]) // partial
	rows, err = tail.Next()
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 || !WorkerOutcome(rows[0]) || rows[0].Sequence != 1 || !rows[0].HasTick || rows[0].Tick != 4200 {
		t.Fatalf("rows: %+v", rows)
	}
	write(step[0][len(step[0])-1:] + "\n")
	rows, err = tail.Next()
	if err != nil || len(rows) != 1 || rows[0].Kind != "planner_step" {
		t.Fatalf("partial line completes next read: rows=%+v err=%v", rows, err)
	}
	// Rotation: the active file restarts small.
	if err := os.WriteFile(path, []byte(dispatch[0]+"\n"), 0644); err != nil {
		t.Fatal(err)
	}
	rows, err = tail.Next()
	if err != nil || len(rows) != 1 || rows[0].Sequence != 3 {
		t.Fatalf("after rotation: rows=%+v err=%v", rows, err)
	}
}

func TestFlightTailMissingFile(t *testing.T) {
	tail := NewFlightTail(filepath.Join(t.TempDir(), "none.jsonl"))
	if rows, err := tail.Next(); err != nil || rows != nil {
		t.Fatalf("missing file: rows=%v err=%v", rows, err)
	}
}
