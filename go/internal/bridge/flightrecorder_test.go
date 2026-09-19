package bridge

import (
	"context"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestRetentionAndTruncationAreExplicit(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := NewFlightRecorder(path, FlightSegmentBytes(1024), FlightSegments(2), FlightPayloadBytes(128))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { r.Close() })
	for i := 0; i < 20; i++ {
		if _, err = r.Event("sample", nil, true, map[string]any{"value": strings.Repeat("x", 500), "index": i}); err != nil {
			t.Fatalf("Event: %v", err)
		}
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatalf("ReadTimeline: %v", err)
	}
	if len(rows) == 0 || rows[0].Kind != "recording_gap" {
		t.Fatalf("expected leading recording_gap from retention, got %+v", rows[0])
	}
	foundTruncated := false
	for _, row := range rows {
		if truncated, ok := row.Payload["truncated"]; ok {
			if b, ok := truncated.(bool); ok && b {
				foundTruncated = true
			}
		}
	}
	if !foundTruncated {
		t.Fatal("expected at least one truncated payload")
	}
	dir := filepath.Dir(path)
	files, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir: %v", err)
	}
	if len(files) != 2 {
		t.Fatalf("expected 2 retained files, got %d", len(files))
	}
	stats := r.FlightRecorderStats()
	if stats.Truncated != 21 {
		t.Fatalf("expected 21 truncated records (20 events + coverage), got %d", stats.Truncated)
	}
}

func TestTruncatedReceiptKeepsRequestCorrelation(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := NewFlightRecorder(path, FlightPayloadBytes(128))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { r.Close() })
	request, err := r.Event("native_request", nil, true, map[string]any{"tool": "games_call_tool", "arguments": map[string]any{}})
	if err != nil {
		t.Fatalf("Event: %v", err)
	}
	timing := map[string]any{"gate_wait_ms": 0.5, "call_ms": 12.0, "decode_ms": 0.25, "total_ms": 13.0, "response_bytes": 2048}
	if _, err = r.Event("native_response", nil, false, map[string]any{"request": request, "native_tool": "rimgovernor/x", "timing": timing, "result": map[string]any{"value": strings.Repeat("x", 2000)}}); err != nil {
		t.Fatalf("Event: %v", err)
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatalf("ReadTimeline: %v", err)
	}
	last := rows[len(rows)-1]
	if truncated, _ := last.Payload["truncated"].(bool); !truncated {
		t.Fatalf("expected truncated payload, got %+v", last.Payload)
	}
	got, ok := last.Payload["request"].(float64)
	if !ok || uint64(got) != request {
		t.Fatalf("expected correlated request %d, got %+v", request, last.Payload["request"])
	}
	// Large receipts are exactly the calls whose phases matter, so the flat
	// timing map and native tool survive truncation alongside the request id.
	kept, _ := last.Payload["timing"].(map[string]any)
	if last.Payload["native_tool"] != "rimgovernor/x" || kept["call_ms"] != 12.0 {
		t.Fatalf("expected timing to survive truncation, got %+v", last.Payload)
	}
}

func TestSegmentRotationDropsOldestAndShiftsIndexes(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := NewFlightRecorder(path, FlightSegmentBytes(minSegmentBytes), FlightSegments(3), FlightPayloadBytes(minPayloadBytes))
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	for i := 0; i < 60; i++ {
		if _, err = r.Event("sample", nil, false, map[string]any{"index": i, "pad": strings.Repeat("y", 40)}); err != nil {
			t.Fatalf("Event: %v", err)
		}
	}
	if err = r.Close(); err != nil {
		t.Fatalf("Close: %v", err)
	}
	dir := filepath.Dir(path)
	files, err := os.ReadDir(dir)
	if err != nil {
		t.Fatalf("ReadDir: %v", err)
	}
	if len(files) != 3 {
		t.Fatalf("expected 3 retained files (active + 2 segments), got %d: %v", len(files), files)
	}
	stats := r.FlightRecorderStats()
	if stats.Rotations == 0 {
		t.Fatal("expected at least one rotation")
	}
}

func TestDurableRecordsFsyncImmediatelyNonDurableDoNot(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	r, err := NewFlightRecorder(path)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { r.Close() })
	if _, err = r.Event("native_request", nil, true, map[string]any{"tool": "x"}); err != nil {
		t.Fatalf("Event: %v", err)
	}
	if _, err = r.Event("native_response", nil, false, map[string]any{"tool": "x"}); err != nil {
		t.Fatalf("Event: %v", err)
	}
	stats := r.FlightRecorderStats()
	// coverage + native_request are durable; native_response is not.
	if stats.DurableRecords != 2 {
		t.Fatalf("expected 2 durable records, got %d", stats.DurableRecords)
	}
	if stats.Records != 3 {
		t.Fatalf("expected 3 total records, got %d", stats.Records)
	}
}

func TestActionContextRoundTrips(t *testing.T) {
	ctx := WithFlightAction(context.Background(), "action-1", "goal-1")
	got := flightActionFrom(ctx)
	if got["action_id"] != "action-1" || got["goal_id"] != "goal-1" {
		t.Fatalf("unexpected action context: %+v", got)
	}
	if flightActionFrom(context.Background()) != nil {
		t.Fatal("expected nil action context on bare context")
	}
	unattached := WithFlightAction(context.Background(), "", "goal")
	if flightActionFrom(unattached) != nil {
		t.Fatal("expected an empty action id to attach no correlation")
	}
}

func TestRejectsBoundsBelowMinimums(t *testing.T) {
	path := filepath.Join(t.TempDir(), "timeline.jsonl")
	if _, err := NewFlightRecorder(path, FlightSegmentBytes(1)); err == nil {
		t.Fatal("expected error for undersized segment bytes")
	}
	if _, err := NewFlightRecorder(path, FlightSegments(1)); err == nil {
		t.Fatal("expected error for undersized segments")
	}
	if _, err := NewFlightRecorder(path, FlightPayloadBytes(1)); err == nil {
		t.Fatal("expected error for undersized payload bytes")
	}
}

// A recorder opened on a path that already holds rows continues their
// sequence (#299: the ring under a profile outlives each launch), so the
// timeline reads without a gap and the run id alone separates launches.
func TestSequenceContinuesAcrossLaunches(t *testing.T) {
	path := filepath.Join(t.TempDir(), "flight.jsonl")
	first, err := NewFlightRecorder(path, FlightRunID("first"))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := first.Event("native_request", nil, true, nil); err != nil {
		t.Fatal(err)
	}
	if err := first.Close(); err != nil {
		t.Fatal(err)
	}
	second, err := NewFlightRecorder(path, FlightRunID("second"))
	if err != nil {
		t.Fatal(err)
	}
	sequence, err := second.Event("native_request", nil, true, nil)
	if err != nil {
		t.Fatal(err)
	}
	if sequence != 4 {
		t.Fatalf("second launch's first request sequence %d, want 4", sequence)
	}
	if err := second.Close(); err != nil {
		t.Fatal(err)
	}
	rows, err := ReadTimeline(path)
	if err != nil {
		t.Fatal(err)
	}
	var runs []string
	for _, row := range rows {
		if row.Kind == "recording_gap" {
			t.Fatalf("gap between launches: %+v", row)
		}
		runs = append(runs, row.Run)
	}
	if len(rows) != 4 || runs[1] != "first" || runs[2] != "second" || rows[3].Sequence != 4 {
		t.Fatalf("rows %+v", rows)
	}
}
