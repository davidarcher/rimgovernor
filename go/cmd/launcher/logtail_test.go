package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func flightLine(seq int, run, kind, level string, tick int, payload string) string {
	return fmt.Sprintf(`{"version":2,"run":%q,"sequence":%d,"wall_time":%d,"kind":%q,"context":{"level":%q,"at":"2026-10-05T10:00:%02dZ","tick":%d,"component":"clock-scheduler"},"payload":%s}`+"\n",
		run, seq, 1000+seq, kind, level, seq, tick, payload)
}

func TestLogTailCollapsesTheNewestRunsWorthyRows(t *testing.T) {
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	write := func(s string) {
		f, err := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o644)
		if err != nil {
			t.Fatal(err)
		}
		f.WriteString(s)
		f.Close()
	}
	tail := &logTail{recorder: newRecorderTail(path)}
	if len(tail.rows()) != 0 {
		t.Fatal("rows without a recording")
	}
	refused := `{"verdict":"refused","reason":"critical_wave_budget","target":"window","dur_ms":0,"attrs":{}}`
	write(flightLine(1, "old", "admission", "WARN", 5, refused))
	write(flightLine(2, "new", "admission", "WARN", 100, refused))
	write(flightLine(3, "new", "admission", "WARN", 200, refused))
	write(flightLine(4, "new", "planner_step", "INFO", 210, `{"verdict":"admitted","reason":"","target":"rounds","dur_ms":1,"attrs":{}}`))
	write(flightLine(5, "new", "colony_stage", "INFO", 220, `{"stage":"Reserves"}`))
	rows := tail.rows()
	if len(rows) != 2 {
		t.Fatalf("want the stage event and one collapsed refusal, got %+v", rows)
	}
	var refusal *LogRow
	for i := range rows {
		if rows[i].Count == 2 {
			refusal = &rows[i]
		}
	}
	if refusal == nil || !refusal.Problem || refusal.FirstTick != 100 || refusal.LastTick != 200 ||
		!strings.Contains(refusal.Message, "critical_wave_budget") || !strings.Contains(refusal.Text, "x 2") {
		t.Fatalf("%+v", rows)
	}
	if rows[0].Message[:12] != "colony_stage" {
		t.Fatalf("newest first: %+v", rows)
	}
	// Appended rows show up on the next poll.
	write(flightLine(6, "new", "admission", "WARN", 300, refused))
	if got := tail.rows(); len(got) != 2 || got[0].Count != 3 {
		t.Fatalf("%+v", got)
	}
}
