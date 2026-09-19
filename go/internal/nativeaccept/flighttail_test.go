package nativeaccept

import (
	"os"
	"path/filepath"
	"testing"
)

func TestFlightTailReadsAppendedRowsOnly(t *testing.T) {
	path := filepath.Join(t.TempDir(), "flight.jsonl")
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
	write(`{"version":1,"run":"a","sequence":1,"wall_time":1,"kind":"native_call","context":{},"payload":{}}` + "\n")
	tail := NewFlightTail(path)
	rows, err := tail.Next()
	if err != nil || len(rows) != 0 {
		t.Fatalf("history is not a wake: rows=%v err=%v", rows, err)
	}
	write(`{"version":1,"run":"a","sequence":2,"wall_time":1,"kind":"worker_outcome","context":{"tick":4200},"payload":{"outcome":"completed"}}` + "\n")
	write(`not json` + "\n")
	write(`{"version":1,"run":"a","sequence":3,"wall_time":1,"kind":"scheduler_step","context":{},"payload":{}`) // partial
	rows, err = tail.Next()
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 1 || !WorkerOutcome(rows[0]) || rows[0].Sequence != 2 || !rows[0].HasTick || rows[0].Tick != 4200 || rows[0].Payload["outcome"] != "completed" {
		t.Fatalf("rows: %+v", rows)
	}
	write("}\n")
	rows, err = tail.Next()
	if err != nil || len(rows) != 1 || rows[0].Kind != "scheduler_step" {
		t.Fatalf("partial line completes next read: rows=%+v err=%v", rows, err)
	}
	// Rotation: the active file restarts small.
	if err := os.WriteFile(path, []byte(`{"version":1,"run":"a","sequence":4,"wall_time":1,"kind":"worker_outcome","context":{},"payload":{}}`+"\n"), 0644); err != nil {
		t.Fatal(err)
	}
	rows, err = tail.Next()
	if err != nil || len(rows) != 1 || rows[0].Sequence != 4 {
		t.Fatalf("after rotation: rows=%+v err=%v", rows, err)
	}
}

func TestFlightTailMissingFile(t *testing.T) {
	tail := NewFlightTail(filepath.Join(t.TempDir(), "none.jsonl"))
	if rows, err := tail.Next(); err != nil || rows != nil {
		t.Fatalf("missing file: rows=%v err=%v", rows, err)
	}
}
