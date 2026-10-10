package bridge

import (
	"testing"
	"time"
)

// noRollup makes every call and frame hit write its own row.
func noRollup(t *testing.T) {
	old := pollRollupEvery
	pollRollupEvery = 0
	t.Cleanup(func() { pollRollupEvery = old })
}

func quietRow(native string, ok bool) map[string]any {
	return map[string]any{"tool": "games_call_tool", "native_tool": native, "ok": ok, "timing": map[string]any{"call_ms": 2.0, "total_ms": 3.0}}
}

func TestFoldCallKeepsFirstFailuresAndNonPolls(t *testing.T) {
	c := &Client{}
	const poll = "rimgovernor/lifecycle_read_tick"
	if c.foldCall(quietRow(poll, true)) {
		t.Fatal("the first call of an interval keeps its row")
	}
	if !c.foldCall(quietRow(poll, true)) {
		t.Fatal("a repeat inside the interval folds")
	}
	if c.foldCall(quietRow(poll, false)) {
		t.Fatal("a failure always keeps its row")
	}
	if c.foldCall(quietRow("rimgovernor/operations_apply", true)) {
		t.Fatal("a write is not a quiet poll")
	}
}

func TestRollupRowCarriesFoldedCallsAndHitsToPhases(t *testing.T) {
	c := &Client{}
	const poll = "rimgovernor/clock_read_events"
	c.foldCall(quietRow(poll, true))
	c.foldCall(quietRow(poll, true))
	c.foldCall(quietRow(poll, true))
	c.foldHit("rimgovernor/snapshot_frame_pawns")
	c.rollup.mu.Lock()
	c.rollup.flushedAt = time.Now().Add(-2 * pollRollupEvery)
	c.rollup.mu.Unlock()
	path := t.TempDir() + "/flight.jsonl"
	rec, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatal(err)
	}
	c.recorder = rec
	c.flushRollup(time.Now())
	rec.Close()
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	rows = nativePollRows(rows)
	if len(rows) != 1 || rows[0].Kind != "native_poll" {
		t.Fatalf("rows %+v", rows)
	}
	summary := SummarizePhases(rows)
	var calls, hits uint64
	for _, tool := range summary.Tools {
		switch tool.NativeTool {
		case poll:
			calls = tool.Calls
			if tool.CallMs != 4 || tool.TotalMs != 6 {
				t.Fatalf("timing sums %+v", tool)
			}
		case "rimgovernor/snapshot_frame_pawns":
			hits = tool.CacheHits
		}
	}
	if calls != 2 || hits != 1 {
		t.Fatalf("calls %d hits %d", calls, hits)
	}
}

// nativePollRows drops the recorder's own coverage row.
func nativePollRows(rows []TimelineRecord) []TimelineRecord {
	var out []TimelineRecord
	for _, row := range rows {
		if row.Kind == "native_poll" {
			out = append(out, row)
		}
	}
	return out
}
